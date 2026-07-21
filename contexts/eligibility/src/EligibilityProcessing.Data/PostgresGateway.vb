Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Core
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions
Imports Npgsql
Imports NpgsqlTypes

' Npgsql-backed implementation of <see cref="IPostgresGateway"/>.
'
' Architecture section 2.2. Two NpgsqlDataSource instances are injected:
' the source DS for the read-only AACT lookup and the output DS for everything
' else. Source DS is optional so output-only callers (e.g. tests that only
' exercise persistence) can omit it.
'
' All SQL is parameterised; no string concatenation of user-supplied values.

Public NotInheritable Class PostgresGateway
    Implements IPostgresGateway

    ' Schema migrations applied in declaration order by EnsureSchemaAsync.
    ' Each .sql is idempotent (CREATE IF NOT EXISTS, ALTER ... IF NOT EXISTS),
    ' so re-running the whole list on every startup is safe.
    Private Shared ReadOnly MigrationResourceNames As String() = {
            "EligibilityProcessing.Data.Migrations.V1__schema.sql",
            "EligibilityProcessing.Data.Migrations.V2__study_table.sql",
            "EligibilityProcessing.Data.Migrations.V3__study_raw_response.sql",
            "EligibilityProcessing.Data.Migrations.V4__drop_watermark.sql",
            "EligibilityProcessing.Data.Migrations.V5__study_detail.sql",
            "EligibilityProcessing.Data.Migrations.V6__authoring.sql",
            "EligibilityProcessing.Data.Migrations.V7__study_embeddings.sql",
            "EligibilityProcessing.Data.Migrations.V8__performance_indexes.sql",
            "EligibilityProcessing.Data.Migrations.V9__llm_stop_diagnostics.sql",
            "EligibilityProcessing.Data.Migrations.V10__authoring_criterion_source.sql",
            "EligibilityProcessing.Data.Migrations.V11__auth.sql",
            "EligibilityProcessing.Data.Migrations.V12__audit.sql",
            "EligibilityProcessing.Data.Migrations.V13__authoring_study_id.sql",
            "EligibilityProcessing.Data.Migrations.V14__authoring_criterion_manual_reason.sql",
            "EligibilityProcessing.Data.Migrations.V15__run_concurrency_cap.sql",
            "EligibilityProcessing.Data.Migrations.V16__study_phase_timings.sql",
            "EligibilityProcessing.Data.Migrations.V17__umls_metathesaurus.sql",
            "EligibilityProcessing.Data.Migrations.V18__umls_fts.sql",
            "EligibilityProcessing.Data.Migrations.V19__umls_retry.sql",
            "EligibilityProcessing.Data.Migrations.V20__umls_concept_normalization.sql",
            "EligibilityProcessing.Data.Migrations.V21__signing_credentials.sql",
            "EligibilityProcessing.Data.Migrations.V22__semantic_type_tuis.sql",
            "EligibilityProcessing.Data.Migrations.V23__concept_hierarchy.sql"
        }

    ''' <summary>
    ''' The embedded schema migrations applied by EnsureSchemaAsync, in order, as
    ''' short names (e.g. "V10__authoring_criterion_source"). The last entry is the
    ''' current target schema level.
    ''' </summary>
    Public Shared ReadOnly Property MigrationNames As IReadOnlyList(Of String)
        Get
            Return MigrationResourceNames _
                .Select(Function(n) n.Replace("EligibilityProcessing.Data.Migrations.", "").Replace(".sql", "")) _
                .ToArray()
        End Get
    End Property

    ' Name of the partial index on ctgov.eligibilities that
    ' EnsureSourcePerformanceIndexesAsync creates when source + output are
    ' co-located. Its WHERE predicate MUST stay aligned with
    ' SelectNextTrialsAsync's filter so the planner can satisfy that filter from
    ' the index (index-only walk) without a per-row heap recheck of criteria.
    Friend Const SelectableSourceIndexName As String = "ix_eligibilities_selectable_nct_id"

    Private ReadOnly _outputDataSource As NpgsqlDataSource
    Private ReadOnly _sourceDataSource As NpgsqlDataSource
    Private ReadOnly _logger As ILogger(Of PostgresGateway)
    Private ReadOnly _maxStudyCount As Integer

    Public Sub New(
            outputDataSource As NpgsqlDataSource,
            Optional sourceDataSource As NpgsqlDataSource = Nothing,
            Optional logger As ILogger(Of PostgresGateway) = Nothing,
            Optional maxStudyCount As Integer = 0)
        If outputDataSource Is Nothing Then Throw New ArgumentNullException(NameOf(outputDataSource))
        _outputDataSource = outputDataSource
        _sourceDataSource = sourceDataSource
        _logger = If(logger, CType(NullLogger(Of PostgresGateway).Instance, ILogger(Of PostgresGateway)))
        ' <= 0 disables the clamp (the default for the test constructor); the
        ' composition root passes Postgres:MaxStudyCount in production.
        _maxStudyCount = maxStudyCount
    End Sub

    ' ============ schema migration ============

    ''' <summary>
    ''' TRUNCATEs every output table — eligibility, eligibility_run,
    ''' eligibility_failed, eligibility_study, eligibility_study_detail — and
    ''' resets identity sequences. Source DB (ctgov.*) is NOT touched.
    ''' Destructive; called by the CLI's <c>reset --confirm</c> command. After
    ''' reset, the next batch starts from the default watermark (NCT00000000).
    ''' </summary>
    Public Async Function ResetOutputAsync(cancellationToken As CancellationToken) As Task
        Const Sql As String = "TRUNCATE
            public.eligibility,
            public.eligibility_run,
            public.eligibility_failed,
            public.eligibility_study,
            public.eligibility_study_detail,
            public.eligibility_study_embedding
          RESTART IDENTITY"
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    ''' <summary>
    ''' Applies every embedded migration to the output database in declaration
    ''' order. Each migration is idempotent (CREATE IF NOT EXISTS), so this
    ''' is safe to call on every startup. Called by the CLI's migrate command
    ''' and by integration tests' setup.
    ''' </summary>
    Public Async Function EnsureSchemaAsync(cancellationToken As CancellationToken) As Task
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            For Each resourceName In MigrationResourceNames
                Dim sql = LoadEmbeddedMigration(resourceName)
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = sql
                    ' No command timeout for migrations: a column add or index build
                    ' on a large table (e.g. the 3M-row umls.atom tsvector in V18)
                    ' can run for minutes, well past the default 30s — which would
                    ' otherwise surface as "Exception while reading from stream".
                    cmd.CommandTimeout = 0
                    Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                End Using
            Next
        End Using
    End Function

    ''' <summary>
    ''' Best-effort startup step: when the source (AACT) and output databases are
    ''' the SAME physical database (host + port + database all match), create a
    ''' partial index on ctgov.eligibilities that mirrors SelectNextTrialsAsync's
    ''' filter, so the trial-selection anti-join walks the index alone (index-only)
    ''' instead of heap-fetching the wide criteria text for every already-processed
    ''' row it skips. CREATE INDEX IF NOT EXISTS, so it is idempotent and re-creates
    ''' the index after an AACT reload drops it.
    '''
    ''' No-op (with a logged reason) when the source data source is not configured
    ''' or is NOT co-located with the output DB — the index lives on the AACT schema,
    ''' which we only take ownership of when it is our own database. Failures are
    ''' swallowed with a warning: a missing performance index degrades selection
    ''' speed but must never block startup.
    ''' </summary>
    Public Async Function EnsureSourcePerformanceIndexesAsync(cancellationToken As CancellationToken) As Task
        If _sourceDataSource Is Nothing Then
            _logger.LogDebug("Source data source not configured; skipping source performance index.")
            Return
        End If
        If Not SourceAndOutputAreCoLocated() Then
            _logger.LogInformation(
                    "Source and output databases are not co-located; skipping the ctgov.eligibilities selection index ({Index}).",
                    SelectableSourceIndexName)
            Return
        End If

        ' Co-located with our output DB does not guarantee AACT is present: the
        ' seeded quickstart runs against a standalone database that holds only the
        ' public.* output tables and no ctgov schema at all. Probe for the source
        ' table first (to_regclass returns NULL rather than raising when the schema
        ' or table is absent) so that expected case logs one clean line instead of
        ' a "schema ctgov does not exist" exception + stack trace.
        If Not Await SourceHasCtgovEligibilitiesAsync(cancellationToken).ConfigureAwait(False) Then
            _logger.LogInformation(
                    "Source database has no ctgov.eligibilities table; skipping the selection index ({Index}). This is expected when running without an AACT source (e.g. the seeded quickstart).",
                    SelectableSourceIndexName)
            Return
        End If

        ' Partial index predicate kept aligned with SelectNextTrialsAsync's WHERE so
        ' the planner uses it to satisfy the filter without a heap recheck.
        Dim sql = $"CREATE INDEX IF NOT EXISTS {SelectableSourceIndexName}
ON ctgov.eligibilities (nct_id)
WHERE criteria IS NOT NULL
  AND LENGTH(TRIM(criteria)) >= 50
  AND criteria NOT ILIKE '%please contact%'
  AND criteria NOT ILIKE '%contact site for%'
  AND criteria NOT ILIKE '%contact study%'"

        Try
            Using conn = Await _sourceDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = sql
                    ' Building the index scans ~600k rows applying the ILIKE filters;
                    ' that comfortably exceeds the default 30s, so no timeout here
                    ' (mirrors EnsureSchemaAsync's reasoning).
                    cmd.CommandTimeout = 0
                    Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                End Using
            End Using
            _logger.LogInformation(
                    "Ensured the ctgov.eligibilities selection index ({Index}) for fast trial selection.",
                    SelectableSourceIndexName)
        Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
            _logger.LogWarning(ex,
                    "Could not ensure the ctgov.eligibilities selection index ({Index}); trial selection will still work but may be slower.",
                    SelectableSourceIndexName)
        End Try
    End Function

    ''' <summary>
    ''' True when the source database exposes the AACT <c>ctgov.eligibilities</c>
    ''' table. Uses <c>to_regclass</c>, which returns NULL (rather than raising
    ''' 3F000 "schema does not exist" / 42P01 "table does not exist") when the
    ''' schema or table is absent - so callers branch on a boolean instead of
    ''' catching an exception. Used by EnsureSourcePerformanceIndexesAsync to skip
    ''' cleanly when a co-located source has no AACT data (e.g. the seeded
    ''' quickstart's standalone output-only database).
    ''' </summary>
    Friend Async Function SourceHasCtgovEligibilitiesAsync(cancellationToken As CancellationToken) As Task(Of Boolean)
        Using conn = Await _sourceDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT to_regclass('ctgov.eligibilities') IS NOT NULL"
                Dim result = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                Return result IsNot Nothing AndAlso Not Convert.IsDBNull(result) AndAlso CBool(result)
            End Using
        End Using
    End Function

    ''' <summary>
    ''' True when the source and output data sources point at the same physical
    ''' database — same host, port, and database name. Used to decide whether we
    ''' may create our performance index on the AACT-owned ctgov schema. The
    ''' NpgsqlDataSource.ConnectionString redacts the password but keeps host /
    ''' port / database, which is all we compare.
    ''' </summary>
    Private Function SourceAndOutputAreCoLocated() As Boolean
        Try
            Dim src As New NpgsqlConnectionStringBuilder(_sourceDataSource.ConnectionString)
            Dim out As New NpgsqlConnectionStringBuilder(_outputDataSource.ConnectionString)
            Return String.Equals(src.Host, out.Host, StringComparison.OrdinalIgnoreCase) _
                AndAlso src.Port = out.Port _
                AndAlso String.Equals(src.Database, out.Database, StringComparison.Ordinal)
        Catch
            ' A connection string we can't parse is treated as "not co-located" —
            ' the conservative choice (skip the DDL) rather than guessing.
            Return False
        End Try
    End Function

    ' ============ SelectNextTrialsAsync (source DB, spec section 2.3) ============

    Public Async Function SelectNextTrialsAsync(
            excludedNctIds As IReadOnlyList(Of String),
            direction As TrialSelectionDirection,
            studyCount As Integer,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of Trial)) _
            Implements IPostgresGateway.SelectNextTrialsAsync

        If _sourceDataSource Is Nothing Then
            Throw New InvalidOperationException(
                    "Source data source is not configured; cannot read trials from ctgov.eligibilities.")
        End If
        If studyCount <= 0 Then Return Array.Empty(Of Trial)()

        ' Clamp an oversized request. Without this, a fat-fingered StudyCount
        ' (e.g. 10000) makes the anti-join walk skip the entire dense prefix of
        ' already-processed trials before it finds enough fresh ones — which once
        ' ran past the source command timeout and failed the whole run.
        Dim effectiveCount = studyCount
        If _maxStudyCount > 0 AndAlso effectiveCount > _maxStudyCount Then
            _logger.LogWarning(
                    "Requested StudyCount {Requested} exceeds the configured maximum {Max}; clamping to {Max}.",
                    studyCount, _maxStudyCount, _maxStudyCount)
            effectiveCount = _maxStudyCount
        End If

        ' We anti-join against an exclusion set held in a session-temp table.
        ' For ~7k entries today the temp-table approach is roughly equivalent
        ' to passing a text[] parameter; the gain shows up later when the
        ' exclusion set reaches 100k+ where parameter-array serialisation
        ' starts to dominate. One code shape from the start avoids a
        ' future refactor under pressure.
        '
        ' The whole flow (DROP / CREATE TEMP / COPY-binary / SELECT) MUST
        ' happen on the same connection — temp tables are session-scoped in
        ' Postgres. The DROP IF EXISTS guards against Npgsql connection
        ' pooling handing us a recently-used connection that still carries
        ' the previous batch's temp table.
        Dim dir = If(direction = TrialSelectionDirection.Recent, "DESC", "ASC")

        ' Two-stage shape so the expensive wide column (criteria) is read ONLY for
        ' the rows actually returned. The inner query picks the next N nct_ids via
        ' the filter + anti-join + ORDER + LIMIT; when the matching partial index
        ' (ix_eligibilities_selectable_nct_id) exists this inner walk is index-only,
        ' so the ~100k already-processed rows it skips are never heap-fetched. The
        ' outer join then fetches criteria for just those N. (Without the index the
        ' plan is no worse than the previous single-statement form.)
        Dim selectSql = $"
SELECT e.nct_id, e.criteria
FROM (
    SELECT src.nct_id
    FROM ctgov.eligibilities src
    WHERE src.criteria IS NOT NULL
      AND LENGTH(TRIM(src.criteria)) >= 50
      AND src.criteria NOT ILIKE '%please contact%'
      AND src.criteria NOT ILIKE '%contact site for%'
      AND src.criteria NOT ILIKE '%contact study%'
      AND NOT EXISTS (SELECT 1 FROM pg_temp.excluded_nct_ids x WHERE x.nct_id = src.nct_id)
    ORDER BY src.nct_id {dir}
    LIMIT @study_count
) sel
JOIN ctgov.eligibilities e ON e.nct_id = sel.nct_id
ORDER BY e.nct_id {dir}"

        Dim trials As New List(Of Trial)
        Using conn = Await _sourceDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "DROP TABLE IF EXISTS pg_temp.excluded_nct_ids;
CREATE TEMP TABLE excluded_nct_ids (nct_id text PRIMARY KEY)"
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using

            If excludedNctIds IsNot Nothing AndAlso excludedNctIds.Count > 0 Then
                Using writer = Await conn.BeginBinaryImportAsync(
                        "COPY pg_temp.excluded_nct_ids (nct_id) FROM STDIN (FORMAT BINARY)",
                        cancellationToken).ConfigureAwait(False)
                    For Each id In excludedNctIds
                        Await writer.StartRowAsync(cancellationToken).ConfigureAwait(False)
                        Await writer.WriteAsync(id, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(False)
                    Next
                    Await writer.CompleteAsync(cancellationToken).ConfigureAwait(False)
                End Using
            End If

            Using cmd = conn.CreateCommand()
                cmd.CommandText = selectSql
                cmd.Parameters.Add(New NpgsqlParameter("study_count", NpgsqlDbType.Integer) With {
                        .Value = effectiveCount})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        ' Normalize criteria at the boundary so AACT markdown
                        ' escapes (e.g. `\>`) don't propagate downstream into
                        ' invalid JSON output from the LLM.
                        trials.Add(New Trial(
                                reader.GetString(0),
                                NormalizeAactText(reader.GetString(1))))
                    End While
                End Using
            End Using
        End Using
        Return trials
    End Function

    ' ============ GetAttemptedNctIdsAsync (output DB) ============

    Public Async Function GetAttemptedNctIdsAsync(
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String)) _
            Implements IPostgresGateway.GetAttemptedNctIdsAsync
        ' DISTINCT — a single nct_id can have many eligibility_study rows
        ' (one per attempt), but we only need it in the set once. Any status
        ' counts: success, parse_empty, parse_invalid_json, llm_failed,
        ' persist_failed, failed, cancelled, running. Failed trials surface
        ' for retry via the History tab's selection-mode Re-run, not via
        ' another forward / recent batch.
        Const Sql As String = "SELECT DISTINCT nct_id FROM public.eligibility_study"
        Dim ids As New List(Of String)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        ids.Add(reader.GetString(0))
                    End While
                End Using
            End Using
        End Using
        Return ids
    End Function

    ' ============ GetSourceTrialAsync (source DB, single-trial re-run path) ============

    Public Async Function GetSourceTrialAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of Trial) _
            Implements IPostgresGateway.GetSourceTrialAsync

        If String.IsNullOrWhiteSpace(nctId) Then Return Nothing
        If _sourceDataSource Is Nothing Then
            Throw New InvalidOperationException(
                    "Source data source is not configured; cannot read trials from ctgov.eligibilities.")
        End If

        ' No length / "please contact" filters here — re-runs are operator-
        ' driven and should process the trial regardless. The criteria still
        ' gets the markdown-escape normalisation NormalizeAactText applies.
        Const Sql As String = "
SELECT nct_id, criteria
FROM ctgov.eligibilities
WHERE nct_id = @nct_id"

        Using conn = Await _sourceDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    If Not Await reader.ReadAsync(cancellationToken).ConfigureAwait(False) Then
                        Return Nothing
                    End If
                    Dim criteria = If(reader.IsDBNull(1), "", reader.GetString(1))
                    Return New Trial(reader.GetString(0), NormalizeAactText(criteria))
                End Using
            End Using
        End Using
    End Function

    ' ============ GetSourceTrialsAsync (source DB, batched re-run path) ============

    Public Async Function GetSourceTrialsAsync(
            nctIds As IReadOnlyList(Of String),
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of Trial)) _
            Implements IPostgresGateway.GetSourceTrialsAsync

        Dim result As New List(Of Trial)
        If nctIds Is Nothing Then Return result

        ' Drop blanks and de-duplicate (case-sensitive: AACT nct_ids are upper-case
        ' canonical). Nothing to fetch -> skip the round-trip entirely.
        Dim ids = nctIds.
                Where(Function(id) Not String.IsNullOrWhiteSpace(id)).
                Select(Function(id) id.Trim()).
                Distinct().
                ToArray()
        If ids.Length = 0 Then Return result

        If _sourceDataSource Is Nothing Then
            Throw New InvalidOperationException(
                    "Source data source is not configured; cannot read trials from ctgov.eligibilities.")
        End If

        ' Single round-trip for the whole selection. No length / "please contact"
        ' filters here -- re-runs are operator-driven and process the trial
        ' regardless, matching GetSourceTrialAsync. Criteria still gets the
        ' markdown-escape normalisation NormalizeAactText applies.
        Const Sql As String = "
SELECT nct_id, criteria
FROM ctgov.eligibilities
WHERE nct_id = ANY(@nct_ids)"

        Using conn = Await _sourceDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("nct_ids", NpgsqlDbType.Array Or NpgsqlDbType.Text) With {.Value = ids})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        Dim criteria = If(reader.IsDBNull(1), "", reader.GetString(1))
                        result.Add(New Trial(reader.GetString(0), NormalizeAactText(criteria)))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    ' ============ PersistTrialAsync (output DB, transactional, spec section 2.8.2) ============

    Public Async Function PersistTrialAsync(
            nctId As String,
            records As IReadOnlyList(Of ResolvedRecord),
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.PersistTrialAsync
        If String.IsNullOrWhiteSpace(nctId) Then
            Throw New ArgumentException("nctId must be non-empty", NameOf(nctId))
        End If

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using tx = Await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(False)
                ' DELETE existing rows for this trial.
                Using deleteCmd = conn.CreateCommand()
                    deleteCmd.Transaction = tx
                    deleteCmd.CommandText = "DELETE FROM public.eligibility WHERE nct_id = @nct_id"
                    deleteCmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                    Await deleteCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                End Using

                ' INSERT the new rows (multi-row VALUES, all parameterised).
                If records IsNot Nothing AndAlso records.Count > 0 Then
                    Using insertCmd = conn.CreateCommand()
                        insertCmd.Transaction = tx
                        BuildMultiRowInsert(insertCmd, records)
                        Await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                    End Using
                End If

                Await tx.CommitAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    ''' <summary>
    ''' Builds a single multi-row INSERT into public.eligibility, with every
    ''' value parameterised. Spec section 2.8.1 column order. Postgres allows up
    ''' to ~65k parameters; with 12 cols that is ~5,400 rows, and a single
    ''' trial's row count is bounded well below that by the LLM token budget
    ''' (no fixed entry cap — spec 2.4.2) — comfortably under.
    ''' </summary>
    Friend Shared Sub BuildMultiRowInsert(cmd As NpgsqlCommand, records As IReadOnlyList(Of ResolvedRecord))
        Dim sb As New StringBuilder()
        sb.Append("INSERT INTO public.eligibility (")
        sb.Append("nct_id, criterion, domain, concept, concept_code, semantic_type, semantic_type_tuis, ")
        sb.Append("qualifier, time_window, original_text, umls_name, match_score, match_source")
        sb.Append(") VALUES ")

        For i As Integer = 0 To records.Count - 1
            If i > 0 Then sb.Append(", ")
            sb.Append("(")
            sb.Append("@p").Append(i).Append("_nct_id, ")
            sb.Append("@p").Append(i).Append("_criterion, ")
            sb.Append("@p").Append(i).Append("_domain, ")
            sb.Append("@p").Append(i).Append("_concept, ")
            sb.Append("@p").Append(i).Append("_concept_code, ")
            sb.Append("@p").Append(i).Append("_semantic_type, ")
            sb.Append("@p").Append(i).Append("_semantic_type_tuis, ")
            sb.Append("@p").Append(i).Append("_qualifier, ")
            sb.Append("@p").Append(i).Append("_time_window, ")
            sb.Append("@p").Append(i).Append("_original_text, ")
            sb.Append("@p").Append(i).Append("_umls_name, ")
            sb.Append("@p").Append(i).Append("_match_score, ")
            sb.Append("@p").Append(i).Append("_match_source")
            sb.Append(")")

            Dim r = records(i)
            cmd.Parameters.Add(New NpgsqlParameter($"p{i}_nct_id", NpgsqlDbType.Text) With {.Value = r.NctId})
            cmd.Parameters.Add(New NpgsqlParameter($"p{i}_criterion", NpgsqlDbType.Text) With {.Value = r.Criterion})
            cmd.Parameters.Add(New NpgsqlParameter($"p{i}_domain", NpgsqlDbType.Text) With {.Value = r.Domain})
            cmd.Parameters.Add(New NpgsqlParameter($"p{i}_concept", NpgsqlDbType.Text) With {.Value = r.Concept})
            cmd.Parameters.Add(New NpgsqlParameter($"p{i}_concept_code", NpgsqlDbType.Text) With {.Value = NullIfEmpty(r.ConceptCode)})
            cmd.Parameters.Add(New NpgsqlParameter($"p{i}_semantic_type", NpgsqlDbType.Text) With {.Value = NullIfEmpty(r.SemanticType)})
            ' NULL rather than an empty array for unresolved rows. NULL means "no
            ' concept, so no semantic types"; an empty array is a distinct value
            ' that containment queries could match, making unresolved rows look
            ' like a category of their own.
            cmd.Parameters.Add(New NpgsqlParameter($"p{i}_semantic_type_tuis", NpgsqlDbType.Array Or NpgsqlDbType.Text) With {
                    .Value = If(r.SemanticTypeTuis Is Nothing OrElse r.SemanticTypeTuis.Count = 0,
                                CObj(DBNull.Value), CObj(r.SemanticTypeTuis.ToArray()))})
            cmd.Parameters.Add(New NpgsqlParameter($"p{i}_qualifier", NpgsqlDbType.Text) With {.Value = NullIfEmpty(r.Qualifier)})
            cmd.Parameters.Add(New NpgsqlParameter($"p{i}_time_window", NpgsqlDbType.Text) With {.Value = NullIfEmpty(r.TimeWindow)})
            cmd.Parameters.Add(New NpgsqlParameter($"p{i}_original_text", NpgsqlDbType.Text) With {.Value = NullIfEmpty(r.OriginalText)})
            cmd.Parameters.Add(New NpgsqlParameter($"p{i}_umls_name", NpgsqlDbType.Text) With {.Value = NullIfEmpty(r.UmlsName)})
            cmd.Parameters.Add(New NpgsqlParameter($"p{i}_match_score", NpgsqlDbType.Numeric) With {.Value = CDec(r.MatchScore)})
            cmd.Parameters.Add(New NpgsqlParameter($"p{i}_match_source", NpgsqlDbType.Text) With {.Value = NullIfEmpty(r.MatchSource)})
        Next

        cmd.CommandText = sb.ToString()
    End Sub

    Friend Shared Function NullIfEmpty(value As String) As Object
        If String.IsNullOrEmpty(value) Then Return DBNull.Value
        Return value
    End Function

    ' Max length for values stored in a btree-indexed text column (audit_log.entity_id
    ' via ix_audit_log_entity). Kept well below Postgres's ~2704-byte index row-size
    ' limit; NCT ids are ASCII so chars map 1:1 to bytes with margin to spare.
    Friend Const MaxIndexedTextLength As Integer = 2000

    Friend Shared Function TruncateForIndex(value As String) As String
        If value Is Nothing OrElse value.Length <= MaxIndexedTextLength Then Return value
        Return value.Substring(0, MaxIndexedTextLength)
    End Function

    ' ============ UMLS-only retry (output DB, V19) ============

    Public Async Function SelectTrialsToRetryUmlsAsync(
            direction As TrialSelectionDirection,
            count As Integer,
            includeRetried As Boolean,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String)) _
            Implements IPostgresGateway.SelectTrialsToRetryUmlsAsync

        Dim capped = Math.Min(Math.Max(count, 1), 100000)
        ' Derived from the enum only — never user input — so safe to interpolate.
        Dim order = If(direction = TrialSelectionDirection.Recent, "DESC", "ASC")
        Dim sql As String =
"SELECT DISTINCT e.nct_id
FROM public.eligibility e
WHERE (e.concept_code IS NULL OR e.concept_code = '')
  AND e.concept IS NOT NULL AND e.concept <> ''
  AND (@include_retried OR NOT EXISTS (
        SELECT 1 FROM public.eligibility_umls_retry r WHERE r.nct_id = e.nct_id))
ORDER BY e.nct_id " & order & "
LIMIT @count"

        Dim result As New List(Of String)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = sql
                cmd.Parameters.Add(New NpgsqlParameter("include_retried", NpgsqlDbType.Boolean) With {.Value = includeRetried})
                cmd.Parameters.Add(New NpgsqlParameter("count", NpgsqlDbType.Integer) With {.Value = capped})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(reader.GetString(0))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function GetUnresolvedRowsForTrialAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of UmlsRetryRow)) _
            Implements IPostgresGateway.GetUnresolvedRowsForTrialAsync

        Dim result As New List(Of UmlsRetryRow)
        If String.IsNullOrWhiteSpace(nctId) Then Return result
        Const sql As String =
