using System.Security.Claims;
using Compass.Models;
using Compass.Services.Raid;
using Compass.ViewModels.Modern;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compass.Controllers.Modern;

/// <summary>RAID register list actions with search / filter (partial).</summary>
public partial class ModernRaidController
{
    private const int RaidDefaultPageSize = 25;

    private async Task<List<RaidLookupOptionVm>> RaidProjectFilterOptionsAsync(CancellationToken cancellationToken)
    {
        var raw = await _db.Projects.AsNoTracking()
            .Where(p => !p.IsDeleted)
            .OrderBy(p => p.Title)
            .Select(p => new { p.Id, p.Title })
            .Take(500)
            .ToListAsync(cancellationToken);
        return raw.Select(x => new RaidLookupOptionVm(x.Id, x.Title ?? "")).ToList();
    }

    private string GetCurrentUserEmailNormalized()
    {
        if (_viewAsUser.IsActive(HttpContext))
        {
            var active = _viewAsUser.GetActive(HttpContext);
            if (!string.IsNullOrWhiteSpace(active?.Email))
                return active.Email.Trim().ToLowerInvariant();
        }

        var email = User.Identity?.Name
            ?? User.FindFirst(ClaimTypes.Email)?.Value
            ?? User.FindFirst("preferred_username")?.Value;
        return string.IsNullOrWhiteSpace(email) ? "" : email.Trim().ToLowerInvariant();
    }

    /// <summary>Risks where the viewer is owner, SRO, legacy owner email, or assigned to any mitigation action.</summary>
    private IQueryable<Risk> WhereRiskIsMine(IQueryable<Risk> query, int? userId, string emailLower)
    {
        if (!userId.HasValue && string.IsNullOrEmpty(emailLower))
            return query.Where(_ => false);

        return query.Where(r =>
            (userId.HasValue && r.OwnerUserId == userId) ||
            (userId.HasValue && r.SroUserId == userId) ||
            (r.OwnerUserId == null && r.OwnerEmail != null && r.OwnerEmail.ToLower() == emailLower) ||
            r.RiskActions.Any(ra =>
                !ra.Action.IsDeleted &&
                ((userId.HasValue && ra.Action.AssignedToUserId == userId) ||
                 (ra.Action.AssignedToEmail != null && ra.Action.AssignedToEmail.ToLower() == emailLower))) ||
            _db.Actions.Any(a =>
                !a.IsDeleted
                && a.RiskId == r.Id
                && ((userId.HasValue && a.AssignedToUserId == userId) ||
                    (a.AssignedToEmail != null && a.AssignedToEmail.ToLower() == emailLower))));
    }

    private static IQueryable<Risk> ApplyRiskRegisterSearch(IQueryable<Risk> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return query;

        var t = search.Trim();
        int? numericId = null;
        var refText = t;
        if (refText.StartsWith("R-", StringComparison.OrdinalIgnoreCase))
            refText = refText[2..];
        if (int.TryParse(refText, out var parsed) && parsed > 0)
            numericId = parsed;

        return query.Where(r =>
            r.Title.Contains(t) ||
            (r.Description != null && r.Description.Contains(t)) ||
            (r.Notes != null && r.Notes.Contains(t)) ||
            (r.Cause != null && r.Cause.Contains(t)) ||
            (r.ResponseStrategy != null && r.ResponseStrategy.Contains(t)) ||
            (r.OwnerEmail != null && r.OwnerEmail.Contains(t)) ||
            (r.OwnerUser != null && (
                (r.OwnerUser.Name != null && r.OwnerUser.Name.Contains(t)) ||
                (r.OwnerUser.Email != null && r.OwnerUser.Email.Contains(t)))) ||
            (r.PrimaryProduct != null && (
                (r.PrimaryProduct.DisplayName != null && r.PrimaryProduct.DisplayName.Contains(t)) ||
                r.PrimaryProduct.FipsId.Contains(t))) ||
            (r.Project != null && r.Project.Title.Contains(t)) ||
            (numericId.HasValue && r.Id == numericId.Value));
    }

    private static IReadOnlyList<int> MergeRaidRegisterFilterIds(int? singleId, int[]? arrayIds)
    {
        if (arrayIds is { Length: > 0 })
            return arrayIds.Where(id => id > 0).Distinct().ToList();

        return singleId is > 0 ? new List<int> { singleId.Value } : Array.Empty<int>();
    }

    private IQueryable<Risk> WhereRiskMatchesAnyDivision(IQueryable<Risk> query, IReadOnlyList<int> divisionIds)
    {
        if (divisionIds.Count == 0)
            return query;

        var ids = divisionIds.ToList();
        return query.Where(r =>
            (r.ProjectId != null &&
             _db.ProjectDirectorates.Any(pd => pd.ProjectId == r.ProjectId && ids.Contains(pd.DivisionId))) ||
            r.RiskDivisions.Any(rd => ids.Contains(rd.DivisionId)));
    }

