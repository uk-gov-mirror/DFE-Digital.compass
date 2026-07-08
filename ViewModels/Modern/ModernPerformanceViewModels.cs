using Compass.Models;

namespace Compass.ViewModels.Modern;

public class ModernPerformanceCommissionsViewModel
{
    /// <summary>open | closed — which tab is active on the index page.</summary>
    public string ActiveTab { get; set; } = "open";

    public List<ModernPerformanceCommissionListRow> OpenForSubmission { get; set; } = new();
    public List<ModernPerformanceCommissionListRow> ClosedOrOther { get; set; } = new();
}

public class ModernPerformanceCommissionListRow
{
    public Commission Commission { get; set; } = new();
    public bool IsOpenForSubmission { get; set; }
    public bool IsPastDue { get; set; }
    public bool IsNotYetOpen { get; set; }
}

/// <summary>Product scope tabs on commission detail, submit, submission, and bulk flows (mine / business area / directorate / all).</summary>
public class PerformanceCommissionScopeNavViewModel
{
    public int CommissionId { get; set; }
    /// <summary>mine | businessarea | directorate | all</summary>
    public string Tab { get; set; } = "mine";
    public string? BusinessArea { get; set; }
    public string? Directorate { get; set; }
    public string? Search { get; set; }
}

public class ModernPerformanceCommissionDetailViewModel
{
    public Commission Commission { get; set; } = new();
    /// <summary>mine | businessarea | directorate | all</summary>
    public string Tab { get; set; } = "mine";

    public List<CommissionSubmissionStatusViewModel> Rows { get; set; } = new();

    /// <summary>
    /// When tab is business area or directorate, products grouped under headings; otherwise null and <see cref="Rows"/> is flat.
    /// </summary>
    public List<CommissionProductGroupSection>? GroupedProductRows { get; set; }

    /// <summary>Products in commission scope before search / dropdown filters.</summary>
    public int ScopeProductCount { get; set; }

    /// <summary>GET filter: FIPS business area category (or Unassigned).</summary>
    public string? BusinessAreaFilter { get; set; }

    /// <summary>GET filter: directorate category (or Unassigned).</summary>
    public string? DirectorateFilter { get; set; }

    /// <summary>GET filter: title / FIPS id / document id substring.</summary>
    public string? Search { get; set; }

    public List<string> BusinessAreaOptions { get; set; } = new();
    public List<string> DirectorateOptions { get; set; } = new();

    public bool IsOpenForSubmission { get; set; }
    public bool IsPastDue { get; set; }
}

/// <summary>Grouped block on commission product list (business area or directorate tab).</summary>
public class CommissionProductGroupSection
{
    public string GroupHeading { get; set; } = "";
    public List<CommissionSubmissionStatusViewModel> Rows { get; set; } = new();
}

/// <summary>Read-only commission submission for modern performance UI.</summary>
public class ModernPerformanceSubmissionViewModel
{
    public Commission Commission { get; set; } = new();
    public ProductDto Product { get; set; } = new();
    public CommissionSubmission Submission { get; set; } = new();

    /// <summary>Breadcrumb / back link: commission list tab.</summary>
    public string ReturnTab { get; set; } = "mine";

    public List<ModernPerformanceSubmissionMetricRow> MetricRows { get; set; } = new();

    public bool IsPastDue { get; set; }

    /// <summary>Whether the user can unsubmit and edit (reporting window open, or delegated override).</summary>
    public bool CanReopen { get; set; }
}

public class ModernPerformanceSubmissionMetricRow
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Identifier { get; set; }
    public string ValueDisplay { get; set; } = "";
    public bool IsComplete { get; set; }
    public bool IsNotCaptured { get; set; }
    public string? NotCapturedReason { get; set; }
    public string? ReasonForDifference { get; set; }
}

/// <summary>Enter or edit commission metrics for one product in the modern performance UI.</summary>
public class ModernPerformanceCommissionSubmitViewModel
{
    public Commission Commission { get; set; } = new();
    public ProductDto Product { get; set; } = new();
    public CommissionSubmission Submission { get; set; } = new();

    public List<CommissionMetricValue> MetricValues { get; set; } = new();

    /// <summary>Breadcrumb / back link: commission list tab.</summary>
    public string ReturnTab { get; set; } = "mine";

    public bool IsReadOnly { get; set; }
    public bool IsPastDue { get; set; }
    public bool HasNoProductTypes { get; set; }
}

public class ModernPerformanceBulkUploadViewModel
{
    public int CommissionId { get; set; }
    public string CommissionName { get; set; } = "";
    public string Tab { get; set; } = "mine";
}

public class ModernPerformanceBulkVerifyViewModel
{
    public int CommissionId { get; set; }
    public string CommissionName { get; set; } = "";
    /// <summary>Scope tab used for this upload (mine, businessarea, directorate, all).</summary>
    public string Tab { get; set; } = "mine";
    public string Token { get; set; } = "";
    public List<ModernPerformanceBulkVerifyLine> Lines { get; set; } = new();
    public bool CanApply { get; set; }
    public int StagedRowCount { get; set; }
}

public class ModernPerformanceBulkVerifyLine
{
    public int RowNumber { get; set; }
    public string DocumentId { get; set; } = "";
    public string? ProductTitle { get; set; }
    public List<string> Errors { get; } = new();
}