"SELECT id, concept
FROM public.eligibility
WHERE nct_id = @nct_id
  AND (concept_code IS NULL OR concept_code = '')
  AND concept IS NOT NULL AND concept <> ''
ORDER BY id"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = sql
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(New UmlsRetryRow(reader.GetInt64(0), reader.GetString(1)))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function ApplyUmlsRetryAsync(
            nctId As String,
            results As IReadOnlyList(Of UmlsRetryResult),
            rowsAttempted As Integer,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.ApplyUmlsRetryAsync

        If String.IsNullOrWhiteSpace(nctId) Then
            Throw New ArgumentException("nctId must be non-empty", NameOf(nctId))
        End If
        Dim safeResults = If(results, CType(Array.Empty(Of UmlsRetryResult)(), IReadOnlyList(Of UmlsRetryResult)))

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using tx = Await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(False)
                ' Targeted per-row UPDATE of only the UMLS columns — preserves id,
                ' created_at, and the already-resolved rows (unlike DELETE+INSERT).
                For Each r In safeResults
                    Using updateCmd = conn.CreateCommand()
                        updateCmd.Transaction = tx
                        updateCmd.CommandText =
"UPDATE public.eligibility
SET concept_code = @cc, umls_name = @un, match_source = @ms,
    match_score = @sc, semantic_type = @st, semantic_type_tuis = @stt
WHERE id = @id"
                        updateCmd.Parameters.Add(New NpgsqlParameter("cc", NpgsqlDbType.Text) With {.Value = NullIfEmpty(r.ConceptCode)})
                        updateCmd.Parameters.Add(New NpgsqlParameter("un", NpgsqlDbType.Text) With {.Value = NullIfEmpty(r.UmlsName)})
                        updateCmd.Parameters.Add(New NpgsqlParameter("ms", NpgsqlDbType.Text) With {.Value = NullIfEmpty(r.MatchSource)})
                        updateCmd.Parameters.Add(New NpgsqlParameter("sc", NpgsqlDbType.Numeric) With {.Value = CDec(r.MatchScore)})
                        updateCmd.Parameters.Add(New NpgsqlParameter("st", NpgsqlDbType.Text) With {.Value = NullIfEmpty(r.SemanticType)})
                        ' NULL rather than an empty array when there are no TUIs -
                        ' NULL means "no semantic types", and an empty array would
                        ' be a distinct, matchable value to containment queries.
                        updateCmd.Parameters.Add(New NpgsqlParameter("stt", NpgsqlDbType.Array Or NpgsqlDbType.Text) With {
                                .Value = If(r.SemanticTypeTuis Is Nothing OrElse r.SemanticTypeTuis.Count = 0,
                                            CObj(DBNull.Value), CObj(r.SemanticTypeTuis.ToArray()))})
                        updateCmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Bigint) With {.Value = r.Id})
                        Await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                    End Using
                Next

                ' Record the attempt (even when nothing newly resolved) so the trial
                ' is anti-joined out of subsequent batches.
                Using upsertCmd = conn.CreateCommand()
                    upsertCmd.Transaction = tx
                    upsertCmd.CommandText =
"INSERT INTO public.eligibility_umls_retry (nct_id, retried_at, rows_attempted, rows_resolved)
VALUES (@nct_id, now(), @attempted, @resolved)
ON CONFLICT (nct_id) DO UPDATE SET
    retried_at = now(),
    rows_attempted = EXCLUDED.rows_attempted,
    rows_resolved = EXCLUDED.rows_resolved"
                    upsertCmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                    upsertCmd.Parameters.Add(New NpgsqlParameter("attempted", NpgsqlDbType.Integer) With {.Value = rowsAttempted})
                    upsertCmd.Parameters.Add(New NpgsqlParameter("resolved", NpgsqlDbType.Integer) With {.Value = safeResults.Count})
                    Await upsertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                End Using

                Await tx.CommitAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    ' ============ LLM concept-normalization (output DB, V20) ============

    Public Async Function SelectConceptsToNormalizeAsync(
            count As Integer,
            includeAttempted As Boolean,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of ConceptToNormalize)) _
            Implements IPostgresGateway.SelectConceptsToNormalizeAsync

        Dim capped = Math.Min(Math.Max(count, 1), 100000)
        ' Distinct residue concepts by normalized key, most-frequent first (best ROI),
        ' with a representative original phrasing (min) so the LLM sees real casing.
        Const sql As String =
"WITH residue AS (
    SELECT regexp_replace(lower(btrim(concept)), '\s+', ' ', 'g') AS concept_norm, concept
    FROM public.eligibility
    WHERE (concept_code IS NULL OR concept_code = '')
      AND concept IS NOT NULL AND concept <> ''
)
SELECT r.concept_norm, min(r.concept) AS sample_concept
FROM residue r
WHERE @include_attempted OR NOT EXISTS (
        SELECT 1 FROM umls.concept_normalization n WHERE n.concept_norm = r.concept_norm)
GROUP BY r.concept_norm
ORDER BY count(*) DESC, r.concept_norm
LIMIT @count"

        Dim result As New List(Of ConceptToNormalize)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = sql
                cmd.Parameters.Add(New NpgsqlParameter("include_attempted", NpgsqlDbType.Boolean) With {.Value = includeAttempted})
                cmd.Parameters.Add(New NpgsqlParameter("count", NpgsqlDbType.Integer) With {.Value = capped})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(New ConceptToNormalize(reader.GetString(0), reader.GetString(1)))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function CountConceptsToNormalizeAsync(
            includeAttempted As Boolean,
            cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.CountConceptsToNormalizeAsync

        ' Mirrors SelectConceptsToNormalizeAsync's residue set, counted (no LIMIT).
        Const sql As String =
"WITH residue AS (
    SELECT regexp_replace(lower(btrim(concept)), '\s+', ' ', 'g') AS concept_norm
    FROM public.eligibility
    WHERE (concept_code IS NULL OR concept_code = '')
      AND concept IS NOT NULL AND concept <> ''
)
SELECT count(DISTINCT r.concept_norm)
FROM residue r
WHERE @include_attempted OR NOT EXISTS (
        SELECT 1 FROM umls.concept_normalization n WHERE n.concept_norm = r.concept_norm)"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = sql
                cmd.Parameters.Add(New NpgsqlParameter("include_attempted", NpgsqlDbType.Boolean) With {.Value = includeAttempted})
                Dim scalar = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                Return If(scalar Is Nothing OrElse scalar Is DBNull.Value, 0, Convert.ToInt32(scalar))
            End Using
        End Using
    End Function

    ' ============ Superseded study attempts (Tools tab housekeeping) ============
    '
    ' "Superseded" is defined exactly as the Studies tab's "Hide superseded
    ' attempts" toggle defines it (SearchStudiesAsync): rank attempts per NCT_ID by
    ' started_at DESC and keep rn = 1. This counts/deletes the complement, rn > 1.
    ' The two MUST stay in step - if they ever diverge, the Tools card would offer
    ' to delete rows the Studies tab still shows as current.
    '
    ' On ties (two attempts with an identical started_at) ROW_NUMBER picks
    ' arbitrarily but consistently within one statement, so exactly one row per
    ' NCT_ID still survives. timestamptz ties are vanishingly unlikely here, and
    ' matching the Studies view's ordering matters more than inventing a tiebreak
    ' that would make the two disagree.
    Private Const SupersededRanked As String = "
    SELECT run_id, nct_id,
           ROW_NUMBER() OVER (PARTITION BY nct_id ORDER BY started_at DESC) AS rn
    FROM public.eligibility_study"

    Public Async Function CountSupersededStudiesAsync(
            cancellationToken As CancellationToken) As Task(Of Long) _
            Implements IPostgresGateway.CountSupersededStudiesAsync

        Const sql As String = "
SELECT COUNT(*)
FROM (" & SupersededRanked & "
) ranked
WHERE ranked.rn > 1"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = sql
                Dim scalar = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                Return If(scalar Is Nothing OrElse scalar Is DBNull.Value, 0L, Convert.ToInt64(scalar))
            End Using
        End Using
    End Function

    Public Async Function DeleteSupersededStudiesAsync(
            cancellationToken As CancellationToken) As Task(Of Long) _
            Implements IPostgresGateway.DeleteSupersededStudiesAsync

        ' DELETE ... USING joins back on the (run_id, nct_id) primary key, so the
        ' window is evaluated once against a stable snapshot and each doomed row is
        ' matched by key rather than re-ranked per row. One statement = one implicit
        ' transaction: it either removes every superseded row or none.
        Const sql As String = "
DELETE FROM public.eligibility_study s
USING (" & SupersededRanked & "
) ranked
WHERE ranked.rn > 1
  AND s.run_id = ranked.run_id
  AND s.nct_id = ranked.nct_id"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = sql
                Dim deleted = Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                _logger.LogInformation(
                        "Removed {Deleted} superseded eligibility_study rows (latest attempt per NCT_ID kept)", deleted)
                Return CLng(deleted)
            End Using
        End Using
    End Function

    Public Async Function RecordConceptNormalizationAsync(
            conceptNorm As String,
            normalizedTerm As String,
            match As UmlsMatch,
            semanticType As String,
            cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.RecordConceptNormalizationAsync

        If String.IsNullOrWhiteSpace(conceptNorm) Then
            Throw New ArgumentException("conceptNorm must be non-empty", NameOf(conceptNorm))
        End If
        Dim m = If(match, UmlsMatch.Unresolved)
        Dim resolved = m.IsResolved
        Dim rowsUpdated As Integer = 0

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using tx = Await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(False)
                ' Upsert the cache row — records the attempt regardless of outcome.
                Using upsertCmd = conn.CreateCommand()
                    upsertCmd.Transaction = tx
                    upsertCmd.CommandText =
"INSERT INTO umls.concept_normalization
    (concept_norm, normalized_term, concept_code, umls_name, match_source, match_score, semantic_type, resolved, normalized_at)
VALUES (@cn, @nt, @cc, @un, @ms, @sc, @st, @resolved, now())
ON CONFLICT (concept_norm) DO UPDATE SET
    normalized_term = EXCLUDED.normalized_term,
    concept_code    = EXCLUDED.concept_code,
    umls_name       = EXCLUDED.umls_name,
    match_source    = EXCLUDED.match_source,
    match_score     = EXCLUDED.match_score,
    semantic_type   = EXCLUDED.semantic_type,
    resolved        = EXCLUDED.resolved,
    normalized_at   = now()"
                    upsertCmd.Parameters.Add(New NpgsqlParameter("cn", NpgsqlDbType.Text) With {.Value = conceptNorm})
                    upsertCmd.Parameters.Add(New NpgsqlParameter("nt", NpgsqlDbType.Text) With {.Value = If(normalizedTerm, "")})
                    upsertCmd.Parameters.Add(New NpgsqlParameter("cc", NpgsqlDbType.Text) With {.Value = NullIfEmpty(m.ConceptCode)})
                    upsertCmd.Parameters.Add(New NpgsqlParameter("un", NpgsqlDbType.Text) With {.Value = NullIfEmpty(m.UmlsName)})
                    upsertCmd.Parameters.Add(New NpgsqlParameter("ms", NpgsqlDbType.Text) With {.Value = NullIfEmpty(m.MatchSource)})
                    upsertCmd.Parameters.Add(New NpgsqlParameter("sc", NpgsqlDbType.Numeric) With {.Value = CDec(m.MatchScore)})
                    upsertCmd.Parameters.Add(New NpgsqlParameter("st", NpgsqlDbType.Text) With {.Value = NullIfEmpty(If(semanticType, ""))})
                    upsertCmd.Parameters.Add(New NpgsqlParameter("resolved", NpgsqlDbType.Boolean) With {.Value = resolved})
                    Await upsertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                End Using

                ' When resolved, apply the mapping in place to every matching residue
                ' row (concept_code empty AND same normalized concept).
                If resolved Then
                    Using updateCmd = conn.CreateCommand()
                        updateCmd.Transaction = tx
                        updateCmd.CommandText =
"UPDATE public.eligibility
SET concept_code = @cc, umls_name = @un, match_source = @ms,
    match_score = @sc, semantic_type = @st
WHERE (concept_code IS NULL OR concept_code = '')
  AND regexp_replace(lower(btrim(concept)), '\s+', ' ', 'g') = @cn"
                        updateCmd.Parameters.Add(New NpgsqlParameter("cc", NpgsqlDbType.Text) With {.Value = NullIfEmpty(m.ConceptCode)})
                        updateCmd.Parameters.Add(New NpgsqlParameter("un", NpgsqlDbType.Text) With {.Value = NullIfEmpty(m.UmlsName)})
                        updateCmd.Parameters.Add(New NpgsqlParameter("ms", NpgsqlDbType.Text) With {.Value = NullIfEmpty(m.MatchSource)})
                        updateCmd.Parameters.Add(New NpgsqlParameter("sc", NpgsqlDbType.Numeric) With {.Value = CDec(m.MatchScore)})
                        updateCmd.Parameters.Add(New NpgsqlParameter("st", NpgsqlDbType.Text) With {.Value = NullIfEmpty(If(semanticType, ""))})
                        updateCmd.Parameters.Add(New NpgsqlParameter("cn", NpgsqlDbType.Text) With {.Value = conceptNorm})
                        rowsUpdated = Await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                    End Using
                End If

                Await tx.CommitAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
        Return rowsUpdated
    End Function

    Public Async Function GetCachedNormalizationsAsync(
            concepts As IReadOnlyList(Of String),
            cancellationToken As CancellationToken) As Task(Of IReadOnlyDictionary(Of String, CachedConceptResolution)) _
            Implements IPostgresGateway.GetCachedNormalizationsAsync

        Dim result As New Dictionary(Of String, CachedConceptResolution)(StringComparer.Ordinal)
        If concepts Is Nothing Then Return result
        Dim raw = concepts.Where(Function(c) Not String.IsNullOrWhiteSpace(c)).Distinct().ToArray()
        If raw.Length = 0 Then Return result

        ' Normalize each raw concept in SQL with the SAME expression the cache is
        ' written under, and return keyed by the original raw concept so the caller
        ' (the Core orchestrator) needs no Data-layer normalization.
        Const sql As String =
"SELECT q.raw, n.concept_code, n.umls_name, n.match_source, n.match_score, n.semantic_type
FROM unnest(@raw) AS q(raw)
JOIN umls.concept_normalization n
  ON n.concept_norm = regexp_replace(lower(btrim(q.raw)), '\s+', ' ', 'g')
WHERE n.resolved = true AND n.concept_code IS NOT NULL AND n.concept_code <> ''"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = sql
                cmd.Parameters.Add(New NpgsqlParameter("raw", NpgsqlDbType.Array Or NpgsqlDbType.Text) With {.Value = raw})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result(reader.GetString(0)) = New CachedConceptResolution(
                                conceptCode:=reader.GetString(1),
                                umlsName:=If(reader.IsDBNull(2), "", reader.GetString(2)),
                                matchSource:=If(reader.IsDBNull(3), "", reader.GetString(3)),
                                matchScore:=CDbl(reader.GetDecimal(4)),
                                semanticType:=If(reader.IsDBNull(5), "", reader.GetString(5)))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    ' ============ RecordRunAsync (output DB, UPSERT on run_id) ============

    Public Async Function RecordRunAsync(
            metrics As RunMetrics,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.RecordRunAsync
        If metrics Is Nothing Then Throw New ArgumentNullException(NameOf(metrics))
        Const Sql As String = "
INSERT INTO public.eligibility_run (
    run_id, started_at, ended_at, trigger_source, study_count,
    studies_processed, rows_persisted, resolution_rate, status, error_summary,
    concurrency_cap
) VALUES (
    @run_id, @started_at, @ended_at, @trigger_source, @study_count,
    @studies_processed, @rows_persisted, @resolution_rate, @status, @error_summary,
    @concurrency_cap
)
ON CONFLICT (run_id) DO UPDATE
    SET ended_at          = excluded.ended_at,
        studies_processed = excluded.studies_processed,
        rows_persisted    = excluded.rows_persisted,
        resolution_rate   = excluded.resolution_rate,
        status            = excluded.status,
        error_summary     = excluded.error_summary,
        concurrency_cap   = excluded.concurrency_cap"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("run_id", NpgsqlDbType.Uuid) With {.Value = metrics.RunId})
                cmd.Parameters.Add(New NpgsqlParameter("started_at", NpgsqlDbType.TimestampTz) With {.Value = metrics.StartedAt})
                cmd.Parameters.Add(New NpgsqlParameter("ended_at", NpgsqlDbType.TimestampTz) With {
                        .Value = If(metrics.EndedAt.HasValue, CObj(metrics.EndedAt.Value), DBNull.Value)})
                cmd.Parameters.Add(New NpgsqlParameter("trigger_source", NpgsqlDbType.Text) With {
                        .Value = If(metrics.TriggerSource, "")})
                cmd.Parameters.Add(New NpgsqlParameter("study_count", NpgsqlDbType.Integer) With {.Value = metrics.StudyCount})
                cmd.Parameters.Add(New NpgsqlParameter("studies_processed", NpgsqlDbType.Integer) With {.Value = metrics.StudiesProcessed})
                cmd.Parameters.Add(New NpgsqlParameter("rows_persisted", NpgsqlDbType.Integer) With {.Value = metrics.RowsPersisted})
                cmd.Parameters.Add(New NpgsqlParameter("resolution_rate", NpgsqlDbType.Numeric) With {.Value = CDec(metrics.ResolutionRate)})
                cmd.Parameters.Add(New NpgsqlParameter("status", NpgsqlDbType.Text) With {.Value = If(metrics.Status, "")})
                cmd.Parameters.Add(New NpgsqlParameter("error_summary", NpgsqlDbType.Text) With {.Value = NullIfEmpty(metrics.ErrorSummary)})
                cmd.Parameters.Add(New NpgsqlParameter("concurrency_cap", NpgsqlDbType.Integer) With {
                        .Value = If(metrics.ConcurrencyCap.HasValue, CObj(metrics.ConcurrencyCap.Value), DBNull.Value)})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    ' ============ ResolveInterruptedRunAsync (output DB, guarded, transactional) ============

    Public Async Function ResolveInterruptedRunAsync(
            runId As Guid,
            status As String,
            reason As String,
            cancellationToken As CancellationToken) As Task(Of (RunUpdated As Boolean, StudiesReconciled As Integer)) _
            Implements IPostgresGateway.ResolveInterruptedRunAsync
        If String.IsNullOrWhiteSpace(status) Then
            Throw New ArgumentException("status must be non-empty", NameOf(status))
        End If
        If String.IsNullOrWhiteSpace(reason) Then
            Throw New ArgumentException("reason must be non-empty", NameOf(reason))
        End If

        ' AND status = 'running' is the concurrency guard - see the interface
        ' remarks. COALESCE on ended_at because a stranded row always has NULL
        ' there; the coalesce stops this inventing a second ending if it ever
        ' does not.
        Const RunSql As String = "
UPDATE public.eligibility_run
   SET status        = @status,
       error_summary = @reason,
       ended_at      = COALESCE(ended_at, @now)
 WHERE run_id = @run_id
   AND status = 'running'"

        ' Scoped to the one run_id and NOT age-filtered. The existing
        ' error_message wins when present: a row that recorded its own failure
        ' knows more than the blanket run reason does.
        Const StudySql As String = "
UPDATE public.eligibility_study
   SET status        = 'interrupted',
       finished_at   = @now,
       error_message = COALESCE(NULLIF(error_message, ''), @reason)
 WHERE run_id = @run_id
   AND status = 'running'"

        Dim now = DateTimeOffset.UtcNow

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using tx = Await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(False)
                Dim runUpdated As Integer
                Using cmd = conn.CreateCommand()
                    cmd.Transaction = tx
                    cmd.CommandText = RunSql
                    cmd.Parameters.Add(New NpgsqlParameter("run_id", NpgsqlDbType.Uuid) With {.Value = runId})
                    cmd.Parameters.Add(New NpgsqlParameter("status", NpgsqlDbType.Text) With {.Value = status})
                    cmd.Parameters.Add(New NpgsqlParameter("reason", NpgsqlDbType.Text) With {.Value = reason})
                    cmd.Parameters.Add(New NpgsqlParameter("now", NpgsqlDbType.TimestampTz) With {.Value = now})
                    runUpdated = Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                End Using

                ' No run was resolved, so there is nothing to cascade to. Commit
                ' the empty transaction and report the no-op.
                If runUpdated = 0 Then
                    Await tx.CommitAsync(cancellationToken).ConfigureAwait(False)
                    Return (False, 0)
                End If

                Dim studiesReconciled As Integer
                Using cmd = conn.CreateCommand()
                    cmd.Transaction = tx
                    cmd.CommandText = StudySql
                    cmd.Parameters.Add(New NpgsqlParameter("run_id", NpgsqlDbType.Uuid) With {.Value = runId})
                    cmd.Parameters.Add(New NpgsqlParameter("reason", NpgsqlDbType.Text) With {.Value = reason})
                    cmd.Parameters.Add(New NpgsqlParameter("now", NpgsqlDbType.TimestampTz) With {.Value = now})
                    studiesReconciled = Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                End Using

                Await tx.CommitAsync(cancellationToken).ConfigureAwait(False)
                _logger.LogInformation(
                        "Run {RunId} manually resolved to '{Status}'; {Count} stranded study row(s) reconciled to 'interrupted'.",
                        runId, status, studiesReconciled)
                Return (True, studiesReconciled)
            End Using
        End Using
    End Function

    ' ============ RecordFailedTrialAsync (output DB, UPSERT, increments attempt_count) ============

    Public Async Function RecordFailedTrialAsync(
            nctId As String,
            errorMessage As String,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.RecordFailedTrialAsync
        If String.IsNullOrWhiteSpace(nctId) Then
            Throw New ArgumentException("nctId must be non-empty", NameOf(nctId))
        End If
        Const Sql As String = "
INSERT INTO public.eligibility_failed (nct_id, last_attempted, attempt_count, last_error)
VALUES (@nct_id, now(), 1, @last_error)
ON CONFLICT (nct_id) DO UPDATE
    SET last_attempted = excluded.last_attempted,
        attempt_count  = public.eligibility_failed.attempt_count + 1,
        last_error     = excluded.last_error"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                cmd.Parameters.Add(New NpgsqlParameter("last_error", NpgsqlDbType.Text) With {.Value = NullIfEmpty(errorMessage)})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    ' ============ GetRecentRunsAsync (output DB) ============

    Public Function GetRecentRunsAsync(
            limit As Integer,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of RunMetrics)) _
            Implements IPostgresGateway.GetRecentRunsAsync
        Return GetRunsPageAsync(limit, 0, cancellationToken)
    End Function

    Public Async Function GetRunsPageAsync(
            limit As Integer,
            offset As Integer,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of RunMetrics)) _
            Implements IPostgresGateway.GetRunsPageAsync
        ' Clamp to keep a misbehaving caller from asking for a million rows.
        Dim cappedLimit = Math.Min(Math.Max(limit, 1), 500)
        Dim cappedOffset = Math.Max(offset, 0)
        ' completion_tokens is summed from eligibility_study (eligibility_run does
        ' not store a token total) via a single grouped LEFT JOIN, so the Runs
        ' table can show aggregate decode throughput (tokens ÷ wall clock). The
        ' SUM counts SUCCESSFUL trials only — failed / truncated (parse_invalid_json)
        ' trials burn tokens without producing useful output and would distort the
        ' throughput guide, so they are excluded via the FILTER clause.
        Const Sql As String = "
SELECT r.run_id, r.started_at, r.ended_at, r.trigger_source, r.study_count,
       r.studies_processed, r.rows_persisted, r.resolution_rate, r.status, r.error_summary,
       COALESCE(t.completion_tokens, 0) AS completion_tokens, r.concurrency_cap,
       t.avg_llm_ms, t.avg_umls_ms, t.avg_persist_ms
FROM public.eligibility_run r
LEFT JOIN (
    SELECT run_id,
           SUM(llm_completion_tokens) FILTER (WHERE status = 'success') AS completion_tokens,
           AVG(llm_ms)     FILTER (WHERE status = 'success') AS avg_llm_ms,
           AVG(umls_ms)    FILTER (WHERE status = 'success') AS avg_umls_ms,
           AVG(persist_ms) FILTER (WHERE status = 'success') AS avg_persist_ms
    FROM public.eligibility_study
    GROUP BY run_id
) t ON t.run_id = r.run_id
ORDER BY r.started_at DESC
LIMIT @limit OFFSET @offset"

        Dim runs As New List(Of RunMetrics)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("limit", NpgsqlDbType.Integer) With {.Value = cappedLimit})
                cmd.Parameters.Add(New NpgsqlParameter("offset", NpgsqlDbType.Integer) With {.Value = cappedOffset})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        runs.Add(New RunMetrics(
                                runId:=reader.GetGuid(0),
                                startedAt:=reader.GetFieldValue(Of DateTimeOffset)(1),
                                endedAt:=If(reader.IsDBNull(2),
                                            CType(Nothing, DateTimeOffset?),
                                            reader.GetFieldValue(Of DateTimeOffset)(2)),
                                triggerSource:=reader.GetString(3),
                                studyCount:=reader.GetInt32(4),
                                studiesProcessed:=If(reader.IsDBNull(5), 0, reader.GetInt32(5)),
                                rowsPersisted:=If(reader.IsDBNull(6), 0, reader.GetInt32(6)),
                                resolutionRate:=If(reader.IsDBNull(7), 0.0, CDbl(reader.GetDecimal(7))),
                                status:=reader.GetString(8),
                                errorSummary:=If(reader.IsDBNull(9), "", reader.GetString(9)),
                                completionTokens:=If(reader.IsDBNull(10), 0L, reader.GetInt64(10)),
                                concurrencyCap:=If(reader.IsDBNull(11), CType(Nothing, Integer?), reader.GetInt32(11)),
                                avgLlmMs:=If(reader.IsDBNull(12), CType(Nothing, Double?), CDbl(reader.GetValue(12))),
                                avgUmlsMs:=If(reader.IsDBNull(13), CType(Nothing, Double?), CDbl(reader.GetValue(13))),
                                avgPersistMs:=If(reader.IsDBNull(14), CType(Nothing, Double?), CDbl(reader.GetValue(14)))))
                    End While
                End Using
            End Using
        End Using
        Return runs
    End Function

    Public Async Function CountRunsAsync(
            cancellationToken As CancellationToken) As Task(Of Long) _
            Implements IPostgresGateway.CountRunsAsync
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT COUNT(*) FROM public.eligibility_run"
                Dim result = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                Return If(result Is Nothing OrElse result Is DBNull.Value, 0L, CLng(result))
            End Using
        End Using
    End Function

    ' ============ GetDashboardMetricsAsync (output DB) ============

    Public Async Function GetDashboardMetricsAsync(
            cancellationToken As CancellationToken) As Task(Of DashboardMetrics) _
            Implements IPostgresGateway.GetDashboardMetricsAsync
        ' Single round-trip. The `latest` CTE collapses eligibility_study to
        ' one row per nct_id (the most recent attempt's status); the outer
        ' SELECT then counts eligibility rows, latest-attempt success,
        ' latest-attempt failure, the UMLS resolution rate (non-null
        ' concept_code / total), and total LLM tokens consumed across every
        ' study attempt. NULLIF on the resolution-rate denominator guards
        ' an empty eligibility table.
        '
        ' "Failure" excludes success / running PLUS parse_empty and cancelled,
        ' which are valid terminal states — parse_empty means the LLM looked
        ' at the trial and said "no eligibility criteria here" (legitimate
        ' zero-record output), cancelled means the operator stopped the run.
        ' Neither indicates anything is broken with the trial's processing.
        ' The remaining failure statuses are llm_failed, parse_invalid_json,
        ' persist_failed, and the generic 'failed'.
        '
        ' prompt_tokens / completion_tokens sum the respective columns across
        ' every audit row, treating NULL as 0. Includes every attempt
        ' regardless of status — the LLM bills for tokens whether the parse
        ' later succeeds or fails.
        Const Sql As String = "