    private IQueryable<Risk> WhereRiskMatchesAnyBusinessArea(IQueryable<Risk> query, IReadOnlyList<int> businessAreaIds)
    {
        if (businessAreaIds.Count == 0)
            return query;

        var ids = businessAreaIds.ToList();
        return query.Where(r =>
            (r.ProjectId != null &&
             _db.Projects.Any(p => p.Id == r.ProjectId && p.BusinessAreaId != null && ids.Contains(p.BusinessAreaId.Value))) ||
            r.RiskBusinessAreas.Any(rba => ids.Contains(rba.BusinessAreaLookupId)));
    }

    /// <summary>
    /// Issues query using the same portfolio filters as the risk register (work item, division, business area, search).
    /// Risk-only filters (status, tier) do not apply.
    /// </summary>
    private IQueryable<Issue> IssueQueryableAlignedToRiskRegisterFilters(
        int? projectId,
        IReadOnlyList<int> divisionIds,
        IReadOnlyList<int> businessAreaIds,
        string? search)
    {
        IQueryable<Issue> iq = _db.Issues.AsNoTracking().Where(i => !i.IsDeleted);

        if (projectId is > 0)
            iq = iq.Where(i => i.ProjectId == projectId);

        if (divisionIds.Count > 0)
        {
            var ids = divisionIds.ToList();
            iq = iq.Where(i =>
                (i.ProjectId != null &&
                 _db.ProjectDirectorates.Any(pd => pd.ProjectId == i.ProjectId && ids.Contains(pd.DivisionId))) ||
                i.IssueDivisions.Any(idv => ids.Contains(idv.DivisionId)));
        }

        if (businessAreaIds.Count > 0)
        {
            var ids = businessAreaIds.ToList();
            iq = iq.Where(i =>
                (i.ProjectId != null &&
                 _db.Projects.Any(p => p.Id == i.ProjectId && p.BusinessAreaId != null && ids.Contains(p.BusinessAreaId.Value))) ||
                i.IssueBusinessAreas.Any(iba => ids.Contains(iba.BusinessAreaLookupId)));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim();
            iq = iq.Where(i =>
                i.Title.Contains(t) ||
                (i.Description != null && i.Description.Contains(t)));
        }

        return iq;
    }

    /// <summary>Issues where the viewer is owner, SRO, or assigned to any linked action.</summary>
    private static IQueryable<Issue> WhereIssueIsMine(IQueryable<Issue> query, int? userId, string emailLower)
    {
        if (!userId.HasValue && string.IsNullOrEmpty(emailLower))
            return query.Where(_ => false);

        return query.Where(i =>
            (userId.HasValue && i.OwnerUserId == userId) ||
            (userId.HasValue && i.SroUserId == userId) ||
            i.IssueActions.Any(ia =>
                !ia.Action.IsDeleted &&
                ((userId.HasValue && ia.Action.AssignedToUserId == userId) ||
                 (ia.Action.AssignedToEmail != null && ia.Action.AssignedToEmail.ToLower() == emailLower))));
    }

    private async Task<int?> GetSavedRaidRegisterBusinessAreaIdAsync(int userId, CancellationToken cancellationToken)
    {
        var pref = await _db.UserPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        var id = pref?.RaidRegisterBusinessAreaLookupId;
        if (id is null or <= 0)
            return null;
        var ok = await _db.BusinessAreaLookups.AsNoTracking()
            .AnyAsync(b => b.Id == id && b.IsActive, cancellationToken);
        return ok ? id : null;
    }

    /// <summary>
    /// Resolves business area scope: query wins; otherwise optional saved default for signed-in users.
    /// </summary>
    private async Task<(int? EffectiveBusinessAreaId, bool ExplicitNone, bool FromSavedPreference)> ResolveRaidRegisterBusinessAreaAsync(
        int? businessAreaIdQuery,
        CancellationToken cancellationToken)
    {
        if (Request.Query.ContainsKey("businessAreaId"))
        {
            if (businessAreaIdQuery is > 0)
                return (businessAreaIdQuery, false, false);
            return (null, true, false);
        }

        var userId = await ResolveCurrentUserIdAsync(cancellationToken);
        if (userId is null)
            return (null, false, false);

        var saved = await GetSavedRaidRegisterBusinessAreaIdAsync(userId.Value, cancellationToken);
        return saved is > 0 ? (saved, false, true) : (null, false, false);
    }

