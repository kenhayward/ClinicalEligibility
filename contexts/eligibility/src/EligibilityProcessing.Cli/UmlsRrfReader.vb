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
