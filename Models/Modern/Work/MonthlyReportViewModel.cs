namespace Compass.Models.Modern.Work;

public class MonthlyReportViewModel
{
    public int WorkItemId { get; set; }
    public string WorkItemTitle { get; set; } = string.Empty;
    public string? WorkItemReference { get; set; }

    public int Year { get; set; }
    public int Month { get; set; }
    public string PeriodLabel => new DateTime(Year, Month, 1).ToString("MMMM yyyy");

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
    public DateTime CloseDate { get; set; }
    public bool CanUnsubmit { get; set; }
    public bool IsPastCloseDate { get; set; }

    /// <summary>When set, submission window from Admin work reporting (explicit period).</summary>
    public DateTime? SubmissionOpens { get; set; }

    /// <summary>When set, last day of the submission window from Admin work reporting.</summary>
    public DateTime? SubmissionCloses { get; set; }

    /// <summary>Human-readable due window rule from admin/config.</summary>
    public string DueRuleDescription { get; set; } = "";

    /// <summary>Configured explicit reporting period dates are present for this month.</summary>
    public bool UsesExplicitReportingPeriod { get; set; }

    /// <summary>Whether this page can create/edit submission for this reporting month (submission window).</summary>
    public bool CanEditMonthlySubmission { get; set; }

    public List<RagStatus> RagStatuses { get; set; } = new();

    /// <summary>In-progress milestones for status and RAG updates during this reporting period.</summary>
    public List<ReportMilestoneRowViewModel> Milestones { get; set; } = new();

    /// <summary>Submitted return for the calendar month before <see cref="Year"/>/<see cref="Month"/>, when one exists.</summary>
    public MonthlyReportPreviousSubmission? PreviousMonthSubmission { get; set; }
}

/// <summary>Read-only snapshot of a prior submitted monthly return (shown in the “Last month” panel).</summary>
public class MonthlyReportPreviousSubmission
{
    public int Year { get; set; }
    public int Month { get; set; }
    public string PeriodLabel => new DateTime(Year, Month, 1).ToString("MMMM yyyy");

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
