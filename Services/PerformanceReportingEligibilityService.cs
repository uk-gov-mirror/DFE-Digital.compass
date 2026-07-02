using Compass.Data;
using Compass.Models;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services;

public class PerformanceReportingEligibilityService : IPerformanceReportingEligibilityService
{
    private readonly CompassDbContext _context;

    public PerformanceReportingEligibilityService(CompassDbContext context)
    {
        _context = context;
    }

    public async Task<PerformanceReportingEligibilityCache> LoadEligibilityCacheAsync()
    {
        var cache = new PerformanceReportingEligibilityCache();
        
        // Load all active configurations sequentially (DbContext is not thread-safe)
        cache.PeriodExclusions = await _context.PerformanceReportingPeriodExclusions
            .Where(e => e.IsActive)
            .ToListAsync();
            
        cache.BusinessAreaConfigs = await _context.PerformanceReportingBusinessAreaConfigs
            .Where(c => c.IsActive)
            .ToListAsync();
            
        cache.ProductExclusions = await _context.PerformanceReportingProductExclusions
            .Where(e => e.IsActive)
            .ToListAsync();
        
        return cache;
    }

    public bool IsReportingRequired(string fipsId, string? businessArea, int year, int month, PerformanceReportingEligibilityCache cache)
    {
        // Step 1: Check if period is excluded at base level
        var periodExcluded = cache.PeriodExclusions.Any(e => e.Year == year && e.Month == month);
        
        // Step 2: Check if business area is configured to report (overrides period exclusion)
        var businessAreaReporting = false;
        if (!string.IsNullOrEmpty(businessArea))
        {
            businessAreaReporting = cache.BusinessAreaConfigs.Any(c => 
                c.BusinessAreaName == businessArea &&
                // Check if the period falls within the applicable range
                (c.ApplicableFromYear < year || 
                 (c.ApplicableFromYear == year && c.ApplicableFromMonth <= month)) &&
                (!c.ApplicableUntilYear.HasValue || 
                 c.ApplicableUntilYear.Value > year || 
                 (c.ApplicableUntilYear.Value == year && c.ApplicableUntilMonth >= month)));
        }
        
        // If period is excluded and business area is NOT configured to report, no reporting required
        if (periodExcluded && !businessAreaReporting)
        {
            return false;
        }
        
        // Step 3: Check if product is specifically excluded (final override)
        if (IsProductExcluded(null, fipsId, year, month, cache))
            return false;
        
        // If we get here, reporting is required
        return true;
    }

    public bool IsReportingRequired(
        string? productDocumentId,
        string? fipsId,
        string? businessArea,
        int year,
        int month,
        PerformanceReportingEligibilityCache cache)
    {
        if (!string.IsNullOrWhiteSpace(fipsId))
            return IsReportingRequired(fipsId, businessArea, year, month, cache);

        if (IsProductExcluded(productDocumentId, null, year, month, cache))
            return false;

        var periodExcluded = cache.PeriodExclusions.Any(e => e.Year == year && e.Month == month);
        if (periodExcluded)
        {
            var businessAreaReporting = false;
            if (!string.IsNullOrEmpty(businessArea))
            {
                businessAreaReporting = cache.BusinessAreaConfigs.Any(c =>
                    c.BusinessAreaName == businessArea &&
                    (c.ApplicableFromYear < year ||
                     (c.ApplicableFromYear == year && c.ApplicableFromMonth <= month)) &&
                    (!c.ApplicableUntilYear.HasValue ||
                     c.ApplicableUntilYear.Value > year ||
                     (c.ApplicableUntilYear.Value == year && c.ApplicableUntilMonth >= month)));
            }

            if (!businessAreaReporting)
                return false;
        }

        return true;
    }

    public async Task<bool> IsReportingRequiredAsync(string fipsId, string? businessArea, int year, int month)
    {
        var cache = await LoadEligibilityCacheAsync();
        return IsReportingRequired(fipsId, businessArea, year, month, cache);
    }

    public bool IsProductExcluded(
        string? productDocumentId,
        string? fipsId,
        int year,
        int month,
        PerformanceReportingEligibilityCache cache,
        string? cmdbSysId = null) =>
        cache.ProductExclusions.Any(e =>
            ProductExclusionMatches(e, productDocumentId, fipsId, cmdbSysId) &&
            IsWithinExclusionRange(year, month, e));

