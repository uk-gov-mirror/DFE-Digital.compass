using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Compass.Models;

/// <summary>Weekly delivery confidence updates for work items in weekly reporting scope.</summary>
public class ProjectWeeklyWorkUpdate
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int ProjectId { get; set; }

    [ForeignKey(nameof(ProjectId))]
    public Project Project { get; set; } = null!;

    [Required]
    public int IsoYear { get; set; }

    [Required]
    [Range(1, 53)]
    public int IsoWeek { get; set; }

    [Required]
    public DateTime WeekStartDate { get; set; }

    [Required]
    public DateTime WeekEndDate { get; set; }

    [Required]
    [MaxLength(4000)]
    public string Narrative { get; set; } = string.Empty;

    public string? CreatedByEntraId { get; set; }
    public string? CreatedByName { get; set; }
    public string? CreatedByEmail { get; set; }
    public int? CreatedByUserId { get; set; }

    [ForeignKey(nameof(CreatedByUserId))]
    public User? CreatedByUser { get; set; }

    public int? UpdatedByUserId { get; set; }

    [ForeignKey(nameof(UpdatedByUserId))]
    public User? UpdatedByUser { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? WeeklyPermFte { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? WeeklyMspFte { get; set; }

    [MaxLength(4000)]
    public string? PeopleNarrative { get; set; }

    public int? DraftRagStatusLookupId { get; set; }

    [ForeignKey(nameof(DraftRagStatusLookupId))]
    public RagStatusLookup? DraftRagStatusLookup { get; set; }

    [MaxLength(4000)]
    public string? DraftRagJustification { get; set; }

    [MaxLength(4000)]
    public string? DraftPathToGreen { get; set; }
}
