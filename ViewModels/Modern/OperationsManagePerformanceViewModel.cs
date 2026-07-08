namespace Compass.ViewModels.Modern;

public class OperationsManagePerformanceViewModel
{
    public IReadOnlyList<CommissionPickerOption> CommissionOptions { get; init; } = Array.Empty<CommissionPickerOption>();
    public int? SelectedCommissionId { get; init; }
    public CommissionSummaryVm? Commission { get; init; }

    /// <summary>Products in catalogue scope for this commission (phase/type rules).</summary>
    public int EligibleProductCount { get; init; }

    public int SubmittedCount { get; init; }
    public int LateCount { get; init; }
    public int InProgressCount { get; init; }
    public int NotStartedCount { get; init; }

    /// <summary>Submitted + Late (final returns).</summary>
    public int ActualReturnCount => SubmittedCount + LateCount;

    public decimal ReturnRatePercent { get; init; }

    public decimal MetricCompletionPercent { get; init; }
    public int CompletedMetricCells { get; init; }
    public int ApplicableMetricCells { get; init; }

    /// <summary>Open, Upcoming, or Closed relative to commission dates.</summary>
    public string SubmissionWindowPhase { get; init; } = "";

    /// <summary>Days until due date (negative if past due).</summary>
    public int? DaysUntilDue { get; init; }

    public IReadOnlyList<string> OverviewLines { get; init; } = Array.Empty<string>();

    public IReadOnlyList<OpsPerfOrgRow> BusinessAreaRows { get; init; } = Array.Empty<OpsPerfOrgRow>();
    public IReadOnlyList<OpsPerfOrgRow> DirectorateRows { get; init; } = Array.Empty<OpsPerfOrgRow>();
    public IReadOnlyList<OpsPerfMetricRow> MetricRows { get; init; } = Array.Empty<OpsPerfMetricRow>();

    public string StatusDoughnutJson { get; init; } = "{}";
    public string BusinessAreaBarJson { get; init; } = "{}";
    public string MetricCompletionBarJson { get; init; } = "{}";
    public string SubmissionTimelineJson { get; init; } = "{}";

    public bool HasCommission { get; init; }
    public bool HasEligibleProducts { get; init; }
}

public class CommissionPickerOption
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public DateTime DueDate { get; init; }
    public bool IsActive { get; init; }
}

public class CommissionSummaryVm
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string? Quarter { get; init; }
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public DateTime OpenDate { get; init; }
    public DateTime DueDate { get; init; }
    public bool IsActive { get; init; }
}

public class OpsPerfOrgRow
{
    public string Name { get; init; } = "";
    public int PotentialSubmissions { get; init; }
    public int ActualSubmitted { get; init; }
    public int ActualLate { get; init; }
    public int InProgress { get; init; }
    public int NotStarted { get; init; }
    public int CompletedMetricCells { get; init; }
    public int ApplicableMetricCells { get; init; }

    public decimal CompletionPercent =>
        PotentialSubmissions <= 0
            ? 0
            : Math.Round(100m * (ActualSubmitted + ActualLate) / PotentialSubmissions, 1);

    public decimal MetricCompletionPercent =>
        ApplicableMetricCells <= 0
            ? 0
            : Math.Round(100m * CompletedMetricCells / ApplicableMetricCells, 1);
}

public class OpsPerfMetricRow
{
    public int MetricId { get; init; }
    public string Name { get; init; } = "";
    public int ApplicableProducts { get; init; }
    public int CompletedCount { get; init; }

    /// <summary>Products that have completed this metric (for drill-down on reporting pages).</summary>
    public IReadOnlyList<OpsPerfMetricProductRow> CompletedProducts { get; init; } = Array.Empty<OpsPerfMetricProductRow>();

    public decimal CompletionPercent =>
        ApplicableProducts <= 0
            ? 0
            : Math.Round(100m * CompletedCount / ApplicableProducts, 1);
}

/// <summary>Single product's reported value for a performance metric drill-down.</summary>
public class OpsPerfMetricProductRow
{
    public string ProductTitle { get; init; } = "";
    public string ProductDocumentId { get; init; } = "";
    public string BusinessArea { get; init; } = "";
    public string DisplayValue { get; init; } = "–";
    public string? NotCapturedReason { get; init; }
    public string? ReasonForDifference { get; init; }
}