WITH latest AS (
    SELECT DISTINCT ON (nct_id) status
    FROM public.eligibility_study
    ORDER BY nct_id, started_at DESC
)
SELECT
    (SELECT COUNT(*) FROM public.eligibility)                                    AS eligibility_rows,
    (SELECT COUNT(*) FROM latest WHERE status = 'success')                       AS studies_successful,
    (SELECT COUNT(*) FROM latest
        WHERE status NOT IN ('success', 'running', 'parse_empty', 'cancelled'))  AS studies_failed_latest,
    COALESCE(
        (SELECT COUNT(*) FILTER (WHERE concept_code IS NOT NULL)::float8
                / NULLIF(COUNT(*), 0)
         FROM public.eligibility),
        0)                                                                       AS resolution_rate,
    (SELECT COALESCE(SUM(llm_prompt_tokens), 0)
        FROM public.eligibility_study)                                           AS prompt_tokens,
    (SELECT COALESCE(SUM(llm_completion_tokens), 0)
        FROM public.eligibility_study)                                           AS completion_tokens,
    (SELECT COUNT(*) FROM latest WHERE status = 'llm_failed')                    AS failed_llm,
    (SELECT COUNT(*) FROM latest WHERE status = 'parse_invalid_json')            AS failed_parse_invalid_json,
    (SELECT COUNT(*) FROM latest WHERE status = 'persist_failed')                AS failed_persist,
    -- Generic / unexpected failures: every latest-attempt status that counts as
    -- a failure but is not one of the three named buckets above. Keeps the
    -- four breakdown counts summing to studies_failed_latest even if a new
    -- failure status is introduced upstream without this query knowing about it.
    (SELECT COUNT(*) FROM latest
        WHERE status NOT IN ('success', 'running', 'parse_empty', 'cancelled',
                             'llm_failed', 'parse_invalid_json', 'persist_failed',
                             'interrupted')) AS failed_other,
    -- parse_empty: latest attempt returned valid JSON but zero records. A valid
    -- terminal state, not a failure — surfaced separately so the dashboard can
    -- show it without inflating studies_failed_latest.
    (SELECT COUNT(*) FROM latest WHERE status = 'parse_empty')                   AS parse_empty,
    -- Embedding backlog: studies that the eligibility pipeline produced rows
    -- for but that `embed-studies` (CLI) has not yet covered. Mirrors the
    -- anti-join in GetStudiesToEmbedAsync (without the model filter — the
    -- embedding table has one row per nct_id, so any embedding counts).
    (SELECT COUNT(*) FROM public.eligibility_study_detail d
        WHERE EXISTS (SELECT 1 FROM public.eligibility e WHERE e.nct_id = d.nct_id)
          AND NOT EXISTS (SELECT 1 FROM public.eligibility_study_embedding em
                          WHERE em.nct_id = d.nct_id))                            AS studies_without_embeddings,
    -- Distinct trials attempted (any status). `latest` is already one row per
    -- nct_id, so this is a free count over a CTE the query has built anyway - no
    -- extra scan. Feeds the dashboard's approximate remaining-trials figure.
    (SELECT COUNT(*) FROM latest)                                                 AS studies_attempted,
    -- Trials whose host died mid-flight, reconciled from 'running' at web-host
    -- startup (see ReconcileInterruptedStudiesAsync). Its OWN bucket rather than
    -- being left to fall into failed_other, because the reader folds failed_other
    -- under the generic failed key, and the dashboard links that line to
    -- History?status=failed, which matches on the literal status - a merged count
    -- would render as a failure and drill through to an empty list.
    --
    -- MUST STAY LAST: the reader below is positional (reader.GetInt64(0..13)), so
    -- inserting a column above this silently shifts every metric.
    (SELECT COUNT(*) FROM latest WHERE status = 'interrupted')                    AS failed_interrupted"

        ' The source count lives in the OTHER database (ctgov.*), so it cannot join the
        ' query below and needs its own round-trip. Read first so the metrics object is
        ' constructed once, complete.
        '
        ' UNFILTERED on purpose: the filtered equivalent costs ~26s against Duke's
        ' hosted AACT versus ~224ms here, and this runs on every dashboard cache miss.
        ' See CountSourceTrialsAsync for the full reasoning and the 0.29% it costs.
        Dim sourceTotal = Await CountSourceTrialsAsync(cancellationToken).ConfigureAwait(False)

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    If Not Await reader.ReadAsync(cancellationToken).ConfigureAwait(False) Then
                        Return DashboardMetrics.Empty
                    End If
                    ' Ordinal 9 is failed_other, surfaced under the "failed" key - the
                    ' generic bucket. Ordinal 13 is the interrupted count, kept separate
                    ' so its dashboard line links to a History filter that matches.
                    Dim failuresByStatus As New Dictionary(Of String, Long) From {
                            {"llm_failed", reader.GetInt64(6)},
                            {"parse_invalid_json", reader.GetInt64(7)},
                            {"persist_failed", reader.GetInt64(8)},
                            {"failed", reader.GetInt64(9)},
                            {"interrupted", reader.GetInt64(13)}}
                    Return New DashboardMetrics(
                            eligibilityRowCount:=reader.GetInt64(0),
                            studiesSuccessful:=reader.GetInt64(1),
                            studiesFailedLatest:=reader.GetInt64(2),
                            resolutionRate:=reader.GetDouble(3),
                            promptTokens:=reader.GetInt64(4),
                            completionTokens:=reader.GetInt64(5),
                            failuresByStatus:=failuresByStatus,
                            parseEmpty:=reader.GetInt64(10),
                            studiesWithoutEmbeddings:=reader.GetInt64(11),
                            studiesAttempted:=reader.GetInt64(12),
                            sourceTrialTotal:=sourceTotal)
                End Using
            End Using
        End Using
    End Function

    ''' <summary>
    ''' TOTAL rows in ctgov.eligibilities - NO selection filter. Returns Nothing when
    ''' there is no reachable AACT source, so the dashboard omits the backlog figure
    ''' rather than showing a wrong one.
    ''' <para>
    ''' WHY UNFILTERED, i.e. why the dashboard's backlog is deliberately a bit wrong:
    ''' the filtered count (CountSelectableSourceTrialsAsync) costs ~26 SECONDS against
    ''' the real AACT and this one costs ~224 ms - a 116x difference. The filter forces
    ''' a full heap read of the wide `criteria` column for all ~593k rows (three ILIKEs
    ''' plus LENGTH(TRIM(...))), and the partial index that makes it cheap
    ''' (SelectableSourceIndexName) only ever exists on a CO-LOCATED source. Against
    ''' Duke's hosted AACT there is no such index and the account cannot create one.
    ''' </para>
    ''' <para>
    ''' The cost of being unfiltered: the filter only removes ~1,695 of ~593,328 rows
    ''' (0.29%), so the dashboard's "trials remaining" overstates by roughly that and
    ''' bottoms out near ~1,700 rather than 0. That is why the card is labelled approx
    ''' and why the Tools tab carries an exact, on-demand figure for when the backlog
    ''' actually matters.
    ''' </para>
    ''' </summary>
    Friend Async Function CountSourceTrialsAsync(
            cancellationToken As CancellationToken) As Task(Of Long?)

        ' No WHERE at all: this is what makes it an index-only count rather than a
        ' 26-second heap scan. See the remarks above before adding a predicate here.
        Const sql As String = "SELECT COUNT(*) FROM ctgov.eligibilities"
        Return Await CountSourceAsync(sql, cancellationToken).ConfigureAwait(False)
    End Function

    ''' <summary>
    ''' Count of AACT trials that pass the selection filter (spec section 2.3) - the
    ''' figure the pipeline would actually consider. Returns Nothing when there is no
    ''' reachable AACT source.
    ''' <para>
    ''' EXPENSIVE AND ON-DEMAND ONLY: ~26 seconds against Duke's hosted AACT, because
    ''' the three ILIKEs force a full read of the wide `criteria` column across ~593k
    ''' rows over the internet, and the partial index that would make it cheap only
    ''' exists when the source is co-located with the output database. Never call this
    ''' on a page load - the dashboard uses CountSourceTrialsAsync (~224 ms) and accepts
    ''' a 0.29% overstatement. This one backs the Tools tab's explicit "exact" button.
    ''' </para>
    ''' <para>
    ''' Still not a true remaining count: there is deliberately no anti-join against the
    ''' attempted set, because that means COPYing ~280k ids to the source. Callers
    ''' subtract the attempted total instead, which leaves a small drift for trials that
    ''' were attempted but are no longer selectable.
    ''' </para>
    ''' <para>
    ''' The WHERE clause MUST stay aligned with SelectNextTrialsAsync's filter, or the
    ''' number stops describing what a batch would pick up.
    ''' </para>
    ''' </summary>
    Public Async Function CountSelectableSourceTrialsAsync(
            cancellationToken As CancellationToken) As Task(Of Long?) _
            Implements IPostgresGateway.CountSelectableSourceTrialsAsync

        Const sql As String = "
SELECT COUNT(*)
FROM ctgov.eligibilities src
WHERE src.criteria IS NOT NULL
  AND LENGTH(TRIM(src.criteria)) >= 50
  AND src.criteria NOT ILIKE '%please contact%'
  AND src.criteria NOT ILIKE '%contact site for%'
  AND src.criteria NOT ILIKE '%contact study%'"
        Return Await CountSourceAsync(sql, cancellationToken).ConfigureAwait(False)
    End Function

    ' Shared plumbing for both source counts: the absent-source probe, the round-trip,
    ' and the non-fatal contract. Keeps the two SQL texts as the only difference between
    ' the cheap dashboard count and the expensive exact one.
    Private Async Function CountSourceAsync(
            sql As String,
            cancellationToken As CancellationToken) As Task(Of Long?)

        If _sourceDataSource Is Nothing Then Return Nothing

        Try
            ' Probe first: to_regclass returns NULL rather than raising when the
            ' schema/table is absent, so the no-AACT case (seeded quickstart) costs
            ' one cheap query and no exception.
            If Not Await SourceHasCtgovEligibilitiesAsync(cancellationToken).ConfigureAwait(False) Then
                Return Nothing
            End If

            Using conn = Await _sourceDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = sql
                    Dim scalar = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                    If scalar Is Nothing OrElse scalar Is DBNull.Value Then Return Nothing
                    Return Convert.ToInt64(scalar)
                End Using
            End Using
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch ex As Exception
            ' Non-fatal: the backlog figure is a nice-to-have on a page that must
            ' render even when the source DB is down. Drop it rather than 500 the
            ' whole dashboard.
            _logger.LogDebug(ex, "Source-trial count failed; omitting the remaining-trials figure")
            Return Nothing
        End Try
    End Function

    ' ============ SearchEligibilityAsync (output DB, dashboard Results browser) ============

    Public Async Function SearchEligibilityAsync(
            filter As EligibilityFilter,
            sortBy As String,
            page As Integer,
            pageSize As Integer,
            cancellationToken As CancellationToken) As Task(Of EligibilityResultPage) _
            Implements IPostgresGateway.SearchEligibilityAsync

        If filter Is Nothing Then Throw New ArgumentNullException(NameOf(filter))
        ' Clamp page size (UI default = 20) and page number to sane bounds.
        Dim cappedPageSize = Math.Min(Math.Max(pageSize, 1), 200)
        Dim cappedPage = Math.Max(page, 1)
        Dim offset = (cappedPage - 1) * cappedPageSize

        ' ORDER BY fragment comes from a hardcoded whitelist — sortBy can never
        ' interpolate user input into SQL. Filter values are parameterised below.
        Dim orderBy = ResolveOrderBy(sortBy)

        ' The page rows and the pre-LIMIT total are fetched as two statements.
        ' The old single query used COUNT(*) OVER(), which forces the window to
        ' buffer every matching row — all columns, including the large
        ' original_text — just to attach a scalar count. Once public.eligibility
        ' grew past 300k rows that spilled ~70MB to disk and pushed this query
        ' past 4s; a bare COUNT(*) scans far less and runs in tens of ms.
        Const whereClause As String = "
WHERE (@nct_id        IS NULL OR nct_id = @nct_id)
  AND (@criterion     IS NULL OR criterion ILIKE '%' || @criterion || '%')
  AND (@domain        IS NULL OR domain = @domain)
  AND (@concept       IS NULL OR concept ILIKE '%' || @concept || '%')
  AND (@concept_code  IS NULL OR concept_code = @concept_code)
  AND (@semantic_type_tuis IS NULL OR semantic_type_tuis && @semantic_type_tuis)"

        Dim pageSql = $"
SELECT id, nct_id, criterion, domain, concept, concept_code, semantic_type,
       qualifier, time_window, original_text, umls_name, match_score, match_source, created_at
