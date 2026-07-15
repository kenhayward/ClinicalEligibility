' Query parameters for the dashboard's Studies tab. Empty / Nothing fields
' on the categorical columns mean "do not filter on this column"; all three
' match exactly when set — see <see cref="IPostgresGateway.GetStudiesAsync"/>.
'
' HideSuperseded toggles a "latest attempt per nct_id" projection BEFORE
' the categorical filters are applied: when true, the result set only
' contains rows whose (nct_id, started_at) is the most recent for that
' trial, so a trial whose latest rerun succeeded won't appear under a
' status=parse_invalid_json filter even if an earlier failed attempt exists.
' When false the table behaves like a flat audit list.

Public NotInheritable Class StudyFilter

    Public Sub New(
            Optional nctId As String = Nothing,
            Optional status As String = Nothing,
            Optional runId As Guid? = Nothing,
            Optional hideSuperseded As Boolean = False)
        Me.NctId = NullIfBlank(nctId)
        Me.Status = NullIfBlank(status)
        Me.RunId = runId
        Me.HideSuperseded = hideSuperseded
    End Sub

    Public ReadOnly Property NctId As String
    Public ReadOnly Property Status As String
    Public ReadOnly Property RunId As Guid?
    Public ReadOnly Property HideSuperseded As Boolean

    Public ReadOnly Property IsEmpty As Boolean
        Get
            Return NctId Is Nothing AndAlso Status Is Nothing AndAlso Not RunId.HasValue
        End Get
    End Property

    Private Shared Function NullIfBlank(value As String) As String
        If String.IsNullOrWhiteSpace(value) Then Return Nothing
        Return value.Trim()
    End Function

End Class
