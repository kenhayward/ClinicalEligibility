' One LLM extraction request for a single trial.
'
' Carries the trial-level inputs (NctId, CriteriaText). Temperature, MaxTokens
' and TimeoutSeconds are advisory only — the LlmClient overrides them with
' the per-deployment LlmOptions values (sourced from appsettings.json + .env).
' The fields are retained for backwards compatibility and documentation;
' changing them at the call site has no wire effect.
'
' Defaults track the spec ("temperature 0.3", "max output tokens 8000",
' "timeout 600s") so a LlmRequest read in isolation looks reasonable.

Public NotInheritable Class LlmRequest

    Public Sub New(
            nctId As String,
            criteriaText As String,
            Optional temperature As Double = 0.3,
            Optional maxTokens As Integer = 8000,
            Optional timeoutSeconds As Integer = 600)
        Me.NctId = nctId
        Me.CriteriaText = criteriaText
        Me.Temperature = temperature
        Me.MaxTokens = maxTokens
        Me.TimeoutSeconds = timeoutSeconds
    End Sub

    Public ReadOnly Property NctId As String
    Public ReadOnly Property CriteriaText As String
    Public ReadOnly Property Temperature As Double   ' advisory; LlmClient uses LlmOptions.Temperature
    Public ReadOnly Property MaxTokens As Integer    ' advisory; LlmClient uses LlmOptions.MaxTokens
    Public ReadOnly Property TimeoutSeconds As Integer ' advisory; HttpClient.Timeout is set from LlmOptions.TimeoutSeconds at DI time

End Class
