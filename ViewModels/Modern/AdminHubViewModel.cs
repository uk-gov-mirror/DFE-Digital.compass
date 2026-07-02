using Compass.Models;
using Compass.Services;

namespace Compass.ViewModels.Modern;

/// <summary>Admin hub (matches Compass2 <c>AdminHubViewModel</c>): left navigation + configuration panel.</summary>
public class AdminHubViewModel
{
    /// <summary>Active panel id (e.g. business-areas, groups, audit).</summary>
    public string Panel { get; set; } = "hub";

    /// <summary>Global product toggles (<c>feature-settings</c> panel).</summary>
    public List<AdminFeatureToggleRow> FeatureSettingsRows { get; set; } = new();

    /// <summary>Environment sync panel (development instance only).</summary>
    public EnvironmentSyncPanelViewModel? EnvironmentSync { get; set; }

    /// <summary>Active groups for the feature-settings &quot;on for some&quot; group dropdowns (same as <c>panel=groups</c> list).</summary>
    public List<AdminFeatureSettingsGroupOption> FeatureSettingsGroupOptions { get; set; } = new();

    /// <summary>Portfolio list (5-column lookup table).</summary>
    public List<AdminLookupRow> PortfolioRows { get; set; } = new();
    public List<AdminLookupRow> BusinessAreas { get; set; } = new();
    public List<AdminLookupRow> UniversalBarriers { get; set; } = new();
    public List<AdminLookupRow> Phases { get; set; } = new();
    public List<AdminLookupRow> Directorates { get; set; } = new();
    public List<AdminPriorityRow> Priorities { get; set; } = new();
    public List<AdminRagRow> RagDefinitions { get; set; } = new();

    public List<AdminLookupRow> ActivityTypes { get; set; } = new();
    public List<AdminLookupRow> WorkItemTags { get; set; } = new();
    public List<AdminResourceBandRow> ResourceBands { get; set; } = new();
    public List<AdminLookupRow> MissionPillars { get; set; } = new();
    public List<AdminPriorityOutcomeRow> PriorityOutcomes { get; set; } = new();
    public List<AdminLookupRow> RiskTiers { get; set; } = new();
    public List<AdminLookupRow> RiskCategories { get; set; } = new();
    public List<AdminLookupRow> RiskOrigins { get; set; } = new();
    public List<AdminLookupRow> IssueCategories { get; set; } = new();

    /// <summary>RAID register spreadsheet default column order (<c>raid-register-table-view</c> panel).</summary>
    public AdminRaidRegisterTableViewPanel? RaidRegisterTableView { get; set; }

    // ── Generic RAID lookup rows (for any active RAID panel) ──
    public List<AdminLookupRow> RaidLookupRows { get; set; } = new();
    public string? RaidPanelKey { get; set; }
    public string? RaidPanelLabel { get; set; }
    public string? RaidPanelDescription { get; set; }
    public bool RaidCanSeedDefaults { get; set; }

    // ── Government departments ──
    public List<AdminGovDeptRow> GovernmentDepartmentRows { get; set; } = new();

    // ── Groups (inline on hub) ──
    public List<AdminGroupRow> GroupRows { get; set; } = new();

    /// <summary><c>business-area-admins</c> panel — delegated admins per Compass business area.</summary>
    public List<AdminBusinessAreaAdminMemberRow> BusinessAreaAdminMemberRows { get; set; } = new();

    /// <summary><c>business-area-leadership</c> panel — Deputy Director (DD) / leadership per business area.</summary>
    public List<AdminBusinessAreaLeadershipMemberRow> BusinessAreaLeadershipMemberRows { get; set; } = new();

    /// <summary><c>directorate-leadership</c> panel — users assigned to a directorate (see Directorates under lookups).</summary>
    public List<AdminDirectorateLeadershipMemberRow> DirectorateLeadershipMemberRows { get; set; } = new();

    // ── API tokens ──
    public List<AdminApiTokenRow> ApiTokenRows { get; set; } = new();

    /// <summary><c>cms-access-products</c> — CMS names for the external CMS access request API.</summary>
    public List<AdminCmsAccessProductRow> CmsAccessProductRows { get; set; } = new();

    // ── Audit log ──
    public List<AdminAuditRow> AuditRows { get; set; } = new();

