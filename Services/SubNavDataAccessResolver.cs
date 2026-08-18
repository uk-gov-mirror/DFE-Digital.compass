using Compass.ViewModels.Modern;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Compass.Services;

/// <summary>Builds sub-navigation export downloads and API guidance for the current modern page.</summary>
public sealed class SubNavDataAccessResolver
{
    private static readonly HashSet<string> NonExportActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Create", "Edit", "New", "Delete", "Add", "Update", "Remove", "Set", "Change", "Log",
        "Close", "Submit", "Review", "Conduct"
    };

    public SubNavDataAccessOptions? Resolve(Controller controller, HttpContext httpContext)
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

        if (actionName.Contains("Export", StringComparison.OrdinalIgnoreCase)
            || actionName.Contains("DataJson", StringComparison.OrdinalIgnoreCase))
            return null;

        if (IsItemDetailAction(controllerName, actionName))
            return ResolveItemDetail(controller, controllerName, actionName, httpContext);

        if (NonExportActions.Contains(actionName))
            return null;

        var export = ResolveExport(controller, controllerName, actionName, httpContext);
        var api = ResolveApi(controller, controllerName, actionName, httpContext);

        if (export == null && api == null)
            return null;

        return new SubNavDataAccessOptions { Export = export, Api = api };
    }

    private static bool IsItemDetailAction(string controllerName, string actionName) =>
        (controllerName, actionName) switch
        {
            ("ModernWork", "Detail") => true,
            ("ModernRaid", "RiskDetail" or "IssueDetail" or "AssumptionDetail" or "DependencyDetail" or "NearMissDetail") => true,
            ("ModernManage", "FipsProduct") => true,
            ("ModernOperations", "ServiceRegisterProduct") => true,
            _ => false
        };

    private static SubNavExportPanel? ResolveExport(
        Controller c,
        string controllerName,
        string actionName,
        HttpContext ctx) =>
        controllerName switch
        {
            "ModernWork" => ResolveWorkExport(c, actionName, ctx),
            "ModernOperations" => ResolveOperationsExport(c, actionName, ctx),
            "ModernRaid" => ResolveRaidExport(c, actionName, ctx),
            "ModernDemand" => ResolveDemandExport(c, actionName),
            "ModernStandards" => ResolveStandardsExport(c, actionName, ctx),
            "ModernPerformance" => ResolvePerformanceExport(c, actionName, ctx),
            "ModernReporting" => ResolveReportingExport(c, actionName, ctx),
            "ModernManage" => ResolveManageExport(c, actionName, ctx),
            _ => null
        };

    private static SubNavApiPanel? ResolveApi(
        Controller c,
        string controllerName,
        string actionName,
        HttpContext ctx)
    {
        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        var docs = c.Url.Action("Api", "Docs");
        var explorer = c.Url.Action("ApiExplorer", "Docs");
        var tokens = c.Url.Action("Index", "DocsDeveloperApi");

        return controllerName switch
        {
            "ModernWork" => ApiPanel(
                baseUrl,
                docs,
                explorer,
                tokens,
                "Work and delivery",
                "Work items and RAID records linked to delivery are available over the API.",
                new[]
                {
                    Ep("Work items", "GET", "/api/v1/WorkItems", "WorkItems:read", "Filter by status, business area, phase, RAG, FIPS visibility or search"),
                    Ep("Work item", "GET", "/api/v1/WorkItems/{id}", "WorkItems:read"),
                    Ep("Milestones", "GET", "/api/v1/Milestones", "Milestones:read"),
                    Ep("Risks", "GET", "/api/v1/Risks", "Risks:read", "Filter by status, score, or product"),
                    Ep("Issues", "GET", "/api/v1/Issues", "Issues:read")
                }),
            "ModernRaid" => ApiPanel(
                baseUrl,
                docs,
                explorer,
                tokens,
                "RAID",
                "Risks and issues are available over the API. Assumptions, dependencies and near misses are not exposed as record endpoints yet (lookup catalogues only).",
                new[]
                {
                    Ep("Risks", "GET", "/api/v1/Risks", "Risks:read"),
                    Ep("Issues", "GET", "/api/v1/Issues", "Issues:read"),
                    Ep("Milestones", "GET", "/api/v1/Milestones", "Milestones:read")
                }),
            "ModernManage" when actionName is "Fips" or "FipsDashboard" => ApiPanel(
                baseUrl,
                docs,
                explorer,
                tokens,
                "Service register",
                "Read products and lookup catalogues. Use query parameters to match Active, Enterprise, or other tabs.",
                new[]
                {
                    Ep("Products", "GET", "/api/v1/ServiceRegister/products", "ServiceRegister:read",
                        "Use excludeEnterprise=true or enterpriseOnly=true to match UI tabs"),
                    Ep("Product", "GET", "/api/v1/ServiceRegister/products/{id}", "ServiceRegister:read"),
                    Ep("Enterprise active", "GET", "/api/v1/ServiceRegister/products/enterprise-active", "ServiceRegister:read")
                }),
            "ModernOperations" when string.Equals(actionName, "ServiceRegister", StringComparison.OrdinalIgnoreCase) =>
                ApiPanel(
                    baseUrl,
                    docs,
                    explorer,
                    tokens,
                    "Service register",
                    "Same Service register API as Manage. Filter with status and enterprise flags to match the tab you are viewing.",
                    new[]
                    {
                        Ep("Products", "GET", "/api/v1/ServiceRegister/products", "ServiceRegister:read",
                            "status=Active&excludeEnterprise=true for the Active tab"),
                        Ep("Enterprise active", "GET", "/api/v1/ServiceRegister/products/enterprise-active", "ServiceRegister:read")
                    }),
            "ModernDemand" => ApiPanel(
                baseUrl,
                docs,
                explorer,
                tokens,
                "Demand",
                "There is no Demand pipeline API yet. Use Excel/CSV exports for programmatic access to demand data.",
                Array.Empty<SubNavApiEndpoint>()),
            "ModernPerformance" => ApiPanel(
                baseUrl,
                docs,
                explorer,
                tokens,
                "Performance reporting",
                "Commission metrics and submissions are available over the API.",
                new[]
                {
                    Ep("Performance metrics", "GET", "/api/v1/PerformanceMetrics", "PerformanceMetrics:read"),
                    Ep("Submit metrics", "POST", "/api/v1/PerformanceMetrics/submit", "PerformanceMetrics:write"),
                    Ep("Enterprise metrics", "GET", "/api/v1/EnterpriseMetrics", "EnterpriseMetrics:read")
                }),
            "ModernStandards" => ApiPanel(
                baseUrl,
                docs,
                explorer,
                tokens,
                "Standards",
                "DDT and functional standards reference data.",
                new[]
                {
                    Ep("DDT standards", "GET", "/api/v1/DdtStandards", "DdtStandards:read"),
                    Ep("Functional standards", "GET", "/api/v1/functionalstandards", "FunctionalStandards:read")
                }),
            "ModernReporting" => ApiPanel(
                baseUrl,
                docs,
                explorer,
                tokens,
                "Reporting",
                "Use the endpoints for the underlying datasets (work items, RAID, service register, performance) rather than report views.",
                new[]
                {
                    Ep("Work items", "GET", "/api/v1/WorkItems", "WorkItems:read"),
                    Ep("Milestones", "GET", "/api/v1/Milestones", "Milestones:read"),
                    Ep("Risks", "GET", "/api/v1/Risks", "Risks:read"),
                    Ep("Service register", "GET", "/api/v1/ServiceRegister/products", "ServiceRegister:read")
                }),
            _ => null
        };
    }

    private static SubNavExportPanel? ResolveWorkExport(Controller c, string action, HttpContext ctx)
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

        var links = new List<SubNavExportLink>
        {
            Link(
                "All work data (Excel)",
                allData,
                "Work items, milestones, and monthly update history in one workbook.")
        };

        if (scope != null)
        {
            var workKeys = new[]
            {
                "tab", "search", "businessAreaId", "directorateId", "phaseId", "ragId", "priorityId",
                "monthlyUpdate", "primaryContactUserId", "tagId", "tagIds", "mine", "sort", "sd",
                "businessAreaKey", "directorateKey", "themeKey", "workItems", "portfolioId"
            };
            var current = ActionWithQuery(c, "ExportRegister", "ModernWork", workKeys, ctx, new { scope });
            if (current != null)
            {
                links.Insert(0, Link(
                    "Current view (Excel)",
                    current,
                    "Matches filters on this page, including RAID and accessibility sheets where applicable."));
            }
        }
        else if (action is "Dashboard" or "ByPriority" or "Portfolios")
        {
            links.Insert(0, Link(
                "Active work (Excel)",
                ActionWithQuery(c, "ExportRegister", "ModernWork", Array.Empty<string>(), ctx, new { scope = "allwork" }) ?? allData,
                "All active work items."));
        }

        return Panel("Work exports", links);
    }

    private static SubNavExportPanel? ResolveOperationsExport(Controller c, string action, HttpContext ctx)
    {
        if (string.Equals(action, "ServiceRegister", StringComparison.OrdinalIgnoreCase))
            return ResolveFipsProductsExport(c, action, ctx, "Service register exports");

        if (!string.Equals(action, "ManageWork", StringComparison.OrdinalIgnoreCase))
            return null;

        var allData = c.Url.Action("DownloadWorkExcel", "Exports");
        var workKeys = new[]
        {
            "tab", "search", "businessAreaId", "directorateId", "phaseId", "ragId", "priorityId",
            "monthlyUpdate", "primaryContactUserId", "tagId", "tagIds", "sort", "sd"
        };
        var links = new List<SubNavExportLink>();
        var current = ActionWithQuery(c, "ExportRegister", "ModernWork", workKeys, ctx, new { scope = "manage-work" });
        if (current != null)
            links.Add(Link("Current view (Excel)", current, "Filtered work register."));
        if (allData != null)
            links.Add(Link("All work data (Excel)", allData, "Full work export workbook."));
        return links.Count == 0 ? null : Panel("Work exports", links);
    }

    private static SubNavExportPanel? ResolveRaidExport(Controller c, string action, HttpContext ctx)
    {
        var allExcel = c.Url.Action("ExportRaidRegisterExcel", "ModernRaid");
        if (allExcel == null)
            return null;

        var links = new List<SubNavExportLink>
        {
            Link("All RAID data (Excel)", allExcel, "Risks, issues, assumptions, dependencies, and near misses."),
            Link("All risks (CSV)", c.Url.Action("ExportRisksCsv", "ModernRaid") ?? "#", "Open risks register.", useModal: false),
            Link("All issues (CSV)", c.Url.Action("ExportIssuesCsv", "ModernRaid") ?? "#", "Open issues register.", useModal: false)
        };

        if (string.Equals(action, "Tier", StringComparison.OrdinalIgnoreCase))
        {
            var tierKeys = new[] { "search", "projectId", "directorateId", "businessAreaId" };
            var current = ActionWithQuery(c, "TierExportExcel", "ModernRaid", tierKeys, ctx);
            if (current != null)
                links.Insert(0, Link("Current tier view (Excel)", current, "Filtered tier register."));
        }
        else if (string.Equals(action, "Dashboard", StringComparison.OrdinalIgnoreCase))
        {
            var current = c.Url.Action("DashboardExportExcel", "ModernRaid");
            if (current != null)
                links.Insert(0, Link("Dashboard summary (Excel)", current));
        }

        return Panel("RAID exports", links);
    }

    private static SubNavExportPanel? ResolveDemandExport(Controller c, string action)
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

        var links = new List<SubNavExportLink>
        {
            Link("All demand data (Excel)", allData, "Business cases, requests, triage, and scoring.")
        };
        if (registerCsv != null)
            links.Insert(0, Link("Demand register (CSV)", registerCsv, "Flat pipeline list.", useModal: false));

        return Panel("Demand exports", links);
    }

    private static SubNavExportPanel? ResolveStandardsExport(Controller c, string action, HttpContext ctx)
    {
        if (!string.Equals(action, "DdtStandards", StringComparison.OrdinalIgnoreCase))
            return null;

        var keys = new[] { "tab", "search", "category", "owner" };
        var current = ActionWithQuery(c, "DdtStandards", "ModernStandards", keys, ctx, new { export = "csv" });
        var all = ActionWithQuery(c, "DdtStandards", "ModernStandards", Array.Empty<string>(), ctx, new { export = "csv", tab = "published" });
        var links = new List<SubNavExportLink>();
        if (current != null)
            links.Add(Link("Current view (CSV)", current, useModal: false));
        if (all != null)
            links.Add(Link("Published standards (CSV)", all, useModal: false));
        return links.Count == 0 ? null : Panel("Standards exports", links);
    }

    private static SubNavExportPanel? ResolvePerformanceExport(Controller c, string action, HttpContext ctx)
    {
        if (!action.Contains("Commission", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(action, "Index", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(action, "Guidance", StringComparison.OrdinalIgnoreCase))
            return null;

        var allData = c.Url.Action("DownloadPerformanceExcel", "Exports");
        if (allData == null)
            return null;

        var links = new List<SubNavExportLink>
        {
            Link("All performance data (Excel)", allData, "Commissions, submissions, and metric values.")
        };

        if (c.RouteData.Values.TryGetValue("commissionId", out var cidObj)
            && int.TryParse(cidObj?.ToString(), out var commissionId)
            && commissionId > 0)
        {
            var keys = new[] { "tab", "search", "phase", "complete" };
            var current = ActionWithQuery(c, "ExportExcel", "ModernPerformance", keys, ctx, new { commissionId });
            if (current != null)
                links.Insert(0, Link("Current commission (Excel)", current));
        }

        return Panel("Performance exports", links);
    }

    private static SubNavExportPanel? ResolveReportingExport(Controller c, string action, HttpContext ctx)
    {
        var workAll = c.Url.Action("DownloadWorkExcel", "Exports");
        var raidAll = c.Url.Action("ExportRaidRegisterExcel", "ModernRaid");

        if (string.Equals(action, "ThematicReport", StringComparison.OrdinalIgnoreCase))
        {
            var keys = new[] { "themeId", "tab" };
            var current = ActionWithQuery(c, "ExportThematicReport", "ModernReporting", keys, ctx);
            var all = c.Url.Action("ExportThematicReportAll", "ModernReporting");
            var links = new List<SubNavExportLink>();
            if (current != null)
                links.Add(Link("Current theme (Excel)", current));
            if (all != null)
                links.Add(Link("All themes (Excel)", all));
            return links.Count == 0 ? null : Panel("Report exports", links);
        }

        if (string.Equals(action, "Raid", StringComparison.OrdinalIgnoreCase))
        {
            var keys = new[] { "tab", "businessAreaId", "directorateId" };
            var current = ActionWithQuery(c, "ExportRaidReport", "ModernReporting", keys, ctx);
            var links = new List<SubNavExportLink>();
            if (current != null)
                links.Add(Link("Current RAID report (Excel)", current));
            if (raidAll != null)
                links.Add(Link("All RAID data (Excel)", raidAll));
            return links.Count == 0 ? null : Panel("Report exports", links);
        }

        if (string.Equals(action, "Assessments", StringComparison.OrdinalIgnoreCase))
        {
            var all = c.Url.Action("ExportAssessmentsReport", "ModernReporting");
            if (all == null)
                return null;

            return Panel(
                "Service assessment exports",
                new List<SubNavExportLink>
                {
                    Link(
                        "All assessment data (Excel)",
                        all,
                        "Published assessments, all actions, and actions by service standard.")
                });
        }

        if (string.Equals(action, "RisksByTier", StringComparison.OrdinalIgnoreCase))
        {
            var risksAll = c.Url.Action("ExportRisksExcel", "ModernRaid");
            var links = new List<SubNavExportLink>();
            if (risksAll != null)
                links.Add(Link("All risk data (Excel)", risksAll, "Every risk in the register."));
            return links.Count == 0 ? null : Panel("Report exports", links);
        }

        if (action is "Dashboard" or "MonthlyUpdate" or "MonthlySubmissionProgress")
        {
            var links = new List<SubNavExportLink>();
            if (workAll != null)
                links.Add(Link("All work data (Excel)", workAll));
            return links.Count == 0 ? null : Panel("Report exports", links);
        }

        return null;
    }

    private static SubNavExportPanel? ResolveManageExport(Controller c, string action, HttpContext ctx)
    {
        if (!string.Equals(action, "Fips", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(action, "FipsDashboard", StringComparison.OrdinalIgnoreCase))
            return null;

        object? routeValues = string.Equals(action, "FipsDashboard", StringComparison.OrdinalIgnoreCase)
            ? new { tab = "active" }
            : null;
        return ResolveFipsProductsExport(c, action, ctx, "Service register exports", routeValues);
    }

    private static SubNavExportPanel? ResolveFipsProductsExport(
        Controller c,
        string action,
        HttpContext ctx,
        string panelTitle,
        object? routeValues = null)
    {
        var allData = c.Url.Action("ExportFipsProducts", "ModernManage", new { allData = true });
        var filterKeys = new[]
        {
            "tab", "search", "businessAreaId", "channelId", "userGroupId", "typeId", "phaseId",
            "categorisationItemId", "categorisationGroupId"
        };
        var current = ActionWithQuery(c, "ExportFipsProducts", "ModernManage", filterKeys, ctx, routeValues);

        var links = new List<SubNavExportLink>();
        if (current != null)
            links.Add(Link("Current view (Excel)", current, "Matches filters on this tab."));
        if (allData != null)
            links.Add(Link("All products (Excel)", allData, "Every product in the register."));
        return links.Count == 0 ? null : Panel(panelTitle, links);
    }

    private static SubNavExportPanel Panel(string title, List<SubNavExportLink> links) =>
        new() { Title = title, Links = links };

    private static SubNavExportLink Link(
        string label,
        string href,
        string? description = null,
        bool useModal = true,
        string format = "excel") =>
        new()
        {
            Label = label,
            Href = href,
            Description = description,
            Format = format,
            UseDownloadModal = useModal,
            DownloadModalTitle = useModal ? "Generating export" : null
        };

    private static SubNavApiPanel? ApiPanel(
        string baseUrl,
        string? docsUrl,
        string? explorerUrl,
        string? tokensUrl,
        string title,
        string intro,
        IReadOnlyList<SubNavApiEndpoint> endpoints)
    {
        if (endpoints.Count == 0 && string.IsNullOrWhiteSpace(intro))
            return null;

        return new SubNavApiPanel
        {
            Title = $"API — {title}",
            Intro = intro,
            BaseUrl = baseUrl.TrimEnd('/'),
            DocsUrl = docsUrl,
            ApiExplorerUrl = explorerUrl,
            ApiTokensUrl = tokensUrl,
            Endpoints = endpoints.ToList()
        };
    }

    private static SubNavApiEndpoint Ep(
        string label,
        string method,
        string path,
        string? scope = null,
        string? note = null) =>
        new()
        {
            Label = label,
            Method = method,
            Path = path,
            Scope = scope,
            Note = note
        };

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

        return query.Count == 0 ? baseUrl : QueryHelpers.AddQueryString(baseUrl, query);
    }

    private static SubNavDataAccessOptions? ResolveItemDetail(
        Controller c,
        string controllerName,
        string actionName,
        HttpContext ctx)
    {
        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        var docs = c.Url.Action("Api", "Docs");
        var explorer = c.Url.Action("ApiExplorer", "Docs");
        var tokens = c.Url.Action("Index", "DocsDeveloperApi");

        return (controllerName, actionName) switch
        {
            ("ModernWork", "Detail") when TryGetIntRouteId(c, out var workId) => ItemOptions(
                ItemExport(c, "work", workId, includeRegisterExcel: true),
                ItemApi(
                    baseUrl, docs, explorer, tokens,
                    "Get this work item as data",
                    "Pull this record from the COMPASS API, or download a snapshot.",
                    itemEndpoint: ApiItem(baseUrl, "GET", $"/api/v1/WorkItems/{workId}", "WorkItems:read")),
                isItem: true),

            ("ModernRaid", "RiskDetail") when TryGetIntRouteId(c, out var riskId) => ItemOptions(
                ItemExport(c, "risks", riskId),
                ItemApi(
                    baseUrl, docs, explorer, tokens,
                    "Get this risk as data",
                    "Pull this record from the COMPASS API, or download a snapshot.",
                    itemEndpoint: ApiItem(baseUrl, "GET", $"/api/v1/Risks/{riskId}", "Risks:read")),
                isItem: true),

            ("ModernRaid", "IssueDetail") when TryGetIntRouteId(c, out var issueId) => ItemOptions(
                ItemExport(c, "issues", issueId),
                ItemApi(
                    baseUrl, docs, explorer, tokens,
                    "Get this issue as data",
                    "Pull this record from the COMPASS API, or download a snapshot.",
                    itemEndpoint: ApiItem(baseUrl, "GET", $"/api/v1/Issues/{issueId}", "Issues:read")),
                isItem: true),

            ("ModernRaid", "AssumptionDetail") when TryGetIntRouteId(c, out var assumptionId) => ItemOptions(
                ItemExport(c, "assumptions", assumptionId),
                ItemApi(
                    baseUrl, docs, explorer, tokens,
                    "Get this assumption as data",
                    "Assumptions are not on the REST API yet. Download a JSON snapshot below, or export the full RAID register as Excel from the RAID section.",
                    itemEndpoint: null),
                isItem: true),

            ("ModernRaid", "DependencyDetail") when TryGetIntRouteId(c, out var dependencyId) => ItemOptions(
                ItemExport(c, "dependencies", dependencyId),
                ItemApi(
                    baseUrl, docs, explorer, tokens,
                    "Get this dependency as data",
                    "Dependencies are not on the REST API yet. Download a JSON snapshot below, or export the full RAID register as Excel from the RAID section.",
                    itemEndpoint: null),
                isItem: true),

            ("ModernRaid", "NearMissDetail") when TryGetIntRouteId(c, out var nearMissId) => ItemOptions(
                ItemExport(c, "near-misses", nearMissId),
                ItemApi(
                    baseUrl, docs, explorer, tokens,
                    "Get this near miss as data",
                    "Near misses are not on the REST API yet. Download a JSON snapshot below, or export the full RAID register as Excel from the RAID section.",
                    itemEndpoint: null),
                isItem: true),

            ("ModernManage", "FipsProduct") when TryGetGuidRouteId(c, out var productId) => ItemOptions(
                ItemExport(c, "products", productId),
                ItemApi(
                    baseUrl, docs, explorer, tokens,
                    "Get this product as data",
                    "Pull this record from the COMPASS API, or download a snapshot.",
                    itemEndpoint: ApiItem(baseUrl, "GET", $"/api/v1/ServiceRegister/products/{productId}", "ServiceRegister:read")),
                isItem: true),

            ("ModernOperations", "ServiceRegisterProduct") when TryGetGuidRouteId(c, out var opsProductId) => ItemOptions(
                ItemExport(c, "products", opsProductId),
                ItemApi(
                    baseUrl, docs, explorer, tokens,
                    "Get this product as data",
                    "Pull this record from the COMPASS API, or download a snapshot.",
                    itemEndpoint: ApiItem(baseUrl, "GET", $"/api/v1/ServiceRegister/products/{opsProductId}", "ServiceRegister:read")),
                isItem: true),

            _ => null
        };
    }

    private static SubNavDataAccessOptions ItemOptions(
        SubNavExportPanel? export,
        SubNavApiPanel? api,
        bool isItem) =>
        new() { IsItemView = isItem, Export = export, Api = api };

    private static SubNavExportPanel? ItemExport(
        Controller c,
        string kind,
        int id,
        bool includeRegisterExcel = false)
    {
        var jsonUrl = c.Url.Action("ItemJson", "ModernItemData", new { kind, id });
        if (jsonUrl == null)
            return null;

        var links = new List<SubNavExportLink>
        {
            Link("JSON", jsonUrl, "Structured data", useModal: false, format: "json")
        };

        if (includeRegisterExcel)
        {
            var excel = c.Url.Action("DownloadWorkExcel", "Exports");
            if (excel != null)
                links.Add(Link("All work data (Excel)", excel, "Full work register workbook", format: "excel"));
        }

        return new SubNavExportPanel
        {
            Title = "Download this item",
            Intro = null,
            Links = links
        };
    }

    private static SubNavExportPanel? ItemExport(Controller c, string kind, Guid id)
    {
        var jsonUrl = c.Url.Action("ItemJson", "ModernItemData", new { kind, id });
        if (jsonUrl == null)
            return null;

        return new SubNavExportPanel
        {
            Title = "Download this item",
            Links = new List<SubNavExportLink>
            {
                Link("JSON", jsonUrl, "Structured data", useModal: false, format: "json")
            }
        };
    }

    private static SubNavApiPanel ItemApi(
        string baseUrl,
        string? docsUrl,
        string? explorerUrl,
        string? tokensUrl,
        string title,
        string intro,
        SubNavApiItemEndpoint? itemEndpoint) =>
        new()
        {
            Title = title,
            Intro = intro,
            BaseUrl = baseUrl.TrimEnd('/'),
            DocsUrl = docsUrl,
            ApiExplorerUrl = explorerUrl,
            ApiTokensUrl = tokensUrl,
            ItemEndpoint = itemEndpoint,
            Endpoints = new List<SubNavApiEndpoint>()
        };

    private static SubNavApiItemEndpoint ApiItem(string baseUrl, string method, string path, string scope) =>
        new()
        {
            Method = method,
            Url = baseUrl.TrimEnd('/') + path,
            Scope = scope
        };

    private static bool TryGetIntRouteId(Controller c, out int id)
    {
        if (c.RouteData.Values["id"] is string s && int.TryParse(s, out id))
            return true;
        id = 0;
        return false;
    }

    private static bool TryGetGuidRouteId(Controller c, out Guid id)
    {
        if (c.RouteData.Values["id"] is string s && Guid.TryParse(s, out id))
            return true;
        id = Guid.Empty;
        return false;
    }

}
