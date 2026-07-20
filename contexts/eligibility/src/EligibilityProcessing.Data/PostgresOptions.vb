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
    ''' Only log SQL commands that took at least this many milliseconds. 0 (the default)
    ''' logs every command, which is the raw Npgsql behaviour.
    ''' <para>
    ''' Only relevant once SQL logging is on at all (Logging__LogLevel__Npgsql=Information
    ''' or Logging__LogLevel__Default=Debug). Without it that logging is a firehose - one
    ''' entry per command, thousands per batch, each spanning as many lines as its query
    ''' has newlines - and an outlier is impossible to spot. With, say, 50 here you get
    ''' one line per slow command and nothing else.
    ''' </para>
    ''' <para>
    ''' Applies only to events Npgsql has actually timed (the "command execution
    ''' completed" event). The Debug-level "Executing command" event has no duration yet
    ''' and is always passed through - it is the only trace a HUNG query leaves, since a
    ''' query that never finishes never reports a duration.
    ''' </para>
    ''' Config: Postgres:SlowCommandLogThresholdMs.
    ''' </summary>
    Public Property SlowCommandLogThresholdMs As Integer = 0

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

    ''' <summary>
    ''' Command timeout (seconds) applied to the OUTPUT data source. Same failure
    ''' mode as the source ceiling above, and demonstrated: loading all of MRSTY
    ''' inserts roughly 5M rows in one statement, which exceeds Npgsql's 30s
    ''' default and dies with "Exception while reading from stream". The regular
    ''' pipeline writes are per-trial and nowhere near this, so the default only
    ''' bites the bulk maintenance paths (load-umls, and the phase 2 backfill).
    ''' 0 means no timeout (infinite). Config: Postgres:OutputCommandTimeoutSeconds.
    ''' </summary>
    Public Property OutputCommandTimeoutSeconds As Integer = 600

End Class
