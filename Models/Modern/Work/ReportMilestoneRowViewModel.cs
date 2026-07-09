namespace Compass.Models.Modern.Work;

/// <summary>Milestone row for monthly/weekly update forms and read-only views.</summary>
public class ReportMilestoneRowViewModel
{
    public int MilestoneId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime DueDate { get; set; }
    public string Status { get; set; } = "not_started";
    public int? RagStatusId { get; set; }
    public string? RagName { get; set; }
    public string? UpdateNote { get; set; }
}
