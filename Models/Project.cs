using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Compass.Models.DemandPipeline;

namespace Compass.Models;

public class Project
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [MaxLength(20)]
    public string ProjectCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public string? Aim { get; set; }

    public string? StrategicObjectives { get; set; }

    public string? MissionPillars { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? TargetDeliveryDate { get; set; }

    public DateTime? ActualDeliveryDate { get; set; }

    [Required]
    public bool IsFlagship { get; set; } = false;

    [Required]
    public bool IsAiInitiative { get; set; } = false;

    // RAG Status - using foreign key to RagStatusLookup
    public int? RagStatusLookupId { get; set; }
    [ForeignKey(nameof(RagStatusLookupId))]
    public RagStatusLookup? RagStatusLookup { get; set; }

    [MaxLength(20)]
    [Obsolete("Use RagStatusLookupId instead. This property is kept for backward compatibility.")]
    public string? RagStatus { get; set; } // Deprecated: Use RagStatusLookupId

    [MaxLength(4000)]
    public string? RagJustification { get; set; }

    [MaxLength(4000)]
    public string? PathToGreen { get; set; }

    // Phase - using foreign key to PhaseLookup
    public int? PhaseId { get; set; }
    [ForeignKey(nameof(PhaseId))]
    public PhaseLookup? PhaseLookup { get; set; }

    // BusinessArea - using foreign key to BusinessAreaLookup
    public int? BusinessAreaId { get; set; }
    [ForeignKey(nameof(BusinessAreaId))]
    public BusinessAreaLookup? BusinessAreaLookup { get; set; }

    [MaxLength(100)]
    public string? HistoricBuRTId { get; set; }

    public int? PrimaryContactUserId { get; set; }

    [ForeignKey(nameof(PrimaryContactUserId))]
    public User? PrimaryContactUser { get; set; }

    public int? DeliveryPriorityId { get; set; }

    [ForeignKey(nameof(DeliveryPriorityId))]
    public DeliveryPriority? DeliveryPriority { get; set; }

    [MaxLength(500)]
    public string? DeliveryPriorityChangeReason { get; set; }

    // Organizational structure fields
    public int? PrimaryOrganizationalGroupId { get; set; }
    public OrganizationalGroup? PrimaryOrganizationalGroup { get; set; }
    
    public bool IsMultiDepartmentProject { get; set; } = false;
    
    public string? OtherDepartments { get; set; } // JSON array of government department IDs

    [MaxLength(20)]
    public string? BusinessCaseApproval { get; set; } // Yes, No, Not applicable

    public decimal? TotalPermFte { get; set; }

    public decimal? TotalMspFte { get; set; }

    [MaxLength(20)]
    public string? Status { get; set; } = "Active"; // Active, Paused, Completed, Cancelled

    public string? StatusChangeReason { get; set; }

    public bool IsDeleted { get; set; } = false;

    /// <summary>Optional link to a pipeline demand request (<see cref="DemandPipeline.DemandPipelineRequest"/>).</summary>
    public Guid? PipelineDemandRequestId { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(20)]
    public string? CreationMethod { get; set; } // Manual, Bulk

    // Navigation properties
    public ICollection<ProjectSuccess> Successes { get; set; } = new List<ProjectSuccess>();
    public ICollection<ProjectRagHistory> RagHistory { get; set; } = new List<ProjectRagHistory>();
    public ICollection<ProjectOutcome> Outcomes { get; set; } = new List<ProjectOutcome>();
    public ICollection<ProjectNeed> Needs { get; set; } = new List<ProjectNeed>();
    public ICollection<ProjectProblemStatement> ProblemStatements { get; set; } = new List<ProjectProblemStatement>();
    public ICollection<ProjectMission> ProjectMissions { get; set; } = new List<ProjectMission>();
    public ICollection<ProjectFundingAllocation> FundingAllocations { get; set; } = new List<ProjectFundingAllocation>();
    public ICollection<ProjectResourceFunding> ResourceFunding { get; set; } = new List<ProjectResourceFunding>();
    public ICollection<ProjectResourceFundingHistory> FundingHistory { get; set; } = new List<ProjectResourceFundingHistory>();
    public ICollection<ProjectContact> ProjectContacts { get; set; } = new List<ProjectContact>();
    public ICollection<ProjectObjective> ProjectObjectives { get; set; } = new List<ProjectObjective>();

    [NotMapped]
    public ICollection<Dependency> DependenciesAsSource { get; set; } = new List<Dependency>();

    [NotMapped]
    public ICollection<Dependency> DependenciesAsTarget { get; set; } = new List<Dependency>();

    public ICollection<ProjectProduct> ProjectProducts { get; set; } = new List<ProjectProduct>();
    public ICollection<Milestone> Milestones { get; set; } = new List<Milestone>();
    public ICollection<Risk> Risks { get; set; } = new List<Risk>();
    public ICollection<Issue> Issues { get; set; } = new List<Issue>();
    public ICollection<Assumption> Assumptions { get; set; } = new List<Assumption>();
    public ICollection<Models.Action> Actions { get; set; } = new List<Models.Action>();
    public ICollection<Decision> Decisions { get; set; } = new List<Decision>();
    public ICollection<Kpi> Kpis { get; set; } = new List<Kpi>();

    // New fields for enhanced project tracking
    public ICollection<ProjectStatusUpdate> StatusUpdates { get; set; } = new List<ProjectStatusUpdate>();
    public ICollection<ProjectMonthlyUpdate> MonthlyUpdates { get; set; } = new List<ProjectMonthlyUpdate>();
    public ICollection<ProjectWeeklyWorkUpdate> WeeklyWorkUpdates { get; set; } = new List<ProjectWeeklyWorkUpdate>();
    public ICollection<ProjectWeeklySuccessUpdate> WeeklySuccessUpdates { get; set; } = new List<ProjectWeeklySuccessUpdate>();
    public ICollection<ProjectSeniorResponsibleOfficer> SeniorResponsibleOfficers { get; set; } = new List<ProjectSeniorResponsibleOfficer>();
    public ICollection<ProjectServiceOwner> ServiceOwners { get; set; } = new List<ProjectServiceOwner>();
    public ICollection<ProjectDirectorate> Directorates { get; set; } = new List<ProjectDirectorate>();
    public ICollection<ProjectBudgetOwner> BudgetOwners { get; set; } = new List<ProjectBudgetOwner>();
    public ICollection<ProjectPmoContact> PmoContacts { get; set; } = new List<ProjectPmoContact>();
    public ICollection<ProjectArtefact> Artefacts { get; set; } = new List<ProjectArtefact>();

    /// <summary>Custom work tags (<see cref="WorkItemTagLookup"/>), managed in Admin → Work → Tagging.</summary>
    public ICollection<ProjectWorkItemTag> ProjectWorkItemTags { get; set; } = new List<ProjectWorkItemTag>();

    [ForeignKey(nameof(PipelineDemandRequestId))]
    public DemandPipelineRequest? PipelineDemandRequest { get; set; }

    // Activity Type
    public int? ActivityTypeLookupId { get; set; }
    [ForeignKey(nameof(ActivityTypeLookupId))]
    public ActivityTypeLookup? ActivityTypeLookup { get; set; }

    // Risk Appetite
    public int? RiskAppetiteLookupId { get; set; }
    [ForeignKey(nameof(RiskAppetiteLookupId))]
    public RiskAppetiteLookup? RiskAppetiteLookup { get; set; }

    // Service Users (free text)
    public string? ServiceUsers { get; set; }

    // Internal/External flags
    public bool IsInternal { get; set; } = false;
    public bool IsExternal { get; set; } = false;

    public bool? IsSubjectToSpendControl { get; set; }

    // Phase dates - Discovery
    public DateTime? DiscoveryStartDatePlanned { get; set; }
    public DateTime? DiscoveryStartDateActual { get; set; }
    public DateTime? DiscoveryEndDatePlanned { get; set; }
    public DateTime? DiscoveryEndDateActual { get; set; }

    // Phase dates - Alpha
    public DateTime? AlphaStartDatePlanned { get; set; }
    public DateTime? AlphaStartDateActual { get; set; }
    public DateTime? AlphaEndDatePlanned { get; set; }
    public DateTime? AlphaEndDateActual { get; set; }

    // Phase dates - Private Beta
    public DateTime? PrivateBetaStartDatePlanned { get; set; }
    public DateTime? PrivateBetaStartDateActual { get; set; }
    public DateTime? PrivateBetaEndDatePlanned { get; set; }
    public DateTime? PrivateBetaEndDateActual { get; set; }

    // Phase dates - Public Beta
    public DateTime? PublicBetaStartDatePlanned { get; set; }
    public DateTime? PublicBetaStartDateActual { get; set; }
    public DateTime? PublicBetaEndDatePlanned { get; set; }
    public DateTime? PublicBetaEndDateActual { get; set; }

    // Computed properties for backward compatibility (will be removed after migration)
    [NotMapped]
    public string? Phase
    {
        get => PhaseLookup?.Name;
        set
        {
            // This setter is for backward compatibility during migration
            // In practice, code should set PhaseId directly
            if (string.IsNullOrEmpty(value))
            {
                PhaseId = null;
            }
        }
    }

    [NotMapped]
    public string? BusinessArea
    {
        get => BusinessAreaLookup?.Name;
        set
        {
            // This setter is for backward compatibility during migration
            // In practice, code should set BusinessAreaId directly
            if (string.IsNullOrEmpty(value))
            {
                BusinessAreaId = null;
            }
        }
    }

    [NotMapped]
    public string? RagStatusName
    {
        get => RagStatusLookup?.Name;
        set
        {
            // This setter is for backward compatibility during migration
            // In practice, code should set RagStatusLookupId directly
            if (string.IsNullOrEmpty(value))
            {
                RagStatusLookupId = null;
            }
        }
    }
}
