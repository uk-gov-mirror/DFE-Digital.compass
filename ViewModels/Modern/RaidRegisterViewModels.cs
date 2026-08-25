using Compass.Models;

namespace Compass.ViewModels.Modern;

public class RaidRegisterOnboardingViewModel
{
    public int? RegisterId { get; set; }
    public int Step { get; set; } = 1;
    public int TotalSteps { get; } = 6;

    // Step 1: Name & description
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Register owner (RaidRegisterUser with Owner role; defaults to creator).</summary>
    public int? OwnerUserId { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerEmail { get; set; }
    public string? CreatedByName { get; set; }

    /// <summary>Current user is the register owner (can transfer ownership or delete the register).</summary>
    public bool IsRegisterOwner { get; set; }

    // Step 2: Directorate & portfolio (multi-select)
    public List<int> SelectedDirectorateLookupIds { get; set; } = new();
    public List<int> SelectedBusinessAreaLookupIds { get; set; } = new();
    public List<SelectOption> DirectorateOptions { get; set; } = new();
    public List<SelectOption> BusinessAreaOptions { get; set; } = new();

    // Step 3: Work items
    public List<int> SelectedWorkItemIds { get; set; } = new();
    public List<SelectOption> WorkItemOptions { get; set; } = new();

    // Step 4: Service register entries
    public List<int> SelectedServiceIds { get; set; } = new();
    public List<SelectOption> ServiceOptions { get; set; } = new();

    // Step 5: Users
    public List<RaidRegisterUserRow> RegisterUsers { get; set; } = new();

    // Step 6: Review (read-only summary)
    public List<string> SelectedDirectorateNames { get; set; } = new();
    public List<string> SelectedBusinessAreaNames { get; set; } = new();
    public List<string> SelectedWorkItemNames { get; set; } = new();
    public List<string> SelectedServiceNames { get; set; } = new();
}

public record SelectOption(int Id, string Name);

public class RaidRegisterUserRow
{
    public int UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public RaidRegisterRole Role { get; set; } = RaidRegisterRole.Manager;
}

/// <summary>Dashboard card for one register on the top-level register list.</summary>
public class RaidRegisterCardViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? DirectorateName { get; set; }
    public string? BusinessAreaName { get; set; }
    public string? OwnerName { get; set; }
    public DateTime UpdatedAt { get; set; }

    public int OpenRiskCount { get; set; }
    public int OpenIssueCount { get; set; }
    public int OpenAssumptionCount { get; set; }
    public int OpenDependencyCount { get; set; }
    public int OpenNearMissCount { get; set; }
    public int TotalItemCount { get; set; }

    public List<string> WorkItemNames { get; set; } = new();
    public List<string> ServiceNames { get; set; } = new();

    /// <summary>User can edit this register (owner or manager). Otherwise read-only.</summary>
    public bool CanManage { get; set; }
}

/// <summary>Top-level RAID registers dashboard.</summary>
public class RaidRegisterDashboardViewModel
{
    public List<RaidRegisterCardViewModel> YourRegisters { get; set; } = new();
    public List<RaidRegisterCardViewModel> AllRegisters { get; set; } = new();
}