FROM public.eligibility{whereClause}
{orderBy}
OFFSET @offset LIMIT @limit"

        Dim countSql = $"SELECT COUNT(*) FROM public.eligibility{whereClause}"

        Dim rows As New List(Of EligibilityRow)
        Dim totalRows As Long = 0
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = pageSql
                AddTextParam(cmd, "nct_id", filter.NctId)
                AddTextParam(cmd, "criterion", filter.Criterion)
                AddTextParam(cmd, "domain", filter.Domain)
                AddTextParam(cmd, "concept", filter.Concept)
                AddTextParam(cmd, "concept_code", filter.ConceptCode)
                AddSemanticTypeTuisParam(cmd, filter)
                cmd.Parameters.Add(New NpgsqlParameter("offset", NpgsqlDbType.Integer) With {.Value = offset})
                cmd.Parameters.Add(New NpgsqlParameter("limit", NpgsqlDbType.Integer) With {.Value = cappedPageSize})

                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        rows.Add(New EligibilityRow(
                                id:=reader.GetInt64(0),
                                nctId:=reader.GetString(1),
                                criterion:=reader.GetString(2),
                                domain:=reader.GetString(3),
                                concept:=reader.GetString(4),
                                conceptCode:=If(reader.IsDBNull(5), "", reader.GetString(5)),
                                semanticType:=If(reader.IsDBNull(6), "", reader.GetString(6)),
                                qualifier:=If(reader.IsDBNull(7), "", reader.GetString(7)),
                                timeWindow:=If(reader.IsDBNull(8), "", reader.GetString(8)),
                                originalText:=If(reader.IsDBNull(9), "", reader.GetString(9)),
                                umlsName:=If(reader.IsDBNull(10), "", reader.GetString(10)),
                                matchScore:=CDbl(reader.GetDecimal(11)),
                                matchSource:=If(reader.IsDBNull(12), "", reader.GetString(12)),
                                createdAt:=reader.GetFieldValue(Of DateTimeOffset)(13)))
                    End While
                End Using
            End Using

            Using countCmd = conn.CreateCommand()
                countCmd.CommandText = countSql
                AddTextParam(countCmd, "nct_id", filter.NctId)
                AddTextParam(countCmd, "criterion", filter.Criterion)
                AddTextParam(countCmd, "domain", filter.Domain)
                AddTextParam(countCmd, "concept", filter.Concept)
                AddTextParam(countCmd, "concept_code", filter.ConceptCode)
                AddSemanticTypeTuisParam(countCmd, filter)
                totalRows = Convert.ToInt64(Await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False))
            End Using
        End Using

        Return New EligibilityResultPage(rows, totalRows, cappedPage, cappedPageSize)
    End Function

    ' ============ GetEligibilityFilterOptionsAsync (output DB) ============

    Public Async Function GetEligibilityFilterOptionsAsync(
            maxDropdownSize As Integer,
            cancellationToken As CancellationToken) As Task(Of EligibilityFilterOptions) _
            Implements IPostgresGateway.GetEligibilityFilterOptionsAsync

        ' Clamp to a sane upper bound. The threshold is the page's tolerance for
        ' how long a <select> can be before it becomes user-hostile.
        Dim cap = Math.Min(Math.Max(maxDropdownSize, 1), 500)

        ' One query per column, each LIMIT cap+1. If a column returns > cap rows,
        ' it's "too big for a dropdown" and we hand back an empty list — the
        ' view renders a text input instead.
        ' semantic_type is NOT here any more: it comes from umls.semantic_type_dim
        ' (see below), which removed the only other column that still needed a
        ' real scan after the estimate pre-filter.
        Dim columns = New String() {
            "nct_id", "criterion", "domain", "concept", "concept_code"}

        ' Cheap pre-filter. SELECT DISTINCT over public.eligibility costs a full
        ' scan of ~3.9M rows per column (measured: `concept` alone is ~780 ms),
        ' and for a high-cardinality column that entire cost is wasted - the
        ' result blows the cap and LoadDistinctAsync throws it away. The planner
        ' already keeps a distinct-count estimate per column, so ask it first and
        ' skip the scan for columns it says are provably over the cap. Measured
        ' on the production corpus this dropped the then-six-column total from
        ' ~1150 ms to ~240 ms; only `criterion` still gets scanned now that
        ' semantic_type is served from the dimension.
        '
        ' Correctness rests on only ever skipping when the estimate clears the
        ' cap by a wide margin - see ShouldSkipDistinctScan.
        Dim estimates = Await LoadEstimatedDistinctCountsAsync(columns, cancellationToken).ConfigureAwait(False)

        Dim results As New Dictionary(Of String, IReadOnlyList(Of String))(StringComparer.Ordinal)
        For Each col In columns
            Dim estimate As Double
            If estimates.TryGetValue(col, estimate) AndAlso ShouldSkipDistinctScan(estimate, cap) Then
                ' Provably over the dropdown cap: same answer LoadDistinctAsync
                ' would have produced, without the scan.
                results(col) = Array.Empty(Of String)()
                Continue For
            End If
            results(col) = Await LoadDistinctAsync(col, cap, cancellationToken).ConfigureAwait(False)
        Next

        Return New EligibilityFilterOptions(
                nctIds:=results("nct_id"),
                criteria:=results("criterion"),
                domains:=results("domain"),
                concepts:=results("concept"),
                conceptCodes:=results("concept_code"),
                semanticTypes:=Await LoadSemanticTypeOptionsAsync(cancellationToken).ConfigureAwait(False))
    End Function

    ''' <summary>
    ''' Selectable semantic types, from the ~132-row umls.semantic_type_dim.
    ''' </summary>
    ''' <remarks>
    ''' Not a DISTINCT over public.eligibility. That scan covered ~3.9M rows and
    ''' was one of only two the estimate pre-filter could not skip - the values
    ''' it produced were joined *combinations* (215 of them on the production
    ''' corpus) rather than the 132 real semantic types, so it was both slower
    ''' and wrong. The maxDropdownSize cap does not apply: the dimension is
    ''' bounded by UMLS.
    ''' </remarks>
    Private Async Function LoadSemanticTypeOptionsAsync(
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of SemanticTypeOption))

        Dim result As New List(Of SemanticTypeOption)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                ' Ordered by display name so the dropdown reads alphabetically.
                cmd.CommandText = "SELECT tui, sty FROM umls.semantic_type_dim ORDER BY sty"
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(New SemanticTypeOption(reader.GetString(0), reader.GetString(1)))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    ' How far past the cap a planner estimate must sit before we trust it enough
    ' to skip the real scan. pg_stats.n_distinct is derived from a sample, so it
    ' is wrong in both directions: under-estimating is harmless (we scan, then
    ' apply the real count), but over-estimating would blank a dropdown that
    ' should have rendered. A 2x margin keeps the skip well away from close
    ' calls - on the production corpus the skipped columns overshoot the cap by
    ' 3x to 700x, so the margin costs nothing real.
    Friend Const DistinctSkipMarginFactor As Integer = 2

    ' Pure decision rule for the pg_stats pre-filter, split out so it can be
    ' unit-tested without a database. Returns True only when the estimate proves
    ' the column cannot fit in a dropdown.
    Friend Shared Function ShouldSkipDistinctScan(estimatedDistinct As Double, cap As Integer) As Boolean
        ' 0 encodes "no statistics" (never analyzed, or ANALYZE could not form an
        ' estimate). An absent estimate is not evidence - scan.
        If estimatedDistinct <= 0 Then Return False
        Return estimatedDistinct > CDbl(cap) * DistinctSkipMarginFactor
    End Function

    ' One round-trip for all six columns' planner distinct-count estimates.
    '
    ' pg_stats.n_distinct encoding: positive = an absolute count estimate;
    ' negative = a multiplier of the row count (-1 means "unique per row");
    ' 0 = unknown. A negative value has to be scaled by reltuples, which is
    ' itself -1 on a table that has never been analyzed - so both unknown cases
    ' resolve to 0 and the caller scans.
    ' Friend rather than Private so the integration suite can assert that the
    ' estimates actually track ANALYZE, which is the assumption the whole
    ' pre-filter rests on.
    Friend Async Function LoadEstimatedDistinctCountsAsync(
            columns As IReadOnlyList(Of String),
            cancellationToken As CancellationToken) As Task(Of Dictionary(Of String, Double))

        Const sql As String = "
SELECT s.attname,
       (CASE
           WHEN s.n_distinct > 0 THEN s.n_distinct
           WHEN s.n_distinct < 0 AND c.reltuples > 0 THEN (-s.n_distinct) * c.reltuples
           ELSE 0
       END)::double precision AS est_distinct
FROM pg_stats s
JOIN pg_class c ON c.relname = s.tablename
JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = s.schemaname
WHERE s.schemaname = 'public'
  AND s.tablename = 'eligibility'
  AND s.attname = ANY(@cols)"

        Dim map As New Dictionary(Of String, Double)(StringComparer.Ordinal)
        Try
            Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = sql
                    cmd.Parameters.Add(New NpgsqlParameter("cols", NpgsqlDbType.Array Or NpgsqlDbType.Text) With {
                            .Value = columns.ToArray()})
                    Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                        While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                            map(reader.GetString(0)) = reader.GetDouble(1)
                        End While
                    End Using
                End Using
            End Using
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Throw
        Catch ex As Exception
            ' Statistics are an optimization, never a correctness input. A failed
            ' probe means "no estimates", which degrades to scanning every column
            ' exactly as before this pre-filter existed.
            _logger.LogDebug(ex,
                    "pg_stats distinct-estimate probe failed; scanning all filter columns")
            map.Clear()
        End Try
        Return map
    End Function

    ' Column name is hardcoded from a fixed whitelist above — never user input —
    ' so string interpolation here is safe. Filter values are still parameterised
    ' via the IS NULL OR ... composite predicates in SearchEligibilityAsync.
    Private Async Function LoadDistinctAsync(
            column As String,
            cap As Integer,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String))
        Dim sql = $"
SELECT DISTINCT {column} AS value
FROM public.eligibility
WHERE {column} IS NOT NULL AND {column} <> ''
ORDER BY value
LIMIT @cap"
        Dim values As New List(Of String)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = sql
                cmd.Parameters.Add(New NpgsqlParameter("cap", NpgsqlDbType.Integer) With {.Value = cap + 1})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        values.Add(reader.GetString(0))
                    End While
                End Using
            End Using
        End Using
        ' Over-cap means the column's cardinality exceeds the dropdown threshold;
        ' return empty so the view renders a text input instead.
        If values.Count > cap Then Return Array.Empty(Of String)()
        Return values
    End Function

    Private Shared Sub AddTextParam(cmd As NpgsqlCommand, name As String, value As String)
        cmd.Parameters.Add(New NpgsqlParameter(name, NpgsqlDbType.Text) With {
                .Value = If(String.IsNullOrEmpty(value), CObj(DBNull.Value), value)})
    End Sub

    ''' <summary>
    ''' Binds the semantic-type filter as a text array for the `&&` (overlap)
    ''' predicate. DBNull when the list is empty, so the `@semantic_type_tuis IS
    ''' NULL` arm short-circuits and the filter is skipped entirely - an empty
    ''' selection means "no filter", not "match nothing".
    ''' </summary>
    Private Shared Sub AddSemanticTypeTuisParam(cmd As NpgsqlCommand, filter As EligibilityFilter)
        cmd.Parameters.Add(New NpgsqlParameter("semantic_type_tuis", NpgsqlDbType.Array Or NpgsqlDbType.Text) With {
                .Value = If(filter.SemanticTypeTuis.Count = 0,
                            CObj(DBNull.Value),
                            CObj(filter.SemanticTypeTuis.ToArray()))})
    End Sub

    ' ============ GetStudyDetailsAsync (source DB, Analysis tab ID card) ============

    Public Async Function GetStudyDetailsAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of StudyDetails) _
            Implements IPostgresGateway.GetStudyDetailsAsync

        If String.IsNullOrWhiteSpace(nctId) Then Return Nothing
        If _sourceDataSource Is Nothing Then
            Throw New InvalidOperationException(
                    "Source data source is not configured; cannot read trial details from ctgov.")
        End If

        ' One round trip for the studies row + brief summary (1:1 via LEFT JOIN),
        ' then two cheap follow-ups for conditions and interventions (indexed
        ' by nct_id in AACT).
        Const StudySql As String = "
SELECT s.brief_title, s.official_title, s.overall_status, s.phase, s.study_type,
       s.start_date, s.completion_date, s.primary_completion_date,
       s.enrollment, s.enrollment_type, s.source, s.why_stopped,
       bs.description AS brief_summary
FROM ctgov.studies s
LEFT JOIN ctgov.brief_summaries bs ON bs.nct_id = s.nct_id
WHERE s.nct_id = @nct_id"

        Dim briefTitle As String = ""
        Dim officialTitle As String = ""
        Dim overallStatus As String = ""
        Dim phase As String = ""
        Dim studyType As String = ""
        Dim startDate As Date? = Nothing
        Dim completionDate As Date? = Nothing
        Dim primaryCompletionDate As Date? = Nothing
        Dim enrollment As Integer? = Nothing
        Dim enrollmentType As String = ""
        Dim source As String = ""
        Dim whyStopped As String = ""
        Dim briefSummary As String = ""
        Dim found As Boolean = False

        Using conn = Await _sourceDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = StudySql
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    If Await reader.ReadAsync(cancellationToken).ConfigureAwait(False) Then
                        found = True
                        briefTitle = ReadStringOrEmpty(reader, 0)
                        officialTitle = ReadStringOrEmpty(reader, 1)
                        overallStatus = ReadStringOrEmpty(reader, 2)
                        phase = ReadStringOrEmpty(reader, 3)
                        studyType = ReadStringOrEmpty(reader, 4)
                        startDate = ReadNullableDate(reader, 5)
                        completionDate = ReadNullableDate(reader, 6)
                        primaryCompletionDate = ReadNullableDate(reader, 7)
                        enrollment = ReadNullableInt32(reader, 8)
                        enrollmentType = ReadStringOrEmpty(reader, 9)
                        source = ReadStringOrEmpty(reader, 10)
                        whyStopped = ReadStringOrEmpty(reader, 11)
                        briefSummary = ReadStringOrEmpty(reader, 12)
                    End If
                End Using
            End Using
        End Using

        If Not found Then Return Nothing

        Dim conditions = Await LoadConditionsAsync(nctId, cancellationToken).ConfigureAwait(False)
        Dim interventions = Await LoadInterventionsAsync(nctId, cancellationToken).ConfigureAwait(False)

        Return New StudyDetails(
                nctId:=nctId,
                briefTitle:=briefTitle,
                officialTitle:=officialTitle,
                overallStatus:=overallStatus,
                phase:=phase,
                studyType:=studyType,
                startDate:=startDate,
                completionDate:=completionDate,
                primaryCompletionDate:=primaryCompletionDate,
                enrollment:=enrollment,
                enrollmentType:=enrollmentType,
                source:=source,
                whyStopped:=whyStopped,
                briefSummary:=briefSummary,
                conditions:=conditions,
                interventions:=interventions)
    End Function

    Private Async Function LoadConditionsAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of String))
        Dim names As New List(Of String)
        Using conn = Await _sourceDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT name FROM ctgov.conditions WHERE nct_id = @nct_id ORDER BY name"
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        If Not reader.IsDBNull(0) Then names.Add(NormalizeAactText(reader.GetString(0)))
                    End While
                End Using
            End Using
        End Using
        Return names
    End Function

    Private Async Function LoadInterventionsAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of Intervention))
        Dim items As New List(Of Intervention)
        Using conn = Await _sourceDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT intervention_type, name
FROM ctgov.interventions
WHERE nct_id = @nct_id
ORDER BY intervention_type, name"
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        items.Add(New Intervention(
                                interventionType:=ReadStringOrEmpty(reader, 0),
                                name:=ReadStringOrEmpty(reader, 1)))
                    End While
                End Using
            End Using
        End Using
        Return items
    End Function

    ' ============ GetSourceEligibilityAsync (source DB, Analysis tab) ============

    Public Async Function GetSourceEligibilityAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of SourceEligibilityDetails) _
            Implements IPostgresGateway.GetSourceEligibilityAsync

        If String.IsNullOrWhiteSpace(nctId) Then Return Nothing
        If _sourceDataSource Is Nothing Then
            Throw New InvalidOperationException(
                    "Source data source is not configured; cannot read eligibility from ctgov.")
        End If

        ' All structured columns AACT publishes on ctgov.eligibilities. Coalesce
        ' the boolean flags with IsDBNull rather than NULLIF — AACT will report
        ' NULL when the trial didn't specify the field, which we preserve as
        ' Nullable(Of Boolean) so the view can render "—".
        Const Sql As String = "
SELECT criteria, gender, minimum_age, maximum_age, healthy_volunteers,
       sampling_method, population, adult, child, older_adult
FROM ctgov.eligibilities
WHERE nct_id = @nct_id"

        Using conn = Await _sourceDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    If Not Await reader.ReadAsync(cancellationToken).ConfigureAwait(False) Then
                        Return Nothing
                    End If
                    Return New SourceEligibilityDetails(
                            nctId:=nctId,
                            criteria:=ReadStringOrEmpty(reader, 0),
                            gender:=ReadStringOrEmpty(reader, 1),
                            minimumAge:=ReadStringOrEmpty(reader, 2),
                            maximumAge:=ReadStringOrEmpty(reader, 3),
                            healthyVolunteers:=ReadStringOrEmpty(reader, 4),
                            samplingMethod:=ReadStringOrEmpty(reader, 5),
                            population:=ReadStringOrEmpty(reader, 6),
                            adult:=ReadNullableBoolean(reader, 7),
                            child:=ReadNullableBoolean(reader, 8),
                            olderAdult:=ReadNullableBoolean(reader, 9))
                End Using
            End Using
        End Using
    End Function

    ' ============ Study snapshot (source → output DB, public.eligibility_study_detail) ============

    Public Async Function CaptureStudySnapshotAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.CaptureStudySnapshotAsync

        If String.IsNullOrWhiteSpace(nctId) Then Return

        ' Reuse the live AACT projections, then persist them to the output DB.
        ' Both throw if the source data source is unconfigured — callers
        ' (orchestrator best-effort wrapper, CLI command) handle that.
        Dim d = Await GetStudyDetailsAsync(nctId, cancellationToken).ConfigureAwait(False)
        Dim e = Await GetSourceEligibilityAsync(nctId, cancellationToken).ConfigureAwait(False)

        ' Nothing in AACT for this trial — no snapshot to write.
        If d Is Nothing AndAlso e Is Nothing Then Return

        Dim conditions As String() = If(d IsNot Nothing,
                d.Conditions.ToArray(), Array.Empty(Of String)())
        Dim interventionsJson As String = SerializeInterventions(
                If(d IsNot Nothing, d.Interventions, Nothing))

        Const Sql As String = "
INSERT INTO public.eligibility_study_detail (
    nct_id, brief_title, official_title, overall_status, phase, study_type,
    start_date, completion_date, primary_completion_date, enrollment,
    enrollment_type, source, why_stopped, brief_summary, conditions,
    interventions, criteria, gender, minimum_age, maximum_age,
    healthy_volunteers, sampling_method, population, adult, child, older_adult
) VALUES (
    @nct_id, @brief_title, @official_title, @overall_status, @phase, @study_type,
    @start_date, @completion_date, @primary_completion_date, @enrollment,
    @enrollment_type, @source, @why_stopped, @brief_summary, @conditions,
    @interventions, @criteria, @gender, @minimum_age, @maximum_age,
    @healthy_volunteers, @sampling_method, @population, @adult, @child, @older_adult
)
ON CONFLICT (nct_id) DO UPDATE SET
    captured_at             = now(),
    brief_title             = excluded.brief_title,
    official_title          = excluded.official_title,
    overall_status          = excluded.overall_status,
    phase                   = excluded.phase,
    study_type              = excluded.study_type,
    start_date              = excluded.start_date,
    completion_date         = excluded.completion_date,
    primary_completion_date = excluded.primary_completion_date,
    enrollment              = excluded.enrollment,
    enrollment_type         = excluded.enrollment_type,
    source                  = excluded.source,
    why_stopped             = excluded.why_stopped,
    brief_summary           = excluded.brief_summary,
    conditions              = excluded.conditions,
    interventions           = excluded.interventions,
    criteria                = excluded.criteria,
    gender                  = excluded.gender,
    minimum_age             = excluded.minimum_age,
    maximum_age             = excluded.maximum_age,
    healthy_volunteers      = excluded.healthy_volunteers,
    sampling_method         = excluded.sampling_method,
    population              = excluded.population,
    adult                   = excluded.adult,
    child                   = excluded.child,
    older_adult             = excluded.older_adult"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                AddTextParam(cmd, "brief_title", If(d IsNot Nothing, d.BriefTitle, ""))
                AddTextParam(cmd, "official_title", If(d IsNot Nothing, d.OfficialTitle, ""))
                AddTextParam(cmd, "overall_status", If(d IsNot Nothing, d.OverallStatus, ""))
                AddTextParam(cmd, "phase", If(d IsNot Nothing, d.Phase, ""))
                AddTextParam(cmd, "study_type", If(d IsNot Nothing, d.StudyType, ""))
                AddNullableDateParam(cmd, "start_date", If(d IsNot Nothing, d.StartDate, Nothing))
                AddNullableDateParam(cmd, "completion_date", If(d IsNot Nothing, d.CompletionDate, Nothing))
                AddNullableDateParam(cmd, "primary_completion_date", If(d IsNot Nothing, d.PrimaryCompletionDate, Nothing))
                AddNullableIntParam(cmd, "enrollment", If(d IsNot Nothing, d.Enrollment, Nothing))
                AddTextParam(cmd, "enrollment_type", If(d IsNot Nothing, d.EnrollmentType, ""))
                AddTextParam(cmd, "source", If(d IsNot Nothing, d.Source, ""))
                AddTextParam(cmd, "why_stopped", If(d IsNot Nothing, d.WhyStopped, ""))
                AddTextParam(cmd, "brief_summary", If(d IsNot Nothing, d.BriefSummary, ""))
                cmd.Parameters.Add(New NpgsqlParameter("conditions", NpgsqlDbType.Array Or NpgsqlDbType.Text) With {.Value = conditions})
                cmd.Parameters.Add(New NpgsqlParameter("interventions", NpgsqlDbType.Jsonb) With {.Value = interventionsJson})
                AddTextParam(cmd, "criteria", If(e IsNot Nothing, e.Criteria, ""))
                AddTextParam(cmd, "gender", If(e IsNot Nothing, e.Gender, ""))
                AddTextParam(cmd, "minimum_age", If(e IsNot Nothing, e.MinimumAge, ""))
                AddTextParam(cmd, "maximum_age", If(e IsNot Nothing, e.MaximumAge, ""))
                AddTextParam(cmd, "healthy_volunteers", If(e IsNot Nothing, e.HealthyVolunteers, ""))
                AddTextParam(cmd, "sampling_method", If(e IsNot Nothing, e.SamplingMethod, ""))
                AddTextParam(cmd, "population", If(e IsNot Nothing, e.Population, ""))
                AddNullableBoolParam(cmd, "adult", If(e IsNot Nothing, e.Adult, Nothing))
                AddNullableBoolParam(cmd, "child", If(e IsNot Nothing, e.Child, Nothing))
                AddNullableBoolParam(cmd, "older_adult", If(e IsNot Nothing, e.OlderAdult, Nothing))
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    Public Async Function GetStudySnapshotAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of StudySnapshot) _
            Implements IPostgresGateway.GetStudySnapshotAsync

        If String.IsNullOrWhiteSpace(nctId) Then Return Nothing

        Const Sql As String = "
SELECT captured_at, brief_title, official_title, overall_status, phase, study_type,
       start_date, completion_date, primary_completion_date, enrollment,
       enrollment_type, source, why_stopped, brief_summary, conditions,
       interventions, criteria, gender, minimum_age, maximum_age,
       healthy_volunteers, sampling_method, population, adult, child, older_adult
