using System.Globalization;
using System.Text.Json;
using EligibilityProcessing.Core;
using EligibilityProcessing.Web.Auth;
using EligibilityProcessing.Web.Export;
using EligibilityProcessing.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityProcessing.Web.Controllers;

/// <summary>
/// The Authoring area (authoring specification §6). Lets a user design a new,
/// not-yet-registered study and persist its characteristics + high-level
/// eligibility data. Milestone 1 covers navigation and the Study Setup phase;
/// the Analysis phase actions land in later milestones.
/// </summary>
[Authorize]
public class AuthoringController : Controller
{
    private readonly IPostgresGateway _gateway;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly ICriteriaNormalizer _normalizer;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly IAuditWriter _audit;
    private readonly ILogger<AuthoringController> _logger;

    private static readonly JsonSerializerOptions SourcesJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public AuthoringController(
        IPostgresGateway gateway,
        IEmbeddingClient embeddingClient,
        ICriteriaNormalizer normalizer,
        ICurrentUserAccessor currentUser,
        IAuditWriter audit,
        ILogger<AuthoringController> logger)
    {
        _gateway = gateway;
        _embeddingClient = embeddingClient;
        _normalizer = normalizer;
        _currentUser = currentUser;
        _audit = audit;
        _logger = logger;
    }

    private Guid CurrentUserId => _currentUser.UserId ?? Guid.Empty;

