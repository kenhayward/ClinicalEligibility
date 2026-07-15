' Carry-through types for the LLM concept-normalization path (CLI `normalize-umls`
' + the pipeline's inline cache consult).
'
' A residue concept that fails lexical UMLS resolution is sent to the LLM to be
' canonicalized, then the canonical term is re-resolved lexically. The outcome is
' cached in umls.concept_normalization keyed by the NORMALIZED concept string, and
' the extraction pipeline reads that cache inline to resolve repeat concepts on
' first pass without an LLM call.

''' <summary>A distinct residue concept to normalize: the normalized cache key plus
''' a representative ORIGINAL phrasing to send to the LLM (original casing is kept
''' so acronyms like "ECOG PS" survive — the key is lower-cased, the sample is not).</summary>
Public Structure ConceptToNormalize
    Public ReadOnly ConceptNorm As String
    Public ReadOnly Concept As String

    Public Sub New(conceptNorm As String, concept As String)
        Me.ConceptNorm = conceptNorm
        Me.Concept = concept
    End Sub
End Structure

''' <summary>A cached, resolved concept→UMLS mapping read from
''' umls.concept_normalization — the five UMLS columns the pipeline applies in place
''' of an unresolved lexical lookup.</summary>
Public Structure CachedConceptResolution
    Public ReadOnly ConceptCode As String
    Public ReadOnly UmlsName As String
    Public ReadOnly MatchSource As String
    Public ReadOnly MatchScore As Double
    Public ReadOnly SemanticType As String

    Public Sub New(
            conceptCode As String,
            umlsName As String,
            matchSource As String,
            matchScore As Double,
            semanticType As String)
        Me.ConceptCode = conceptCode
        Me.UmlsName = umlsName
        Me.MatchSource = matchSource
        Me.MatchScore = matchScore
        Me.SemanticType = semanticType
    End Sub
End Structure