FROM public.eligibility_study_detail
WHERE nct_id = @nct_id"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    If Not Await reader.ReadAsync(cancellationToken).ConfigureAwait(False) Then
                        Return Nothing
                    End If

                    Dim capturedAt = reader.GetFieldValue(Of DateTimeOffset)(0)
                    Dim conditions As IReadOnlyList(Of String) =
                            If(reader.IsDBNull(14),
                               CType(Array.Empty(Of String)(), IReadOnlyList(Of String)),
                               reader.GetFieldValue(Of String())(14))
                    Dim interventions = DeserializeInterventions(
                            If(reader.IsDBNull(15), "[]", reader.GetString(15)))

                    Dim details = New StudyDetails(
                            nctId:=nctId,
                            briefTitle:=ReadStringOrEmpty(reader, 1),
                            officialTitle:=ReadStringOrEmpty(reader, 2),
                            overallStatus:=ReadStringOrEmpty(reader, 3),
                            phase:=ReadStringOrEmpty(reader, 4),
                            studyType:=ReadStringOrEmpty(reader, 5),
                            startDate:=ReadNullableDate(reader, 6),
                            completionDate:=ReadNullableDate(reader, 7),
                            primaryCompletionDate:=ReadNullableDate(reader, 8),
                            enrollment:=ReadNullableInt32(reader, 9),
                            enrollmentType:=ReadStringOrEmpty(reader, 10),
                            source:=ReadStringOrEmpty(reader, 11),
                            whyStopped:=ReadStringOrEmpty(reader, 12),
                            briefSummary:=ReadStringOrEmpty(reader, 13),
                            conditions:=conditions,
                            interventions:=interventions)

                    Dim eligibility = New SourceEligibilityDetails(
                            nctId:=nctId,
                            criteria:=ReadStringOrEmpty(reader, 16),
                            gender:=ReadStringOrEmpty(reader, 17),
                            minimumAge:=ReadStringOrEmpty(reader, 18),
                            maximumAge:=ReadStringOrEmpty(reader, 19),
                            healthyVolunteers:=ReadStringOrEmpty(reader, 20),
                            samplingMethod:=ReadStringOrEmpty(reader, 21),
                            population:=ReadStringOrEmpty(reader, 22),
                            adult:=ReadNullableBoolean(reader, 23),
                            child:=ReadNullableBoolean(reader, 24),
                            olderAdult:=ReadNullableBoolean(reader, 25))

                    Return New StudySnapshot(details, eligibility, capturedAt)
                End Using
            End Using
        End Using
    End Function

    ' Interventions are stored as a jsonb array of {"type","name"} objects.
    ' Serialize/deserialize through the same camelCase options so the column
    ' is human-readable if queried directly.
    Private Shared ReadOnly InterventionJsonOptions As New JsonSerializerOptions With {
            .PropertyNamingPolicy = JsonNamingPolicy.CamelCase}

    Private Shared Function SerializeInterventions(items As IReadOnlyList(Of Intervention)) As String
        If items Is Nothing OrElse items.Count = 0 Then Return "[]"
        Dim dto = items.Select(Function(i) New InterventionJson With {
                .Type = i.InterventionType, .Name = i.Name}).ToList()
        Return JsonSerializer.Serialize(dto, InterventionJsonOptions)
    End Function

    Private Shared Function DeserializeInterventions(json As String) As IReadOnlyList(Of Intervention)
        If String.IsNullOrWhiteSpace(json) Then Return Array.Empty(Of Intervention)()
        Dim dto = JsonSerializer.Deserialize(Of List(Of InterventionJson))(json, InterventionJsonOptions)
        If dto Is Nothing Then Return Array.Empty(Of Intervention)()
        Return dto.Select(Function(i) New Intervention(i.Type, i.Name)).ToList()
    End Function

    Private NotInheritable Class InterventionJson
        Public Property [Type] As String
        Public Property Name As String
    End Class

    Private Shared Sub AddNullableDateParam(cmd As NpgsqlCommand, name As String, value As Date?)
        cmd.Parameters.Add(New NpgsqlParameter(name, NpgsqlDbType.Date) With {
                .Value = If(value.HasValue, CObj(value.Value), DBNull.Value)})
    End Sub

    Private Shared Sub AddNullableIntParam(cmd As NpgsqlCommand, name As String, value As Integer?)
        cmd.Parameters.Add(New NpgsqlParameter(name, NpgsqlDbType.Integer) With {
                .Value = If(value.HasValue, CObj(value.Value), DBNull.Value)})
    End Sub

    Private Shared Sub AddNullableBoolParam(cmd As NpgsqlCommand, name As String, value As Boolean?)
        cmd.Parameters.Add(New NpgsqlParameter(name, NpgsqlDbType.Boolean) With {
                .Value = If(value.HasValue, CObj(value.Value), DBNull.Value)})
    End Sub

    ' AACT's published types occasionally drift between schema versions —
    ' e.g. healthy_volunteers has been varchar in older snapshots and boolean
    ' in newer ones, and source / enrollment_type can appear typed
    ' differently across mirrors. Rather than hardcode an expected type per
    ' column, stringify whatever the column actually is: booleans render
    ' as "Yes"/"No", numerics stringify, text stays text. This keeps the
    ' gateway resilient to AACT schema variation without losing fidelity.
    Private Shared Function ReadStringOrEmpty(reader As NpgsqlDataReader, ordinal As Integer) As String
        If reader.IsDBNull(ordinal) Then Return ""
        Dim dataType = reader.GetDataTypeName(ordinal)
        If String.Equals(dataType, "boolean", StringComparison.OrdinalIgnoreCase) Then
            Return If(reader.GetBoolean(ordinal), "Yes", "No")
        End If
        Dim value = reader.GetValue(ordinal)
        If value Is Nothing OrElse value Is DBNull.Value Then Return ""
        ' Normalize AACT markdown-escape backslashes — purely a rendering
        ' artefact (their markdown viewer would interpret '>' as a blockquote
        ' otherwise). Stripping at the boundary keeps the LLM input, the
        ' parser output, and the dashboard display all in agreement. Only
        ' called from ctgov.* readers, so output-DB text is unaffected.
        Return NormalizeAactText(value.ToString())
    End Function

    ' --- AACT text normalization ---

    ' Matches any backslash followed by one of CommonMark's ASCII-punctuation
    ' escapable chars (spec §2.4). Broader than the chars we have actually
    ' seen in AACT — `\>`, `\<`, `\*`, `\[`, `\]`, `\&` etc. — but matching
    ' the full spec set means we don't whack-a-mole when AACT escapes a new
    ' character. Backslash before any non-punctuation char (letter, digit,
    ' whitespace) is left untouched.
    Private Shared ReadOnly MarkdownEscapePattern As New Regex(
            "\\([\\!""#$%&'()*+,\-./:;<=>?@\[\]\^_`{|}~])",
            RegexOptions.Compiled)

    ''' <summary>
    ''' Strips CommonMark backslash-escapes from text. AACT escapes punctuation
    ''' chars (`\>`, `\*`, `\[`, `\&`, etc.) in its text fields so they don't
    ''' trigger markdown formatting in their viewer. These escapes are pure
    ''' storage artefacts — no clinical meaning — and they break downstream
    ''' consumers: the LLM carries `\>` into its JSON output (invalid escape),
    ''' and the dashboard's substring match against the original_text fails.
    ''' Normalize once at the gateway boundary so nobody downstream sees the
    ''' escapes.
    ''' </summary>
    Friend Shared Function NormalizeAactText(text As String) As String
        If String.IsNullOrEmpty(text) Then Return text
        Return MarkdownEscapePattern.Replace(text, "$1")
    End Function

    Private Shared Function ReadNullableDate(reader As NpgsqlDataReader, ordinal As Integer) As Date?
        If reader.IsDBNull(ordinal) Then Return Nothing
        ' AACT publishes dates as date, but some mirrors store as text. Use
        ' GetValue() and coerce so either typing works.
        Dim value = reader.GetValue(ordinal)
        If TypeOf value Is DateTime Then Return CType(value, DateTime)
        Dim parsed As DateTime
        If DateTime.TryParse(value.ToString(), parsed) Then Return parsed
        Return Nothing
    End Function

    Private Shared Function ReadNullableInt32(reader As NpgsqlDataReader, ordinal As Integer) As Integer?
        If reader.IsDBNull(ordinal) Then Return Nothing
        Dim value = reader.GetValue(ordinal)
        If TypeOf value Is Integer Then Return CInt(value)
        If TypeOf value Is Long Then Return CInt(CLng(value))
        If TypeOf value Is Short Then Return CInt(CShort(value))
        If TypeOf value Is Decimal Then Return CInt(Math.Truncate(CDec(value)))
        If TypeOf value Is Double Then Return CInt(Math.Truncate(CDbl(value)))
        Dim parsed As Integer
        If Integer.TryParse(value.ToString(), parsed) Then Return parsed
        Return Nothing
    End Function

    Private Shared Function ReadNullableBoolean(reader As NpgsqlDataReader, ordinal As Integer) As Boolean?
        If reader.IsDBNull(ordinal) Then Return Nothing
        Dim dataType = reader.GetDataTypeName(ordinal)
        If String.Equals(dataType, "boolean", StringComparison.OrdinalIgnoreCase) Then
            Return reader.GetBoolean(ordinal)
        End If
        ' AACT mirror typed this column as text instead of boolean — accept
        ' "true"/"false"/"t"/"f"/"yes"/"no"/"1"/"0" case-insensitively.
        Dim text = reader.GetValue(ordinal)?.ToString()?.Trim()?.ToLowerInvariant()
        Select Case text
            Case "true", "t", "yes", "y", "1" : Return True
            Case "false", "f", "no", "n", "0" : Return False
            Case Else : Return Nothing
        End Select
    End Function

    ' Whitelist of supported sort orderings for SearchEligibilityAsync.
    ' Keys are the stable URL/query-param values; the SQL fragments are baked
    ' in (never user-supplied), so we can safely interpolate them into the
    ' ORDER BY clause. Direction is fixed per column: timestamps and scores
    ' default to DESC (most relevant first), text/categorical columns default
    ' to ASC (alphabetical browsing). Tiebreak by id for deterministic ordering.
    Private Shared ReadOnly OrderByMap As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"created_at_desc", "ORDER BY created_at DESC, id DESC"},
            {"created_at_asc", "ORDER BY created_at ASC, id ASC"},
            {"nct_id_asc", "ORDER BY nct_id ASC, id ASC"},
            {"criterion_asc", "ORDER BY criterion ASC, id ASC"},
            {"domain_asc", "ORDER BY domain ASC, id ASC"},
            {"concept_asc", "ORDER BY concept ASC, id ASC"},
            {"concept_code_asc", "ORDER BY concept_code ASC NULLS LAST, id ASC"},
            {"semantic_type_asc", "ORDER BY semantic_type ASC NULLS LAST, id ASC"},
            {"match_score_desc", "ORDER BY match_score DESC, id DESC"}
        }

    Private Shared Function ResolveOrderBy(sortBy As String) As String
        If String.IsNullOrWhiteSpace(sortBy) Then Return OrderByMap("created_at_desc")
        Dim fragment As String = Nothing
        If OrderByMap.TryGetValue(sortBy, fragment) Then Return fragment
        Return OrderByMap("created_at_desc")
    End Function

    ' ============ Study audit (output DB, public.eligibility_study) ============

    ''' <summary>
    ''' Reconciles orphaned study rows: rows still at status='running' whose host died
    ''' before reaching a terminal status. Marks any older than
    ''' <paramref name="minimumAge"/> as 'interrupted' and returns the count.
    ''' <paramref name="minimumAge"/> &lt;= zero disables the sweep (returns 0 without
    ''' touching the database).
    ''' <para>
    ''' WHY AN AGE THRESHOLD, AND WHY IT IS THE ONLY SAFEGUARD: a 'running' row is not
    ''' proof of an orphan - it is proof of an in-flight trial. RunGate is an
    ''' in-process CLR lock, so the CLI (`elig run`) can be processing trials against
    ''' this same database right now and this process cannot see it. There is no
    ''' ownership registry to consult. The age gate is what keeps the sweep off those
    ''' live rows: a single trial's worst case is ~2h on the shipped config (see
    ''' PostgresOptions.InterruptedStudyThresholdHours), so anything older is dead.
    ''' </para>
    ''' <para>
    ''' SELF-HEALING, DELIBERATELY: FinishStudyAsync updates by (run_id, nct_id) with
    ''' NO status predicate, so if this sweep ever does mislabel a live trial, that
    ''' trial's completion overwrites 'interrupted' with its real outcome. Do NOT
    ''' "harden" FinishStudyAsync with `AND status = 'running'` - that turns a
    ''' self-correcting transient into permanent corruption, freezing the row at
    ''' 'interrupted' and discarding the true result.
    ''' </para>
    ''' <para>
    ''' Progression is untouched. GetAttemptedNctIdsAsync has no status filter, so an
    ''' 'interrupted' trial stays excluded from forward batches exactly as a failed one
    ''' does; recovery is the History tab's Re-run, which bypasses the anti-join. This
    ''' method buys visibility, not re-selection.
    ''' </para>
    ''' </summary>
    Public Async Function ReconcileInterruptedStudiesAsync(
            minimumAge As TimeSpan,
            cancellationToken As CancellationToken) As Task(Of Integer)

        If minimumAge <= TimeSpan.Zero Then
            _logger.LogDebug(
                    "Interrupted-study reconcile disabled (threshold {Hours}h); skipping.",
                    minimumAge.TotalHours)
            Return 0
        End If

        ' Cutoff from the APP clock, not the database's now(): started_at is written
        ' by the caller (PipelineOrchestrator) from DateTimeOffset.UtcNow, so comparing
        ' against now() would silently drift if the app and DB clocks disagree.
        Dim cutoff = DateTimeOffset.UtcNow - minimumAge

        ' status='running' is served by ix_eligibility_study_status and started_at by
        ' ix_eligibility_study_started_at (both from V2), so no new index is needed.
        ' finished_at IS NULL is redundant with status='running' (StartStudyAsync's
        ' upsert clears it) but costs nothing and documents the invariant.
        Const Sql As String = "
UPDATE public.eligibility_study
   SET status        = 'interrupted',
       finished_at   = @finished_at,
       error_message = COALESCE(NULLIF(error_message, ''),
                                'Interrupted: the host process stopped before this trial reached a terminal status.')
 WHERE status = 'running'
   AND finished_at IS NULL
   AND started_at < @cutoff"

        ' Exceptions propagate: the caller (Program.cs startup) owns the try/catch, the
        ' same shape as EnsureSourcePerformanceIndexesAsync's startup step.
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("finished_at", NpgsqlDbType.TimestampTz) With {
                        .Value = DateTimeOffset.UtcNow})
                cmd.Parameters.Add(New NpgsqlParameter("cutoff", NpgsqlDbType.TimestampTz) With {
                        .Value = cutoff})
                Dim updated = Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                If updated > 0 Then
                    _logger.LogInformation(
                            "Reconciled {Count} study row(s) stranded at 'running' for more than {Hours}h to 'interrupted'. " &
                            "They are visible on the dashboard and re-runnable from History.",
                            updated, minimumAge.TotalHours)
                End If
                Return updated
            End Using
        End Using
    End Function

    Public Async Function StartStudyAsync(
            runId As Guid,
            nctId As String,
            startedAt As DateTimeOffset,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.StartStudyAsync
        If String.IsNullOrWhiteSpace(nctId) Then
            Throw New ArgumentException("nctId must be non-empty", NameOf(nctId))
        End If

        ' UPSERT so a re-processed trial within the same run resets cleanly:
        ' if Start ran, the run died, then a new attempt re-acquired the trial,
        ' we want fresh started_at and a cleared terminal state.
        Const Sql As String = "
INSERT INTO public.eligibility_study (run_id, nct_id, started_at, status)
VALUES (@run_id, @nct_id, @started_at, 'running')
ON CONFLICT (run_id, nct_id) DO UPDATE
    SET started_at            = excluded.started_at,
        finished_at           = NULL,
        status                = 'running',
        llm_succeeded         = NULL,
        llm_finish_reason     = NULL,
        llm_prompt_tokens     = NULL,
        llm_completion_tokens = NULL,
        parsed_record_count   = NULL,
        persisted_row_count   = NULL,
        error_message         = NULL"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("run_id", NpgsqlDbType.Uuid) With {.Value = runId})
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                cmd.Parameters.Add(New NpgsqlParameter("started_at", NpgsqlDbType.TimestampTz) With {.Value = startedAt})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    Public Async Function FinishStudyAsync(
            execution As StudyExecution,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.FinishStudyAsync
        If execution Is Nothing Then Throw New ArgumentNullException(NameOf(execution))

        ' UPDATE only — Start should have inserted the row. If somehow no row
        ' is present (Start swallowed an audit-write failure), this is a no-op
        ' and the trial proceeds without an audit trail rather than crashing.
        Const Sql As String = "
UPDATE public.eligibility_study
   SET finished_at           = @finished_at,
       status                = @status,
       llm_succeeded         = @llm_succeeded,
       llm_finish_reason     = @llm_finish_reason,
       llm_prompt_tokens     = @llm_prompt_tokens,
       llm_completion_tokens = @llm_completion_tokens,
       parsed_record_count   = @parsed_record_count,
       persisted_row_count   = @persisted_row_count,
       error_message         = @error_message,
       llm_raw_response      = @llm_raw_response,
       llm_stopped_eos       = @llm_stopped_eos,
       llm_stopped_limit     = @llm_stopped_limit,
       llm_stopped_word      = @llm_stopped_word,
       llm_stopping_word     = @llm_stopping_word,
       llm_truncated         = @llm_truncated,
       llm_ms                = @llm_ms,
       umls_ms               = @umls_ms,
       persist_ms            = @persist_ms
 WHERE run_id = @run_id AND nct_id = @nct_id"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("run_id", NpgsqlDbType.Uuid) With {.Value = execution.RunId})
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = execution.NctId})
                cmd.Parameters.Add(New NpgsqlParameter("finished_at", NpgsqlDbType.TimestampTz) With {
                        .Value = If(execution.FinishedAt.HasValue, CObj(execution.FinishedAt.Value), DBNull.Value)})
                cmd.Parameters.Add(New NpgsqlParameter("status", NpgsqlDbType.Text) With {.Value = execution.Status})
                cmd.Parameters.Add(New NpgsqlParameter("llm_succeeded", NpgsqlDbType.Boolean) With {
                        .Value = If(execution.LlmSucceeded.HasValue, CObj(execution.LlmSucceeded.Value), DBNull.Value)})
                cmd.Parameters.Add(New NpgsqlParameter("llm_finish_reason", NpgsqlDbType.Text) With {
                        .Value = NullIfEmpty(execution.LlmFinishReason)})
                cmd.Parameters.Add(New NpgsqlParameter("llm_prompt_tokens", NpgsqlDbType.Integer) With {
                        .Value = If(execution.LlmPromptTokens.HasValue, CObj(execution.LlmPromptTokens.Value), DBNull.Value)})
                cmd.Parameters.Add(New NpgsqlParameter("llm_completion_tokens", NpgsqlDbType.Integer) With {
                        .Value = If(execution.LlmCompletionTokens.HasValue, CObj(execution.LlmCompletionTokens.Value), DBNull.Value)})
                cmd.Parameters.Add(New NpgsqlParameter("parsed_record_count", NpgsqlDbType.Integer) With {
                        .Value = If(execution.ParsedRecordCount.HasValue, CObj(execution.ParsedRecordCount.Value), DBNull.Value)})
                cmd.Parameters.Add(New NpgsqlParameter("persisted_row_count", NpgsqlDbType.Integer) With {
                        .Value = If(execution.PersistedRowCount.HasValue, CObj(execution.PersistedRowCount.Value), DBNull.Value)})
                cmd.Parameters.Add(New NpgsqlParameter("error_message", NpgsqlDbType.Text) With {
                        .Value = NullIfEmpty(execution.ErrorMessage)})
                cmd.Parameters.Add(New NpgsqlParameter("llm_raw_response", NpgsqlDbType.Text) With {
                        .Value = NullIfEmpty(execution.LlmRawResponse)})
                cmd.Parameters.Add(New NpgsqlParameter("llm_stopped_eos", NpgsqlDbType.Boolean) With {
                        .Value = If(execution.LlmStoppedEos.HasValue, CObj(execution.LlmStoppedEos.Value), DBNull.Value)})
                cmd.Parameters.Add(New NpgsqlParameter("llm_stopped_limit", NpgsqlDbType.Boolean) With {
                        .Value = If(execution.LlmStoppedLimit.HasValue, CObj(execution.LlmStoppedLimit.Value), DBNull.Value)})
                cmd.Parameters.Add(New NpgsqlParameter("llm_stopped_word", NpgsqlDbType.Boolean) With {
                        .Value = If(execution.LlmStoppedWord.HasValue, CObj(execution.LlmStoppedWord.Value), DBNull.Value)})
                cmd.Parameters.Add(New NpgsqlParameter("llm_stopping_word", NpgsqlDbType.Text) With {
                        .Value = NullIfEmpty(execution.LlmStoppingWord)})
                cmd.Parameters.Add(New NpgsqlParameter("llm_truncated", NpgsqlDbType.Boolean) With {
                        .Value = If(execution.LlmTruncated.HasValue, CObj(execution.LlmTruncated.Value), DBNull.Value)})
                cmd.Parameters.Add(New NpgsqlParameter("llm_ms", NpgsqlDbType.Integer) With {
                        .Value = If(execution.LlmMs.HasValue, CObj(execution.LlmMs.Value), DBNull.Value)})
                cmd.Parameters.Add(New NpgsqlParameter("umls_ms", NpgsqlDbType.Integer) With {
                        .Value = If(execution.UmlsMs.HasValue, CObj(execution.UmlsMs.Value), DBNull.Value)})
                cmd.Parameters.Add(New NpgsqlParameter("persist_ms", NpgsqlDbType.Integer) With {
                        .Value = If(execution.PersistMs.HasValue, CObj(execution.PersistMs.Value), DBNull.Value)})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    ' Hardcoded sort whitelist — sortBy can never interpolate user input.
    ' Numeric/duration columns use NULLS LAST so in-flight rows (finished_at
    ' IS NULL, token counts not yet captured) sink to the bottom rather than
    ' floating to the top of a descending sort.
    Private Shared ReadOnly StudyOrderByMap As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"started_at_desc", "ORDER BY started_at DESC, nct_id DESC"},
            {"started_at_asc", "ORDER BY started_at ASC, nct_id ASC"},
            {"finished_at_desc", "ORDER BY finished_at DESC NULLS LAST, nct_id DESC"},
            {"nct_id_asc", "ORDER BY nct_id ASC, started_at DESC"},
            {"status_asc", "ORDER BY status ASC, started_at DESC"},
            {"duration_desc", "ORDER BY (finished_at - started_at) DESC NULLS LAST, nct_id DESC"},
            {"parsed_desc", "ORDER BY parsed_record_count DESC NULLS LAST, nct_id DESC"},
            {"persisted_desc", "ORDER BY persisted_row_count DESC NULLS LAST, nct_id DESC"},
            {"prompt_tokens_desc", "ORDER BY llm_prompt_tokens DESC NULLS LAST, nct_id DESC"},
            {"completion_tokens_desc", "ORDER BY llm_completion_tokens DESC NULLS LAST, nct_id DESC"}
        }

    Public Async Function GetStudiesAsync(
            filter As StudyFilter,
            sortBy As String,
            page As Integer,
            pageSize As Integer,
            cancellationToken As CancellationToken) As Task(Of StudyExecutionPage) _
            Implements IPostgresGateway.GetStudiesAsync
        If filter Is Nothing Then Throw New ArgumentNullException(NameOf(filter))
        ' Upper bound must track the largest StudiesViewModel.PageSizeChoices
        ' option — a smaller cap clamps the returned PageSize to a value the
        ' History-tab dropdown can't match, so it falls back to showing 10.
        Dim cappedPageSize = Math.Min(Math.Max(pageSize, 1), 1000)
        Dim cappedPage = Math.Max(page, 1)
        Dim offset = (cappedPage - 1) * cappedPageSize

        Dim orderBy As String = Nothing
        If String.IsNullOrWhiteSpace(sortBy) OrElse Not StudyOrderByMap.TryGetValue(sortBy, orderBy) Then
            orderBy = StudyOrderByMap("started_at_desc")
        End If

        ' When HideSuperseded is on, run ROW_NUMBER() over (nct_id, started_at DESC)
        ' first and keep rn=1 rows BEFORE the categorical filters are applied.
        ' Filtering first would let "latest failed attempt per trial" leak through
        ' even after a later successful rerun — see StudyFilter.HideSuperseded
        ' for the rationale.
        '
        ' Page rows and the total count run as two statements. The old single
        ' query used COUNT(*) OVER(), which forces the window to buffer every
        ' matching row — including the large llm_raw_response — and spilled to
        ' disk once eligibility_study grew past ~20k rows. For HideSuperseded
        ' the ROW_NUMBER window now selects only the (run_id, nct_id) keys, so
        ' the sort it needs stays narrow; the wide columns are joined back by
        ' primary key for just the page rows.
        Dim pageSql As String
        Dim countSql As String
        If filter.HideSuperseded Then
            Const latestKeys As String = "
    SELECT run_id AS l_run_id, nct_id AS l_nct_id
    FROM (
        SELECT run_id, nct_id,
               ROW_NUMBER() OVER (PARTITION BY nct_id ORDER BY started_at DESC) AS rn
        FROM public.eligibility_study
    ) ranked
    WHERE rn = 1"
            pageSql = $"
SELECT s.run_id, s.nct_id, s.started_at, s.finished_at, s.status,
       s.llm_succeeded, s.llm_finish_reason, s.llm_prompt_tokens, s.llm_completion_tokens,
       s.parsed_record_count, s.persisted_row_count, s.error_message, s.llm_raw_response,
       s.llm_stopped_eos, s.llm_stopped_limit, s.llm_stopped_word, s.llm_stopping_word, s.llm_truncated
FROM public.eligibility_study s
JOIN ({latestKeys}
) latest ON latest.l_run_id = s.run_id AND latest.l_nct_id = s.nct_id
WHERE (@nct_id IS NULL OR s.nct_id = @nct_id)
  AND (@status IS NULL OR s.status = @status)
  AND (@run_id IS NULL OR s.run_id = @run_id)
{orderBy}
OFFSET @offset LIMIT @limit"
            countSql = "
SELECT COUNT(*)
FROM (
    SELECT run_id, nct_id, status,
           ROW_NUMBER() OVER (PARTITION BY nct_id ORDER BY started_at DESC) AS rn
    FROM public.eligibility_study
) latest
WHERE rn = 1
  AND (@nct_id IS NULL OR nct_id = @nct_id)
  AND (@status IS NULL OR status = @status)
  AND (@run_id IS NULL OR run_id = @run_id)"
        Else
            Const whereClause As String = "
WHERE (@nct_id IS NULL OR nct_id = @nct_id)
  AND (@status IS NULL OR status = @status)
  AND (@run_id IS NULL OR run_id = @run_id)"
            pageSql = $"
SELECT run_id, nct_id, started_at, finished_at, status,
       llm_succeeded, llm_finish_reason, llm_prompt_tokens, llm_completion_tokens,
       parsed_record_count, persisted_row_count, error_message, llm_raw_response,
       llm_stopped_eos, llm_stopped_limit, llm_stopped_word, llm_stopping_word, llm_truncated
