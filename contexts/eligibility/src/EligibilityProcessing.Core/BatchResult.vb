Imports System.Collections.Generic

' Return value of <see cref="PipelineOrchestrator.ExecuteAsync"/>.
'
' Wraps the persistable <see cref="RunMetrics"/> with the volatile list of
' NCT_IDs that failed during this run. The metrics get written to
' public.eligibility_run; the failed-IDs list feeds the error notification
' channel and the CLI retry path (architecture section 6 mapping).

Public NotInheritable Class BatchResult

    Public Sub New(metrics As RunMetrics, failedNctIds As IReadOnlyList(Of String))
        If metrics Is Nothing Then Throw New ArgumentNullException(NameOf(metrics))
        Me.Metrics = metrics
        Me.FailedNctIds = If(failedNctIds, CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
    End Sub

    Public ReadOnly Property Metrics As RunMetrics
    Public ReadOnly Property FailedNctIds As IReadOnlyList(Of String)

End Class
