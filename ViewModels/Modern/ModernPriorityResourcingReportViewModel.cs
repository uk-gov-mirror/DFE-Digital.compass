using Compass.Models;

namespace Compass.ViewModels.Modern;

/// <summary>Monthly Perm/MSC totals from submitted returns, grouped by delivery priority and directorate.</summary>
public sealed class ModernPriorityResourcingReportViewModel
{
    public static readonly string[] PriorityOrder = ["Critical", "High", "Medium", "Low", "Not set"];

    public int ReportYear { get; set; }
    public int ReportMonth { get; set; }
    public string MonthName { get; set; } = "";

    public int MinReportYear { get; set; } = 2026;
    public int MaxReportYear { get; set; } = 2026;

    public int? FilterBusinessAreaId { get; set; }
    public int? FilterDirectorateId { get; set; }

    public List<BusinessAreaLookup> BusinessAreas { get; set; } = new();
    public List<Division> Directorates { get; set; } = new();

    public bool HasPreviousMonthNav { get; set; }
    public bool HasNextMonthNav { get; set; }
    public int? PreviousNavYear { get; set; }
    public int? PreviousNavMonth { get; set; }
    public int? NextNavYear { get; set; }
    public int? NextNavMonth { get; set; }

    public decimal TotalPermFte { get; set; }
    public decimal TotalMspFte { get; set; }
    public int SubmittedWorkItemCount { get; set; }

    public List<PriorityResourcingChartSection> Sections { get; set; } = new();
    public Dictionary<int, ResourcingWorkItemRow> WorkItemsById { get; set; } = new();
}

public sealed class PriorityResourcingChartSection
{
    public string Key { get; set; } = "";
    public int? DirectorateId { get; set; }
    public string Title { get; set; } = "";
    public List<PriorityResourcingBarPoint> Bars { get; set; } = new();
}

public sealed class PriorityResourcingBarPoint
{
    public string Priority { get; set; } = "";
    public decimal PermFte { get; set; }
    public decimal MspFte { get; set; }
    public List<int> PermWorkItemIds { get; set; } = new();
    public List<int> MspWorkItemIds { get; set; } = new();
    public List<int> AllWorkItemIds { get; set; } = new();
}