FROM public.eligibility_study{whereClause}
{orderBy}
OFFSET @offset LIMIT @limit"
            countSql = $"SELECT COUNT(*) FROM public.eligibility_study{whereClause}"
        End If

        Dim rows As New List(Of StudyExecution)
        Dim totalRows As Long = 0
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = pageSql
                AddTextParam(cmd, "nct_id", filter.NctId)
                AddTextParam(cmd, "status", filter.Status)
                cmd.Parameters.Add(New NpgsqlParameter("run_id", NpgsqlDbType.Uuid) With {
                        .Value = If(filter.RunId.HasValue, CObj(filter.RunId.Value), DBNull.Value)})
                cmd.Parameters.Add(New NpgsqlParameter("offset", NpgsqlDbType.Integer) With {.Value = offset})
                cmd.Parameters.Add(New NpgsqlParameter("limit", NpgsqlDbType.Integer) With {.Value = cappedPageSize})

                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        rows.Add(ReadStudyExecution(reader))
                    End While
                End Using
            End Using

            Using countCmd = conn.CreateCommand()
                countCmd.CommandText = countSql
                AddTextParam(countCmd, "nct_id", filter.NctId)
                AddTextParam(countCmd, "status", filter.Status)
                countCmd.Parameters.Add(New NpgsqlParameter("run_id", NpgsqlDbType.Uuid) With {
                        .Value = If(filter.RunId.HasValue, CObj(filter.RunId.Value), DBNull.Value)})
                totalRows = Convert.ToInt64(Await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False))
            End Using
        End Using

        Return New StudyExecutionPage(rows, totalRows, cappedPage, cappedPageSize)
    End Function

    Public Async Function GetStudyHistoryAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of StudyExecution)) _
            Implements IPostgresGateway.GetStudyHistoryAsync
        If String.IsNullOrWhiteSpace(nctId) Then
            Return Array.Empty(Of StudyExecution)()
        End If

        Const Sql As String = "
SELECT run_id, nct_id, started_at, finished_at, status,
       llm_succeeded, llm_finish_reason, llm_prompt_tokens, llm_completion_tokens,
       parsed_record_count, persisted_row_count, error_message, llm_raw_response,
       llm_stopped_eos, llm_stopped_limit, llm_stopped_word, llm_stopping_word, llm_truncated
FROM public.eligibility_study
WHERE nct_id = @nct_id
ORDER BY started_at DESC"

        Dim rows As New List(Of StudyExecution)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        rows.Add(ReadStudyExecution(reader))
                    End While
                End Using
            End Using
        End Using
        Return rows
    End Function

    ' ============ SearchStudyDetailsAsync (output DB, Analysis-tab Search modal) ============

    Public Async Function SearchStudyDetailsAsync(
            filter As StudySearchFilter,
            limit As Integer,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of StudySearchResult)) _
            Implements IPostgresGateway.SearchStudyDetailsAsync

        ' Refuse the empty-filter case — the modal exists to narrow on at
        ' least one characteristic, and an unbounded "everything in the
        ' snapshot table" dump is never what the user asked for.
        If filter Is Nothing OrElse filter.IsEmpty Then
            Return Array.Empty(Of StudySearchResult)()
        End If

        Dim cappedLimit = Math.Min(Math.Max(limit, 1), 500)

        ' "@param = '' OR column ILIKE '%' || @param || '%'" lets a single SQL
        ' shape cover every combination of populated / blank filter fields
        ' without dynamic SQL. The condition[] match uses EXISTS over unnest
        ' so any element substring-matching the value qualifies the row.
        Const Sql As String = "
SELECT nct_id, COALESCE(brief_title, '') AS brief_title,
       COALESCE(overall_status, '') AS overall_status,
       COALESCE(phase, '') AS phase,
       COALESCE(study_type, '') AS study_type,
       COALESCE(source, '') AS source,
       COALESCE(conditions, '{}') AS conditions
FROM public.eligibility_study_detail
WHERE (@nct_id = '' OR nct_id ILIKE '%' || @nct_id || '%')
  AND (@brief_title = '' OR COALESCE(brief_title, '') ILIKE '%' || @brief_title || '%')
  AND (@official_title = '' OR COALESCE(official_title, '') ILIKE '%' || @official_title || '%')
  AND (@overall_status = '' OR COALESCE(overall_status, '') ILIKE '%' || @overall_status || '%')
  AND (@phase = '' OR COALESCE(phase, '') ILIKE '%' || @phase || '%')
  AND (@study_type = '' OR COALESCE(study_type, '') ILIKE '%' || @study_type || '%')
  AND (@source = '' OR COALESCE(source, '') ILIKE '%' || @source || '%')
  AND (@brief_summary = '' OR COALESCE(brief_summary, '') ILIKE '%' || @brief_summary || '%')
  AND (@condition = '' OR EXISTS (
        SELECT 1 FROM unnest(conditions) c WHERE c ILIKE '%' || @condition || '%'))
  AND (@gender = '' OR COALESCE(gender, '') ILIKE '%' || @gender || '%')
  AND (@healthy_volunteers = '' OR COALESCE(healthy_volunteers, '') ILIKE '%' || @healthy_volunteers || '%')
ORDER BY nct_id
LIMIT @limit"

        Dim results As New List(Of StudySearchResult)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = If(filter.NctId, "")})
                cmd.Parameters.Add(New NpgsqlParameter("brief_title", NpgsqlDbType.Text) With {.Value = If(filter.BriefTitle, "")})
                cmd.Parameters.Add(New NpgsqlParameter("official_title", NpgsqlDbType.Text) With {.Value = If(filter.OfficialTitle, "")})
                cmd.Parameters.Add(New NpgsqlParameter("overall_status", NpgsqlDbType.Text) With {.Value = If(filter.OverallStatus, "")})
                cmd.Parameters.Add(New NpgsqlParameter("phase", NpgsqlDbType.Text) With {.Value = If(filter.Phase, "")})
                cmd.Parameters.Add(New NpgsqlParameter("study_type", NpgsqlDbType.Text) With {.Value = If(filter.StudyType, "")})
                cmd.Parameters.Add(New NpgsqlParameter("source", NpgsqlDbType.Text) With {.Value = If(filter.Source, "")})
                cmd.Parameters.Add(New NpgsqlParameter("brief_summary", NpgsqlDbType.Text) With {.Value = If(filter.BriefSummary, "")})
                cmd.Parameters.Add(New NpgsqlParameter("condition", NpgsqlDbType.Text) With {.Value = If(filter.Condition, "")})
                cmd.Parameters.Add(New NpgsqlParameter("gender", NpgsqlDbType.Text) With {.Value = If(filter.Gender, "")})
                cmd.Parameters.Add(New NpgsqlParameter("healthy_volunteers", NpgsqlDbType.Text) With {.Value = If(filter.HealthyVolunteers, "")})
                cmd.Parameters.Add(New NpgsqlParameter("limit", NpgsqlDbType.Integer) With {.Value = cappedLimit})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        Dim conditions As IReadOnlyList(Of String) =
                                If(reader.IsDBNull(6),
                                   CType(Array.Empty(Of String)(), IReadOnlyList(Of String)),
                                   reader.GetFieldValue(Of String())(6))
                        results.Add(New StudySearchResult(
                                nctId:=reader.GetString(0),
                                briefTitle:=reader.GetString(1),
                                overallStatus:=reader.GetString(2),
                                phase:=reader.GetString(3),
                                studyType:=reader.GetString(4),
                                source:=reader.GetString(5),
                                conditions:=conditions))
                    End While
                End Using
            End Using
        End Using
        Return results
    End Function

    Public Async Function DeleteStudyAsync(
            runId As Guid,
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.DeleteStudyAsync
        If String.IsNullOrWhiteSpace(nctId) Then Return 0

        Const Sql As String = "
DELETE FROM public.eligibility_study
 WHERE run_id = @run_id AND nct_id = @nct_id"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("run_id", NpgsqlDbType.Uuid) With {.Value = runId})
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                Return Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    Private Shared Function ReadStudyExecution(reader As NpgsqlDataReader) As StudyExecution
        Return New StudyExecution(
                runId:=reader.GetGuid(0),
                nctId:=reader.GetString(1),
                startedAt:=reader.GetFieldValue(Of DateTimeOffset)(2),
                finishedAt:=If(reader.IsDBNull(3),
                               CType(Nothing, DateTimeOffset?),
                               reader.GetFieldValue(Of DateTimeOffset)(3)),
                status:=reader.GetString(4),
                llmSucceeded:=If(reader.IsDBNull(5),
                                 CType(Nothing, Boolean?),
                                 reader.GetBoolean(5)),
                llmFinishReason:=If(reader.IsDBNull(6), "", reader.GetString(6)),
                llmPromptTokens:=If(reader.IsDBNull(7), CType(Nothing, Integer?), reader.GetInt32(7)),
                llmCompletionTokens:=If(reader.IsDBNull(8), CType(Nothing, Integer?), reader.GetInt32(8)),
                parsedRecordCount:=If(reader.IsDBNull(9), CType(Nothing, Integer?), reader.GetInt32(9)),
                persistedRowCount:=If(reader.IsDBNull(10), CType(Nothing, Integer?), reader.GetInt32(10)),
                errorMessage:=If(reader.IsDBNull(11), "", reader.GetString(11)),
                llmRawResponse:=If(reader.IsDBNull(12), "", reader.GetString(12)),
                llmStoppedEos:=If(reader.IsDBNull(13), CType(Nothing, Boolean?), reader.GetBoolean(13)),
                llmStoppedLimit:=If(reader.IsDBNull(14), CType(Nothing, Boolean?), reader.GetBoolean(14)),
                llmStoppedWord:=If(reader.IsDBNull(15), CType(Nothing, Boolean?), reader.GetBoolean(15)),
                llmStoppingWord:=If(reader.IsDBNull(16), "", reader.GetString(16)),
                llmTruncated:=If(reader.IsDBNull(17), CType(Nothing, Boolean?), reader.GetBoolean(17)))
    End Function

    ' ============ Authoring feature (output DB, authoring_* tables) ============

    Public Async Function ListAuthoringStudiesAsync(
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of AuthoringStudySummary)) _
            Implements IPostgresGateway.ListAuthoringStudiesAsync
        Const Sql As String = "
SELECT s.authoring_study_id, s.label, s.source_kind, s.source_ref, s.phase,
       s.created_at, s.updated_at,
       (SELECT count(*) FROM public.authoring_criterion c
         WHERE c.authoring_study_id = s.authoring_study_id),
       s.study_id
FROM public.authoring_study s
ORDER BY s.updated_at DESC"

        Dim result As New List(Of AuthoringStudySummary)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(New AuthoringStudySummary(
                                authoringStudyId:=reader.GetGuid(0),
                                studyId:=If(reader.IsDBNull(8), "", reader.GetString(8)),
                                label:=reader.GetString(1),
                                sourceKind:=reader.GetString(2),
                                sourceRef:=If(reader.IsDBNull(3), "", reader.GetString(3)),
                                phase:=If(reader.IsDBNull(4), "", reader.GetString(4)),
                                createdAt:=reader.GetFieldValue(Of DateTimeOffset)(5),
                                updatedAt:=reader.GetFieldValue(Of DateTimeOffset)(6),
                                criterionCount:=CInt(reader.GetInt64(7))))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function GetAuthoringStudyAsync(
            authoringStudyId As Guid,
            cancellationToken As CancellationToken) As Task(Of AuthoringStudyAggregate) _
            Implements IPostgresGateway.GetAuthoringStudyAsync

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Dim study As AuthoringStudy = Nothing
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT authoring_study_id, label, source_kind, source_ref, created_at, updated_at,
       brief_title, official_title, overall_status, phase, study_type,
       start_date, completion_date, primary_completion_date, enrollment,
       enrollment_type, source, why_stopped, brief_summary, conditions, interventions,
       created_by, last_updated_by, study_id
FROM public.authoring_study
WHERE authoring_study_id = @id"
                cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = authoringStudyId})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    If Not Await reader.ReadAsync(cancellationToken).ConfigureAwait(False) Then
                        Return Nothing
                    End If
                    study = ReadAuthoringStudy(reader)
                End Using
            End Using

            Dim eligibility As New AuthoringEligibility With {.AuthoringStudyId = authoringStudyId}
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT criteria, gender, minimum_age, maximum_age, healthy_volunteers,
       sampling_method, population, adult, child, older_adult
FROM public.authoring_eligibility
WHERE authoring_study_id = @id"
                cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = authoringStudyId})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    If Await reader.ReadAsync(cancellationToken).ConfigureAwait(False) Then
                        eligibility.Criteria = ReadOutputString(reader, 0)
                        eligibility.Gender = ReadOutputString(reader, 1)
                        eligibility.MinimumAge = ReadOutputString(reader, 2)
                        eligibility.MaximumAge = ReadOutputString(reader, 3)
                        eligibility.HealthyVolunteers = ReadOutputString(reader, 4)
                        eligibility.SamplingMethod = ReadOutputString(reader, 5)
                        eligibility.Population = ReadOutputString(reader, 6)
                        eligibility.Adult = ReadNullableBoolean(reader, 7)
                        eligibility.Child = ReadNullableBoolean(reader, 8)
                        eligibility.OlderAdult = ReadNullableBoolean(reader, 9)
                    End If
                End Using
            End Using

            Dim criteria As New List(Of AuthoringCriterion)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
SELECT authoring_criterion_id, authoring_study_id, ordinal, criterion,
       normalized_text, concept, concept_code, semantic_type, domain,
       source_note, created_at, updated_at, created_by, last_updated_by,
       manual_reason
FROM public.authoring_criterion
WHERE authoring_study_id = @id
ORDER BY ordinal"
                cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = authoringStudyId})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        criteria.Add(New AuthoringCriterion With {
                                .AuthoringCriterionId = reader.GetGuid(0),
                                .AuthoringStudyId = reader.GetGuid(1),
                                .Ordinal = reader.GetInt32(2),
                                .Criterion = reader.GetString(3),
                                .NormalizedText = reader.GetString(4),
                                .Concept = ReadOutputString(reader, 5),
                                .ConceptCode = ReadOutputString(reader, 6),
                                .SemanticType = ReadOutputString(reader, 7),
                                .Domain = ReadOutputString(reader, 8),
                                .SourceNote = ReadOutputString(reader, 9),
                                .CreatedAt = reader.GetFieldValue(Of DateTimeOffset)(10),
                                .UpdatedAt = reader.GetFieldValue(Of DateTimeOffset)(11),
                                .CreatedBy = ReadNullableGuid(reader, 12),
                                .LastUpdatedBy = ReadNullableGuid(reader, 13),
                                .ManualReason = ReadOutputString(reader, 14)})
                    End While
                End Using
            End Using

            ' Lineage: load each criterion's source records (authoring_criterion_source,
            ' migration V10) in one query and attach them by criterion id.
            If criteria.Count > 0 Then
                Dim byId As New Dictionary(Of Guid, AuthoringCriterion)()
                For Each cr In criteria
                    byId(cr.AuthoringCriterionId) = cr
                Next
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "
SELECT s.authoring_criterion_source_id, s.authoring_criterion_id, s.eligibility_id,
       s.nct_id, s.criterion, s.domain, s.concept, s.concept_code, s.semantic_type,
       s.qualifier, s.time_window, s.original_text, s.match_score, s.created_at
FROM public.authoring_criterion_source s
JOIN public.authoring_criterion c ON c.authoring_criterion_id = s.authoring_criterion_id
WHERE c.authoring_study_id = @id
ORDER BY s.authoring_criterion_id, s.nct_id, s.eligibility_id"
                    cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = authoringStudyId})
                    Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                        While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                            Dim critId = reader.GetGuid(1)
                            Dim owner As AuthoringCriterion = Nothing
                            If Not byId.TryGetValue(critId, owner) Then Continue While
                            owner.Sources.Add(New AuthoringCriterionSource With {
                                    .AuthoringCriterionSourceId = reader.GetGuid(0),
                                    .AuthoringCriterionId = critId,
                                    .EligibilityId = If(reader.IsDBNull(2), CType(Nothing, Long?), reader.GetInt64(2)),
                                    .NctId = ReadOutputString(reader, 3),
                                    .Criterion = ReadOutputString(reader, 4),
                                    .Domain = ReadOutputString(reader, 5),
                                    .Concept = ReadOutputString(reader, 6),
                                    .ConceptCode = ReadOutputString(reader, 7),
                                    .SemanticType = ReadOutputString(reader, 8),
                                    .Qualifier = ReadOutputString(reader, 9),
                                    .TimeWindow = ReadOutputString(reader, 10),
                                    .OriginalText = ReadOutputString(reader, 11),
                                    .MatchScore = If(reader.IsDBNull(12), 0D, reader.GetDecimal(12)),
                                    .CreatedAt = reader.GetFieldValue(Of DateTimeOffset)(13)})
                        End While
                    End Using
                End Using
            End If

            Return New AuthoringStudyAggregate(study, eligibility, criteria)
        End Using
    End Function

    Public Async Function StudyIdExistsAsync(
            studyId As String,
            cancellationToken As CancellationToken) As Task(Of Boolean) _
            Implements IPostgresGateway.StudyIdExistsAsync
        Dim trimmed = If(studyId, "").Trim()
        If trimmed.Length = 0 Then Return False

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT 1 FROM public.authoring_study WHERE lower(study_id) = lower(@sid) LIMIT 1"
                cmd.Parameters.Add(New NpgsqlParameter("sid", NpgsqlDbType.Text) With {.Value = trimmed})
                Dim hit = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                Return hit IsNot Nothing
            End Using
        End Using
    End Function

    Public Async Function CreateAuthoringStudyAsync(
            study As AuthoringStudy,
            eligibility As AuthoringEligibility,
            userId As Guid,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.CreateAuthoringStudyAsync
        If study Is Nothing Then Throw New ArgumentNullException(NameOf(study))

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using tx = Await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(False)
                Using cmd = conn.CreateCommand()
                    cmd.Transaction = tx
                    cmd.CommandText = "
INSERT INTO public.authoring_study (
    authoring_study_id, label, source_kind, source_ref,
    brief_title, official_title, overall_status, phase, study_type,
    start_date, completion_date, primary_completion_date, enrollment,
    enrollment_type, source, why_stopped, brief_summary, conditions, interventions,
    created_by, last_updated_by, study_id
) VALUES (
    @id, @label, @source_kind, @source_ref,
    @brief_title, @official_title, @overall_status, @phase, @study_type,
    @start_date, @completion_date, @primary_completion_date, @enrollment,
    @enrollment_type, @source, @why_stopped, @brief_summary, @conditions, @interventions,
    @uid, @uid, @study_id
)"
                    AddAuthoringStudyParams(cmd, study)
                    cmd.Parameters.Add(New NpgsqlParameter("uid", NpgsqlDbType.Uuid) With {.Value = userId})
                    ' study_id is INSERT-only (fixed once set); the shared UPDATE
                    ' statement built from AddAuthoringStudyParams must not touch it.
                    cmd.Parameters.Add(New NpgsqlParameter("study_id", NpgsqlDbType.Text) With {.Value = NullIfEmpty(study.StudyId)})
                    Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                End Using

                Using cmd = conn.CreateCommand()
                    cmd.Transaction = tx
                    BuildAuthoringEligibilityUpsert(cmd, study.AuthoringStudyId, eligibility)
                    Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                End Using

                Await tx.CommitAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    Public Async Function UpdateAuthoringStudyAsync(
            study As AuthoringStudy,
            userId As Guid,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.UpdateAuthoringStudyAsync
        If study Is Nothing Then Throw New ArgumentNullException(NameOf(study))

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
UPDATE public.authoring_study SET
    label                   = @label,
    source_kind             = @source_kind,
    source_ref              = @source_ref,
    brief_title             = @brief_title,
    official_title          = @official_title,
    overall_status          = @overall_status,
    phase                   = @phase,
    study_type              = @study_type,
    start_date              = @start_date,
    completion_date         = @completion_date,
    primary_completion_date = @primary_completion_date,
    enrollment              = @enrollment,
    enrollment_type         = @enrollment_type,
    source                  = @source,
    why_stopped             = @why_stopped,
    brief_summary           = @brief_summary,
    conditions              = @conditions,
    interventions           = @interventions,
    updated_at              = now(),
    last_updated_by         = @uid
WHERE authoring_study_id = @id"
                AddAuthoringStudyParams(cmd, study)
                cmd.Parameters.Add(New NpgsqlParameter("uid", NpgsqlDbType.Uuid) With {.Value = userId})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    Public Async Function SetAuthoringStudyIdAsync(
            authoringStudyId As Guid,
            studyId As String,
            userId As Guid,
            cancellationToken As CancellationToken) As Task(Of Boolean) _
            Implements IPostgresGateway.SetAuthoringStudyIdAsync
        Dim trimmed = If(studyId, "").Trim()
        If trimmed.Length = 0 Then Return False

        ' Guarded to currently-empty study_id only: the "fixed once set"
        ' invariant must hold, so a study that already has an id is never
        ' overwritten (the WHERE clause makes that a no-op even under a race).
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
UPDATE public.authoring_study SET
    study_id        = @sid,
    updated_at      = now(),
    last_updated_by = @uid
WHERE authoring_study_id = @id AND (study_id IS NULL OR study_id = '')"
                cmd.Parameters.Add(New NpgsqlParameter("sid", NpgsqlDbType.Text) With {.Value = trimmed})
                cmd.Parameters.Add(New NpgsqlParameter("uid", NpgsqlDbType.Uuid) With {.Value = userId})
                cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = authoringStudyId})
                Dim rows = Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                Return rows > 0
            End Using
        End Using
    End Function

    Public Async Function SaveAuthoringEligibilityAsync(
            eligibility As AuthoringEligibility,
            userId As Guid,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.SaveAuthoringEligibilityAsync
        If eligibility Is Nothing Then Throw New ArgumentNullException(NameOf(eligibility))

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using tx = Await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(False)
                Using cmd = conn.CreateCommand()
                    cmd.Transaction = tx
                    BuildAuthoringEligibilityUpsert(cmd, eligibility.AuthoringStudyId, eligibility)
                    Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                End Using
                Await TouchAuthoringStudyAsync(conn, tx, eligibility.AuthoringStudyId, userId, cancellationToken).ConfigureAwait(False)
                Await tx.CommitAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    Public Async Function SaveAuthoringCriteriaAsync(
            authoringStudyId As Guid,
            criteria As IReadOnlyList(Of AuthoringCriterion),
            userId As Guid,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.SaveAuthoringCriteriaAsync

        ' Upsert (not replace-all): assign ids only to genuinely-new rows, then
        ' delete the rows the editor no longer carries and INSERT ... ON CONFLICT
        ' the rest. This preserves created_at/created_by for surviving rows so
        ' per-row attribution is meaningful. (Pre-V12 this was DELETE-all +
        ' INSERT-all, which reset both on every save.)
        Dim incoming As IReadOnlyList(Of AuthoringCriterion) =
                If(criteria, CType(Array.Empty(Of AuthoringCriterion)(), IReadOnlyList(Of AuthoringCriterion)))
        Dim keepIds(incoming.Count - 1) As Guid
        For i As Integer = 0 To incoming.Count - 1
            keepIds(i) = If(incoming(i).AuthoringCriterionId = Guid.Empty,
                            Guid.NewGuid(), incoming(i).AuthoringCriterionId)
        Next

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using tx = Await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(False)
                ' Remove only rows the editor dropped (cascade clears their sources).
                ' An empty keep array deletes everything (x <> ALL('{}') is true).
                Using deleteCmd = conn.CreateCommand()
                    deleteCmd.Transaction = tx
                    deleteCmd.CommandText =
                        "DELETE FROM public.authoring_criterion " &
                        "WHERE authoring_study_id = @id AND authoring_criterion_id <> ALL(@keep)"
                    deleteCmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = authoringStudyId})
                    deleteCmd.Parameters.Add(New NpgsqlParameter("keep", NpgsqlDbType.Array Or NpgsqlDbType.Uuid) With {.Value = keepIds})
                    Await deleteCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                End Using

                For i As Integer = 0 To incoming.Count - 1
                    Dim c = incoming(i)
                    Dim criterionId = keepIds(i)
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "
INSERT INTO public.authoring_criterion (
    authoring_criterion_id, authoring_study_id, ordinal, criterion,
    normalized_text, concept, concept_code, semantic_type, domain, source_note,
    manual_reason, created_by, last_updated_by
) VALUES (
    @id, @study_id, @ordinal, @criterion,
    @normalized_text, @concept, @concept_code, @semantic_type, @domain, @source_note,
    @manual_reason, @uid, @uid
)
ON CONFLICT (authoring_criterion_id) DO UPDATE SET
    ordinal         = excluded.ordinal,
    criterion       = excluded.criterion,
    normalized_text = excluded.normalized_text,
    concept         = excluded.concept,
    concept_code    = excluded.concept_code,
    semantic_type   = excluded.semantic_type,
    domain          = excluded.domain,
    source_note     = excluded.source_note,
    manual_reason   = excluded.manual_reason,
    last_updated_by = excluded.last_updated_by,
    updated_at      = now()"
                        cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = criterionId})
                        cmd.Parameters.Add(New NpgsqlParameter("study_id", NpgsqlDbType.Uuid) With {.Value = authoringStudyId})
                        cmd.Parameters.Add(New NpgsqlParameter("ordinal", NpgsqlDbType.Integer) With {.Value = i})
                        cmd.Parameters.Add(New NpgsqlParameter("criterion", NpgsqlDbType.Text) With {.Value = If(c.Criterion, "")})
                        cmd.Parameters.Add(New NpgsqlParameter("normalized_text", NpgsqlDbType.Text) With {.Value = If(c.NormalizedText, "")})
                        cmd.Parameters.Add(New NpgsqlParameter("concept", NpgsqlDbType.Text) With {.Value = NullIfEmpty(c.Concept)})
                        cmd.Parameters.Add(New NpgsqlParameter("concept_code", NpgsqlDbType.Text) With {.Value = NullIfEmpty(c.ConceptCode)})
                        cmd.Parameters.Add(New NpgsqlParameter("semantic_type", NpgsqlDbType.Text) With {.Value = NullIfEmpty(c.SemanticType)})
                        cmd.Parameters.Add(New NpgsqlParameter("domain", NpgsqlDbType.Text) With {.Value = NullIfEmpty(c.Domain)})
                        cmd.Parameters.Add(New NpgsqlParameter("source_note", NpgsqlDbType.Text) With {.Value = NullIfEmpty(c.SourceNote)})
                        cmd.Parameters.Add(New NpgsqlParameter("manual_reason", NpgsqlDbType.Text) With {.Value = NullIfEmpty(c.ManualReason)})
                        cmd.Parameters.Add(New NpgsqlParameter("uid", NpgsqlDbType.Uuid) With {.Value = userId})
                        Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                    End Using

                    ' Lineage: rewrite this criterion's source snapshots. The
                    ' selective DELETE above no longer clears surviving rows'
                    ' sources, so clear them explicitly before re-inserting.
                    Using delSrc = conn.CreateCommand()
                        delSrc.Transaction = tx
                        delSrc.CommandText = "DELETE FROM public.authoring_criterion_source WHERE authoring_criterion_id = @cid"
                        delSrc.Parameters.Add(New NpgsqlParameter("cid", NpgsqlDbType.Uuid) With {.Value = criterionId})
                        Await delSrc.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                    End Using
                    Await InsertAuthoringCriterionSourcesAsync(
                            conn, tx, criterionId, c.Sources, cancellationToken).ConfigureAwait(False)
                Next

                Await TouchAuthoringStudyAsync(conn, tx, authoringStudyId, userId, cancellationToken).ConfigureAwait(False)
                Await tx.CommitAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    ' Inserts the lineage snapshot rows for one authored criterion into
    ' authoring_criterion_source, on the supplied transaction.
    Private Shared Async Function InsertAuthoringCriterionSourcesAsync(
            conn As NpgsqlConnection,
            tx As NpgsqlTransaction,
            criterionId As Guid,
            sources As IReadOnlyList(Of AuthoringCriterionSource),
            cancellationToken As CancellationToken) As Task
        If sources Is Nothing OrElse sources.Count = 0 Then Return
        For Each src In sources
            Using cmd = conn.CreateCommand()
                cmd.Transaction = tx
                cmd.CommandText = "
