Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Text
Imports System.Threading

' Builds the system + user prompt pair for LLM chat-completions calls.
'
' Spec section 2.4.2 / 2.4.3. The system prompt is checked in as an embedded
' resource (Prompts/system.v1.md) so its content is version-controlled, fully
' visible in the diff, and testable without hitting the LLM.

Public NotInheritable Class PromptBuilder

    Private Const ResourceName As String = "EligibilityProcessing.Llm.Prompts.system.v1.md"
    Private Const NormalizeResourceName As String = "EligibilityProcessing.Llm.Prompts.normalize.v1.md"
    Private Const ConceptNormalizeResourceName As String = "EligibilityProcessing.Llm.Prompts.concept-normalize.v1.md"

    Private Shared ReadOnly s_systemPromptCache As New Lazy(Of String)(
            AddressOf LoadSystemPrompt, LazyThreadSafetyMode.ExecutionAndPublication)

    Private Shared ReadOnly s_normalizePromptCache As New Lazy(Of String)(
            Function() LoadPrompt(NormalizeResourceName), LazyThreadSafetyMode.ExecutionAndPublication)

    Private Shared ReadOnly s_conceptNormalizePromptCache As New Lazy(Of String)(
            Function() LoadPrompt(ConceptNormalizeResourceName), LazyThreadSafetyMode.ExecutionAndPublication)

    ''' <summary>
    ''' The verbatim system prompt sent on every LLM call. Loaded once from the
    ''' embedded resource and cached for the process lifetime.
    ''' </summary>
    Public Shared ReadOnly Property SystemPrompt As String
        Get
            Return s_systemPromptCache.Value
        End Get
    End Property

    ''' <summary>
    ''' Builds the user message body per spec section 2.4.3:
    ''' "NCT_ID: &lt;trial identifier&gt;\nCriteria:\n&lt;raw criteria text from source&gt;".
    ''' </summary>
    Public Shared Function BuildUserMessage(nctId As String, criteriaText As String) As String
        Return $"NCT_ID: {If(nctId, "")}{vbLf}Criteria:{vbLf}{If(criteriaText, "")}"
    End Function

    ''' <summary>
    ''' The system prompt for the criterion-normalization call (authoring
    ''' specification §3.5). Loaded once from the embedded resource and cached.
    ''' </summary>
    Public Shared ReadOnly Property NormalizeSystemPrompt As String
        Get
            Return s_normalizePromptCache.Value
        End Get
    End Property

    ''' <summary>
    ''' Builds the user message for the normalization call — the numbered list
    ''' of original-text phrasings to be merged into one canonical criterion.
    ''' </summary>
    Public Shared Function BuildNormalizeUserMessage(originalTexts As IReadOnlyList(Of String)) As String
        Dim sb As New StringBuilder()
        sb.Append("Phrasings of the same eligibility criterion:").Append(vbLf)
        Dim n As Integer = 0
        If originalTexts IsNot Nothing Then
            For Each t In originalTexts
                If String.IsNullOrWhiteSpace(t) Then Continue For
                n += 1
                sb.Append(n).Append(". ").Append(t.Trim()).Append(vbLf)
            Next
        End If
        Return sb.ToString()
    End Function

    ''' <summary>
    ''' The system prompt for the single-concept normalization call (the
    ''' `normalize-umls` UMLS gap-recovery path). Maps one extracted concept phrase
    ''' to its canonical clinical term, or "NONE". Loaded once and cached.
    ''' </summary>
    Public Shared ReadOnly Property ConceptNormalizeSystemPrompt As String
        Get
            Return s_conceptNormalizePromptCache.Value
        End Get
    End Property

    ''' <summary>
    ''' Builds the user message for the single-concept normalization call — just
    ''' the one extracted concept phrase to canonicalize.
    ''' </summary>
    Public Shared Function BuildConceptNormalizeUserMessage(concept As String) As String
        Return $"Concept: {If(concept, "").Trim()}"
    End Function

    Private Shared Function LoadSystemPrompt() As String
        Return LoadPrompt(ResourceName)
    End Function

    Private Shared Function LoadPrompt(resourceName As String) As String
        Dim asm = GetType(PromptBuilder).Assembly
        Using stream = asm.GetManifestResourceStream(resourceName)
            If stream Is Nothing Then
                Throw New InvalidOperationException(
                        $"Embedded resource '{resourceName}' not found. " &
                        "Ensure the .md file is included as <EmbeddedResource> in the .vbproj.")
            End If
            Using reader As New StreamReader(stream)
                Return reader.ReadToEnd()
            End Using
        End Using
    End Function

End Class
