using System.Security.Claims;
using System.Text.Json;
using Compass.Data;
using Compass.Models;
using Compass.Models.Raid;
using Compass.Models.Modern.Work;
using Compass.Services.Fips;
using Compass.ViewModels.Modern;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services.Raid;

public sealed class RaidRiskEditorFormService(CompassDbContext db) : IRaidRiskEditorFormService
{
    private readonly record struct RaidAssociationBind(string StoredKind, int? ProjectId, int? PrimaryProductId);

    public async Task PrepareRiskEditorLookupsAsync(
        Controller controller,
        int? ownerUserId,
        int? sroUserId,
        CancellationToken cancellationToken)
    {
        controller.ViewBag.RiskTierOptions = await db.RiskTiers.AsNoTracking()
            .Where(x => x.IsActive && !x.IsProposedTier).OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Name }).ToListAsync(cancellationToken);
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
        controller.ViewBag.ProjectOptions = await RaidEditorProjectOptionsFullAsync(cancellationToken);
        controller.ViewBag.FipsProductOptions = await FipsProductRaidQuery.BuildActiveServiceRegisterSelectOptionsForRaidAsync(db, cancellationToken);
        await PopulateRaidUserPickersAsync(controller, ownerUserId, sroUserId, cancellationToken);
        if (controller.ViewBag.OwnerUserPicker is UserPickerViewModel oPick)
            oPick.Label = "Who will own this risk day to day?";

        var likScores = await db.RiskLikelihoods.AsNoTracking()
            .Where(x => x.IsActive).ToDictionaryAsync(x => x.Id, x => x.MatrixScore, cancellationToken);
        var impScores = await db.RiskImpactLevels.AsNoTracking()
            .Where(x => x.IsActive).ToDictionaryAsync(x => x.Id, x => x.MatrixScore, cancellationToken);
        controller.ViewBag.RiskMatrixScoresJson = JsonSerializer.Serialize(new { likelihood = likScores, impact = impScores });
        await LoadRaidDivisionBusinessAreaOptionsAsync(controller, cancellationToken);
    }

    public async Task<IReadOnlyList<RiskIssueNamedIntOption>> BuildRiskCreateTierOptionsAsync(CancellationToken cancellationToken)
    {
        var all = await db.RiskTiers.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var rows = ResolveRaidRiskTierRows(all);
        var options = new List<RiskIssueNamedIntOption>();
        if (rows.Tier3 != null)
            options.Add(new RiskIssueNamedIntOption { Id = rows.Tier3.Id, Name = "Tier 3" });
        if (rows.Tier2Proposed != null)
            options.Add(new RiskIssueNamedIntOption { Id = rows.Tier2Proposed.Id, Name = "Tier 2 - Proposed" });
        if (rows.Tier1Proposed != null)
            options.Add(new RiskIssueNamedIntOption { Id = rows.Tier1Proposed.Id, Name = "Tier 1 - Proposed" });
        return options;
    }

    public async Task<Risk?> TryCreateRiskFromEditorFormAsync(
        ModelStateDictionary modelState,
        ClaimsPrincipal user,
        ModernRaidRiskEditorForm form,
        int? forceWorkProjectId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(form.Title))
            modelState.AddModelError(nameof(form.Title), "Enter a title.");

        RaidAssociationBind? a;
        if (forceWorkProjectId is int forcedPid && forcedPid > 0)
        {
            var projectOk = await db.Projects.AsNoTracking()
                .AnyAsync(p => p.Id == forcedPid && !p.IsDeleted, cancellationToken);
            if (!projectOk)
            {
                modelState.AddModelError(nameof(ModernRaidRiskEditorForm.ProjectId), "Select a valid work item.");
                a = null;
            }
            else
            {
                a = new RaidAssociationBind(RaidAssociationKinds.WorkItem, forcedPid, null);
            }
        }
        else
        {
            a = await TryBindRaidAssociationAsync(
                modelState,
                form.AssociationKind,
                form.ProjectId,
                form.PrimaryProductId,
                nameof(ModernRaidRiskEditorForm.ProjectId),
                nameof(ModernRaidRiskEditorForm.PrimaryProductId),
                cancellationToken);
        }

        if (a is not { } assoc)
            return null;

        form.DivisionIds ??= new List<int>();
        form.BusinessAreaLookupIds ??= new List<int>();
        if (!ModernRaidRiskCategoryFormHelper.TryBuildCategoryIdList(
                form.PrimaryRiskCategoryId, form.SecondaryRiskCategoryId, out var riskCategoryIdList, out var catError))
        {
            var (key, message) = catError!.Value;
            modelState.AddModelError(key, message);
        }

        DateTime identifiedVal;
        if (!(form.IdentifiedDay.HasValue || form.IdentifiedMonth.HasValue || form.IdentifiedYear.HasValue))
        {
            identifiedVal = DateTime.UtcNow.Date;
        }
        else if (!RaidDateFormHelper.TryOptionalDate(form.IdentifiedDay, form.IdentifiedMonth, form.IdentifiedYear, "Identified", modelState, out var idParsed) || !idParsed.HasValue)
        {
            return null;
        }
        else
        {
            identifiedVal = idParsed.Value;
        }

        if (!RaidDateFormHelper.TryOptionalDate(form.NextReviewDay, form.NextReviewMonth, form.NextReviewYear, "NextReview", modelState, out var nextReviewDt))
            return null;

        if (!modelState.IsValid)
            return null;

        var (likelihoodRating, impactRating, riskScore, inherentScore) =
            await ComputeRaidRiskScoresAsync(form.RiskLikelihoodId, form.RiskImpactLevelId, cancellationToken);

        var residualScore = await ComputeRaidRiskScoreDecimalAsync(form.ResidualLikelihoodId, form.ResidualImpactLevelId, cancellationToken);
        var toleranceScore = await ComputeRaidRiskScoreDecimalAsync(form.ToleranceLikelihoodId, form.ToleranceImpactLevelId, cancellationToken);
        if (RaidRiskScoreRules.ResidualExceedsTolerance(residualScore, toleranceScore))
        {
            RaidRiskScoreRules.AddResidualAboveToleranceError(modelState);
            return null;
        }
        var currentLikelihoodId = form.CurrentLikelihoodId ?? form.RiskLikelihoodId;
        var currentImpactLevelId = form.CurrentImpactLevelId ?? form.RiskImpactLevelId;
        var currentScore = await ComputeRaidRiskScoreDecimalAsync(currentLikelihoodId, currentImpactLevelId, cancellationToken);

        var createdByUserId = await ResolveCurrentUserIdAsync(user, cancellationToken);

        var riskStatusId = await GetOpenRiskStatusIdAsync(cancellationToken) ?? await GetDefaultRaidRiskStatusIdAsync(cancellationToken);
        var riskStatusRow = riskStatusId.HasValue
            ? await db.RiskStatuses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == riskStatusId.Value, cancellationToken)
            : null;
        var riskTreatment = form.RiskTreatmentId.HasValue
            ? await db.RiskTreatments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == form.RiskTreatmentId.Value && x.IsActive, cancellationToken)
            : null;

        var now = DateTime.UtcNow;
        var risk = new Risk
        {
            ProjectId = assoc.ProjectId,
            PrimaryProductId = assoc.PrimaryProductId,
            RaidAssociationKind = assoc.StoredKind,
            Title = TruncateRaid(form.Title.Trim(), RaidFieldLimits.TitleMaxLength),
            Description = RaidFieldLimits.NormalizeNarrative(form.Description),
            Cause = RaidFieldLimits.NormalizeNarrative(form.Cause),
            ImpactIfRealised = RaidFieldLimits.NormalizeNarrative(form.ImpactIfRealised),
            Contingency = RaidFieldLimits.NormalizeNarrative(form.Contingency),
            Assurance = RaidFieldLimits.NormalizeNarrative(form.Assurance),
            FinancialImpact = RaidFieldLimits.NormalizeNarrative(form.FinancialImpact),
            ResponseStrategy = RaidFieldLimits.NormalizeNarrative(form.ResponseStrategy),
            RiskTierId = form.RiskTierId,
            RiskStatusId = riskStatusId,
            RiskPriorityId = form.RiskPriorityId,
            RiskLikelihoodId = form.RiskLikelihoodId,
            RiskImpactLevelId = form.RiskImpactLevelId,
            RiskProximityId = form.RiskProximityId,
            OwnerUserId = form.OwnerUserId > 0 ? form.OwnerUserId : null,
            SroUserId = form.SroUserId > 0 ? form.SroUserId : null,
            ImpactRating = impactRating,
            LikelihoodRating = likelihoodRating,
            RiskScore = riskScore,
            InherentScore = inherentScore,
            CurrentLikelihoodId = currentLikelihoodId,
            CurrentImpactLevelId = currentImpactLevelId,
            CurrentScore = currentScore,
            ResidualLikelihoodId = form.ResidualLikelihoodId,
            ResidualImpactLevelId = form.ResidualImpactLevelId,
            ResidualScore = residualScore,
            ToleranceLikelihoodId = form.ToleranceLikelihoodId,
            ToleranceImpactLevelId = form.ToleranceImpactLevelId,
            ToleranceScore = toleranceScore,
            Status = TruncateLowerRaid(riskStatusRow?.Label ?? "new", 20),
            Response = riskTreatment != null ? TruncateRaid(riskTreatment.Label, 20) : null,
            Notes = RaidFieldLimits.NormalizeNarrative(form.ResponseStrategy),
            IdentifiedDate = identifiedVal,
            NextReviewDate = nextReviewDt,
            HowIdentified = "RAID register",
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Risks.Add(risk);
        await db.SaveChangesAsync(cancellationToken);
        await PersistRiskCategoryLinksAsync(risk, riskCategoryIdList, cancellationToken);
        await PersistRiskDivisionLinksAsync(risk, form.DivisionIds, cancellationToken);
        await PersistRiskBusinessAreaLinksAsync(risk, form.BusinessAreaLookupIds, cancellationToken);
        await PersistRiskKeyRiskIndicatorsAsync(risk.Id, form.KriItems, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return risk;
    }

    public async Task PersistRiskKeyRiskIndicatorsAsync(
        int riskId,
        List<RiskKriItemForm>? items,
        CancellationToken cancellationToken)
    {
        items ??= new List<RiskKriItemForm>();
        var existing = await db.RiskKeyRiskIndicators.Where(x => x.RiskId == riskId).ToListAsync(cancellationToken);
        db.RiskKeyRiskIndicators.RemoveRange(existing);

        var now = DateTime.UtcNow;
        var sortOrder = 0;
        foreach (var item in items)
        {
            var metric = string.IsNullOrWhiteSpace(item.Metric) ? null : item.Metric.Trim();
            var threshold = string.IsNullOrWhiteSpace(item.Threshold) ? null : item.Threshold.Trim();
            if (metric == null && threshold == null)
                continue;

            sortOrder++;
            var titleSource = metric ?? threshold ?? "KRI";
            db.RiskKeyRiskIndicators.Add(new RiskKeyRiskIndicator
            {
                RiskId = riskId,
                Title = TruncateRaid(titleSource, 300),
                Metric = RaidFieldLimits.NormalizeNarrative(metric),
                Threshold = RaidFieldLimits.NormalizeNarrative(threshold),
                SortOrder = sortOrder,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
    }

    private async Task<List<RiskIssueNamedIntOption>> RaidEditorProjectOptionsFullAsync(CancellationToken cancellationToken) =>
        await db.Projects.AsNoTracking()
            .Where(p => !p.IsDeleted)
            .OrderBy(p => p.Title)
            .Select(p => new RiskIssueNamedIntOption { Id = p.Id, Name = p.Title ?? "" })
            .ToListAsync(cancellationToken);

    private async Task PopulateRaidUserPickersAsync(
        Controller controller,
        int? ownerUserId,
        int? sroUserId,
        CancellationToken cancellationToken)
    {
        var ids = new List<int>();
        if (ownerUserId is > 0) ids.Add(ownerUserId.Value);
        if (sroUserId is > 0) ids.Add(sroUserId.Value);
        ids = ids.Distinct().ToList();

        Dictionary<int, User> map = new();
        if (ids.Count > 0)
            map = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, cancellationToken);

        static string? DisplayName(User u) => string.IsNullOrWhiteSpace(u.Name) ? u.Email : u.Name;

        User? OwnerRow() => ownerUserId is > 0 && map.TryGetValue(ownerUserId.Value, out var u) ? u : null;
        User? SroRow() => sroUserId is > 0 && map.TryGetValue(sroUserId.Value, out var u) ? u : null;

        controller.ViewBag.OwnerUserPicker = new UserPickerViewModel
        {
            FieldName = "OwnerUserId",
            Label = "Owner",
            DefaultUserId = ownerUserId,
            DefaultName = OwnerRow() is { } o ? DisplayName(o) : null,
            DefaultEmail = OwnerRow()?.Email,
            InputIdSuffix = "owner",
            UseGovUkStyling = true
        };

        controller.ViewBag.SroUserPicker = new UserPickerViewModel
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

    private async Task LoadRaidDivisionBusinessAreaOptionsAsync(Controller controller, CancellationToken cancellationToken)
    {
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

    private sealed record RaidRiskTierRows(
        RiskTier? Tier3,
        RiskTier? Tier2Operational,
        RiskTier? Tier2Proposed,
        RiskTier? Tier1Operational,
        RiskTier? Tier1Proposed);

    private static RaidRiskTierRows ResolveRaidRiskTierRows(IReadOnlyList<RiskTier> all)
    {
        static string NL(RiskTier t) => (t.Name ?? string.Empty).ToLowerInvariant();
        static string CL(RiskTier t) => (t.Code ?? string.Empty).ToLowerInvariant();

        var tier3 = all.FirstOrDefault(t => !t.IsProposedTier && t.GovernanceLevel == 3);
        tier3 ??= all.FirstOrDefault(t => !t.IsProposedTier && (NL(t).Contains("tier 3") || CL(t).Contains("3")));

        var tier2Op = all.FirstOrDefault(t => !t.IsProposedTier && t.GovernanceLevel == 2);
        tier2Op ??= all.FirstOrDefault(t => !t.IsProposedTier && (NL(t).Contains("tier 2") || CL(t) == "2"));

        var tier2Proposed = all.FirstOrDefault(t => t.IsProposedTier && t.GovernanceLevel == 2);
        tier2Proposed ??= all.FirstOrDefault(t => t.IsProposedTier && (NL(t).Contains("tier 2") || CL(t).Contains("2")));

        var tier1Op = all.FirstOrDefault(t => !t.IsProposedTier && t.GovernanceLevel == 1);
        tier1Op ??= all.FirstOrDefault(t => !t.IsProposedTier && (NL(t).Contains("tier 1") || CL(t) == "1"));

        var tier1Proposed = all.FirstOrDefault(t => t.IsProposedTier && t.GovernanceLevel == 1);
        tier1Proposed ??= all.FirstOrDefault(t => t.IsProposedTier && (NL(t).Contains("tier 1") || CL(t).Contains("1")));

        return new RaidRiskTierRows(tier3, tier2Op, tier2Proposed, tier1Op, tier1Proposed);
    }

    private async Task<RaidAssociationBind?> TryBindRaidAssociationAsync(
        ModelStateDictionary modelState,
        string? uiAssociationKind,
        int? projectIdForm,
        int? primaryProductIdForm,
        string projectFieldKey,
        string productFieldKey,
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

                return new RaidAssociationBind(RaidAssociationKinds.Product, null, primaryProductId);

            case "organisation":
                return new RaidAssociationBind(RaidAssociationKinds.Organisation, null, null);

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

                return new RaidAssociationBind(RaidAssociationKinds.WorkItem, projectId, null);

            default:
                modelState.AddModelError("AssociationKind",
                    "Select whether this is associated with a work item, a service register product, or organisation.");
                return null;
        }
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

    private static string TruncateRaid(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.Trim();
        return t.Length <= max ? t : t[..max];
    }

    private static string TruncateLowerRaid(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.Trim().ToLowerInvariant();
        return t.Length <= max ? t : t[..max];
    }

    private static int MapRaidLookupOrderToFive(int? selectedId, IReadOnlyList<int> orderedIds)
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

    private async Task<(int likelihoodRating, int impactRating, int riskScore, decimal inherentScore)> ComputeRaidRiskScoresAsync(
        int? riskLikelihoodId,
        int? riskImpactLevelId,
        CancellationToken cancellationToken)
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
                : MapRaidLookupOrderToFive(riskLikelihoodId, likelihoodOrdered))
            : MapRaidLookupOrderToFive(riskLikelihoodId, likelihoodOrdered);

        var impactRating = im != null
            ? (im.MatrixScore is >= 1 and <= 5
                ? im.MatrixScore
                : MapRaidLookupOrderToFive(riskImpactLevelId, impactOrdered))
            : MapRaidLookupOrderToFive(riskImpactLevelId, impactOrdered);

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

    private async Task<int?> GetOpenRiskStatusIdAsync(CancellationToken cancellationToken) =>
        await db.RiskStatuses.AsNoTracking()
            .Where(x => x.IsActive)
            .Where(x => x.Code.ToLower() == "open" || x.Label.ToLower() == "open")
            .OrderBy(x => x.SortOrder)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<int?> ResolveCurrentUserIdAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var email = user.Identity?.Name
            ?? user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.FindFirst("preferred_username")?.Value;
        if (string.IsNullOrWhiteSpace(email))
            return null;
        email = email.Trim();
        return await db.Users.AsNoTracking()
            .Where(u => u.Email.ToLower() == email.ToLower())
            .Select(u => (int?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
