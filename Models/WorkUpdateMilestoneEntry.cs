using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Compass.Models;

/// <summary>Milestone status and RAG snapshot captured on a monthly or weekly work update.</summary>
public class WorkUpdateMilestoneEntry
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int MilestoneId { get; set; }

    [ForeignKey(nameof(MilestoneId))]
    public Milestone Milestone { get; set; } = null!;

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "not_started";

    public int? RagStatusLookupId { get; set; }

    [ForeignKey(nameof(RagStatusLookupId))]
    public RagStatusLookup? RagStatusLookup { get; set; }

    [MaxLength(4000)]
    public string? UpdateNote { get; set; }

    public int? ProjectMonthlyUpdateId { get; set; }

    [ForeignKey(nameof(ProjectMonthlyUpdateId))]
    public ProjectMonthlyUpdate? ProjectMonthlyUpdate { get; set; }

    public int? ProjectWeeklyWorkUpdateId { get; set; }

    [ForeignKey(nameof(ProjectWeeklyWorkUpdateId))]
    public ProjectWeeklyWorkUpdate? ProjectWeeklyWorkUpdate { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
