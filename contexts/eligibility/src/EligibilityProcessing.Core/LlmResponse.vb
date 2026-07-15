' Result of one LLM extraction call.
'
' Carries the raw model output text plus observability fields (architecture
' section 2.3: "FinishReason and token counts feed observability (section 6.6) —
' the n8n implementation loses these because chainLlm strips them; in .NET we
' retain them").
'
' Use the Success / Failure factories; the constructor is private so callers
' cannot construct a half-built result. Failure surfaces transport problems to
' the orchestrator without throwing — per spec section 2.4.4 a failed trial
' must NOT abort the batch.

Public NotInheritable Class LlmResponse

    Private Sub New(
            nctId As String,
            rawText As String,
            finishReason As String,
            promptTokens As Integer,
            completionTokens As Integer,
            stoppedEos As Boolean?,
            stoppedLimit As Boolean?,
            stoppedWord As Boolean?,
            stoppingWord As String,
            truncated As Boolean?,
            succeeded As Boolean,
            errorMessage As String)
        Me.NctId = nctId
        Me.RawText = rawText
        Me.FinishReason = finishReason
        Me.PromptTokens = promptTokens
        Me.CompletionTokens = completionTokens
        Me.StoppedEos = stoppedEos
        Me.StoppedLimit = stoppedLimit
        Me.StoppedWord = stoppedWord
        Me.StoppingWord = If(stoppingWord, "")
        Me.Truncated = truncated
        Me.Succeeded = succeeded
        Me.ErrorMessage = errorMessage
    End Sub

    Public ReadOnly Property NctId As String
    Public ReadOnly Property RawText As String                ' empty when not succeeded
    Public ReadOnly Property FinishReason As String           ' e.g. "stop", "length"; empty when not succeeded
    Public ReadOnly Property PromptTokens As Integer
    Public ReadOnly Property CompletionTokens As Integer
    Public ReadOnly Property Succeeded As Boolean
    Public ReadOnly Property ErrorMessage As String           ' empty when succeeded

    ' llama.cpp vendor diagnostics (not part of the OpenAI schema). The
    ' OpenAI-compat layer sticks these at the root of the response next to
    ' "choices" / "usage". Captured here so a length-truncated trial's
    ' audit row can carry the actual stop reason (slot exhausted vs limit
    ' hit vs EOS suppressed). All four are Nothing on servers that don't
    ' surface them — see PipelineOrchestrator for the rendering rules.
    Public ReadOnly Property StoppedEos As Boolean?
    Public ReadOnly Property StoppedLimit As Boolean?
    Public ReadOnly Property StoppedWord As Boolean?
    Public ReadOnly Property StoppingWord As String           ' empty when not present
    Public ReadOnly Property Truncated As Boolean?

    Public Shared Function Success(
            nctId As String,
            rawText As String,
            Optional finishReason As String = "",
            Optional promptTokens As Integer = 0,
            Optional completionTokens As Integer = 0,
            Optional stoppedEos As Boolean? = Nothing,
            Optional stoppedLimit As Boolean? = Nothing,
            Optional stoppedWord As Boolean? = Nothing,
            Optional stoppingWord As String = "",
            Optional truncated As Boolean? = Nothing) As LlmResponse
        Return New LlmResponse(
                nctId:=nctId,
                rawText:=rawText,
                finishReason:=finishReason,
                promptTokens:=promptTokens,
                completionTokens:=completionTokens,
                stoppedEos:=stoppedEos,
                stoppedLimit:=stoppedLimit,
                stoppedWord:=stoppedWord,
                stoppingWord:=stoppingWord,
                truncated:=truncated,
                succeeded:=True,
                errorMessage:="")
    End Function

    Public Shared Function Failure(nctId As String, errorMessage As String) As LlmResponse
        Return New LlmResponse(
                nctId:=nctId,
                rawText:="",
                finishReason:="",
                promptTokens:=0,
                completionTokens:=0,
                stoppedEos:=Nothing,
                stoppedLimit:=Nothing,
                stoppedWord:=Nothing,
                stoppingWord:="",
                truncated:=Nothing,
                succeeded:=False,
                errorMessage:=errorMessage)
    End Function

End Class
