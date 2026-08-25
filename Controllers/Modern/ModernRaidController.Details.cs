using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Compass.Models;
using Compass.Models.Raid;
using Compass.Models.Fips;
using Compass.Models.Modern.Work;
using Compass.Services;
using Compass.Services.Raid;
using Compass.ViewModels.Modern;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace Compass.Controllers.Modern;

/// <summary>RAID detail pages and create/edit forms (partial).</summary>
public partial class ModernRaidController
{
    private string? GetCurrentUserEmailRaw()
    {
        return User.Identity?.Name
            ?? User.FindFirst(ClaimTypes.Email)?.Value
            ?? User.FindFirst("preferred_username")?.Value;
    }

    /// <summary>Owner, creator, SRO, business-area admin (see Admin → Access), or Central Ops / Super admin.</summary>
    private async Task<bool> CurrentUserMayEditIssueAsync(Issue issue, CancellationToken cancellationToken)
    {
        var email = GetCurrentUserEmailRaw();
        if (!_viewAsUser.IsActive(HttpContext)
            && !string.IsNullOrWhiteSpace(email)
            && await _permissions.IsCentralOperationsAdminOrSuperAdminAsync(email.Trim()))
            return true;

        var uid = await ResolveCurrentUserIdAsync(cancellationToken);
        if (!uid.HasValue)
            return false;

        if (issue.OwnerUserId == uid || issue.SroUserId == uid || issue.CreatedByUserId == uid)
            return true;

        var baIds = BusinessAreaAdminHelper.GetBusinessAreaLookupIdsForIssue(issue);
        if (baIds.Count > 0
            && (await _businessAreaAdmins.IsUserAdminForAnyBusinessAreaAsync(uid.Value, baIds, cancellationToken)
                || await _businessAreaLeadership.IsUserLeaderForAnyBusinessAreaAsync(
                    uid.Value, baIds, cancellationToken)))
            return true;

        IReadOnlyList<int> divIds = Array.Empty<int>();
        if (issue.ProjectId is int projectId)
        {
            divIds = issue.Project?.Directorates != null
                ? issue.Project.Directorates.Select(d => d.DivisionId).ToList()
                : await _db.ProjectDirectorates.AsNoTracking()
                    .Where(pd => pd.ProjectId == projectId)
                    .Select(pd => pd.DivisionId)
                    .ToListAsync(cancellationToken);
        }

        return await _directorateLeadership.IsUserDirectorateLeaderForProjectContextAsync(
            uid.Value, divIds, baIds, cancellationToken);
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

    /// <summary>Uses matrix scores from admin when valid (1–5); otherwise falls back to sort-order mapping.</summary>
    private async Task<(int likelihoodRating, int impactRating, int riskScore, decimal inherentScore)> ComputeRaidRiskScoresAsync(
        int? riskLikelihoodId,
        int? riskImpactLevelId,
        CancellationToken cancellationToken)
    {
        var likelihoodOrdered = await _db.RiskLikelihoods.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => x.Id).ToListAsync(cancellationToken);
        var impactOrdered = await _db.RiskImpactLevels.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => x.Id).ToListAsync(cancellationToken);

