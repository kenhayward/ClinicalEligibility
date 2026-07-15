Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks

' Contract for LLM-normalizing a cluster of eligibility-criterion phrasings
' into one canonical statement (authoring specification §3.5).
'
' Lives in Core so the Web Authoring controller does not depend on the
' transport library. CriteriaNormalizer implements this from the Llm project
' against an OpenAI-compatible chat-completions endpoint.

Public Interface ICriteriaNormalizer

    ''' <summary>
    ''' Sends the <paramref name="originalTexts"/> variants of one underlying
    ''' criterion to the LLM and returns a single canonical phrasing. Transport
    ''' failures are returned as <see cref="NormalizationResult.Failure"/>, not
    ''' thrown; user cancellation is re-thrown.
    ''' </summary>
    Function NormalizeAsync(
            originalTexts As IReadOnlyList(Of String),
            cancellationToken As CancellationToken) As Task(Of NormalizationResult)

    ''' <summary>
    ''' Maps ONE extracted concept phrase to its canonical clinical term (the
    ''' `normalize-umls` UMLS gap-recovery path) — e.g. "low blood sugar" →
    ''' "Hypoglycemia", "ECOG PS" → "Eastern Cooperative Oncology Group performance
    ''' status". Returns the single word "NONE" (as a successful result) when the
    ''' phrase is not a biomedical concept. Transport failures are returned as
    ''' <see cref="NormalizationResult.Failure"/>, not thrown; cancellation is
    ''' re-thrown. Uses the same endpoint/model/options as
    ''' <see cref="NormalizeAsync"/> with a concept-specific prompt.
    ''' </summary>
    Function NormalizeConceptAsync(
            concept As String,
            cancellationToken As CancellationToken) As Task(Of NormalizationResult)

End Interface