/// <summary>A single register's detail/sub-dashboard.</summary>
public class RaidRegisterDetailViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? DirectorateName { get; set; }
    public string? BusinessAreaName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public RaidRegisterRole CurrentUserRole { get; set; }

    public int OpenRiskCount { get; set; }
    public int OpenIssueCount { get; set; }
    public int OpenAssumptionCount { get; set; }
    public int OpenDependencyCount { get; set; }
    public int OpenNearMissCount { get; set; }

    public List<RaidRegisterRiskRow> Risks { get; set; } = new();
    public List<RaidRegisterIssueRow> Issues { get; set; } = new();
    public List<RaidRegisterAssumptionRow> Assumptions { get; set; } = new();
    public List<RaidRegisterDependencyRow> Dependencies { get; set; } = new();
    public List<RaidRegisterNearMissRow> NearMisses { get; set; } = new();

    public List<string> WorkItemNames { get; set; } = new();
    public List<string> ServiceNames { get; set; } = new();
    public List<RaidRegisterUserRow> Users { get; set; } = new();

    // Lookups for spreadsheet editing
    public List<SelectOption> RiskStatuses { get; set; } = new();
    public List<SelectOption> RiskPriorities { get; set; } = new();
    public List<SelectOption> RiskLikelihoods { get; set; } = new();
    public List<SelectOption> RiskImpactLevels { get; set; } = new();
    public List<SelectOption> RiskProximities { get; set; } = new();
    public List<SelectOption> RiskCategories { get; set; } = new();
    public List<SelectOption> IssueStatuses { get; set; } = new();
    public List<SelectOption> IssuePriorities { get; set; } = new();
    public List<SelectOption> IssueSeverities { get; set; } = new();
    public List<SelectOption> IssueCategories { get; set; } = new();
    public List<SelectOption> NearMissTypes { get; set; } = new();
    public List<SelectOption> NearMissSeriousnesses { get; set; } = new();
    public List<SelectOption> NearMissStatuses { get; set; } = new();
    public List<SelectOption> AssumptionStatuses { get; set; } = new();
    public List<SelectOption> AssumptionCriticalities { get; set; } = new();
    public List<SelectOption> RiskTiers { get; set; } = new();

    /// <summary>Work items for inline relation editing.</summary>
    public List<SelectOption> WorkItemOptions { get; set; } = new();

    /// <summary>Service register products for inline relation editing.</summary>
    public List<SelectOption> ServiceOptions { get; set; } = new();

    /// <summary>Admin-configured default column order per spreadsheet entity type (risk, issue, …).</summary>
    public Dictionary<string, List<string>> SpreadsheetColumnOrders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Central Ops / Super admin may edit inherent impact and likelihood after first save.</summary>
    public bool CanEditLockedInherentRatings { get; set; }
}

