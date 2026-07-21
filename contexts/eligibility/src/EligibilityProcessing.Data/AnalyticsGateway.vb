Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Npgsql
Imports NpgsqlTypes

''' <summary>
''' Read-only analytics queries over the processed corpus (V25 indexes). The
''' six cohort/profile methods and GetTrendAsync are fully implemented here;
''' GetConceptSummaryAsync and SearchConceptsAsync still carry minimal stub
''' bodies - they are implemented in Task 8 but must resolve cleanly today
''' because this class is registered in DI and constructed at startup.
''' </summary>
Public NotInheritable Class AnalyticsGateway
    Implements IAnalyticsGateway

    Private ReadOnly _dataSource As NpgsqlDataSource

    Public Sub New(outputDataSource As NpgsqlDataSource)
        If outputDataSource Is Nothing Then Throw New ArgumentNullException(NameOf(outputDataSource))
        _dataSource = outputDataSource
    End Sub

    ' Returns the SQL fragment selecting the cohort's nct_ids. All four kinds
    ' produce a single-column set of nct_id so the callers can treat them
    ' identically. Concept/Phase/Condition bind @val (text); Year binds
    ' @val_int (integer) - see TryBindCohortParam.
    Private Shared Function CohortSql(cohort As AnalyticsCohort) As String
        Select Case cohort.Kind
            Case AnalyticsCohortKind.Concept
                If cohort.IncludeDescendants Then
                    Return "
SELECT DISTINCT e.nct_id FROM public.eligibility e
WHERE e.concept_code = @val
   OR e.concept_code IN (SELECT descendant_cui FROM umls.concept_ancestor WHERE ancestor_cui = @val)"
                End If
                Return "SELECT DISTINCT e.nct_id FROM public.eligibility e WHERE e.concept_code = @val"

            Case AnalyticsCohortKind.Condition
                If cohort.IncludeDescendants Then
                    Return "
SELECT DISTINCT d.nct_id
FROM public.eligibility_study_detail d
JOIN LATERAL unnest(d.conditions) AS cond(txt) ON true
JOIN public.condition_concept cc
  ON cc.condition_norm = regexp_replace(btrim(lower(cond.txt)), '\s+', ' ', 'g')
WHERE cc.concept_code = @val
   OR cc.concept_code IN (SELECT descendant_cui FROM umls.concept_ancestor WHERE ancestor_cui = @val)"
                End If
                Return "
SELECT DISTINCT d.nct_id
FROM public.eligibility_study_detail d
JOIN LATERAL unnest(d.conditions) AS cond(txt) ON true
JOIN public.condition_concept cc
  ON cc.condition_norm = regexp_replace(btrim(lower(cond.txt)), '\s+', ' ', 'g')
WHERE cc.concept_code = @val"

            Case AnalyticsCohortKind.Phase
                Return "SELECT DISTINCT d.nct_id FROM public.eligibility_study_detail d WHERE d.phase = @val"

            Case Else ' Year
                Return "
SELECT DISTINCT d.nct_id FROM public.eligibility_study_detail d
WHERE d.start_date IS NOT NULL
  AND EXTRACT(year FROM d.start_date)::int = @val_int"
        End Select
    End Function

    ' Binds the cohort's single value onto the command built from CohortSql.
    ' Returns False (without adding a parameter) when the cohort is a Year
    ' whose value does not parse as an integer - callers must treat that as an
    ' empty result rather than letting an unparseable value reach Postgres.
    Private Shared Function TryBindCohortParam(cmd As NpgsqlCommand, cohort As AnalyticsCohort) As Boolean
        If cohort.Kind = AnalyticsCohortKind.Year Then
            Dim year As Integer
            If Not Integer.TryParse(cohort.Value, year) Then Return False
            cmd.Parameters.Add(New NpgsqlParameter("val_int", NpgsqlDbType.Integer) With {.Value = year})
        Else
            cmd.Parameters.Add(New NpgsqlParameter("val", NpgsqlDbType.Text) With {.Value = cohort.Value})
        End If
        Return True
    End Function

    Public Async Function GetCohortSizeAsync(cohort As AnalyticsCohort,
                                             cancellationToken As CancellationToken) _
            As Task(Of Integer) Implements IAnalyticsGateway.GetCohortSizeAsync

        If cohort Is Nothing Then Return 0

        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT count(*) FROM (" & CohortSql(cohort) & ") c"
                If Not TryBindCohortParam(cmd, cohort) Then Return 0
                Dim scalar = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                Return If(scalar Is Nothing OrElse scalar Is DBNull.Value, 0, Convert.ToInt32(scalar))
            End Using
        End Using
    End Function

    Public Async Function GetCohortProfileAsync(cohort As AnalyticsCohort,
                                                cancellationToken As CancellationToken) _
            As Task(Of IReadOnlyList(Of ConceptCount)) Implements IAnalyticsGateway.GetCohortProfileAsync

        Dim result As New List(Of ConceptCount)
        If cohort Is Nothing Then Return result

        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
WITH cohort AS (" & CohortSql(cohort) & ")
SELECT e.concept_code, count(DISTINCT e.nct_id)
FROM public.eligibility e JOIN cohort c ON c.nct_id = e.nct_id
WHERE e.concept_code <> ''
GROUP BY e.concept_code"
                If Not TryBindCohortParam(cmd, cohort) Then Return result
                ' count(DISTINCT ...) is bigint - GetInt32 throws on that column;
                ' read as Int64 and narrow explicitly (Option Strict requires it).
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(New ConceptCount(reader.GetString(0), CInt(reader.GetInt64(1))))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function GetCorpusProfileAsync(cancellationToken As CancellationToken) _
            As Task(Of IReadOnlyList(Of ConceptCount)) Implements IAnalyticsGateway.GetCorpusProfileAsync

        Dim result As New List(Of ConceptCount)
        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT concept_code, count(DISTINCT nct_id)
FROM public.eligibility WHERE concept_code <> '' GROUP BY concept_code"
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(New ConceptCount(reader.GetString(0), CInt(reader.GetInt64(1))))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function GetCorpusTrialCountAsync(cancellationToken As CancellationToken) _
            As Task(Of Integer) Implements IAnalyticsGateway.GetCorpusTrialCountAsync

        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT count(DISTINCT nct_id) FROM public.eligibility WHERE concept_code <> ''"
                Dim scalar = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                Return If(scalar Is Nothing OrElse scalar Is DBNull.Value, 0, Convert.ToInt32(scalar))
            End Using
        End Using
    End Function

    Public Async Function GetCohortDefiningCodesAsync(cohort As AnalyticsCohort,
                                                       cancellationToken As CancellationToken) _
            As Task(Of IReadOnlyList(Of String)) Implements IAnalyticsGateway.GetCohortDefiningCodesAsync

        If cohort Is Nothing Then Return Array.Empty(Of String)()

        ' Phase and Year have no hierarchy - nothing is tautological, and there
        ' is nothing to query.
        If cohort.Kind = AnalyticsCohortKind.Phase OrElse cohort.Kind = AnalyticsCohortKind.Year Then
            Return Array.Empty(Of String)()
        End If

        Dim result As New List(Of String) From {cohort.Value}
        If cohort.IncludeDescendants Then
            Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT descendant_cui FROM umls.concept_ancestor WHERE ancestor_cui = @val"
                    cmd.Parameters.Add(New NpgsqlParameter("val", NpgsqlDbType.Text) With {.Value = cohort.Value})
                    Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                        While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                            result.Add(reader.GetString(0))
                        End While
                    End Using
                End Using
            End Using
        End If
        Return result
    End Function

    Public Async Function GetPrefNamesAsync(conceptCodes As IReadOnlyList(Of String),
                                            cancellationToken As CancellationToken) _
            As Task(Of IReadOnlyDictionary(Of String, String)) Implements IAnalyticsGateway.GetPrefNamesAsync

        Dim result As New Dictionary(Of String, String)
        If conceptCodes Is Nothing OrElse conceptCodes.Count = 0 Then Return result

        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT cui, pref_name FROM umls.concept WHERE cui = ANY(@codes)"
                cmd.Parameters.Add(New NpgsqlParameter("codes", NpgsqlDbType.Array Or NpgsqlDbType.Text) With {
                        .Value = conceptCodes.ToArray()})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result(reader.GetString(0)) = If(reader.IsDBNull(1), "", reader.GetString(1))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    ''' <summary>
    ''' One point per year in which any processed study started, showing the
    ''' concept's share of that year's studies. The LEFT JOIN from yr to hits is
    ''' deliberate - a year in which the concept never appears must still
    ''' produce a point (0 / that year's study count), not be skipped, or a line
    ''' chart would draw a continuous line through the gap. All years are
    ''' included; there is no cutoff, because the corpus's current skew toward
    ''' 2019+ reflects processing progress, not a permanent property of the
    ''' data. currentYear is supplied by the caller (never read from the clock
    ''' here) purely so IsPartial can flag the one part-year deterministically.
    ''' </summary>
    Public Async Function GetTrendAsync(conceptCode As String,
                                        currentYear As Integer,
                                        cancellationToken As CancellationToken) _
            As Task(Of IReadOnlyList(Of TrendPoint)) Implements IAnalyticsGateway.GetTrendAsync

        Dim result As New List(Of TrendPoint)
        Using conn = Await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
WITH yr AS (
  SELECT EXTRACT(year FROM d.start_date)::int AS y, count(*)::numeric AS studies
  FROM public.eligibility_study_detail d
  WHERE d.start_date IS NOT NULL
  GROUP BY 1),
hits AS (
  SELECT EXTRACT(year FROM d.start_date)::int AS y, count(DISTINCT e.nct_id)::numeric AS trials
  FROM public.eligibility e
  JOIN public.eligibility_study_detail d ON d.nct_id = e.nct_id
  WHERE e.concept_code = @cui AND d.start_date IS NOT NULL
  GROUP BY 1)
SELECT yr.y, yr.studies::int, COALESCE(hits.trials, 0)::int,
       100.0 * COALESCE(hits.trials, 0) / yr.studies
FROM yr LEFT JOIN hits USING (y)
ORDER BY yr.y"
                cmd.Parameters.Add(New NpgsqlParameter("cui", NpgsqlDbType.Text) With {.Value = If(conceptCode, "")})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        Dim year = reader.GetInt32(0)
                        Dim studiesThatYear = reader.GetInt32(1)
                        Dim trialsWithConcept = reader.GetInt32(2)
                        Dim pctOfYear = reader.GetDouble(3)
                        result.Add(New TrendPoint(year, studiesThatYear, trialsWithConcept, pctOfYear, year = currentYear))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    ' --- Stubs below: declared on the interface now, implemented in Task 8.
    ' Deliberately not NotImplementedException - this class is resolved from DI
    ' at startup, and a premature call (including from an unrelated test) must
    ' get a well-formed empty answer, not a crash.

    Public Function GetConceptSummaryAsync(conceptCode As String,
                                           cancellationToken As CancellationToken) _
            As Task(Of ConceptSummary) Implements IAnalyticsGateway.GetConceptSummaryAsync

        Return Task.FromResult(Of ConceptSummary)(Nothing)
    End Function

    Public Function SearchConceptsAsync(term As String, limit As Integer,
                                        cancellationToken As CancellationToken) _
            As Task(Of IReadOnlyList(Of ConceptSummary)) Implements IAnalyticsGateway.SearchConceptsAsync

        Return Task.FromResult(Of IReadOnlyList(Of ConceptSummary))(Array.Empty(Of ConceptSummary)())
    End Function

End Class
