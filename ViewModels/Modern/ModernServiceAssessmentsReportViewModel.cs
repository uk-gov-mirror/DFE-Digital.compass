namespace Compass.ViewModels.Modern;

/// <summary>Data for <c>/modern/reporting/assessments</c> — service assessment (SAS) published summary and standard actions.</summary>
public class ModernServiceAssessmentsReportViewModel
{
    public bool SummaryLoadFailed { get; set; }
    public bool ActionsByStandardLoadFailed { get; set; }

    public int TotalAssessments { get; set; }
    public IReadOnlyList<KeyValuePair<string, int>> ByOutcome { get; set; } = Array.Empty<KeyValuePair<string, int>>();
    public IReadOnlyList<KeyValuePair<string, int>> ByType { get; set; } = Array.Empty<KeyValuePair<string, int>>();
    public IReadOnlyList<KeyValuePair<string, int>> ByPhase { get; set; } = Array.Empty<KeyValuePair<string, int>>();
    public IReadOnlyList<KeyValuePair<string, int>> ByYear { get; set; } = Array.Empty<KeyValuePair<string, int>>();

    public IReadOnlyList<SasAssessmentListItem> AssessmentRows { get; set; } = Array.Empty<SasAssessmentListItem>();

    /// <summary>Same as <see cref="AssessmentRows"/> but grouped for display (by assessment type).</summary>
    public IReadOnlyList<SasAssessmentsTypeGroup> AssessmentsGroupedByType { get; set; } = Array.Empty<SasAssessmentsTypeGroup>();

    public IReadOnlyList<SasStandardActionRow> StandardActionRows { get; set; } = Array.Empty<SasStandardActionRow>();

    /// <summary>True when actions are split by parent Service assessment RAG; includes published cohort + <c>actions/all</c> join.</summary>
    public bool ServiceStandardOutcomeBreakdownAvailable { get; set; }

    /// <summary>Base URL for published report pages, e.g. <c>https://service-assessments.education.gov.uk/reports/report</c> (no trailing slash).</summary>
    public string SasReportBaseUrl { get; set; } = "https://service-assessments.education.gov.uk/reports/report";

    /// <summary>Standards analysis tab (GSS points 1–14).</summary>
    public SasStandardsAnalysisVm StandardsAnalysis { get; set; } = new();

    /// <summary>Assessor analysis tab from SAS <c>/assessors/summary</c>.</summary>
    public SasAssessorAnalysisVm AssessorAnalysis { get; set; } = new();
}

public class SasAssessorAnalysisVm
{
    public bool LoadFailed { get; set; }
    public int TotalAssessors { get; set; }
    public int TotalPanelAssignments { get; set; }
    public int PublishedAssignments { get; set; }
    public IReadOnlyList<KeyValuePair<string, int>> ByPrimaryRole { get; set; } = Array.Empty<KeyValuePair<string, int>>();
    public IReadOnlyList<KeyValuePair<string, int>> ByOutcome { get; set; } = Array.Empty<KeyValuePair<string, int>>();
    public IReadOnlyList<KeyValuePair<string, int>> ByType { get; set; } = Array.Empty<KeyValuePair<string, int>>();
    public IReadOnlyList<KeyValuePair<string, int>> ByStatus { get; set; } = Array.Empty<KeyValuePair<string, int>>();
    public IReadOnlyList<string> PeriodLabels { get; set; } = Array.Empty<string>();
    public IReadOnlyList<int> PeriodTotals { get; set; } = Array.Empty<int>();
    /// <summary>League table slices: All time, then calendar years (newest first).</summary>
    public IReadOnlyList<SasAssessorLeagueYearTabVm> LeagueYearTabs { get; set; } = Array.Empty<SasAssessorLeagueYearTabVm>();
    public IReadOnlyList<KeyValuePair<string, int>> TopRoles { get; set; } = Array.Empty<KeyValuePair<string, int>>();
}

public class SasAssessorLeagueYearTabVm
{
    /// <summary><c>all</c> for all time, otherwise the calendar year as a string (e.g. <c>2026</c>).</summary>
    public string Key { get; set; } = "all";
    public string Label { get; set; } = "All time";
    public bool IsDefault { get; set; } = true;
    public IReadOnlyList<SasAssessorLeagueRowVm> Rows { get; set; } = Array.Empty<SasAssessorLeagueRowVm>();
}

public class SasAssessorLeagueRowVm
{
    public int Rank { get; set; }
    public int AssessorId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public string PrimaryRole { get; set; } = "";
    public int ServiceAssessmentCount { get; set; }
    public int PeerReviewCount { get; set; }
    public int PanelCount { get; set; }
    public int PublishedCount { get; set; }
    public int Red { get; set; }
    public int Amber { get; set; }
    public int Green { get; set; }
    /// <summary>Panel assignments in the league table period (for drill-down).</summary>
    public IReadOnlyList<SasAssessorLeagueAssignmentVm> Assignments { get; set; } = Array.Empty<SasAssessorLeagueAssignmentVm>();
}

