Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Npgsql
Imports NpgsqlTypes

''' <summary>
''' Data-access for public.condition_concept (V24), the condition-string to
''' UMLS-CUI dictionary.
'''
''' The SQL normalization expression regexp_replace(btrim(lower(x)), '\s+', ' ', 'g')
''' appears in several statements here and MUST stay identical to
''' ConceptKey.Normalize - ConditionConceptStoreTests asserts they agree, because
''' a divergence would silently stop exact matches aligning.
''' </summary>
Public NotInheritable Class ConditionConceptStore
    Implements IConditionConceptStore

    Private ReadOnly _dataSource As NpgsqlDataSource

    Public Sub New(outputDataSource As NpgsqlDataSource)
        If outputDataSource Is Nothing Then Throw New ArgumentNullException(NameOf(outputDataSource))
        _dataSource = outputDataSource
    End Sub

    Public Async Function LookupExactAsync(conditionNorm As String,
                                           cancellationToken As CancellationToken) _
            As Task(Of IReadOnlyList(Of UmlsCandidate)) Implements IConditionConceptStore.LookupExactAsync

        If String.IsNullOrWhiteSpace(conditionNorm) Then Return Array.Empty(Of UmlsCandidate)()

        Dim result As New List(Of UmlsCandidate)
        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                ' DISTINCT on cui: several atoms of the same concept can share a
                ' normalized string (different sources/term types).
                cmd.CommandText = "
SELECT DISTINCT c.cui, c.pref_name, c.root_source
FROM umls.atom a
JOIN umls.concept c ON c.cui = a.cui
WHERE a.str_norm = @norm
ORDER BY c.cui"
                cmd.Parameters.Add(New NpgsqlParameter("norm", NpgsqlDbType.Text) With {.Value = conditionNorm})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(New UmlsCandidate(
                                reader.GetString(0),
                                If(reader.IsDBNull(1), "", reader.GetString(1)),
                                If(reader.IsDBNull(2), "", reader.GetString(2))))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function UpsertAsync(entry As ConditionConceptEntry,
                                      cancellationToken As CancellationToken) _
            As Task Implements IConditionConceptStore.UpsertAsync

        If entry Is Nothing OrElse String.IsNullOrWhiteSpace(entry.ConditionNorm) Then Return

        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO public.condition_concept
    (condition_norm, raw_form, study_count, concept_code, umls_name, match_tier, match_score, resolved_at)
VALUES
    (@norm, @raw, @count, @code, @name, @tier, @score, now())
ON CONFLICT (condition_norm) DO UPDATE SET
    raw_form     = excluded.raw_form,
    concept_code = excluded.concept_code,
    umls_name    = excluded.umls_name,
    match_tier   = excluded.match_tier,
    match_score  = excluded.match_score,
    resolved_at  = now()"
                cmd.Parameters.Add(New NpgsqlParameter("norm", NpgsqlDbType.Text) With {.Value = entry.ConditionNorm})
                cmd.Parameters.Add(New NpgsqlParameter("raw", NpgsqlDbType.Text) With {.Value = entry.RawForm})
                cmd.Parameters.Add(New NpgsqlParameter("count", NpgsqlDbType.Integer) With {.Value = entry.StudyCount})
                cmd.Parameters.Add(New NpgsqlParameter("code", NpgsqlDbType.Text) With {
                        .Value = If(String.IsNullOrEmpty(entry.ConceptCode), CObj(DBNull.Value), entry.ConceptCode)})
                cmd.Parameters.Add(New NpgsqlParameter("name", NpgsqlDbType.Text) With {
                        .Value = If(String.IsNullOrEmpty(entry.UmlsName), CObj(DBNull.Value), entry.UmlsName)})
                cmd.Parameters.Add(New NpgsqlParameter("tier", NpgsqlDbType.Text) With {.Value = entry.MatchTier})
                cmd.Parameters.Add(New NpgsqlParameter("score", NpgsqlDbType.Numeric) With {.Value = CDec(entry.MatchScore)})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    Public Async Function GetUnseenConditionsForStudyAsync(nctId As String,
                                                           cancellationToken As CancellationToken) _
            As Task(Of IReadOnlyList(Of String)) Implements IConditionConceptStore.GetUnseenConditionsForStudyAsync

        If String.IsNullOrWhiteSpace(nctId) Then Return Array.Empty(Of String)()

        Dim result As New List(Of String)
        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT DISTINCT s.cond
FROM (SELECT unnest(conditions) AS cond
      FROM public.eligibility_study_detail
      WHERE nct_id = @nct) s
WHERE btrim(s.cond) <> ''
  AND NOT EXISTS (
      SELECT 1 FROM public.condition_concept d
      WHERE d.condition_norm = regexp_replace(btrim(lower(s.cond)), '\s+', ' ', 'g'))
ORDER BY s.cond"
                cmd.Parameters.Add(New NpgsqlParameter("nct", NpgsqlDbType.Text) With {.Value = nctId})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(reader.GetString(0))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function SeedFromCorpusAsync(cancellationToken As CancellationToken) _
            As Task(Of Integer) Implements IConditionConceptStore.SeedFromCorpusAsync

        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                ' raw_form = most frequent original casing, ties broken
                ' lexicographically so a re-run reproduces the same choice. That
                ' matters: the scorer's acronym term only fires on uppercase, and
                ' the corpus holds COPD (657 studies) alongside Copd (93).
                '
                ' The tiebreak column is explicitly COLLATE "C" (byte order): the
                ' default en_US.utf8 database collation sorts "Hiv" ahead of "HIV"
                ' (case is a low-priority tiebreak under that locale, applied after
                ' a case-insensitive primary comparison), which would pick the
                ' lowercase form and silently disable the scorer's acronym match.
                ' "C" collation is plain byte order, where uppercase ASCII sorts
                ' before lowercase, giving a deterministic and locale-independent
                ' choice regardless of the server's configured locale.
                cmd.CommandText = "
WITH mentions AS (
    SELECT nct_id, unnest(conditions) AS cond
    FROM public.eligibility_study_detail
),
per_form AS (
    SELECT regexp_replace(btrim(lower(cond)), '\s+', ' ', 'g') AS norm,
           cond AS raw,
           count(DISTINCT nct_id) AS cnt
    FROM mentions
    WHERE btrim(cond) <> ''
    GROUP BY 1, 2
),
rolled AS (
    SELECT norm,
           (array_agg(raw ORDER BY cnt DESC, raw COLLATE ""C"" ASC))[1] AS raw_form,
           sum(cnt)::int AS study_count
    FROM per_form
    GROUP BY norm
),
ins AS (
    INSERT INTO public.condition_concept (condition_norm, raw_form, study_count, match_tier)
    SELECT norm, raw_form, study_count, 'unresolved' FROM rolled
    ON CONFLICT (condition_norm) DO UPDATE SET
        raw_form    = excluded.raw_form,
        study_count = excluded.study_count
    RETURNING (xmax = 0) AS inserted
)
SELECT count(*) FILTER (WHERE inserted) FROM ins"
                cmd.CommandTimeout = 0   ' full-corpus aggregation; can exceed the 30s default
                Dim scalar = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                Return If(scalar Is Nothing OrElse scalar Is DBNull.Value, 0, Convert.ToInt32(scalar))
            End Using
        End Using
    End Function

    Public Async Function GetPendingAsync(limit As Integer, force As Boolean,
                                          cancellationToken As CancellationToken) _
            As Task(Of IReadOnlyList(Of ConditionConceptEntry)) Implements IConditionConceptStore.GetPendingAsync

        Dim result As New List(Of ConditionConceptEntry)
        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT condition_norm, raw_form, study_count
FROM public.condition_concept
WHERE @force OR resolved_at IS NULL
ORDER BY study_count DESC, condition_norm
LIMIT @limit"
                cmd.Parameters.Add(New NpgsqlParameter("force", NpgsqlDbType.Boolean) With {.Value = force})
                cmd.Parameters.Add(New NpgsqlParameter("limit", NpgsqlDbType.Integer) With {.Value = Math.Max(1, limit)})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(New ConditionConceptEntry With {
                                .ConditionNorm = reader.GetString(0),
                                .RawForm = reader.GetString(1),
                                .StudyCount = reader.GetInt32(2)})
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function CountPendingAsync(force As Boolean,
                                            cancellationToken As CancellationToken) _
            As Task(Of Integer) Implements IConditionConceptStore.CountPendingAsync

        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT count(*)::int FROM public.condition_concept
WHERE @force OR resolved_at IS NULL"
                cmd.Parameters.Add(New NpgsqlParameter("force", NpgsqlDbType.Boolean) With {.Value = force})
                Dim scalar = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                Return If(scalar Is Nothing OrElse scalar Is DBNull.Value, 0, Convert.ToInt32(scalar))
            End Using
        End Using
    End Function

End Class