INSERT INTO public.authoring_criterion_source (
    authoring_criterion_source_id, authoring_criterion_id, eligibility_id, nct_id,
    criterion, domain, concept, concept_code, semantic_type, qualifier,
    time_window, original_text, match_score
) VALUES (
    @id, @criterion_id, @eligibility_id, @nct_id,
    @criterion, @domain, @concept, @concept_code, @semantic_type, @qualifier,
    @time_window, @original_text, @match_score
)"
                cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = Guid.NewGuid()})
                cmd.Parameters.Add(New NpgsqlParameter("criterion_id", NpgsqlDbType.Uuid) With {.Value = criterionId})
                cmd.Parameters.Add(New NpgsqlParameter("eligibility_id", NpgsqlDbType.Bigint) With {
                        .Value = If(src.EligibilityId.HasValue, CObj(src.EligibilityId.Value), CObj(DBNull.Value))})
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = If(src.NctId, "")})
                cmd.Parameters.Add(New NpgsqlParameter("criterion", NpgsqlDbType.Text) With {.Value = NullIfEmpty(src.Criterion)})
                cmd.Parameters.Add(New NpgsqlParameter("domain", NpgsqlDbType.Text) With {.Value = NullIfEmpty(src.Domain)})
                cmd.Parameters.Add(New NpgsqlParameter("concept", NpgsqlDbType.Text) With {.Value = NullIfEmpty(src.Concept)})
                cmd.Parameters.Add(New NpgsqlParameter("concept_code", NpgsqlDbType.Text) With {.Value = NullIfEmpty(src.ConceptCode)})
                cmd.Parameters.Add(New NpgsqlParameter("semantic_type", NpgsqlDbType.Text) With {.Value = NullIfEmpty(src.SemanticType)})
                cmd.Parameters.Add(New NpgsqlParameter("qualifier", NpgsqlDbType.Text) With {.Value = NullIfEmpty(src.Qualifier)})
                cmd.Parameters.Add(New NpgsqlParameter("time_window", NpgsqlDbType.Text) With {.Value = NullIfEmpty(src.TimeWindow)})
                cmd.Parameters.Add(New NpgsqlParameter("original_text", NpgsqlDbType.Text) With {.Value = NullIfEmpty(src.OriginalText)})
                cmd.Parameters.Add(New NpgsqlParameter("match_score", NpgsqlDbType.Numeric) With {.Value = CObj(src.MatchScore)})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        Next
    End Function

    Public Async Function DeleteAuthoringStudyAsync(
            authoringStudyId As Guid,
            cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.DeleteAuthoringStudyAsync
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "DELETE FROM public.authoring_study WHERE authoring_study_id = @id"
                cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = authoringStudyId})
                Return Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    ' Adds the @id + 18 characteristic params shared by the authoring_study
    ' INSERT and UPDATE statements.
    Private Shared Sub AddAuthoringStudyParams(cmd As NpgsqlCommand, study As AuthoringStudy)
        cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = study.AuthoringStudyId})
        cmd.Parameters.Add(New NpgsqlParameter("label", NpgsqlDbType.Text) With {.Value = If(study.Label, "")})
        cmd.Parameters.Add(New NpgsqlParameter("source_kind", NpgsqlDbType.Text) With {.Value = If(study.SourceKind, "blank")})
        cmd.Parameters.Add(New NpgsqlParameter("source_ref", NpgsqlDbType.Text) With {.Value = NullIfEmpty(study.SourceRef)})
        AddTextParam(cmd, "brief_title", study.BriefTitle)
        AddTextParam(cmd, "official_title", study.OfficialTitle)
        AddTextParam(cmd, "overall_status", study.OverallStatus)
        AddTextParam(cmd, "phase", study.Phase)
        AddTextParam(cmd, "study_type", study.StudyType)
        AddNullableDateParam(cmd, "start_date", study.StartDate)
        AddNullableDateParam(cmd, "completion_date", study.CompletionDate)
        AddNullableDateParam(cmd, "primary_completion_date", study.PrimaryCompletionDate)
        AddNullableIntParam(cmd, "enrollment", study.Enrollment)
        AddTextParam(cmd, "enrollment_type", study.EnrollmentType)
        AddTextParam(cmd, "source", study.Source)
        AddTextParam(cmd, "why_stopped", study.WhyStopped)
        AddTextParam(cmd, "brief_summary", study.BriefSummary)
        cmd.Parameters.Add(New NpgsqlParameter("conditions", NpgsqlDbType.Array Or NpgsqlDbType.Text) With {
                .Value = If(study.Conditions, New List(Of String)()).ToArray()})
        cmd.Parameters.Add(New NpgsqlParameter("interventions", NpgsqlDbType.Jsonb) With {
                .Value = SerializeInterventions(study.Interventions)})
    End Sub

    ' Builds an UPSERT into authoring_eligibility for one study.
    Private Shared Sub BuildAuthoringEligibilityUpsert(
            cmd As NpgsqlCommand, authoringStudyId As Guid, eligibility As AuthoringEligibility)
        cmd.CommandText = "
INSERT INTO public.authoring_eligibility (
    authoring_study_id, criteria, gender, minimum_age, maximum_age,
    healthy_volunteers, sampling_method, population, adult, child, older_adult
) VALUES (
    @id, @criteria, @gender, @minimum_age, @maximum_age,
    @healthy_volunteers, @sampling_method, @population, @adult, @child, @older_adult
)
ON CONFLICT (authoring_study_id) DO UPDATE SET
    criteria           = excluded.criteria,
    gender             = excluded.gender,
    minimum_age        = excluded.minimum_age,
    maximum_age        = excluded.maximum_age,
    healthy_volunteers = excluded.healthy_volunteers,
    sampling_method    = excluded.sampling_method,
    population         = excluded.population,
    adult              = excluded.adult,
    child              = excluded.child,
    older_adult        = excluded.older_adult"
        cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = authoringStudyId})
        Dim e = If(eligibility, New AuthoringEligibility())
        AddTextParam(cmd, "criteria", e.Criteria)
        AddTextParam(cmd, "gender", e.Gender)
        AddTextParam(cmd, "minimum_age", e.MinimumAge)
        AddTextParam(cmd, "maximum_age", e.MaximumAge)
        AddTextParam(cmd, "healthy_volunteers", e.HealthyVolunteers)
        AddTextParam(cmd, "sampling_method", e.SamplingMethod)
        AddTextParam(cmd, "population", e.Population)
        AddNullableBoolParam(cmd, "adult", e.Adult)
        AddNullableBoolParam(cmd, "child", e.Child)
        AddNullableBoolParam(cmd, "older_adult", e.OlderAdult)
    End Sub

    Private Shared Async Function TouchAuthoringStudyAsync(
            conn As NpgsqlConnection,
            tx As NpgsqlTransaction,
            authoringStudyId As Guid,
            userId As Guid,
            cancellationToken As CancellationToken) As Task
        Using cmd = conn.CreateCommand()
            cmd.Transaction = tx
            cmd.CommandText = "UPDATE public.authoring_study SET updated_at = now(), last_updated_by = @uid WHERE authoring_study_id = @id"
            cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = authoringStudyId})
            cmd.Parameters.Add(New NpgsqlParameter("uid", NpgsqlDbType.Uuid) With {.Value = userId})
            Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
        End Using
    End Function

    Private Shared Function ReadAuthoringStudy(reader As NpgsqlDataReader) As AuthoringStudy
        Dim conditions As List(Of String) =
                If(reader.IsDBNull(19),
                   New List(Of String)(),
                   reader.GetFieldValue(Of String())(19).ToList())
        Dim interventions = DeserializeInterventions(
                If(reader.IsDBNull(20), "[]", reader.GetString(20))).ToList()
        Return New AuthoringStudy With {
                .AuthoringStudyId = reader.GetGuid(0),
                .Label = reader.GetString(1),
                .SourceKind = reader.GetString(2),
                .SourceRef = If(reader.IsDBNull(3), "", reader.GetString(3)),
                .CreatedAt = reader.GetFieldValue(Of DateTimeOffset)(4),
                .UpdatedAt = reader.GetFieldValue(Of DateTimeOffset)(5),
                .BriefTitle = ReadOutputString(reader, 6),
                .OfficialTitle = ReadOutputString(reader, 7),
                .OverallStatus = ReadOutputString(reader, 8),
                .Phase = ReadOutputString(reader, 9),
                .StudyType = ReadOutputString(reader, 10),
                .StartDate = ReadNullableDate(reader, 11),
                .CompletionDate = ReadNullableDate(reader, 12),
                .PrimaryCompletionDate = ReadNullableDate(reader, 13),
                .Enrollment = ReadNullableInt32(reader, 14),
                .EnrollmentType = ReadOutputString(reader, 15),
                .Source = ReadOutputString(reader, 16),
                .WhyStopped = ReadOutputString(reader, 17),
                .BriefSummary = ReadOutputString(reader, 18),
                .Conditions = conditions,
                .Interventions = interventions,
                .CreatedBy = ReadNullableGuid(reader, 21),
                .LastUpdatedBy = ReadNullableGuid(reader, 22),
                .StudyId = ReadOutputString(reader, 23)}
    End Function

    Private Shared Function ReadNullableGuid(reader As NpgsqlDataReader, ordinal As Integer) As Guid?
        Return If(reader.IsDBNull(ordinal), CType(Nothing, Guid?), reader.GetGuid(ordinal))
    End Function

    ' Plain string read for output-DB (authoring_*) text columns. Unlike
    ' ReadStringOrEmpty this does NOT strip backslash-escapes — those are an
    ' AACT markdown artefact, and user-authored text must round-trip verbatim.
    Private Shared Function ReadOutputString(reader As NpgsqlDataReader, ordinal As Integer) As String
        Return If(reader.IsDBNull(ordinal), "", reader.GetString(ordinal))
    End Function

    ' ============ Authoring Analysis (similarity + clustering) ============

    Public Async Function FindSimilarStudiesAsync(
            queryVector As IReadOnlyList(Of Single),
            limit As Integer,
            cancellationToken As CancellationToken,
            Optional filterPhase As String = "",
            Optional filterStudyType As String = "") As Task(Of IReadOnlyList(Of SimilarStudy)) _
            Implements IPostgresGateway.FindSimilarStudiesAsync
        If queryVector Is Nothing OrElse queryVector.Count = 0 Then
            Return Array.Empty(Of SimilarStudy)()
        End If
        Dim cappedLimit = Math.Min(Math.Max(limit, 1), 500)
        Dim phaseFilter = If(filterPhase, "")
        Dim typeFilter = If(filterStudyType, "")

        ' Served by the HNSW index ix_eligibility_study_embedding_hnsw (vector_cosine_ops,
        ' added in V8) - the `<=>` operator in the ORDER BY is what lets the planner use
        ' it. Measured at 2.6-4 ms warm over a 281k-vector corpus.
        '
        ' NOTE: this comment previously claimed "exact KNN - no index". That was wrong -
        ' the index predates the claim - and it sent at least one investigation chasing a
        ' scan that does not exist. If you change the ORDER BY expression so it no longer
        ' matches the index's operator class, the query silently degrades to a real exact
        ' scan and this becomes a full pass over every embedding.
        '
        ' The query vector is bound as text and cast to `vector`, which avoids a
        ' Pgvector.Npgsql type-handler dependency.
        '
        ' The phase / study_type filters use a "empty string disables" pattern
        ' inside the WHERE clause, so a single SQL shape covers all four
        ' filter combinations without dynamic string concatenation.
        Const Sql As String = "
SELECT em.nct_id, d.brief_title, d.phase, d.study_type, d.overall_status,
       d.brief_summary,
       1 - (em.embedding <=> @q::vector) AS similarity
FROM public.eligibility_study_embedding em
JOIN public.eligibility_study_detail d ON d.nct_id = em.nct_id
WHERE EXISTS (SELECT 1 FROM public.eligibility e WHERE e.nct_id = em.nct_id)
  AND (@filterPhase = '' OR d.phase = @filterPhase)
  AND (@filterStudyType = '' OR d.study_type = @filterStudyType)
ORDER BY em.embedding <=> @q::vector
LIMIT @limit"

        Dim result As New List(Of SimilarStudy)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("q", NpgsqlDbType.Text) With {.Value = FormatVector(queryVector)})
                cmd.Parameters.Add(New NpgsqlParameter("limit", NpgsqlDbType.Integer) With {.Value = cappedLimit})
                cmd.Parameters.Add(New NpgsqlParameter("filterPhase", NpgsqlDbType.Text) With {.Value = phaseFilter})
                cmd.Parameters.Add(New NpgsqlParameter("filterStudyType", NpgsqlDbType.Text) With {.Value = typeFilter})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(New SimilarStudy(
                                nctId:=reader.GetString(0),
                                briefTitle:=ReadOutputString(reader, 1),
                                phase:=ReadOutputString(reader, 2),
                                studyType:=ReadOutputString(reader, 3),
                                overallStatus:=ReadOutputString(reader, 4),
                                briefSummary:=ReadOutputString(reader, 5),
                                similarity:=reader.GetDouble(6)))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function FindSimilarTrialsToAsync(
            nctId As String,
            limit As Integer,
            matchPhase As Boolean,
            matchStudyType As Boolean,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of SimilarStudy)) _
            Implements IPostgresGateway.FindSimilarTrialsToAsync

        If String.IsNullOrWhiteSpace(nctId) Then Return Nothing
        Dim cappedLimit = Math.Min(Math.Max(limit, 1), 500)

        ' Single round trip: a CTE pulls the source trial's vector + phase +
        ' study_type, then the main query KNN-scans against the corpus while
        ' applying the optional same-phase / same-type filters. Self-exclusion
        ' lives in the WHERE. If the CTE returns no row (no embedding for the
        ' source), the join collapses to empty and we surface that to the
        ' caller as a separate code path below.
        Const ProbeSql As String = "
SELECT 1 FROM public.eligibility_study_embedding WHERE nct_id = @nct_id"

        Const Sql As String = "
WITH src AS (
    SELECT em.embedding AS qv,
           COALESCE(d.phase, '')      AS sphase,
           COALESCE(d.study_type, '') AS stype
    FROM public.eligibility_study_embedding em
    LEFT JOIN public.eligibility_study_detail d ON d.nct_id = em.nct_id
    WHERE em.nct_id = @nct_id
)
SELECT em.nct_id, d.brief_title, d.phase, d.study_type, d.overall_status,
       d.brief_summary,
       1 - (em.embedding <=> src.qv) AS similarity
FROM public.eligibility_study_embedding em
JOIN public.eligibility_study_detail d ON d.nct_id = em.nct_id
CROSS JOIN src
WHERE em.nct_id <> @nct_id
  AND EXISTS (SELECT 1 FROM public.eligibility e WHERE e.nct_id = em.nct_id)
  AND (NOT @match_phase OR COALESCE(d.phase, '') = src.sphase)
  AND (NOT @match_study_type OR COALESCE(d.study_type, '') = src.stype)
ORDER BY em.embedding <=> src.qv
LIMIT @limit"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            ' Probe first: distinguish "source has no embedding" (return
            ' Nothing) from "embedded but no matches" (empty list).
            Using probe = conn.CreateCommand()
                probe.CommandText = ProbeSql
                probe.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                Dim probeResult = Await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                If probeResult Is Nothing OrElse probeResult Is DBNull.Value Then
                    Return Nothing
                End If
            End Using

            Dim result As New List(Of SimilarStudy)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                cmd.Parameters.Add(New NpgsqlParameter("limit", NpgsqlDbType.Integer) With {.Value = cappedLimit})
                cmd.Parameters.Add(New NpgsqlParameter("match_phase", NpgsqlDbType.Boolean) With {.Value = matchPhase})
                cmd.Parameters.Add(New NpgsqlParameter("match_study_type", NpgsqlDbType.Boolean) With {.Value = matchStudyType})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(New SimilarStudy(
                                nctId:=reader.GetString(0),
                                briefTitle:=ReadOutputString(reader, 1),
                                phase:=ReadOutputString(reader, 2),
                                studyType:=ReadOutputString(reader, 3),
                                overallStatus:=ReadOutputString(reader, 4),
                                briefSummary:=ReadOutputString(reader, 5),
                                similarity:=reader.GetDouble(6)))
                    End While
                End Using
            End Using
            Return result
        End Using
    End Function

    Public Async Function ClusterCommonCriteriaAsync(
            nctIds As IReadOnlyList(Of String),
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of CriterionCluster)) _
            Implements IPostgresGateway.ClusterCommonCriteriaAsync
        If nctIds Is Nothing OrElse nctIds.Count = 0 Then
            Return Array.Empty(Of CriterionCluster)()
        End If

        ' Concept identity: the UMLS concept_code when resolved, else a
        ' 'concept:<lowercased text>' fallback so unresolved criteria still
        ' cluster (authoring specification §3.4.2).
        Const Sql As String = "
SELECT criterion,
       COALESCE(NULLIF(concept_code, ''), 'concept:' || lower(concept)) AS group_key,
       (concept_code IS NOT NULL AND concept_code <> '') AS resolved,
       max(concept) AS concept,
       COALESCE(max(concept_code), '') AS concept_code,
       -- Every distinct semantic type in the group, not one arbitrary pick.
       -- max() over text returns the lexicographically largest string, which was
       -- harmless while a group meant one CUI. It is not: the group key falls
       -- back to lowercased concept TEXT for unresolved criteria, so one cluster
       -- can span several CUIs with genuinely different semantic types.
       COALESCE((SELECT string_agg(DISTINCT s, ', ' ORDER BY s)
                   FROM unnest(array_agg(semantic_type)) AS s
                  WHERE s IS NOT NULL AND s <> ''), '') AS semantic_type,
       count(DISTINCT nct_id) AS study_count,
       count(*) AS record_count
FROM public.eligibility
WHERE nct_id = ANY(@ids)
GROUP BY criterion,
         COALESCE(NULLIF(concept_code, ''), 'concept:' || lower(concept)),
         (concept_code IS NOT NULL AND concept_code <> '')
ORDER BY study_count DESC, record_count DESC"

        Dim result As New List(Of CriterionCluster)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("ids", NpgsqlDbType.Array Or NpgsqlDbType.Text) With {
                        .Value = nctIds.ToArray()})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(New CriterionCluster(
                                criterion:=ReadOutputString(reader, 0),
                                groupKey:=ReadOutputString(reader, 1),
                                resolved:=Not reader.IsDBNull(2) AndAlso reader.GetBoolean(2),
                                concept:=ReadOutputString(reader, 3),
                                conceptCode:=ReadOutputString(reader, 4),
                                semanticType:=ReadOutputString(reader, 5),
                                studyCount:=CInt(reader.GetInt64(6)),
                                recordCount:=CInt(reader.GetInt64(7))))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function GetClusterRecordsAsync(
            nctIds As IReadOnlyList(Of String),
            criterion As String,
            groupKey As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of EligibilityRow)) _
            Implements IPostgresGateway.GetClusterRecordsAsync
        If nctIds Is Nothing OrElse nctIds.Count = 0 Then
            Return Array.Empty(Of EligibilityRow)()
        End If

        Const Sql As String = "
SELECT id, nct_id, criterion, domain, concept, concept_code, semantic_type,
       qualifier, time_window, original_text, umls_name, match_score, match_source, created_at
FROM public.eligibility
WHERE nct_id = ANY(@ids) AND criterion = @criterion
  AND COALESCE(NULLIF(concept_code, ''), 'concept:' || lower(concept)) = @group_key
ORDER BY nct_id, id"

        Dim result As New List(Of EligibilityRow)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("ids", NpgsqlDbType.Array Or NpgsqlDbType.Text) With {
                        .Value = nctIds.ToArray()})
                cmd.Parameters.Add(New NpgsqlParameter("criterion", NpgsqlDbType.Text) With {.Value = If(criterion, "")})
                cmd.Parameters.Add(New NpgsqlParameter("group_key", NpgsqlDbType.Text) With {.Value = If(groupKey, "")})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(New EligibilityRow(
                                id:=reader.GetInt64(0),
                                nctId:=reader.GetString(1),
                                criterion:=ReadOutputString(reader, 2),
                                domain:=ReadOutputString(reader, 3),
                                concept:=ReadOutputString(reader, 4),
                                conceptCode:=ReadOutputString(reader, 5),
                                semanticType:=ReadOutputString(reader, 6),
                                qualifier:=ReadOutputString(reader, 7),
                                timeWindow:=ReadOutputString(reader, 8),
                                originalText:=ReadOutputString(reader, 9),
                                umlsName:=ReadOutputString(reader, 10),
                                matchScore:=If(reader.IsDBNull(11), 0.0, CDbl(reader.GetDecimal(11))),
                                matchSource:=ReadOutputString(reader, 12),
                                createdAt:=reader.GetFieldValue(Of DateTimeOffset)(13)))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function GetStudiesToEmbedAsync(
            model As String,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of StudyEmbeddingInput)) _
            Implements IPostgresGateway.GetStudiesToEmbedAsync
        Const Sql As String = "
