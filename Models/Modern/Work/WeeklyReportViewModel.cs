namespace Compass.Models.Modern.Work;

public class WeeklyReportViewModel
{
    public int WorkItemId { get; set; }
    public string WorkItemTitle { get; set; } = string.Empty;
    public string? WorkItemReference { get; set; }

    public int IsoYear { get; set; }
    public int IsoWeek { get; set; }
    public string PeriodKey { get; set; } = string.Empty;
    public string PeriodLabel { get; set; } = string.Empty;

    public int? UpdateId { get; set; }
    public bool IsSubmitted { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public string? SubmittedByName { get; set; }

    public string? Narrative { get; set; }
    public string? PeopleNarrative { get; set; }
    public decimal? PermFte { get; set; }
    public decimal? MspFte { get; set; }

    public int? RagStatusId { get; set; }
    public string? RagJustification { get; set; }
    public string? PathToGreen { get; set; }

    public DateTime DueDate { get; set; }
    public bool CanUnsubmit { get; set; }

    public DateTime SubmissionOpens { get; set; }
    public DateTime SubmissionCloses { get; set; }
    public string DueRuleDescription { get; set; } = "";
    public bool CanEditWeeklySubmission { get; set; }

    public List<RagStatus> RagStatuses { get; set; } = new();

    public List<ReportMilestoneRowViewModel> Milestones { get; set; } = new();

    public WeeklyReportPreviousSubmission? PreviousWeekSubmission { get; set; }
}

public class WeeklyReportPreviousSubmission
{
    public int IsoYear { get; set; }
    public int IsoWeek { get; set; }
    public string PeriodLabel { get; set; } = string.Empty;
    public DateTime? SubmittedAt { get; set; }
    public string? SubmittedByName { get; set; }
    public string? Narrative { get; set; }
    public string? PeopleNarrative { get; set; }
    public decimal? PermFte { get; set; }
    public decimal? MspFte { get; set; }
    public string? RagName { get; set; }
    public string? RagCssClass { get; set; }
    public string? RagJustification { get; set; }
    public string? PathToGreen { get; set; }
    public bool IsGreenRag { get; set; }
}
