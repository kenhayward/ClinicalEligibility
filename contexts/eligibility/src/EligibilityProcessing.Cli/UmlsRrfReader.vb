Imports System.Collections.Generic
Imports System.IO
Imports EligibilityProcessing.Data

' Streaming parser for the two UMLS Release Format (RRF) files the load-umls
' command needs. RRF is pipe-delimited with a trailing pipe; STR values never
' contain an unescaped pipe, so a plain Split is safe. Files are streamed line by
' line (File.ReadLines) so a multi-GB MRCONSO never loads into memory.
'
' MRCONSO.RRF columns (0-indexed): 0=CUI 1=LAT 6=ISPREF 11=SAB 12=TTY 14=STR 16=SUPPRESS
' MRSTY.RRF   columns:             0=CUI 1=TUI 3=STY
' MRREL.RRF   columns:             0=CUI1 3=REL 4=CUI2 10=SAB

Friend NotInheritable Class UmlsRrfReader

    ''' <summary>
    ''' Yields one <see cref="AtomRow"/> per MRCONSO atom, filtered to English,
    ''' non-suppressed rows in the given source vocabularies (empty set = all
    ''' English). str_norm is stamped via the store's canonical normalizer so it
    ''' aligns with query-time lookup.
    ''' </summary>
    Public Shared Iterator Function ReadAtoms(
            mrconsoPath As String,
            sourceVocabularies As IReadOnlyCollection(Of String)) As IEnumerable(Of AtomRow)

        Dim sabFilter As New HashSet(Of String)(
                If(sourceVocabularies, CType(Array.Empty(Of String)(), IReadOnlyCollection(Of String))),
                StringComparer.OrdinalIgnoreCase)
        Dim useFilter = sabFilter.Count > 0

        For Each line In File.ReadLines(mrconsoPath)
            Dim f = line.Split("|"c)
            If f.Length < 17 Then Continue For
            If Not String.Equals(f(1), "ENG", StringComparison.Ordinal) Then Continue For      ' LAT
            If Not String.Equals(f(16), "N", StringComparison.Ordinal) Then Continue For        ' SUPPRESS
            Dim sab = f(11)
            If useFilter AndAlso Not sabFilter.Contains(sab) Then Continue For
            Dim str = f(14)
            If String.IsNullOrWhiteSpace(str) Then Continue For

            Yield New AtomRow With {
                .Cui = f(0),
                .Str = str,
                .StrNorm = UmlsMetathesaurusStore.NormalizeConcept(str),
                .Sab = sab,
                .Tty = f(12),
                .IsPref = String.Equals(f(6), "Y", StringComparison.Ordinal)
            }
        Next
    End Function

    ''' <summary>
    ''' Yields one <see cref="ConceptEdgeRow"/> per SNOMED is-a relationship in
    ''' MRREL, normalised so ChildCui is always the more specific concept.
    ''' </summary>
    ''' <remarks>
    ''' MRREL.REL describes the relationship of the SECOND concept to the first:
    ''' (CUI1, 'PAR', CUI2) means CUI2 is the parent of CUI1; 'CHD' is the
    ''' inverse. UMLS stores both directions, so most edges appear twice - the
    ''' staging insert dedupes.
    '''
    ''' Scoped to SAB='SNOMEDCT_US'. UMLS asserts no cross-source hierarchy, so
    ''' mixing vocabularies produces incoherent ancestry and can introduce cycles.
    ''' RB/RN (broader/narrower) are deliberately excluded - they are not the
    ''' is-a hierarchy, and they are the most common relationship in the file.
    ''' </remarks>
    Public Shared Iterator Function ReadRelations(mrrelPath As String) As IEnumerable(Of ConceptEdgeRow)
        For Each line In File.ReadLines(mrrelPath)
            Dim f = line.Split("|"c)
            If f.Length < 11 Then Continue For
            If Not String.Equals(f(10), "SNOMEDCT_US", StringComparison.Ordinal) Then Continue For

            Dim cui1 = f(0)
            Dim rel = f(3)
            Dim cui2 = f(4)
            If String.IsNullOrWhiteSpace(cui1) OrElse String.IsNullOrWhiteSpace(cui2) Then Continue For
            If String.Equals(cui1, cui2, StringComparison.Ordinal) Then Continue For

            If String.Equals(rel, "PAR", StringComparison.Ordinal) Then
                Yield New ConceptEdgeRow With {.ChildCui = cui1, .ParentCui = cui2}
            ElseIf String.Equals(rel, "CHD", StringComparison.Ordinal) Then
                Yield New ConceptEdgeRow With {.ChildCui = cui2, .ParentCui = cui1}
            End If
        Next
    End Function

    ''' <summary>Yields one <see cref="SemanticTypeRow"/> per MRSTY line.</summary>
    Public Shared Iterator Function ReadSemanticTypes(mrstyPath As String) As IEnumerable(Of SemanticTypeRow)
        For Each line In File.ReadLines(mrstyPath)
            Dim f = line.Split("|"c)
            If f.Length < 4 Then Continue For
            If String.IsNullOrWhiteSpace(f(0)) OrElse String.IsNullOrWhiteSpace(f(3)) Then Continue For
            Yield New SemanticTypeRow With {.Cui = f(0), .Tui = f(1), .Sty = f(3)}
        Next
    End Function

End Class
