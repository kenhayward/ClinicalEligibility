Imports System.Collections.Generic
Imports System.Linq

' Post-UMLS-resolution dedup pass for a single trial's resolved records.
' Collapses entries that share (ConceptCode, SemanticType, Criterion) into a
' single row whose OriginalText is the space-joined deduplicated set of the
' source snippets; other fields take the first value seen in input order.
'
' Why this exists: the LLM often extracts the same medical concept from two
' separate sentences in a trial's eligibility criteria — e.g. "history of
' breast cancer" and "breast cancer survivor" both resolve to the same UMLS
' CUI. Without dedup we persist two rows that say the same thing, just with
' different source snippets. Merging keeps one row carrying both snippets.
'
' Unresolved records (ConceptCode = "") are passed through untouched —
' the empty concept code carries no identity, so we can't tell whether two
' unresolved records refer to the same underlying concept.
'
' Order-preserving: each output record appears at the position of its first
' occurrence in the input, so trial-level persist order is stable.
'
' Same ConceptCode + SemanticType but DIFFERENT Criterion (Inclusion vs
' Exclusion) is NOT a duplicate — a trial that includes diabetics and
' excludes severe diabetes are two distinct logical criteria.

Public NotInheritable Class DuplicateConceptMerger

    Public Shared Function Merge(records As IReadOnlyList(Of ResolvedRecord)) As IReadOnlyList(Of ResolvedRecord)
        If records Is Nothing OrElse records.Count = 0 Then
            Return Array.Empty(Of ResolvedRecord)()
        End If

        ' Parallel structures: `groups` holds the buckets in input-order;
        ' `seen` maps the merge key to the bucket index so we can find an
        ' existing bucket in O(1). Unresolved records bypass `seen` and get
        ' their own single-record bucket — empty ConceptCode is not a key
        ' worth de-duping on.
        Dim groups As New List(Of List(Of ResolvedRecord))
        Dim seen As New Dictionary(Of (ConceptCode As String, SemanticType As String, Criterion As String), Integer)

        For Each r In records
            If String.IsNullOrEmpty(r.ConceptCode) Then
                groups.Add(New List(Of ResolvedRecord) From {r})
                Continue For
            End If

            ' Merge identity is the TUI SET, not the joined display string. The
            ' previous key used that string, so the same CUI with differently
            ' ordered semantic types counted as two distinct records.
            Dim tuiKey = String.Join("|", r.SemanticTypeTuis.OrderBy(Function(t) t, StringComparer.Ordinal))
            Dim key = (r.ConceptCode, tuiKey, r.Criterion)
            Dim idx As Integer
            If seen.TryGetValue(key, idx) Then
                groups(idx).Add(r)
            Else
                seen(key) = groups.Count
                groups.Add(New List(Of ResolvedRecord) From {r})
            End If
        Next

        Dim result As New List(Of ResolvedRecord)(groups.Count)
        For Each g In groups
            result.Add(MergeGroup(g))
        Next
        Return result
    End Function

    Private Shared Function MergeGroup(g As IReadOnlyList(Of ResolvedRecord)) As ResolvedRecord
        If g.Count = 1 Then Return g(0)

        Dim first = g(0)
        Dim combinedOriginalText = String.Join(" ",
                g.Select(Function(r) r.OriginalText) _
                 .Where(Function(s) Not String.IsNullOrEmpty(s)) _
                 .Distinct(StringComparer.Ordinal))

        Dim mergedCriterion = New CriterionRecord(
                nctId:=first.NctId,
                criterion:=first.Criterion,
                domain:=first.Domain,
                concept:=first.Concept,
                qualifier:=first.Qualifier,
                timeWindow:=first.TimeWindow,
                originalText:=combinedOriginalText)

        Dim mergedMatch = New UmlsMatch(
                conceptCode:=first.ConceptCode,
                umlsName:=first.UmlsName,
                matchSource:=first.MatchSource,
                matchScore:=first.MatchScore)

        ' Pass the assignments straight through. This used to split the joined
        ' display string on ", " to rebuild a list, with a comment claiming
        ' "semantic-type names don't contain ', ' in practice" - which is false:
        ' "Amino Acid, Peptide, or Protein" is a single UMLS semantic type name.
        ' That code was safe only because it re-joined with the same separator,
        ' so the string round-tripped; the intermediate list was garbage. Under a
        ' real list representation that accident no longer saves it.
        Return New ResolvedRecord(mergedCriterion, mergedMatch, first.Assignments)
    End Function

End Class