    public bool IsProductExcludedForCommission(
        ProductDto product,
        Commission commission,
        PerformanceReportingEligibilityCache cache)
    {
        var rangeStart = new DateTime(commission.StartDate.Year, commission.StartDate.Month, 1);
        // Commissions are often submitted after EndDate; exclusions effective during the open window must apply too.
        var reportingEnd = commission.DueDate > commission.EndDate ? commission.DueDate : commission.EndDate;
        var rangeEnd = new DateTime(reportingEnd.Year, reportingEnd.Month, 1);
        if (rangeEnd < rangeStart)
            rangeEnd = rangeStart;

        for (var cursor = rangeStart; cursor <= rangeEnd; cursor = cursor.AddMonths(1))
        {
            if (IsProductExcluded(
                    product.DocumentId,
                    product.FipsId,
                    cursor.Year,
                    cursor.Month,
                    cache,
                    product.CmdbSysId))
                return true;
        }

        return false;
    }

    public async Task<bool> IsPeriodExcludedAsync(int year, int month)
    {
        var exclusion = await _context.PerformanceReportingPeriodExclusions
            .FirstOrDefaultAsync(e => e.Year == year && e.Month == month && e.IsActive);
        
        return exclusion != null;
    }

    public async Task<bool> IsBusinessAreaReportingAsync(string businessArea, int year, int month)
    {
        var config = await _context.PerformanceReportingBusinessAreaConfigs
            .Where(c => c.BusinessAreaName == businessArea && c.IsActive)
            .FirstOrDefaultAsync(c => 
                // Check if the period falls within the applicable range
                (c.ApplicableFromYear < year || 
                 (c.ApplicableFromYear == year && c.ApplicableFromMonth <= month)) &&
                (!c.ApplicableUntilYear.HasValue || 
                 c.ApplicableUntilYear.Value > year || 
                 (c.ApplicableUntilYear.Value == year && c.ApplicableUntilMonth >= month)));
        
        return config != null;
    }

    public async Task<bool> IsProductExcludedAsync(string fipsId, int year, int month)
    {
        var cache = await LoadEligibilityCacheAsync();
        return IsProductExcluded(null, fipsId, year, month, cache);
    }

    private static bool ProductExclusionMatches(
        PerformanceReportingProductExclusion exclusion,
        string? productDocumentId,
        string? fipsId,
        string? cmdbSysId = null)
    {
        if (!string.IsNullOrWhiteSpace(productDocumentId) &&
            string.Equals(exclusion.ProductDocumentId, productDocumentId.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(fipsId) &&
            !string.IsNullOrWhiteSpace(exclusion.FipsId) &&
            string.Equals(exclusion.FipsId, fipsId.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(cmdbSysId) &&
            string.Equals(exclusion.ProductDocumentId, cmdbSysId.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        // Legacy rows may only have FIPS id populated on the exclusion record.
        if (!string.IsNullOrWhiteSpace(fipsId) &&
            string.Equals(exclusion.ProductDocumentId, fipsId.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsWithinExclusionRange(int year, int month, PerformanceReportingProductExclusion exclusion) =>
        (exclusion.ExclusionFromYear < year ||
         (exclusion.ExclusionFromYear == year && exclusion.ExclusionFromMonth <= month)) &&
        (!exclusion.ExclusionUntilYear.HasValue ||
         exclusion.ExclusionUntilYear.Value > year ||
         (exclusion.ExclusionUntilYear.Value == year && exclusion.ExclusionUntilMonth >= month));

    public async Task<(int Year, int Month)?> FindNextActiveReportingPeriodAsync(int fromYear, int fromMonth, string? businessArea = null, PerformanceReportingEligibilityCache? cache = null)
    {
        cache ??= await LoadEligibilityCacheAsync();
        
        // Start from the month after the given period
        var currentDate = new DateTime(fromYear, fromMonth, 1).AddMonths(1);
        var maxDate = currentDate.AddYears(2); // Look ahead up to 2 years
        
        while (currentDate <= maxDate)
        {
            var year = currentDate.Year;
            var month = currentDate.Month;
            
            // Check if this period is excluded
            var periodExcluded = cache.PeriodExclusions.Any(e => e.Year == year && e.Month == month);
            
            // Check if business area overrides the exclusion
            var businessAreaReporting = false;
            if (!string.IsNullOrEmpty(businessArea))
            {
                businessAreaReporting = cache.BusinessAreaConfigs.Any(c => 
                    c.BusinessAreaName == businessArea &&
                    (c.ApplicableFromYear < year || 
                     (c.ApplicableFromYear == year && c.ApplicableFromMonth <= month)) &&
                    (!c.ApplicableUntilYear.HasValue || 
                     c.ApplicableUntilYear.Value > year || 
                     (c.ApplicableUntilYear.Value == year && c.ApplicableUntilMonth >= month)));
            }
            
            // If period is not excluded, or business area overrides it, this is an active period
            if (!periodExcluded || businessAreaReporting)
            {
                return (year, month);
            }
            
            currentDate = currentDate.AddMonths(1);
        }
        
        return null; // No active period found within 2 years
    }
}

