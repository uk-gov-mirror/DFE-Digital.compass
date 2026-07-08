using System.Globalization;
using Compass.Models;
using Compass.Models.Modern.Work;
using Compass.Services;
using Compass.Services.Modern;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace Compass.Controllers.Modern;

public partial class ModernWorkController
{
    [HttpGet("{id:int}/weekly-update/add")]
    public IActionResult AddWeeklyUpdate(int id, [FromQuery] string? periodKey)
    {
        if (_weeklyUpdateService.TryParsePeriodKey(periodKey ?? "", out var y, out var w))
            return RedirectToAction(nameof(WeeklyReport), new { id, year = y, week = w });

        var (year, week) = _weeklyUpdateService.ResolveDashboardReportingPeriod(DateTime.UtcNow);
        return RedirectToAction(nameof(WeeklyReport), new { id, year, week });
    }

    [HttpGet("{id:int}/weekly-update/view")]
    public async Task<IActionResult> ViewWeeklyUpdate(int id, [FromQuery] int updateId, CancellationToken cancellationToken = default)
    {
        if (!await _weeklyUpdateService.IsProjectInWeeklyReportingScopeAsync(id, cancellationToken))
            return NotFound();

        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var wu = await _context.ProjectWeeklyWorkUpdates.AsNoTracking()
            .Include(m => m.DraftRagStatusLookup)
            .FirstOrDefaultAsync(m => m.Id == updateId && m.ProjectId == id, cancellationToken);
        if (wu == null)
            return NotFound();

        if (!wu.SubmittedAt.HasValue)
            return RedirectToAction(nameof(WeeklyReport), new { id, year = wu.IsoYear, week = wu.IsoWeek });

        var work = await _modernWork.PopulateWorkDetailAsync(
            this, id, currentUser, userEmail, tab: "weeklyupdates", milestonestab: null, cancellationToken);
        if (work == null)
            return NotFound();

        ViewBag.WorkItem = work;

        var period = _weeklyUpdateService.TryGetReportingPeriod(wu.IsoYear, wu.IsoWeek);
        var vm = new WeeklyUpdate
        {
            Id = wu.Id,
            WorkItemId = wu.ProjectId,
            IsoYear = wu.IsoYear,
            IsoWeek = wu.IsoWeek,
            WeekStartDate = wu.WeekStartDate,
            WeekEndDate = wu.WeekEndDate,
            PeriodLabel = period?.PeriodLabel ?? WeeklyUpdateService.FormatPeriodLabel(wu.WeekStartDate, wu.WeekEndDate),
            Narrative = wu.Narrative,
            SubmittedAt = wu.SubmittedAt,
            SubmittedByUserId = wu.CreatedByUserId,
            SubmittedBy = wu.CreatedByName ?? wu.CreatedByEmail,
            PermFte = wu.WeeklyPermFte,
            MspFte = wu.WeeklyMspFte,
            PeopleNarrative = wu.PeopleNarrative
        };

        if (wu.CreatedByUserId.HasValue)
        {
            var sub = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == wu.CreatedByUserId.Value, cancellationToken);
            ViewBag.SubmittedByName = sub?.Name ?? sub?.Email ?? vm.SubmittedBy ?? "—";
        }
        else
        {
            ViewBag.SubmittedByName = vm.SubmittedBy ?? "—";
        }

        var ragHistDesc = await _context.ProjectRagHistories.AsNoTracking()
            .Include(r => r.RagStatusLookup)
            .Where(r => r.ProjectId == id)
            .OrderByDescending(r => r.ChangedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(cancellationToken);
        var ragLookupById = await _context.RagStatusLookups.AsNoTracking()
            .ToDictionaryAsync(r => r.Id, cancellationToken);
        var projectRow = await _context.Projects.AsNoTracking()
            .Include(p => p.RagStatusLookup)
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);

        ApplyWeeklyUpdateRagDisplay(wu, ragHistDesc, projectRow, ragLookupById, vm);

