using Compass.ViewModels.Modern;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Compass.Services;

/// <summary>Builds sub-navigation export URLs from the current request (filters preserved for “current view”).</summary>
public sealed class SubNavExportResolver
{
    private static readonly HashSet<string> NonExportActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Create", "Edit", "New", "Delete", "Detail", "Add", "Update", "Remove", "Set", "Change", "Log",
        "Close", "Submit", "Review", "Conduct"
    };

    public SubNavExportOptions? Resolve(Controller controller, HttpContext httpContext)
    {
        if (!string.Equals(httpContext.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            return null;

        var path = httpContext.Request.Path.Value ?? "";
        if (!path.StartsWith("/modern", StringComparison.OrdinalIgnoreCase))
            return null;

        if (httpContext.Request.Query.ContainsKey("embed"))
            return null;

        var controllerName = controller.RouteData.Values["controller"]?.ToString() ?? "";
        var actionName = controller.RouteData.Values["action"]?.ToString() ?? "";

        if (NonExportActions.Contains(actionName) || actionName.Contains("Export", StringComparison.OrdinalIgnoreCase))
            return null;

        return controllerName switch
        {
            "ModernWork" => ResolveWork(controller, actionName, httpContext),
            "ModernOperations" => ResolveOperations(controller, actionName, httpContext),
            "ModernRaid" => ResolveRaid(controller, actionName, httpContext),
            "ModernDemand" => ResolveDemand(controller, actionName),
            "ModernStandards" => ResolveStandards(controller, actionName, httpContext),
            "ModernPerformance" => ResolvePerformance(controller, actionName, httpContext),
            "ModernDesignDecisionRecords" => ResolveDdr(controller, actionName),
            "ModernReporting" => ResolveReporting(controller, actionName, httpContext),
            "ModernManage" => ResolveManage(controller, actionName, httpContext),
            "Exports" => null,
            "ModernAdmin" => null,
            _ => null
        };
    }

    private static SubNavExportOptions? ResolveWork(Controller c, string action, HttpContext ctx)
    {
        var allData = c.Url.Action("DownloadWorkExcel", "Exports");
        if (allData == null)
            return null;

        var scope = action switch
        {
            "Index" => "index",
            "AllWork" => "allwork",
            "Watching" => "watching",
            "Directorates" => "directorates",
            "BusinessAreas" => "business-areas",
            "ByTheme" => "by-theme",
            _ => null
        };

        if (scope == null)
            return action is "Dashboard" or "ByPriority" or "Portfolios"
                ? Options(null, allData)
                : null;

        var workKeys = new[]
        {
            "tab", "search", "businessAreaId", "directorateId", "phaseId", "ragId", "priorityId",
            "monthlyUpdate", "primaryContactUserId", "tagId", "tagIds", "mine", "sort", "sd",
            "businessAreaKey", "directorateKey", "themeKey", "workItems", "portfolioId"
        };
        var route = new { scope };
        var currentView = ActionWithQuery(c, "ExportRegister", "ModernWork", workKeys, ctx, route);
        return Options(currentView, allData);
    }

    private static SubNavExportOptions? ResolveOperations(Controller c, string action, HttpContext ctx)
    {
        if (!string.Equals(action, "ManageWork", StringComparison.OrdinalIgnoreCase))
            return null;

        var allData = c.Url.Action("DownloadWorkExcel", "Exports");
        var workKeys = new[]
        {
            "tab", "search", "businessAreaId", "directorateId", "phaseId", "ragId", "priorityId",
            "monthlyUpdate", "primaryContactUserId", "tagId", "tagIds", "sort", "sd"
        };
        var current = ActionWithQuery(c, "ExportRegister", "ModernWork", workKeys, ctx, new { scope = "manage-work" });
        return Options(current, allData);
    }

    private static SubNavExportOptions? ResolveRaid(Controller c, string action, HttpContext ctx)
    {
        var allData = c.Url.Action("ExportRaidRegisterExcel", "ModernRaid");
        if (allData == null)
            return null;

        if (string.Equals(action, "Tier", StringComparison.OrdinalIgnoreCase))
        {
            var tierKeys = new[] { "search", "projectId", "directorateId", "businessAreaId" };
            var current = ActionWithQuery(c, "TierExportExcel", "ModernRaid", tierKeys, ctx);
            return Options(current, allData);
        }

        if (string.Equals(action, "Dashboard", StringComparison.OrdinalIgnoreCase))
        {
            var current = c.Url.Action("DashboardExportExcel", "ModernRaid");
            return Options(current, allData);
        }

        var registerActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Risks", "Issues", "Dependencies", "Assumptions", "NearMisses", "Directorate", "BusinessAreas"
        };

        if (!registerActions.Contains(action))
            return Options(null, allData);

        var currentCsv = action switch
        {
            "Risks" or "Directorate" or "BusinessAreas" => c.Url.Action("ExportRisksCsv", "ModernRaid"),
            "Issues" => c.Url.Action("ExportIssuesCsv", "ModernRaid"),
            _ => null
        };

        return Options(currentCsv, allData);
    }

    private static SubNavExportOptions? ResolveDemand(Controller c, string action)
    {
        var allData = c.Url.Action("DownloadDemandExcel", "Exports");
        var registerCsv = c.Url.Action("DownloadDemandRegisterCsv", "Exports");
        if (allData == null)
            return null;

        var registerActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Register", "Explore", "Triage", "Scoring", "BusinessCase", "Dashboard"
        };

        if (!registerActions.Contains(action))
            return null;

        return Options(registerCsv, allData);
    }

    private static SubNavExportOptions? ResolveStandards(Controller c, string action, HttpContext ctx)
    {
        if (!string.Equals(action, "DdtStandards", StringComparison.OrdinalIgnoreCase))
            return null;

        var keys = new[] { "tab", "search", "category", "owner" };
        var route = new { export = "csv" };
        var current = ActionWithQuery(c, "DdtStandards", "ModernStandards", keys, ctx, route);
        var all = ActionWithQuery(c, "DdtStandards", "ModernStandards", Array.Empty<string>(), ctx, new { export = "csv", tab = "published" });
        return Options(current, all);
    }

    private static SubNavExportOptions? ResolvePerformance(Controller c, string action, HttpContext ctx)
    {
        if (!action.Contains("Commission", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(action, "Index", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(action, "Guidance", StringComparison.OrdinalIgnoreCase))
            return null;

        var allData = c.Url.Action("DownloadPerformanceExcel", "Exports");
        if (allData == null)
            return null;

        if (c.RouteData.Values.TryGetValue("commissionId", out var cidObj)
            && int.TryParse(cidObj?.ToString(), out var commissionId)
            && commissionId > 0)
        {
            var keys = new[] { "tab", "search", "phase", "complete" };
            var current = ActionWithQuery(c, "ExportExcel", "ModernPerformance", keys, ctx, new { commissionId });
            return Options(current, allData);
        }

        return Options(null, allData);
    }

    private static SubNavExportOptions? ResolveDdr(Controller c, string action)
    {
        if (!string.Equals(action, "Register", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(action, "Oversight", StringComparison.OrdinalIgnoreCase))
            return null;

        return null;
    }

    private static SubNavExportOptions? ResolveReporting(Controller c, string action, HttpContext ctx)
    {
        var workAll = c.Url.Action("DownloadWorkExcel", "Exports");

        if (string.Equals(action, "ThematicReport", StringComparison.OrdinalIgnoreCase))
        {
            var keys = new[] { "themeId", "tab" };
            var current = ActionWithQuery(c, "ExportThematicReport", "ModernReporting", keys, ctx);
            var all = c.Url.Action("ExportThematicReportAll", "ModernReporting");
            return Options(current, all);
        }

        if (string.Equals(action, "Raid", StringComparison.OrdinalIgnoreCase))
        {
            var keys = new[] { "tab", "businessAreaId", "directorateId" };
            var current = ActionWithQuery(c, "ExportRaidReport", "ModernReporting", keys, ctx);
            var all = c.Url.Action("ExportRaidRegisterExcel", "ModernRaid");
            return Options(current, all);
        }

        if (string.Equals(action, "MonthlySubmissionProgress", StringComparison.OrdinalIgnoreCase))
        {
            var keys = new[] { "year", "month", "businessAreaId", "directorateId" };
            var current = ActionWithQuery(c, "ExportMonthlySubmissionProgress", "ModernReporting", keys, ctx);
            return Options(current, workAll);
        }

        if (string.Equals(action, "RisksByTier", StringComparison.OrdinalIgnoreCase))
        {
            var risksAll = c.Url.Action("ExportRisksExcel", "ModernRaid");
            return Options(null, risksAll);
        }

        return action switch
        {
            "Dashboard" or "MonthlyUpdate" or "Performance" or "Assessments" or "Accessibility" or "RaidReviewProgress"
                => Options(null, workAll),
            _ => null
        };
    }

    private static SubNavExportOptions? ResolveManage(Controller c, string action, HttpContext ctx)
    {
        if (!string.Equals(action, "Fips", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(action, "FipsDashboard", StringComparison.OrdinalIgnoreCase))
            return null;

        var allData = c.Url.Action("ExportFipsProducts", "ModernManage", new { allData = true });
        var filterKeys = new[]
        {
            "tab", "search", "businessAreaId", "channelId", "userGroupId", "typeId", "phaseId",
            "categorisationItemId", "categorisationGroupId"
        };

        object? routeValues = string.Equals(action, "FipsDashboard", StringComparison.OrdinalIgnoreCase)
            ? new { tab = "active" }
            : null;

        var current = ActionWithQuery(c, "ExportFipsProducts", "ModernManage", filterKeys, ctx, routeValues);
        return Options(current, allData);
    }

    private static string? ActionWithQuery(
        Controller c,
        string action,
        string controller,
        IReadOnlyList<string> queryKeys,
        HttpContext ctx,
        object? routeValues = null)
    {
        var baseUrl = c.Url.Action(action, controller, routeValues);
        if (string.IsNullOrEmpty(baseUrl))
            return null;

        var query = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in queryKeys)
        {
            if (ctx.Request.Query.TryGetValue(key, out var values) && values.Count > 0)
            {
                var v = values.ToString();
                if (!string.IsNullOrWhiteSpace(v))
                    query[key] = v;
            }
        }

        if (query.Count == 0)
            return baseUrl;

        return QueryHelpers.AddQueryString(baseUrl, query);
    }

    private static SubNavExportOptions Options(string? currentView, string? allData)
    {
        if (string.IsNullOrEmpty(currentView) && string.IsNullOrEmpty(allData))
            return new SubNavExportOptions { Show = false };

        return new SubNavExportOptions
        {
            Show = true,
            CurrentViewUrl = currentView,
            AllDataUrl = allData
        };
    }
}
