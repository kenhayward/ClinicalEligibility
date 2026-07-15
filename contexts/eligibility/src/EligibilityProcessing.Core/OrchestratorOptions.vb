' Runtime configuration for <see cref="PipelineOrchestrator"/>.
'
' Architecture section 3.2 / spec section 6.7. The LLM concurrency cap MUST
' NOT exceed the aggregate slot count of the model server pool (spec section
' 2.4.5); 8 matches the production reference (2 backends * 4 slots each).

Public Class OrchestratorOptions

    Public Property LlmConcurrencyCap As Integer = 8

    ' Hybrid normalization-cache hook: when a criterion fails to resolve lexically,
    ' the orchestrator consults the umls.concept_normalization cache (populated
    ' offline by the `normalize-umls` command) by normalized concept — a cheap
    ' indexed lookup, no LLM — and applies a cached resolution on first pass. True
    ' by default; set False to disable the inline consult (a harmless miss when the
    ' REST backend is active or the cache is empty). Config: `Pipeline:UseNormalizationCache`.
    Public Property UseNormalizationCache As Boolean = True

End Class