    /// <summary>Shared risks register query for Risks / Tier / Divisions / Business areas views.</summary>
    private async Task<ModernRaidRisksPageViewModel> BuildRiskRegisterAsync(
        string? search,
        int? projectId,
        int? riskStatusId,
        int? riskTierId,
        string? tab,
        bool openOnly,
        int? divisionId,
        IReadOnlyList<int> divisionFilterIds,
        IReadOnlyList<int> businessAreaFilterIds,
        int? effectiveBusinessAreaId,
        bool raidBusinessAreaExplicitNone,
        bool raidBusinessAreaFromSavedPreference,
        bool showRaidRegisterBusinessAreaBar,
        bool canSaveRaidBusinessAreaPreference,
        int? savedRaidBusinessAreaLookupIdForHint,
        int page,
        bool groupRowsByRiskTier,
        bool loadDivisionOptions,
        bool loadBusinessAreaOptions,
        CancellationToken cancellationToken)
    {
        page = page < 1 ? 1 : page;

        var currentUserId = await ResolveCurrentUserIdAsync(cancellationToken);
        var emailLower = GetCurrentUserEmailNormalized();

        IQueryable<Risk> qBase = _db.Risks.AsNoTracking().Where(r => !r.IsDeleted);
        qBase = qBase
            .AsSplitQuery()
            .Include(r => r.RiskTier)
            .Include(r => r.RiskStatus)
            .Include(r => r.RiskPriority)
            .Include(r => r.OwnerUser)
            .Include(r => r.Likelihood)
            .Include(r => r.ImpactLevel)
            .Include(r => r.Project).ThenInclude(p => p!.BusinessAreaLookup)
            .Include(r => r.PrimaryProduct)
            .Include(r => r.RiskBusinessAreas).ThenInclude(x => x.BusinessAreaLookup)
            .Include(r => r.RiskDivisions);

        var activeTab = (tab ?? "").Trim().ToLowerInvariant() switch
        {
            "my" => "my",
            "open" => "open",
            "closed" => "closed",
            "all" => "all",
            _ => openOnly ? "my" : "all"
        };

        if (projectId is > 0)
            qBase = qBase.Where(r => r.ProjectId == projectId);

        if (riskStatusId is > 0)
            qBase = qBase.Where(r => r.RiskStatusId == riskStatusId);

        if (riskTierId is > 0)
            qBase = qBase.Where(r => r.RiskTierId == riskTierId);

        if (divisionFilterIds.Count > 0)
            qBase = WhereRiskMatchesAnyDivision(qBase, divisionFilterIds);

        if (businessAreaFilterIds.Count > 0)
            qBase = WhereRiskMatchesAnyBusinessArea(qBase, businessAreaFilterIds);

        if (!string.IsNullOrWhiteSpace(search))
            qBase = ApplyRiskRegisterSearch(qBase, search);

        var myCount = await WhereRiskIsMine(qBase, currentUserId, emailLower).CountAsync(cancellationToken);

        var stripOpenRisk = RaidRiskClosure.WhereOpen(qBase);
        var registerStripOpenRiskCount = await stripOpenRisk.CountAsync(cancellationToken);
        var registerStripOpenCrisisCriticalRiskCount =
            await stripOpenRisk.CountAsync(r => r.RiskScore >= 16);
        var registerStripOpenModerateRiskCount =
            await stripOpenRisk.CountAsync(r => r.RiskScore >= 6 && r.RiskScore <= 15);
        var registerStripOpenMarginalNegligibleRiskCount =
            await stripOpenRisk.CountAsync(r => r.RiskScore <= 5);

        var issueStripQ = IssueQueryableAlignedToRiskRegisterFilters(
            projectId, divisionFilterIds, businessAreaFilterIds, search);
        var registerStripOpenIssueCount =
            await issueStripQ.Where(i => i.ClosedDate == null).CountAsync(cancellationToken);

        var riskEscalationLabels = await (
            from r in stripOpenRisk
            join s in _db.RiskStatuses.AsNoTracking() on r.RiskStatusId equals s.Id into gj
            from s in gj.DefaultIfEmpty()
            select s != null ? s.Label : (r.Status ?? "")).ToListAsync(cancellationToken);
        var issueEscalationLabels = await (
            from i in issueStripQ.Where(i => i.ClosedDate == null)
            join st in _db.IssueStatuses.AsNoTracking() on i.StatusId equals st.Id into gj
            from st in gj.DefaultIfEmpty()
            select st != null ? st.Label : (i.Status ?? "")).ToListAsync(cancellationToken);
        var registerStripEscalatedOpenRecordCount =
            riskEscalationLabels.Count(RaidLooksEscalated) + issueEscalationLabels.Count(RaidLooksEscalated);

        var allCount = await qBase.CountAsync(cancellationToken);
        var openCount = await RaidRiskClosure.WhereOpen(qBase).CountAsync(cancellationToken);
        var closedCount = await RaidRiskClosure.WhereClosed(qBase).CountAsync(cancellationToken);

        IQueryable<Risk> q = activeTab switch
        {
            "my" => WhereRiskIsMine(qBase, currentUserId, emailLower),
            "open" => RaidRiskClosure.WhereOpen(qBase),
            "closed" => RaidRiskClosure.WhereClosed(qBase),
            _ => qBase
        };

        var total = await q.CountAsync(cancellationToken);

        List<Risk> list;
        if (groupRowsByRiskTier)
        {
            list = await q
                .OrderBy(r => r.RiskTierId == null ? 1 : 0)
                .ThenBy(r => r.RiskTier != null ? r.RiskTier.SortOrder : int.MaxValue)
                .ThenByDescending(r => r.UpdatedAt)
                .Skip((page - 1) * RaidDefaultPageSize)
                .Take(RaidDefaultPageSize)
                .ToListAsync(cancellationToken);
        }
        else
        {
            list = await q
                .OrderByDescending(r => r.UpdatedAt)
                .Skip((page - 1) * RaidDefaultPageSize)
                .Take(RaidDefaultPageSize)
                .ToListAsync(cancellationToken);
        }

        var rows = new List<ModernRaidRiskRow>(list.Count);
        foreach (var r in list)
        {
            rows.Add(new ModernRaidRiskRow(
                r.Id,
                r.Title,
                RaidRegisterTableFormatting.FormatRiskBusinessAreaLabels(r),
                RaidRegisterTableFormatting.BuildRiskRelation(r),
                r.RiskStatus?.Label ?? r.Status,
                r.OwnerUser != null ? (r.OwnerUser.Name ?? r.OwnerUser.Email) : r.OwnerEmail,
                r.Likelihood?.Label ?? r.LikelihoodRating.ToString(),
                r.ImpactLevel?.Label ?? r.ImpactRating.ToString(),
                r.RiskScore,
                r.RiskTier?.Name,
                Snippet(r.Description, 8000),
                RaidRiskClosure.IsClosed(r)));
        }

        var statusRaw = await _db.RiskStatuses.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new { x.Id, x.Label })
            .ToListAsync(cancellationToken);
        var tierRaw = await _db.RiskTiers.AsNoTracking()
            .Where(x => x.IsActive && !x.IsProposedTier).OrderBy(x => x.SortOrder)
            .Select(x => new { x.Id, x.Name })
            .ToListAsync(cancellationToken);

