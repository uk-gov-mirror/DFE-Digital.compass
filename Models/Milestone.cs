using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Compass.Models;

public class Milestone
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int? ObjectiveId { get; set; }

    [ForeignKey(nameof(ObjectiveId))]
    public Objective? Objective { get; set; }

    public int? ProjectId { get; set; }

    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }

    [MaxLength(50)]
    public string? FipsId { get; set; }

    [MaxLength(100)]
    public string? ProductDocumentId { get; set; } // Product DocumentID from CMS (primary identifier)

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [MaxLength(100)]
    public string? BusinessArea { get; set; }

    public int? OwnerUserId { get; set; }

    [ForeignKey(nameof(OwnerUserId))]
    public User? OwnerUser { get; set; }

    [MaxLength(255)]
    public string? OwnerEmail { get; set; }

    public DateTime? BaselineDueDate { get; set; }

    [Required]
    public DateTime DueDate { get; set; }

    public DateTime? ActualDate { get; set; }

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "not_started"; // not_started, on_track, at_risk, delayed, complete, cancelled

    public int? RagStatusLookupId { get; set; }

    [ForeignKey(nameof(RagStatusLookupId))]
    public RagStatusLookup? RagStatusLookup { get; set; }

    [Range(0, 100)]
    public int? ProgressPercent { get; set; }

    [MaxLength(100)]
    public string? ExternalRef { get; set; }

    public string? Notes { get; set; }

    public bool IsDeleted { get; set; } = false;

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<MilestoneAction> MilestoneActions { get; set; } = new List<MilestoneAction>();
    public ICollection<MilestoneRisk> MilestoneRisks { get; set; } = new List<MilestoneRisk>();
    public ICollection<MilestoneIssue> MilestoneIssues { get; set; } = new List<MilestoneIssue>();

    [NotMapped]
    public ICollection<Dependency> DependenciesAsSource { get; set; } = new List<Dependency>();

    [NotMapped]
    public ICollection<Dependency> DependenciesAsTarget { get; set; } = new List<Dependency>();

    public ICollection<MilestoneUpdate> MilestoneUpdates { get; set; } = new List<MilestoneUpdate>();
    public ICollection<Kpi> Kpis { get; set; } = new List<Kpi>();
}