        ViewBag.RagStatusesDict = ragLookupById.Values.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Name);
        ViewBag.PeriodDueDate = _weeklyUpdateService.GetWeeklyUpdateDueDate(wu.IsoYear, wu.IsoWeek);
        ViewBag.CanUnsubmit = wu.SubmittedAt.HasValue &&
            DateTime.UtcNow.Date <= _weeklyUpdateService.GetWeeklyUpdateDueDate(wu.IsoYear, wu.IsoWeek).Date;
        ViewBag.ReportMilestoneRows = await MilestoneReportHelper.LoadSubmittedMilestoneRowsAsync(
            _context, null, wu.Id, cancellationToken);
        ViewBag.WorkChromeSubPage = false;
        ViewBag.WorkChromeMinimalHeader = false;

        return View("~/Views/Modern/Work/ViewWeeklyUpdate.cshtml", vm);
    }

    [HttpGet("{id:int}/weekly-report/{year:int}/{week:int}")]
    public async Task<IActionResult> WeeklyReport(int id, int year, int week, CancellationToken cancellationToken = default)
    {
        if (week < 1 || week > 53)
            return BadRequest("Invalid week.");

        var vm = await LoadWeeklyReportViewModelAsync(id, year, week, posted: null, cancellationToken);
        if (vm == null)
            return NotFound();

        return await WeeklyReportViewResultAsync(vm, cancellationToken);
    }

    [HttpPost("{id:int}/weekly-report/{year:int}/{week:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WeeklyReportPost(
        int id, int year, int week,
        string? narrative, string? peopleNarrative, decimal? permFte, decimal? mspFte,
        int? ragStatusId, string? ragJustification, string? pathToGreen,
        [FromForm] Dictionary<int, string>? milestoneStatus,
        [FromForm] Dictionary<int, int?>? milestoneRagStatusId,
        [FromForm] Dictionary<int, string>? milestoneUpdateNote,
        string? command,
        CancellationToken cancellationToken = default)
    {
        if (week < 1 || week > 53)
            return BadRequest("Invalid week.");

        if (!await _weeklyUpdateService.IsProjectInWeeklyReportingScopeAsync(id, cancellationToken))
            return NotFound();

        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);

        var project = await _context.Projects
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        if (!_weeklyUpdateService.IsWeeklyReportEditingAllowed(year, week))
        {
            TempData["WeeklyReportError"] =
                "This reporting week is not accepting submissions yet, or the submission window has closed.";
            return RedirectToAction(nameof(WeeklyReport), new { id, year, week });
        }

        var existingForLock = await _context.ProjectWeeklyWorkUpdates.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ProjectId == id && m.IsoYear == year && m.IsoWeek == week, cancellationToken);
        if (existingForLock?.SubmittedAt != null)
        {
            TempData["WeeklyReportError"] = "This weekly report has already been submitted.";
            return RedirectToAction(nameof(WeeklyReport), new { id, year, week });
        }

        var isSubmit = string.Equals(command, "submit", StringComparison.OrdinalIgnoreCase);
        var isSave = string.Equals(command, "save", StringComparison.OrdinalIgnoreCase);
        if (!isSubmit && !isSave)
            ModelState.AddModelError(string.Empty, "Choose Save as draft or Submit weekly report.");

        RagStatusLookup? resolvedRag = null;
        if (ragStatusId is { } ridPost)
        {
            resolvedRag = await _context.RagStatusLookups.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == ridPost && r.IsActive, cancellationToken);
        }

        var existingDraft = await _context.ProjectWeeklyWorkUpdates.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ProjectId == id && m.IsoYear == year && m.IsoWeek == week, cancellationToken);
        var milestoneRows = await ResolveWeeklyReportMilestoneRowsAsync(
            id,
            existingDraft?.Id,
            milestoneStatus,
            milestoneRagStatusId,
            milestoneUpdateNote,
            cancellationToken);
        var activeRagById = await MilestoneReportHelper.LoadActiveRagLookupsAsync(_context, cancellationToken);

        ValidateWeeklyReportForm(
            ModelState, isSubmit, narrative, peopleNarrative, permFte, mspFte,
            ragStatusId, resolvedRag, ragJustification, pathToGreen);

        MilestoneReportHelper.ValidatePostedMilestones(ModelState, isSubmit, milestoneRows, activeRagById);

        if (!ModelState.IsValid)
        {
            var posted = new WeeklyReportPostedForm(
                narrative, peopleNarrative, permFte, mspFte, ragStatusId, ragJustification, pathToGreen,
                milestoneStatus, milestoneRagStatusId, milestoneUpdateNote);
            var vmInvalid = await LoadWeeklyReportViewModelAsync(id, year, week, posted, cancellationToken);
            if (vmInvalid == null)
                return NotFound();
            vmInvalid.Milestones = milestoneRows;
            return await WeeklyReportViewResultAsync(vmInvalid, cancellationToken);
        }

        var period = _weeklyUpdateService.TryGetReportingPeriod(year, week);
        if (period == null)
            return NotFound();

        var pathPersist = resolvedRag != null && MonthlyReportIsGreenRagName(resolvedRag.Name)
            ? null
            : pathToGreen;

        var update = await _context.ProjectWeeklyWorkUpdates
            .FirstOrDefaultAsync(m => m.ProjectId == id && m.IsoYear == year && m.IsoWeek == week, cancellationToken);

        if (update == null)
        {
            update = new ProjectWeeklyWorkUpdate
            {
                ProjectId = id,
                IsoYear = year,
                IsoWeek = week,
                WeekStartDate = period.PeriodStart,
                WeekEndDate = period.PeriodEnd,
                CreatedAt = DateTime.UtcNow,
                CreatedByEmail = userEmail,
                CreatedByName = currentUser?.Name,
                CreatedByUserId = currentUser?.Id
            };
            _context.ProjectWeeklyWorkUpdates.Add(update);
        }

        update.Narrative = narrative ?? string.Empty;
        update.PeopleNarrative = peopleNarrative;
        update.WeeklyPermFte = permFte;
        update.WeeklyMspFte = mspFte;
        update.UpdatedAt = DateTime.UtcNow;
        update.DraftRagStatusLookupId = ragStatusId;
        update.DraftRagJustification = ragStatusId.HasValue ? ragJustification : null;
        update.DraftPathToGreen = ragStatusId.HasValue ? pathPersist : null;

        if (isSubmit && ragStatusId.HasValue)
        {
            var ragEntry = new ProjectRagHistory
            {
                ProjectId = id,
                RagStatusLookupId = ragStatusId.Value,
                RagStatus = resolvedRag?.Name ?? string.Empty,
                Justification = ragJustification,
                PathToGreen = pathPersist,
                ChangedAt = DateTime.UtcNow,
                ChangedByEmail = userEmail,
                ChangedByName = currentUser?.Name
            };
            _context.ProjectRagHistories.Add(ragEntry);
            project.RagStatusLookupId = ragStatusId.Value;
#pragma warning disable CS0618
            project.RagStatus = resolvedRag?.Name;
#pragma warning restore CS0618
            project.RagJustification = ragJustification;
            project.PathToGreen = pathPersist;
            update.SubmittedAt = DateTime.UtcNow;
        }

        project.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        await MilestoneReportHelper.PersistMilestoneEntriesForWeeklyUpdateAsync(
            _context,
            id,
            update.Id,
            milestoneRows,
            activeRagById,
            userEmail,
            currentUser?.Name,
            cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        TempData["WeeklyReportMessage"] = isSubmit
            ? "Weekly update submitted successfully."
            : "Weekly update saved as draft.";

        return RedirectToAction(nameof(WeeklyReport), new { id, year, week });
    }

    [HttpPost("{id:int}/weekly-report/{year:int}/{week:int}/unsubmit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WeeklyReportUnsubmit(int id, int year, int week, CancellationToken cancellationToken = default)
    {
        var update = await _context.ProjectWeeklyWorkUpdates
            .FirstOrDefaultAsync(m => m.ProjectId == id && m.IsoYear == year && m.IsoWeek == week, cancellationToken);
        if (update == null)
            return NotFound();

        if (!update.SubmittedAt.HasValue)
        {
            TempData["WeeklyReportMessage"] = "This update is not submitted.";
            return RedirectToAction(nameof(WeeklyReport), new { id, year, week });
        }

        var dueDate = _weeklyUpdateService.GetWeeklyUpdateDueDate(year, week);
        if (DateTime.UtcNow.Date > dueDate.Date)
        {
            TempData["WeeklyReportError"] = "Unsubmit is only allowed before the period due date has passed.";
            return RedirectToAction(nameof(WeeklyReport), new { id, year, week });
        }

        update.SubmittedAt = null;
        var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project != null)
            project.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        TempData["WeeklyReportMessage"] = "Weekly update unsubmitted. You can now edit and resubmit.";
        return RedirectToAction(nameof(WeeklyReport), new { id, year, week });
    }

    private sealed record WeeklyReportPostedForm(
        string? Narrative,
        string? PeopleNarrative,
        decimal? PermFte,
        decimal? MspFte,
        int? RagStatusId,
        string? RagJustification,
        string? PathToGreen,
        Dictionary<int, string>? MilestoneStatus = null,
        Dictionary<int, int?>? MilestoneRagStatusId = null,
        Dictionary<int, string>? MilestoneUpdateNote = null);

    private static void ValidateWeeklyReportForm(
        ModelStateDictionary modelState,
        bool isSubmit,
        string? narrative,
        string? peopleNarrative,
        decimal? permFte,
        decimal? mspFte,
        int? ragStatusId,
        RagStatusLookup? resolvedRag,
        string? ragJustification,
        string? pathToGreen)
    {
        ValidateMonthlyReportForm(
            modelState,
            isSubmit,
            narrative,
            peopleNarrative,
            permFte,
            mspFte,
            ragStatusId,
            resolvedRag,
            ragJustification,
            pathToGreen);

        if (modelState.TryGetValue(nameof(narrative), out var narrativeEntry) &&
            narrativeEntry?.Errors.Count > 0 &&
            narrativeEntry.Errors[0].ErrorMessage.Contains("monthly", StringComparison.OrdinalIgnoreCase))
        {
            modelState.Remove(nameof(narrative));
            if (isSubmit && string.IsNullOrWhiteSpace(narrative))
                modelState.AddModelError(nameof(narrative), "Enter a weekly update narrative.");
        }
    }

    private async Task<WeeklyReportViewModel?> LoadWeeklyReportViewModelAsync(
        int id,
        int year,
        int week,
        WeeklyReportPostedForm? posted,
        CancellationToken cancellationToken)
    {
        if (week < 1 || week > 53)
            return null;

        if (!await _weeklyUpdateService.IsProjectInWeeklyReportingScopeAsync(id, cancellationToken))
            return null;

        var period = _weeklyUpdateService.TryGetReportingPeriod(year, week);
        if (period == null)
            return null;

        var project = await _context.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return null;

        var update = await _context.ProjectWeeklyWorkUpdates.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ProjectId == id && m.IsoYear == year && m.IsoWeek == week, cancellationToken);

        var ragHistDesc = await _context.ProjectRagHistories.AsNoTracking()
            .Include(r => r.RagStatusLookup)
            .Where(r => r.ProjectId == id)
            .OrderByDescending(r => r.ChangedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(cancellationToken);
        var latestRag = ragHistDesc.FirstOrDefault();

        var ragStatuses = await _context.RagStatusLookups.AsNoTracking()
            .Where(r => r.IsActive)
            .OrderBy(r => r.SortOrder)
            .Select(r => new RagStatus { Id = r.Id, Name = r.Name, Description = r.Description })
            .ToListAsync(cancellationToken);

        var dueDate = period.DueDate;
        var canUnsubmit = update?.SubmittedAt != null && DateTime.UtcNow.Date <= dueDate.Date;

        int? currentRagId = update?.DraftRagStatusLookupId ?? latestRag?.RagStatusLookupId;
        string? currentRagJustification = update?.DraftRagJustification ?? latestRag?.Justification;
        string? currentPathToGreen = update?.DraftPathToGreen ?? latestRag?.PathToGreen;

        string? submittedByName = null;
        if (update?.SubmittedAt != null)
        {
            submittedByName = update.CreatedByName;
            if (string.IsNullOrEmpty(submittedByName) && update.CreatedByUserId.HasValue)
            {
                var sub = await _context.Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == update.CreatedByUserId.Value, cancellationToken);
                submittedByName = sub?.Name ?? sub?.Email;
            }
            submittedByName ??= update.CreatedByEmail ?? "Unknown";
        }

        var vm = new WeeklyReportViewModel
        {
            WorkItemId = id,
            WorkItemTitle = project.Title,
            WorkItemReference = "WI-" + id.ToString("D8", CultureInfo.InvariantCulture),
            IsoYear = year,
            IsoWeek = week,
            PeriodKey = period.PeriodKey,
            PeriodLabel = period.PeriodLabel,
            UpdateId = update?.Id,
            IsSubmitted = update?.SubmittedAt.HasValue == true,
            SubmittedAt = update?.SubmittedAt,
            SubmittedByName = submittedByName,
            Narrative = update?.Narrative,
            PeopleNarrative = update?.PeopleNarrative,
            PermFte = update?.WeeklyPermFte,
            MspFte = update?.WeeklyMspFte,
            RagStatusId = currentRagId,
            RagJustification = currentRagJustification,
            PathToGreen = currentPathToGreen,
            DueDate = dueDate,
            CanUnsubmit = canUnsubmit,
            SubmissionOpens = period.SubmissionOpens,
            SubmissionCloses = period.SubmissionCloses,
            DueRuleDescription =
                $"Submission opens {period.SubmissionOpens:d MMMM yyyy}, closes {period.SubmissionCloses:d MMMM yyyy}",
            CanEditWeeklySubmission = _weeklyUpdateService.IsWeeklyReportEditingAllowed(year, week),
            RagStatuses = ragStatuses
        };

        if (posted != null)
        {
            vm.Narrative = posted.Narrative;
            vm.PeopleNarrative = posted.PeopleNarrative;
            vm.PermFte = posted.PermFte;
            vm.MspFte = posted.MspFte;
            vm.RagStatusId = posted.RagStatusId;
            vm.RagJustification = posted.RagJustification;
            vm.PathToGreen = posted.PathToGreen;
        }

        vm.PreviousWeekSubmission = await TryLoadPreviousWeekSubmissionAsync(id, year, week, ragHistDesc, cancellationToken);

        vm.Milestones = await MilestoneReportHelper.LoadReportMilestoneRowsAsync(
            _context, id, null, update?.Id, cancellationToken);

        if (posted != null)
        {
            var activeRagById = await MilestoneReportHelper.LoadActiveRagLookupsAsync(_context, cancellationToken);
            vm.Milestones = MilestoneReportHelper.ApplyPostedMilestoneRows(
                vm.Milestones,
                posted.MilestoneStatus,
                posted.MilestoneRagStatusId,
                posted.MilestoneUpdateNote,
                activeRagById);
        }

        return vm;
    }

    private async Task<List<ReportMilestoneRowViewModel>> ResolveWeeklyReportMilestoneRowsAsync(
        int projectId,
        int? weeklyUpdateId,
        Dictionary<int, string>? postedStatus,
        Dictionary<int, int?>? postedRagStatusId,
        Dictionary<int, string>? postedUpdateNote,
        CancellationToken cancellationToken)
    {
        var baseline = await MilestoneReportHelper.LoadReportMilestoneRowsAsync(
            _context, projectId, null, weeklyUpdateId, cancellationToken);
        var activeRagById = await MilestoneReportHelper.LoadActiveRagLookupsAsync(_context, cancellationToken);
        return MilestoneReportHelper.ApplyPostedMilestoneRows(
            baseline, postedStatus, postedRagStatusId, postedUpdateNote, activeRagById);
    }

    private async Task<WeeklyReportPreviousSubmission?> TryLoadPreviousWeekSubmissionAsync(
        int projectId,
        int year,
        int week,
        List<ProjectRagHistory> ragHistDesc,
        CancellationToken cancellationToken)
    {
        var anchor = ISOWeek.ToDateTime(year, week, DayOfWeek.Monday).AddDays(-7);
        var prevYear = ISOWeek.GetYear(anchor);
        var prevWeek = ISOWeek.GetWeekOfYear(anchor);

        var prevUpdate = await _context.ProjectWeeklyWorkUpdates.AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.ProjectId == projectId && m.IsoYear == prevYear && m.IsoWeek == prevWeek && m.SubmittedAt != null,
                cancellationToken);
        if (prevUpdate == null)
            return null;

        var period = _weeklyUpdateService.TryGetReportingPeriod(prevYear, prevWeek);
        var ragLookupById = await _context.RagStatusLookups.AsNoTracking()
            .ToDictionaryAsync(r => r.Id, cancellationToken);

        string? ragName = null;
        string? ragCss = null;
        if (prevUpdate.DraftRagStatusLookupId is int rid && ragLookupById.TryGetValue(rid, out var rag))
        {
            ragName = rag.Name;
            ragCss = rag.CssClass;
        }

        return new WeeklyReportPreviousSubmission
        {
            IsoYear = prevYear,
            IsoWeek = prevWeek,
            PeriodLabel = period?.PeriodLabel ?? WeeklyUpdateService.FormatPeriodLabel(prevUpdate.WeekStartDate, prevUpdate.WeekEndDate),
            SubmittedAt = prevUpdate.SubmittedAt,
            SubmittedByName = prevUpdate.CreatedByName ?? prevUpdate.CreatedByEmail,
            Narrative = prevUpdate.Narrative,
            PeopleNarrative = prevUpdate.PeopleNarrative,
            PermFte = prevUpdate.WeeklyPermFte,
            MspFte = prevUpdate.WeeklyMspFte,
            RagName = ragName,
            RagCssClass = ragCss,
            RagJustification = prevUpdate.DraftRagJustification,
            PathToGreen = prevUpdate.DraftPathToGreen,
            IsGreenRag = (ragName ?? "").Contains("green", StringComparison.OrdinalIgnoreCase)
        };
    }

    private async Task<IActionResult> WeeklyReportViewResultAsync(
        WeeklyReportViewModel vm,
        CancellationToken cancellationToken)
    {
        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var work = await _modernWork.PopulateWorkDetailAsync(
            this, vm.WorkItemId, currentUser, userEmail, tab: "weeklyupdates", milestonestab: null, cancellationToken);
        if (work == null)
            return NotFound();

        ViewBag.WorkItem = work;
        ViewBag.WorkChromeSubPage = false;
        ViewBag.WorkChromeMinimalHeader = false;
        return View("~/Views/Modern/Work/WeeklyReport.cshtml", vm);
    }

    private static void ApplyWeeklyUpdateRagDisplay(
        ProjectWeeklyWorkUpdate wu,
        IReadOnlyList<ProjectRagHistory> historyDesc,
        Project? project,
        IReadOnlyDictionary<int, RagStatusLookup> ragLookupById,
        WeeklyUpdate vm)
    {
        if (wu.DraftRagStatusLookupId is int draftId && draftId > 0)
        {
            if (wu.DraftRagStatusLookup is { } draft)
            {
                vm.RagStatusId = draftId;
                vm.RagDisplayName = draft.Name;
                vm.RagCssClass = draft.CssClass;
            }
            else if (ragLookupById.TryGetValue(draftId, out var lookup))
            {
                vm.RagStatusId = draftId;
                vm.RagDisplayName = lookup.Name;
                vm.RagCssClass = lookup.CssClass;
            }
            vm.RagJustification = wu.DraftRagJustification;
            vm.PathToGreen = wu.DraftPathToGreen;
            return;
        }

        if (wu.SubmittedAt.HasValue)
        {
            var atSubmit = MonthlyUpdateSubmittedRagResolver.Resolve(historyDesc, wu.SubmittedAt.Value);
            if (atSubmit != null)
            {
                vm.RagStatusId = atSubmit.RagStatusLookupId;
                vm.RagJustification = atSubmit.Justification;
                vm.PathToGreen = atSubmit.PathToGreen;
                if (atSubmit.RagStatusLookupId is int ragId && ragLookupById.TryGetValue(ragId, out var lookup))
                {
                    vm.RagDisplayName = lookup.Name;
                    vm.RagCssClass = lookup.CssClass;
                }
            }
        }
    }
}
