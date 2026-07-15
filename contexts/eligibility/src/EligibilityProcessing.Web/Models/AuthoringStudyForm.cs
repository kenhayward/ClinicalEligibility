namespace EligibilityProcessing.Web.Models;

/// <summary>
/// Form-post input model for the Study Setup characteristics form. Every field
/// is a string so the raw form values bind cleanly; the controller parses
/// dates / enrollment / the conditions + interventions text areas into the
/// <c>AuthoringStudy</c> domain shape.
/// </summary>
public sealed class AuthoringStudyForm
{
    public Guid Id { get; set; }

    /// <summary>
    /// User-facing Study ID. Only honoured when the study has no Study ID yet
    /// (legacy studies created before the V13 migration); the controller and a
    /// guarded gateway update enforce that an already-set Study ID is immutable.
    /// </summary>
    public string? StudyId { get; set; }

    public string? Label { get; set; }
    public string? BriefTitle { get; set; }
    public string? OfficialTitle { get; set; }
    public string? OverallStatus { get; set; }
    public string? Phase { get; set; }
    public string? StudyType { get; set; }
    public string? StartDate { get; set; }
    public string? CompletionDate { get; set; }
    public string? PrimaryCompletionDate { get; set; }
    public string? Enrollment { get; set; }
    public string? EnrollmentType { get; set; }
    public string? Source { get; set; }
    public string? WhyStopped { get; set; }
    public string? BriefSummary { get; set; }

    /// <summary>One condition name per line.</summary>
    public string? Conditions { get; set; }

    /// <summary>One intervention per line, formatted "Type | Name".</summary>
    public string? Interventions { get; set; }
}
