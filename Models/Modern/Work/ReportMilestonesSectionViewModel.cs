using Compass.Models;

namespace Compass.Models.Modern.Work;

public class MilestoneRagFieldViewModel
{
    public string FieldName { get; set; } = "ragStatusLookupId";
    public string FieldNamePrefix { get; set; } = "milestone";
    public int? SelectedRagStatusId { get; set; }
    public List<RagStatus> RagStatuses { get; set; } = new();
}

public class ReportMilestonesSectionViewModel
{
    public string SectionHeading { get; set; } = "Milestones";
    public string SectionId { get; set; } = "report-milestones-heading";
    public bool IsReadOnly { get; set; }
    public List<ReportMilestoneRowViewModel> Milestones { get; set; } = new();
    public List<RagStatus> RagStatuses { get; set; } = new();
}