    /// <summary>The /Authoring landing page — the list of authored studies.</summary>
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        try
        {
            var studies = await _gateway.ListAuthoringStudiesAsync(cancellationToken);
            return View(new AuthoringIndexViewModel { Studies = studies });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list authored studies");
            return View(new AuthoringIndexViewModel { ErrorMessage = ex.Message });
        }
    }

    /// <summary>The Setup + Analysis editor for one authored study.</summary>
    public async Task<IActionResult> Edit(CancellationToken cancellationToken, Guid id)
    {
        IReadOnlyList<AuthoringStudySummary> studies = Array.Empty<AuthoringStudySummary>();
        try
        {
            studies = await _gateway.ListAuthoringStudiesAsync(cancellationToken);
            var aggregate = await _gateway.GetAuthoringStudyAsync(id, cancellationToken);
            if (aggregate is null)
            {
                return View(new AuthoringEditViewModel { NotFound = true, Studies = studies });
            }

            return View(new AuthoringEditViewModel
            {
                Aggregate = aggregate,
                Studies = studies,
                StatusMessage = TempData["AuthoringStatus"] as string
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load authored study {Id}", id);
            return View(new AuthoringEditViewModel { ErrorMessage = ex.Message, Studies = studies });
        }
    }

    /// <summary>
    /// Creates a new authored study three ways (authoring specification §3.1.2):
    /// blank, from an AACT trial (copies its snapshot), or from another authored
    /// study (clones its parameters). Redirects to the editor on success.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "AuthorWrite")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        CancellationToken cancellationToken,
        string? mode,
        string? studyId,
        string? label,
        string? sourceRef)
    {
        var trimmedStudyId = studyId?.Trim() ?? "";
        if (trimmedStudyId.Length == 0)
        {
            TempData["AuthoringStatus"] = "Study ID is required.";
            return RedirectToAction(nameof(Index));
        }

        if (await _gateway.StudyIdExistsAsync(trimmedStudyId, cancellationToken))
        {
            TempData["AuthoringStatus"] = $"A study with ID '{trimmedStudyId}' already exists.";
            return RedirectToAction(nameof(Index));
        }

        var newId = Guid.NewGuid();
        var study = new AuthoringStudy
        {
            AuthoringStudyId = newId,
            StudyId = trimmedStudyId,
            Label = string.IsNullOrWhiteSpace(label) ? "Untitled study" : label.Trim(),
            SourceKind = "blank"
        };
        var eligibility = new AuthoringEligibility { AuthoringStudyId = newId };

        try
        {
            var trimmedRef = sourceRef?.Trim() ?? "";
            if (string.Equals(mode, "aact", StringComparison.OrdinalIgnoreCase) &&
                trimmedRef.Length > 0)
            {
                await PopulateFromAactAsync(study, eligibility, trimmedRef, cancellationToken);
            }
            else if (string.Equals(mode, "authored", StringComparison.OrdinalIgnoreCase) &&
                     Guid.TryParse(trimmedRef, out var originId))
            {
                await PopulateFromAuthoredAsync(study, eligibility, originId, cancellationToken);
            }

            await _gateway.CreateAuthoringStudyAsync(study, eligibility, CurrentUserId, cancellationToken);
            await _audit.WriteAsync("create", "authoring_study", newId.ToString(), study.Label, cancellationToken);
            TempData["AuthoringStatus"] = "Study created.";
            return RedirectToAction(nameof(Edit), new { id = newId });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create authored study");
            TempData["AuthoringStatus"] = "Could not create the study: " + ex.Message;
            return RedirectToAction(nameof(Index));
        }
    }

    /// <summary>Persists the Study Setup characteristics form.</summary>
    [HttpPost]
    [Authorize(Policy = "AuthorWrite")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveStudy(
        CancellationToken cancellationToken,
        AuthoringStudyForm form)
    {
        try
        {
            var existing = await _gateway.GetAuthoringStudyAsync(form.Id, cancellationToken);
            if (existing is null)
            {
                return NotFound();
            }

            var study = existing.Study;

            // Study ID is fixed once set. Allow assigning it only when the study
            // has none yet (legacy studies created before the V13 migration);
            // for any study that already has one, the posted value is ignored.
            var newStudyId = form.StudyId?.Trim() ?? "";
            var canSetStudyId = string.IsNullOrWhiteSpace(study.StudyId) && newStudyId.Length > 0;
            if (canSetStudyId && await _gateway.StudyIdExistsAsync(newStudyId, cancellationToken))
            {
                TempData["AuthoringStatus"] = $"A study with ID '{newStudyId}' already exists.";
                return RedirectToAction(nameof(Edit), new { id = form.Id });
            }

            study.Label = string.IsNullOrWhiteSpace(form.Label) ? study.Label : form.Label.Trim();
            study.BriefTitle = form.BriefTitle?.Trim() ?? "";
            study.OfficialTitle = form.OfficialTitle?.Trim() ?? "";
            study.OverallStatus = form.OverallStatus?.Trim() ?? "";
            study.Phase = form.Phase?.Trim() ?? "";
            study.StudyType = form.StudyType?.Trim() ?? "";
            study.StartDate = ParseDate(form.StartDate);
            study.CompletionDate = ParseDate(form.CompletionDate);
            study.PrimaryCompletionDate = ParseDate(form.PrimaryCompletionDate);
            study.Enrollment = ParseInt(form.Enrollment);
            study.EnrollmentType = form.EnrollmentType?.Trim() ?? "";
            study.Source = form.Source?.Trim() ?? "";
            study.WhyStopped = form.WhyStopped?.Trim() ?? "";
            study.BriefSummary = form.BriefSummary?.Trim() ?? "";
            study.Conditions = ParseLines(form.Conditions);
            study.Interventions = ParseInterventions(form.Interventions);

            await _gateway.UpdateAuthoringStudyAsync(study, CurrentUserId, cancellationToken);
            if (canSetStudyId)
            {
                await _gateway.SetAuthoringStudyIdAsync(form.Id, newStudyId, CurrentUserId, cancellationToken);
                await _audit.WriteAsync("update", "authoring_study", form.Id.ToString(), "study_id", cancellationToken);
            }
            await _audit.WriteAsync("update", "authoring_study", form.Id.ToString(), "characteristics", cancellationToken);
            TempData["AuthoringStatus"] = "Study characteristics saved.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save authored study {Id}", form.Id);
            TempData["AuthoringStatus"] = "Save failed: " + ex.Message;
        }

        return RedirectToAction(nameof(Edit), new { id = form.Id });
    }

    /// <summary>Persists the high-level eligibility data form.</summary>
    [HttpPost]
    [Authorize(Policy = "AuthorWrite")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveEligibility(
        CancellationToken cancellationToken,
        AuthoringEligibilityForm form)
    {
        try
        {
            var eligibility = new AuthoringEligibility
            {
                AuthoringStudyId = form.Id,
                Criteria = form.Criteria?.Trim() ?? "",
                Gender = form.Gender?.Trim() ?? "",
                MinimumAge = form.MinimumAge?.Trim() ?? "",
                MaximumAge = form.MaximumAge?.Trim() ?? "",
                HealthyVolunteers = form.HealthyVolunteers?.Trim() ?? "",
                SamplingMethod = form.SamplingMethod?.Trim() ?? "",
                Population = form.Population?.Trim() ?? "",
                Adult = ParseTriBool(form.Adult),
                Child = ParseTriBool(form.Child),
                OlderAdult = ParseTriBool(form.OlderAdult)
            };

            await _gateway.SaveAuthoringEligibilityAsync(eligibility, CurrentUserId, cancellationToken);
            await _audit.WriteAsync("update", "authoring_study", form.Id.ToString(), "eligibility", cancellationToken);
            TempData["AuthoringStatus"] = "Eligibility data saved.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save authored eligibility {Id}", form.Id);
            TempData["AuthoringStatus"] = "Save failed: " + ex.Message;
        }

        return RedirectToAction(nameof(Edit), new { id = form.Id });
    }

    /// <summary>Deletes an authored study (eligibility + criteria cascade).</summary>
    [HttpPost]
    [Authorize(Policy = "AuthorWrite")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(CancellationToken cancellationToken, Guid id)
    {
        try
        {
            await _gateway.DeleteAuthoringStudyAsync(id, cancellationToken);
            await _audit.WriteAsync("delete", "authoring_study", id.ToString(), null, cancellationToken);
            TempData["AuthoringStatus"] = "Study deleted.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete authored study {Id}", id);
            TempData["AuthoringStatus"] = "Delete failed: " + ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    // ===== Analysis phase (authoring specification §3.3–§3.4) =====

    /// <summary>
    /// Embeds the authored study's topic text and returns the most similar
    /// processed studies, ranked by cosine similarity.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Similar(
        CancellationToken cancellationToken,
        Guid id,
        int topN = 20,
        bool matchPhaseType = false)
    {
        try
        {
            var aggregate = await _gateway.GetAuthoringStudyAsync(id, cancellationToken);
            if (aggregate is null)
            {
                return NotFound(new { error = "authored study not found" });
            }

            var s = aggregate.Study;
            var input = new StudyEmbeddingInput(
                nctId: "",
                briefTitle: s.BriefTitle,
                officialTitle: s.OfficialTitle,
                briefSummary: s.BriefSummary,
                conditions: s.Conditions,
                interventions: s.Interventions);
            var text = EmbeddingTextBuilder.Build(input);
            if (string.IsNullOrWhiteSpace(text))
            {
                return BadRequest(new { error = "Add study characteristics (title, summary, conditions) before searching for similar studies." });
            }

            var embedding = await _embeddingClient.EmbedAsync(text, cancellationToken);
            if (!embedding.Succeeded)
            {
                return StatusCode(StatusCodes.Status502BadGateway,
                    new { error = "Embedding endpoint unavailable: " + embedding.ErrorMessage });
            }

            var effectiveTopN = Math.Clamp(topN, 1, 200);
            // "Match Phase and Type" — exact-match filter against the authored
            // study's Phase / StudyType. Empty values disable the filter at
                // the gateway layer.
            var filterPhase = matchPhaseType ? (s.Phase ?? "") : "";
            var filterStudyType = matchPhaseType ? (s.StudyType ?? "") : "";
            var similar = await _gateway.FindSimilarStudiesAsync(
                embedding.Vector, effectiveTopN, cancellationToken,
                filterPhase, filterStudyType);

            return Json(new
            {
                studies = similar.Select(x => new
                {
                    nctId = x.NctId,
                    briefTitle = x.BriefTitle,
                    phase = x.Phase,
                    studyType = x.StudyType,
                    overallStatus = x.OverallStatus,
                    briefSummary = x.BriefSummary,
                    similarity = Math.Round(x.Similarity, 4)
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Similar-study search failed for {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Clusters the common eligibility criteria across the supplied studies,
    /// split into Inclusion and Exclusion, ordered by commonality.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cluster(CancellationToken cancellationToken, string[]? nctIds)
    {
        var ids = (nctIds ?? Array.Empty<string>())
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0)
        {
            return BadRequest(new { error = "Select at least one similar study to cluster." });
        }

        try
        {
            var clusters = await _gateway.ClusterCommonCriteriaAsync(ids, cancellationToken);
            object Project(CriterionCluster c) => new
            {
                criterion = c.Criterion,
                groupKey = c.GroupKey,
                resolved = c.Resolved,
                concept = c.Concept,
                conceptCode = c.ConceptCode,
                semanticType = c.SemanticType,
                studyCount = c.StudyCount,
                recordCount = c.RecordCount
            };

            return Json(new
            {
                studyCount = ids.Length,
                inclusion = clusters
                    .Where(c => string.Equals(c.Criterion, "Inclusion", StringComparison.OrdinalIgnoreCase))
                    .Select(Project),
                exclusion = clusters
                    .Where(c => string.Equals(c.Criterion, "Exclusion", StringComparison.OrdinalIgnoreCase))
                    .Select(Project)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Criteria clustering failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Returns the individual eligibility records behind one criterion cluster
    /// — the expand-panel content for a cluster row.
    /// </summary>
    public async Task<IActionResult> ClusterRecords(
        CancellationToken cancellationToken,
        string[]? nctIds,
        string? criterion,
        string? groupKey)
    {
        var ids = (nctIds ?? Array.Empty<string>())
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0 || string.IsNullOrWhiteSpace(criterion) || string.IsNullOrWhiteSpace(groupKey))
        {
            return BadRequest(new { error = "nctIds, criterion and groupKey are required" });
        }

        try
        {
            var records = await _gateway.GetClusterRecordsAsync(ids, criterion, groupKey, cancellationToken);
            return Json(new
            {
                records = records.Select(r => new
                {
                    nctId = r.NctId,
                    concept = r.Concept,
                    conceptCode = r.ConceptCode,
                    qualifier = r.Qualifier,
                    timeWindow = r.TimeWindow,
                    originalText = r.OriginalText,
                    matchScore = r.MatchScore
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cluster-records lookup failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    /// <summary>
    /// LLM-normalizes the original-text variants behind one criterion cluster
    /// into a single canonical statement (authoring specification §3.5).
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Normalize(
        CancellationToken cancellationToken,
        string[]? nctIds,
        string? criterion,
        string? groupKey)
    {
        var ids = (nctIds ?? Array.Empty<string>())
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0 || string.IsNullOrWhiteSpace(criterion) || string.IsNullOrWhiteSpace(groupKey))
        {
            return BadRequest(new { error = "nctIds, criterion and groupKey are required" });
        }

        try
        {
            var records = await _gateway.GetClusterRecordsAsync(ids, criterion, groupKey, cancellationToken);
            var texts = records
                .Select(r => r.OriginalText?.Trim() ?? "")
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (texts.Count == 0)
            {
                return BadRequest(new { error = "This cluster has no original text to normalize." });
            }

            var result = await _normalizer.NormalizeAsync(texts, cancellationToken);
            if (!result.Succeeded)
            {
                return StatusCode(StatusCodes.Status502BadGateway,
                    new { error = "Normalization failed: " + result.ErrorMessage });
            }

            return Json(new
            {
                normalizedText = result.NormalizedText,
                sourceCount = texts.Count,
                // All records behind the cluster — the lineage the Add button
                // snapshots into authoring_criterion_source. camelCase keys match
                // AuthoringCriterionSourceForm so the payload round-trips on save.
                records = records.Select(r => new
                {
                    eligibilityId = r.Id,
                    nctId = r.NctId,
                    criterion = r.Criterion,
                    domain = r.Domain,
                    concept = r.Concept,
                    conceptCode = r.ConceptCode,
                    semanticType = r.SemanticType,
                    qualifier = r.Qualifier,
                    timeWindow = r.TimeWindow,
                    originalText = r.OriginalText,
                    matchScore = r.MatchScore
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Normalization failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Replaces the authored study's eligibility-criteria list (§3.6).
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "AuthorWrite")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCriteria(
        CancellationToken cancellationToken,
        Guid id,
        List<AuthoringCriterionForm>? criteria)
    {
        try
        {
            var entries = (criteria ?? new List<AuthoringCriterionForm>())
                .Where(c => !string.IsNullOrWhiteSpace(c.NormalizedText))
                .Select((c, index) => new AuthoringCriterion
                {
                    // Round-trip the id so the upsert preserves created_at/created_by
                    // for pre-existing rows; Guid.Empty marks a newly-added entry.
                    AuthoringCriterionId = c.Id ?? Guid.Empty,
                    AuthoringStudyId = id,
                    Ordinal = index,
                    Criterion = string.Equals(c.Criterion, "Exclusion", StringComparison.OrdinalIgnoreCase)
                        ? "Exclusion" : "Inclusion",
                    NormalizedText = c.NormalizedText!.Trim(),
                    Concept = c.Concept?.Trim() ?? "",
                    ConceptCode = c.ConceptCode?.Trim() ?? "",
                    SemanticType = c.SemanticType?.Trim() ?? "",
                    Domain = c.Domain?.Trim() ?? "",
                    SourceNote = c.SourceNote?.Trim() ?? "",
                    ManualReason = c.ManualReason?.Trim() ?? "",
                    Sources = MapSources(c.SourcesJson)
                })
                .ToList<AuthoringCriterion>();

            await _gateway.SaveAuthoringCriteriaAsync(id, entries, CurrentUserId, cancellationToken);
            await _audit.WriteAsync("update", "authoring_study", id.ToString(), $"criteria ({entries.Count})", cancellationToken);
            TempData["AuthoringStatus"] = $"Saved {entries.Count} criterion entr{(entries.Count == 1 ? "y" : "ies")}.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save authored criteria {Id}", id);
            TempData["AuthoringStatus"] = "Save failed: " + ex.Message;
        }

        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>
    /// Exports the study's eligibility-criteria list as CSV (every criterion
    /// column plus the study's id + label). A read action — available to anyone
    /// who can view the study (no AuthorWrite gate).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ExportCriteria(CancellationToken cancellationToken, Guid id)
    {
        try
        {
            var aggregate = await _gateway.GetAuthoringStudyAsync(id, cancellationToken);
            if (aggregate is null)
            {
                return NotFound();
            }

            var csv = AuthoringCriteriaCsv.Build(aggregate.Study, aggregate.Criteria);
            var name = $"{StudyFileNamePart(aggregate.Study)}_Eligibility.csv";
            return ExportResults.CsvFile(csv, name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to export authored criteria {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Exports the study's lineage audit as CSV — a denormalised join of every
    /// eligibility criterion with the source records it was normalised from, one
    /// row per criterion–source pair, each keyed by a persistent deterministic
    /// id. Named <c>{StudyId}_Eligibility_Audit.csv</c>. Destined for an audit table in the
    /// main data fabric. A read action — no AuthorWrite gate (same as
    /// <see cref="ExportCriteria"/>).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ExportCriteriaAudit(CancellationToken cancellationToken, Guid id)
    {
        try
        {
            var aggregate = await _gateway.GetAuthoringStudyAsync(id, cancellationToken);
            if (aggregate is null)
            {
                return NotFound();
            }

            var csv = AuthoringCriteriaAuditCsv.Build(aggregate.Study, aggregate.Criteria);
            var name = $"{StudyFileNamePart(aggregate.Study)}_Eligibility_Audit.csv";
            return ExportResults.CsvFile(csv, name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to export authored criteria audit {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    // Filename-safe slug from the study label, falling back to a short id.
    private static string FileNamePart(string? label, Guid id)
    {
        var slug = new string((label ?? "").Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray())
            .Trim('-');
        return string.IsNullOrEmpty(slug) ? id.ToString("N")[..8] : slug;
    }

    // Filename stem for the study's CSV exports: the user-facing Study ID
    // (sanitised), falling back to the label slug / short id for legacy studies
    // with no Study ID set.
    private static string StudyFileNamePart(AuthoringStudy study)
    {
        var slug = new string((study.StudyId ?? "").Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray())
            .Trim('-');
        return string.IsNullOrEmpty(slug) ? FileNamePart(study.Label, study.AuthoringStudyId) : slug;
    }

    /// <summary>
    /// Deserializes one criterion's lineage payload (the hidden <c>crit-sources</c>
    /// JSON) into snapshot models. Tolerant of null / empty / malformed input —
    /// returns an empty list rather than failing the whole save.
    /// </summary>
    private static List<AuthoringCriterionSource> MapSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<AuthoringCriterionSource>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<AuthoringCriterionSourceForm>>(json, SourcesJsonOptions)
                ?? new List<AuthoringCriterionSourceForm>();
            return parsed.Select(s => new AuthoringCriterionSource
            {
                EligibilityId = s.EligibilityId,
                NctId = s.NctId?.Trim() ?? "",
                Criterion = s.Criterion?.Trim() ?? "",
                Domain = s.Domain?.Trim() ?? "",
                Concept = s.Concept?.Trim() ?? "",
                ConceptCode = s.ConceptCode?.Trim() ?? "",
                SemanticType = s.SemanticType?.Trim() ?? "",
                Qualifier = s.Qualifier?.Trim() ?? "",
                TimeWindow = s.TimeWindow?.Trim() ?? "",
                OriginalText = s.OriginalText ?? "",
                MatchScore = s.MatchScore ?? 0m
            }).ToList();
        }
        catch (JsonException)
        {
            return new List<AuthoringCriterionSource>();
        }
    }

    private async Task PopulateFromAactAsync(
        AuthoringStudy study,
        AuthoringEligibility eligibility,
        string nctId,
        CancellationToken cancellationToken)
    {
        var snapshot = await _gateway.GetStudySnapshotAsync(nctId, cancellationToken);
        var details = snapshot?.Details
            ?? await _gateway.GetStudyDetailsAsync(nctId, cancellationToken);
        var source = snapshot?.Eligibility
            ?? await _gateway.GetSourceEligibilityAsync(nctId, cancellationToken);

        study.SourceKind = "aact";
        study.SourceRef = nctId;

        if (details is not null)
        {
            study.BriefTitle = details.BriefTitle;
            study.OfficialTitle = details.OfficialTitle;
            study.OverallStatus = details.OverallStatus;
            study.Phase = details.Phase;
            study.StudyType = details.StudyType;
            study.StartDate = details.StartDate;
            study.CompletionDate = details.CompletionDate;
            study.PrimaryCompletionDate = details.PrimaryCompletionDate;
            study.Enrollment = details.Enrollment;
            study.EnrollmentType = details.EnrollmentType;
            study.Source = details.Source;
            study.WhyStopped = details.WhyStopped;
            study.BriefSummary = details.BriefSummary;
            study.Conditions = details.Conditions.ToList();
            study.Interventions = details.Interventions.ToList();
        }

        if (source is not null)
        {
            eligibility.Criteria = source.Criteria;
            eligibility.Gender = source.Gender;
            eligibility.MinimumAge = source.MinimumAge;
            eligibility.MaximumAge = source.MaximumAge;
            eligibility.HealthyVolunteers = source.HealthyVolunteers;
            eligibility.SamplingMethod = source.SamplingMethod;
            eligibility.Population = source.Population;
            eligibility.Adult = source.Adult;
            eligibility.Child = source.Child;
            eligibility.OlderAdult = source.OlderAdult;
        }
    }

    private async Task PopulateFromAuthoredAsync(
        AuthoringStudy study,
        AuthoringEligibility eligibility,
        Guid originId,
        CancellationToken cancellationToken)
    {
        var origin = await _gateway.GetAuthoringStudyAsync(originId, cancellationToken);
        if (origin is null)
        {
            return;
        }

        study.SourceKind = "authored";
        study.SourceRef = originId.ToString();

        var os = origin.Study;
        study.BriefTitle = os.BriefTitle;
        study.OfficialTitle = os.OfficialTitle;
        study.OverallStatus = os.OverallStatus;
        study.Phase = os.Phase;
        study.StudyType = os.StudyType;
        study.StartDate = os.StartDate;
        study.CompletionDate = os.CompletionDate;
        study.PrimaryCompletionDate = os.PrimaryCompletionDate;
        study.Enrollment = os.Enrollment;
        study.EnrollmentType = os.EnrollmentType;
        study.Source = os.Source;
        study.WhyStopped = os.WhyStopped;
        study.BriefSummary = os.BriefSummary;
        study.Conditions = os.Conditions.ToList();
        study.Interventions = os.Interventions.ToList();

        var oe = origin.Eligibility;
        eligibility.Criteria = oe.Criteria;
        eligibility.Gender = oe.Gender;
        eligibility.MinimumAge = oe.MinimumAge;
        eligibility.MaximumAge = oe.MaximumAge;
        eligibility.HealthyVolunteers = oe.HealthyVolunteers;
        eligibility.SamplingMethod = oe.SamplingMethod;
        eligibility.Population = oe.Population;
        eligibility.Adult = oe.Adult;
        eligibility.Child = oe.Child;
        eligibility.OlderAdult = oe.OlderAdult;
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed)
            ? parsed.Date
            : null;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static bool? ParseTriBool(string? value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => null
    };

    private static List<string> ParseLines(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

    /// <summary>
    /// Parses the interventions text area — one intervention per line, each
    /// "Type | Name". A line with no pipe is treated as the name with an empty
    /// type, so a hastily-typed list still round-trips.
    /// </summary>
    private static List<Intervention> ParseInterventions(string? value)
    {
        var result = new List<Intervention>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        foreach (var raw in value.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var pipe = line.IndexOf('|');
            if (pipe >= 0)
            {
                var type = line[..pipe].Trim();
                var name = line[(pipe + 1)..].Trim();
                if (name.Length > 0 || type.Length > 0)
                {
                    result.Add(new Intervention(type, name));
                }
            }
            else
            {
                result.Add(new Intervention("", line));
            }
        }

        return result;
    }
}
