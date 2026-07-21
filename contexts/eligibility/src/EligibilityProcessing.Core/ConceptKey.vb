Imports System.Text.RegularExpressions

''' <summary>
''' The single normalization used for exact concept matching: lower-invariant,
''' internal whitespace collapsed to single spaces, trimmed.
'''
''' This is the form stored as umls.atom.str_norm by the loader, applied to the
''' query by the lookup, and used as the primary key of
''' public.condition_concept. All three MUST go through this one function or an
''' exact match silently stops aligning.
'''
''' The SQL mirror is regexp_replace(btrim(lower(x)), '\s+', ' ', 'g'). The two
''' agree on ASCII input - which is all the corpus contains, measured at zero
''' Unicode-whitespace occurrences across 611,329 condition mentions - and
''' ConditionConceptStoreTests asserts that agreement for the ASCII cases.
'''
''' They DIVERGE on Unicode whitespace: .NET's \s collapses a non-breaking space
''' (U+00A0), Postgres's \s under glibc does not. This function is authoritative,
''' because it also produced the persisted umls.atom.str_norm values for roughly
''' 3 million atoms - changing it would invalidate stored data without a reload.
''' </summary>
Public Module ConceptKey

    Private ReadOnly WhitespaceRegex As New Regex("\s+", RegexOptions.Compiled)

    Public Function Normalize(value As String) As String
        If String.IsNullOrWhiteSpace(value) Then Return ""
        Return WhitespaceRegex.Replace(value.Trim().ToLowerInvariant(), " ")
    End Function

End Module
