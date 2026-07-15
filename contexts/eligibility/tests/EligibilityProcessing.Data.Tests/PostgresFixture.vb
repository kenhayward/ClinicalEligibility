Imports System.Threading
Imports System.Threading.Tasks
Imports EligibilityProcessing.Data
Imports Npgsql
Imports Testcontainers.PostgreSql
Imports Xunit

' xUnit ClassFixture that spins up a Postgres container, applies the V1
' migration, and exposes a ready-to-use PostgresGateway.
'
' If Docker is not available on the host (no daemon, no socket), the fixture
' captures the failure in SkipReason and every integration test calls
' Skip.If(SkipReason IsNot Nothing, ...). This keeps the suite green on
' developer machines without Docker while running fully in CI.
'
' The same container is reused for all tests in a class; each test calls
' ResetAsync() to TRUNCATE all four tables back to a clean slate.

Public NotInheritable Class PostgresFixture
    Implements IAsyncLifetime

    Private _container As PostgreSqlContainer
    Public Property DataSource As NpgsqlDataSource
    Public Property Gateway As PostgresGateway
    Public Property SkipReason As String
    Public Property ConnectionString As String

    Public Async Function InitializeAsync() As Task Implements IAsyncLifetime.InitializeAsync
        Try
            ' pgvector image (Postgres 16 + the vector extension) — migration
            ' V7 runs CREATE EXTENSION vector, which a stock postgres image
            ' cannot satisfy.
            _container = New PostgreSqlBuilder("pgvector/pgvector:pg16").Build()
            Await _container.StartAsync()

            ' Capture the unsanitised string (with password) for tests that
            ' need to spin up their own NpgsqlDataSource — NpgsqlDataSource.ConnectionString
            ' redacts the password by design.
            ConnectionString = _container.GetConnectionString()
            DataSource = NpgsqlDataSource.Create(ConnectionString)
            ' Single DB doubles as source + output in tests — we create the
            ' ctgov.eligibilities table ourselves so SelectNextTrials can be
            ' exercised against fixture-controlled data.
            Gateway = New PostgresGateway(
                    outputDataSource:=DataSource,
                    sourceDataSource:=DataSource)
            Await Gateway.EnsureSchemaAsync(CancellationToken.None)
            Await CreateSourceSchemaAsync()
        Catch ex As Exception
            SkipReason = $"Postgres test container could not start (Docker likely unavailable): {ex.GetType().Name}: {ex.Message}"
        End Try
    End Function

    Public Async Function DisposeAsync() As Task Implements IAsyncLifetime.DisposeAsync
        If DataSource IsNot Nothing Then Await DataSource.DisposeAsync()
        If _container IsNot Nothing Then Await _container.DisposeAsync()
    End Function

    ''' <summary>
    ''' Truncates every table to a known-empty state. Call at the start of each
    ''' test that mutates state.
    ''' </summary>
    Public Async Function ResetAsync() As Task
        Using conn = Await DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "TRUNCATE
                    public.eligibility,
                    public.eligibility_run,
                    public.eligibility_failed,
                    public.eligibility_umls_retry,
                    public.eligibility_study,
                    public.eligibility_study_detail,
                    public.authoring_study,
                    public.authoring_eligibility,
                    public.authoring_criterion,
                    public.authoring_criterion_source,
                    public.eligibility_study_embedding,
                    public.app_user,
                    public.audit_log,
                    umls.atom,
                    umls.concept,
                    umls.semantic_type,
                    umls.concept_normalization,
                    ctgov.eligibilities,
                    ctgov.studies,
                    ctgov.brief_summaries,
                    ctgov.conditions,
                    ctgov.interventions
                  RESTART IDENTITY"
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Public Async Function InsertSourceTrialAsync(nctId As String, criteria As String) As Task
        Using conn = Await DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "INSERT INTO ctgov.eligibilities (nct_id, criteria) VALUES (@n, @c)"
                cmd.Parameters.AddWithValue("n", nctId)
                cmd.Parameters.AddWithValue("c", If(CObj(criteria), CObj(DBNull.Value)))
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    ''' <summary>
    ''' Seeds a full ctgov.studies row for the Analysis-tab tests. Only the
    ''' columns the gateway actually reads are required; everything else
    ''' defaults to NULL or "".
    ''' </summary>
    Public Async Function InsertSourceStudyAsync(
            nctId As String,
            Optional briefTitle As String = "Test study",
            Optional officialTitle As String = "",
            Optional overallStatus As String = "Completed",
            Optional phase As String = "Phase 2",
            Optional studyType As String = "Interventional",
            Optional source As String = "Test Sponsor",
            Optional enrollment As Integer? = Nothing,
            Optional briefSummary As String = "") As Task
        Using conn = Await DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    INSERT INTO ctgov.studies
                        (nct_id, brief_title, official_title, overall_status, phase,
                         study_type, source, enrollment)
                    VALUES (@n, @bt, @ot, @st, @ph, @sty, @src, @en)"
                cmd.Parameters.AddWithValue("n", nctId)
                cmd.Parameters.AddWithValue("bt", briefTitle)
                cmd.Parameters.AddWithValue("ot", officialTitle)
                cmd.Parameters.AddWithValue("st", overallStatus)
                cmd.Parameters.AddWithValue("ph", phase)
                cmd.Parameters.AddWithValue("sty", studyType)
                cmd.Parameters.AddWithValue("src", source)
                cmd.Parameters.AddWithValue("en", If(enrollment.HasValue, CObj(enrollment.Value), CObj(DBNull.Value)))
                Await cmd.ExecuteNonQueryAsync()

                If Not String.IsNullOrEmpty(briefSummary) Then
                    Using cmd2 = conn.CreateCommand()
                        cmd2.CommandText = "INSERT INTO ctgov.brief_summaries (nct_id, description) VALUES (@n, @d)"
                        cmd2.Parameters.AddWithValue("n", nctId)
                        cmd2.Parameters.AddWithValue("d", briefSummary)
                        Await cmd2.ExecuteNonQueryAsync()
                    End Using
                End If
            End Using
        End Using
    End Function

    Public Async Function InsertSourceConditionAsync(nctId As String, name As String) As Task
        Using conn = Await DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "INSERT INTO ctgov.conditions (nct_id, name) VALUES (@n, @c)"
                cmd.Parameters.AddWithValue("n", nctId)
                cmd.Parameters.AddWithValue("c", name)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Public Async Function InsertSourceInterventionAsync(
            nctId As String, interventionType As String, name As String) As Task
        Using conn = Await DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    INSERT INTO ctgov.interventions (nct_id, intervention_type, name)
                    VALUES (@n, @t, @na)"
                cmd.Parameters.AddWithValue("n", nctId)
                cmd.Parameters.AddWithValue("t", interventionType)
                cmd.Parameters.AddWithValue("na", name)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    ''' <summary>
    ''' Extended ctgov.eligibilities row with structured columns. The base
    ''' <see cref="InsertSourceTrialAsync"/> only seeds (nct_id, criteria);
    ''' Analysis-tab tests need the rest of the columns populated.
    ''' </summary>
    Public Async Function InsertSourceEligibilityFullAsync(
            nctId As String,
            criteria As String,
            Optional gender As String = "All",
            Optional minimumAge As String = "18 Years",
            Optional maximumAge As String = "N/A",
            Optional healthyVolunteers As String = "No",
            Optional samplingMethod As String = "",
            Optional population As String = "",
            Optional adult As Boolean? = True,
            Optional child As Boolean? = False,
            Optional olderAdult As Boolean? = True) As Task
        Using conn = Await DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    INSERT INTO ctgov.eligibilities
                        (nct_id, criteria, gender, minimum_age, maximum_age,
                         healthy_volunteers, sampling_method, population,
                         adult, child, older_adult)
                    VALUES (@n, @c, @g, @min, @max, @hv, @sm, @pop, @a, @ch, @oa)
                    ON CONFLICT (nct_id) DO UPDATE SET
                        criteria = excluded.criteria,
                        gender = excluded.gender,
                        minimum_age = excluded.minimum_age,
                        maximum_age = excluded.maximum_age,
                        healthy_volunteers = excluded.healthy_volunteers,
                        sampling_method = excluded.sampling_method,
                        population = excluded.population,
                        adult = excluded.adult,
                        child = excluded.child,
                        older_adult = excluded.older_adult"
                cmd.Parameters.AddWithValue("n", nctId)
                cmd.Parameters.AddWithValue("c", If(CObj(criteria), CObj(DBNull.Value)))
                cmd.Parameters.AddWithValue("g", gender)
                cmd.Parameters.AddWithValue("min", minimumAge)
                cmd.Parameters.AddWithValue("max", maximumAge)
                cmd.Parameters.AddWithValue("hv", healthyVolunteers)
                cmd.Parameters.AddWithValue("sm", samplingMethod)
                cmd.Parameters.AddWithValue("pop", population)
                cmd.Parameters.AddWithValue("a", If(adult.HasValue, CObj(adult.Value), CObj(DBNull.Value)))
                cmd.Parameters.AddWithValue("ch", If(child.HasValue, CObj(child.Value), CObj(DBNull.Value)))
                cmd.Parameters.AddWithValue("oa", If(olderAdult.HasValue, CObj(olderAdult.Value), CObj(DBNull.Value)))
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Public Async Function CountEligibilityRowsAsync(nctId As String) As Task(Of Integer)
        Using conn = Await DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT COUNT(*) FROM public.eligibility WHERE nct_id = @n"
                cmd.Parameters.AddWithValue("n", nctId)
                Dim result = Await cmd.ExecuteScalarAsync()
                Return Convert.ToInt32(result)
            End Using
        End Using
    End Function

    Public Async Function CountStudyRowsAsync(nctId As String) As Task(Of Integer)
        Using conn = Await DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT COUNT(*) FROM public.eligibility_study WHERE nct_id = @n"
                cmd.Parameters.AddWithValue("n", nctId)
                Return Convert.ToInt32(Await cmd.ExecuteScalarAsync())
            End Using
        End Using
    End Function

    Public Async Function GetFailedTrialAttemptCountAsync(nctId As String) As Task(Of Integer)
        Using conn = Await DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT attempt_count FROM public.eligibility_failed WHERE nct_id = @n"
                cmd.Parameters.AddWithValue("n", nctId)
                Dim result = Await cmd.ExecuteScalarAsync()
                Return Convert.ToInt32(result)
            End Using
        End Using
    End Function

    ''' <summary>
    ''' Inserts one public.eligibility row. Authoring Analysis tests use this to
    ''' seed criterion records to cluster. An empty conceptCode persists as NULL.
    ''' </summary>
    Public Async Function InsertEligibilityRowAsync(
            nctId As String,
            criterion As String,
            concept As String,
            Optional domain As String = "Disease",
            Optional conceptCode As String = "",
            Optional semanticType As String = "",
            Optional originalText As String = "",
            Optional qualifier As String = "") As Task
        Using conn = Await DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    INSERT INTO public.eligibility
                        (nct_id, criterion, domain, concept, concept_code,
                         semantic_type, qualifier, original_text, match_score)
                    VALUES (@n, @cr, @dom, @con, @cc, @st, @q, @ot, 0)"
                cmd.Parameters.AddWithValue("n", nctId)
                cmd.Parameters.AddWithValue("cr", criterion)
                cmd.Parameters.AddWithValue("dom", domain)
                cmd.Parameters.AddWithValue("con", concept)
                cmd.Parameters.AddWithValue("cc", If(String.IsNullOrEmpty(conceptCode), CObj(DBNull.Value), conceptCode))
                cmd.Parameters.AddWithValue("st", If(String.IsNullOrEmpty(semanticType), CObj(DBNull.Value), semanticType))
                cmd.Parameters.AddWithValue("q", If(String.IsNullOrEmpty(qualifier), CObj(DBNull.Value), qualifier))
                cmd.Parameters.AddWithValue("ot", If(String.IsNullOrEmpty(originalText), CObj(DBNull.Value), originalText))
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    ''' <summary>
    ''' Inserts a minimal public.eligibility_study_detail snapshot row — enough
    ''' for the similarity-search join and the embed-backfill query.
    ''' </summary>
    Public Async Function InsertStudyDetailAsync(
            nctId As String,
            Optional briefTitle As String = "Test study",
            Optional phase As String = "Phase 2",
            Optional studyType As String = "Interventional") As Task
        Using conn = Await DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    INSERT INTO public.eligibility_study_detail
                        (nct_id, brief_title, phase, study_type)
                    VALUES (@n, @bt, @ph, @sty)"
                cmd.Parameters.AddWithValue("n", nctId)
                cmd.Parameters.AddWithValue("bt", briefTitle)
                cmd.Parameters.AddWithValue("ph", phase)
                cmd.Parameters.AddWithValue("sty", studyType)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    ''' <summary>
    ''' Inserts a rich public.eligibility_study_detail row for Analysis-tab
    ''' Search modal tests. Covers every column the search filter exposes.
    ''' </summary>
    Public Async Function InsertStudyDetailFullAsync(
            nctId As String,
            Optional briefTitle As String = "",
            Optional officialTitle As String = "",
            Optional overallStatus As String = "",
            Optional phase As String = "",
            Optional studyType As String = "",
            Optional source As String = "",
            Optional briefSummary As String = "",
            Optional conditions As String() = Nothing,
            Optional gender As String = "",
            Optional healthyVolunteers As String = "") As Task
        Using conn = Await DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    INSERT INTO public.eligibility_study_detail
                        (nct_id, brief_title, official_title, overall_status,
                         phase, study_type, source, brief_summary, conditions,
                         gender, healthy_volunteers)
                    VALUES (@n, @bt, @ot, @os, @ph, @sty, @src, @bs, @cond, @g, @hv)"
                cmd.Parameters.AddWithValue("n", nctId)
                cmd.Parameters.AddWithValue("bt", briefTitle)
                cmd.Parameters.AddWithValue("ot", officialTitle)
                cmd.Parameters.AddWithValue("os", overallStatus)
                cmd.Parameters.AddWithValue("ph", phase)
                cmd.Parameters.AddWithValue("sty", studyType)
                cmd.Parameters.AddWithValue("src", source)
                cmd.Parameters.AddWithValue("bs", briefSummary)
                cmd.Parameters.AddWithValue("cond", If(conditions, Array.Empty(Of String)()))
                cmd.Parameters.AddWithValue("g", gender)
                cmd.Parameters.AddWithValue("hv", healthyVolunteers)
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

    Private Async Function CreateSourceSchemaAsync() As Task
        ' Mirror enough of the AACT ctgov schema for the gateway's queries to
        ' work end-to-end. Real AACT has many more columns and tables; we only
        ' need the columns the pipeline and Analysis tab actually read.
        Using conn = Await DataSource.OpenConnectionAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    CREATE SCHEMA IF NOT EXISTS ctgov;

                    CREATE TABLE IF NOT EXISTS ctgov.eligibilities (
                        nct_id              text PRIMARY KEY,
                        criteria            text,
                        gender              text,
                        minimum_age         text,
                        maximum_age         text,
                        healthy_volunteers  text,
                        sampling_method     text,
                        population          text,
                        adult               boolean,
                        child               boolean,
                        older_adult         boolean
                    );

                    CREATE TABLE IF NOT EXISTS ctgov.studies (
                        nct_id                  text PRIMARY KEY,
                        brief_title             text,
                        official_title          text,
                        overall_status          text,
                        phase                   text,
                        study_type              text,
                        start_date              date,
                        completion_date         date,
                        primary_completion_date date,
                        enrollment              integer,
                        enrollment_type         text,
                        source                  text,
                        why_stopped             text
                    );

                    CREATE TABLE IF NOT EXISTS ctgov.brief_summaries (
                        nct_id      text PRIMARY KEY,
                        description text
                    );

                    CREATE TABLE IF NOT EXISTS ctgov.conditions (
                        id      bigserial PRIMARY KEY,
                        nct_id  text NOT NULL,
                        name    text
                    );

                    CREATE TABLE IF NOT EXISTS ctgov.interventions (
                        id                bigserial PRIMARY KEY,
                        nct_id            text NOT NULL,
                        intervention_type text,
                        name              text
                    );"
                Await cmd.ExecuteNonQueryAsync()
            End Using
        End Using
    End Function

End Class
