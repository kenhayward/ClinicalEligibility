' Configuration for Postgres access. Architecture section 3.2.
'
' Two connection strings:
'   - ConnectionStringSource: read-only credentials to AACT (ctgov.eligibilities)
'   - ConnectionStringOutput: read/write credentials to the eligibility output DB
'
' Both MUST be sourced from secret storage per spec section 6.5.

Public Class PostgresOptions

    Public Property ConnectionStringSource As String = ""
    Public Property ConnectionStringOutput As String = ""

    ''' <summary>
    ''' Upper bound on a single batch's StudyCount, applied as a clamp inside
    ''' SelectNextTrialsAsync (a value above this is capped, with a warning).
    ''' Guards against an oversized request (e.g. a fat-fingered 10000) turning
    ''' the source-table anti-join walk into a multi-minute scan. 0 disables the
    ''' clamp. Config: Postgres:MaxStudyCount.
    ''' </summary>
    Public Property MaxStudyCount As Integer = 5000

    ''' <summary>
    ''' Age (hours) beyond which a status='running' eligibility_study row is assumed
    ''' orphaned by a killed host and reconciled to 'interrupted' at web-host startup.
    ''' 0 or less disables the reconcile entirely.
    ''' <para>
    ''' MUST stay well above the worst-case legitimate duration of a single trial,
    ''' because the CLI (`elig run`) can process trials against the same database
    ''' concurrently and RunGate is an in-process lock that cannot see it. The age
    ''' threshold is the ONLY thing keeping the reconcile off a live CLI run's rows.
    ''' </para>
    ''' <para>
    ''' Worst case is ~2h on the shipped config: one LLM call is 3 attempts x
    ''' Llm:TimeoutSeconds (1200) + 2 x Llm:RetryDelaySeconds (5) = 3610s, and the
    ''' timeout is PER ATTEMPT (HttpClient.Timeout is InfiniteTimeSpan - see
    ''' CompositionRoot); reasoning escalation, on by default, can spend that budget
    ''' a second time. The 6h default is ~3x that, so it absorbs a large increase to
    ''' Llm:TimeoutSeconds / Llm:RetryCount without a false positive. Raise it if you
    ''' raise those. Config: Postgres:InterruptedStudyThresholdHours.
    ''' </para>
    ''' </summary>
    Public Property InterruptedStudyThresholdHours As Integer = 6

    ''' <summary>
    ''' Command timeout (seconds) applied to the SOURCE data source — the
    ''' read-only AACT connection that runs the trial-selection scan + the
    ''' exclusion-set COPY. The Npgsql default of 30s surfaces a slow selection
    ''' as "Exception while reading from stream" (a TimeoutException during a
    ''' reader read), which aborts the whole run; a larger ceiling lets a
    ''' legitimately large batch finish instead of dying. 0 means no timeout
    ''' (infinite). Config: Postgres:SourceCommandTimeoutSeconds.
    ''' </summary>
    Public Property SourceCommandTimeoutSeconds As Integer = 300

End Class
