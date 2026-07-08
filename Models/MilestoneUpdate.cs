using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Compass.Models;

public class MilestoneUpdate
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int MilestoneId { get; set; }

    [ForeignKey(nameof(MilestoneId))]
    public Milestone Milestone { get; set; } = null!;

    [Required]
    public string UpdateDetails { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? PreviousStatus { get; set; }

    [MaxLength(20)]
    public string? NewStatus { get; set; }

    [Range(0, 100)]
    public int? PreviousProgress { get; set; }

    [Range(0, 100)]
    public int? NewProgress { get; set; }

    public int? PreviousRagStatusLookupId { get; set; }

    public int? NewRagStatusLookupId { get; set; }

    [Required]
    [MaxLength(255)]
    public string UpdatedByEmail { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? UpdatedByName { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