        RiskLikelihood? lk = null;
        RiskImpactLevel? im = null;
        if (riskLikelihoodId is > 0)
            lk = await _db.RiskLikelihoods.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == riskLikelihoodId.Value, cancellationToken);
        if (riskImpactLevelId is > 0)
            im = await _db.RiskImpactLevels.AsNoTracking()
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

    private async Task<decimal?> ComputeRaidRiskScoreDecimalAsync(
        int? likelihoodId,
        int? impactLevelId,
        CancellationToken cancellationToken)
    {
        if (!likelihoodId.HasValue || !impactLevelId.HasValue)
            return null;

        var lk = await _db.RiskLikelihoods.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == likelihoodId.Value, cancellationToken);
        var im = await _db.RiskImpactLevels.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == impactLevelId.Value, cancellationToken);

        if (lk == null || im == null)
            return null;

        return (decimal)(lk.MatrixScore * im.MatrixScore);
    }

    private async Task<IReadOnlyList<WorkRaidRegisterTrackingVm>> LoadRaidRegistersTrackingRiskAsync(
        int riskId,
        CancellationToken cancellationToken)
    {
        var registerIds = await _db.RaidRegisterRisks.AsNoTracking()
            .Where(rr => rr.RiskId == riskId && !rr.RaidRegister.IsDeleted)
            .Select(rr => rr.RaidRegisterId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (registerIds.Count == 0)
            return Array.Empty<WorkRaidRegisterTrackingVm>();

        var registers = await _db.RaidRegisters.AsNoTracking()
            .Where(r => registerIds.Contains(r.Id))
            .Include(r => r.Users).ThenInclude(u => u.User)
            .Include(r => r.CreatedByUser)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

        return registers.Select(r => new WorkRaidRegisterTrackingVm
        {
            RegisterId = r.Id,
            Name = r.Name,
            OwnerName = ResolveRaidRegisterOwnerName(r),
            DetailUrl = Url.Action("RegisterDetail", "ModernRaid", new { id = r.Id }) ?? "#"
        }).ToList();
    }

    private static string ResolveRaidRegisterOwnerName(RaidRegister register)
    {
        var ownerUser = register.Users
            .FirstOrDefault(u => u.Role == RaidRegisterRole.Owner)?.User;
        if (ownerUser != null)
            return ownerUser.Name ?? ownerUser.Email ?? "Unknown";

        return register.CreatedByUser?.Name
            ?? register.CreatedByUser?.Email
            ?? "Unknown";
    }

    private async Task<int?> GetDefaultRaidRiskStatusIdAsync(CancellationToken cancellationToken)
    {
        var id = await _db.RiskStatuses.AsNoTracking()
            .Where(x => x.IsActive && x.Code.ToLower() == "new")
            .OrderBy(x => x.SortOrder)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (id.HasValue)
            return id;
        return await _db.RiskStatuses.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<int?> GetDefaultRiskStatusForTierAsync(int? riskTierId, CancellationToken cancellationToken)
    {
        if (!riskTierId.HasValue || riskTierId.Value <= 0)
            return await GetDefaultRaidRiskStatusIdAsync(cancellationToken);

        var tier = await _db.RiskTiers.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == riskTierId.Value && t.IsActive, cancellationToken);
        if (tier == null)
            return await GetDefaultRaidRiskStatusIdAsync(cancellationToken);

        if (tier.IsProposedTier)
        {
            var open = await _db.RiskStatuses.AsNoTracking()
                .Where(x => x.IsActive)
                .Where(x => x.Code.ToLower() == "open" || x.Label.ToLower() == "open")
                .OrderBy(x => x.SortOrder)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (open.HasValue)
                return open;
        }
        else
        {
            var nameLower = (tier.Name ?? string.Empty).ToLowerInvariant();
            var codeLower = (tier.Code ?? string.Empty).ToLowerInvariant();
            var isTierThree = tier.GovernanceLevel == 3 || nameLower.Contains("tier 3") || codeLower.Contains("3");
            if (isTierThree)
            {
                var open = await _db.RiskStatuses.AsNoTracking()
                    .Where(x => x.IsActive)
                    .Where(x => x.Code.ToLower() == "open" || x.Label.ToLower() == "open")
                    .OrderBy(x => x.SortOrder)
                    .Select(x => (int?)x.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                if (open.HasValue)
                    return open;
            }
        }

        return await GetDefaultRaidRiskStatusIdAsync(cancellationToken);
    }

    private async Task<int?> GetOpenRiskStatusIdAsync(CancellationToken cancellationToken) =>
        await _db.RiskStatuses.AsNoTracking()
            .Where(x => x.IsActive)
            .Where(x => x.Code.ToLower() == "open" || x.Label.ToLower() == "open")
            .OrderBy(x => x.SortOrder)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private sealed record RaidRiskTierRows(
        RiskTier? Tier3,
        RiskTier? Tier2Operational,
        RiskTier? Tier2Proposed,
        RiskTier? Tier1Operational,
        RiskTier? Tier1Proposed);

    private enum RaidRiskEditTierBand
    {
        OperationalTier3,
        Tier2,
        Tier1
    }

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

    private static RaidRiskEditTierBand ClassifyRiskEditTierBand(RiskTier? current)
    {
        if (current == null)
            return RaidRiskEditTierBand.OperationalTier3;

        if (current.IsProposedTier)
        {
            if (current.GovernanceLevel == 1)
                return RaidRiskEditTierBand.Tier1;
            if (current.GovernanceLevel == 2)
                return RaidRiskEditTierBand.Tier2;
            var nl = (current.Name ?? string.Empty).ToLowerInvariant();
            if (nl.Contains("tier 1"))
                return RaidRiskEditTierBand.Tier1;
            if (nl.Contains("tier 2"))
                return RaidRiskEditTierBand.Tier2;
            return RaidRiskEditTierBand.OperationalTier3;
        }

        if (current.GovernanceLevel == 1)
            return RaidRiskEditTierBand.Tier1;
        if (current.GovernanceLevel == 2)
            return RaidRiskEditTierBand.Tier2;
        if (current.GovernanceLevel == 3)
            return RaidRiskEditTierBand.OperationalTier3;

        var n = (current.Name ?? string.Empty).ToLowerInvariant();
        var c = (current.Code ?? string.Empty).ToLowerInvariant();
        if (n.Contains("tier 1") || c == "1")
            return RaidRiskEditTierBand.Tier1;
        if (n.Contains("tier 2") || c == "2")
            return RaidRiskEditTierBand.Tier2;
        if (n.Contains("tier 3") || c.Contains("3"))
            return RaidRiskEditTierBand.OperationalTier3;

        return RaidRiskEditTierBand.OperationalTier3;
    }

    private static string RiskEditTierDisplayName(RiskTier tierRow)
    {
        if (tierRow.IsProposedTier)
        {
            if (tierRow.GovernanceLevel == 1 || (tierRow.Name ?? string.Empty).ToLowerInvariant().Contains("tier 1"))
                return "Tier 1 - Proposed";
            if (tierRow.GovernanceLevel == 2 || (tierRow.Name ?? string.Empty).ToLowerInvariant().Contains("tier 2"))
                return "Tier 2 - Proposed";
        }
        else
        {
            if (tierRow.GovernanceLevel == 1 || (tierRow.Name ?? string.Empty).ToLowerInvariant().Contains("tier 1"))
                return "Tier 1";
            if (tierRow.GovernanceLevel == 2 || (tierRow.Name ?? string.Empty).ToLowerInvariant().Contains("tier 2"))
                return "Tier 2";
            if (tierRow.GovernanceLevel == 3 || (tierRow.Name ?? string.Empty).ToLowerInvariant().Contains("tier 3"))
                return "Tier 3";
        }

        return string.IsNullOrWhiteSpace(tierRow.Name) ? tierRow.Code : tierRow.Name;
    }

    private async Task<List<RiskIssueNamedIntOption>> BuildRiskEditTierOptionsAsync(int? currentRiskTierId, CancellationToken cancellationToken)
    {
        var allActive = await _db.RiskTiers.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var rows = ResolveRaidRiskTierRows(allActive);

        RiskTier? current = null;
        if (currentRiskTierId is > 0)
        {
            current = await _db.RiskTiers.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == currentRiskTierId.Value, cancellationToken);
        }

        var band = ClassifyRiskEditTierBand(current);
        var list = new List<RiskIssueNamedIntOption>();

        switch (band)
        {
            case RaidRiskEditTierBand.OperationalTier3:
                if (rows.Tier3 != null)
                    list.Add(new RiskIssueNamedIntOption { Id = rows.Tier3.Id, Name = "Tier 3" });
                if (rows.Tier2Proposed != null)
                    list.Add(new RiskIssueNamedIntOption { Id = rows.Tier2Proposed.Id, Name = "Tier 2 - Proposed" });
                if (rows.Tier1Proposed != null)
                    list.Add(new RiskIssueNamedIntOption { Id = rows.Tier1Proposed.Id, Name = "Tier 1 - Proposed" });
                break;
            case RaidRiskEditTierBand.Tier2:
                if (rows.Tier3 != null)
                    list.Add(new RiskIssueNamedIntOption { Id = rows.Tier3.Id, Name = "Tier 3" });
                if (current != null)
                    list.Add(new RiskIssueNamedIntOption { Id = current.Id, Name = RiskEditTierDisplayName(current) });
                else if (rows.Tier2Operational != null)
                    list.Add(new RiskIssueNamedIntOption { Id = rows.Tier2Operational.Id, Name = "Tier 2" });
                else if (rows.Tier2Proposed != null)
                    list.Add(new RiskIssueNamedIntOption { Id = rows.Tier2Proposed.Id, Name = "Tier 2 - Proposed" });
                if (rows.Tier1Proposed != null)
                    list.Add(new RiskIssueNamedIntOption { Id = rows.Tier1Proposed.Id, Name = "Tier 1 - Proposed" });
                break;
            case RaidRiskEditTierBand.Tier1:
                if (rows.Tier3 != null)
                    list.Add(new RiskIssueNamedIntOption { Id = rows.Tier3.Id, Name = "Tier 3" });
                if (rows.Tier2Proposed != null)
                    list.Add(new RiskIssueNamedIntOption { Id = rows.Tier2Proposed.Id, Name = "Tier 2 - Proposed" });
                if (current != null)
                    list.Add(new RiskIssueNamedIntOption { Id = current.Id, Name = RiskEditTierDisplayName(current) });
                else if (rows.Tier1Operational != null)
                    list.Add(new RiskIssueNamedIntOption { Id = rows.Tier1Operational.Id, Name = "Tier 1" });
                else if (rows.Tier1Proposed != null)
                    list.Add(new RiskIssueNamedIntOption { Id = rows.Tier1Proposed.Id, Name = "Tier 1 - Proposed" });
                break;
        }

        if (current != null && list.All(x => x.Id != current.Id))
            list.Insert(0, new RiskIssueNamedIntOption { Id = current.Id, Name = RiskEditTierDisplayName(current) });

        return list;
    }

    private async Task<int?> GetDefaultRaidIssueStatusIdAsync(CancellationToken cancellationToken)
    {
        var id = await _db.IssueStatuses.AsNoTracking()
            .Where(x => x.IsActive && x.Code.ToLower() == "open")
            .OrderBy(x => x.SortOrder)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (id.HasValue)
            return id;
        return await _db.IssueStatuses.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task PrepareRiskEditorLookupsAsync(CancellationToken cancellationToken, int? ownerUserId = null, int? sroUserId = null) =>
        await _raidRiskEditorForm.PrepareRiskEditorLookupsAsync(this, ownerUserId, sroUserId, cancellationToken);

    private async Task PrepareIssueEditorLookupsAsync(CancellationToken cancellationToken, int? ownerUserId = null, int? sroUserId = null) =>
        await _raidIssueEditorForm.PrepareIssueEditorLookupsAsync(this, ownerUserId, sroUserId, cancellationToken);

    private readonly record struct RaidAssociationBind(string StoredKind, int? ProjectId, int? PrimaryProductId);

    private async Task PersistRiskCategoryLinksAsync(Risk risk, List<int>? selectedIds, CancellationToken cancellationToken)
    {
        var ids = (selectedIds ?? new List<int>()).Where(i => i > 0).Distinct().ToList();
        var rows = await _db.RiskRiskCategories.Where(x => x.RiskId == risk.Id).ToListAsync(cancellationToken);
        _db.RiskRiskCategories.RemoveRange(rows);
        foreach (var cid in ids)
            _db.RiskRiskCategories.Add(new RiskRiskCategory { RiskId = risk.Id, RiskCategoryId = cid });
        risk.RiskCategoryId = ids.Count > 0 ? ids[0] : null;
    }

    private async Task PersistIssueCategoryLinksAsync(Issue issue, List<int>? selectedIds, CancellationToken cancellationToken)
    {
        var ids = (selectedIds ?? new List<int>()).Where(i => i > 0).Distinct().ToList();
        var rows = await _db.IssueIssueCategories.Where(x => x.IssueId == issue.Id).ToListAsync(cancellationToken);
        _db.IssueIssueCategories.RemoveRange(rows);
        foreach (var cid in ids)
            _db.IssueIssueCategories.Add(new IssueIssueCategory { IssueId = issue.Id, IssueCategoryId = cid });
        issue.IssueCategoryId = ids.Count > 0 ? ids[0] : null;
    }

    private async Task LoadRaidDivisionBusinessAreaOptionsAsync(CancellationToken cancellationToken)
    {
        ViewBag.DivisionOptions = await _db.Divisions.AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .Select(d => new RiskIssueNamedIntOption { Id = d.Id, Name = d.Name })
            .ToListAsync(cancellationToken);
        ViewBag.BusinessAreaOptions = await _db.BusinessAreaLookups.AsNoTracking()
            .Where(b => b.IsActive)
            .OrderBy(b => b.SortOrder).ThenBy(b => b.Name)
            .Select(b => new RiskIssueNamedIntOption { Id = b.Id, Name = b.Name })
            .ToListAsync(cancellationToken);
    }

    private async Task PersistRiskDivisionLinksAsync(Risk risk, List<int>? selectedIds, CancellationToken cancellationToken)
    {
        var ids = (selectedIds ?? new List<int>()).Where(i => i > 0).Distinct().ToList();
        var rows = await _db.RiskDivisions.Where(x => x.RiskId == risk.Id).ToListAsync(cancellationToken);
        _db.RiskDivisions.RemoveRange(rows);
        foreach (var divId in ids)
            _db.RiskDivisions.Add(new RiskDivision { RiskId = risk.Id, DivisionId = divId });
    }

    private async Task PersistRiskBusinessAreaLinksAsync(Risk risk, List<int>? selectedIds, CancellationToken cancellationToken)
    {
        var ids = (selectedIds ?? new List<int>()).Where(i => i > 0).Distinct().ToList();
        var rows = await _db.RiskBusinessAreas.Where(x => x.RiskId == risk.Id).ToListAsync(cancellationToken);
        _db.RiskBusinessAreas.RemoveRange(rows);
        foreach (var baId in ids)
            _db.RiskBusinessAreas.Add(new RiskBusinessArea { RiskId = risk.Id, BusinessAreaLookupId = baId });
    }

    private async Task PersistIssueDivisionLinksAsync(Issue issue, List<int>? selectedIds, CancellationToken cancellationToken)
    {
        var ids = (selectedIds ?? new List<int>()).Where(i => i > 0).Distinct().ToList();
        var rows = await _db.IssueDivisions.Where(x => x.IssueId == issue.Id).ToListAsync(cancellationToken);
        _db.IssueDivisions.RemoveRange(rows);
        foreach (var divId in ids)
            _db.IssueDivisions.Add(new IssueDivision { IssueId = issue.Id, DivisionId = divId });
    }

    private async Task PersistIssueBusinessAreaLinksAsync(Issue issue, List<int>? selectedIds, CancellationToken cancellationToken)
    {
        var ids = (selectedIds ?? new List<int>()).Where(i => i > 0).Distinct().ToList();
        var rows = await _db.IssueBusinessAreas.Where(x => x.IssueId == issue.Id).ToListAsync(cancellationToken);
        _db.IssueBusinessAreas.RemoveRange(rows);
        foreach (var baId in ids)
            _db.IssueBusinessAreas.Add(new IssueBusinessArea { IssueId = issue.Id, BusinessAreaLookupId = baId });
    }

    /// <summary>Replaces all assurance event rows for an issue. Returns false if validation failed.</summary>
    private async Task<bool> PersistIssueAssuranceEventsAsync(
        int issueId,
        List<IssueAssuranceItemForm>? items,
        CancellationToken cancellationToken)
    {
        items ??= new List<IssueAssuranceItemForm>();
        var existing = await _db.IssueAssuranceEvents.Where(x => x.IssueId == issueId).ToListAsync(cancellationToken);
        _db.IssueAssuranceEvents.RemoveRange(existing);

        var sortOrder = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var title = (it.Title ?? "").Trim();
            var kind = (it.EventKind ?? "").Trim();
            var decision = (it.DecisionSummary ?? "").Trim();
            var hasDatePart = it.EventDay.HasValue || it.EventMonth.HasValue || it.EventYear.HasValue;
            var hasContent = !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(kind)
                || !string.IsNullOrWhiteSpace(decision) || hasDatePart;
            if (!hasContent)
                continue;

            var dateKey = $"AssuranceItems[{i}].EventDate";
            if (!RaidDateFormHelper.TryOptionalDate(it.EventDay, it.EventMonth, it.EventYear, dateKey, ModelState, out var eventDt))
                return false;

            if (string.IsNullOrWhiteSpace(title))
            {
                ModelState.AddModelError($"AssuranceItems[{i}].Title", "Enter a short name for each assurance event (or remove the empty row).");
                return false;
            }

            var kindNorm = string.IsNullOrWhiteSpace(kind) ? "review" : kind;
            if (kindNorm.Length > 50)
                kindNorm = kindNorm[..50];
            var titleSafe = title.Length > 500 ? title[..500] : title;

            _db.IssueAssuranceEvents.Add(new IssueAssuranceEvent
            {
                IssueId = issueId,
                EventKind = kindNorm,
                Title = titleSafe,
                EventDate = eventDt,
                DecisionSummary = string.IsNullOrWhiteSpace(decision) ? null : decision,
                SortOrder = sortOrder++
            });
        }

        return true;
    }

    private async Task PersistAssumptionDivisionLinksAsync(Assumption assumption, List<int>? selectedIds, CancellationToken cancellationToken)
    {
        var ids = (selectedIds ?? new List<int>()).Where(i => i > 0).Distinct().ToList();
        var rows = await _db.AssumptionDivisions.Where(x => x.AssumptionId == assumption.Id).ToListAsync(cancellationToken);
        _db.AssumptionDivisions.RemoveRange(rows);
        foreach (var divId in ids)
            _db.AssumptionDivisions.Add(new AssumptionDivision { AssumptionId = assumption.Id, DivisionId = divId });
    }

    private async Task PersistAssumptionBusinessAreaLinksAsync(Assumption assumption, List<int>? selectedIds, CancellationToken cancellationToken)
    {
        var ids = (selectedIds ?? new List<int>()).Where(i => i > 0).Distinct().ToList();
        var rows = await _db.AssumptionBusinessAreas.Where(x => x.AssumptionId == assumption.Id).ToListAsync(cancellationToken);
        _db.AssumptionBusinessAreas.RemoveRange(rows);
        foreach (var baId in ids)
            _db.AssumptionBusinessAreas.Add(new AssumptionBusinessArea { AssumptionId = assumption.Id, BusinessAreaLookupId = baId });
    }

    private async Task<RaidAssociationBind?> TryBindRaidAssociationAsync(
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
            ModelState.AddModelError("AssociationKind",
                "Select whether this is associated with a work item, a service register product, or organisation.");
            return null;
        }

        switch (k)
        {
            case "product":
                var primaryProductId = primaryProductIdForm is > 0 ? primaryProductIdForm : null;
                if (!primaryProductId.HasValue)
                {
                    ModelState.AddModelError(productFieldKey, "Select a service register product.");
                    return null;
                }

                var productOk = await _db.Services.AsNoTracking()
                    .AnyAsync(s => s.ServiceId == primaryProductId.Value && s.IsActive, cancellationToken);
                if (!productOk)
                {
                    ModelState.AddModelError(productFieldKey, "Select a valid service register product.");
                    return null;
                }

                return new RaidAssociationBind(RaidAssociationKinds.Product, null, primaryProductId);

            case "organisation":
                return new RaidAssociationBind(RaidAssociationKinds.Organisation, null, null);

            case "work":
                var projectId = projectIdForm is > 0 ? projectIdForm : null;
                if (!projectId.HasValue)
                {
                    ModelState.AddModelError(projectFieldKey, "Select a work item.");
                    return null;
                }

                var projectOk = await _db.Projects.AsNoTracking()
                    .AnyAsync(p => p.Id == projectId.Value && !p.IsDeleted, cancellationToken);
                if (!projectOk)
                {
                    ModelState.AddModelError(projectFieldKey, "Select a valid work item.");
                    return null;
                }

                return new RaidAssociationBind(RaidAssociationKinds.WorkItem, projectId, null);

            default:
                ModelState.AddModelError("AssociationKind",
                    "Select whether this is associated with a work item, a service register product, or organisation.");
                return null;
        }
    }

    private async Task PrepareDependencyEditorLookupsAsync(CancellationToken cancellationToken)
    {
        ViewBag.LinkTypeOptions = await _db.DependencyLinkTypes.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label }).ToListAsync(cancellationToken);
        ViewBag.CriticalityOptions = await _db.DependencyCriticalities.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label }).ToListAsync(cancellationToken);
    }

    [HttpGet("risks/create")]
    public async Task<IActionResult> RiskCreate(
        [FromQuery] string? associationKind,
        [FromQuery] int? projectId,
        [FromQuery] int? primaryProductId,
        CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-risks");
        await PrepareRiskEditorLookupsAsync(cancellationToken);
        RaidDateFormHelper.SplitDateParts(DateTime.UtcNow.Date, out var idd, out var idm, out var idy);
        var form = new ModernRaidRiskEditorForm
        {
            AssociationKind = "organisation",
            IdentifiedDay = idd,
            IdentifiedMonth = idm,
            IdentifiedYear = idy,
            KriItems = new List<RiskKriItemForm> { new() }
        };
        var ak = (associationKind ?? "").Trim().ToLowerInvariant();
        if (ak == "product" && primaryProductId is > 0)
        {
            form.AssociationKind = "product";
            form.PrimaryProductId = primaryProductId;
        }
        else if (ak == "work" && projectId is > 0)
        {
            form.AssociationKind = "work";
            form.ProjectId = projectId;
        }
        else if (ak == "organisation" || ak == "organization")
        {
            form.AssociationKind = "organisation";
        }

        ViewBag.RiskTierOptions = (await _raidRiskEditorForm.BuildRiskCreateTierOptionsAsync(cancellationToken)).ToList();
        ViewBag.EditorTitle = "Add risk";
        return View("~/Views/Modern/Raid/RiskEditor.cshtml", form);
    }

    [HttpPost("risks/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RiskCreate([FromForm] ModernRaidRiskEditorForm form, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-risks");
        await PrepareRiskEditorLookupsAsync(cancellationToken, form.OwnerUserId, form.SroUserId);
        ViewBag.RiskTierOptions = (await _raidRiskEditorForm.BuildRiskCreateTierOptionsAsync(cancellationToken)).ToList();
        ViewBag.EditorTitle = "Add risk";

        try
        {
            var risk = await _raidRiskEditorForm.TryCreateRiskFromEditorFormAsync(
                ModelState, User, form, forceWorkProjectId: null, cancellationToken);
            if (risk == null)
                return View("~/Views/Modern/Raid/RiskEditor.cshtml", form);

            TempData["Message"] = "Risk created.";
            return RedirectToAction(nameof(RiskDetail), new { id = risk.Id });
        }
        catch (Exception)
        {
            ModelState.AddModelError(
                string.Empty,
                "Unable to save the risk. Try again or contact support if the problem continues.");
            return View("~/Views/Modern/Raid/RiskEditor.cshtml", form);
        }
    }

    [HttpGet("risks/{id:int}")]
    public async Task<IActionResult> RiskDetail(int id, string? tab, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-risks");
        var risk = await _db.Risks.AsNoTracking()
            .Include(r => r.Project)!.ThenInclude(p => p!.BusinessAreaLookup)
            .Include(r => r.Project)!.ThenInclude(p => p!.PrimaryOrganizationalGroup)
            .Include(r => r.Project)!.ThenInclude(p => p!.PhaseLookup)
            .Include(r => r.Project)!.ThenInclude(p => p!.RagStatusLookup)
            .Include(r => r.Project)!.ThenInclude(p => p!.DeliveryPriority)
            .Include(r => r.PrimaryProduct)
            .Include(r => r.RiskTier)
            .Include(r => r.RiskStatus)
            .Include(r => r.RiskPriority)
            .Include(r => r.OwnerUser)
            .Include(r => r.SroUser)
            .Include(r => r.CreatedByUser)
            .Include(r => r.Likelihood)
            .Include(r => r.ImpactLevel)
            .Include(r => r.CurrentLikelihood)
            .Include(r => r.CurrentImpactLevel)
            .Include(r => r.ResidualLikelihoodLevel)
            .Include(r => r.ResidualImpactLevel)
            .Include(r => r.ToleranceLikelihood)
            .Include(r => r.ToleranceImpactLevel)
            .Include(r => r.Proximity)
            .Include(r => r.RiskCategory)
            .Include(r => r.RiskRiskCategories).ThenInclude(x => x.RiskCategory)
            .Include(r => r.RiskDivisions).ThenInclude(x => x.Division)
            .Include(r => r.RiskBusinessAreas).ThenInclude(x => x.BusinessAreaLookup)
            .Include(r => r.RiskActions).ThenInclude(ra => ra.Action).ThenInclude(a => a.AssignedToUser)
            .Include(r => r.UpdatedByUser)
            .Include(r => r.KeyRiskIndicators)
            .Include(r => r.GovernanceBoard)
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, cancellationToken);
        if (risk == null)
            return NotFound();

        await RaidRiskMitigations.AttachOrphansInMemoryAsync(_db, risk, cancellationToken);

        var ownerForDisplay = risk.OwnerUser;
        if (ownerForDisplay == null && !string.IsNullOrWhiteSpace(risk.OwnerEmail))
        {
            var ownerEmailNorm = risk.OwnerEmail.Trim().ToLowerInvariant();
            ownerForDisplay = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(
                    u => u.Email != null && u.Email.ToLower() == ownerEmailNorm,
                    cancellationToken);
        }
        ViewBag.OwnerForDisplay = ownerForDisplay;

        ViewBag.LinkedIssues = await _db.Issues.AsNoTracking()
            .Where(i => !i.IsDeleted && (
                i.SourceRiskId == id ||
                i.RiskId == id ||
                i.IssueRisks.Any(ir => ir.RiskId == id)))
            .OrderByDescending(i => i.UpdatedAt)
            .Take(24)
            .Select(i => new RiskLinkedIssueCardVm(
                i.Id,
                i.Title,
                i.StatusLookup != null ? i.StatusLookup.Label : i.Status,
                i.SeverityLookup != null ? i.SeverityLookup.Label : i.Severity,
                i.PriorityLookup != null ? i.PriorityLookup.Label : i.Priority,
                i.UpdatedAt,
                i.SourceRiskId == id
                    ? "Raised from this risk"
                    : i.RiskId == id
                        ? "Primary linked issue"
                        : "Related issue"))
            .ToListAsync(cancellationToken);
        var activeTabNorm = string.IsNullOrWhiteSpace(tab) ? "detail" : tab.Trim().ToLowerInvariant();
        ViewBag.ActiveTab = activeTabNorm;
        ViewBag.RiskAssociationUiKind = ToRaidAssociationUiKind(risk.RaidAssociationKind, risk.ProjectId, risk.PrimaryProductId);
        if (string.Equals(activeTabNorm, "linked", StringComparison.Ordinal))
        {
            await PrepareRiskEditorLookupsAsync(cancellationToken, risk.OwnerUserId, risk.SroUserId);
            ViewBag.RiskLinkedAssociationEditor = true;
        }

        Guid? linkedCmdbProductId = null;
        string? linkedCmdbProductTitle = null;
        string? linkedCmdbPhaseName = null;
        if (risk.PrimaryProductId is int svcId && svcId > 0)
        {
            var svc = risk.PrimaryProduct;
            var cmdbByService = await _db.CMDBProducts.AsNoTracking()
                .Include(p => p.Phase)
                .FirstOrDefaultAsync(p => p.UniqueID == svcId, cancellationToken);
            var cmdb = cmdbByService;
            if (cmdb == null && !string.IsNullOrWhiteSpace(svc?.FipsId))
            {
                var fid = svc!.FipsId.Trim();
                cmdb = await _db.CMDBProducts.AsNoTracking()
                    .Include(p => p.Phase)
                    .FirstOrDefaultAsync(p => p.CMDBID == fid, cancellationToken);
            }

            if (cmdb != null)
            {
                linkedCmdbProductId = cmdb.Id;
                linkedCmdbProductTitle = cmdb.Title;
                linkedCmdbPhaseName = cmdb.Phase?.Name;
            }
        }

        ViewBag.LinkedCmdbProductId = linkedCmdbProductId;
        ViewBag.LinkedCmdbProductTitle = linkedCmdbProductTitle;
        ViewBag.LinkedCmdbPhaseName = linkedCmdbPhaseName;

        ViewBag.MitigationAddOwnerUserPicker = MitigationAddOwnerPicker();

        var orderedTierIds = await _db.RiskTiers.AsNoTracking()
            .Where(t => t.IsActive && !t.IsProposedTier)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
        ViewBag.TierBadgeStep = MapRiskTierToBadgeStep(risk.RiskTier, orderedTierIds, risk.RiskTierId);
        ViewBag.RatingLabel = RaidRiskRatingLabel(risk);
        ViewBag.OpenDurationText = FormatRiskOpenDuration(risk.IdentifiedDate ?? risk.CreatedAt);
        ViewBag.RiskHistoryTimeline = await RiskAuditTimelineBuilder.BuildAsync(_db, id, cancellationToken);

        var pendingEscalation = await _db.RaidEscalationTierChangeRequests.AsNoTracking()
            .Where(x => x.RiskId == id && x.Status == "pending")
            .Include(x => x.FromRiskTier)
            .Include(x => x.ToRiskTier)
            .Include(x => x.SubmittedByUser)
            .OrderByDescending(x => x.SubmittedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (pendingEscalation != null)
        {
            var tierList = await _db.RiskTiers.AsNoTracking()
                .Where(t => t.IsActive)
                .ToListAsync(cancellationToken);
            var from = pendingEscalation.FromRiskTier;
            var toT = pendingEscalation.ToRiskTier;
            var fromName = from?.Name;

            string requestLine;
            if (from != null && toT != null)
            {
                if (RiskTierGovernance.IsEscalation(from, toT, tierList))
                    requestLine = $"Request to escalate to {toT.Name} from {from.Name}.";
                else if (RiskTierGovernance.ResolveLevel(toT, tierList) > RiskTierGovernance.ResolveLevel(from, tierList))
                    requestLine = $"Request to de-escalate to {toT.Name} from {from.Name}.";
                else
                    requestLine = $"Request to change to {toT.Name} from {from.Name}.";
            }
            else if (toT != null)
            {
                requestLine = string.IsNullOrEmpty(fromName)
                    ? $"Request to change to {toT.Name}."
                    : $"Request to change to {toT.Name} from {fromName}.";
            }
            else
            {
                requestLine = "Pending tier change request (target tier missing).";
            }

            ViewBag.PendingRiskTierChange = new RiskPendingTierChangeVm(requestLine);
        }
        else
        {
            ViewBag.PendingRiskTierChange = null;
        }

        var materialisedIssueId = await _db.Issues.AsNoTracking()
            .Where(i => !i.IsDeleted && i.SourceRiskId == id)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => (int?)i.Id)
            .FirstOrDefaultAsync(cancellationToken);
        ViewBag.RiskMaterialisedIssueId = materialisedIssueId;
        ViewBag.RiskCanMakeIssue = !risk.ClosedDate.HasValue && !materialisedIssueId.HasValue;
        ViewBag.RiskTrackingRegisters = await LoadRaidRegistersTrackingRiskAsync(id, cancellationToken);

        return View("~/Views/Modern/Raid/RiskDetail.cshtml", risk);
    }

    [HttpPost("risks/{id:int}/association")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RiskAssociationUpdate(
        int id,
        [FromForm] string? associationKind,
        [FromForm] int? projectId,
        [FromForm] int? primaryProductId,
        CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-risks");
        var risk = await _db.Risks.FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, cancellationToken);
        if (risk == null)
            return NotFound();

        ModelState.Clear();
        var bind = await TryBindRaidAssociationAsync(
            associationKind,
            projectId,
            primaryProductId,
            nameof(ModernRaidRiskEditorForm.ProjectId),
            nameof(ModernRaidRiskEditorForm.PrimaryProductId),
            cancellationToken);

        if (bind is not { } a)
        {
            var msg = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault()
                ?? "Check your selection and try again.";
            TempData["ErrorMessage"] = msg;
            return RedirectToAction(nameof(RiskDetail), new { id, tab = "linked" });
        }

        risk.ProjectId = a.ProjectId;
        risk.PrimaryProductId = a.PrimaryProductId;
        risk.RaidAssociationKind = a.StoredKind;
        risk.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = "Primary link updated.";
        return RedirectToAction(nameof(RiskDetail), new { id, tab = "linked" });
    }

    /// <summary>Maps to badge colour: 1=red, 2=yellow, 3=green. Uses "Tier 1" / "Tier 1 - Proposed" in the name, then list position.</summary>
    private static int MapRiskTierToBadgeStep(RiskTier? currentTier, IReadOnlyList<int> orderedNonProposedTierIds, int? currentTierId)
    {
        if (currentTier != null)
        {
            var fromName = TryParseTierGovernanceLevelFromName(currentTier.Name);
            if (fromName is 1 or 2 or 3)
                return fromName.Value;
        }
        if (!currentTierId.HasValue || orderedNonProposedTierIds.Count == 0)
            return 2;
        var idx = -1;
        for (var i = 0; i < orderedNonProposedTierIds.Count; i++)
        {
            if (orderedNonProposedTierIds[i] != currentTierId.Value)
                continue;
            idx = i;
            break;
        }
        if (idx < 0)
            return 2;
        if (orderedNonProposedTierIds.Count == 1)
            return 2;
        if (idx == 0)
            return 1;
        if (idx == orderedNonProposedTierIds.Count - 1)
            return 3;
        return 2;
    }

    /// <summary>Resolves 1, 2, or 3 from display names (e.g. "Tier 1", "Tier 1 - Proposed", "TIER 2 — Proposed", "T 3").</summary>
    private static int? TryParseTierGovernanceLevelFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var n = name.Trim();
        var m = Regex.Match(n, @"\b[Tt]ier\s*([1-3])\b", RegexOptions.CultureInvariant);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var a) && a is >= 1 and <= 3)
            return a;
        m = Regex.Match(n, @"\bT\s*([1-3])\b", RegexOptions.CultureInvariant);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var b) && b is >= 1 and <= 3)
            return b;
        return null;
    }

    private static string FormatRiskOpenDuration(DateTime startUtc)
    {
        var now = DateTime.UtcNow;
        if (startUtc > now)
            return "—";
        var days = (int)Math.Floor((now - startUtc).TotalDays);
        if (days < 1)
            return "Open less than 1 day";
        if (days < 30)
            return days == 1 ? "1 day open" : $"{days} days open";
        var y0 = startUtc.Year;
        var m0 = startUtc.Month;
        var d0 = startUtc.Day;
        var y1 = now.Year;
        var m1 = now.Month;
        var d1 = now.Day;
        var totalMonths = (y1 - y0) * 12 + (m1 - m0);
        if (d1 < d0)
            totalMonths--;
        if (totalMonths < 12)
            return totalMonths == 1 ? "1 month open" : $"{totalMonths} months open";
        var years = y1 - y0;
        if (m1 < m0 || (m1 == m0 && d1 < d0))
            years--;
        if (years < 1)
        {
            return totalMonths == 1 ? "1 month open" : $"{totalMonths} months open";
        }
        return years == 1 ? "1 year open" : $"{years} years open";
    }

    [HttpGet("risks/{id:int}/edit")]
    public async Task<IActionResult> RiskEdit(int id, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-risks");
        var risk = await _db.Risks.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, cancellationToken);
        if (risk == null)
            return NotFound();

        await PrepareRiskEditorLookupsAsync(cancellationToken, risk.OwnerUserId, risk.SroUserId);

        var catIds = await _db.RiskRiskCategories.AsNoTracking()
            .Where(x => x.RiskId == risk.Id)
            .Select(x => x.RiskCategoryId)
            .ToListAsync(cancellationToken);
        if (catIds.Count == 0 && risk.RiskCategoryId.HasValue)
            catIds.Add(risk.RiskCategoryId.Value);
        int? primaryCat = catIds.Count > 0 ? catIds[0] : null;
        int? secondaryCat = catIds.Count > 1 && catIds[1] != catIds[0] ? catIds[1] : null;

        var divisionIds = await _db.RiskDivisions.AsNoTracking()
            .Where(x => x.RiskId == risk.Id)
            .Select(x => x.DivisionId)
            .ToListAsync(cancellationToken);
        var businessAreaIds = await _db.RiskBusinessAreas.AsNoTracking()
            .Where(x => x.RiskId == risk.Id)
            .Select(x => x.BusinessAreaLookupId)
            .ToListAsync(cancellationToken);

        RaidDateFormHelper.SplitDateParts(risk.IdentifiedDate, out var idd, out var idm, out var idy);
        RaidDateFormHelper.SplitDateParts(risk.NextReviewDate, out var nrd, out var nrm, out var nry);

        var form = new ModernRaidRiskEditorForm
        {
            Id = risk.Id,
            AssociationKind = ToRaidAssociationUiKind(risk.RaidAssociationKind, risk.ProjectId, risk.PrimaryProductId),
            ProjectId = risk.ProjectId,
            PrimaryProductId = risk.PrimaryProductId,
            Title = risk.Title,
            Description = risk.Description,
            Cause = risk.Cause,
            ImpactIfRealised = risk.ImpactIfRealised,
            Contingency = risk.Contingency,
            Assurance = risk.Assurance,
            FinancialImpact = risk.FinancialImpact,
            KriItems = await LoadRiskKriItemFormsAsync(risk.Id, cancellationToken),
            RiskTierId = risk.RiskTierId,
            RiskStatusId = risk.RiskStatusId,
            RiskPriorityId = risk.RiskPriorityId,
            RiskLikelihoodId = risk.RiskLikelihoodId,
            RiskImpactLevelId = risk.RiskImpactLevelId,
            CurrentLikelihoodId = risk.CurrentLikelihoodId,
            CurrentImpactLevelId = risk.CurrentImpactLevelId,
            RiskProximityId = risk.RiskProximityId,
            ResidualLikelihoodId = risk.ResidualLikelihoodId,
            ResidualImpactLevelId = risk.ResidualImpactLevelId,
            ToleranceLikelihoodId = risk.ToleranceLikelihoodId,
            ToleranceImpactLevelId = risk.ToleranceImpactLevelId,
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
            ResponseStrategy = risk.ResponseStrategy ?? risk.Notes
        };
        if (!string.IsNullOrWhiteSpace(risk.Response))
        {
            var response = risk.Response.Trim().ToLowerInvariant();
            form.RiskTreatmentId = await _db.RiskTreatments.AsNoTracking()
                .Where(x => x.IsActive)
                .Where(x => x.Code.ToLower() == response || x.Label.ToLower() == response)
                .OrderBy(x => x.SortOrder)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        ViewBag.EditorTitle = "Edit risk";
        return View("~/Views/Modern/Raid/RiskEditor.cshtml", form);
    }

    [HttpPost("risks/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RiskEditPost(int id, [FromForm] ModernRaidRiskEditorForm form, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-risks");
        await PrepareRiskEditorLookupsAsync(cancellationToken, form.OwnerUserId, form.SroUserId);
        ViewBag.EditorTitle = "Edit risk";
        form.Id = id;

        var risk = await _db.Risks.FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, cancellationToken);
        if (risk == null)
            return NotFound();

        form.DivisionIds ??= new List<int>();
        form.BusinessAreaLookupIds ??= new List<int>();
        if (!ModernRaidRiskCategoryFormHelper.TryBuildCategoryIdList(
                form.PrimaryRiskCategoryId, form.SecondaryRiskCategoryId, out var riskEditCategoryIdList, out var editCatError))
        {
            var (key, message) = editCatError!.Value;
            ModelState.AddModelError(key, message);
        }

        DateTime identifiedVal;
        if (!(form.IdentifiedDay.HasValue || form.IdentifiedMonth.HasValue || form.IdentifiedYear.HasValue))
        {
            identifiedVal = risk.IdentifiedDate ?? DateTime.UtcNow.Date;
        }
        else if (!RaidDateFormHelper.TryOptionalDate(form.IdentifiedDay, form.IdentifiedMonth, form.IdentifiedYear, "Identified", ModelState, out var identifiedParsed) || !identifiedParsed.HasValue)
        {
            return View("~/Views/Modern/Raid/RiskEditor.cshtml", form);
        }
        else
        {
            identifiedVal = identifiedParsed.Value;
        }

        if (!RaidDateFormHelper.TryOptionalDate(form.NextReviewDay, form.NextReviewMonth, form.NextReviewYear, "NextReview", ModelState, out var nextReviewDt))
            return View("~/Views/Modern/Raid/RiskEditor.cshtml", form);

        var assocBind = await TryBindRaidAssociationAsync(
            form.AssociationKind,
            form.ProjectId,
            form.PrimaryProductId,
            nameof(ModernRaidRiskEditorForm.ProjectId),
            nameof(ModernRaidRiskEditorForm.PrimaryProductId),
            cancellationToken);

        if (assocBind is not { } a || !ModelState.IsValid)
            return View("~/Views/Modern/Raid/RiskEditor.cshtml", form);

        var (likelihoodRating, impactRating, riskScore, inherentScore) =
            await ComputeRaidRiskScoresAsync(form.RiskLikelihoodId, form.RiskImpactLevelId, cancellationToken);
        var residualScore = await ComputeRaidRiskScoreDecimalAsync(
            form.ResidualLikelihoodId, form.ResidualImpactLevelId, cancellationToken);
        var toleranceScore = await ComputeRaidRiskScoreDecimalAsync(
            form.ToleranceLikelihoodId, form.ToleranceImpactLevelId, cancellationToken);
        if (RaidRiskScoreRules.ResidualExceedsTolerance(residualScore, toleranceScore))
        {
            RaidRiskScoreRules.AddResidualAboveToleranceError(ModelState);
            return View("~/Views/Modern/Raid/RiskEditor.cshtml", form);
        }
        var currentLikelihoodId = form.CurrentLikelihoodId ?? form.RiskLikelihoodId;
        var currentImpactLevelId = form.CurrentImpactLevelId ?? form.RiskImpactLevelId;
        var currentScore = await ComputeRaidRiskScoreDecimalAsync(
            currentLikelihoodId, currentImpactLevelId, cancellationToken);

        var riskStatusId = form.RiskStatusId ?? await GetDefaultRaidRiskStatusIdAsync(cancellationToken);
        var riskStatusRow = riskStatusId.HasValue
            ? await _db.RiskStatuses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == riskStatusId.Value, cancellationToken)
            : null;
        var riskTreatment = form.RiskTreatmentId.HasValue
            ? await _db.RiskTreatments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == form.RiskTreatmentId.Value && x.IsActive, cancellationToken)
            : null;

        risk.ProjectId = a.ProjectId;
        risk.PrimaryProductId = a.PrimaryProductId;
        risk.RaidAssociationKind = a.StoredKind;
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
        risk.RiskProximityId = form.RiskProximityId;
        risk.ResidualLikelihoodId = form.ResidualLikelihoodId;
        risk.ResidualImpactLevelId = form.ResidualImpactLevelId;
        risk.ResidualScore = residualScore;
        risk.ToleranceLikelihoodId = form.ToleranceLikelihoodId;
        risk.ToleranceImpactLevelId = form.ToleranceImpactLevelId;
        risk.ToleranceScore = toleranceScore;
        risk.OwnerUserId = form.OwnerUserId > 0 ? form.OwnerUserId : null;
        risk.SroUserId = form.SroUserId > 0 ? form.SroUserId : null;
        risk.ImpactRating = impactRating;
        risk.LikelihoodRating = likelihoodRating;
        risk.RiskScore = riskScore;
        risk.InherentScore = inherentScore;
        risk.Status = TruncateLowerRaid(riskStatusRow?.Label ?? risk.Status, 20);
        RaidRiskClosure.SyncClosedDate(risk, riskStatusRow, DateTime.UtcNow);
        risk.Response = riskTreatment != null ? TruncateRaid(riskTreatment.Label, 20) : null;
        risk.ResponseStrategy = RaidFieldLimits.NormalizeNarrative(form.ResponseStrategy);
        risk.IdentifiedDate = identifiedVal;
        risk.NextReviewDate = nextReviewDt;
        risk.UpdatedAt = DateTime.UtcNow;

        await PersistRiskCategoryLinksAsync(risk, riskEditCategoryIdList, cancellationToken);
        await PersistRiskDivisionLinksAsync(risk, form.DivisionIds, cancellationToken);
        await PersistRiskBusinessAreaLinksAsync(risk, form.BusinessAreaLookupIds, cancellationToken);
        await _raidRiskEditorForm.PersistRiskKeyRiskIndicatorsAsync(risk.Id, form.KriItems, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = "Risk updated.";
        return RedirectToAction(nameof(RiskDetail), new { id });
    }

    private async Task<List<RiskKriItemForm>> LoadRiskKriItemFormsAsync(int riskId, CancellationToken cancellationToken)
    {
        var rows = await _db.RiskKeyRiskIndicators.AsNoTracking()
            .Where(x => x.RiskId == riskId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .Select(x => new RiskKriItemForm { Metric = x.Metric, Threshold = x.Threshold })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            rows.Add(new RiskKriItemForm());
        return rows;
    }

    [HttpPost("risks/{riskId:int}/key-risk-indicators/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RiskKeyRiskIndicatorAdd(
        int riskId,
        [FromForm] string? title,
        [FromForm] string? description,
        [FromForm] string? metric,
        [FromForm] string? threshold,
        CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-risks");
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(metric) && string.IsNullOrWhiteSpace(threshold))
        {
            TempData["ErrorMessage"] = "Enter a title or metric for the key risk indicator.";
            return RedirectToAction(nameof(RiskDetail), new { id = riskId, tab = "kris" });
        }

        var riskExists = await _db.Risks.AsNoTracking()
            .AnyAsync(r => r.Id == riskId && !r.IsDeleted, cancellationToken);
        if (!riskExists)
            return NotFound();

        var metricNorm = RaidFieldLimits.NormalizeNarrative(metric);
        var thresholdNorm = RaidFieldLimits.NormalizeNarrative(threshold);
        var titleSource = !string.IsNullOrWhiteSpace(title)
            ? title.Trim()
            : metricNorm ?? thresholdNorm ?? "KRI";
        var t = TruncateRaid(titleSource, 300);
        var maxOrder = await _db.RiskKeyRiskIndicators
            .Where(x => x.RiskId == riskId)
            .Select(x => (int?)x.SortOrder)
            .MaxAsync(cancellationToken) ?? 0;
        var now = DateTime.UtcNow;
        var descNorm = RaidFieldLimits.NormalizeNarrative(description);

        _db.RiskKeyRiskIndicators.Add(new RiskKeyRiskIndicator
        {
            RiskId = riskId,
            Title = t,
            Description = descNorm,
            Metric = metricNorm,
            Threshold = thresholdNorm,
            SortOrder = maxOrder + 1,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = "Key risk indicator added.";
        return RedirectToAction(nameof(RiskDetail), new { id = riskId, tab = "kris" });
    }

    [HttpPost("risks/{riskId:int}/key-risk-indicators/{kriId:int}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RiskKeyRiskIndicatorRemove(
        int riskId,
        int kriId,
        CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-risks");
        var row = await _db.RiskKeyRiskIndicators
            .FirstOrDefaultAsync(x => x.Id == kriId && x.RiskId == riskId, cancellationToken);
        if (row == null)
            return NotFound();

        _db.RiskKeyRiskIndicators.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = "Key risk indicator removed.";
        return RedirectToAction(nameof(RiskDetail), new { id = riskId, tab = "kris" });
    }

    [HttpGet("risks/{id:int}/make-issue")]
    public async Task<IActionResult> RiskMakeIssue(int id, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-risks");
        var risk = await _db.Risks.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, cancellationToken);
        if (risk == null)
            return NotFound();

        var existingIssue = await _db.Issues.AsNoTracking()
            .Where(i => !i.IsDeleted && i.SourceRiskId == id)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new { i.Id })
            .FirstOrDefaultAsync(cancellationToken);

        var vm = new ModernRaidMakeIssueFromRiskPageVm
        {
            RiskId = risk.Id,
            RiskReference = $"R-{risk.Id:D4}",
            Title = risk.Title,
            Description = risk.Description,
            Cause = risk.Cause,
            ImpactIfRealised = risk.ImpactIfRealised,
            RiskIsClosed = risk.ClosedDate.HasValue,
            HasExistingMaterialisedIssue = existingIssue != null,
            ExistingIssueId = existingIssue?.Id,
            ExistingIssueReference = existingIssue != null ? $"I-{existingIssue.Id:D4}" : null
        };

        return View("~/Views/Modern/Raid/MakeIssueFromRisk.cshtml", vm);
    }

    [HttpPost("risks/{id:int}/make-issue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RiskMakeIssuePost(
        int id,
        [FromForm] ModernRaidMakeIssueFromRiskForm form,
        CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-risks");
        form.RiskId = id;

        var risk = await _db.Risks.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, cancellationToken);
        if (risk == null)
            return NotFound();

        var issue = await _raidIssueEditorForm.TryCreateIssueFromRiskAsync(ModelState, User, form, cancellationToken);
        if (issue == null)
        {
            var existingIssue = await _db.Issues.AsNoTracking()
                .Where(i => !i.IsDeleted && i.SourceRiskId == id)
                .OrderByDescending(i => i.CreatedAt)
                .Select(i => new { i.Id })
                .FirstOrDefaultAsync(cancellationToken);
            var vm = new ModernRaidMakeIssueFromRiskPageVm
            {
                RiskId = risk.Id,
                RiskReference = $"R-{risk.Id:D4}",
                Title = risk.Title,
                Description = risk.Description,
                Cause = risk.Cause,
                ImpactIfRealised = risk.ImpactIfRealised,
                RiskIsClosed = risk.ClosedDate.HasValue,
                HasExistingMaterialisedIssue = existingIssue != null,
                ExistingIssueId = existingIssue?.Id,
                ExistingIssueReference = existingIssue != null ? $"I-{existingIssue.Id:D4}" : null
            };
            return View("~/Views/Modern/Raid/MakeIssueFromRisk.cshtml", vm);
        }

        var riskMsg = string.Equals(form.RiskAfterIssue, "keep_open", StringComparison.OrdinalIgnoreCase)
            ? "Issue created. The risk remains open."
            : "Issue created. The risk has been closed as materialised.";
        TempData["Message"] = riskMsg;
        return RedirectToAction(nameof(IssueDetail), new { id = issue.Id });
    }

    [HttpGet("issues/create")]
    public async Task<IActionResult> IssueCreate(
        [FromQuery] string? associationKind,
        [FromQuery] int? projectId,
        [FromQuery] int? primaryProductId,
        CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-issues");
        await PrepareIssueEditorLookupsAsync(cancellationToken);
        var form = new ModernRaidIssueEditorForm { AssociationKind = "organisation" };
        var ak = (associationKind ?? "").Trim().ToLowerInvariant();
        if (ak == "product" && primaryProductId is > 0)
        {
            form.AssociationKind = "product";
            form.PrimaryProductId = primaryProductId;
        }
        else if (ak == "work" && projectId is > 0)
        {
            form.AssociationKind = "work";
            form.ProjectId = projectId;
        }
        else if (ak == "organisation" || ak == "organization")
        {
            form.AssociationKind = "organisation";
        }

        ViewBag.EditorTitle = "Add issue";
        form.AssuranceItems = new List<IssueAssuranceItemForm> { new IssueAssuranceItemForm() };
        return View("~/Views/Modern/Raid/IssueEditor.cshtml", form);
    }

    [HttpPost("issues/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> IssueCreatePost([FromForm] ModernRaidIssueEditorForm form, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-issues");
        await PrepareIssueEditorLookupsAsync(cancellationToken, form.OwnerUserId, form.SroUserId);
        ViewBag.EditorTitle = "Add issue";

        var issue = await _raidIssueEditorForm.TryCreateIssueFromEditorFormAsync(ModelState, User, form, forceWorkProjectId: null, cancellationToken);
        if (issue == null)
        {
            if (form.AssuranceItems == null || form.AssuranceItems.Count == 0)
                form.AssuranceItems = new List<IssueAssuranceItemForm> { new IssueAssuranceItemForm() };
            return View("~/Views/Modern/Raid/IssueEditor.cshtml", form);
        }

        TempData["Message"] = "Issue created.";
        return RedirectToAction(nameof(IssueDetail), new { id = issue.Id });
    }

    [HttpGet("issues/{id:int}")]
    public async Task<IActionResult> IssueDetail(int id, string? tab, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-issues");
        var issue = await _db.Issues.AsNoTracking()
            .Include(i => i.Project)!.ThenInclude(p => p!.BusinessAreaLookup)
            .Include(i => i.Project)!.ThenInclude(p => p!.PrimaryOrganizationalGroup)
            .Include(i => i.PrimaryProduct)
            .Include(i => i.StatusLookup)
            .Include(i => i.PriorityLookup)
            .Include(i => i.SeverityLookup)
            .Include(i => i.CategoryLookup)
            .Include(i => i.IssueIssueCategories).ThenInclude(x => x.IssueCategory)
            .Include(i => i.IssueDivisions).ThenInclude(x => x.Division)
            .Include(i => i.IssueBusinessAreas).ThenInclude(x => x.BusinessAreaLookup)
            .Include(i => i.OwnerUser)
            .Include(i => i.SroUser)
            .Include(i => i.CreatedByUser)
            .Include(i => i.UpdatedByUser)
            .Include(i => i.IssueActions).ThenInclude(x => x.Action)
            .Include(i => i.AssuranceEvents)
            .FirstOrDefaultAsync(i => i.Id == id && !i.IsDeleted, cancellationToken);
        if (issue == null)
            return NotFound();
        ViewBag.OriginRisk = issue.SourceRiskId.HasValue
            ? await _db.Risks.AsNoTracking()
                .Include(r => r.RiskStatus)
                .Where(r => !r.IsDeleted && r.Id == issue.SourceRiskId.Value)
                .Select(r => new { r.Id, r.Title, r.RiskScore, Status = r.RiskStatus != null ? r.RiskStatus.Label : r.Status })
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        ViewBag.ActiveTab = string.IsNullOrWhiteSpace(tab) ? "detail" : tab.Trim().ToLowerInvariant();

        var orderedSeverityIds = await _db.IssueSeverities.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        ViewBag.IssueSeverityBadgeStep = MapIssueSeverityToBadgeStep(orderedSeverityIds, issue.SeverityId);
        ViewBag.ResolutionTierLabel = RaidIssueTierLabel(issue);
        ViewBag.OpenDurationPhrase = FormatIssueOpenPhrase(issue.DetectedDate, issue.ClosedDate);
        ViewBag.DaysOpenCount = IssueDaysOpen(issue.DetectedDate, issue.ClosedDate);
        ViewBag.CanChangeIssueDetail = await CurrentUserMayEditIssueAsync(issue, cancellationToken);
        return View("~/Views/Modern/Raid/IssueDetail.cshtml", issue);
    }

    private static int MapIssueSeverityToBadgeStep(IReadOnlyList<int> orderedSeverityIds, int? severityId)
    {
        if (!severityId.HasValue || orderedSeverityIds.Count == 0)
            return 2;
        var idx = -1;
        for (var i = 0; i < orderedSeverityIds.Count; i++)
        {
            if (orderedSeverityIds[i] != severityId.Value)
                continue;
            idx = i;
            break;
        }
        if (idx < 0)
            return 2;
        if (orderedSeverityIds.Count == 1)
            return 2;
        if (idx == 0)
            return 3;
        if (idx == orderedSeverityIds.Count - 1)
            return 1;
        return 2;
    }

    private static int IssueDaysOpen(DateTime detectedUtc, DateTime? closedUtc)
    {
        var end = closedUtc ?? DateTime.UtcNow;
        if (end < detectedUtc)
            return 0;
        return (int)Math.Floor((end - detectedUtc).TotalDays);
    }

    private static string FormatIssueOpenPhrase(DateTime detectedUtc, DateTime? closedUtc)
    {
        if (closedUtc.HasValue)
        {
            var days = IssueDaysOpen(detectedUtc, closedUtc);
            return days == 1 ? "Open for 1 day before closure" : $"Open for {days} days before closure";
        }
        var d = IssueDaysOpen(detectedUtc, null);
        if (d == 0)
            return "Open today";
        return d == 1 ? "Open for 1 day" : $"Open for {d} days";
    }

    [HttpGet("issues/{id:int}/edit")]
    public async Task<IActionResult> IssueEdit(int id, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-issues");
        var issue = await _db.Issues.AsNoTracking()
            .Include(i => i.Project)
            .Include(i => i.IssueBusinessAreas)
            .FirstOrDefaultAsync(i => i.Id == id && !i.IsDeleted, cancellationToken);
        if (issue == null)
            return NotFound();
        if (!await CurrentUserMayEditIssueAsync(issue, cancellationToken))
            return Forbid();

        await PrepareIssueEditorLookupsAsync(cancellationToken, issue.OwnerUserId, issue.SroUserId);

        var catIds = await _db.IssueIssueCategories.AsNoTracking()
            .Where(x => x.IssueId == issue.Id)
            .Select(x => x.IssueCategoryId)
            .ToListAsync(cancellationToken);
        if (catIds.Count == 0 && issue.IssueCategoryId.HasValue)
            catIds.Add(issue.IssueCategoryId.Value);

        var issueDivisionIds = await _db.IssueDivisions.AsNoTracking()
            .Where(x => x.IssueId == issue.Id)
            .Select(x => x.DivisionId)
            .ToListAsync(cancellationToken);
        var issueBusinessAreaIds = await _db.IssueBusinessAreas.AsNoTracking()
            .Where(x => x.IssueId == issue.Id)
            .Select(x => x.BusinessAreaLookupId)
            .ToListAsync(cancellationToken);

        RaidDateFormHelper.SplitDateParts(issue.TargetResolutionDate, out var trd, out var trm, out var trYear);

        var assuranceRows = await _db.IssueAssuranceEvents.AsNoTracking()
            .Where(x => x.IssueId == issue.Id)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var assuranceForms = assuranceRows.Select(a =>
        {
            RaidDateFormHelper.SplitDateParts(a.EventDate, out var ed, out var em, out var ey);
            return new IssueAssuranceItemForm
            {
                EventKind = a.EventKind,
                Title = a.Title,
                EventDay = ed,
                EventMonth = em,
                EventYear = ey,
                DecisionSummary = a.DecisionSummary
            };
        }).ToList();
        if (assuranceForms.Count == 0)
            assuranceForms.Add(new IssueAssuranceItemForm());

        var form = new ModernRaidIssueEditorForm
        {
            Id = issue.Id,
            AssociationKind = ToRaidAssociationUiKind(issue.RaidAssociationKind, issue.ProjectId, issue.PrimaryProductId),
            ProjectId = issue.ProjectId,
            PrimaryProductId = issue.PrimaryProductId,
            Title = issue.Title,
            Description = issue.Description,
            StatusId = issue.StatusId,
            SeverityId = issue.SeverityId,
            PriorityId = issue.PriorityId,
            IssueCategoryIds = catIds,
            DivisionIds = issueDivisionIds,
            BusinessAreaLookupIds = issueBusinessAreaIds,
            OwnerUserId = issue.OwnerUserId,
            SroUserId = issue.SroUserId,
            TargetResolutionDay = trd,
            TargetResolutionMonth = trm,
            TargetResolutionYear = trYear,
            Workaround = issue.Workaround,
            DetailedCause = issue.DetailedCause,
            AssuranceArrangements = issue.AssuranceArrangements,
            AssuranceItems = assuranceForms
        };
        ViewBag.EditorTitle = "Edit issue";
        return View("~/Views/Modern/Raid/IssueEditor.cshtml", form);
    }

    [HttpPost("issues/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> IssueEditPost(int id, [FromForm] ModernRaidIssueEditorForm form, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-issues");
        await PrepareIssueEditorLookupsAsync(cancellationToken, form.OwnerUserId, form.SroUserId);
        ViewBag.EditorTitle = "Edit issue";
        form.Id = id;

        if (string.IsNullOrWhiteSpace(form.Title))
            ModelState.AddModelError(nameof(form.Title), "Enter a title.");
        var assocBind = await TryBindRaidAssociationAsync(
            form.AssociationKind,
            form.ProjectId,
            form.PrimaryProductId,
            nameof(ModernRaidIssueEditorForm.ProjectId),
            nameof(ModernRaidIssueEditorForm.PrimaryProductId),
            cancellationToken);
        if (!form.SeverityId.HasValue || form.SeverityId <= 0)
            ModelState.AddModelError(nameof(form.SeverityId), "Select severity.");
        if (!form.PriorityId.HasValue || form.PriorityId <= 0)
            ModelState.AddModelError(nameof(form.PriorityId), "Select priority.");

        var issue = await _db.Issues
            .Include(i => i.Project)
            .Include(i => i.IssueBusinessAreas)
            .FirstOrDefaultAsync(i => i.Id == id && !i.IsDeleted, cancellationToken);
        if (issue == null)
            return NotFound();
        if (!await CurrentUserMayEditIssueAsync(issue, cancellationToken))
            return Forbid();

        form.IssueCategoryIds ??= new List<int>();
        form.DivisionIds ??= new List<int>();
        form.BusinessAreaLookupIds ??= new List<int>();
        form.AssuranceItems ??= new List<IssueAssuranceItemForm>();

        if (!RaidDateFormHelper.TryOptionalDate(form.TargetResolutionDay, form.TargetResolutionMonth, form.TargetResolutionYear, "TargetResolution", ModelState, out var targetResolutionDt))
            return View("~/Views/Modern/Raid/IssueEditor.cshtml", form);

        if (assocBind is not { } a || !ModelState.IsValid)
            return View("~/Views/Modern/Raid/IssueEditor.cshtml", form);

        var issueStatusId = form.StatusId ?? await GetDefaultRaidIssueStatusIdAsync(cancellationToken);
        var sevRow = form.SeverityId.HasValue
            ? await _db.IssueSeverities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == form.SeverityId.Value, cancellationToken)
            : null;
        var priRow = form.PriorityId.HasValue
            ? await _db.IssuePriorities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == form.PriorityId.Value, cancellationToken)
            : null;
        var stRow = issueStatusId.HasValue
            ? await _db.IssueStatuses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == issueStatusId.Value, cancellationToken)
            : null;

        issue.ProjectId = a.ProjectId;
        issue.PrimaryProductId = a.PrimaryProductId;
        issue.RaidAssociationKind = a.StoredKind;
        issue.Description = RaidFieldLimits.NormalizeNarrative(form.Description) ?? "";
        issue.StatusId = issueStatusId;
        issue.SeverityId = form.SeverityId;
        issue.PriorityId = form.PriorityId;
        issue.OwnerUserId = form.OwnerUserId > 0 ? form.OwnerUserId : null;
        issue.SroUserId = form.SroUserId > 0 ? form.SroUserId : null;
        issue.Severity = TruncateLowerRaid(sevRow?.Label ?? issue.Severity, 10);
        issue.Priority = priRow != null ? TruncateRaid(priRow.Label, 10) : issue.Priority;
        issue.Status = TruncateLowerRaid(stRow?.Label ?? issue.Status, 20);
        issue.TargetResolutionDate = targetResolutionDt;
        issue.Workaround = RaidFieldLimits.NormalizeNarrative(form.Workaround);
        issue.DetailedCause = RaidFieldLimits.NormalizeNarrative(form.DetailedCause);
        issue.AssuranceArrangements = RaidFieldLimits.NormalizeNarrative(form.AssuranceArrangements);
        issue.UpdatedAt = DateTime.UtcNow;

        await PersistIssueCategoryLinksAsync(issue, form.IssueCategoryIds, cancellationToken);
        await PersistIssueDivisionLinksAsync(issue, form.DivisionIds, cancellationToken);
        await PersistIssueBusinessAreaLinksAsync(issue, form.BusinessAreaLookupIds, cancellationToken);
        if (!await PersistIssueAssuranceEventsAsync(issue.Id, form.AssuranceItems, cancellationToken))
            return View("~/Views/Modern/Raid/IssueEditor.cshtml", form);
        await _db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = "Issue updated.";
        return RedirectToAction(nameof(IssueDetail), new { id });
    }

    [HttpGet("dependencies/create")]
    public async Task<IActionResult> DependencyCreate(CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-dependencies");
        await PrepareDependencyEditorLookupsAsync(cancellationToken);
        ViewBag.EditorTitle = "Add dependency";
        return View("~/Views/Modern/Raid/DependencyEditor.cshtml", new ModernRaidDependencyEditorForm { Status = "Active" });
    }

    [HttpPost("dependencies/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DependencyCreatePost([FromForm] ModernRaidDependencyEditorForm form, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-dependencies");
        await PrepareDependencyEditorLookupsAsync(cancellationToken);
        ViewBag.EditorTitle = "Add dependency";

        if (!RaidDateFormHelper.TryOptionalDate(form.DueDay, form.DueMonth, form.DueYear, "Due", ModelState, out var dueDt))
            return View("~/Views/Modern/Raid/DependencyEditor.cshtml", form);

        if (!ModelState.IsValid)
            return View("~/Views/Modern/Raid/DependencyEditor.cshtml", form);

        var now = DateTime.UtcNow;
        var dep = new Dependency
        {
            SourceEntityType = form.SourceEntityType.Trim(),
            SourceEntityId = form.SourceEntityId,
            TargetEntityType = form.TargetEntityType.Trim(),
            TargetEntityId = form.TargetEntityId,
            DependencyLinkTypeId = form.DependencyLinkTypeId,
            DependencyCriticalityId = form.DependencyCriticalityId,
            DependencyType = form.DependencyType,
            Description = form.Description,
            Status = string.IsNullOrWhiteSpace(form.Status) ? "Active" : form.Status.Trim(),
            DueDate = dueDt,
            Organisation = form.Organisation,
            OwnerUserId = form.OwnerUserId > 0 ? form.OwnerUserId : null,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Dependencies.Add(dep);
        await _db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = "Dependency created.";
        return RedirectToAction(nameof(DependencyDetail), new { id = dep.Id });
    }

    [HttpGet("dependencies/{id:int}")]
    public async Task<IActionResult> DependencyDetail(int id, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-dependencies");
        var dep = await _db.Dependencies.AsNoTracking()
            .Include(d => d.LinkTypeLookup)
            .Include(d => d.CriticalityLookup)
            .Include(d => d.OwnerUser)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (dep == null)
            return NotFound();

        var map = await BuildDependencyEndpointTitleMapAsync(new[] { dep }, cancellationToken);
        string Label(string typ, int iid) =>
            map.TryGetValue((NormalizeRaidEntityType(typ), iid), out var t) ? t : $"{typ.Trim()} #{iid}";
        ViewBag.SourceTitle = Label(dep.SourceEntityType, dep.SourceEntityId);
        ViewBag.TargetTitle = Label(dep.TargetEntityType, dep.TargetEntityId);
        return View("~/Views/Modern/Raid/DependencyDetail.cshtml", dep);
    }

    [HttpGet("dependencies/{id:int}/edit")]
    public async Task<IActionResult> DependencyEdit(int id, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-dependencies");
        await PrepareDependencyEditorLookupsAsync(cancellationToken);
        var dep = await _db.Dependencies.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (dep == null)
            return NotFound();

        RaidDateFormHelper.SplitDateParts(dep.DueDate, out var dueD, out var dueM, out var dueY);

        var form = new ModernRaidDependencyEditorForm
        {
            Id = dep.Id,
            SourceEntityType = dep.SourceEntityType,
            SourceEntityId = dep.SourceEntityId,
            TargetEntityType = dep.TargetEntityType,
            TargetEntityId = dep.TargetEntityId,
            DependencyLinkTypeId = dep.DependencyLinkTypeId,
            DependencyCriticalityId = dep.DependencyCriticalityId,
            DependencyType = dep.DependencyType,
            Description = dep.Description,
            Status = dep.Status,
            DueDay = dueD,
            DueMonth = dueM,
            DueYear = dueY,
            Organisation = dep.Organisation,
            OwnerUserId = dep.OwnerUserId
        };
        ViewBag.EditorTitle = "Edit dependency";
        return View("~/Views/Modern/Raid/DependencyEditor.cshtml", form);
    }

    [HttpPost("dependencies/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DependencyEditPost(int id, [FromForm] ModernRaidDependencyEditorForm form, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-dependencies");
        await PrepareDependencyEditorLookupsAsync(cancellationToken);
        ViewBag.EditorTitle = "Edit dependency";
        form.Id = id;

        var dep = await _db.Dependencies.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (dep == null)
            return NotFound();

        if (!RaidDateFormHelper.TryOptionalDate(form.DueDay, form.DueMonth, form.DueYear, "Due", ModelState, out var dueDt))
            return View("~/Views/Modern/Raid/DependencyEditor.cshtml", form);

        if (!ModelState.IsValid)
            return View("~/Views/Modern/Raid/DependencyEditor.cshtml", form);

        dep.SourceEntityType = form.SourceEntityType.Trim();
        dep.SourceEntityId = form.SourceEntityId;
        dep.TargetEntityType = form.TargetEntityType.Trim();
        dep.TargetEntityId = form.TargetEntityId;
        dep.DependencyLinkTypeId = form.DependencyLinkTypeId;
        dep.DependencyCriticalityId = form.DependencyCriticalityId;
        dep.DependencyType = form.DependencyType;
        dep.Description = form.Description;
        dep.Status = string.IsNullOrWhiteSpace(form.Status) ? dep.Status : form.Status.Trim();
        dep.DueDate = dueDt;
        dep.Organisation = form.Organisation;
        dep.OwnerUserId = form.OwnerUserId > 0 ? form.OwnerUserId : null;
        dep.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = "Dependency updated.";
        return RedirectToAction(nameof(DependencyDetail), new { id });
    }

    [HttpGet("assumptions/create")]
    public async Task<IActionResult> AssumptionCreate(int? projectId = null, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-assumptions");
        await PrepareAssumptionCreateLookupsAsync(cancellationToken);
        int? workId = null;
        if (projectId is int pid && pid > 0)
        {
            var exists = await _db.Projects.AsNoTracking()
                .AnyAsync(p => p.Id == pid && !p.IsDeleted, cancellationToken);
            if (exists)
                workId = pid;
        }

        var form = workId is int wid && wid > 0
            ? new ModernRaidCreateAssumptionForm
            {
                ProjectId = wid,
                AssociationKind = "work",
                ReturnToWorkItemId = wid
            }
            : new ModernRaidCreateAssumptionForm();
        return View("~/Views/Modern/Raid/AssumptionCreate.cshtml", form);
    }

    [HttpPost("assumptions/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssumptionCreatePost([FromForm] ModernRaidCreateAssumptionForm form, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-assumptions");
        await PrepareAssumptionCreateLookupsAsync(cancellationToken, form.OwnerUserId, form.SroUserId);

        var desc = (form.Description ?? "").Trim();
        if (string.IsNullOrEmpty(desc))
            ModelState.AddModelError(nameof(form.Description), "Enter the assumption.");
        var assocBind = await TryBindRaidAssociationAsync(
            form.AssociationKind,
            form.ProjectId,
            form.PrimaryProductId,
            nameof(ModernRaidCreateAssumptionForm.ProjectId),
            nameof(ModernRaidCreateAssumptionForm.PrimaryProductId),
            cancellationToken);

        DateTime? reviewDt = null;
        if (RaidDateFormHelper.TryRequiredDate(form.ReviewDay, form.ReviewMonth, form.ReviewYear, "Review", ModelState, out reviewDt)
            && reviewDt.HasValue
            && reviewDt.Value.Date < DateTime.UtcNow.Date)
            ModelState.AddModelError("Review", "Review date cannot be in the past.");

        if (form.AssumptionCriticalityId is null or <= 0)
            ModelState.AddModelError(nameof(form.AssumptionCriticalityId), "Select a criticality.");
        if (form.AssumptionStatusId is null or <= 0)
            ModelState.AddModelError(nameof(form.AssumptionStatusId), "Select a status.");
        if (form.OwnerUserId is null or <= 0)
            ModelState.AddModelError(nameof(form.OwnerUserId), "Select an owner.");
        if (form.SroUserId is null or <= 0)
            ModelState.AddModelError(nameof(form.SroUserId), "Select a senior responsible officer (SRO).");

        if (assocBind is not { } a || !ModelState.IsValid)
            return View("~/Views/Modern/Raid/AssumptionCreate.cshtml", form);

        form.DivisionIds ??= new List<int>();
        form.BusinessAreaLookupIds ??= new List<int>();

        var now = DateTime.UtcNow;
        var entity = new Assumption
        {
            ProjectId = a.ProjectId,
            PrimaryProductId = a.PrimaryProductId,
            RaidAssociationKind = a.StoredKind,
            Description = desc,
            AssumptionCriticalityId = form.AssumptionCriticalityId,
            AssumptionStatusId = form.AssumptionStatusId,
            ReviewDate = reviewDt,
            OwnerUserId = form.OwnerUserId > 0 ? form.OwnerUserId : null,
            SroUserId = form.SroUserId > 0 ? form.SroUserId : null,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Assumptions.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        await PersistAssumptionDivisionLinksAsync(entity, form.DivisionIds, cancellationToken);
        await PersistAssumptionBusinessAreaLinksAsync(entity, form.BusinessAreaLookupIds, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = "Assumption added to the register.";
        if (form.ReturnToWorkItemId is int rwid && rwid > 0)
        {
            var back = await _db.Projects.AsNoTracking()
                .AnyAsync(p => p.Id == rwid && !p.IsDeleted, cancellationToken);
            if (back)
            {
                TempData["SuccessMessage"] = "Assumption added to the register.";
                var url = Url.Action("Detail", "ModernWork", new { id = rwid, tab = "assumptions" });
                if (!string.IsNullOrEmpty(url))
                    return Redirect(url + "#wd-assumptions");
            }
        }

        return RedirectToAction(nameof(AssumptionDetail), new { id = entity.Id });
    }

    [HttpGet("assumptions/{id:int}")]
    public async Task<IActionResult> AssumptionDetail(int id, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-assumptions");
        var a = await _db.Assumptions.AsNoTracking()
            .Include(x => x.Project)!.ThenInclude(p => p!.BusinessAreaLookup)
            .Include(x => x.Project)!.ThenInclude(p => p!.PrimaryOrganizationalGroup)
            .Include(x => x.PrimaryProduct)
            .Include(x => x.CriticalityLookup)
            .Include(x => x.StatusLookup)
            .Include(x => x.OwnerUser)
            .Include(x => x.SroUser)
            .Include(x => x.AssumptionDivisions).ThenInclude(x => x.Division)
            .Include(x => x.AssumptionBusinessAreas).ThenInclude(x => x.BusinessAreaLookup)
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, cancellationToken);
        if (a == null)
            return NotFound();
        return View("~/Views/Modern/Raid/AssumptionDetail.cshtml", a);
    }

    [HttpGet("assumptions/{id:int}/edit")]
    public async Task<IActionResult> AssumptionEdit(int id, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-assumptions");
        var a = await _db.Assumptions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, cancellationToken);
        if (a == null)
            return NotFound();

        await PrepareAssumptionCreateLookupsAsync(cancellationToken, a.OwnerUserId, a.SroUserId);

        RaidDateFormHelper.SplitDateParts(a.ReviewDate, out var rd, out var rm, out var ry);

        var asmDivIds = await _db.AssumptionDivisions.AsNoTracking()
            .Where(x => x.AssumptionId == a.Id)
            .Select(x => x.DivisionId)
            .ToListAsync(cancellationToken);
        var asmBaIds = await _db.AssumptionBusinessAreas.AsNoTracking()
            .Where(x => x.AssumptionId == a.Id)
            .Select(x => x.BusinessAreaLookupId)
            .ToListAsync(cancellationToken);

        var form = new ModernRaidCreateAssumptionForm
        {
            Id = a.Id,
            AssociationKind = ToRaidAssociationUiKind(a.RaidAssociationKind, a.ProjectId, a.PrimaryProductId),
            ProjectId = a.ProjectId,
            PrimaryProductId = a.PrimaryProductId,
            Description = a.Description,
            AssumptionCriticalityId = a.AssumptionCriticalityId,
            AssumptionStatusId = a.AssumptionStatusId,
            ReviewDay = rd,
            ReviewMonth = rm,
            ReviewYear = ry,
            OwnerUserId = a.OwnerUserId,
            SroUserId = a.SroUserId,
            DivisionIds = asmDivIds,
            BusinessAreaLookupIds = asmBaIds
        };
        return View("~/Views/Modern/Raid/AssumptionEdit.cshtml", form);
    }

    [HttpPost("assumptions/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssumptionEditPost(int id, [FromForm] ModernRaidCreateAssumptionForm form, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-assumptions");
        await PrepareAssumptionCreateLookupsAsync(cancellationToken, form.OwnerUserId, form.SroUserId);

        var a = await _db.Assumptions.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, cancellationToken);
        if (a == null)
            return NotFound();

        var desc = (form.Description ?? "").Trim();
        if (string.IsNullOrEmpty(desc))
            ModelState.AddModelError(nameof(form.Description), "Enter the assumption.");
        var assocBind = await TryBindRaidAssociationAsync(
            form.AssociationKind,
            form.ProjectId,
            form.PrimaryProductId,
            nameof(ModernRaidCreateAssumptionForm.ProjectId),
            nameof(ModernRaidCreateAssumptionForm.PrimaryProductId),
            cancellationToken);

        DateTime? reviewDt = null;
        if (RaidDateFormHelper.TryRequiredDate(form.ReviewDay, form.ReviewMonth, form.ReviewYear, "Review", ModelState, out reviewDt)
            && reviewDt.HasValue
            && reviewDt.Value.Date < DateTime.UtcNow.Date)
            ModelState.AddModelError("Review", "Review date cannot be in the past.");

        if (form.AssumptionCriticalityId is null or <= 0)
            ModelState.AddModelError(nameof(form.AssumptionCriticalityId), "Select a criticality.");
        if (form.AssumptionStatusId is null or <= 0)
            ModelState.AddModelError(nameof(form.AssumptionStatusId), "Select a status.");
        if (form.OwnerUserId is null or <= 0)
            ModelState.AddModelError(nameof(form.OwnerUserId), "Select an owner.");
        if (form.SroUserId is null or <= 0)
            ModelState.AddModelError(nameof(form.SroUserId), "Select a senior responsible officer (SRO).");

        if (assocBind is not { } assoc || !ModelState.IsValid)
            return View("~/Views/Modern/Raid/AssumptionEdit.cshtml", form);

        form.DivisionIds ??= new List<int>();
        form.BusinessAreaLookupIds ??= new List<int>();

        a.ProjectId = assoc.ProjectId;
        a.PrimaryProductId = assoc.PrimaryProductId;
        a.RaidAssociationKind = assoc.StoredKind;
        a.Description = desc;
        a.AssumptionCriticalityId = form.AssumptionCriticalityId;
        a.AssumptionStatusId = form.AssumptionStatusId;
        a.ReviewDate = reviewDt;
        a.OwnerUserId = form.OwnerUserId > 0 ? form.OwnerUserId : null;
        a.SroUserId = form.SroUserId > 0 ? form.SroUserId : null;
        a.UpdatedAt = DateTime.UtcNow;

        await PersistAssumptionDivisionLinksAsync(a, form.DivisionIds, cancellationToken);
        await PersistAssumptionBusinessAreaLinksAsync(a, form.BusinessAreaLookupIds, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = "Assumption updated.";
        return RedirectToAction(nameof(AssumptionDetail), new { id });
    }

    [HttpGet("tier/{id:int}/edit")]
    public async Task<IActionResult> TierEdit(int id, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-tier");
        var tier = await _db.RiskTiers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tier == null)
            return NotFound();

        var form = new ModernRaidRiskTierEditorForm
        {
            Code = tier.Code,
            Name = tier.Name,
            Description = tier.Description,
            Summary = tier.Summary,
            SortOrder = tier.SortOrder,
            IsActive = tier.IsActive
        };
        ViewBag.TierId = id;
        return View("~/Views/Modern/Raid/TierEdit.cshtml", form);
    }

    [HttpPost("tier/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TierEditPost(int id, [FromForm] ModernRaidRiskTierEditorForm form, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-tier");
        var tier = await _db.RiskTiers.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tier == null)
            return NotFound();

        if (!ModelState.IsValid)
        {
            ViewBag.TierId = id;
            return View("~/Views/Modern/Raid/TierEdit.cshtml", form);
        }

        tier.Code = form.Code.Trim();
        tier.Name = form.Name.Trim();
        tier.Description = form.Description;
        tier.Summary = form.Summary;
        tier.SortOrder = form.SortOrder;
        tier.IsActive = form.IsActive;
        tier.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = "Risk tier updated.";
        return RedirectToAction(nameof(Tier));
    }

    [HttpGet("directorate/{id:int}")]
    public async Task<IActionResult> DirectorateDetail(int id, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-directorate");
        var d = await _db.Divisions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (d == null || !d.IsActive)
            return NotFound();

        var scope = await BuildDirectorateDashboardScopeAsync(d, cancellationToken);
        var vm = await BuildScopedRaidDashboardViewModelAsync(scope, d.Name ?? "Directorate", cancellationToken);
        return View("~/Views/Modern/Raid/Dashboard.cshtml", vm);
    }

    [HttpGet("divisions/{id:int}")]
    public IActionResult DirectorateDetailLegacyRedirect(int id)
    {
        return RedirectPermanent(Url.Action(nameof(DirectorateDetail), "ModernRaid", new { id }) ??
                                 $"/modern/raid/directorate/{id}");
    }

    private async Task<ModernRaidBusinessAreaDetailViewModel> BuildModernRaidBusinessAreaDetailViewModelAsync(
        BusinessAreaLookup ba,
        string? section,
        CancellationToken cancellationToken)
    {
        var id = ba.Id;
        var directorateNames = await _db.DivisionBusinessAreas.AsNoTracking()
            .Where(x => x.BusinessAreaLookupId == id)
            .Select(x => x.Division.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(cancellationToken);

        var leadershipNames = await _db.BusinessAreaLeadershipMembers.AsNoTracking()
            .Where(m => m.BusinessAreaLookupId == id)
            .Select(m => m.User.Name ?? m.User.Email ?? "")
            .Where(s => s != "")
            .Distinct()
            .ToListAsync(cancellationToken);

        var projectIds = await _db.Projects.AsNoTracking()
            .Where(p => !p.IsDeleted && p.BusinessAreaId == id)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        var activeSection = (section ?? "").Trim().ToLowerInvariant() switch
        {
            "issues" => "issues",
            "dependencies" => "dependencies",
            "assumptions" => "assumptions",
            _ => "risks"
        };

        var riskList = await _db.Risks.AsNoTracking()
            .Where(r => !r.IsDeleted &&
                ((r.ProjectId != null && projectIds.Contains(r.ProjectId.Value)) ||
                 r.RiskBusinessAreas.Any(rba => rba.BusinessAreaLookupId == id)))
            .AsSplitQuery()
            .Include(r => r.RiskTier)
            .Include(r => r.RiskStatus)
            .Include(r => r.Likelihood)
            .Include(r => r.ImpactLevel)
            .Include(r => r.OwnerUser)
            .Include(r => r.Project).ThenInclude(p => p!.BusinessAreaLookup)
            .Include(r => r.PrimaryProduct)
            .Include(r => r.RiskBusinessAreas).ThenInclude(x => x.BusinessAreaLookup)
            .OrderByDescending(r => r.UpdatedAt)
            .ToListAsync(cancellationToken);

        var openRisks = new List<ModernRaidRiskRow>();
        var closedRisks = new List<ModernRaidRiskRow>();
        foreach (var r in riskList)
        {
            var row = new ModernRaidRiskRow(
                r.Id,
                r.Title ?? "—",
                RaidRegisterTableFormatting.FormatRiskBusinessAreaLabels(r),
                RaidRegisterTableFormatting.BuildRiskRelation(r),
                r.RiskStatus?.Label ?? r.Status,
                r.OwnerUser != null ? (r.OwnerUser.Name ?? r.OwnerUser.Email) : r.OwnerEmail,
                r.Likelihood?.Label ?? r.LikelihoodRating.ToString(),
                r.ImpactLevel?.Label ?? r.ImpactRating.ToString(),
                r.RiskScore,
                r.RiskTier?.Name,
                Snippet(r.Description, 8000));
            if (RaidRiskClosure.IsClosed(r))
                closedRisks.Add(row);
            else
                openRisks.Add(row);
        }

        var riskIds = riskList.Select(r => r.Id).ToList();

        var issueList = await _db.Issues.AsNoTracking()
            .Where(i => !i.IsDeleted &&
                ((i.ProjectId != null && projectIds.Contains(i.ProjectId.Value)) ||
                 i.IssueBusinessAreas.Any(iba => iba.BusinessAreaLookupId == id)))
            .AsSplitQuery()
            .Include(i => i.StatusLookup)
            .Include(i => i.PriorityLookup)
            .Include(i => i.SeverityLookup)
            .Include(i => i.OwnerUser)
            .Include(i => i.Project).ThenInclude(p => p!.BusinessAreaLookup)
            .Include(i => i.PrimaryProduct)
            .Include(i => i.IssueBusinessAreas).ThenInclude(x => x.BusinessAreaLookup)
            .OrderByDescending(i => i.UpdatedAt)
            .ToListAsync(cancellationToken);

        var openIssues = new List<ModernRaidIssueRow>();
        var closedIssues = new List<ModernRaidIssueRow>();
        foreach (var i in issueList)
        {
            var row = new ModernRaidIssueRow(
                i.Id,
                i.Title ?? "—",
                RaidRegisterTableFormatting.FormatIssueBusinessAreaLabels(i),
                RaidRegisterTableFormatting.BuildIssueRelation(i),
                i.StatusLookup?.Label ?? i.Status,
                i.OwnerUser != null ? (i.OwnerUser.Name ?? i.OwnerUser.Email) : null,
                i.PriorityLookup?.Label ?? i.Priority,
                i.SeverityLookup?.Label ?? i.Severity,
                Snippet(i.Description, 8000));
            if (i.ClosedDate == null)
                openIssues.Add(row);
            else
                closedIssues.Add(row);
        }

        var issueIds = issueList.Select(i => i.Id).ToList();

        var depEntities = await _db.Dependencies.AsNoTracking()
            .Include(d => d.LinkTypeLookup)
            .Include(d => d.CriticalityLookup)
            .Where(d =>
                (d.SourceEntityType == "Project" && projectIds.Contains(d.SourceEntityId)) ||
                (d.TargetEntityType == "Project" && projectIds.Contains(d.TargetEntityId)) ||
                (d.SourceEntityType == "Risk" && riskIds.Contains(d.SourceEntityId)) ||
                (d.TargetEntityType == "Risk" && riskIds.Contains(d.TargetEntityId)) ||
                (d.SourceEntityType == "Issue" && issueIds.Contains(d.SourceEntityId)) ||
                (d.TargetEntityType == "Issue" && issueIds.Contains(d.TargetEntityId)))
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync(cancellationToken);

        var titleMap = await BuildDependencyEndpointTitleMapAsync(depEntities, cancellationToken);
        string DepLabel(string entityType, int entityId) =>
            titleMap.TryGetValue((NormalizeRaidEntityType(entityType), entityId), out var tl)
                ? tl
                : $"{entityType.Trim()} #{entityId}";

        var openDeps = new List<ModernRaidDependencyRow>();
        var closedDeps = new List<ModernRaidDependencyRow>();
        foreach (var d in depEntities)
        {
            var row = new ModernRaidDependencyRow(
                d.Id,
                d.SourceEntityType,
                d.SourceEntityId,
                DepLabel(d.SourceEntityType, d.SourceEntityId),
                RaidDependencyEntityDetailUrl(d.SourceEntityType, d.SourceEntityId),
                d.TargetEntityType,
                d.TargetEntityId,
                DepLabel(d.TargetEntityType, d.TargetEntityId),
                RaidDependencyEntityDetailUrl(d.TargetEntityType, d.TargetEntityId),
                d.DependencyType,
                d.LinkTypeLookup?.Label,
                d.CriticalityLookup?.Label,
                d.Organisation,
                d.DueDate,
                d.Description,
                d.Status,
                d.UpdatedAt);
            var depClosed = !string.IsNullOrEmpty(d.Status)
                            && d.Status.Contains("closed", StringComparison.OrdinalIgnoreCase);
            if (depClosed)
                closedDeps.Add(row);
            else
                openDeps.Add(row);
        }

        var closedStatusIds = await _db.AssumptionStatuses.AsNoTracking()
            .Where(x => x.IsActive && EF.Functions.Like(x.Label, "%closed%"))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var asmList = await _db.Assumptions.AsNoTracking()
            .Where(a => !a.IsDeleted && a.ProjectId != null && projectIds.Contains(a.ProjectId.Value))
            .Include(a => a.Project)
            .Include(a => a.PrimaryProduct)
            .Include(a => a.CriticalityLookup)
            .Include(a => a.StatusLookup)
            .OrderByDescending(a => a.UpdatedAt)
            .ToListAsync(cancellationToken);

        var openAsm = new List<ModernRaidAssumptionRow>();
        var closedAsm = new List<ModernRaidAssumptionRow>();
        foreach (var a in asmList)
        {
            var row = new ModernRaidAssumptionRow(
                a.Id,
                ToRaidAssociationUiKind(a.RaidAssociationKind, a.ProjectId, a.PrimaryProductId),
                a.ProjectId,
                a.Project?.Title,
                a.PrimaryProduct != null ? (a.PrimaryProduct.DisplayName ?? a.PrimaryProduct.FipsId) : null,
                Snippet(a.Description, 160),
                a.CriticalityLookup?.Label,
                a.StatusLookup?.Label,
                a.ReviewDate,
                a.UpdatedAt);
            var isClosed = a.AssumptionStatusId.HasValue
                           && closedStatusIds.Count > 0
                           && closedStatusIds.Contains(a.AssumptionStatusId.Value);
            if (isClosed)
                closedAsm.Add(row);
            else
                openAsm.Add(row);
        }

        var openRiskSummary = await RaidRiskClosure.WhereOpen(
                _db.Risks.AsNoTracking()
                    .Where(r => !r.IsDeleted &&
                        ((r.ProjectId != null && projectIds.Contains(r.ProjectId.Value)) ||
                         r.RiskBusinessAreas.Any(rba => rba.BusinessAreaLookupId == id))))
            .CountAsync(cancellationToken);

        return new ModernRaidBusinessAreaDetailViewModel
        {
            BusinessArea = ba,
            WorkItemCount = projectIds.Count,
            TotalRiskCount = riskList.Count,
            OpenRiskCountSummary = openRiskSummary,
            OpenIssueCount = openIssues.Count,
            LinkedDirectoratesSummary = directorateNames.Count == 0
                ? null
                : string.Join(", ", directorateNames),
            LeadershipScopeSummary = leadershipNames.Count == 0
                ? null
                : string.Join(", ", leadershipNames),
            ActiveSection = activeSection,
            OpenRisks = openRisks,
            ClosedRisks = closedRisks,
            OpenIssues = openIssues,
            ClosedIssues = closedIssues,
            OpenDependencies = openDeps,
            ClosedDependencies = closedDeps,
            OpenAssumptions = openAsm,
            ClosedAssumptions = closedAsm,
            TotalIssuesCount = issueList.Count,
            TotalDependenciesCount = depEntities.Count,
            TotalAssumptionsCount = asmList.Count
        };
    }

    [HttpGet("business-areas/{id:int}")]
    public async Task<IActionResult> BusinessAreaDetail(int id, string? section, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-business-areas");
        var ba = await _db.BusinessAreaLookups.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (ba == null)
            return NotFound();

        var scope = await BuildBusinessAreaDashboardScopeAsync(ba, cancellationToken);
        var vm = await BuildScopedRaidDashboardViewModelAsync(scope, ba.Name ?? "Business area", cancellationToken);
        return View("~/Views/Modern/Raid/Dashboard.cshtml", vm);
    }

    /// <summary>Legacy route — portfolios list moved to <see cref="ModernRaidController.BusinessAreas"/>.</summary>
    [HttpGet("portfolios/{id:int}")]
    public IActionResult PortfolioLegacyDetail(int _) =>
        RedirectToActionPermanent(nameof(BusinessAreas));

    // ── Risk mitigation actions (detail tab) ─────────────────────────────────

    private static UserPickerViewModel MitigationAddOwnerPicker() =>
        new UserPickerViewModel
        {
            FieldName = "AssignedToUserId",
            Label = "Owner",
            Hint = "Search by name or email, then select a person.",
            InputIdSuffix = "mit-add",
            UseGovUkStyling = true
        };

    private static UserPickerViewModel BuildMitigationOwnerPicker(Models.Action action, string inputIdSuffix)
    {
        var u = action.AssignedToUser;
        var dispName = u != null
            ? (string.IsNullOrWhiteSpace(u.Name) ? u.Email : u.Name)
            : (string.IsNullOrWhiteSpace(action.AssignedToEmail) ? null : action.AssignedToEmail);
        var dispEmail = u?.Email ?? action.AssignedToEmail;
        return new UserPickerViewModel
        {
            FieldName = "AssignedToUserId",
            Label = "Owner",
            Hint = "Search by name or email, then select a person.",
            DefaultUserId = action.AssignedToUserId,
            DefaultName = dispName,
            DefaultEmail = dispEmail,
            InputIdSuffix = inputIdSuffix,
            UseGovUkStyling = true
        };
    }

    private static string MitigationNormalizeStoredStatic(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return MitigationStatuses.NotStarted;
        var t = raw.Trim();
        var l = t.ToLowerInvariant();
        if (l is "not started" or "not_started" or "open") return MitigationStatuses.NotStarted;
        if (l is "in progress" or "in_progress" or "progress") return MitigationStatuses.InProgress;
        if (l is "complete" or "completed" or "done") return MitigationStatuses.Complete;
        if (l == "overdue") return MitigationStatuses.Overdue;
        if (l.Contains("progress")) return MitigationStatuses.InProgress;
        if (l.Contains("complete")) return MitigationStatuses.Complete;
        return t.Length > 0 ? t : MitigationStatuses.NotStarted;
    }

    private static string MitigationEffectiveStatusForUi(Models.Action a)
    {
        var s = MitigationNormalizeStoredStatic(a.Status);
        if (s == MitigationStatuses.Complete) return MitigationStatuses.Complete;
        if (s == MitigationStatuses.Overdue) return MitigationStatuses.Overdue;
        if (a.DueDate is DateTime d && d.Date < DateTime.UtcNow.Date && s != MitigationStatuses.Complete)
            return MitigationStatuses.Overdue;
        return s is MitigationStatuses.InProgress or MitigationStatuses.NotStarted ? s : MitigationStatuses.NotStarted;
    }

    private void PopulateMitigationEditViewBag(
        int riskId,
        string riskTitle,
        Models.Action mitigation,
        string? statusSelectOverride = null,
        int? targetDateDay = null,
        int? targetDateMonth = null,
        int? targetDateYear = null,
        string? firstTargetDateError = null)
    {
        ViewBag.RiskId = riskId;
        ViewBag.RiskTitle = riskTitle;
        ViewBag.BackUrl = Url.Action(nameof(RiskDetail), new { id = riskId, tab = "mitigations" }) ?? "#";
        ViewBag.EffectiveStatus = string.IsNullOrEmpty(statusSelectOverride)
            ? MitigationEffectiveStatusForUi(mitigation)
            : statusSelectOverride;
        var picker = BuildMitigationOwnerPicker(mitigation, "mit-edit-" + mitigation.Id);
        picker.UseGovUkStyling = true;
        ViewBag.MitigationOwnerUserPicker = picker;
        int? d;
        int? m;
        int? y;
        if (targetDateDay.HasValue || targetDateMonth.HasValue || targetDateYear.HasValue)
        {
            d = targetDateDay;
            m = targetDateMonth;
            y = targetDateYear;
        }
        else
        {
            RaidDateFormHelper.SplitDateParts(mitigation.DueDate, out d, out m, out y);
        }

        const string targetDateStateKey = "targetDate";
        var hasTdErr = (ModelState[targetDateStateKey]?.Errors.Count ?? 0) > 0;
        if (string.IsNullOrEmpty(firstTargetDateError) && hasTdErr)
            firstTargetDateError = ModelState[targetDateStateKey]?.Errors.FirstOrDefault()?.ErrorMessage;
        ViewBag.MitigationTargetDate = new RaidGovUkDateFieldVm
        {
            NamePrefix = "TargetDate",
            Legend = "Target date",
            FormGroupId = "targetDate",
            FieldIdPrefix = "raid-mit-edit-targetdate",
            Day = d,
            Month = m,
            Year = y,
            HasError = hasTdErr,
            ErrorMessage = firstTargetDateError
        };
    }

    [HttpGet("risks/{riskId:int}/mitigations/{actionId:int}/edit")]
    public async Task<IActionResult> MitigationEdit(int riskId, int actionId, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-risks");
        var mitigation = await RaidRiskMitigations.FindForRiskAsync(
            _db, riskId, actionId, tracking: false, cancellationToken);
        if (mitigation == null)
            return NotFound();

        var risk = await _db.Risks.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == riskId && !r.IsDeleted, cancellationToken);
        if (risk == null)
            return NotFound();

        ViewData["Title"] = "Edit mitigation action";
        PopulateMitigationEditViewBag(riskId, risk.Title, mitigation);
        return View("~/Views/Modern/Raid/MitigationEdit.cshtml", mitigation);
    }

    [HttpPost("risks/{riskId:int}/mitigations/{actionId:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MitigationEdit(int riskId, int actionId, string? title, int? assignedToUserId,
        int? targetDateDay, int? targetDateMonth, int? targetDateYear, string? status, string? updateNote,
        CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-risks");
        var mitigationAction = await RaidRiskMitigations.FindForRiskAsync(
            _db, riskId, actionId, tracking: true, cancellationToken);
        if (mitigationAction == null)
            return NotFound();

        var risk = await _db.Risks.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == riskId && !r.IsDeleted, cancellationToken);
        if (risk == null)
            return NotFound();

        title = RaidFieldLimits.NormalizeNarrative(title) ?? "";
        var normalizedStatus = NormalizeMitigationInputStatus(status);
        RaidDateFormHelper.TryRequiredDate(targetDateDay, targetDateMonth, targetDateYear, "targetDate", ModelState, out var parsedTargetDate);

        if (string.IsNullOrWhiteSpace(title))
            ModelState.AddModelError("title", "Enter the mitigation action.");
        if (assignedToUserId is null or <= 0)
            ModelState.AddModelError("AssignedToUserId", "Select an owner.");
        if (!MitigationStatuses.Canonical.Contains(normalizedStatus))
            ModelState.AddModelError("status", "Select a valid status.");

        User? ownerUser = null;
        if (assignedToUserId is > 0)
        {
            ownerUser = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == assignedToUserId.Value, cancellationToken);
            if (ownerUser == null)
                ModelState.AddModelError("AssignedToUserId", "Select a valid owner.");
        }

        if (!ModelState.IsValid)
        {
            mitigationAction.Title = title;
            if (parsedTargetDate.HasValue)
                mitigationAction.DueDate = parsedTargetDate;
            if (assignedToUserId is > 0 && ownerUser != null)
            {
                mitigationAction.AssignedToUserId = assignedToUserId;
                mitigationAction.AssignedToEmail = ownerUser.Email;
                mitigationAction.AssignedToUser = ownerUser;
            }

            ViewData["Title"] = "Edit mitigation action";
            PopulateMitigationEditViewBag(
                riskId,
                risk.Title,
                mitigationAction,
                MitigationStatuses.Canonical.Contains(normalizedStatus) ? normalizedStatus : null,
                targetDateDay,
                targetDateMonth,
                targetDateYear);
            return View("~/Views/Modern/Raid/MitigationEdit.cshtml", mitigationAction);
        }

        mitigationAction.Title = title;
        mitigationAction.AssignedToUserId = assignedToUserId;
        mitigationAction.AssignedToEmail = ownerUser!.Email;
        mitigationAction.DueDate = parsedTargetDate!.Value.Date;
        mitigationAction.Status = normalizedStatus;
        mitigationAction.UpdatedAt = DateTime.UtcNow;
        if (normalizedStatus == MitigationStatuses.Complete && mitigationAction.CompletedDate == null)
            mitigationAction.CompletedDate = DateTime.UtcNow.Date;
        if (normalizedStatus != MitigationStatuses.Complete)
            mitigationAction.CompletedDate = null;

        var note = RaidFieldLimits.NormalizeNarrative(updateNote);
        if (!string.IsNullOrEmpty(note))
        {
            mitigationAction.Notes = AppendMitigationAuditLine(mitigationAction.Notes, note);
        }

        await RaidRiskMitigations.EnsureJunctionAsync(_db, riskId, actionId, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = "Mitigation saved.";
        return RedirectToAction(nameof(RiskDetail), new { id = riskId, tab = "mitigations" });
    }

    [HttpPost("risks/{riskId:int}/mitigations/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RiskMitigationAdd(int riskId, string? title, int? assignedToUserId, DateTime? targetDate,
        CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-risks");
        var risk = await _db.Risks.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == riskId && !r.IsDeleted, cancellationToken);
        if (risk == null)
            return NotFound();

        title = RaidFieldLimits.NormalizeNarrative(title) ?? "";
        if (string.IsNullOrWhiteSpace(title) || targetDate == null || assignedToUserId is null or <= 0)
        {
            TempData["Message"] = "Enter the mitigation action, select an owner, and target date.";
            return RedirectToAction(nameof(RiskDetail), new { id = riskId, tab = "mitigations" });
        }

        var ownerUser = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == assignedToUserId.Value, cancellationToken);
        if (ownerUser == null)
        {
            TempData["Message"] = "Select a valid owner.";
            return RedirectToAction(nameof(RiskDetail), new { id = riskId, tab = "mitigations" });
        }

        var mitTypeId = await _db.ActionTypes.AsNoTracking()
            .Where(t => t.IsActive && t.Code == "MIT")
            .Select(t => (int?)t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var entity = new Models.Action
        {
            Title = title,
            RiskId = riskId,
            ProjectId = risk.ProjectId,
            PrimaryProductId = risk.PrimaryProductId,
            AssignedToUserId = assignedToUserId,
            AssignedToEmail = ownerUser.Email,
            DueDate = targetDate.Value.Date,
            Status = MitigationStatuses.NotStarted,
            ActionTypeId = mitTypeId,
            SourceType = "risk_mitigation",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Actions.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        _db.RiskActions.Add(new RiskAction { RiskId = riskId, ActionId = entity.Id });
        await _db.SaveChangesAsync(cancellationToken);

        TempData["Message"] = "Mitigation action added.";
        return RedirectToAction(nameof(RiskDetail), new { id = riskId, tab = "mitigations" });
    }

    private static string AppendMitigationAuditLine(string? existing, string appendLine)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC — {appendLine}";
        var combined = string.IsNullOrWhiteSpace(existing) ? line : $"{existing.Trim()}\n{line}";
        if (combined.Length <= RaidFieldLimits.NarrativeMaxLength)
            return combined;
        return combined[^RaidFieldLimits.NarrativeMaxLength..];
    }

    private static string NormalizeMitigationInputStatus(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (string.Equals(s, MitigationStatuses.NotStarted, StringComparison.OrdinalIgnoreCase))
            return MitigationStatuses.NotStarted;
        if (string.Equals(s, MitigationStatuses.InProgress, StringComparison.OrdinalIgnoreCase))
            return MitigationStatuses.InProgress;
        if (string.Equals(s, MitigationStatuses.Complete, StringComparison.OrdinalIgnoreCase))
            return MitigationStatuses.Complete;
        if (string.Equals(s, MitigationStatuses.Overdue, StringComparison.OrdinalIgnoreCase))
            return MitigationStatuses.Overdue;
        return s;
    }

    private static class MitigationStatuses
    {
        public const string NotStarted = "Not started";
        public const string InProgress = "In progress";
        public const string Complete = "Complete";
        public const string Overdue = "Overdue";

        public static readonly string[] Canonical =
        {
            NotStarted, InProgress, Complete, Overdue
        };
    }
}
