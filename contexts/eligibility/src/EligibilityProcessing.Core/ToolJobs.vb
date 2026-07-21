Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks

' Shared contracts for the long-running maintenance "tools" that can run either
' from the CLI or as background jobs on the web Tools tab: normalize-umls and
' embed-studies. The job *logic* lives in UmlsNormalizeJob / StudyEmbeddingJob
' (this Core project) so the CLI and the web host run the identical code path -
' the only thing that differs is how each renders the progress snapshots.
'
' Mutual exclusion (no two tools + the main pipeline at once) is enforced by the
' web host's RunGate, not here; these services just do the work and report.

''' <summary>The maintenance tools exposed as background jobs.</summary>
Public Enum ToolJobKind
    NormalizeUmls
    EmbedStudies
    NormalizeConditions
End Enum

''' <summary>
''' Options for the UMLS concept-normalization job (mirrors the CLI
''' <c>normalize-umls</c> switches). <see cref="Concurrency"/> is pre-resolved by
''' the caller - it defaults to the pipeline's LLM concurrency cap.
''' </summary>
Public NotInheritable Class NormalizeUmlsOptions
    Public Property Count As Integer = 50
    Public Property Concurrency As Integer = 1
    Public Property DryRun As Boolean
    Public Property Force As Boolean
End Class

''' <summary>
''' Options for the study-embedding job (mirrors <c>embed-studies</c>).
''' <see cref="Model"/> is the embedding model name the caller read from config.
''' </summary>
Public NotInheritable Class EmbedStudiesOptions
    Public Property Concurrency As Integer = 1
    Public Property Model As String = ""
End Class

''' <summary>
''' Options for the condition-normalization job (mirrors the CLI
''' <c>normalize-conditions</c> switches). <see cref="Count"/> of 0 means "every
''' pending row".
''' </summary>
Public NotInheritable Class NormalizeConditionsOptions
    Public Property Count As Integer = 0
    Public Property DryRun As Boolean
    Public Property Force As Boolean

    ''' <summary>
    ''' How many pending condition strings to resolve concurrently. Each is
    ''' independent (a store lookup plus, for the harder ones, a UMLS search),
    ''' so this parallelises safely; see ConditionNormalizeJob.RunAsync. Clamped
    ''' to at least 1 by the job. Default 8 mirrors EmbedStudiesOptions.Concurrency.
    ''' </summary>
    Public Property Concurrency As Integer = 8
End Class

''' <summary>One named metric value - a stat tile in the web UI, and a fragment of
''' the CLI progress/summary line.</summary>
Public Structure ToolMetric
    Public Sub New(label As String, value As Long)
        _label = label
        _value = value
    End Sub

    Private ReadOnly _label As String
    Private ReadOnly _value As Long

    Public ReadOnly Property Label As String
        Get
            Return _label
        End Get
    End Property

    Public ReadOnly Property Value As Long
        Get
            Return _value
        End Get
    End Property
End Structure

''' <summary>
''' A point-in-time view of a running tool job: how far it has progressed plus the
''' same metric values the CLI prints. Emitted periodically through the
''' <see cref="IProgress(Of ToolJobSnapshot)"/> passed to a job's RunAsync, with a
''' final snapshot at completion.
''' </summary>
Public NotInheritable Class ToolJobSnapshot
    Public Sub New(kind As ToolJobKind, total As Integer, processed As Integer,
                   elapsed As TimeSpan, metrics As IReadOnlyList(Of ToolMetric))
        Me.Kind = kind
        Me.Total = total
        Me.Processed = processed
        Me.Elapsed = elapsed
        Me.Metrics = metrics
    End Sub

    Public ReadOnly Property Kind As ToolJobKind
    Public ReadOnly Property Total As Integer
    Public ReadOnly Property Processed As Integer
    Public ReadOnly Property Elapsed As TimeSpan
    Public ReadOnly Property Metrics As IReadOnlyList(Of ToolMetric)
End Class

''' <summary>Lock-free counters for the normalize-umls job (updated via Interlocked,
''' so the fields are public and not properties).</summary>
Public NotInheritable Class NormalizeCounters
    Public Done As Integer
    Public Resolved As Integer
    Public NoneCount As Integer
    Public RowsUpdated As Integer
    Public Errors As Integer
End Class

''' <summary>Lock-free counters for the embed-studies job.</summary>
Public NotInheritable Class EmbedCounters
    Public Processed As Integer
    Public Failed As Integer
End Class

''' <summary>Counters for the condition-normalization job.</summary>
Public NotInheritable Class ConditionCounters
    Public Done As Integer
    Public Resolved As Integer
    Public Unresolved As Integer
End Class

''' <summary>The UMLS concept-normalization maintenance job.</summary>
Public Interface IUmlsNormalizeJob
    ''' <summary>Distinct unresolved residue concepts still eligible for
    ''' normalization (the remaining-work count shown on the Tools tab).</summary>
    Function CountRemainingAsync(includeAttempted As Boolean, cancellationToken As CancellationToken) As Task(Of Integer)

    ''' <summary>Run one batch (up to <c>options.Count</c> distinct concepts),
    ''' reporting periodic snapshots through <paramref name="progress"/> (may be
    ''' Nothing). Returns the final counters.</summary>
    Function RunAsync(options As NormalizeUmlsOptions, progress As IProgress(Of ToolJobSnapshot), cancellationToken As CancellationToken) As Task(Of NormalizeCounters)
End Interface

''' <summary>The study topic-embedding maintenance job.</summary>
Public Interface IStudyEmbeddingJob
    ''' <summary>Processed studies with a snapshot but no embedding for
    ''' <paramref name="model"/> (the remaining-work count shown on the Tools tab).</summary>
    Function CountRemainingAsync(model As String, cancellationToken As CancellationToken) As Task(Of Integer)

    ''' <summary>Embed every remaining study for the configured model, reporting
    ''' periodic snapshots through <paramref name="progress"/> (may be Nothing).
    ''' Returns the final counters.</summary>
    Function RunAsync(options As EmbedStudiesOptions, progress As IProgress(Of ToolJobSnapshot), cancellationToken As CancellationToken) As Task(Of EmbedCounters)
End Interface

''' <summary>The condition-normalization maintenance job.</summary>
Public Interface IConditionNormalizeJob
    ''' <summary>Dictionary rows still needing resolution (the Tools tab count).</summary>
    Function CountRemainingAsync(force As Boolean, cancellationToken As CancellationToken) As Task(Of Integer)

    ''' <summary>Seed the dictionary from the corpus, then resolve pending rows
    ''' highest study_count first, reporting snapshots through
    ''' <paramref name="progress"/> (may be Nothing).</summary>
    Function RunAsync(options As NormalizeConditionsOptions, progress As IProgress(Of ToolJobSnapshot), cancellationToken As CancellationToken) As Task(Of ConditionCounters)
End Interface

''' <summary>Shared background progress pump: periodically rebuilds a snapshot from
''' the live counters and reports it, until the caller cancels the token (when the
''' batch ends). Display-only, so reading the lock-free counters needs no lock.</summary>
Friend Module ToolJobProgressPump
    Friend Async Function PumpAsync(progress As IProgress(Of ToolJobSnapshot),
                                    build As Func(Of ToolJobSnapshot),
                                    token As CancellationToken) As Task
        Try
            Do
                Await Task.Delay(500, token).ConfigureAwait(False)
                progress.Report(build())
            Loop
        Catch ex As OperationCanceledException
            ' Stopped by the caller once the batch completes.
        End Try
    End Function
End Module
