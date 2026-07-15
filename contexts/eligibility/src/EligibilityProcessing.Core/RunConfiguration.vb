Imports System.Collections.Generic
Imports System.Linq

' Input to <see cref="PipelineOrchestrator.ExecuteAsync"/>.
'
' Spec section 2.1 trigger normalisation produces this shape from any of the
' three invocation paths (form / webhook / sub-workflow). StudyCount has
' already been coerced to a positive integer by the caller — the orchestrator
' treats StudyCount <= 0 as "empty batch" and exits early after watermark
' bookkeeping.

Public NotInheritable Class RunConfiguration

    Private Shared ReadOnly EmptyIds As IReadOnlyList(Of String) = Array.Empty(Of String)()

    Public Sub New(
            studyCount As Integer,
            triggerSource As String,
            Optional rerunNctIds As IReadOnlyList(Of String) = Nothing,
            Optional direction As TrialSelectionDirection = TrialSelectionDirection.Forward)
        Me.StudyCount = studyCount
        Me.TriggerSource = If(triggerSource, "")
        Me.RerunNctIds = NormaliseIds(rerunNctIds)
        Me.Direction = direction
    End Sub

    ' Convenience overload preserved for the historical single-trial rerun call
    ' sites (CLI rerun, single-row dashboard button). Wraps the one NCT ID in a
    ' single-element list so the orchestrator's iteration path is uniform.
    Public Sub New(
            studyCount As Integer,
            triggerSource As String,
            rerunNctId As String)
        Me.New(studyCount, triggerSource,
               If(String.IsNullOrWhiteSpace(rerunNctId),
                  EmptyIds,
                  CType(New String() {rerunNctId.Trim()}, IReadOnlyList(Of String))))
    End Sub

    Public ReadOnly Property StudyCount As Integer
    Public ReadOnly Property TriggerSource As String   ' "form" | "webhook" | "subworkflow" | "rerun" | "recent"
    Public ReadOnly Property Direction As TrialSelectionDirection

    ''' <summary>
    ''' When non-empty, the orchestrator processes ONLY these trials (each
    ''' fetched directly from ctgov.eligibilities by nct_id) and bypasses the
    ''' watermark + batch-selection path. Used by the dashboard's single-trial
    ''' "Run Trial" button (one entry) and the Studies tab's "Rerun selection"
    ''' button (many entries). One run_id per batch regardless of list size.
    ''' </summary>
    Public ReadOnly Property RerunNctIds As IReadOnlyList(Of String)

    ''' <summary>
    ''' Back-compat shim for callers that only care about the single-trial case.
    ''' Returns the first ID when the list is non-empty, otherwise empty string.
    ''' Prefer reading RerunNctIds directly when implementing new behaviour.
    ''' </summary>
    Public ReadOnly Property RerunNctId As String
        Get
            Return If(RerunNctIds.Count > 0, RerunNctIds(0), "")
        End Get
    End Property

    Public ReadOnly Property IsRerun As Boolean
        Get
            Return RerunNctIds.Count > 0
        End Get
    End Property

    Private Shared Function NormaliseIds(ids As IReadOnlyList(Of String)) As IReadOnlyList(Of String)
        If ids Is Nothing Then Return EmptyIds
        Dim cleaned = ids _
            .Where(Function(s) Not String.IsNullOrWhiteSpace(s)) _
            .Select(Function(s) s.Trim()) _
            .Distinct(StringComparer.OrdinalIgnoreCase) _
            .ToArray()
        If cleaned.Length = 0 Then Return EmptyIds
        Return cleaned
    End Function

End Class
