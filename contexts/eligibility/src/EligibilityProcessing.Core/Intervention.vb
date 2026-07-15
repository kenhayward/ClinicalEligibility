' One row from ctgov.interventions — a treatment, device, behaviour, etc.
' Used inside <see cref="StudyDetails"/> to drive the dashboard's Analysis
' tab; multiple interventions are common (drug + comparator, or arms with
' different doses).

Public NotInheritable Class Intervention

    Public Sub New(interventionType As String, name As String)
        Me.InterventionType = If(interventionType, "")
        Me.Name = If(name, "")
    End Sub

    Public ReadOnly Property InterventionType As String  ' Drug | Device | Behavioral | ...
    Public ReadOnly Property Name As String

End Class