SELECT d.nct_id, d.brief_title, d.official_title, d.brief_summary, d.conditions, d.interventions
FROM public.eligibility_study_detail d
WHERE EXISTS (SELECT 1 FROM public.eligibility e WHERE e.nct_id = d.nct_id)
  AND NOT EXISTS (SELECT 1 FROM public.eligibility_study_embedding em
                  WHERE em.nct_id = d.nct_id AND em.model = @model)
ORDER BY d.nct_id"

        Dim result As New List(Of StudyEmbeddingInput)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("model", NpgsqlDbType.Text) With {.Value = If(model, "")})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        Dim conditions As IReadOnlyList(Of String) =
                                If(reader.IsDBNull(4),
                                   CType(Array.Empty(Of String)(), IReadOnlyList(Of String)),
                                   reader.GetFieldValue(Of String())(4))
                        Dim interventions = DeserializeInterventions(
                                If(reader.IsDBNull(5), "[]", reader.GetString(5)))
                        result.Add(New StudyEmbeddingInput(
                                nctId:=reader.GetString(0),
                                briefTitle:=ReadOutputString(reader, 1),
                                officialTitle:=ReadOutputString(reader, 2),
                                briefSummary:=ReadOutputString(reader, 3),
                                conditions:=conditions,
                                interventions:=interventions))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function CountStudiesToEmbedAsync(
            model As String,
            cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.CountStudiesToEmbedAsync
        Const Sql As String = "
SELECT count(*)
FROM public.eligibility_study_detail d
WHERE EXISTS (SELECT 1 FROM public.eligibility e WHERE e.nct_id = d.nct_id)
  AND NOT EXISTS (SELECT 1 FROM public.eligibility_study_embedding em
                  WHERE em.nct_id = d.nct_id AND em.model = @model)"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("model", NpgsqlDbType.Text) With {.Value = If(model, "")})
                Dim scalar = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                Return If(scalar Is Nothing OrElse scalar Is DBNull.Value, 0, Convert.ToInt32(scalar))
            End Using
        End Using
    End Function

    Public Async Function GetStudyEmbeddingInputAsync(
            nctId As String,
            cancellationToken As CancellationToken) As Task(Of StudyEmbeddingInput) _
            Implements IPostgresGateway.GetStudyEmbeddingInputAsync
        If String.IsNullOrWhiteSpace(nctId) Then Return Nothing

        ' Same projection as GetStudiesToEmbedAsync, scoped to one study and
        ' without the already-embedded anti-join — the pipeline always UPSERTs.
        Const Sql As String = "
SELECT d.nct_id, d.brief_title, d.official_title, d.brief_summary, d.conditions, d.interventions
FROM public.eligibility_study_detail d
WHERE d.nct_id = @nct_id"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    If Not Await reader.ReadAsync(cancellationToken).ConfigureAwait(False) Then
                        Return Nothing
                    End If
                    Dim conditions As IReadOnlyList(Of String) =
                            If(reader.IsDBNull(4),
                               CType(Array.Empty(Of String)(), IReadOnlyList(Of String)),
                               reader.GetFieldValue(Of String())(4))
                    Dim interventions = DeserializeInterventions(
                            If(reader.IsDBNull(5), "[]", reader.GetString(5)))
                    Return New StudyEmbeddingInput(
                            nctId:=reader.GetString(0),
                            briefTitle:=ReadOutputString(reader, 1),
                            officialTitle:=ReadOutputString(reader, 2),
                            briefSummary:=ReadOutputString(reader, 3),
                            conditions:=conditions,
                            interventions:=interventions)
                End Using
            End Using
        End Using
    End Function

    Public Async Function UpsertStudyEmbeddingAsync(
            nctId As String,
            embedding As IReadOnlyList(Of Single),
            model As String,
            sourceText As String,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.UpsertStudyEmbeddingAsync
        If String.IsNullOrWhiteSpace(nctId) Then
            Throw New ArgumentException("nctId must be non-empty", NameOf(nctId))
        End If
        If embedding Is Nothing OrElse embedding.Count = 0 Then
            Throw New ArgumentException("embedding must be non-empty", NameOf(embedding))
        End If

        Const Sql As String = "
INSERT INTO public.eligibility_study_embedding (nct_id, embedding, model, source_text, embedded_at)
VALUES (@nct_id, @embedding::vector, @model, @source_text, now())
ON CONFLICT (nct_id) DO UPDATE SET
    embedding   = excluded.embedding,
    model       = excluded.model,
    source_text = excluded.source_text,
    embedded_at = now()"

        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                cmd.Parameters.Add(New NpgsqlParameter("nct_id", NpgsqlDbType.Text) With {.Value = nctId})
                cmd.Parameters.Add(New NpgsqlParameter("embedding", NpgsqlDbType.Text) With {.Value = FormatVector(embedding)})
                cmd.Parameters.Add(New NpgsqlParameter("model", NpgsqlDbType.Text) With {.Value = If(model, "")})
                cmd.Parameters.Add(New NpgsqlParameter("source_text", NpgsqlDbType.Text) With {.Value = NullIfEmpty(sourceText)})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    ' Formats a float vector as the pgvector text literal `[v1,v2,...]`.
    ' Round-trip ("R") format + invariant culture so the decimal point and
    ' precision survive regardless of host locale.
    Private Shared Function FormatVector(vector As IReadOnlyList(Of Single)) As String
        Dim sb As New StringBuilder(vector.Count * 12 + 2)
        sb.Append("["c)
        For i As Integer = 0 To vector.Count - 1
            If i > 0 Then sb.Append(","c)
            sb.Append(vector(i).ToString("R", CultureInfo.InvariantCulture))
        Next
        sb.Append("]"c)
        Return sb.ToString()
    End Function

    Public Async Function GetEmbeddingStatsAsync(cancellationToken As CancellationToken) As Task(Of EmbeddingStats) _
            Implements IPostgresGateway.GetEmbeddingStatsAsync
        Const Sql As String = "
SELECT model, count(*) AS n
FROM public.eligibility_study_embedding
GROUP BY model
ORDER BY n DESC"
        Dim models As New List(Of EmbeddingModelCount)()
        Dim total As Long = 0
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = Sql
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        Dim model = If(reader.IsDBNull(0), "", reader.GetString(0))
                        Dim n = reader.GetInt64(1)
                        models.Add(New EmbeddingModelCount(model, n))
                        total += n
                    End While
                End Using
            End Using
        End Using
        Return New EmbeddingStats(total, models)
    End Function

    Public Async Function ClearStudyEmbeddingsAsync(cancellationToken As CancellationToken) As Task(Of Long) _
            Implements IPostgresGateway.ClearStudyEmbeddingsAsync
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Dim removed As Long
            Using countCmd = conn.CreateCommand()
                countCmd.CommandText = "SELECT count(*) FROM public.eligibility_study_embedding"
                removed = CLng(Await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False))
            End Using
            Using truncCmd = conn.CreateCommand()
                ' TRUNCATE (not DELETE) - it is the fast, space-reclaiming clear for the
                ' full ~280k-row index and the import restores a fresh set right after.
                truncCmd.CommandText = "TRUNCATE TABLE public.eligibility_study_embedding"
                Await truncCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
            Return removed
        End Using
    End Function

    ' ============ Authentication / users (output DB, app_user) ============

    Public Async Function CountUsersAsync(cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.CountUsersAsync
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT count(*) FROM public.app_user"
                Dim scalar = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                Return CInt(CLng(scalar))
            End Using
        End Using
    End Function

    Public Async Function CountOwnersAsync(cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.CountOwnersAsync
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT count(*) FROM public.app_user WHERE role = @owner AND is_active"
                cmd.Parameters.Add(New NpgsqlParameter("owner", NpgsqlDbType.Text) With {.Value = Roles.Owner})
                Dim scalar = Await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False)
                Return CInt(CLng(scalar))
            End Using
        End Using
    End Function

    Public Async Function GetUserByUserNameAsync(
            userName As String,
            cancellationToken As CancellationToken) As Task(Of AppUser) _
            Implements IPostgresGateway.GetUserByUserNameAsync
        If String.IsNullOrWhiteSpace(userName) Then Return Nothing
        Return Await QuerySingleUserAsync(
                "lower(user_name) = lower(@v)", "v", userName.Trim(), cancellationToken).ConfigureAwait(False)
    End Function

    Public Async Function GetUserByEmailAsync(
            email As String,
            cancellationToken As CancellationToken) As Task(Of AppUser) _
            Implements IPostgresGateway.GetUserByEmailAsync
        If String.IsNullOrWhiteSpace(email) Then Return Nothing
        Return Await QuerySingleUserAsync(
                "lower(email) = lower(@v)", "v", email.Trim(), cancellationToken).ConfigureAwait(False)
    End Function

    Public Async Function GetUserByGoogleSubjectAsync(
            googleSubject As String,
            cancellationToken As CancellationToken) As Task(Of AppUser) _
            Implements IPostgresGateway.GetUserByGoogleSubjectAsync
        If String.IsNullOrWhiteSpace(googleSubject) Then Return Nothing
        Return Await QuerySingleUserAsync(
                "google_subject = @v", "v", googleSubject.Trim(), cancellationToken).ConfigureAwait(False)
    End Function

    Public Async Function GetUserAsync(
            userId As Guid,
            cancellationToken As CancellationToken) As Task(Of AppUser) _
            Implements IPostgresGateway.GetUserAsync
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = AppUserSelect & " WHERE user_id = @id"
                cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = userId})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    If Not Await reader.ReadAsync(cancellationToken).ConfigureAwait(False) Then Return Nothing
                    Return ReadAppUser(reader)
                End Using
            End Using
        End Using
    End Function

    Public Async Function ListUsersAsync(
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of AppUser)) _
            Implements IPostgresGateway.ListUsersAsync
        Dim result As New List(Of AppUser)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = AppUserSelect & " ORDER BY user_name"
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        result.Add(ReadAppUser(reader))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Async Function CreateUserAsync(
            user As AppUser,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.CreateUserAsync
        If user Is Nothing Then Throw New ArgumentNullException(NameOf(user))
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO public.app_user (
    user_id, user_name, email, display_name, role,
    password_hash, google_subject, picture_url, is_active
) VALUES (
    @id, @user_name, @email, @display_name, @role,
    @password_hash, @google_subject, @picture_url, @is_active
)"
                cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = user.UserId})
                cmd.Parameters.Add(New NpgsqlParameter("user_name", NpgsqlDbType.Text) With {.Value = If(user.UserName, "")})
                cmd.Parameters.Add(New NpgsqlParameter("email", NpgsqlDbType.Text) With {.Value = If(user.Email, "")})
                cmd.Parameters.Add(New NpgsqlParameter("display_name", NpgsqlDbType.Text) With {.Value = If(user.DisplayName, "")})
                cmd.Parameters.Add(New NpgsqlParameter("role", NpgsqlDbType.Text) With {.Value = Roles.ToRoleName(user.Role)})
                cmd.Parameters.Add(New NpgsqlParameter("password_hash", NpgsqlDbType.Text) With {.Value = NullIfEmpty(user.PasswordHash)})
                cmd.Parameters.Add(New NpgsqlParameter("google_subject", NpgsqlDbType.Text) With {.Value = NullIfEmpty(user.GoogleSubject)})
                cmd.Parameters.Add(New NpgsqlParameter("picture_url", NpgsqlDbType.Text) With {.Value = NullIfEmpty(user.PictureUrl)})
                cmd.Parameters.Add(New NpgsqlParameter("is_active", NpgsqlDbType.Boolean) With {.Value = user.IsActive})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    Public Async Function UpdateUserRoleAsync(
            userId As Guid,
            role As Role,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.UpdateUserRoleAsync
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "UPDATE public.app_user SET role = @role, updated_at = now() WHERE user_id = @id"
                cmd.Parameters.Add(New NpgsqlParameter("role", NpgsqlDbType.Text) With {.Value = Roles.ToRoleName(role)})
                cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = userId})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    Public Async Function UpdateUserPasswordHashAsync(
            userId As Guid,
            passwordHash As String,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.UpdateUserPasswordHashAsync
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "UPDATE public.app_user SET password_hash = @hash, updated_at = now() WHERE user_id = @id"
                cmd.Parameters.Add(New NpgsqlParameter("hash", NpgsqlDbType.Text) With {.Value = NullIfEmpty(passwordHash)})
                cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = userId})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    Public Async Function LinkGoogleSubjectAsync(
            userId As Guid,
            googleSubject As String,
            pictureUrl As String,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.LinkGoogleSubjectAsync
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                ' Keep an existing picture if the new one is blank.
                cmd.CommandText = "
UPDATE public.app_user SET
    google_subject = @sub,
    picture_url    = COALESCE(NULLIF(@pic, ''), picture_url),
    updated_at     = now()
WHERE user_id = @id"
                cmd.Parameters.Add(New NpgsqlParameter("sub", NpgsqlDbType.Text) With {.Value = NullIfEmpty(googleSubject)})
                cmd.Parameters.Add(New NpgsqlParameter("pic", NpgsqlDbType.Text) With {.Value = If(pictureUrl, "")})
                cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = userId})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    Public Async Function RecordLoginAsync(
            userId As Guid,
            whenUtc As DateTimeOffset,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.RecordLoginAsync
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "UPDATE public.app_user SET last_login_at = @when WHERE user_id = @id"
                cmd.Parameters.Add(New NpgsqlParameter("when", NpgsqlDbType.TimestampTz) With {.Value = whenUtc})
                cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = userId})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    Public Async Function DeleteUserAsync(
            userId As Guid,
            cancellationToken As CancellationToken) As Task(Of Integer) _
            Implements IPostgresGateway.DeleteUserAsync
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "DELETE FROM public.app_user WHERE user_id = @id"
                cmd.Parameters.Add(New NpgsqlParameter("id", NpgsqlDbType.Uuid) With {.Value = userId})
                Return Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    Private Const AppUserSelect As String =
        "SELECT user_id, user_name, email, display_name, role, password_hash, " &
        "google_subject, picture_url, is_active, created_at, updated_at, last_login_at " &
        "FROM public.app_user"

    Private Async Function QuerySingleUserAsync(
            whereClause As String,
            paramName As String,
            paramValue As String,
            cancellationToken As CancellationToken) As Task(Of AppUser)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = AppUserSelect & " WHERE " & whereClause
                cmd.Parameters.Add(New NpgsqlParameter(paramName, NpgsqlDbType.Text) With {.Value = paramValue})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    If Not Await reader.ReadAsync(cancellationToken).ConfigureAwait(False) Then Return Nothing
                    Return ReadAppUser(reader)
                End Using
            End Using
        End Using
    End Function

    Private Shared Function ReadAppUser(reader As NpgsqlDataReader) As AppUser
        Dim role As Role
        Roles.TryParseRole(reader.GetString(4), role)   ' defaults to Viewer on unknown
        Return New AppUser With {
                .UserId = reader.GetGuid(0),
                .UserName = reader.GetString(1),
                .Email = reader.GetString(2),
                .DisplayName = ReadOutputString(reader, 3),
                .Role = role,
                .PasswordHash = ReadOutputString(reader, 5),
                .GoogleSubject = ReadOutputString(reader, 6),
                .PictureUrl = ReadOutputString(reader, 7),
                .IsActive = reader.GetBoolean(8),
                .CreatedAt = reader.GetFieldValue(Of DateTimeOffset)(9),
                .UpdatedAt = reader.GetFieldValue(Of DateTimeOffset)(10),
                .LastLoginAt = If(reader.IsDBNull(11), CType(Nothing, DateTimeOffset?), reader.GetFieldValue(Of DateTimeOffset)(11))}
    End Function

    ' ============ Auditing (output DB, audit_log) ============

    Public Async Function InsertAuditAsync(
            entry As AuditEntry,
            cancellationToken As CancellationToken) As Task _
            Implements IPostgresGateway.InsertAuditAsync
        If entry Is Nothing Then Throw New ArgumentNullException(NameOf(entry))
        Dim occurredAt = If(entry.OccurredAt = Nothing, DateTimeOffset.UtcNow, entry.OccurredAt)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
INSERT INTO public.audit_log (
    occurred_at, user_id, user_label, action, entity_type, entity_id, detail
) VALUES (
    @occurred_at, @user_id, @user_label, @action, @entity_type, @entity_id, @detail
)"
                cmd.Parameters.Add(New NpgsqlParameter("occurred_at", NpgsqlDbType.TimestampTz) With {.Value = occurredAt})
                cmd.Parameters.Add(New NpgsqlParameter("user_id", NpgsqlDbType.Uuid) With {
                        .Value = If(entry.UserId.HasValue, CObj(entry.UserId.Value), CObj(DBNull.Value))})
                cmd.Parameters.Add(New NpgsqlParameter("user_label", NpgsqlDbType.Text) With {.Value = If(entry.UserLabel, "")})
                cmd.Parameters.Add(New NpgsqlParameter("action", NpgsqlDbType.Text) With {.Value = If(entry.Action, "")})
                cmd.Parameters.Add(New NpgsqlParameter("entity_type", NpgsqlDbType.Text) With {.Value = If(entry.EntityType, "")})
                ' entity_id is part of the btree index ix_audit_log_entity, whose row-size
                ' limit is ~2704 bytes. Cap it well under that so an oversized value (e.g. a
                ' batch rerun's comma-joined NCT list) can never abort the audit write -
                ' auditing is best-effort and must never throw. The full value belongs in
                ' the unindexed detail column; callers should put long lists there.
                cmd.Parameters.Add(New NpgsqlParameter("entity_id", NpgsqlDbType.Text) With {.Value = NullIfEmpty(TruncateForIndex(entry.EntityId))})
                cmd.Parameters.Add(New NpgsqlParameter("detail", NpgsqlDbType.Text) With {.Value = NullIfEmpty(entry.Detail)})
                Await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End Using
    End Function

    Public Async Function GetAuditLogAsync(
            filter As AuditLogFilter,
            page As Integer,
            pageSize As Integer,
            cancellationToken As CancellationToken) As Task(Of AuditLogPage) _
            Implements IPostgresGateway.GetAuditLogAsync
        Dim f = If(filter, New AuditLogFilter())
        Dim cappedPageSize = Math.Min(Math.Max(pageSize, 1), 200)
        Dim cappedPage = Math.Max(page, 1)
        Dim offset = (cappedPage - 1) * cappedPageSize

        Dim pageSql = AuditSelect & AuditWhereClause & "
ORDER BY occurred_at DESC, audit_id DESC
OFFSET @offset LIMIT @limit"
        Dim countSql = "SELECT COUNT(*) FROM public.audit_log" & AuditWhereClause

        Dim rows As New List(Of AuditEntry)
        Dim totalRows As Long = 0
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = pageSql
                AddAuditFilterParams(cmd, f)
                cmd.Parameters.Add(New NpgsqlParameter("offset", NpgsqlDbType.Integer) With {.Value = offset})
                cmd.Parameters.Add(New NpgsqlParameter("limit", NpgsqlDbType.Integer) With {.Value = cappedPageSize})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        rows.Add(ReadAuditEntry(reader))
                    End While
                End Using
            End Using

            Using countCmd = conn.CreateCommand()
                countCmd.CommandText = countSql
                AddAuditFilterParams(countCmd, f)
                totalRows = Convert.ToInt64(Await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False))
            End Using
        End Using

        Return New AuditLogPage(rows, totalRows, cappedPage, cappedPageSize)
    End Function

    ' Hard cap so an export can't materialise an unbounded result set.
    Private Const AuditExportLimit As Integer = 100000

    Public Async Function GetAuditLogForExportAsync(
            filter As AuditLogFilter,
            cancellationToken As CancellationToken) As Task(Of IReadOnlyList(Of AuditEntry)) _
            Implements IPostgresGateway.GetAuditLogForExportAsync
        Dim f = If(filter, New AuditLogFilter())
        Dim sql = AuditSelect & AuditWhereClause & "
ORDER BY occurred_at DESC, audit_id DESC
LIMIT @limit"

        Dim rows As New List(Of AuditEntry)
        Using conn = Await _outputDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = sql
                AddAuditFilterParams(cmd, f)
                cmd.Parameters.Add(New NpgsqlParameter("limit", NpgsqlDbType.Integer) With {.Value = AuditExportLimit})
                Using reader = Await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                    While Await reader.ReadAsync(cancellationToken).ConfigureAwait(False)
                        rows.Add(ReadAuditEntry(reader))
                    End While
                End Using
            End Using
        End Using
        Return rows
    End Function

    Private Const AuditSelect As String = "
SELECT audit_id, occurred_at, user_id, user_label, action, entity_type, entity_id, detail
FROM public.audit_log"

    ' Shared "empty disables" filter, mirroring SelectNextTrials / GetStudies.
    Private Const AuditWhereClause As String = "
WHERE (@user_search IS NULL OR user_label ILIKE @user_search)
  AND (@action IS NULL OR action = @action)
  AND (@from_ts IS NULL OR occurred_at >= @from_ts)
  AND (@to_ts   IS NULL OR occurred_at <= @to_ts)"

    Private Shared Sub AddAuditFilterParams(cmd As NpgsqlCommand, f As AuditLogFilter)
        Dim userParam As Object = If(String.IsNullOrWhiteSpace(f.UserSearch),
                                     CObj(DBNull.Value), "%" & f.UserSearch.Trim() & "%")
        cmd.Parameters.Add(New NpgsqlParameter("user_search", NpgsqlDbType.Text) With {.Value = userParam})
        cmd.Parameters.Add(New NpgsqlParameter("action", NpgsqlDbType.Text) With {.Value = NullIfEmpty(If(f.Action, "").Trim())})
        cmd.Parameters.Add(New NpgsqlParameter("from_ts", NpgsqlDbType.TimestampTz) With {
                .Value = If(f.FromUtc.HasValue, CObj(f.FromUtc.Value), CObj(DBNull.Value))})
        cmd.Parameters.Add(New NpgsqlParameter("to_ts", NpgsqlDbType.TimestampTz) With {
                .Value = If(f.ToUtc.HasValue, CObj(f.ToUtc.Value), CObj(DBNull.Value))})
    End Sub

    Private Shared Function ReadAuditEntry(reader As NpgsqlDataReader) As AuditEntry
        Return New AuditEntry With {
                .AuditId = reader.GetInt64(0),
                .OccurredAt = reader.GetFieldValue(Of DateTimeOffset)(1),
                .UserId = If(reader.IsDBNull(2), CType(Nothing, Guid?), reader.GetGuid(2)),
                .UserLabel = ReadOutputString(reader, 3),
                .Action = ReadOutputString(reader, 4),
                .EntityType = ReadOutputString(reader, 5),
                .EntityId = ReadOutputString(reader, 6),
                .Detail = ReadOutputString(reader, 7)}
    End Function

    ' ============ helpers ============

    Private Shared Function LoadEmbeddedMigration(resourceName As String) As String
        Dim asm = GetType(PostgresGateway).Assembly
        Using stream = asm.GetManifestResourceStream(resourceName)
            If stream Is Nothing Then
                Throw New InvalidOperationException(
                        $"Embedded migration resource '{resourceName}' not found. " &
                        "Ensure the .sql is <EmbeddedResource> with the correct LogicalName in EligibilityProcessing.Data.vbproj.")
            End If
            Using reader As New StreamReader(stream)
                Return reader.ReadToEnd()
            End Using
        End Using
    End Function

End Class
