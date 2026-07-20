Imports System.Collections.Generic
Imports System.Linq

' A criterion after UMLS resolution — the final shape written to public.eligibility.
'
' Spec section 4.3 / 4.4 / 2.8.1. Composed from:
'   - a <see cref="CriterionRecord"/> (LLM-extracted)
'   - a <see cref="UmlsMatch"/> (best-match scoring result; Unresolved when below threshold)
'   - the UMLS semantic-type assignments for the matched CUI (empty when unresolved)
'
' When the match is unresolved (IsResolved = False), the UMLS-derived fields
' all hold the unresolved sentinels: empty strings for ConceptCode/UmlsName/
' MatchSource/SemanticType, an empty TUI list, and 0.0 for MatchScore
' (spec section 2.8.1).

Public NotInheritable Class ResolvedRecord

    Public Sub New(
            criterion As CriterionRecord,
            umlsMatch As UmlsMatch,
            semanticTypes As IReadOnlyList(Of SemanticTypeAssignment))
        If criterion Is Nothing Then Throw New ArgumentNullException(NameOf(criterion))
        If umlsMatch Is Nothing Then Throw New ArgumentNullException(NameOf(umlsMatch))

        Me.NctId = criterion.NctId
        Me.Criterion = criterion.Criterion
        Me.Domain = criterion.Domain
        Me.Concept = criterion.Concept
        Me.Qualifier = criterion.Qualifier
        Me.TimeWindow = criterion.TimeWindow
        Me.OriginalText = criterion.OriginalText

        Me.ConceptCode = umlsMatch.ConceptCode
        Me.UmlsName = umlsMatch.UmlsName
        Me.MatchSource = umlsMatch.MatchSource
        Me.MatchScore = umlsMatch.MatchScore

        Dim assignments = If(semanticTypes,
                             CType(Array.Empty(Of SemanticTypeAssignment)(), IReadOnlyList(Of SemanticTypeAssignment)))
        Me.Assignments = assignments.ToArray()
        ' Sorted by name so one CUI yields one string corpus-wide. The legacy
        ' REST-era values preserved UMLS API order, which is not alphabetical -
        ' the phase 2 backfill rewrites them to this form.
        Me.SemanticType = String.Join(", ",
                assignments.Select(Function(a) a.Sty).OrderBy(Function(s) s, StringComparer.Ordinal))
        ' Empty TUIs are dropped rather than stored. The REST backend derives the
        ' TUI from a URI and can come up empty; "" is not a semantic type id and
        ' would become a bogus array member that phase 3's containment filter
        ' could match on. The name still reaches the display string above.
        Me.SemanticTypeTuis = assignments.Select(Function(a) a.Tui) _
                                         .Where(Function(t) Not String.IsNullOrEmpty(t)) _
                                         .Distinct(StringComparer.Ordinal) _
                                         .ToArray()
    End Sub

    Public ReadOnly Property NctId As String
    Public ReadOnly Property Criterion As String
    Public ReadOnly Property Domain As String
    Public ReadOnly Property Concept As String
    Public ReadOnly Property ConceptCode As String     ' UMLS CUI; empty when unresolved

    ''' <summary>
    ''' Display form only: names sorted and `", "`-joined. DERIVED from the
    ''' assignments, never supplied independently, so it cannot drift from
    ''' <see cref="SemanticTypeTuis"/>. Do not parse it - several UMLS semantic
    ''' type names contain commas ("Amino Acid, Peptide, or Protein"), so a split
    ''' on ", " does not recover the parts.
    ''' </summary>
    Public ReadOnly Property SemanticType As String

    ''' <summary>
    ''' The analytic form: semantic type ids. TUIs are stable across UMLS
    ''' releases where names get reworded. Empty when unresolved.
    ''' </summary>
    Public ReadOnly Property SemanticTypeTuis As IReadOnlyList(Of String)

    ''' <summary>The (tui, sty) pairs behind both projections above.</summary>
    Public ReadOnly Property Assignments As IReadOnlyList(Of SemanticTypeAssignment)

    Public ReadOnly Property Qualifier As String
    Public ReadOnly Property TimeWindow As String
    Public ReadOnly Property OriginalText As String
    Public ReadOnly Property UmlsName As String        ' preferred UMLS name; empty when unresolved
    Public ReadOnly Property MatchScore As Double      ' [0, 1] rounded to 3 dp; 0 when unresolved
    Public ReadOnly Property MatchSource As String     ' root source vocabulary; empty when unresolved

End Class

''' <summary>
''' One semantic-type assignment: the stable TUI and its display name.
''' </summary>
''' <remarks>
''' Carried as a pair rather than two parallel lists because the display string
''' is ordered by name while the array is keyed by TUI - separate lists would
''' invite them drifting out of alignment.
''' </remarks>
Public NotInheritable Class SemanticTypeAssignment

    Public Sub New(tui As String, sty As String)
        Me.Tui = If(tui, "")
        Me.Sty = If(sty, "")
    End Sub

    Public ReadOnly Property Tui As String
    Public ReadOnly Property Sty As String

End Class
