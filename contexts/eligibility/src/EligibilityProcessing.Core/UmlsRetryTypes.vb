' Carry-through types for the UMLS-only retry path (CLI `retry-umls`).
'
' A retry re-resolves the UMLS columns of existing public.eligibility rows whose
' concept_code is empty — no LLM call. The gateway hands the CLI the unresolved
' rows (UmlsRetryRow), the CLI re-runs each Concept through IUmlsClient +
' UmlsMatchScorer, and feeds the rows that newly resolve back as UmlsRetryResult
' for a per-trial transactional UPDATE.

''' <summary>A UMLS-unresolved eligibility row to retry: its primary key and the
''' stored concept text to re-resolve. Only rows with a non-empty concept are
''' returned (an empty concept has nothing to look up).</summary>
Public Structure UmlsRetryRow
    Public ReadOnly Id As Long
    Public ReadOnly Concept As String

    Public Sub New(id As Long, concept As String)
        Me.Id = id
        Me.Concept = concept
    End Sub
End Structure

''' <summary>The outcome of re-resolving one row — the row id plus the five UMLS
''' columns to write. Constructed only for rows that newly cleared the scorer's
''' match threshold; rows that still don't resolve are left untouched.</summary>
Public Structure UmlsRetryResult
    Public ReadOnly Id As Long
    Public ReadOnly ConceptCode As String
    Public ReadOnly UmlsName As String
    Public ReadOnly MatchSource As String
    Public ReadOnly MatchScore As Double
    Public ReadOnly SemanticType As String

    Public Sub New(
            id As Long,
            conceptCode As String,
            umlsName As String,
            matchSource As String,
            matchScore As Double,
            semanticType As String)
        Me.Id = id
        Me.ConceptCode = conceptCode
        Me.UmlsName = umlsName
        Me.MatchSource = matchSource
        Me.MatchScore = matchScore
        Me.SemanticType = semanticType
    End Sub
End Structure