public class SasAssessorLeagueAssignmentVm
{
    public int AssessmentId { get; set; }
    public string Name { get; set; } = "";
    public string PanelRole { get; set; } = "";
    public string Type { get; set; } = "";
    public string Phase { get; set; } = "";
    public string Status { get; set; } = "";
    public string Outcome { get; set; } = "";
    public DateTime? AssessmentDateTime { get; set; }
}

public class SasStandardsAnalysisVm
{
    public bool OutcomeBreakdownAvailable { get; set; }
    public int TrendComparisonMonths { get; set; } = 6;
    public IReadOnlyList<SasStandardAnalysisPointVm> StandardPoints { get; set; } = Array.Empty<SasStandardAnalysisPointVm>();
    public IReadOnlyList<SasStandardAnalysisPointVm> IncreasingStandards { get; set; } = Array.Empty<SasStandardAnalysisPointVm>();
    public IReadOnlyList<SasRepeatedActionThemeVm> TopRepeatedThemes { get; set; } = Array.Empty<SasRepeatedActionThemeVm>();
    /// <summary>Last six UK financial quarters (oldest first), e.g. 2024–25 Q4.</summary>
    public IReadOnlyList<string> TrendPeriodLabels { get; set; } = Array.Empty<string>();
    public IReadOnlyList<int> TrendPeriodTotals { get; set; } = Array.Empty<int>();
    public IReadOnlyList<string> TrendYears { get; set; } = Array.Empty<string>();
    public IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>> ActionsByStandardByYear { get; set; } =
        new Dictionary<int, IReadOnlyDictionary<string, int>>();
}

public class SasStandardAnalysisPointVm
{
    public int Standard { get; set; }
    public string StandardTitle { get; set; } = "";
    public int ActionCount { get; set; }
    public int AssessmentCount { get; set; }
    public int ActionsRecentPeriod { get; set; }
    public int ActionsPriorPeriod { get; set; }
    public int AssessmentsRecentPeriod { get; set; }
    public int AssessmentsPriorPeriod { get; set; }
    public int ActionsWithoutDate { get; set; }
    public string TrendLabel { get; set; } = "Stable";
    public IReadOnlyDictionary<string, int> ActionsByPeriod { get; set; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> ActionsByYear { get; set; } = new Dictionary<string, int>();
    public IReadOnlyList<KeyValuePair<string, int>> ActionsByStatus { get; set; } = Array.Empty<KeyValuePair<string, int>>();
    public IReadOnlyDictionary<string, int> ActionsByOutcome { get; set; } = new Dictionary<string, int>();
    public double? PctOnAmberOrRed { get; set; }
    public int ActionsFromRed { get; set; }
    public int ActionsFromAmber { get; set; }
    public int ActionsFromGreen { get; set; }
    public int ActionsFromOther { get; set; }
    public IReadOnlyList<SasRepeatedActionThemeVm> RepeatedThemes { get; set; } = Array.Empty<SasRepeatedActionThemeVm>();
    public IReadOnlyList<SasStandardActionDetailVm> Actions { get; set; } = Array.Empty<SasStandardActionDetailVm>();
}

public class SasRepeatedActionThemeVm
{
    public int Standard { get; set; }
    public string Snippet { get; set; } = "";
    public string FullText { get; set; } = "";
    public int OccurrenceCount { get; set; }
    public int AssessmentCount { get; set; }
}

public class SasReportPanelViewModel
{
    public string PanelKey { get; set; } = "";
    public string HeadingId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Hint { get; set; }
    public string CanvasId { get; set; } = "";
    public string ChartAriaLabel { get; set; } = "";
    public bool DoughnutWrap { get; set; }
    public Microsoft.AspNetCore.Html.IHtmlContent? TableContent { get; set; }
}

public class SasStandardActionDetailVm
{
    public int AssessmentId { get; set; }
    public string AssessmentName { get; set; } = "";
    public string? Outcome { get; set; }
    public string? Status { get; set; }
    public string? ActionId { get; set; }
    public int Standard { get; set; }
    public DateTime? Created { get; set; }
    public string Comment { get; set; } = "";
}

public class SasAssessmentsTypeGroup
{
    public string TypeLabel { get; set; } = "Other";
    public IReadOnlyList<SasAssessmentListItem> Items { get; set; } = Array.Empty<SasAssessmentListItem>();
}

public class SasAssessmentListItem
{
    public int AssessmentId { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Phase { get; set; }
    public string? Outcome { get; set; }
    public string? Portfolio { get; set; }
    public DateTime? AssessmentDate { get; set; }
}

public class SasStandardActionRow
{
    public int Standard { get; set; }
    public int ActionCount { get; set; }
    public int AssessmentCount { get; set; }

    public int ActionsFromRedOutcome { get; set; }
    public int ActionsFromAmberOutcome { get; set; }
    public int ActionsFromGreenOutcome { get; set; }
    public int ActionsFromOtherOutcome { get; set; }

    /// <summary>0–100: share of this standard’s actions on assessments with Amber or Red overall outcome. Null in legacy (aggregate-only) mode.</summary>
    public double? PctOnAmberOrRed { get; set; }

    /// <summary>1 = highest concern by Red/Amber share (when outcome breakdown available); otherwise by standard number.</summary>
    public int MostProblematicRank { get; set; }
}
