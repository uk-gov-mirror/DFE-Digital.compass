using Compass.Models;

namespace Compass.Services;

public interface IPerformanceReportingEligibilityService
{
    /// <summary>
    /// Determines if a product is required to report for a given period.
    /// Implements the hierarchy: Period exclusion (base) -> Business area config (override) -> Product exclusion (final override)
    /// </summary>
    Task<bool> IsReportingRequiredAsync(string fipsId, string? businessArea, int year, int month);
    
    /// <summary>
    /// Checks if a period is excluded at the base level
    /// </summary>
    Task<bool> IsPeriodExcludedAsync(int year, int month);
    
    /// <summary>
    /// Checks if a business area is configured to report for a given period
    /// </summary>
    Task<bool> IsBusinessAreaReportingAsync(string businessArea, int year, int month);
    
    /// <summary>
    /// Checks if a specific product is excluded from reporting
    /// </summary>
    Task<bool> IsProductExcludedAsync(string fipsId, int year, int month);

    /// <summary>
    /// Checks if a product is excluded for a calendar month using document id and/or FIPS id.
    /// </summary>
    bool IsProductExcluded(
        string? productDocumentId,
        string? fipsId,
        int year,
        int month,
        PerformanceReportingEligibilityCache cache,
        string? cmdbSysId = null);

    /// <summary>
    /// Checks if a product is excluded for any month from the commission reporting period through its due date.
    /// </summary>
    bool IsProductExcludedForCommission(
        ProductDto product,
        Commission commission,
        PerformanceReportingEligibilityCache cache);
    
    /// <summary>
    /// Loads all eligibility configurations into memory for efficient batch checking
    /// </summary>
    Task<PerformanceReportingEligibilityCache> LoadEligibilityCacheAsync();
    
    /// <summary>
    /// Determines if a product is required to report using pre-loaded cache (much faster for batch operations)
    /// </summary>
    bool IsReportingRequired(string fipsId, string? businessArea, int year, int month, PerformanceReportingEligibilityCache cache);

    /// <summary>
    /// Determines if a product is required to report using document id and/or FIPS id.
    /// </summary>
    bool IsReportingRequired(
        string? productDocumentId,
        string? fipsId,
        string? businessArea,
        int year,
        int month,
        PerformanceReportingEligibilityCache cache);
    
    /// <summary>
    /// Finds the next active reporting period after the given year/month
    /// </summary>
    Task<(int Year, int Month)?> FindNextActiveReportingPeriodAsync(int fromYear, int fromMonth, string? businessArea = null, PerformanceReportingEligibilityCache? cache = null);
}

public class PerformanceReportingEligibilityCache
{
    public List<PerformanceReportingPeriodExclusion> PeriodExclusions { get; set; } = new();
    public List<PerformanceReportingBusinessAreaConfig> BusinessAreaConfigs { get; set; } = new();
    public List<PerformanceReportingProductExclusion> ProductExclusions { get; set; } = new();
}

