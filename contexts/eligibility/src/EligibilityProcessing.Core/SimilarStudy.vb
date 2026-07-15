' One processed AACT study ranked by semantic similarity to a proposed
' authored study (authoring specification §3.3). Returned by
' IPostgresGateway.FindSimilarStudiesAsync, ordered by descending Similarity.

Public NotInheritable Class SimilarStudy

    Public Sub New(
            nctId As String,
            briefTitle As String,
            phase As String,
            studyType As String,
            overallStatus As String,
            briefSummary As String,
            similarity As Double)
        Me.NctId = nctId
        Me.BriefTitle = If(briefTitle, "")
        Me.Phase = If(phase, "")
        Me.StudyType = If(studyType, "")
        Me.OverallStatus = If(overallStatus, "")
        Me.BriefSummary = If(briefSummary, "")
        Me.Similarity = similarity
    End Sub

    Public ReadOnly Property NctId As String
    Public ReadOnly Property BriefTitle As String
    Public ReadOnly Property Phase As String
    Public ReadOnly Property StudyType As String
    Public ReadOnly Property OverallStatus As String
    Public ReadOnly Property BriefSummary As String
    Public ReadOnly Property Similarity As Double   ' cosine similarity, 1 - distance

End Class