public class RaidRegisterRiskRow
{
    public int MitigationCount { get; set; }
    public int KriCount { get; set; }
    public int Id { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Status { get; set; }
    public int? StatusId { get; set; }
    public DateTime? ClosedDate { get; set; }
    public string? Owner { get; set; }
    public int? OwnerUserId { get; set; }
    public string? Tier { get; set; }
    public int? TierId { get; set; }
    public string? Category { get; set; }
    public int? CategoryId { get; set; }
    public string? Priority { get; set; }
    public int? PriorityId { get; set; }
    public string? Proximity { get; set; }
    public int? ProximityId { get; set; }
    public string? ResponseStrategy { get; set; }
    public string? Cause { get; set; }
    public string? ImpactIfRealised { get; set; }
    public string? Contingency { get; set; }
    public string? Assurance { get; set; }
    public string? FinancialImpact { get; set; }
    /// <summary>Formatted KRI summary for spreadsheet display.</summary>
    public string? KrisSummary { get; set; }
    public string? Response { get; set; }

    // Original rating
    public int? OriginalImpactId { get; set; }
    public string? OriginalImpact { get; set; }
    public int? OriginalLikelihoodId { get; set; }
    public string? OriginalLikelihood { get; set; }
    public decimal? InherentScore { get; set; }

    // Current rating
    public int? CurrentImpactId { get; set; }
    public string? CurrentImpact { get; set; }
    public int? CurrentLikelihoodId { get; set; }
    public string? CurrentLikelihood { get; set; }
    public decimal? CurrentScore { get; set; }

    // Residual rating
    public int? ResidualImpactId { get; set; }
    public string? ResidualImpact { get; set; }
    public int? ResidualLikelihoodId { get; set; }
    public string? ResidualLikelihood { get; set; }
    public decimal? ResidualScore { get; set; }

    // Tolerance rating
    public int? ToleranceImpactId { get; set; }
    public string? ToleranceImpact { get; set; }
    public int? ToleranceLikelihoodId { get; set; }
    public string? ToleranceLikelihood { get; set; }
    public decimal? ToleranceScore { get; set; }

    public DateTime? NextReviewDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? IdentifiedDate { get; set; }
    public DateTime? LastReviewDate { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int CommentCount { get; set; }

    /// <summary>Full text of the most recent comment or mitigation update line.</summary>
    public string? LastCommentUpdateText { get; set; }

    /// <summary>Source label, e.g. Comment or Mitigation update.</summary>
    public string? LastCommentUpdateKind { get; set; }

    public DateTime? LastCommentUpdateAt { get; set; }

    public string RelationKind { get; set; } = RaidRegisterRelationKinds.Unknown;
    public int? RelationProjectId { get; set; }
    public string? RelationTarget { get; set; }
    public string? RelationSourceLabel { get; set; }
    public string? RelationRelatedTitle { get; set; }
    public string? RelationRelatedDescription { get; set; }
    public string? RelationLinkHref { get; set; }
    public string? AssociationUiKind { get; set; }
    public int? PrimaryProductId { get; set; }

    public RaidRegisterRelationParts ToRelationParts(string workDetailSection) =>
        new(RelationKind, RelationProjectId, RelationTarget, workDetailSection,
            RelationSourceLabel, RelationRelatedTitle, RelationRelatedDescription, RelationLinkHref,
            AssociationUiKind, PrimaryProductId);
}

public class RaidRegisterIssueRow
{
    public int Id { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Status { get; set; }
    public int? StatusId { get; set; }
    public DateTime? ClosedDate { get; set; }
    public string? Severity { get; set; }
    public int? SeverityId { get; set; }
    public string? Priority { get; set; }
    public int? PriorityId { get; set; }
    public string? Category { get; set; }
    public int? CategoryId { get; set; }
    public string? Owner { get; set; }
    public int? OwnerUserId { get; set; }
    public DateTime? IdentifiedDate { get; set; }
    public DateTime? TargetResolutionDate { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int CommentCount { get; set; }

    public string RelationKind { get; set; } = RaidRegisterRelationKinds.Unknown;
    public int? RelationProjectId { get; set; }
    public string? RelationTarget { get; set; }
    public string? RelationSourceLabel { get; set; }
    public string? RelationRelatedTitle { get; set; }
    public string? RelationRelatedDescription { get; set; }
    public string? RelationLinkHref { get; set; }
    public string? AssociationUiKind { get; set; }
    public int? PrimaryProductId { get; set; }

    public RaidRegisterRelationParts ToRelationParts(string workDetailSection) =>
        new(RelationKind, RelationProjectId, RelationTarget, workDetailSection,
            RelationSourceLabel, RelationRelatedTitle, RelationRelatedDescription, RelationLinkHref,
            AssociationUiKind, PrimaryProductId);
}

public class RaidRegisterAssumptionRow
{
    public int Id { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Status { get; set; }
    public int? StatusId { get; set; }
    public string? Criticality { get; set; }
    public int? CriticalityId { get; set; }
    public string? Owner { get; set; }
    public int? OwnerUserId { get; set; }
    public DateTime? ReviewDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int CommentCount { get; set; }

    public string RelationKind { get; set; } = RaidRegisterRelationKinds.Unknown;
    public int? RelationProjectId { get; set; }
    public string? RelationTarget { get; set; }
    public string? RelationSourceLabel { get; set; }
    public string? RelationRelatedTitle { get; set; }
    public string? RelationRelatedDescription { get; set; }
    public string? RelationLinkHref { get; set; }
    public string? AssociationUiKind { get; set; }
    public int? PrimaryProductId { get; set; }

    public RaidRegisterRelationParts ToRelationParts(string workDetailSection) =>
        new(RelationKind, RelationProjectId, RelationTarget, workDetailSection,
            RelationSourceLabel, RelationRelatedTitle, RelationRelatedDescription, RelationLinkHref,
            AssociationUiKind, PrimaryProductId);
}

public class RaidRegisterDependencyRow
{
    public int Id { get; set; }
    public string? Description { get; set; }
    public string? LinkType { get; set; }
    public string? Status { get; set; }
    public string? Owner { get; set; }
}

public class RaidRegisterNearMissRow
{
    public int Id { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Impact { get; set; }
    public string? Status { get; set; }
    public int? StatusId { get; set; }
    public string? Seriousness { get; set; }
    public int? SeriousnessId { get; set; }
    public string? Type { get; set; }
    public int? TypeId { get; set; }
    public string? Owner { get; set; }
    public int? OwnerUserId { get; set; }
    public DateTime? DateLogged { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int CommentCount { get; set; }

    public string RelationKind { get; set; } = RaidRegisterRelationKinds.Unknown;
    public int? RelationProjectId { get; set; }
    public string? RelationTarget { get; set; }
    public string? RelationSourceLabel { get; set; }
    public string? RelationRelatedTitle { get; set; }
    public string? RelationRelatedDescription { get; set; }
    public string? RelationLinkHref { get; set; }

    public RaidRegisterRelationParts ToRelationParts(string workDetailSection) =>
        new(RelationKind, RelationProjectId, RelationTarget, workDetailSection,
            RelationSourceLabel, RelationRelatedTitle, RelationRelatedDescription, RelationLinkHref);
}
