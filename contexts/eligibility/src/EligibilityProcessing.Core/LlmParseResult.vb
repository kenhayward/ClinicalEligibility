' Outcome of parsing one LLM response envelope. Returned by
' <see cref="LlmResponseParser.ParseWithOutcome"/> so the orchestrator can
' distinguish "LLM produced unusable output" from "LLM legitimately produced
' nothing" — same Records.Count = 0 result, very different root cause.

Public NotInheritable Class LlmParseResult

    ''' <summary>One or more criterion records were extracted.</summary>
    Public Const OutcomeSuccess As String = "success"

    ''' <summary>LLM returned valid JSON but no records (an empty array, or an
    ''' array containing only non-object elements). Distinct from
    ''' <see cref="OutcomeInvalidJson"/>: nothing went wrong with parsing,
    ''' the model just didn't extract anything for this trial.</summary>
    Public Const OutcomeEmptyArray As String = "empty_array"

    ''' <summary>The LLM output was not parseable. Covers truncation by
    ''' max_tokens (the common case), missing brackets, non-JSON noise, or
    ''' empty / Nothing response payload. The orchestrator records this
    ''' distinctly because it usually points at a config issue (token cap,
    ''' prompt, model) rather than a "no criteria here" reality.</summary>
    Public Const OutcomeInvalidJson As String = "invalid_json"

    Public Sub New(
            records As IReadOnlyList(Of CriterionRecord),
            outcome As String,
            Optional wasRepaired As Boolean = False)
        Me.Records = If(records, CType(Array.Empty(Of CriterionRecord)(), IReadOnlyList(Of CriterionRecord)))
        Me.Outcome = If(outcome, OutcomeSuccess)
        Me.WasRepaired = wasRepaired
    End Sub

    Public ReadOnly Property Records As IReadOnlyList(Of CriterionRecord)
    Public ReadOnly Property Outcome As String

    ''' <summary>
    ''' True when the parser had to apply a JSON-repair pass before the model
    ''' output was parseable (e.g. inserted a missing colon after a key).
    ''' Observability signal — the orchestrator logs a warning when this is
    ''' set so operators can track how often the model needs rescuing.
    ''' </summary>
    Public ReadOnly Property WasRepaired As Boolean

End Class
