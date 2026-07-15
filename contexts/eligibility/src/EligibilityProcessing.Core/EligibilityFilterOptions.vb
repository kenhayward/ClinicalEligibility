' Per-column distinct-value lists from public.eligibility, used by the Results
' view to decide between rendering a <select> dropdown vs. a free-text input
' for each filterable column.
'
' A column's list is populated only when its distinct cardinality is at or
' below the configured threshold (defaults to 100). Columns above the
' threshold get an empty list — the view falls back to a text input.
'
' Empty list semantics:
'   - Empty list means "too many distinct values; do not render a dropdown."
'   - Distinct from "zero distinct values" (which on a populated table is
'     impossible for the NOT NULL columns).
' Tests should rely on the documented threshold rather than counting empties.

Public NotInheritable Class EligibilityFilterOptions

    Public Sub New(
            nctIds As IReadOnlyList(Of String),
            criteria As IReadOnlyList(Of String),
            domains As IReadOnlyList(Of String),
            concepts As IReadOnlyList(Of String),
            conceptCodes As IReadOnlyList(Of String),
            semanticTypes As IReadOnlyList(Of String))
        Me.NctIds = If(nctIds, CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
        Me.Criteria = If(criteria, CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
        Me.Domains = If(domains, CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
        Me.Concepts = If(concepts, CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
        Me.ConceptCodes = If(conceptCodes, CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
        Me.SemanticTypes = If(semanticTypes, CType(Array.Empty(Of String)(), IReadOnlyList(Of String)))
    End Sub

    Public ReadOnly Property NctIds As IReadOnlyList(Of String)
    Public ReadOnly Property Criteria As IReadOnlyList(Of String)
    Public ReadOnly Property Domains As IReadOnlyList(Of String)
    Public ReadOnly Property Concepts As IReadOnlyList(Of String)
    Public ReadOnly Property ConceptCodes As IReadOnlyList(Of String)
    Public ReadOnly Property SemanticTypes As IReadOnlyList(Of String)

    Public Shared ReadOnly Empty As New EligibilityFilterOptions(
            Nothing, Nothing, Nothing, Nothing, Nothing, Nothing)

End Class
