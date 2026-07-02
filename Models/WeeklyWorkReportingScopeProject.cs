using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Compass.Models;

/// <summary>Work item (project) included in the weekly reporting scope.</summary>
public class WeeklyWorkReportingScopeProject
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int ProjectId { get; set; }

    [ForeignKey(nameof(ProjectId))]
    public Project Project { get; set; } = null!;

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public string? AddedByEmail { get; set; }
}
