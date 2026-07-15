' Full ctgov.eligibilities row for one trial. The pipeline only reads
' (nct_id, criteria); this richer projection backs the bottom-left column of
' the dashboard's Analysis tab so a user can see all the structured
' eligibility fields AACT publishes alongside the free-text criteria.

Public NotInheritable Class SourceEligibilityDetails

    Public Sub New(
            nctId As String,
            criteria As String,
            gender As String,
            minimumAge As String,
            maximumAge As String,
            healthyVolunteers As String,
            samplingMethod As String,
            population As String,
            adult As Boolean?,
            child As Boolean?,
            olderAdult As Boolean?)
        Me.NctId = nctId
        Me.Criteria = If(criteria, "")
        Me.Gender = If(gender, "")
        Me.MinimumAge = If(minimumAge, "")
        Me.MaximumAge = If(maximumAge, "")
        Me.HealthyVolunteers = If(healthyVolunteers, "")
        Me.SamplingMethod = If(samplingMethod, "")
        Me.Population = If(population, "")
        Me.Adult = adult
        Me.Child = child
        Me.OlderAdult = olderAdult
    End Sub

    Public ReadOnly Property NctId As String
    Public ReadOnly Property Criteria As String          ' the long free-text the pipeline parses
    Public ReadOnly Property Gender As String            ' "All" | "Male" | "Female"
    Public ReadOnly Property MinimumAge As String        ' AACT publishes as text e.g. "18 Years"
    Public ReadOnly Property MaximumAge As String        ' "N/A" or e.g. "65 Years"
    Public ReadOnly Property HealthyVolunteers As String ' "Yes" | "No" | "Accepts Healthy Volunteers"
    Public ReadOnly Property SamplingMethod As String    ' observational studies only
    Public ReadOnly Property Population As String        ' observational population description
    Public ReadOnly Property Adult As Boolean?
    Public ReadOnly Property Child As Boolean?
    Public ReadOnly Property OlderAdult As Boolean?

End Class
