using System;
using System.Collections.Generic;
using Compass.Models;
using Compass.Services;

namespace Compass.ViewModels.Dashboard;

public class HomeDashboardViewModel
{
    public User CurrentUser { get; set; } = null!;
    public string FirstName { get; set; } = "User";
    public DashboardSectionConfig SectionConfig { get; set; } = new();
    public DashboardMetrics Metrics { get; set; } = new();
    public DashboardMetrics LeadershipMetrics { get; set; } = new();

    public IReadOnlyCollection<DashboardTaskItem> PriorityTasks { get; set; } = Array.Empty<DashboardTaskItem>();
    public IReadOnlyCollection<DashboardReminder> Reminders { get; set; } = Array.Empty<DashboardReminder>();
    public IReadOnlyCollection<DashboardQuickLink> QuickLinks { get; set; } = Array.Empty<DashboardQuickLink>();
    public IReadOnlyCollection<DashboardQuickLinkOption> QuickLinkOptions { get; set; } = Array.Empty<DashboardQuickLinkOption>();
    public IReadOnlyCollection<DashboardBlockDefinition> BlockDefinitions { get; set; } = Array.Empty<DashboardBlockDefinition>();
    public IReadOnlyCollection<DashboardBlockInstance> BlockInstances { get; set; } = Array.Empty<DashboardBlockInstance>();

    public IReadOnlyCollection<Project> MyProjects { get; set; } = Array.Empty<Project>();
    public IReadOnlyCollection<Project> WatchedProjects { get; set; } = Array.Empty<Project>();
    public IReadOnlyCollection<ProductDto> MyProducts { get; set; } = Array.Empty<ProductDto>();
    public IReadOnlyCollection<Milestone> MilestonesDueThisWeek { get; set; } = Array.Empty<Milestone>();
    public IReadOnlyCollection<Milestone> OverdueMilestones { get; set; } = Array.Empty<Milestone>();
    public IReadOnlyCollection<Issue> HighPriorityIssues { get; set; } = Array.Empty<Issue>();
    public IReadOnlyCollection<Risk> UnmonitoredRisks { get; set; } = Array.Empty<Risk>();
    public IReadOnlyCollection<Models.Action> AssignedActions { get; set; } = Array.Empty<Models.Action>();
    public IReadOnlyCollection<Project> AtRiskProjects { get; set; } = Array.Empty<Project>();
    public IReadOnlyCollection<Project> ProjectsNeedingPathToGreen { get; set; } = Array.Empty<Project>();
    public IReadOnlyCollection<ProjectSuccess> RecentSuccesses { get; set; } = Array.Empty<ProjectSuccess>();
    public IReadOnlyCollection<(ProductDto Product, ReturnStatus Status, DateTime DueDate)> ProductsNeedingReturns { get; set; } = Array.Empty<(ProductDto, ReturnStatus, DateTime)>();
    public IReadOnlyCollection<(Project Project, UpdateSubmissionStatus Status, DateTime DueDate)> ProjectsNeedingMonthlyUpdates { get; set; } = Array.Empty<(Project, UpdateSubmissionStatus, DateTime)>();

    public IReadOnlyCollection<DashboardServiceRegisterTaskItem> IncompleteServiceRegisterProducts { get; set; } =
        Array.Empty<DashboardServiceRegisterTaskItem>();

    /// <summary>Monthly update status per project for the applicable reporting period (matches tasks / project table logic).</summary>
    public IReadOnlyDictionary<int, UpdateSubmissionStatus> MonthlyUpdateStatusByProjectId { get; set; } =
        new Dictionary<int, UpdateSubmissionStatus>();

    /// <summary>Applicable reporting period year/month for monthly delivery updates (aligned with deadline rules).</summary>
    public int ApplicableMonthlyUpdateYear { get; set; }
    public int ApplicableMonthlyUpdateMonth { get; set; }

    /// <summary>Display label for <see cref="ApplicableMonthlyUpdateYear"/> / <see cref="ApplicableMonthlyUpdateMonth"/> (explicit Admin period label when set).</summary>
    public string ApplicableMonthlyUpdatePeriodLabel { get; set; } = "";

    /// <summary>Whether the monthly delivery update submission window is open for the applicable period.</summary>
    public bool MonthlyReportingWindowOpen { get; set; }

    /// <summary>Active or paused work items still due or late for the applicable monthly period.</summary>
    public int MonthlyReportingRemainingCount { get; set; }
    public IReadOnlyCollection<(ProductDto Product, Commission Commission, CommissionSubmissionStatus Status, DateTime DueDate)> ProductsNeedingCommissionReporting { get; set; } = Array.Empty<(ProductDto, Commission, CommissionSubmissionStatus, DateTime)>();
    public YourTasksViewModel YourTasks { get; set; } = new();
    public IReadOnlyCollection<Project> OversightProjects { get; set; } = Array.Empty<Project>();
    public IReadOnlyCollection<UserBusinessAreaRoleAssignment> LeadershipAssignments { get; set; } = Array.Empty<UserBusinessAreaRoleAssignment>();
    public IReadOnlyCollection<string> LeadershipBusinessAreas { get; set; } = Array.Empty<string>();
    public LeadershipRoleTier? HighestLeadershipRole { get; set; }
    
