using System.ComponentModel.DataAnnotations;
using Compass.Models;

namespace Compass.Models.Modern.Work;

/// <summary>Modern work item shape for ported compass-2 views; backed by <see cref="Project"/> (int id).</summary>
public class WorkItem
{
    public int Id { get; set; }
    public int? LegacyProjectId { get; set; }
    public Guid? DemandRequestId { get; set; }
    public Guid? BusinessCaseId { get; set; }

    [Required, MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    public string? ProblemStatement { get; set; }

    [MaxLength(4000)]
    public string? Aim { get; set; }

    public string? Description { get; set; }

    [Required, MaxLength(50)]
    public string Status { get; set; } = "Active";

    public int? RagStatusId { get; set; }
    public int? PriorityId { get; set; }
    [MaxLength(2000)]
    public string? PriorityChangeReason { get; set; }
    public int? DeliveryPhaseId { get; set; }
    public int? WorkTypeId { get; set; }
    public int? PortfolioId { get; set; }
    public int? PrimaryContactUserId { get; set; }
    public int? BudgetOwnerUserId { get; set; }
    public int? ActivityTypeId { get; set; }
    public int? RiskAppetiteId { get; set; }
    public bool SubjectToSpendControl { get; set; }
    public bool FlagshipProject { get; set; }
    public bool IsCentralRiskBucket { get; set; }

    /// <summary>
    /// When true, this work item is shown in FIPS and can be found by the FIPS service.
    /// </summary>
    public bool ShowInFips { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? TargetEndDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    [MaxLength(450)]
    public string? CreatedBy { get; set; }
    [MaxLength(450)]
    public string? UpdatedBy { get; set; }
    public int? PriorityOutcomeId { get; set; }
    public int? MissionPillarId { get; set; }

    public ICollection<WorkItemPriorityOutcome> PriorityOutcomes { get; set; } = new List<WorkItemPriorityOutcome>();
    public ICollection<WorkItemMissionPillar> MissionPillars { get; set; } = new List<WorkItemMissionPillar>();

    /// <summary>Custom tags from admin (<see cref="Compass.Models.WorkItemTagLookup"/>).</summary>
    public List<WorkItemTagRef> Tags { get; set; } = new();

    public ICollection<WorkItemContact> Contacts { get; set; } = new List<WorkItemContact>();
    public ICollection<WorkItemTeamMember> TeamMembers { get; set; } = new List<WorkItemTeamMember>();
    public ICollection<WorkItemDependency> Dependencies { get; set; } = new List<WorkItemDependency>();
    public ICollection<WorkItemDirectorate> Directorates { get; set; } = new List<WorkItemDirectorate>();
    public ICollection<WorkItemGovernmentDepartment> GovernmentDepartments { get; set; } = new List<WorkItemGovernmentDepartment>();
    public ICollection<WorkItemRagHistory> RagHistory { get; set; } = new List<WorkItemRagHistory>();
    public ICollection<Milestone> Milestones { get; set; } = new List<Milestone>();
    public ICollection<MonthlyUpdate> MonthlyUpdates { get; set; } = new List<MonthlyUpdate>();
    public ICollection<WeeklyUpdate> WeeklyUpdates { get; set; } = new List<WeeklyUpdate>();
    public ICollection<WorkItemRiskOrIssue> RiskOrIssues { get; set; } = new List<WorkItemRiskOrIssue>();

    /// <summary>RAID assumptions scoped to this work item (see project link on assumption records).</summary>
    public ICollection<WorkItemAssumptionRef> Assumptions { get; set; } = new List<WorkItemAssumptionRef>();
}

/// <summary>Lightweight assumption row for work item detail.</summary>
public class WorkItemAssumptionRef
{
    public int Id { get; set; }
    public string Description { get; set; } = "";
    public string? Criticality { get; set; }
    public string? Status { get; set; }
}

public class WorkItemPriorityOutcome
{
    public int Id { get; set; }
    public int PriorityOutcomeId { get; set; }
    public WorkLookupOption? PriorityOutcome { get; set; }
}

public class WorkItemMissionPillar
{
    public int Id { get; set; }
    public int MissionPillarId { get; set; }
    public WorkLookupOption? MissionPillar { get; set; }
}

public class WorkItemTagRef
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class ContactRoleType
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class WorkItemContact
{
    public int Id { get; set; }
    public int WorkItemId { get; set; }
    public int? ContactRoleTypeId { get; set; }
    /// <summary>When <see cref="ContactRoleTypeId"/> is custom (5), the label stored in <see cref="ProjectContact.Role"/>.</summary>
    public string? RoleName { get; set; }
    /// <summary>Display name stored on <see cref="Compass.Models.ProjectContact.Name"/> (editable on the work item).</summary>
    public string DisplayName { get; set; } = "";
    public User? AppUser { get; set; }
}

public class WorkItemDependency
{
    public int Id { get; set; }
    public int WorkItemId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public bool IsInternal { get; set; }
    public WorkItem? TargetWorkItem { get; set; }
    public string? ExternalDescription { get; set; }
}

public class WorkItemDirectorate
{
    public int Id { get; set; }
    public int DirectorateId { get; set; }
    public Division? Division { get; set; }
    public Compass.Models.Directorate? Directorate { get; set; }
}
public class WorkItemGovernmentDepartment
{
    public int Id { get; set; }
    public int GovernmentDepartmentId { get; set; }
    public GovernmentDepartment? GovernmentDepartment { get; set; }
}

/// <summary>RAG history entry for modern work views and Update RAG form; extends chrome snapshot for <c>_WorkRagBadge</c>.</summary>
public class WorkItemRagHistory : ChromeRagSnapshot
{
    public int Id { get; set; }
    public int WorkItemId { get; set; }
    public int RagStatusId { get; set; }
    public string? Justification { get; set; }
    public string? PathToGreen { get; set; }
    public int? UpdatedByUserId { get; set; }
}

/// <summary>Lifecycle / audit rows for work detail audit tab.</summary>
public class LifecycleAuditEntry
{
    public bool Highlight { get; set; }
    public string? LinkUrl { get; set; }
    public string? LinkText { get; set; }
    public string Event { get; set; } = string.Empty;
    public DateTime When { get; set; }
    public string Who { get; set; } = string.Empty;
}

public class AuditLog
{
    public string? ChangeType { get; set; }
    public DateTime ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
}

/// <summary>Reporting period row for work detail monthly updates tab.</summary>
public class ReportingCyclePeriod
{
    public string PeriodKey { get; set; } = string.Empty;
    public string? PeriodLabel { get; set; }
    /// <summary>Submission deadline (explicit period closes date, or legacy-calculated due date).</summary>
    public DateTime DueDate { get; set; }
    public DateTime? SubmissionOpens { get; set; }
    public DateTime? SubmissionCloses { get; set; }
    /// <summary>Rollup matching UpdateSubmissionStatus (Upcoming, Due, Late, Submitted).</summary>
    public string UpdateStatus { get; set; } = "Upcoming";
    /// <summary>False when an explicit period exists and the current date is outside the submission window.</summary>
    public bool WindowAllowsEditing { get; set; } = true;
}

/// <summary>Monthly update block for modern work views (aligned with <see cref="ProjectMonthlyUpdate"/> fields used in UI).</summary>
public class MonthlyUpdate
{
    public int Id { get; set; }
    public int WorkItemId { get; set; }
    public DateTime ReportMonth { get; set; }
    public string? Narrative { get; set; }
    public int? RagStatusId { get; set; }
    /// <summary>Resolved display label for submitted returns (includes legacy/history fallbacks).</summary>
    public string? RagDisplayName { get; set; }
    public string? RagCssClass { get; set; }
    public string? RagJustification { get; set; }
    public string? PathToGreen { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public int? SubmittedByUserId { get; set; }
    public decimal? PermFte { get; set; }
    public decimal? MspFte { get; set; }
    public string? PeopleNarrative { get; set; }
}

/// <summary>Weekly update block for modern work views (aligned with <see cref="Compass.Models.ProjectWeeklyWorkUpdate"/>).</summary>
public class WeeklyUpdate
{
    public int Id { get; set; }
    public int WorkItemId { get; set; }
    public int IsoYear { get; set; }
    public int IsoWeek { get; set; }
    public DateTime WeekStartDate { get; set; }
    public DateTime WeekEndDate { get; set; }
    public string PeriodLabel { get; set; } = string.Empty;
    public string? Narrative { get; set; }
    public int? RagStatusId { get; set; }
    public string? RagDisplayName { get; set; }
    public string? RagCssClass { get; set; }
    public string? RagJustification { get; set; }
    public string? PathToGreen { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public int? SubmittedByUserId { get; set; }
    public decimal? PermFte { get; set; }
    public decimal? MspFte { get; set; }
    public string? PeopleNarrative { get; set; }
}

public class WorkAppUser
{
    public string? Name { get; set; }
    public string? Email { get; set; }
}
