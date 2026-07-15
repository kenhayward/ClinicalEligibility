Imports System.Collections.Generic
Imports System.Linq
Imports System.Text

' Builds the single text block sent to the embedding model from a study's
' topic fields (authoring specification §3.3.2). Labelled lines, empty parts
' skipped — so a sparse authored study and a fully-populated AACT snapshot
' both produce a coherent document.

Public NotInheritable Class EmbeddingTextBuilder

    Private Sub New()
    End Sub

    Public Shared Function Build(input As StudyEmbeddingInput) As String
        If input Is Nothing Then Return ""
        Dim sb As New StringBuilder()

        AppendLine(sb, "Title", input.BriefTitle)
        ' Skip the official title when it merely repeats the brief title.
        If Not String.Equals(input.OfficialTitle.Trim(), input.BriefTitle.Trim(),
                             StringComparison.OrdinalIgnoreCase) Then
            AppendLine(sb, "Official title", input.OfficialTitle)
        End If
        AppendLine(sb, "Summary", input.BriefSummary)

        Dim conditions = input.Conditions.
                Where(Function(c) Not String.IsNullOrWhiteSpace(c)).
                Select(Function(c) c.Trim())
        AppendLine(sb, "Conditions", String.Join(", ", conditions))

        Dim interventions = input.Interventions.
                Where(Function(i) i IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(i.Name)).
                Select(Function(i) (i.InterventionType & " " & i.Name).Trim())
        AppendLine(sb, "Interventions", String.Join(", ", interventions))

        Return sb.ToString().Trim()
    End Function

    Private Shared Sub AppendLine(sb As StringBuilder, label As String, value As String)
        If String.IsNullOrWhiteSpace(value) Then Return
        sb.Append(label).Append(": ").Append(value.Trim()).Append(vbLf)
    End Sub

End Class
