using System.Text.Json;
using Compass.Data;
using Compass.Models;
using Compass.Models.Modern.Work;
using Compass.Models.Raid;
using Compass.Services.Fips;
using Compass.ViewModels.Modern;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Compass.Services.Raid;

public sealed class OperationsRiskEditService(
    CompassDbContext db,
    IRaidRiskEditorFormService raidRiskEditorForm) : IOperationsRiskEditService
{
    private readonly record struct AssociationBind(string StoredKind, int? ProjectId, int? PrimaryProductId);

    public async Task LoadEditorViewBagAsync(
        Controller controller,
        ModernRaidRiskEditorForm form,
        CancellationToken cancellationToken)
    {
        var ownerUserId = form.OwnerUserId;
        var sroUserId = form.SroUserId;

        var tierOptions = await db.RiskTiers.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.IsProposedTier)
            .ThenBy(x => x.Name)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.IsProposedTier ? $"{x.Name} (proposed)" : x.Name })
            .ToListAsync(cancellationToken);
        await EnsureTierOptionAsync(tierOptions, form.RiskTierId, cancellationToken);
        controller.ViewBag.RiskTierOptions = tierOptions;

        controller.ViewBag.RiskLikelihoodOptions = await db.RiskLikelihoods.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label, Description = x.Description, MatrixScore = x.MatrixScore }).ToListAsync(cancellationToken);
        controller.ViewBag.RiskImpactLevelOptions = await db.RiskImpactLevels.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label, Description = x.Description, MatrixScore = x.MatrixScore }).ToListAsync(cancellationToken);
        controller.ViewBag.RiskProximityOptions = await db.RiskProximities.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label, Description = x.Description }).ToListAsync(cancellationToken);
        controller.ViewBag.RiskCategoryOptions = await db.RiskCategories.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label }).ToListAsync(cancellationToken);
        controller.ViewBag.RiskStatusOptions = await db.RiskStatuses.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label }).ToListAsync(cancellationToken);
        controller.ViewBag.RiskPriorityOptions = await db.RiskPriorities.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label, Description = x.Description }).ToListAsync(cancellationToken);
        controller.ViewBag.RiskTreatmentOptions = await db.RiskTreatments.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label, Description = x.Description }).ToListAsync(cancellationToken);
        var projectOptions = await RaidEditorProjectOptionsFullAsync(cancellationToken);
        await EnsureProjectOptionAsync(projectOptions, form.ProjectId, cancellationToken);
        controller.ViewBag.ProjectOptions = projectOptions;

        var productOptions = await RaidFipsProductSelectOptionsAsync(cancellationToken);
        await EnsureProductOptionAsync(productOptions, form.PrimaryProductId, cancellationToken);
        controller.ViewBag.FipsProductOptions = productOptions;

        await PopulateUserPickersAsync(controller, ownerUserId, sroUserId, cancellationToken);

        if (form.Id is int riskId && riskId > 0)
        {
            var riskRow = await db.Risks.AsNoTracking()
                .Include(r => r.RiskTier)
                .Include(r => r.RiskStatus)
                .FirstOrDefaultAsync(r => r.Id == riskId && !r.IsDeleted, cancellationToken);
            if (riskRow != null)
            {
                var score = riskRow.RiskScore;
                controller.ViewBag.OperationsRiskSummary = new OperationsRiskEditorSummaryVm
                {
                    Reference = $"R-{riskRow.Id:D4}",
                    Title = riskRow.Title ?? "",
                    TierName = riskRow.RiskTier?.Name,
                    StatusLabel = riskRow.RiskStatus?.Label ?? riskRow.Status,
                    RiskScore = score,
                    InherentLabel = InherentLabelFromScore(score)
                };
            }
        }

        var likScores = await db.RiskLikelihoods.AsNoTracking()
            .Where(x => x.IsActive).ToDictionaryAsync(x => x.Id, x => x.MatrixScore, cancellationToken);
        var impScores = await db.RiskImpactLevels.AsNoTracking()
            .Where(x => x.IsActive).ToDictionaryAsync(x => x.Id, x => x.MatrixScore, cancellationToken);
        controller.ViewBag.RiskMatrixScoresJson = JsonSerializer.Serialize(new { likelihood = likScores, impact = impScores });

        controller.ViewBag.DivisionOptions = await db.Divisions.AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .Select(d => new RiskIssueNamedIntOption { Id = d.Id, Name = d.Name })
            .ToListAsync(cancellationToken);
        controller.ViewBag.BusinessAreaOptions = await db.BusinessAreaLookups.AsNoTracking()
            .Where(b => b.IsActive)
            .OrderBy(b => b.SortOrder).ThenBy(b => b.Name)
            .Select(b => new RiskIssueNamedIntOption { Id = b.Id, Name = b.Name })
            .ToListAsync(cancellationToken);
    }

    public async Task<ModernRaidRiskEditorForm?> BuildFormAsync(int riskId, CancellationToken cancellationToken)
    {
        var risk = await db.Risks.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == riskId && !r.IsDeleted, cancellationToken);
        if (risk == null)
            return null;

        var catIds = await db.RiskRiskCategories.AsNoTracking()
            .Where(x => x.RiskId == risk.Id)
            .Select(x => x.RiskCategoryId)
            .ToListAsync(cancellationToken);
        if (catIds.Count == 0 && risk.RiskCategoryId.HasValue)
            catIds.Add(risk.RiskCategoryId.Value);
        int? primaryCat = catIds.Count > 0 ? catIds[0] : null;
        int? secondaryCat = catIds.Count > 1 && catIds[1] != catIds[0] ? catIds[1] : null;

        var divisionIds = await db.RiskDivisions.AsNoTracking()
            .Where(x => x.RiskId == risk.Id)
            .Select(x => x.DivisionId)
            .ToListAsync(cancellationToken);
        var businessAreaIds = await db.RiskBusinessAreas.AsNoTracking()
            .Where(x => x.RiskId == risk.Id)
            .Select(x => x.BusinessAreaLookupId)
            .ToListAsync(cancellationToken);

        RaidDateFormHelper.SplitDateParts(risk.IdentifiedDate, out var idd, out var idm, out var idy);
        RaidDateFormHelper.SplitDateParts(risk.NextReviewDate, out var nrd, out var nrm, out var nry);

        var form = new ModernRaidRiskEditorForm
        {
            Id = risk.Id,
            AssociationKind = ToUiAssociationKind(risk.RaidAssociationKind, risk.ProjectId, risk.PrimaryProductId),
            ProjectId = risk.ProjectId,
            PrimaryProductId = risk.PrimaryProductId,
            Title = risk.Title,
            Description = risk.Description,
            Cause = risk.Cause,
            ImpactIfRealised = risk.ImpactIfRealised,
            Contingency = risk.Contingency,
            Assurance = risk.Assurance,
            FinancialImpact = risk.FinancialImpact,
            RiskTierId = risk.RiskTierId,
            RiskStatusId = risk.RiskStatusId,
            RiskPriorityId = risk.RiskPriorityId,
            RiskLikelihoodId = risk.RiskLikelihoodId,
            RiskImpactLevelId = risk.RiskImpactLevelId,
            CurrentLikelihoodId = risk.CurrentLikelihoodId,
            CurrentImpactLevelId = risk.CurrentImpactLevelId,
            ResidualLikelihoodId = risk.ResidualLikelihoodId,
            ResidualImpactLevelId = risk.ResidualImpactLevelId,
            ToleranceLikelihoodId = risk.ToleranceLikelihoodId,
            ToleranceImpactLevelId = risk.ToleranceImpactLevelId,
            RiskProximityId = risk.RiskProximityId,
            RiskTreatmentId = null,
            RiskCategoryIds = catIds,
            PrimaryRiskCategoryId = primaryCat,
            SecondaryRiskCategoryId = secondaryCat,
            DivisionIds = divisionIds,
            BusinessAreaLookupIds = businessAreaIds,
            OwnerUserId = risk.OwnerUserId,
            SroUserId = risk.SroUserId,
            IdentifiedDay = idd,
            IdentifiedMonth = idm,
            IdentifiedYear = idy,
            NextReviewDay = nrd,
            NextReviewMonth = nrm,
            NextReviewYear = nry,
            ResponseStrategy = risk.ResponseStrategy ?? risk.Notes,
            KriItems = await LoadRiskKriItemFormsAsync(risk.Id, cancellationToken)
        };
        if (!string.IsNullOrWhiteSpace(risk.Response))
        {
            var response = risk.Response.Trim().ToLowerInvariant();
            form.RiskTreatmentId = await db.RiskTreatments.AsNoTracking()
                .Where(x => x.IsActive)
                .Where(x => x.Code.ToLower() == response || x.Label.ToLower() == response)
                .OrderBy(x => x.SortOrder)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return form;
    }

    public async Task<bool> TrySaveAsync(
        int riskId,
        ModernRaidRiskEditorForm form,
        string? operationsChangeReason,
        int? editorUserId,
        string? editorEmail,
        ModelStateDictionary modelState,
        CancellationToken cancellationToken)
    {
        form.Id = riskId;
        var reason = (operationsChangeReason ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            modelState.AddModelError(nameof(operationsChangeReason), "Enter a reason for this change.");
            return false;
        }
        if (reason.Length < 10)
        {
            modelState.AddModelError(nameof(operationsChangeReason), "Enter a reason of at least 10 characters.");
            return false;
        }
        if (reason.Length > 4000)
        {
            modelState.AddModelError(nameof(operationsChangeReason), "Reason must be 4,000 characters or fewer.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(form.Title))
            modelState.AddModelError(nameof(form.Title), "Enter a title.");

        var risk = await db.Risks.FirstOrDefaultAsync(r => r.Id == riskId && !r.IsDeleted, cancellationToken);
        if (risk == null)
        {
            modelState.AddModelError(string.Empty, "Risk not found.");
            return false;
        }

        if (!ModernRaidRiskCategoryFormHelper.TryBuildCategoryIdList(
                form.PrimaryRiskCategoryId, form.SecondaryRiskCategoryId, out var opCategoryIdList, out var opCatErr))
        {
            var (ckey, cmsg) = opCatErr!.Value;
            modelState.AddModelError(ckey, cmsg);
        }
        form.DivisionIds ??= new List<int>();
        form.BusinessAreaLookupIds ??= new List<int>();

        DateTime identifiedVal;
        if (!(form.IdentifiedDay.HasValue || form.IdentifiedMonth.HasValue || form.IdentifiedYear.HasValue))
        {
            identifiedVal = risk.IdentifiedDate ?? DateTime.UtcNow.Date;
        }
        else if (!RaidDateFormHelper.TryOptionalDate(
                     form.IdentifiedDay, form.IdentifiedMonth, form.IdentifiedYear, "Identified", modelState,
                     out var identifiedParsed)
                 || !identifiedParsed.HasValue)
        {
            return false;
        }
        else
        {
            identifiedVal = identifiedParsed.Value;
        }

        if (!RaidDateFormHelper.TryOptionalDate(
                form.NextReviewDay, form.NextReviewMonth, form.NextReviewYear, "NextReview", modelState, out var nextReviewDt))
            return false;

        var assocBind = await TryBindAssociationAsync(
            form.AssociationKind,
            form.ProjectId,
            form.PrimaryProductId,
            nameof(ModernRaidRiskEditorForm.ProjectId),
            nameof(ModernRaidRiskEditorForm.PrimaryProductId),
            modelState,
            cancellationToken);

        if (assocBind is not { } a || !modelState.IsValid)
            return false;

        var (likelihoodRating, impactRating, riskScore, inherentScore) =
            await ComputeRaidRiskScoresAsync(form.RiskLikelihoodId, form.RiskImpactLevelId, cancellationToken);
        var currentLikelihoodId = form.CurrentLikelihoodId ?? form.RiskLikelihoodId;
        var currentImpactLevelId = form.CurrentImpactLevelId ?? form.RiskImpactLevelId;
        var currentScore = await ComputeRaidRiskScoreDecimalAsync(currentLikelihoodId, currentImpactLevelId, cancellationToken);
        var residualScore = await ComputeRaidRiskScoreDecimalAsync(form.ResidualLikelihoodId, form.ResidualImpactLevelId, cancellationToken);
        var toleranceScore = await ComputeRaidRiskScoreDecimalAsync(form.ToleranceLikelihoodId, form.ToleranceImpactLevelId, cancellationToken);
        if (RaidRiskScoreRules.ResidualExceedsTolerance(residualScore, toleranceScore))
        {
            RaidRiskScoreRules.AddResidualAboveToleranceError(modelState);
            return false;
        }

        var riskStatusId = form.RiskStatusId ?? await GetDefaultRaidRiskStatusIdAsync(cancellationToken);
        var riskStatusRow = riskStatusId.HasValue
            ? await db.RiskStatuses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == riskStatusId.Value, cancellationToken)
            : null;
        var riskTreatment = form.RiskTreatmentId.HasValue
            ? await db.RiskTreatments.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == form.RiskTreatmentId.Value && x.IsActive, cancellationToken)
            : null;

        var safeEmail = string.IsNullOrWhiteSpace(editorEmail) ? "unknown" : editorEmail.Trim();
        var audit = $"\n\n[Operations update {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC — {safeEmail}]: {reason}";

        risk.ProjectId = a.ProjectId;
        risk.PrimaryProductId = a.PrimaryProductId;
        risk.RaidAssociationKind = a.StoredKind;
        risk.Title = Truncate(form.Title.Trim(), RaidFieldLimits.TitleMaxLength);
        risk.Description = RaidFieldLimits.NormalizeNarrative(form.Description);
        risk.Cause = RaidFieldLimits.NormalizeNarrative(form.Cause);
        risk.ImpactIfRealised = RaidFieldLimits.NormalizeNarrative(form.ImpactIfRealised);
        risk.Contingency = RaidFieldLimits.NormalizeNarrative(form.Contingency);
        risk.Assurance = RaidFieldLimits.NormalizeNarrative(form.Assurance);
        risk.FinancialImpact = RaidFieldLimits.NormalizeNarrative(form.FinancialImpact);
        risk.RiskTierId = form.RiskTierId;
        risk.RiskStatusId = riskStatusId;
        risk.RiskPriorityId = form.RiskPriorityId;
        risk.RiskLikelihoodId = form.RiskLikelihoodId;
        risk.RiskImpactLevelId = form.RiskImpactLevelId;
        risk.CurrentLikelihoodId = currentLikelihoodId;
        risk.CurrentImpactLevelId = currentImpactLevelId;
        risk.CurrentScore = currentScore;
        risk.ResidualLikelihoodId = form.ResidualLikelihoodId;
        risk.ResidualImpactLevelId = form.ResidualImpactLevelId;
        risk.ResidualScore = residualScore;
        risk.ToleranceLikelihoodId = form.ToleranceLikelihoodId;
        risk.ToleranceImpactLevelId = form.ToleranceImpactLevelId;
        risk.ToleranceScore = toleranceScore;
        risk.RiskProximityId = form.RiskProximityId;
        risk.OwnerUserId = form.OwnerUserId > 0 ? form.OwnerUserId : null;
        risk.SroUserId = form.SroUserId > 0 ? form.SroUserId : null;
        risk.ImpactRating = impactRating;
        risk.LikelihoodRating = likelihoodRating;
        risk.RiskScore = riskScore;
        risk.InherentScore = inherentScore;
        risk.Status = TruncateLower(riskStatusRow?.Label ?? risk.Status, 20);
        RaidRiskClosure.SyncClosedDate(risk, riskStatusRow, DateTime.UtcNow);
        risk.Response = riskTreatment != null ? Truncate(riskTreatment.Label, 20) : null;
        var responseStrategy = RaidFieldLimits.NormalizeNarrative(form.ResponseStrategy) ?? string.Empty;
        risk.ResponseStrategy = responseStrategy;
        risk.Notes = string.IsNullOrEmpty(responseStrategy) ? audit.Trim() : responseStrategy + audit;
        risk.IdentifiedDate = identifiedVal;
        risk.NextReviewDate = nextReviewDt;
        risk.UpdatedAt = DateTime.UtcNow;
        risk.UpdatedByUserId = editorUserId;

        await PersistRiskCategoryLinksAsync(risk, opCategoryIdList, cancellationToken);
        await PersistRiskDivisionLinksAsync(risk, form.DivisionIds, cancellationToken);
        await PersistRiskBusinessAreaLinksAsync(risk, form.BusinessAreaLookupIds, cancellationToken);
        await raidRiskEditorForm.PersistRiskKeyRiskIndicatorsAsync(risk.Id, form.KriItems, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<List<RiskKriItemForm>> LoadRiskKriItemFormsAsync(int riskId, CancellationToken cancellationToken)
    {
        var rows = await db.RiskKeyRiskIndicators.AsNoTracking()
            .Where(x => x.RiskId == riskId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .Select(x => new RiskKriItemForm { Metric = x.Metric, Threshold = x.Threshold })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            rows.Add(new RiskKriItemForm());
        return rows;
    }

    private static string ToUiAssociationKind(string? storedKind, int? projectId, int? primaryProductId)
    {
        if (storedKind == RaidAssociationKinds.Product) return "product";
        if (storedKind == RaidAssociationKinds.Organisation) return "organisation";
        if (storedKind == RaidAssociationKinds.WorkItem) return "work";
        if (primaryProductId.HasValue) return "product";
        if (projectId.HasValue) return "work";
        return "organisation";
    }

    private static int MapOrderToFive(int? selectedId, IReadOnlyList<int> orderedIds)
    {
        if (!selectedId.HasValue || orderedIds.Count == 0)
            return 3;
        var idx = -1;
        for (var i = 0; i < orderedIds.Count; i++)
        {
            if (orderedIds[i] != selectedId.Value)
                continue;
            idx = i;
            break;
        }
        if (idx < 0)
            return 3;
        if (orderedIds.Count == 1)
            return 3;
        var scaled = 1d + (double)idx / (orderedIds.Count - 1) * 4d;
        return (int)Math.Round(Math.Clamp(scaled, 1, 5));
    }

    private async Task<(int likelihoodRating, int impactRating, int riskScore, decimal inherentScore)>
        ComputeRaidRiskScoresAsync(int? riskLikelihoodId, int? riskImpactLevelId, CancellationToken cancellationToken)
    {
        var likelihoodOrdered = await db.RiskLikelihoods.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => x.Id).ToListAsync(cancellationToken);
        var impactOrdered = await db.RiskImpactLevels.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => x.Id).ToListAsync(cancellationToken);

        RiskLikelihood? lk = null;
        RiskImpactLevel? im = null;
        if (riskLikelihoodId is > 0)
            lk = await db.RiskLikelihoods.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == riskLikelihoodId.Value, cancellationToken);
        if (riskImpactLevelId is > 0)
            im = await db.RiskImpactLevels.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == riskImpactLevelId.Value, cancellationToken);

        var likelihoodRating = lk != null
            ? (lk.MatrixScore is >= 1 and <= 5
                ? lk.MatrixScore
                : MapOrderToFive(riskLikelihoodId, likelihoodOrdered))
            : MapOrderToFive(riskLikelihoodId, likelihoodOrdered);

        var impactRating = im != null
            ? (im.MatrixScore is >= 1 and <= 5
                ? im.MatrixScore
                : MapOrderToFive(riskImpactLevelId, impactOrdered))
            : MapOrderToFive(riskImpactLevelId, impactOrdered);

        likelihoodRating = Math.Clamp(likelihoodRating, 1, 5);
        impactRating = Math.Clamp(impactRating, 1, 5);

        var riskScore = Math.Clamp(impactRating * likelihoodRating, 1, 25);
        var inherentScore = (decimal)(impactRating * likelihoodRating);
        return (likelihoodRating, impactRating, riskScore, inherentScore);
    }

    private async Task<decimal?> ComputeRaidRiskScoreDecimalAsync(int? likelihoodId, int? impactLevelId, CancellationToken cancellationToken)
    {
        if (!likelihoodId.HasValue || !impactLevelId.HasValue)
            return null;

        var lk = await db.RiskLikelihoods.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == likelihoodId.Value, cancellationToken);
        var im = await db.RiskImpactLevels.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == impactLevelId.Value, cancellationToken);

        if (lk == null || im == null)
            return null;

        return (decimal)(lk.MatrixScore * im.MatrixScore);
    }

    private async Task<int?> GetDefaultRaidRiskStatusIdAsync(CancellationToken cancellationToken)
    {
        var id = await db.RiskStatuses.AsNoTracking()
            .Where(x => x.IsActive && x.Code.ToLower() == "new")
            .OrderBy(x => x.SortOrder)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (id.HasValue)
            return id;
        return await db.RiskStatuses.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<AssociationBind?> TryBindAssociationAsync(
        string? uiAssociationKind,
        int? projectIdForm,
        int? primaryProductIdForm,
        string projectFieldKey,
        string productFieldKey,
        ModelStateDictionary modelState,
        CancellationToken cancellationToken)
    {
        var k = (uiAssociationKind ?? "").Trim().ToLowerInvariant();
        if (k == "organization")
            k = "organisation";

        if (string.IsNullOrEmpty(k))
        {
            modelState.AddModelError("AssociationKind",
                "Select whether this is associated with a work item, a service register product, or organisation.");
            return null;
        }

        switch (k)
        {
            case "product":
                var primaryProductId = primaryProductIdForm is > 0 ? primaryProductIdForm : null;
                if (!primaryProductId.HasValue)
                {
                    modelState.AddModelError(productFieldKey, "Select a service register product.");
                    return null;
                }

                var productOk = await db.Services.AsNoTracking()
                    .AnyAsync(s => s.ServiceId == primaryProductId.Value && s.IsActive, cancellationToken);
                if (!productOk)
                {
                    modelState.AddModelError(productFieldKey, "Select a valid service register product.");
                    return null;
                }

                return new AssociationBind(RaidAssociationKinds.Product, null, primaryProductId);

            case "organisation":
                return new AssociationBind(RaidAssociationKinds.Organisation, null, null);

            case "work":
                var projectId = projectIdForm is > 0 ? projectIdForm : null;
                if (!projectId.HasValue)
                {
                    modelState.AddModelError(projectFieldKey, "Select a work item.");
                    return null;
                }

                var projectOk = await db.Projects.AsNoTracking()
                    .AnyAsync(p => p.Id == projectId.Value && !p.IsDeleted, cancellationToken);
                if (!projectOk)
                {
                    modelState.AddModelError(projectFieldKey, "Select a valid work item.");
                    return null;
                }

                return new AssociationBind(RaidAssociationKinds.WorkItem, projectId, null);

            default:
                modelState.AddModelError("AssociationKind",
                    "Select whether this is associated with a work item, a service register product, or organisation.");
                return null;
        }
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.Trim();
        return t.Length <= max ? t : t[..max];
    }

    private static string TruncateLower(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.Trim().ToLowerInvariant();
        return t.Length <= max ? t : t[..max];
    }

    private async Task PersistRiskCategoryLinksAsync(Risk risk, List<int>? selectedIds, CancellationToken cancellationToken)
    {
        var ids = (selectedIds ?? new List<int>()).Where(i => i > 0).Distinct().ToList();
        var rows = await db.RiskRiskCategories.Where(x => x.RiskId == risk.Id).ToListAsync(cancellationToken);
        db.RiskRiskCategories.RemoveRange(rows);
        foreach (var cid in ids)
            db.RiskRiskCategories.Add(new RiskRiskCategory { RiskId = risk.Id, RiskCategoryId = cid });
        risk.RiskCategoryId = ids.Count > 0 ? ids[0] : null;
    }

    private async Task PersistRiskDivisionLinksAsync(Risk risk, List<int>? selectedIds, CancellationToken cancellationToken)
    {
        var ids = (selectedIds ?? new List<int>()).Where(i => i > 0).Distinct().ToList();
        var rows = await db.RiskDivisions.Where(x => x.RiskId == risk.Id).ToListAsync(cancellationToken);
        db.RiskDivisions.RemoveRange(rows);
        foreach (var divId in ids)
            db.RiskDivisions.Add(new RiskDivision { RiskId = risk.Id, DivisionId = divId });
    }

    private async Task PersistRiskBusinessAreaLinksAsync(Risk risk, List<int>? selectedIds, CancellationToken cancellationToken)
    {
        var ids = (selectedIds ?? new List<int>()).Where(i => i > 0).Distinct().ToList();
        var rows = await db.RiskBusinessAreas.Where(x => x.RiskId == risk.Id).ToListAsync(cancellationToken);
        db.RiskBusinessAreas.RemoveRange(rows);
        foreach (var baId in ids)
            db.RiskBusinessAreas.Add(new RiskBusinessArea { RiskId = risk.Id, BusinessAreaLookupId = baId });
    }

    private Task<List<RiskIssueNamedIntOption>> RaidFipsProductSelectOptionsAsync(CancellationToken cancellationToken) =>
        FipsProductRaidQuery.BuildActiveServiceRegisterSelectOptionsForRaidAsync(db, cancellationToken);

    private Task<List<RiskIssueNamedIntOption>> RaidEditorProjectOptionsFullAsync(CancellationToken cancellationToken) =>
        db.Projects.AsNoTracking()
            .Where(p => !p.IsDeleted)
            .OrderBy(p => p.Title)
            .Select(p => new RiskIssueNamedIntOption { Id = p.Id, Name = p.Title ?? "" })
            .ToListAsync(cancellationToken);

    private async Task PopulateUserPickersAsync(Controller c, int? ownerUserId, int? sroUserId, CancellationToken cancellationToken)
    {
        var ids = new List<int>();
        if (ownerUserId is > 0) ids.Add(ownerUserId.Value);
        if (sroUserId is > 0) ids.Add(sroUserId.Value);
        ids = ids.Distinct().ToList();
        Dictionary<int, User> map = new();
        if (ids.Count > 0)
            map = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, cancellationToken);

        string? DisplayName(User u) => string.IsNullOrWhiteSpace(u.Name) ? u.Email : u.Name;
        User? OwnerRow() => ownerUserId is > 0 && map.TryGetValue(ownerUserId.Value, out var u) ? u : null;
        User? SroRow() => sroUserId is > 0 && map.TryGetValue(sroUserId.Value, out var u) ? u : null;

        c.ViewBag.OwnerUserPicker = new UserPickerViewModel
        {
            FieldName = "OwnerUserId",
            Label = "Who will own this risk day to day?",
            DefaultUserId = ownerUserId,
            DefaultName = OwnerRow() is { } o ? DisplayName(o) : null,
            DefaultEmail = OwnerRow()?.Email,
            InputIdSuffix = "owner",
            UseGovUkStyling = true
        };
        c.ViewBag.SroUserPicker = new UserPickerViewModel
        {
            FieldName = "SroUserId",
            Label = "Senior responsible officer (SRO)",
            DefaultUserId = sroUserId,
            DefaultName = SroRow() is { } s ? DisplayName(s) : null,
            DefaultEmail = SroRow()?.Email,
            InputIdSuffix = "sro",
            UseGovUkStyling = true
        };
    }

    private static string InherentLabelFromScore(int score) =>
        score >= 20
            ? "Crisis / likely"
            : score >= 16
                ? "Critical / possible"
                : score >= 11
                    ? "High / possible"
                    : score >= 6
                        ? "Moderate / possible"
                        : "Low / unlikely";

    private async Task EnsureTierOptionAsync(
        List<RiskIssueNamedIntOption> options,
        int? tierId,
        CancellationToken cancellationToken)
    {
        if (tierId is not > 0 || options.Any(o => o.Id == tierId))
            return;
        var tier = await db.RiskTiers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tierId.Value, cancellationToken);
        if (tier == null)
            return;
        var name = tier.IsProposedTier ? $"{tier.Name} (proposed)" : tier.Name;
        options.Insert(0, new RiskIssueNamedIntOption { Id = tier.Id, Name = name });
    }

    private async Task EnsureProjectOptionAsync(
        List<RiskIssueNamedIntOption> options,
        int? projectId,
        CancellationToken cancellationToken)
    {
        if (projectId is not > 0 || options.Any(o => o.Id == projectId))
            return;
        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId.Value && !p.IsDeleted, cancellationToken);
        if (project == null)
            return;
        options.Insert(0, new RiskIssueNamedIntOption { Id = project.Id, Name = project.Title ?? $"Work item {project.Id}" });
    }

    private async Task EnsureProductOptionAsync(
        List<RiskIssueNamedIntOption> options,
        int? serviceId,
        CancellationToken cancellationToken)
    {
        if (serviceId is not > 0 || options.Any(o => o.Id == serviceId))
            return;
        var service = await db.Services.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ServiceId == serviceId.Value, cancellationToken);
        if (service == null)
            return;
        options.Insert(0, new RiskIssueNamedIntOption
        {
            Id = service.ServiceId,
            Name = service.DisplayName ?? service.FipsId
        });
    }
}
