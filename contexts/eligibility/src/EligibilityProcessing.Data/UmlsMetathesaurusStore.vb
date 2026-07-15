Imports System.Collections.Generic
Imports System.Linq
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Npgsql
Imports NpgsqlTypes

' Data-access for the local UMLS Metathesaurus store (the `umls` schema, V17).
'
' Owns ALL umls.* SQL: the bulk-load writes used by the CLI `load-umls` command
' and the runtime reads used by PostgresUmlsClient. Uses the OUTPUT data source
' (the umls schema lives in the output DB alongside eligibility). Stateless and
' thread-safe — registered as a singleton.
'
' Normalization (NormalizeConcept) is the single source of truth for str_norm:
' the loader stamps it onto every atom and the lookup applies it to the query,
' so an exact match aligns. Keep both paths going through this one function.

Public NotInheritable Class UmlsMetathesaurusStore

    Private Shared ReadOnly WhitespaceRegex As New Regex("\s+", RegexOptions.Compiled)
    Private Shared ReadOnly NonWordRegex As New Regex("\W+", RegexOptions.Compiled)

    Private ReadOnly _dataSource As NpgsqlDataSource

    Public Sub New(outputDataSource As NpgsqlDataSource)
        If outputDataSource Is Nothing Then Throw New ArgumentNullException(NameOf(outputDataSource))
        _dataSource = outputDataSource
    End Sub

    ''' <summary>
    ''' Normalized form used for exact match (and stored as umls.atom.str_norm):
    ''' lower-invariant, internal whitespace collapsed to single spaces, trimmed.
    ''' Deterministic so the loader and the query agree.
    ''' </summary>
    Public Shared Function NormalizeConcept(value As String) As String
        If String.IsNullOrWhiteSpace(value) Then Return ""
        Return WhitespaceRegex.Replace(value.Trim().ToLowerInvariant(), " ")
    End Function

    ' ============ runtime reads (PostgresUmlsClient) ============

    ''' <summary>
    ''' Candidate CUIs for a concept string, ranked best-first, capped at
    ''' <paramref name="limit"/>. Two query shapes selected by
    ''' <paramref name="includeTrigram"/>:
    '''   - <b>includeTrigram = False (fast path):</b> exact match on the normalized
    '''     string (rank 2.0) UNION full-text search — word-level (OR) match ranked
    '''     by ts_rank (rank 1.0 + ts_rank ∈ [1, 2)). The FTS arm is rank-capped
    '''     (ORDER BY rank LIMIT) so the aggregate never materializes the whole
    '''     OR-match set (a common token like "type" matches 100k+ atoms). No
    '''     pg_trgm `%`, so no set_limit round-trip and no full fuzzy scan.
    '''   - <b>includeTrigram = True (fuzzy path):</b> the same two arms plus the
    '''     pg_trgm fuzzy arm (`str % @raw`, rank = similarity ∈ [0, 1)) — a typo /
    '''     no-whole-word-overlap fallback. The EXPLAIN-measured cost of the fuzzy
    '''     scan (~250 ms on a 3.2M-atom table) is paid only on this path.
    ''' The CALLER (PostgresUmlsClient) decides which shape to run: it tries the
    ''' fast path first and only re-runs with the trigram arm when the fast path's
    ''' candidates fail to resolve (no candidate clears the scorer's 0.45 threshold
    ''' after the coverage / discriminative-token guards) — a score-aware gate, so
    ''' the fuzzy scan fires only when it can change a resolved/unresolved outcome.
    ''' Each candidate's preferred name + root source come from umls.concept;
    ''' UmlsMatchScorer ranks them lexically downstream.
    ''' </summary>
    Public Async Function SearchCandidatesAsync(
            concept As String,
            limit As Integer,
            trigramThreshold As Double,
            maxAtomLength As Integer,
            includeTrigram As Boolean,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of UmlsCandidate))

        If String.IsNullOrWhiteSpace(concept) Then Return Array.Empty(Of UmlsCandidate)()
        Dim norm = NormalizeConcept(concept)
        Dim cappedLimit = Math.Min(Math.Max(limit, 1), 100)
        Dim threshold = CSng(Math.Min(Math.Max(trigramThreshold, 0.0), 1.0))
        Dim maxLen = If(maxAtomLength > 0, maxAtomLength, Integer.MaxValue)
        ' FTS arm cap: rank-order then LIMIT so the aggregate never sees the whole
        ' OR-match set (a common lexeme like "type" matches 100k+ atoms — the cost
        ' driver in the EXPLAIN). Kept at least the candidate limit so the scorer is
        ' never starved.
        Dim ftsCap = Math.Max(cappedLimit, 40)
        ' OR-joined lexemes for to_tsquery (alphanumeric only -> injection-safe).
        ' A non-matching sentinel keeps to_tsquery valid when the query has none.
        Dim tsq = BuildOrTsQuery(concept)
        If tsq.Length = 0 Then tsq = "zzqzznomatchzzqzz"

        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            If Not includeTrigram Then
                ' Fast path: exact + rank-capped FTS. No pg_trgm `%`, so no set_limit
                ' round-trip and no full fuzzy scan — the EXPLAIN-measured ~5x win.
                Return Await RunCandidateQueryAsync(
                        conn, PrimarySql, concept, norm, tsq, maxLen, ftsCap, cappedLimit, cancellationToken).ConfigureAwait(False)
            End If

            ' Fuzzy path: add the pg_trgm arm. Set its `%` similarity floor first.
            Using setCmd = conn.CreateCommand()
                setCmd.CommandText = "SELECT set_limit(@t)"
                setCmd.Parameters.Add(New NpgsqlParameter("t", NpgsqlDbType.Real) With {.Value = threshold})
                Await setCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
            End Using
            Return Await RunCandidateQueryAsync(
                    conn, ThreeArmSql, concept, norm, tsq, maxLen, ftsCap, cappedLimit, cancellationToken).ConfigureAwait(False)
        End Using
    End Function

    ' The fuzzy arms (FTS + trigram) skip atoms longer than @maxlen and LOINC
    ' multi-part panel atoms (containing ' | ') — both are long survey/lab/chemical
    ' strings that share a token with the query and pollute matching. ts_rank uses
    ' normalization=1 (1 + log(length)) so a long atom that slips under the cap is
    ' still down-ranked vs a short precise match. The exact arm is exempt.
    ' Parameters (not CTE values) so the indexes are used. The FTS arm is rank-capped
    ' (ORDER BY rank LIMIT @ftscap) so a broad OR-match never floods the aggregate.

    ' Fast path: exact + rank-capped FTS only.
    Private Const PrimarySql As String = "
WITH hits AS (
    SELECT a.cui, 2.0::real AS rank
    FROM umls.atom a
    WHERE a.str_norm = @norm
    UNION ALL
    SELECT cui, rank FROM (
        SELECT a.cui, (1.0 + LEAST(ts_rank(a.str_tsv, to_tsquery('english', @tsq), 1), 0.999))::real AS rank
        FROM umls.atom a
        WHERE a.str_tsv @@ to_tsquery('english', @tsq)
          AND char_length(a.str) <= @maxlen
          AND a.str NOT LIKE '% | %'
        ORDER BY rank DESC
        LIMIT @ftscap
    ) fts
),
top AS (
    SELECT cui, max(rank) AS best
    FROM hits
    GROUP BY cui
    ORDER BY best DESC
    LIMIT @limit
)
SELECT c.cui, c.pref_name, c.root_source
FROM top
JOIN umls.concept c ON c.cui = top.cui
ORDER BY top.best DESC"

    ' Fuzzy fallback: exact + rank-capped FTS + pg_trgm trigram arm.
    Private Const ThreeArmSql As String = "
WITH hits AS (
    SELECT a.cui, 2.0::real AS rank
    FROM umls.atom a
    WHERE a.str_norm = @norm
    UNION ALL
    SELECT cui, rank FROM (
        SELECT a.cui, (1.0 + LEAST(ts_rank(a.str_tsv, to_tsquery('english', @tsq), 1), 0.999))::real AS rank
        FROM umls.atom a
        WHERE a.str_tsv @@ to_tsquery('english', @tsq)
          AND char_length(a.str) <= @maxlen
          AND a.str NOT LIKE '% | %'
        ORDER BY rank DESC
        LIMIT @ftscap
    ) fts
    UNION ALL
    SELECT a.cui, similarity(a.str, @raw)::real AS rank
    FROM umls.atom a
    WHERE a.str % @raw
      AND char_length(a.str) <= @maxlen
      AND a.str NOT LIKE '% | %'
),
top AS (
    SELECT cui, max(rank) AS best
    FROM hits
    GROUP BY cui
    ORDER BY best DESC
    LIMIT @limit
)
SELECT c.cui, c.pref_name, c.root_source
FROM top
JOIN umls.concept c ON c.cui = top.cui
ORDER BY top.best DESC"

    ' Binds the shared parameter set and reads UmlsCandidate rows. Both query shapes
    ' use the same parameters (@raw is unused by PrimarySql — Npgsql ignores the
    ' extra binding), so one helper serves both.
    Private Shared Async Function RunCandidateQueryAsync(
            conn As NpgsqlConnection,
            sql As String,
            concept As String,
            norm As String,
            tsq As String,
            maxLen As Integer,
            ftsCap As Integer,
            limit As Integer,
            cancellationToken As CancellationToken) As Task(Of List(Of UmlsCandidate))

        Dim result As New List(Of UmlsCandidate)
        Using cmd = conn.CreateCommand()
            cmd.CommandText = sql
            ' @raw drives the pg_trgm arm and exists only in ThreeArmSql; bind it only
            ' when referenced so PrimarySql never carries an unused parameter.
            If sql.Contains("@raw") Then
                cmd.Parameters.Add(New NpgsqlParameter("raw", NpgsqlDbType.Text) With {.Value = concept.Trim()})
            End If
            cmd.Parameters.Add(New NpgsqlParameter("norm", NpgsqlDbType.Text) With {.Value = norm})
            cmd.Parameters.Add(New NpgsqlParameter("tsq", NpgsqlDbType.Text) With {.Value = tsq})
            cmd.Parameters.Add(New NpgsqlParameter("maxlen", NpgsqlDbType.Integer) With {.Value = maxLen})
            cmd.Parameters.Add(New NpgsqlParameter("ftscap", NpgsqlDbType.Integer) With {.Value = ftsCap})
            cmd.Parameters.Add(New NpgsqlParameter("limit", NpgsqlDbType.Integer) With {.Value = limit})
            Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                    result.Add(New UmlsCandidate(
                            ui:=reader.GetString(0),
                            name:=reader.GetString(1),
                            rootSource:=reader.GetString(2)))
                End While
            End Using
        End Using
        Return result
    End Function

    ''' <summary>
    ''' Builds an OR-joined to_tsquery argument from a concept string: split on
    ''' non-word chars, lower-cased, alphanumeric lexemes only (so the result is
    ''' always a safe to_tsquery input — no operator characters survive). Returns
    ''' "" when the query has no usable lexemes.
    ''' </summary>
    Friend Shared Function BuildOrTsQuery(concept As String) As String
        If String.IsNullOrWhiteSpace(concept) Then Return ""
        Dim lexemes = NonWordRegex.Split(concept.ToLowerInvariant()) _
            .Where(Function(t) t.Length >= 1 AndAlso t.Any(AddressOf Char.IsLetterOrDigit)) _
            .Distinct()
        Return String.Join(" | ", lexemes)
    End Function

    ''' <summary>Semantic-type names for a CUI (umls.semantic_type). Empty when none.</summary>
    Public Async Function GetSemanticTypesAsync(
            cui As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String))

        If String.IsNullOrWhiteSpace(cui) Then Return Array.Empty(Of String)()

        Dim result As New List(Of String)
        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT sty FROM umls.semantic_type WHERE cui = @cui ORDER BY sty"
                cmd.Parameters.Add(New NpgsqlParameter("cui", NpgsqlDbType.Text) With {.Value = cui.Trim()})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(reader.GetString(0))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    ' ============ loader writes (CLI load-umls) ============

    ''' <summary>Empties the three umls.* tables for a full per-release rebuild.</summary>
    Public Async Function TruncateAsync(cancellationToken As CancellationToken) As Task
        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "TRUNCATE umls.atom, umls.concept, umls.semantic_type"
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    ''' <summary>Bulk-COPY atoms into umls.atom. Returns the row count written.</summary>
    Public Async Function BulkLoadAtomsAsync(
            rows As IEnumerable(Of AtomRow),
            cancellationToken As CancellationToken) As Task(Of Long)

        Dim count As Long = 0
        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using writer = Await conn.BeginBinaryImportAsync(
                    "COPY umls.atom (cui, str, str_norm, sab, tty, is_pref) FROM STDIN (FORMAT BINARY)",
                    cancellationToken).ConfigureAwait(False)
                For Each r In rows
                    cancellationToken.ThrowIfCancellationRequested()
                    Await writer.StartRowAsync(cancellationToken).ConfigureAwait(False)
                    Await writer.WriteAsync(r.Cui, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(False)
                    Await writer.WriteAsync(r.Str, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(False)
                    Await writer.WriteAsync(r.StrNorm, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(False)
                    Await writer.WriteAsync(r.Sab, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(False)
                    If String.IsNullOrEmpty(r.Tty) Then
                        Await writer.WriteNullAsync(cancellationToken).ConfigureAwait(False)
                    Else
                        Await writer.WriteAsync(r.Tty, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(False)
                    End If
                    Await writer.WriteAsync(r.IsPref, NpgsqlDbType.Boolean, cancellationToken).ConfigureAwait(False)
                    count += 1
                Next
                Await writer.CompleteAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
        Return count
    End Function

    ''' <summary>
    ''' Loads semantic types into umls.semantic_type, keeping only CUIs that the
    ''' curated atom load actually imported (joined to umls.concept). MRSTY covers
    ''' all of UMLS, so it is streamed into a temp staging table on one connection,
    ''' then filtered + deduped into the real table. Call AFTER atoms are loaded
    ''' and RebuildConceptTableAsync has run. Returns the final row count.
    ''' </summary>
    Public Async Function LoadSemanticTypesAsync(
            rows As IEnumerable(Of SemanticTypeRow),
            cancellationToken As CancellationToken) As Task(Of Long)

        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "DROP TABLE IF EXISTS pg_temp.mrsty_stage;
CREATE TEMP TABLE mrsty_stage (cui text, tui text, sty text)"
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using

            Using writer = Await conn.BeginBinaryImportAsync(
                    "COPY pg_temp.mrsty_stage (cui, tui, sty) FROM STDIN (FORMAT BINARY)",
                    cancellationToken).ConfigureAwait(False)
                For Each r In rows
                    cancellationToken.ThrowIfCancellationRequested()
                    Await writer.StartRowAsync(cancellationToken).ConfigureAwait(False)
                    Await writer.WriteAsync(r.Cui, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(False)
                    If String.IsNullOrEmpty(r.Tui) Then
                        Await writer.WriteNullAsync(cancellationToken).ConfigureAwait(False)
                    Else
                        Await writer.WriteAsync(r.Tui, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(False)
                    End If
                    Await writer.WriteAsync(r.Sty, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(False)
                Next
                Await writer.CompleteAsync(cancellationToken).ConfigureAwait(False)
            End Using

            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO umls.semantic_type (cui, tui, sty)
SELECT DISTINCT ON (cui, sty) cui, tui, sty
FROM pg_temp.mrsty_stage
WHERE cui IN (SELECT cui FROM umls.concept)
ORDER BY cui, sty
ON CONFLICT (cui, sty) DO NOTHING"
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
        Return Await CountAsync("umls.semantic_type", cancellationToken).ConfigureAwait(False)
    End Function

    ''' <summary>
    ''' A sample of distinct concept strings from public.eligibility for the
    ''' umls-compare validation command: up to <paramref name="unresolvedLimit"/>
    ''' the existing backend left unresolved (concept_code NULL or '') — where the
    ''' local backend's recall wins show up — plus up to
    ''' <paramref name="resolvedLimit"/> it did resolve (precision/regression check).
    ''' </summary>
    Public Async Function GetSampleConceptsAsync(
            unresolvedLimit As Integer,
            resolvedLimit As Integer,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String))

        Const Sql As String = "
(SELECT DISTINCT concept FROM public.eligibility
   WHERE (concept_code IS NULL OR concept_code = '') AND concept <> '' ORDER BY concept LIMIT @u)
UNION
(SELECT DISTINCT concept FROM public.eligibility
   WHERE concept_code IS NOT NULL AND concept_code <> '' AND concept <> '' ORDER BY concept LIMIT @r)"

        Dim result As New List(Of String)
        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("u", NpgsqlDbType.Integer) With {.Value = Math.Max(unresolvedLimit, 0)})
                cmd.Parameters.Add(New NpgsqlParameter("r", NpgsqlDbType.Integer) With {.Value = Math.Max(resolvedLimit, 0)})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(reader.GetString(0))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    ''' <summary>
    ''' Rebuilds umls.concept (preferred name + source per CUI) from umls.atom.
    ''' Priority: preferred atoms first, then a clinical-vocabulary ordering, then
    ''' the shortest string (usually the cleanest canonical form). Returns the
    ''' concept count.
    ''' </summary>
    Public Async Function RebuildConceptTableAsync(cancellationToken As CancellationToken) As Task(Of Long)
        Const Sql As String = "
TRUNCATE umls.concept;
INSERT INTO umls.concept (cui, pref_name, root_source)
SELECT DISTINCT ON (cui) cui, str, sab
FROM umls.atom
ORDER BY cui,
         is_pref DESC,
         CASE sab
             WHEN 'SNOMEDCT_US' THEN 1 WHEN 'MSH' THEN 2 WHEN 'RXNORM' THEN 3
             WHEN 'LNC' THEN 4 WHEN 'ICD10CM' THEN 5 WHEN 'MDR' THEN 6 ELSE 9
         END,
         length(str)"
        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
        Return Await CountAsync("umls.concept", cancellationToken).ConfigureAwait(False)
    End Function

    ''' <summary>Row count for one of the umls.* tables (name whitelisted).</summary>
    Public Async Function CountAsync(qualifiedTable As String, cancellationToken As CancellationToken) As Task(Of Long)
        ' Whitelist — never interpolate caller input into SQL.
        Dim safe As String
        Select Case qualifiedTable
            Case "umls.atom" : safe = "umls.atom"
            Case "umls.concept" : safe = "umls.concept"
            Case "umls.semantic_type" : safe = "umls.semantic_type"
            Case Else : Throw New ArgumentException($"Unknown umls table: {qualifiedTable}", NameOf(qualifiedTable))
        End Select
        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = $"SELECT count(*) FROM {safe}"
                Dim scalar = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                Return Convert.ToInt64(scalar)
            End Using
        End Using
    End Function

End Class

''' <summary>One atom row for bulk loading umls.atom. StrNorm must be produced by
''' <see cref="UmlsMetathesaurusStore.NormalizeConcept"/> so it aligns with queries.</summary>
Public Structure AtomRow
    Public Property Cui As String
    Public Property Str As String
    Public Property StrNorm As String
    Public Property Sab As String
    Public Property Tty As String
    Public Property IsPref As Boolean
End Structure

''' <summary>One semantic-type row for bulk loading umls.semantic_type.</summary>
Public Structure SemanticTypeRow
    Public Property Cui As String
    Public Property Tui As String
    Public Property Sty As String
End Structure