        IReadOnlyList<RaidLookupOptionVm> divisionOpts = Array.Empty<RaidLookupOptionVm>();
        if (loadDivisionOptions)
        {
            divisionOpts = await _db.Divisions.AsNoTracking()
                .Where(d => d.IsActive)
                .OrderBy(d => d.Name)
                .Select(d => new RaidLookupOptionVm(d.Id, d.Name))
                .ToListAsync(cancellationToken);
        }

        IReadOnlyList<RaidLookupOptionVm> baOpts = Array.Empty<RaidLookupOptionVm>();
        if (loadBusinessAreaOptions)
        {
            baOpts = await _db.BusinessAreaLookups.AsNoTracking()
                .Where(b => b.IsActive)
                .OrderBy(b => b.SortOrder).ThenBy(b => b.Name)
                .Select(b => new RaidLookupOptionVm(b.Id, b.Name))
                .ToListAsync(cancellationToken);
        }

        var matchesSaved = effectiveBusinessAreaId is > 0 &&
                           savedRaidBusinessAreaLookupIdForHint is > 0 &&
                           effectiveBusinessAreaId == savedRaidBusinessAreaLookupIdForHint;

        return new ModernRaidRisksPageViewModel
        {
            Rows = rows,
            TotalFiltered = total,
            Page = page,
            PageSize = RaidDefaultPageSize,
            Search = search,
            ProjectId = projectId,
            RiskStatusId = riskStatusId,
            RiskTierId = riskTierId,
            OpenOnly = activeTab == "open",
            ActiveTab = activeTab,
            MyCount = myCount,
            OpenCount = openCount,
            ClosedCount = closedCount,
            AllCount = allCount,
            DivisionId = divisionFilterIds.Count == 1 ? divisionFilterIds[0] : divisionId,
            DivisionIds = divisionFilterIds,
            BusinessAreaId = businessAreaFilterIds.Count == 1 ? businessAreaFilterIds[0] : effectiveBusinessAreaId,
            BusinessAreaIds = businessAreaFilterIds,
            RaidBusinessAreaExplicitNone = raidBusinessAreaExplicitNone,
            RaidBusinessAreaFromSavedPreference = raidBusinessAreaFromSavedPreference,
            ShowRaidRegisterBusinessAreaBar = showRaidRegisterBusinessAreaBar,
            CanSaveRaidBusinessAreaPreference = canSaveRaidBusinessAreaPreference,
            RaidBusinessAreaMatchesSavedDefault = matchesSaved,
            GroupRowsByRiskTier = groupRowsByRiskTier,
            RegisterStripOpenRiskCount = registerStripOpenRiskCount,
            RegisterStripOpenCrisisCriticalRiskCount = registerStripOpenCrisisCriticalRiskCount,
            RegisterStripOpenModerateRiskCount = registerStripOpenModerateRiskCount,
            RegisterStripOpenMarginalNegligibleRiskCount = registerStripOpenMarginalNegligibleRiskCount,
            RegisterStripOpenIssueCount = registerStripOpenIssueCount,
            RegisterStripEscalatedOpenRecordCount = registerStripEscalatedOpenRecordCount,
            ProjectOptions = await RaidProjectFilterOptionsAsync(cancellationToken),
            RiskStatusOptions = statusRaw.Select(x => new RaidLookupOptionVm(x.Id, x.Label)).ToList(),
            TierOptions = tierRaw.Select(x => new RaidLookupOptionVm(x.Id, x.Name)).ToList(),
            DivisionOptions = divisionOpts,
            BusinessAreaOptions = baOpts
        };
    }

    [HttpGet("risks")]
    [HttpHead("risks")]
    [HttpGet("/ModernRaid/Risks")]
    [HttpHead("/ModernRaid/Risks")]
    public async Task<IActionResult> Risks(
        string? search,
        int? projectId,
        int? divisionId,
        [FromQuery] int[]? divisionIds,
        int? riskStatusId,
        int? riskTierId,
        string? tab,
        int? businessAreaId,
        [FromQuery] int[]? businessAreaIds,
        bool openOnly = true,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-risks");
        var currentUserId = await ResolveCurrentUserIdAsync(cancellationToken);

        var divisionFilterIds = MergeRaidRegisterFilterIds(divisionId, divisionIds);

        IReadOnlyList<int> businessAreaFilterIds;
        bool explicitNone;
        bool fromSaved;
        int? effectiveBa;

        if (businessAreaIds is { Length: > 0 })
        {
            businessAreaFilterIds = businessAreaIds.Where(id => id > 0).Distinct().ToList();
            explicitNone = false;
            fromSaved = false;
            effectiveBa = businessAreaFilterIds.Count == 1 ? businessAreaFilterIds[0] : null;
        }
        else if (Request.Query.ContainsKey("businessAreaIds"))
        {
            businessAreaFilterIds = Array.Empty<int>();
            explicitNone = true;
            fromSaved = false;
            effectiveBa = null;
        }
        else
        {
            (effectiveBa, explicitNone, fromSaved) =
                await ResolveRaidRegisterBusinessAreaAsync(businessAreaId, cancellationToken);
            businessAreaFilterIds = effectiveBa is > 0 ? new List<int> { effectiveBa.Value } : Array.Empty<int>();
        }

        var savedHint = currentUserId is > 0
            ? await GetSavedRaidRegisterBusinessAreaIdAsync(currentUserId.Value, cancellationToken)
            : null;

        var vm = await BuildRiskRegisterAsync(
            search, projectId, riskStatusId, riskTierId, tab, openOnly,
            divisionId: divisionId,
            divisionFilterIds: divisionFilterIds,
            businessAreaFilterIds: businessAreaFilterIds,
            effectiveBusinessAreaId: effectiveBa,
            raidBusinessAreaExplicitNone: explicitNone,
            raidBusinessAreaFromSavedPreference: fromSaved,
            showRaidRegisterBusinessAreaBar: true,
            canSaveRaidBusinessAreaPreference: currentUserId.HasValue,
            savedRaidBusinessAreaLookupIdForHint: savedHint,
            page,
            groupRowsByRiskTier: false,
            loadDivisionOptions: true,
            loadBusinessAreaOptions: true,
            cancellationToken);
        ViewData["RaidRegisterListAction"] = nameof(Risks);
        ViewData["RaidRegisterMode"] = "risks";
        return View("~/Views/Modern/Raid/Risks.cshtml", vm);
    }

    [HttpPost("preferences/raid-register-business-area")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRaidRegisterBusinessAreaPreference(
        int? businessAreaId,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var userId = await ResolveCurrentUserIdAsync(cancellationToken);
        if (userId is null)
            return Unauthorized();

        int? toSave = null;
        if (businessAreaId is > 0)
        {
            var ok = await _db.BusinessAreaLookups.AsNoTracking()
                .AnyAsync(b => b.Id == businessAreaId.Value && b.IsActive, cancellationToken);
            if (!ok)
            {
                TempData["ErrorMessage"] = "Choose a valid business area.";
                return LocalRedirectOrRaidRisks(returnUrl);
            }

            toSave = businessAreaId;
        }

        var pref = await _db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == userId.Value, cancellationToken);
        if (pref == null)
        {
            pref = new UserPreference
            {
                UserId = userId.Value,
                PreferredTaskGrouping = "priority",
                ShowTasksPanel = true,
                ShowProductPanel = true,
                ShowRiskPanel = true,
                ShowMilestonePanel = true,
                ShowRemindersPanel = true,
                ShowSuccessPanel = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.UserPreferences.Add(pref);
        }

        pref.RaidRegisterBusinessAreaLookupId = toSave;
        pref.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        TempData["Message"] = toSave is > 0
            ? "Your default RAID business area has been saved."
            : "Your default RAID business area has been cleared.";

        return LocalRedirectOrRaidRisks(returnUrl);
    }

    private IActionResult LocalRedirectOrRaidRisks(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Risks));
    }

    [HttpGet("issues")]
    [HttpGet("/ModernRaid/Issues")]
    public async Task<IActionResult> Issues(
        string? search,
        int? projectId,
        int? divisionId,
        int? issueStatusId,
        int? severityId,
        string? tab,
        int? businessAreaId,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-issues");
        page = page < 1 ? 1 : page;

        var currentUserId = await ResolveCurrentUserIdAsync(cancellationToken);
        var emailLower = GetCurrentUserEmailNormalized();
        var (effectiveBa, raidBaExplicitNone, raidBaFromSaved) =
            await ResolveRaidRegisterBusinessAreaAsync(businessAreaId, cancellationToken);
        var savedRaidBaHint = currentUserId is > 0
            ? await GetSavedRaidRegisterBusinessAreaIdAsync(currentUserId.Value, cancellationToken)
            : null;

        IQueryable<Issue> qBase = _db.Issues.AsNoTracking().Where(i => !i.IsDeleted);
        qBase = qBase
            .AsSplitQuery()
            .Include(i => i.StatusLookup)
            .Include(i => i.PriorityLookup)
            .Include(i => i.SeverityLookup)
            .Include(i => i.OwnerUser)
            .Include(i => i.Project).ThenInclude(p => p!.BusinessAreaLookup)
            .Include(i => i.PrimaryProduct)
            .Include(i => i.IssueBusinessAreas).ThenInclude(x => x.BusinessAreaLookup);

        if (effectiveBa is > 0)
        {
            var baId = effectiveBa.Value;
            qBase = qBase.Where(i =>
                (i.ProjectId != null &&
                 _db.Projects.Any(p => p.Id == i.ProjectId && p.BusinessAreaId == baId)) ||
                i.IssueBusinessAreas.Any(iba => iba.BusinessAreaLookupId == baId));
        }

        if (divisionId is > 0)
        {
            var did = divisionId.Value;
            qBase = qBase.Where(i =>
                (i.ProjectId != null &&
                 _db.ProjectDirectorates.Any(pd => pd.ProjectId == i.ProjectId && pd.DivisionId == did)) ||
                i.IssueDivisions.Any(idv => idv.DivisionId == did));
        }

        var activeTab = (tab ?? "").Trim().ToLowerInvariant() switch
        {
            "my" => "my",
            "open" => "open",
            "closed" => "closed",
            "all" => "all",
            _ => "my"
        };

        if (projectId is > 0)
            qBase = qBase.Where(i => i.ProjectId == projectId);

        if (issueStatusId is > 0)
            qBase = qBase.Where(i => i.StatusId == issueStatusId);

        if (severityId is > 0)
            qBase = qBase.Where(i => i.SeverityId == severityId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim();
            qBase = qBase.Where(i =>
                i.Title.Contains(t) ||
                (i.Description != null && i.Description.Contains(t)));
        }

        var myCount = await WhereIssueIsMine(qBase, currentUserId, emailLower).CountAsync(cancellationToken);

        var allCount = await qBase.CountAsync(cancellationToken);
        var openCount = await qBase.Where(i => i.ClosedDate == null).CountAsync(cancellationToken);
        var closedCount = await qBase.Where(i => i.ClosedDate != null).CountAsync(cancellationToken);

        IQueryable<Issue> q = activeTab switch
        {
            "my" => WhereIssueIsMine(qBase, currentUserId, emailLower),
            "open" => qBase.Where(i => i.ClosedDate == null),
            "closed" => qBase.Where(i => i.ClosedDate != null),
            _ => qBase
        };

        var total = await q.CountAsync(cancellationToken);
        var list = await q
            .OrderByDescending(i => i.UpdatedAt)
            .Skip((page - 1) * RaidDefaultPageSize)
            .Take(RaidDefaultPageSize)
            .ToListAsync(cancellationToken);

        var rows = new List<ModernRaidIssueRow>(list.Count);
        foreach (var i in list)
        {
            rows.Add(new ModernRaidIssueRow(
                i.Id,
                i.Title,
                RaidRegisterTableFormatting.FormatIssueBusinessAreaLabels(i),
                RaidRegisterTableFormatting.BuildIssueRelation(i),
                i.StatusLookup?.Label ?? i.Status,
                i.OwnerUser != null ? (i.OwnerUser.Name ?? i.OwnerUser.Email) : null,
                i.PriorityLookup?.Label ?? i.Priority,
                i.SeverityLookup?.Label ?? i.Severity,
                Snippet(i.Description, 8000)));
        }

        var stRaw = await _db.IssueStatuses.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new { x.Id, x.Label })
            .ToListAsync(cancellationToken);
        var sevRaw = await _db.IssueSeverities.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new { x.Id, x.Label })
            .ToListAsync(cancellationToken);

        var baOpts = await _db.BusinessAreaLookups.AsNoTracking()
            .Where(b => b.IsActive)
            .OrderBy(b => b.SortOrder).ThenBy(b => b.Name)
            .Select(b => new RaidLookupOptionVm(b.Id, b.Name))
            .ToListAsync(cancellationToken);

        var matchesSavedIssues = effectiveBa is > 0 &&
                                 savedRaidBaHint is > 0 &&
                                 effectiveBa == savedRaidBaHint;

        var vm = new ModernRaidIssuesPageViewModel
        {
            Rows = rows,
            TotalFiltered = total,
            Page = page,
            PageSize = RaidDefaultPageSize,
            Search = search,
            ProjectId = projectId,
            IssueStatusId = issueStatusId,
            SeverityId = severityId,
            OpenOnly = activeTab == "open",
            ActiveTab = activeTab,
            MyCount = myCount,
            OpenCount = openCount,
            ClosedCount = closedCount,
            AllCount = allCount,
            BusinessAreaId = effectiveBa,
            RaidBusinessAreaExplicitNone = raidBaExplicitNone,
            RaidBusinessAreaFromSavedPreference = raidBaFromSaved,
            ShowRaidRegisterBusinessAreaBar = true,
            CanSaveRaidBusinessAreaPreference = currentUserId.HasValue,
            RaidBusinessAreaMatchesSavedDefault = matchesSavedIssues,
            ProjectOptions = await RaidProjectFilterOptionsAsync(cancellationToken),
            IssueStatusOptions = stRaw.Select(x => new RaidLookupOptionVm(x.Id, x.Label)).ToList(),
            SeverityOptions = sevRaw.Select(x => new RaidLookupOptionVm(x.Id, x.Label)).ToList(),
            BusinessAreaOptions = baOpts
        };

        return View("~/Views/Modern/Raid/Issues.cshtml", vm);
    }

    [HttpGet("dependencies")]
    [HttpGet("/ModernRaid/Dependencies")]
    public async Task<IActionResult> Dependencies(
        string? search,
        int? linkTypeId,
        int? criticalityId,
        string? status,
        string? tab,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-dependencies");
        page = page < 1 ? 1 : page;

        IQueryable<Dependency> qBase = _db.Dependencies.AsNoTracking();
        qBase = qBase
            .Include(d => d.LinkTypeLookup)
            .Include(d => d.CriticalityLookup);

        var activeTab = (tab ?? "").Trim().ToLowerInvariant() switch
        {
            "open" => "open",
            "closed" => "closed",
            "all" => "all",
            _ => "open"
        };

        if (linkTypeId is > 0)
            qBase = qBase.Where(d => d.DependencyLinkTypeId == linkTypeId);

        if (criticalityId is > 0)
            qBase = qBase.Where(d => d.DependencyCriticalityId == criticalityId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            var st = status.Trim();
            qBase = qBase.Where(d => d.Status != null && d.Status == st);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim();
            qBase = qBase.Where(d =>
                (d.Description != null && d.Description.Contains(t)) ||
                (d.DependencyType != null && d.DependencyType.Contains(t)) ||
                d.SourceEntityType.Contains(t) ||
                d.TargetEntityType.Contains(t));
        }

        var allCount = await qBase.CountAsync(cancellationToken);
        var closedCount = await qBase.Where(d => EF.Functions.Like(d.Status ?? "", "%closed%")).CountAsync(cancellationToken);
        var openCount = allCount - closedCount;

        IQueryable<Dependency> q = activeTab switch
        {
            "open" => qBase.Where(d => !EF.Functions.Like(d.Status ?? "", "%closed%")),
            "closed" => qBase.Where(d => EF.Functions.Like(d.Status ?? "", "%closed%")),
            _ => qBase
        };

        var total = await q.CountAsync(cancellationToken);
        var totalPages = total <= 0 ? 1 : (int)Math.Ceiling(total / (double)RaidDefaultPageSize);
        page = Math.Min(Math.Max(page, 1), totalPages);

        var list = await q
            .OrderByDescending(d => d.UpdatedAt)
            .Skip((page - 1) * RaidDefaultPageSize)
            .Take(RaidDefaultPageSize)
            .ToListAsync(cancellationToken);

        var titleMap = await BuildDependencyEndpointTitleMapAsync(list, cancellationToken);
        string Label(string entityType, int id) =>
            titleMap.TryGetValue((NormalizeRaidEntityType(entityType), id), out var tl) ? tl : $"{entityType.Trim()} #{id}";

        var rows = list.Select(d => new ModernRaidDependencyRow(
            d.Id,
            d.SourceEntityType,
            d.SourceEntityId,
            Label(d.SourceEntityType, d.SourceEntityId),
            RaidDependencyEntityDetailUrl(d.SourceEntityType, d.SourceEntityId),
            d.TargetEntityType,
            d.TargetEntityId,
            Label(d.TargetEntityType, d.TargetEntityId),
            RaidDependencyEntityDetailUrl(d.TargetEntityType, d.TargetEntityId),
            d.DependencyType,
            d.LinkTypeLookup?.Label,
            d.CriticalityLookup?.Label,
            d.Organisation,
            d.DueDate,
            d.Description,
            d.Status,
            d.UpdatedAt)).ToList();

        var distinctStatuses = await _db.Dependencies.AsNoTracking()
            .Where(d => d.Status != null && d.Status != "")
            .Select(d => d.Status!)
            .Distinct()
            .OrderBy(s => s)
            .Take(50)
            .ToListAsync(cancellationToken);

        var ltRaw = await _db.DependencyLinkTypes.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new { x.Id, x.Label })
            .ToListAsync(cancellationToken);
        var crRaw = await _db.DependencyCriticalities.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new { x.Id, x.Label })
            .ToListAsync(cancellationToken);

        var vm = new ModernRaidDependenciesPageViewModel
        {
            Rows = rows,
            TotalFiltered = total,
            Page = page,
            PageSize = RaidDefaultPageSize,
            Search = search,
            LinkTypeId = linkTypeId,
            CriticalityId = criticalityId,
            Status = status,
            ActiveTab = activeTab,
            OpenCount = openCount,
            ClosedCount = closedCount,
            AllCount = allCount,
            LinkTypeOptions = ltRaw.Select(x => new RaidLookupOptionVm(x.Id, x.Label)).ToList(),
            CriticalityOptions = crRaw.Select(x => new RaidLookupOptionVm(x.Id, x.Label)).ToList(),
            StatusChoices = distinctStatuses
        };

        return View("~/Views/Modern/Raid/Dependencies.cshtml", vm);
    }

    [HttpGet("assumptions")]
    [HttpGet("/ModernRaid/Assumptions")]
    public async Task<IActionResult> Assumptions(
        string? search,
        int? projectId,
        int? criticalityId,
        int? statusId,
        string? tab,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-assumptions");
        page = page < 1 ? 1 : page;

        IQueryable<Assumption> qBase = _db.Assumptions.AsNoTracking().Where(a => !a.IsDeleted);
        qBase = qBase
            .Include(a => a.Project)
            .Include(a => a.PrimaryProduct)
            .Include(a => a.CriticalityLookup)
            .Include(a => a.StatusLookup);

        var activeTab = (tab ?? "").Trim().ToLowerInvariant() switch
        {
            "open" => "open",
            "closed" => "closed",
            "all" => "all",
            _ => "open"
        };

        if (projectId is > 0)
            qBase = qBase.Where(a => a.ProjectId == projectId);

        if (criticalityId is > 0)
            qBase = qBase.Where(a => a.AssumptionCriticalityId == criticalityId);

        if (statusId is > 0)
            qBase = qBase.Where(a => a.AssumptionStatusId == statusId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim();
            qBase = qBase.Where(a => a.Description.Contains(t));
        }

        var closedStatusIds = await _db.AssumptionStatuses.AsNoTracking()
            .Where(x => x.IsActive && EF.Functions.Like(x.Label, "%closed%"))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var allCount = await qBase.CountAsync(cancellationToken);
        var closedCount = closedStatusIds.Count == 0
            ? 0
            : await qBase.Where(a => a.AssumptionStatusId.HasValue && closedStatusIds.Contains(a.AssumptionStatusId.Value)).CountAsync(cancellationToken);
        var openCount = allCount - closedCount;

        IQueryable<Assumption> q = activeTab switch
        {
            "open" => closedStatusIds.Count == 0
                ? qBase
                : qBase.Where(a => !a.AssumptionStatusId.HasValue || !closedStatusIds.Contains(a.AssumptionStatusId.Value)),
            "closed" => closedStatusIds.Count == 0
                ? qBase.Where(a => false)
                : qBase.Where(a => a.AssumptionStatusId.HasValue && closedStatusIds.Contains(a.AssumptionStatusId.Value)),
            _ => qBase
        };

        var total = await q.CountAsync(cancellationToken);
        var list = await q
            .OrderByDescending(a => a.UpdatedAt)
            .Skip((page - 1) * RaidDefaultPageSize)
            .Take(RaidDefaultPageSize)
            .ToListAsync(cancellationToken);

        var rows = list.Select(a => new ModernRaidAssumptionRow(
            a.Id,
            ToRaidAssociationUiKind(a.RaidAssociationKind, a.ProjectId, a.PrimaryProductId),
            a.ProjectId,
            a.Project?.Title,
            a.PrimaryProduct != null ? (a.PrimaryProduct.DisplayName ?? a.PrimaryProduct.FipsId) : null,
            Snippet(a.Description, 160),
            a.CriticalityLookup?.Label,
            a.StatusLookup?.Label,
            a.ReviewDate,
            a.UpdatedAt)).ToList();

        var critRaw = await _db.AssumptionCriticalities.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new { x.Id, x.Label })
            .ToListAsync(cancellationToken);
        var stRaw = await _db.AssumptionStatuses.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new { x.Id, x.Label })
            .ToListAsync(cancellationToken);

        var vm = new ModernRaidAssumptionsPageViewModel
        {
            Rows = rows,
            TotalFiltered = total,
            Page = page,
            PageSize = RaidDefaultPageSize,
            Search = search,
            ProjectId = projectId,
            CriticalityId = criticalityId,
            StatusId = statusId,
            ActiveTab = activeTab,
            OpenCount = openCount,
            ClosedCount = closedCount,
            AllCount = allCount,
            ProjectOptions = await RaidProjectFilterOptionsAsync(cancellationToken),
            CriticalityOptions = critRaw.Select(x => new RaidLookupOptionVm(x.Id, x.Label)).ToList(),
            StatusOptions = stRaw.Select(x => new RaidLookupOptionVm(x.Id, x.Label)).ToList()
        };

        return View("~/Views/Modern/Raid/Assumptions.cshtml", vm);
    }

    /// <summary>Backward-compatible redirect; organisational groups are no longer listed here.</summary>
    [HttpGet("portfolios")]
    public IActionResult PortfoliosRedirect()
        => RedirectToActionPermanent(nameof(BusinessAreas));
}