    // ── FIPS configuration ──
    public List<AdminLookupRow> FipsChannels { get; set; } = new();
    public List<AdminLookupRow> FipsTypes { get; set; } = new();
    public List<AdminLookupRow> FipsBusinessAreas { get; set; } = new();
    public List<AdminFipsUserGroupRow> FipsUserGroups { get; set; } = new();
    public List<AdminFipsUserGroupParentOption> FipsUserGroupParentOptions { get; set; } = new();
    public List<AdminFipsContactRoleRow> FipsContactRoles { get; set; } = new();
    public List<AdminFipsCategorisationGroupRow> FipsCategorisationGroups { get; set; } = new();

    // ── Standards configuration ──
    public List<AdminLookupRow> StdCategories { get; set; } = new();
    public List<AdminStdSubCategoryRow> StdSubCategories { get; set; } = new();
    public List<AdminFunctionalStandardRow> StdFunctional { get; set; } = new();

    /// <summary>Legacy inline-edit id (unused; create/edit use dedicated routes).</summary>
    public int? EditId { get; set; }
}

public class AdminStdSubCategoryRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public class AdminFunctionalStandardRow
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int ThemeCount { get; set; }
    public DateTime? PublishedDate { get; set; }
}

public class AdminApiTokenRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsActive { get; set; }
    public string? Environment { get; set; }
    public string? AccessTier { get; set; }
    public string? OwnerEmail { get; set; }
    public bool IsSelfService { get; set; }
}

public sealed class AdminCmsAccessProductRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SignInPageUrl { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public class AdminAuditRow
{
    public DateTime Timestamp { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public class AdminFipsUserGroupRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
    public bool Active { get; set; }
    public int Depth { get; set; }
    public string? ParentPath { get; set; }
    public bool HasChildren { get; set; }
    public List<string> SynonymNames { get; set; } = new();
}

public class AdminFipsUserGroupParentOption
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
}

public class AdminFipsContactRoleRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool AllowMultiple { get; set; }
    public int DisplayOrder { get; set; }
    public bool Active { get; set; }
}

/// <summary>FIPS admin: custom categorisation group with child items.</summary>
public sealed class AdminFipsCategorisationGroupRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
    public bool Active { get; set; }
    public List<AdminLookupRow> Items { get; set; } = new();
}

