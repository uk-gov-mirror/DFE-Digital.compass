using Compass.Models;
using Microsoft.AspNetCore.Mvc;

namespace Compass.ViewModels.Dashboard;

public class YourTasksViewModel
{
    public bool MonthlyReportingWindowOpen { get; set; }
    public int MonthlyReportingRemainingCount { get; set; }
    public string ReportingPeriodLabel { get; set; } = "";

    public int PerformanceReturnsDueCount { get; set; }

    public string MonthlyReportingHref { get; set; } = "";
    public string PerformanceCommissionsHref { get; set; } = "";
    public string AllWorkHref { get; set; } = "";

    public IReadOnlyList<YourTaskRow> VisibleTasks { get; set; } = Array.Empty<YourTaskRow>();
    public IReadOnlyList<YourTaskRow> HiddenTasks { get; set; } = Array.Empty<YourTaskRow>();

    public bool ShowMonthlyReportingTask { get; set; }
    public bool ShowPerformanceReturnsTask { get; set; }
    public bool HasAnyTasks { get; set; }

    /// <summary>Prefix for element ids (e.g. dashboard-task vs work-dashboard-task).</summary>
    public string IdPrefix { get; set; } = "dashboard-task";
}

public class YourTaskRow
{
    public string Title { get; init; } = "";
    public string Hint { get; init; } = "";
    public string Href { get; init; } = "";
    public string StatusTag { get; init; } = "";
    public int? CompletionPercent { get; init; }
}

public class YourTasksLinkOptions
{
    public string MonthlyReportingHref { get; init; } = "";
    public string PerformanceCommissionsHref { get; init; } = "";
    public string AllWorkHref { get; init; } = "";
}

public sealed class YourTasksBuildInput
{
    public IReadOnlyList<Project> MyProjects { get; init; } = Array.Empty<Project>();
    public IReadOnlyList<Milestone> OverdueMilestones { get; init; } = Array.Empty<Milestone>();
    public IReadOnlyList<Milestone> MilestonesDueThisWeek { get; init; } = Array.Empty<Milestone>();
    public IReadOnlyList<Issue> HighPriorityIssues { get; init; } = Array.Empty<Issue>();
    public IReadOnlyList<Models.Action> AssignedActions { get; init; } = Array.Empty<Models.Action>();
    public bool ShowRaidIssues { get; init; }
    public bool MonthlyReportingWindowOpen { get; init; }
    public int MonthlyReportingRemainingCount { get; init; }
    public string ReportingPeriodLabel { get; init; } = "";
    public int PerformanceReturnsDueCount { get; init; }
    public YourTasksLinkOptions Links { get; init; } = new();
    public string IdPrefix { get; init; } = "dashboard-task";
    public IUrlHelper? Url { get; init; }
}