    // DDT Standards
    public IReadOnlyCollection<DdtStandard> MyDdtStandards { get; set; } = Array.Empty<DdtStandard>();
    public IReadOnlyCollection<DdtStandard> MyOwnedDdtStandards { get; set; } = Array.Empty<DdtStandard>();
    public IReadOnlyCollection<DdtStandard> MyContactDdtStandards { get; set; } = Array.Empty<DdtStandard>();

    // Leadership view data
    public EnterpriseLeadershipMetrics EnterpriseMetrics { get; set; } = new();
    public IReadOnlyCollection<Mission> ActiveMissions { get; set; } = Array.Empty<Mission>();
    public IReadOnlyCollection<Objective> PriorityOutcomes { get; set; } = Array.Empty<Objective>();
    public IReadOnlyCollection<Project> EnterpriseAtRiskProjects { get; set; } = Array.Empty<Project>();

    public bool HasData => MyProjects.Any() || MyProducts.Any();
    public bool HasLeadershipRole => HighestLeadershipRole.HasValue;
    public bool IsEnterpriseLeader => HighestLeadershipRole.HasValue && 
        (HighestLeadershipRole == LeadershipRoleTier.PermanentSecretary || 
         HighestLeadershipRole == LeadershipRoleTier.DirectorGeneral || 
         HighestLeadershipRole == LeadershipRoleTier.CLevel);
    public bool IsBusinessAreaLeader => HighestLeadershipRole.HasValue && 
        (HighestLeadershipRole == LeadershipRoleTier.DeputyDirectorOrSro || 
         HighestLeadershipRole == LeadershipRoleTier.HeadOfProfession ||
         HighestLeadershipRole == LeadershipRoleTier.PortfolioLead);
}

public class EnterpriseLeadershipMetrics
{
    public int TotalOpenIssues { get; set; }
    public int TotalOpenRisks { get; set; }
    public int TotalOpenActions { get; set; }
    public int TotalAtRiskProjects { get; set; }
    public int ActiveMissionsCount { get; set; }
    public int ActivePriorityOutcomesCount { get; set; }
}

public class DashboardSectionConfig
{
    public bool ShowTasksPanel { get; set; } = true;
    public bool ShowProductPanel { get; set; } = true;
    public bool ShowRiskPanel { get; set; } = true;
    public bool ShowMilestonePanel { get; set; } = true;
    public bool ShowRemindersPanel { get; set; } = true;
    public bool ShowSuccessPanel { get; set; } = true;
    public string PreferredTaskGrouping { get; set; } = "priority";
    public string? DashboardFocus { get; set; }
}

public class DashboardMetrics
{
    public int TasksDue { get; set; }
    public int ServiceHealthIssues { get; set; }
    public int ProjectHealthIssues { get; set; }
    public int ProductCount { get; set; }
    public int UpcomingMilestones { get; set; }
    public int OpenIssues { get; set; }
    public int UnreviewedRisks { get; set; }
    public int WatchedDeliverables { get; set; }
}

public class DashboardTaskItem
{
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = "Pending";
    public string PriorityBadgeClass { get; set; } = "badge-secondary";
    public DateTime? DueDate { get; set; }
    public string? LinkLabel { get; set; }
    public string? LinkUrl { get; set; }
}

public sealed class DashboardServiceRegisterTaskItem
{
    public Guid ProductId { get; init; }
    public string Title { get; init; } = "";
    public int CompletionPercent { get; init; }
    public string MissingFieldsHint { get; init; } = "";
}

public class DashboardReminder
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = "fas fa-bell";
    public string FrequencyBadge { get; set; } = "Ad-hoc";
    public string Tone { get; set; } = "info";
    public string? LinkLabel { get; set; }
    public string? LinkUrl { get; set; }
}

public class DashboardQuickLink
{
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = "fas fa-link";
    public string Url { get; set; } = "#";
}

public class DashboardQuickLinkOption : DashboardQuickLink
{
    public bool Selected { get; set; }
    public bool Disabled { get; set; }
}

public class DashboardBlockDefinition
{
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public string Description { get; set; } = string.Empty;
    public int DefaultWidth { get; set; } = 3;
    public int DefaultHeight { get; set; } = 1;
    public int MinWidth { get; set; } = 2;
    public int MinHeight { get; set; } = 1;
    public bool SupportsConfiguration { get; set; }
    public bool UsesChart { get; set; }
    public bool IsTable { get; set; }
}

public class DashboardBlockInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Type { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 3;
    public int Height { get; set; } = 1;
    public Dictionary<string, string>? Settings { get; set; }
}

