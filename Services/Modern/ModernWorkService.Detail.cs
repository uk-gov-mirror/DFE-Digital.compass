using System.Globalization;
using System.Text.Json;
using Compass.Models;
using Compass.Models.Modern.Work;
using Compass.ViewModels.Modern;
using Compass.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services.Modern;

public partial class ModernWorkService
{
    private static WorkAppUser? ToWorkApp(User? u) =>
        u == null
            ? null
            : new WorkAppUser { Name = u.Name, Email = u.Email };

    public async Task<WorkItem?> PopulateWorkDetailAsync(
        Controller controller,
        int projectId,
        User currentUser,
        string userEmail,
        string? tab,
        string? milestonestab,
        CancellationToken cancellationToken = default)
    {
        var emailLower = EmailLower(userEmail);

        var p = await _db.Projects
            .AsNoTracking()
            .AsSplitQuery()
            .Where(x => x.Id == projectId && !x.IsDeleted)
            .Include(x => x.RagHistory).ThenInclude(r => r.RagStatusLookup)
            .Include(x => x.MonthlyUpdates).ThenInclude(mu => mu.CreatedByUser)
            .Include(x => x.MonthlyUpdates).ThenInclude(mu => mu.MonthlyUpdateNarratives)
            .Include(x => x.MonthlyUpdates).ThenInclude(mu => mu.DraftRagStatusLookup)
            .Include(x => x.WeeklyWorkUpdates).ThenInclude(wu => wu.DraftRagStatusLookup)
            .Include(x => x.Milestones).ThenInclude(m => m.RagStatusLookup)
            .Include(x => x.Risks).ThenInclude(r => r.OwnerUser)
            .Include(x => x.Risks).ThenInclude(r => r.RiskTier)
            .Include(x => x.Risks).ThenInclude(r => r.RiskStatus)
            .Include(x => x.Risks).ThenInclude(r => r.RiskPriority)
            .Include(x => x.Risks).ThenInclude(r => r.Likelihood)
            .Include(x => x.Risks).ThenInclude(r => r.ImpactLevel)
            .Include(x => x.Risks).ThenInclude(r => r.RiskBusinessAreas).ThenInclude(rba => rba.BusinessAreaLookup)
            .Include(x => x.Issues).ThenInclude(i => i.OwnerUser)
            .Include(x => x.Issues).ThenInclude(i => i.IssueBusinessAreas).ThenInclude(iba => iba.BusinessAreaLookup)
            .Include(x => x.Issues).ThenInclude(i => i.StatusLookup)
            .Include(x => x.Issues).ThenInclude(i => i.SeverityLookup)
            .Include(x => x.Issues).ThenInclude(i => i.PriorityLookup)
            .Include(x => x.RagStatusLookup)
            .Include(x => x.PhaseLookup)
            .Include(x => x.DeliveryPriority)
            .Include(x => x.BusinessAreaLookup)
            .Include(x => x.PrimaryOrganizationalGroup)
            .Include(x => x.PrimaryContactUser)
            .Include(x => x.ActivityTypeLookup)
            .Include(x => x.RiskAppetiteLookup)
            .Include(x => x.Directorates).ThenInclude(d => d.Division)
            .Include(x => x.ProblemStatements)
            .Include(x => x.ProjectMissions).ThenInclude(pm => pm.Mission)
            .Include(x => x.ProjectObjectives).ThenInclude(po => po.Objective)
            .Include(x => x.ProjectContacts).ThenInclude(pc => pc.User)
            .Include(x => x.SeniorResponsibleOfficers).ThenInclude(sro => sro.User)
            .Include(x => x.ServiceOwners).ThenInclude(so => so.User)
            .Include(x => x.PmoContacts).ThenInclude(pmo => pmo.User)
            .Include(x => x.BudgetOwners).ThenInclude(bo => bo.BusinessAreaLookup)
            .Include(x => x.ProjectWorkItemTags).ThenInclude(t => t.WorkItemTagLookup)
            .FirstOrDefaultAsync(cancellationToken);

        if (p == null)
            return null;

        // Link child risks/issues to this project in-memory (avoids Include cycle Project→Risks→Project in no-tracking queries).
        // RaidRegisterTableFormatting fallbacks use r.Project / i.Project for BusinessAreaLookup when junctions are empty.
        foreach (var r in p.Risks)
            r.Project = p;
        foreach (var i in p.Issues)
            i.Project = p;

        var work = MapProjectToWorkItem(p);

        var ps = p.ProblemStatements?.OrderByDescending(x => x.UpdatedAt).FirstOrDefault()?.ProblemStatement;
        work.ProblemStatement = string.IsNullOrWhiteSpace(ps) ? null : ps;

        work.PriorityChangeReason = p.DeliveryPriorityChangeReason;

        work.PriorityOutcomes.Clear();
        foreach (var po in p.ProjectObjectives)
        {
            if (po.Objective == null) continue;
            work.PriorityOutcomes.Add(new WorkItemPriorityOutcome
            {
                Id = po.Id,
                PriorityOutcomeId = po.ObjectiveId,
                PriorityOutcome = new LookupOption
                {
                    Id = po.Objective.Id,
                    Name = po.Objective.Title,
                    Value = po.Objective.Title
                }
            });
        }

        work.MissionPillars.Clear();
        foreach (var pm in p.ProjectMissions)
        {
            if (pm.Mission == null) continue;
            work.MissionPillars.Add(new WorkItemMissionPillar
            {
                Id = pm.Id,
                MissionPillarId = pm.MissionId,
                MissionPillar = new LookupOption
                {
                    Id = pm.Mission.Id,
                    Name = pm.Mission.Title,
                    Value = pm.Mission.Title
                }
            });
        }

        work.Directorates.Clear();
        foreach (var d in p.Directorates)
        {
            work.Directorates.Add(new WorkItemDirectorate
            {
                Id = d.Id,
                DirectorateId = d.DivisionId,
                Division = d.Division
            });
        }

        work.GovernmentDepartments.Clear();
        if (!string.IsNullOrWhiteSpace(p.OtherDepartments))
        {
            try
            {
                var ids = JsonSerializer.Deserialize<int[]>(p.OtherDepartments);
                if (ids is { Length: > 0 })
                {
                    var gds = await _db.GovernmentDepartments.AsNoTracking()
                        .Where(g => ids.Contains(g.Id))
                        .ToListAsync(cancellationToken);
                    foreach (var gd in gds)
                    {
                        work.GovernmentDepartments.Add(new WorkItemGovernmentDepartment
                        {
                            Id = gd.Id,
                            GovernmentDepartmentId = gd.Id,
                            GovernmentDepartment = gd
                        });
                    }
                }
            }
            catch
            {
                // ignore malformed JSON
            }
        }

        ProjectGovernanceContacts.PopulateWorkItemContacts(work, p);

        work.RiskOrIssues.Clear();
        foreach (var r in p.Risks.Where(x => !x.IsDeleted))
        {
            var statusName = r.RiskStatus?.Label ?? r.Status ?? "";
            var closedRisk = r.ClosedDate.HasValue ||
                             statusName.Contains("closed", StringComparison.OrdinalIgnoreCase);
            var lLabel = r.Likelihood?.Label;
            var iLabel = r.ImpactLevel?.Label;
            work.RiskOrIssues.Add(new WorkItemRiskOrIssue
            {
                Id = r.Id,
                WorkItemId = p.Id,
                ReferenceNumber = r.Id,
                Type = "Risk",
                Title = r.Title,
                Description = r.Description,
                Tier = r.RiskTier?.Name,
                Priority = r.RiskPriority?.Label,
                Status = statusName,
                MitigationOrAction = !string.IsNullOrWhiteSpace(r.ResponseStrategy)
                    ? r.ResponseStrategy
                    : r.Notes,
                Likelihood = lLabel,
                LikelihoodLabel = lLabel,
                ImpactLabel = iLabel,
                RiskScore = r.RiskScore,
                OwnerUser = ToWorkApp(r.OwnerUser),
                RaisedAt = r.IdentifiedDate ?? r.CreatedAt,
                ClosedAt = closedRisk ? r.ClosedDate ?? r.UpdatedAt : null,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
                BusinessAreaLabel = RaidRegisterTableFormatting.FormatRiskBusinessAreaLabels(r)
            });
        }

        foreach (var i in p.Issues.Where(x => !x.IsDeleted))
        {
            var statusName = i.StatusLookup?.Label ?? i.Status ?? "";
            var closedIssue = i.ClosedDate.HasValue ||
                              statusName.Contains("closed", StringComparison.OrdinalIgnoreCase);
            var mit = !string.IsNullOrWhiteSpace(i.Workaround)
                ? i.Workaround
                : (i.ResolutionSummary ?? "");
            var sevLbl = i.SeverityLookup?.Label ?? i.Severity;
            var priLbl = i.PriorityLookup?.Label ?? i.Priority;
            work.RiskOrIssues.Add(new WorkItemRiskOrIssue
            {
                Id = i.Id,
                WorkItemId = p.Id,
                ReferenceNumber = i.Id,
                Type = "Issue",
                Title = i.Title,
                Description = i.Description,
                Priority = sevLbl,
                IssueSeverityLabel = sevLbl,
                IssuePriorityLabel = priLbl,
                Status = statusName,
                MitigationOrAction = mit,
                OwnerUser = ToWorkApp(i.OwnerUser),
                RaisedAt = i.DetectedDate,
                ClosedAt = closedIssue ? i.ClosedDate ?? i.UpdatedAt : null,
                ClosureOutcome = i.ResolutionSummary,
                CreatedAt = i.CreatedAt,
                UpdatedAt = i.UpdatedAt,
                BusinessAreaLabel = RaidRegisterTableFormatting.FormatIssueBusinessAreaLabels(i)
            });
        }

        work.Dependencies.Clear();
        var dependencyRows = await _db.Dependencies.AsNoTracking()
            .Where(d =>
                (d.Status == null || d.Status == "Active") &&
                (
                    (d.SourceEntityType == "Project" && d.SourceEntityId == projectId) ||
                    (d.TargetEntityType == "Project" && d.TargetEntityId == projectId)
                ))
            .ToListAsync(cancellationToken);

        var projectIdsForLookup = new HashSet<int>();
        foreach (var d in dependencyRows)
        {
            if (d.SourceEntityType == "Project" && d.SourceEntityId != projectId)
                projectIdsForLookup.Add(d.SourceEntityId);
            if (d.TargetEntityType == "Project" && d.TargetEntityId != projectId)
                projectIdsForLookup.Add(d.TargetEntityId);
        }

        var projectsById = new Dictionary<int, Project>();
        if (projectIdsForLookup.Count > 0)
        {
            var projRows = await _db.Projects.AsNoTracking()
                .Where(p => projectIdsForLookup.Contains(p.Id))
                .Include(p => p.PrimaryOrganizationalGroup)
                .Include(p => p.BusinessAreaLookup)
                .ToListAsync(cancellationToken);
            foreach (var pr in projRows)
                projectsById[pr.Id] = pr;
        }

        var depPortfolioNames = new Dictionary<int, string>();
        foreach (var d in dependencyRows)
        {
            if (d.SourceEntityType == "Project" && d.SourceEntityId == projectId &&
                d.TargetEntityType == "Project" && d.TargetEntityId != projectId &&
                projectsById.TryGetValue(d.TargetEntityId, out var tp))
            {
                var targetPortfolioId = tp.BusinessAreaId ?? tp.PrimaryOrganizationalGroupId;
                work.Dependencies.Add(new WorkItemDependency
                {
                    Id = d.Id,
                    WorkItemId = projectId,
                    Direction = "In",
                    IsInternal = true,
                    ExternalDescription = null,
                    TargetWorkItem = new WorkItem
                    {
                        Id = tp.Id,
                        Title = tp.Title ?? "",
                        PortfolioId = targetPortfolioId
                    }
                });
                if (targetPortfolioId.HasValue)
                {
                    var label = ModernWorkService.ResolveProjectBusinessAreaDisplayName(tp);
                    if (label != "—")
                        depPortfolioNames[targetPortfolioId.Value] = label;
                }
            }
            else if (d.SourceEntityType == "Project" && d.SourceEntityId == projectId &&
                     string.Equals(d.TargetEntityType, "External", StringComparison.OrdinalIgnoreCase))
            {
                work.Dependencies.Add(new WorkItemDependency
                {
                    Id = d.Id,
                    WorkItemId = projectId,
                    Direction = "In",
                    IsInternal = false,
                    ExternalDescription = d.Description,
                    TargetWorkItem = null
                });
            }
            else if (d.TargetEntityType == "Project" && d.TargetEntityId == projectId &&
                     d.SourceEntityType == "Project" && d.SourceEntityId != projectId &&
                     projectsById.TryGetValue(d.SourceEntityId, out var sp))
            {
                var sourcePortfolioId = sp.BusinessAreaId ?? sp.PrimaryOrganizationalGroupId;
                work.Dependencies.Add(new WorkItemDependency
                {
                    Id = d.Id,
                    WorkItemId = projectId,
                    Direction = "Out",
                    IsInternal = true,
                    ExternalDescription = null,
                    TargetWorkItem = new WorkItem
                    {
                        Id = sp.Id,
                        Title = sp.Title ?? "",
                        PortfolioId = sourcePortfolioId
                    }
                });
                if (sourcePortfolioId.HasValue)
                {
                    var label = ModernWorkService.ResolveProjectBusinessAreaDisplayName(sp);
                    if (label != "—")
                        depPortfolioNames[sourcePortfolioId.Value] = label;
                }
            }
            else if (d.TargetEntityType == "Project" && d.TargetEntityId == projectId &&
                     string.Equals(d.SourceEntityType, "External", StringComparison.OrdinalIgnoreCase))
            {
                work.Dependencies.Add(new WorkItemDependency
                {
                    Id = d.Id,
                    WorkItemId = projectId,
                    Direction = "Out",
                    IsInternal = false,
                    ExternalDescription = d.Description,
                    TargetWorkItem = null
                });
            }
        }

        controller.ViewBag.DependencyTargetPortfolioNames = depPortfolioNames;

        work.Assumptions.Clear();
        var asmRows = await _db.Assumptions.AsNoTracking()
            .Where(a => !a.IsDeleted && a.ProjectId == projectId)
            .Include(a => a.CriticalityLookup)
            .Include(a => a.StatusLookup)
            .OrderByDescending(a => a.UpdatedAt)
            .ToListAsync(cancellationToken);
        foreach (var a in asmRows)
        {
            work.Assumptions.Add(new WorkItemAssumptionRef
            {
                Id = a.Id,
                Description = a.Description ?? "",
                Criticality = a.CriticalityLookup?.Label,
                Status = a.StatusLookup?.Label
            });
        }

        var canEdit = await CanUserEditWorkItemAsync(projectId, userEmail, cancellationToken);

        var isWatching = await _db.ProjectWatchlists.AsNoTracking()
            .AnyAsync(w => w.UserId == currentUser.Id && w.ProjectId == projectId, cancellationToken);

        var ragRows = await _db.RagStatusLookups.AsNoTracking()
            .Where(r => r.IsActive)
            .OrderBy(r => r.SortOrder)
            .ToListAsync(cancellationToken);
        var ragStatusesDict = ragRows.ToDictionary(r => r.Id, r => r.Name);
        var ragLookupById = await _db.RagStatusLookups.AsNoTracking()
            .ToDictionaryAsync(r => r.Id, cancellationToken);
        EnrichMonthlyUpdateRagDisplay(work, p, ragLookupById);
        var ragBgByStatusId = new Dictionary<int, string?>();
        var ragTextByStatusId = new Dictionary<int, string?>();

        var riskAppetiteOpts = await _db.RiskAppetiteLookups.AsNoTracking()
            .Where(r => r.IsActive)
            .OrderBy(r => r.SortOrder)
            .Select(r => new LookupOption
            {
                Id = r.Id,
                Name = r.Name,
                Value = r.Name
            })
            .ToListAsync(cancellationToken);

        var nowDate = DateTime.UtcNow.Date;
        var (reportY, reportM) = _monthlyUpdateService.ResolveDashboardReportingPeriod(DateTime.UtcNow);
        var currentDueDate = _monthlyUpdateService.GetMonthlyUpdateDueDate(reportY, reportM);
        var currentPeriodLabel = new DateTime(reportY, reportM, 1).ToString("MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));

        var submittedMonths = new HashSet<(int Y, int M)>();
        foreach (var mu in p.MonthlyUpdates)
        {
            if (mu.SubmittedAt.HasValue)
                submittedMonths.Add((mu.Year, mu.Month));
        }

        var hasCurrent = submittedMonths.Contains((reportY, reportM));
        var monthlyReportDue = (work.Status == "Active" || work.Status == "Paused")
            && currentDueDate.Date < nowDate
            && !hasCurrent;

        int? daysRemaining = null;
        if (currentDueDate.Date >= nowDate)
            daysRemaining = (currentDueDate.Date - nowDate).Days;

        ProjectMonthlyUpdate? lastSubmitted = null;
        foreach (var mu in p.MonthlyUpdates.Where(m => m.SubmittedAt.HasValue)
                     .OrderByDescending(m => m.Year).ThenByDescending(m => m.Month))
        {
            lastSubmitted = mu;
            break;
        }

        string? lastMonthLabel = null;
        string? lastBy = null;
        if (lastSubmitted != null)
        {
            lastMonthLabel = new DateTime(lastSubmitted.Year, lastSubmitted.Month, 1)
                .ToString("MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
            lastBy = lastSubmitted.CreatedByName ?? lastSubmitted.CreatedByUser?.Name;
        }

        var latestRag = work.RagHistory.OrderByDescending(r => r.UpdatedAt).ThenByDescending(r => r.Id).FirstOrDefault();
        var currentRag = ProjectCurrentRagResolver.Resolve(p);
        var currentRagName = currentRag.Name ?? latestRag?.RagStatus?.Name;

        var milestoneProgressNames = work.Milestones
            .Where(m => !m.IsDeleted)
            .ToDictionary(
                m => m.Id,
                m => string.IsNullOrEmpty(m.Status) ? "—" : m.Status.Replace("_", " ", StringComparison.OrdinalIgnoreCase));

        var submittedUserIds = p.MonthlyUpdates
            .Where(m => m.SubmittedAt.HasValue && m.CreatedByUserId.HasValue)
            .Select(m => m.CreatedByUserId!.Value)
            .Distinct()
            .ToList();
        var monthlyUpdateSubmittedByNames = new Dictionary<int, string>();
        if (submittedUserIds.Count > 0)
        {
            var users = await _db.Users.AsNoTracking()
                .Where(u => submittedUserIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Name, u.Email })
                .ToListAsync(cancellationToken);
            foreach (var u in users)
                monthlyUpdateSubmittedByNames[u.Id] = u.Name ?? u.Email ?? "—";
        }

        var reportingPeriods = new List<ReportingCyclePeriod>();
        var periodDueByKey = new Dictionary<string, (DateTime DueDate, string Label)>();
        var updatesByYearMonth = p.MonthlyUpdates
            .ToLookup(mu => (mu.Year, mu.Month));
        var currentMonth = new DateTime(reportY, reportM, 1);
        for (var i = -18; i <= 1; i++)
        {
            var dt = new DateTime(reportY, reportM, 1).AddMonths(i);
            var y = dt.Year;
            var m = dt.Month;
            var key = y + "-" + m;
            var due = _monthlyUpdateService.GetMonthlyUpdateDueDate(y, m);
            var label = dt.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
            periodDueByKey[key] = (due, label);

            var hasUpdate = updatesByYearMonth[(y, m)].Any();
            var isCurrentOrFuture = dt >= currentMonth;
            var explicitPeriod = _monthlyUpdateService.TryGetActiveExplicitReportingPeriod(y, m);
            if (!hasUpdate && !isCurrentOrFuture)
            {
                var graceEditingAllowed = explicitPeriod != null
                    && _monthlyUpdateService.IsMonthlyReportEditingAllowed(y, m);
                if (!graceEditingAllowed)
                    continue;
            }
            var muForPeriod = updatesByYearMonth[(y, m)].FirstOrDefault();
            var rollup = _monthlyUpdateService.CalculateUpdateStatus(y, m, muForPeriod?.SubmittedAt);
            reportingPeriods.Add(new ReportingCyclePeriod
            {
                PeriodKey = key,
                PeriodLabel = label,
                DueDate = due,
                SubmissionOpens = explicitPeriod?.SubmissionOpens,
                SubmissionCloses = explicitPeriod?.SubmissionCloses,
                UpdateStatus = rollup.ToString(),
                WindowAllowsEditing = explicitPeriod != null
                    ? _monthlyUpdateService.IsMonthlyReportEditingAllowed(y, m)
                    : true
            });
        }

        static int PeriodKeySortDesc(string periodKey)
        {
            var parts = periodKey.Split('-');
            var py = parts.Length >= 1 && int.TryParse(parts[0], out var yy) ? yy : 0;
            var pm = parts.Length >= 2 && int.TryParse(parts[1], out var mm) ? mm : 0;
            return py * 100 + pm;
        }

        reportingPeriods = reportingPeriods
            .OrderByDescending(p => p.SubmissionCloses ?? p.DueDate)
            .ThenByDescending(p => PeriodKeySortDesc(p.PeriodKey))
            .ToList();

        var inWeeklyScope = await _weeklyUpdateService.IsProjectInWeeklyReportingScopeAsync(projectId, cancellationToken);
        var weeklyReportingPeriods = new List<ReportingCyclePeriod>();
        var weeklyUpdateSubmittedByNames = new Dictionary<int, string>();
        if (inWeeklyScope)
        {
            var weeklyPeriodInfos = _weeklyUpdateService.EnumerateRecentPeriods(DateTime.UtcNow, 26).ToList();
            var updatesByWeek = p.WeeklyWorkUpdates.ToLookup(wu => (wu.IsoYear, wu.IsoWeek));
            foreach (var wp in weeklyPeriodInfos)
            {
                var muForPeriod = updatesByWeek[(wp.IsoYear, wp.IsoWeek)].FirstOrDefault();
                var rollup = _weeklyUpdateService.CalculateUpdateStatus(wp.IsoYear, wp.IsoWeek, muForPeriod?.SubmittedAt);
                weeklyReportingPeriods.Add(new ReportingCyclePeriod
                {
                    PeriodKey = wp.PeriodKey,
                    PeriodLabel = wp.PeriodLabel,
                    DueDate = wp.DueDate,
                    SubmissionOpens = wp.SubmissionOpens,
                    SubmissionCloses = wp.SubmissionCloses,
                    UpdateStatus = rollup.ToString(),
                    WindowAllowsEditing = _weeklyUpdateService.IsWeeklyReportEditingAllowed(wp.IsoYear, wp.IsoWeek)
                });
            }

            weeklyReportingPeriods = weeklyReportingPeriods
                .OrderByDescending(p => p.SubmissionCloses ?? p.DueDate)
                .ToList();

            var weeklySubmittedUserIds = p.WeeklyWorkUpdates
                .Where(w => w.SubmittedAt.HasValue && w.CreatedByUserId.HasValue)
                .Select(w => w.CreatedByUserId!.Value)
                .Distinct()
                .ToList();
            if (weeklySubmittedUserIds.Count > 0)
            {
                var weeklyUsers = await _db.Users.AsNoTracking()
                    .Where(u => weeklySubmittedUserIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.Name, u.Email })
                    .ToListAsync(cancellationToken);
                foreach (var u in weeklyUsers)
                    weeklyUpdateSubmittedByNames[u.Id] = u.Name ?? u.Email ?? "—";
            }

            EnrichWeeklyUpdateRagDisplay(work, p, ragLookupById);
        }

        var periodDraft = p.MonthlyUpdates.FirstOrDefault(mu =>
            mu.Year == reportY && mu.Month == reportM && mu.SubmittedAt == null);

        var section = (tab ?? "overview").Trim().ToLowerInvariant() switch
        {
            "updates" => "updates",
            "weeklyupdates" => "weeklyupdates",
            "milestones" => "milestones",
            "risks" => "risks",
            "issues" => "issues",
            "contacts" => "contacts",
            "serviceregister" => "serviceregister",
            "service-register" => "serviceregister",
            "governance" => "strategicalignment", // legacy tab param
            "strategicalignment" => "strategicalignment",
            "dependencies" => "dependencies",
            "assumptions" => "assumptions",
            "history" => "history",
            "links" => "dependencies",
            _ => "overview"
        };

        var milestoneTab = (milestonestab ?? "inprogress").Trim().ToLowerInvariant() switch
        {
            "complete" => "complete",
            _ => "inprogress"
        };

        var budgetOwnerNames = p.BudgetOwners?
            .Where(bo => bo.BusinessAreaLookup != null)
            .Select(bo => bo.BusinessAreaLookup!.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        var budgetOwnerName = budgetOwnerNames.Count > 0 ? string.Join(", ", budgetOwnerNames) : "—";

        controller.ViewBag.MainNavSection = "work";
        controller.ViewBag.SubNavItem = "work-allwork";
        controller.ViewBag.PortfolioName = ResolveProjectBusinessAreaDisplayName(p);
        controller.ViewBag.PriorityName = p.DeliveryPriority?.Name ?? "—";
        controller.ViewBag.DeliveryPhaseName = p.PhaseLookup?.Name ?? "—";
        controller.ViewBag.ActivityTypeName = p.ActivityTypeLookup?.Name ?? "—";
        controller.ViewBag.RiskAppetiteName = p.RiskAppetiteLookup?.Name ?? "—";
        controller.ViewBag.RiskAppetiteOptions = riskAppetiteOpts;
        controller.ViewBag.PrimaryContactName = p.PrimaryContactUser?.Name ?? p.PrimaryContactUser?.Email ?? "—";
        controller.ViewBag.BudgetOwnerName = budgetOwnerName;
        controller.ViewBag.BudgetOwnerNames = budgetOwnerNames;
        controller.ViewBag.BusinessCaseApproval = string.IsNullOrWhiteSpace(p.BusinessCaseApproval) ? null : p.BusinessCaseApproval.Trim();
        DemandRequest? linkedDemandVm = null;
        if (p.PipelineDemandRequestId.HasValue)
        {
            var pipeDr = await _db.DemandPipelineRequests.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == p.PipelineDemandRequestId.Value, cancellationToken);
            if (pipeDr != null)
            {
                linkedDemandVm = new DemandRequest
                {
                    Id = pipeDr.Id,
                    Reference = pipeDr.Reference,
                    Title = pipeDr.Title
                };
            }
        }

        controller.ViewBag.LinkedDemand = linkedDemandVm;
        controller.ViewBag.LinkedBusinessCase = null;
        controller.ViewBag.LatestRag = latestRag;
        controller.ViewBag.IsWatching = isWatching;
        controller.ViewBag.MonthlyReportDue = monthlyReportDue;
        controller.ViewBag.CurrentPeriodLabel = currentPeriodLabel;
        controller.ViewBag.CurrentPeriodDueDate = currentDueDate;
        controller.ViewBag.MonthlyReportDaysRemaining = daysRemaining;
        controller.ViewBag.LastMonthlyUpdateMonth = lastMonthLabel;
        controller.ViewBag.LastMonthlyUpdateBy = lastBy;
        controller.ViewBag.RagUpdatedByNames = new Dictionary<int, string>();
        controller.ViewBag.RagStatusesDict = ragStatusesDict;
        controller.ViewBag.CurrentRagName = currentRagName;
        controller.ViewBag.MilestoneProgressNames = milestoneProgressNames;
        controller.ViewBag.PrimaryContactUser = p.PrimaryContactUser;
        controller.ViewBag.BudgetOwnerUser = (User?)null;
        var keyPeople = new List<(string Key, string Label)>
        {
            ("ContactRoleType:1", "Senior Responsible Officer(s)"),
            ("ContactRoleType:2", "Service Owner(s)"),
            ("PrimaryContact", "Primary Contact"),
            ("ContactRoleType:3", "PMO Contacts"),
            ("Directorates", "Directorate(s)"),
            ("BudgetOwner", "Budget Owner(s)")
        };
        var customKeys = new List<(string Key, string Label)>();
        foreach (var pc in p.ProjectContacts
                     .Where(pc => string.Equals(pc.TeamStatus, ProjectGovernanceContacts.GovernanceTeamStatus, StringComparison.OrdinalIgnoreCase)))
        {
            var k = "CustomRole:" + Uri.EscapeDataString(pc.Role);
            if (customKeys.All(x => x.Key != k))
                customKeys.Add((k, pc.Role));
        }
        customKeys.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        keyPeople.AddRange(customKeys);
        controller.ViewBag.KeyPeoplePanelItems = keyPeople;
        controller.ViewBag.ContactRoleTypes = new List<ContactRoleType>
        {
            new() { Id = 1, Name = "SRO" },
            new() { Id = 2, Name = "Service Owner" },
            new() { Id = 3, Name = "PMO Contact" },
            new() { Id = 4, Name = "Reporting contact" },
            new() { Id = 5, Name = "Other (custom role)" }
        };
        controller.ViewBag.CanEditWorkItem = canEdit;
        controller.ViewBag.CanChangeStatus = canEdit;
        controller.ViewBag.WorkStatusDisplayName = work.Status;
        controller.ViewBag.TeamMemberFundingOptions = new List<LookupOption>();
        controller.ViewBag.TeamMemberTypeOptions = new List<LookupOption>();
        controller.ViewBag.ReportingPeriodsList = reportingPeriods;
        controller.ViewBag.WeeklyReportingPeriodsList = weeklyReportingPeriods;
        controller.ViewBag.ShowWeeklyReporting = inWeeklyScope;
        controller.ViewBag.WeeklyUpdateSubmittedByNames = weeklyUpdateSubmittedByNames;
        controller.ViewBag.ReportingPeriodDueByKey = periodDueByKey;
        controller.ViewBag.MonthlyUpdateSubmittedByNames = monthlyUpdateSubmittedByNames;
        controller.ViewBag.RagBgByStatusId = ragBgByStatusId;
        controller.ViewBag.RagTextByStatusId = ragTextByStatusId;

        controller.ViewBag.FallbackRagByUpdateId = new Dictionary<int, int>();
        controller.ViewBag.RagCssClassByStatusId = ragLookupById.ToDictionary(kv => kv.Key, kv => kv.Value.CssClass);
        controller.ViewBag.WorkChromeSection = section;
        controller.ViewBag.WorkChromeTabsAsLinks = true;
        controller.ViewBag.MilestoneCount = work.Milestones.Count(m => !m.IsDeleted);
        controller.ViewBag.MilestoneTab = milestoneTab;
        controller.ViewBag.CurrentPeriodMonthLabel = currentPeriodLabel;
        controller.ViewBag.CurrentPeriodDraftUpdateId = periodDraft?.Id;
        controller.ViewBag.CurrentPeriodKey = reportY + "-" + reportM;
        controller.ViewBag.CurrentRagBackgroundColourKey = null;
        controller.ViewBag.CurrentRagTextColourKey = null;
        controller.ViewBag.CurrentRagCssClass = currentRag.CssClass;
        controller.ViewBag.WorkIdShort = work.Id.ToString("D8", CultureInfo.InvariantCulture);

        var riskCount = work.RiskOrIssues.Count(r =>
            string.Equals(r.Type, "Risk", StringComparison.OrdinalIgnoreCase));
        var issueCount = work.RiskOrIssues.Count(r =>
            string.Equals(r.Type, "Issue", StringComparison.OrdinalIgnoreCase));
        var strategicAlignmentCount =
            work.PriorityOutcomes.Count +
            work.MissionPillars.Count +
            work.Tags.Count +
            work.GovernmentDepartments.Count;
        var contactsCount =
            work.Contacts.Count +
            (p.PrimaryContactUserId.HasValue ? 1 : 0) +
            budgetOwnerNames.Count +
            work.Directorates.Count;

        controller.ViewBag.WorkSideNavMilestoneCount = work.Milestones.Count(m => !m.IsDeleted);
        controller.ViewBag.WorkSideNavMonthlyUpdatesCount =
            (work.Status == "Active" || work.Status == "Paused")
                ? reportingPeriods.Count(p =>
                    string.Equals(p.UpdateStatus, nameof(UpdateSubmissionStatus.Due), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.UpdateStatus, nameof(UpdateSubmissionStatus.Late), StringComparison.OrdinalIgnoreCase))
                : 0;
        controller.ViewBag.WorkSideNavWeeklyUpdatesCount =
            inWeeklyScope && (work.Status == "Active" || work.Status == "Paused")
                ? weeklyReportingPeriods.Count(p =>
                    string.Equals(p.UpdateStatus, nameof(UpdateSubmissionStatus.Due), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.UpdateStatus, nameof(UpdateSubmissionStatus.Late), StringComparison.OrdinalIgnoreCase))
                : 0;
        controller.ViewBag.WorkSideNavRisksCount = riskCount;
        controller.ViewBag.WorkSideNavIssuesCount = issueCount;
        controller.ViewBag.WorkSideNavAssumptionsCount = work.Assumptions.Count;
        controller.ViewBag.WorkSideNavDependenciesCount = work.Dependencies.Count;
        controller.ViewBag.WorkSideNavContactsCount = contactsCount;
        controller.ViewBag.WorkSideNavStrategicAlignmentCount = strategicAlignmentCount;

        var trackingRegisters = await LoadRaidRegistersTrackingWorkItemAsync(
            projectId,
            controller,
            cancellationToken);
        controller.ViewBag.WorkRaidTrackingRegisters = trackingRegisters;

        return work;
    }

    private async Task<List<WorkRaidRegisterTrackingVm>> LoadRaidRegistersTrackingWorkItemAsync(
        int projectId,
        Controller controller,
        CancellationToken cancellationToken)
    {
        var registerIds = await _db.RaidRegisterWorkItems.AsNoTracking()
            .Where(w => w.ProjectId == projectId && !w.RaidRegister.IsDeleted)
            .Select(w => w.RaidRegisterId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (registerIds.Count == 0)
            return new List<WorkRaidRegisterTrackingVm>();

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
            DetailUrl = controller.Url.Action("RegisterDetail", "ModernRaid", new { id = r.Id }) ?? "#"
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
}
