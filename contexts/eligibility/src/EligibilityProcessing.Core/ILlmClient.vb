Imports System.Threading
Imports System.Threading.Tasks

' Contract for LLM extraction calls.
'
' Lives in Core so the orchestrator does not have to depend on the transport
' library (EligibilityProcessing.Llm). The concrete LlmClient implements this
' interface from Llm.
'
' Per spec section 2.4.4: transport failures (after retries) MUST be returned
' as <see cref="LlmResponse.Failure"/> — they must not throw. User cancellation
' (token signalled) MUST still propagate.

Public Interface ILlmClient

    ''' <summary>
    ''' Runs one extraction call. <paramref name="reasoningEffortOverride"/>,
    ''' when non-empty, overrides the per-deployment reasoning effort for this
    ''' single call (used by the orchestrator's reasoning-escalation retry);
    ''' Nothing/empty leaves the configured default in force.
    ''' </summary>
    Function CompleteAsync(
            request As LlmRequest,
            cancellationToken As CancellationToken,
            Optional reasoningEffortOverride As String = Nothing) As Task(Of LlmResponse)

    ''' <summary>
    ''' The reasoning effort a trial should be retried at when its first attempt
    ''' parses to an empty array (the model bailed). Returns "" when escalation
    ''' is disabled, unconfigured, or would retry at the same level — the
    ''' orchestrator treats "" as "do not escalate". Encapsulates the
    ''' EnableReasoningEscalation flag so Core need not know the LLM config shape.
    ''' </summary>
    ReadOnly Property EscalationReasoningEffort As String

End Interface