public class AdminLookupRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Optional; RAID-style code (shown in list when relevant).</summary>
    public string? Code { get; set; }
    /// <summary>Optional; used by universal barriers panel (GOV.UK guidance link).</summary>
    public string? GuidanceUrl { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public class AdminPriorityRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? CssClass { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public class AdminRagRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CssClass { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public class AdminResourceBandRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal MinFte { get; set; }
    public decimal? MaxFte { get; set; }
    public string? CssClass { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public class WorkReportingViewModel
{
    /// <summary>Explicit monthly reporting periods (commission-style dates).</summary>
    public List<WorkReportingPeriodRow> ReportingPeriods { get; set; } = new();

    public WeeklyWorkReportingConfigFormViewModel WeeklyConfig { get; set; } = new();
    public List<WeeklyWorkReportingScopeRow> WeeklyScopeProjects { get; set; } = new();
    public List<WeeklyWorkReportingScopeCandidateRow> WeeklyScopeCandidates { get; set; } = new();
}

public class WeeklyWorkReportingConfigFormViewModel
{
    public DayOfWeek PeriodStartDayOfWeek { get; set; } = DayOfWeek.Monday;
    public DayOfWeek PeriodEndDayOfWeek { get; set; } = DayOfWeek.Friday;
    public DayOfWeek DueDayOfWeek { get; set; } = DayOfWeek.Friday;
    public WeeklyWorkReportingDueWeekOffset DueWeekOffset { get; set; } = WeeklyWorkReportingDueWeekOffset.SameWeek;
    public DateTime? FirstReportingPeriodStart { get; set; } = new(2026, 6, 29, 0, 0, 0, DateTimeKind.Utc);
    public bool IsActive { get; set; } = true;
}

public class WeeklyWorkReportingScopeRow
{
    public int ScopeId { get; set; }
    public int ProjectId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string BusinessArea { get; set; } = "—";
}

public class WeeklyWorkReportingScopeCandidateRow
{
    public int ProjectId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string BusinessArea { get; set; } = "—";
}

/// <summary>Admin list row for <see cref="Models.WorkReportingCyclePeriod"/>.</summary>
public class WorkReportingPeriodRow
{
    public int Id { get; set; }
    public string PeriodKey { get; set; } = string.Empty;
    public string PeriodLabel { get; set; } = string.Empty;
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public DateTime SubmissionOpens { get; set; }
    public DateTime SubmissionCloses { get; set; }
    public bool IsActive { get; set; }
    public bool IsSubmissionWindowLive { get; set; }
}

/// <summary>Create/edit form for a work reporting period.</summary>
public class WorkReportingPeriodFormViewModel
{
    public int Id { get; set; }
    public string PeriodLabel { get; set; } = string.Empty;
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public DateTime? SubmissionOpens { get; set; }
    public DateTime? SubmissionCloses { get; set; }
    /// <summary>Create form starts unchecked; edit loads from DB. Avoid a duplicate hidden <c>isActive=false</c> — it breaks checkbox binding when checked.</summary>
    public bool IsActive { get; set; }
}

public class DeadlineConfigRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DueRule { get; set; } = string.Empty;
    public int DueMonthOffset { get; set; }
    public int DueCalendarDay { get; set; }
    public int CommissionDaysBeforeMonthEnd { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveUntil { get; set; }
    public bool IsActive { get; set; }
    public bool IsDefault { get; set; }
    public string? Notes { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsCurrentlyEffective { get; set; }
}

public class AdminGroupsViewModel
{
    public List<AdminGroupRow> Groups { get; set; } = new();
}

/// <summary>Create / edit group (dedicated pages, not inline on the list).</summary>
public class AdminGroupFormViewModel
{
    public int? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsSystemGroup { get; set; }
}

public class AdminGroupRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int MemberCount { get; set; }
    public int FeatureCount { get; set; }
    public bool IsActive { get; set; }
    public bool IsSystemGroup { get; set; }
}

public class AdminGroupDetailViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public bool IsSystemGroup { get; set; }
    public List<AdminGroupMemberRow> Members { get; set; } = new();
    public List<AdminGroupFeatureRow> Features { get; set; } = new();
    public List<AdminAvailableFeature> AvailableFeatures { get; set; } = new();
    public List<AdminUserOption> AvailableUsers { get; set; } = new();
}

public class AdminGroupMemberRow
{
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Role { get; set; }
    public DateTime AssignedAt { get; set; }
}

public class AdminGroupFeatureRow
{
    public int PermissionId { get; set; }
    public int FeatureId { get; set; }
    public string FeatureName { get; set; } = string.Empty;
    public string FeatureCode { get; set; } = string.Empty;
    public string Permission { get; set; } = string.Empty;
}

public class AdminAvailableFeature
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

public class AdminUserOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

// ── Performance Reporting Hub ──

public class PerfReportingHubViewModel
{
    public string Panel { get; set; } = "metrics";
    public int? EditId { get; set; }

    public List<PerfMetricRow> Metrics { get; set; } = new();
    public List<PerfCommissionRow> Commissions { get; set; } = new();
    public List<PerfDueDateOverrideRow> DueDateOverrides { get; set; } = new();
    public List<PerfProductExclusionRow> ProductExclusions { get; set; } = new();
    /// <summary>Active Service Register products for the exclusion picker.</summary>
    public List<PerfProductExclusionProductOption> ServiceRegisterProductOptions { get; set; } = new();
}

public class PerfMetricRow
{
    public int Id { get; set; }
    public string Identifier { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ValueType { get; set; } = string.Empty;
    /// <summary>Enum value for form selects (matches <see cref="Models.ValueType"/>).</summary>
    public int ValueTypeInt { get; set; }
    public int ValidFromYear { get; set; }
    public int ValidFromMonth { get; set; }
    public string ValidFrom { get; set; } = string.Empty;
    public string? ApplicablePhases { get; set; }
    public string? ApplicableTypes { get; set; }
    public string ValidationRules { get; set; } = "{\"required\": true, \"allowNull\": false}";
    public int? ConditionalOnMetricId { get; set; }
    public bool IsDisabled { get; set; }
    public List<string> CataloguePhaseOptions { get; set; } = new();
    public List<string> CatalogueTypeOptions { get; set; } = new();
    public List<PerfMetricConditionalOption> ConditionalMetricOptions { get; set; } = new();
}

public class PerfMetricConditionalOption
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

public class PerfCommissionMetricPickRow
{
    public int Id { get; set; }
    public string Identifier { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool Included { get; set; }
}

public class PerfCommissionRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Quarter { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime OpenDate { get; set; }
    public DateTime DueDate { get; set; }
    public bool IsActive { get; set; }
    public int SubmissionCount { get; set; }

    /// <summary>Comma-separated phases in scope; empty means all phases (subject to global exclusions).</summary>
    public string? InScopePhases { get; set; }

    /// <summary>Comma-separated Type category values in scope; empty means all types.</summary>
    public string? InScopeTypes { get; set; }

    /// <summary>Comma-separated performance metric ids; empty means all metrics valid for the period.</summary>
    public string? IncludedPerformanceMetricIds { get; set; }

    public List<string> CataloguePhaseOptions { get; set; } = new();
    public List<string> CatalogueTypeOptions { get; set; } = new();
    public List<PerfCommissionMetricPickRow> MetricOptions { get; set; } = new();
}

public class PerfDueDateOverrideRow
{
    public int Id { get; set; }
    public int ReportingYear { get; set; }
    public int ReportingMonth { get; set; }
    public DateTime DueDate { get; set; }
    public string? Reason { get; set; }
    public bool IsActive { get; set; }
}

public class PerfProductExclusionRow
{
    public int Id { get; set; }
    public string ProductDocumentId { get; set; } = string.Empty;
    public string? ProductName { get; set; }
    public string ExclusionReason { get; set; } = string.Empty;
    public string ExclusionFrom { get; set; } = string.Empty;
    public string? ExclusionUntil { get; set; }
    public int ExclusionFromYear { get; set; }
    public int ExclusionFromMonth { get; set; }
    public int? ExclusionUntilYear { get; set; }
    public int? ExclusionUntilMonth { get; set; }
    public bool IsActive { get; set; }
}

public class AdminGovDeptRow
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Abbreviation { get; set; }
    public string? Format { get; set; }
    public string? GovukStatus { get; set; }
    public bool IsActive { get; set; }
    public int ChildCount { get; set; }
    public DateTime LastSyncedAt { get; set; }
    public string? ParentTitle { get; set; }
}

public sealed class AdminBusinessAreaAdminMemberRow
{
    public int MembershipId { get; set; }
    public string BusinessAreaName { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public string UserDisplayName { get; set; } = string.Empty;
}

public sealed class AdminBusinessAreaLeadershipMemberRow
{
    public int MembershipId { get; set; }
    public string BusinessAreaName { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public string UserDisplayName { get; set; } = string.Empty;
}

/// <summary>Add delegated business area admin (<c>/modern/admin/business-area-admins/create</c>).</summary>
public sealed class AdminBusinessAreaAdminCreateViewModel
{
    public IReadOnlyList<RaidLookupOptionVm> BusinessAreaOptions { get; init; } = Array.Empty<RaidLookupOptionVm>();
}

/// <summary>Assign Deputy Director (DD) / business area leadership (<c>/modern/admin/business-area-leadership/create</c>).</summary>
public sealed class AdminBusinessAreaLeadershipCreateViewModel
{
    public IReadOnlyList<RaidLookupOptionVm> BusinessAreaOptions { get; init; } = Array.Empty<RaidLookupOptionVm>();
}

public sealed class AdminDirectorateLeadershipMemberRow
{
    public int MembershipId { get; set; }
    public string DirectorateName { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public string UserDisplayName { get; set; } = string.Empty;
}

/// <summary>Add directorate leader (<c>/modern/admin/directorate-leadership/create</c>).</summary>
public sealed class AdminDirectorateLeadershipCreateViewModel
{
    public IReadOnlyList<RaidLookupOptionVm> DirectorateOptions { get; init; } = Array.Empty<RaidLookupOptionVm>();
}

/// <summary>Manage which business areas sit under each directorate (many-to-many; a BA can map to more than one directorate).</summary>
public sealed class AdminOrganisationStructureViewModel
{
    public List<AdminDirectorateWithBusinessAreasRow> Directorates { get; set; } = new();
    public IReadOnlyList<RaidLookupOptionVm> AllBusinessAreaOptions { get; set; } = Array.Empty<RaidLookupOptionVm>();
}

public sealed class AdminDirectorateWithBusinessAreasRow
{
    public int DivisionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<AdminDirectorateBusinessAreaLinkRow> Links { get; set; } = new();
}

public sealed class AdminDirectorateBusinessAreaLinkRow
{
    public int LinkId { get; set; }
    public string BusinessAreaName { get; set; } = string.Empty;
}

public class AdminPriorityOutcomeRow
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Theme { get; set; }
    public string? Description { get; set; }
    public string? OwnerName { get; set; }
    public string? ThemeSroName { get; set; }
    public string? OutcomeSroName { get; set; }
    public string? MissionPillar { get; set; }
    public string? Status { get; set; }
    public bool IsDeleted { get; set; }
}

