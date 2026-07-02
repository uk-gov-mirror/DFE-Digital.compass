using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Compass.Data;
using Compass.Models;
using Compass.Services;
using Microsoft.AspNetCore.Authorization;
using System.Text.Json;
using ClosedXML.Excel;
using System.IO;

namespace Compass.Controllers;

[Authorize]
public class ProductReportingController : Controller
{
    private readonly CompassDbContext _context;
    private readonly IProductsApiService _productsApiService;
    private readonly IReturnStatusService _returnStatusService;
    private readonly IPerformanceReportingEligibilityService _eligibilityService;
    private readonly ILogger<ProductReportingController> _logger;

    public ProductReportingController(
        CompassDbContext context,
        IProductsApiService productsApiService,
        IReturnStatusService returnStatusService,
        IPerformanceReportingEligibilityService eligibilityService,
        ILogger<ProductReportingController> logger)
    {
        _context = context;
        _productsApiService = productsApiService;
        _returnStatusService = returnStatusService;
        _eligibilityService = eligibilityService;
        _logger = logger;
    }

    // GET: ProductReporting/PerformanceMetrics
    public async Task<IActionResult> PerformanceMetrics(
        string view = "mine", 
        string search = "", 
        string phase = "", 
        string businessArea = "", 
        string reportingStatus = "",
        bool clearFilters = false)
    {
        // Handle guidance view - redirect to guidance page
        if (view == "guidance")
        {
            return RedirectToAction("Guidance", "ProductReporting");
        }

        // Handle clear filters
        if (clearFilters)
        {
            return RedirectToAction("PerformanceMetrics", new { view = view });
        }

        // Get the current user's email
        var userEmail = User.Identity?.Name;
        
        // Fetch user's products - from service_owner, product_manager, delivery_manager, and reporting_user
        var productsByServiceOwner = await _productsApiService.GetProductsByServiceOwnerAsync(userEmail);
        var productsByProductManager = await _productsApiService.GetProductsByProductManagerAsync(userEmail);
        var productsByDeliveryManager = await _productsApiService.GetProductsByDeliveryManagerAsync(userEmail);
        var productsByReportingUser = await _productsApiService.GetProductsByReportingUserAsync(userEmail);
        
        // Combine and deduplicate products (by FipsId)
        var userProducts = productsByServiceOwner
            .Concat(productsByProductManager)
            .Concat(productsByDeliveryManager)
            .Concat(productsByReportingUser)
            .GroupBy(p => p.FipsId)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => g.First())
            .ToList();
        
        // Get current reporting period (previous month)
        var now = DateTime.UtcNow;
        var currentYear = now.Month == 1 ? now.Year - 1 : now.Year;
        var currentMonth = now.Month == 1 ? 12 : now.Month - 1;
        
        // PERFORMANCE OPTIMIZATION: Load eligibility cache once upfront
        var eligibilityCache = await _eligibilityService.LoadEligibilityCacheAsync();
        
        // Get all returns for current period in one query
        var fipsIds = userProducts.Where(p => !string.IsNullOrEmpty(p.FipsId)).Select(p => p.FipsId).ToList();
        var userReturns = await _context.ProductReturns
            .Include(pr => pr.MetricValues)
            .Where(pr => fipsIds.Contains(pr.FipsId) && pr.Year == currentYear && pr.Month == currentMonth)
            .ToDictionaryAsync(pr => pr.FipsId, pr => pr);

        // Create view model for user's products (show ALL products user is responsible for)
        var userProductStatuses = new List<ProductReturnStatusViewModel>();
        foreach (var product in userProducts)
        {
            var vm = await CreateProductStatusViewModelAsync(product, userReturns, currentYear, currentMonth, userEmail, eligibilityCache);
            if (vm != null)
            {
                userProductStatuses.Add(vm);
            }
        }
        
        // Calculate counts for navigation badges
        // Tasks count: products that require reporting AND are Due or Late
        var tasksCount = userProductStatuses.Count(p => 
            p.IsReportingRequired && (p.Status == ReturnStatus.Due || p.Status == ReturnStatus.Late));
        var yourProductsCount = userProductStatuses.Count;
        
        // For "All products" view, get all eligible products
        List<ProductReturnStatusViewModel> allProductStatuses = new List<ProductReturnStatusViewModel>();
        int allProductsCount = 0;
        
        if (view == "all")
        {
            var allProducts = await _productsApiService.GetAllProductsAsync();
            // Filter to only Active products
            var activeProducts = allProducts
                .Where(p => p.State != null && p.State.Equals("Active", StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            var allFipsIds = activeProducts
                .Where(p => !string.IsNullOrEmpty(p.FipsId))
                .Select(p => p.FipsId!)
                .ToList();
            
            var allReturns = await _context.ProductReturns
                .Include(pr => pr.MetricValues)
                .Where(pr => allFipsIds.Contains(pr.FipsId) && pr.Year == currentYear && pr.Month == currentMonth)
                .ToDictionaryAsync(pr => pr.FipsId, pr => pr);
            
            foreach (var product in activeProducts)
            {
                var vm = await CreateProductStatusViewModelAsync(product, allReturns, currentYear, currentMonth, userEmail, eligibilityCache);
                if (vm != null)
                {
                    allProductStatuses.Add(vm);
                }
            }
        }
        
        // Note: allProductsCount will be 0 when not on "all" view - badge will show 0 or be hidden
        allProductsCount = allProductStatuses.Count;
        
        // Determine which data to show based on view
        List<ProductReturnStatusViewModel> displayData;
        if (view == "mine" || string.IsNullOrEmpty(view))
        {
            displayData = userProductStatuses;
        }
        else if (view == "watched")
        {
            // Watched products not yet implemented - show empty list
            displayData = new List<ProductReturnStatusViewModel>();
        }
        else if (view == "all")
        {
            displayData = allProductStatuses;
        }
        else
        {
            displayData = userProductStatuses;
        }

        // Apply filters
        if (!string.IsNullOrEmpty(search))
        {
            displayData = displayData.Where(p => 
                p.Product.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (p.Product.FipsId != null && p.Product.FipsId.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(p.Product.Phase) && p.Product.Phase.Contains(search, StringComparison.OrdinalIgnoreCase))
            ).ToList();
        }

        if (!string.IsNullOrEmpty(phase))
        {
            displayData = displayData.Where(p => 
                !string.IsNullOrEmpty(p.Product.Phase) && 
                p.Product.Phase.Equals(phase, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        if (!string.IsNullOrEmpty(businessArea))
        {
            displayData = displayData.Where(p => 
                p.Product.CategoryValues != null &&
                p.Product.CategoryValues.Any(cv => 
                    cv.CategoryType != null &&
                    cv.CategoryType.Name.Equals("Business area", StringComparison.OrdinalIgnoreCase) &&
                    cv.Name.Equals(businessArea, StringComparison.OrdinalIgnoreCase))
            ).ToList();
        }

        if (!string.IsNullOrEmpty(reportingStatus))
        {
            if (reportingStatus == "Due")
            {
                displayData = displayData.Where(p => p.Status == ReturnStatus.Due).ToList();
            }
            else if (reportingStatus == "Late")
            {
                displayData = displayData.Where(p => p.Status == ReturnStatus.Late).ToList();
            }
            else if (reportingStatus == "Submitted")
            {
                displayData = displayData.Where(p => p.Status == ReturnStatus.Submitted).ToList();
            }
            else if (reportingStatus == "Upcoming")
            {
                displayData = displayData.Where(p => p.Status == ReturnStatus.Upcoming).ToList();
            }
            else if (reportingStatus == "Required")
            {
                displayData = displayData.Where(p => p.IsReportingRequired).ToList();
            }
            else if (reportingStatus == "NotRequired")
            {
                displayData = displayData.Where(p => !p.IsReportingRequired).ToList();
            }
        }
        
        // Check if current period is excluded and there are no business area overrides
        var periodExcluded = eligibilityCache.PeriodExclusions.Any(e => e.Year == currentYear && e.Month == currentMonth);
        var hasBusinessAreaOverrides = eligibilityCache.BusinessAreaConfigs.Any(c => 
            (c.ApplicableFromYear < currentYear || 
             (c.ApplicableFromYear == currentYear && c.ApplicableFromMonth <= currentMonth)) &&
            (!c.ApplicableUntilYear.HasValue || 
             c.ApplicableUntilYear.Value > currentYear || 
             (c.ApplicableUntilYear.Value == currentYear && c.ApplicableUntilMonth >= currentMonth)));
        
        string? globalSuspensionMessage = null;
        DateTime? globalNextReportingPeriod = null;
        DateTime? globalNextReportingPeriodDueDate = null;
        
        if (periodExcluded && !hasBusinessAreaOverrides)
        {
            globalSuspensionMessage = "Reporting is not required at this time due to period exclusion";
            var nextPeriod = await _eligibilityService.FindNextActiveReportingPeriodAsync(currentYear, currentMonth, null, eligibilityCache);
            if (nextPeriod.HasValue)
            {
                globalNextReportingPeriod = new DateTime(nextPeriod.Value.Year, nextPeriod.Value.Month, 1);
                globalNextReportingPeriodDueDate = _returnStatusService.GetReturnDueDate(nextPeriod.Value.Year, nextPeriod.Value.Month);
            }
        }
        
        // Calculate upcoming reporting dates (3 months in advance)
        // Exclude periods that are in the period exclusions list
        var upcomingReportingDates = new List<(int Year, int Month, DateTime DueDate, string PeriodName)>();
        
        // Start from current reporting period and go forward 3 months
        for (int i = 0; i <= 3; i++)
        {
            var futureMonth = currentMonth + i;
            var futureYear = currentYear;
            
            // Handle year rollover
            while (futureMonth > 12)
            {
                futureMonth -= 12;
                futureYear += 1;
            }
            
            // Skip if this period is excluded
            var isExcluded = eligibilityCache.PeriodExclusions.Any(e => e.Year == futureYear && e.Month == futureMonth);
            if (isExcluded)
            {
                continue;
            }
            
            var dueDate = _returnStatusService.GetReturnDueDate(futureYear, futureMonth);
            var periodName = new DateTime(futureYear, futureMonth, 1).ToString("MMMM yyyy");
            upcomingReportingDates.Add((futureYear, futureMonth, dueDate, periodName));
        }
        
        // Get products table data (user's products that need reporting)
        var productsForTable = userProductStatuses
            .Where(p => p.IsReportingRequired)
            .OrderBy(p => p.Product.Title)
            .ToList();

        // Get unique values for filter dropdowns
        var allProductsForFilters = view == "all" ? allProductStatuses : userProductStatuses;
        var phases = allProductsForFilters
            .Where(p => !string.IsNullOrEmpty(p.Product.Phase))
            .Select(p => p.Product.Phase!)
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        
        var businessAreas = allProductsForFilters
            .Where(p => p.Product.CategoryValues != null)
            .SelectMany(p => p.Product.CategoryValues!)
            .Where(cv => cv.CategoryType != null && 
                        cv.CategoryType.Name.Equals("Business area", StringComparison.OrdinalIgnoreCase))
            .Select(cv => cv.Name)
            .Distinct()
            .OrderBy(ba => ba)
            .ToList();

        ViewBag.CurrentView = view;
        ViewBag.YourProductsCount = yourProductsCount;
        ViewBag.AllProductsCount = allProductsCount;
        ViewBag.CurrentYear = currentYear;
        ViewBag.CurrentMonth = currentMonth;
        ViewBag.GlobalSuspensionMessage = globalSuspensionMessage;
        ViewBag.GlobalNextReportingPeriod = globalNextReportingPeriod;
        ViewBag.GlobalNextReportingPeriodDueDate = globalNextReportingPeriodDueDate;
        ViewBag.UpcomingReportingDates = upcomingReportingDates;
        ViewBag.ProductsForTable = productsForTable;
        ViewBag.UserProducts = userProductStatuses;
        
        // Filter values
        ViewBag.CurrentSearch = search;
        ViewBag.CurrentPhase = phase;
        ViewBag.CurrentBusinessArea = businessArea;
        ViewBag.CurrentReportingStatus = reportingStatus;
        ViewBag.Phases = phases;
        ViewBag.BusinessAreas = businessAreas;
        
        return View("~/Views/ProductReporting/PerformanceMetrics/Index.cshtml", displayData);
    }

    // GET: ProductReporting/ProductCommissions — also /Performance/Product/{productId}
    public async Task<IActionResult> ProductCommissions(string productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return NotFound();

        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
        {
            TempData["ErrorMessage"] = "Unable to identify user. Please ensure you are logged in.";
            return RedirectToAction("PerformanceMetrics");
        }

        var products = await _productsApiService.GetProductsAsync();
        var product = products?.FirstOrDefault(p =>
            string.Equals(p.FipsId, productId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.DocumentId, productId, StringComparison.OrdinalIgnoreCase));

        if (product == null)
        {
            TempData["ErrorMessage"] = "Product not found.";
            return RedirectToAction("PerformanceMetrics");
        }

        var productsByServiceOwner = await _productsApiService.GetProductsByServiceOwnerAsync(userEmail);
        var productsByProductManager = await _productsApiService.GetProductsByProductManagerAsync(userEmail);
        var productsByDeliveryManager = await _productsApiService.GetProductsByDeliveryManagerAsync(userEmail);
        var productsByReportingUser = await _productsApiService.GetProductsByReportingUserAsync(userEmail);

        var userProducts = productsByServiceOwner
            .Concat(productsByProductManager)
            .Concat(productsByDeliveryManager)
            .Concat(productsByReportingUser)
            .GroupBy(p => p.FipsId)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => g.First())
            .Where(p => string.IsNullOrEmpty(p.Phase) ||
                        (!p.Phase.Equals("Decommissioned", StringComparison.OrdinalIgnoreCase) &&
                         !p.Phase.Equals("Decommissioning", StringComparison.OrdinalIgnoreCase)))
            .Where(p =>
            {
                var types = p.CategoryValues?
                    .Where(cv => cv.CategoryType?.Name?.Trim().Equals("Type", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(cv => cv.Name?.Trim() ?? string.Empty)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();
                if (!types.Any())
                    return true;
                if (types.Count == 1 && types[0].Trim().Equals("Data", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (types.All(t => t.Trim().Equals("Data", StringComparison.OrdinalIgnoreCase)))
                    return false;
                return true;
            })
            .ToList();

        bool InList(IEnumerable<ProductDto> list) => list.Any(p =>
            (!string.IsNullOrEmpty(product.DocumentId) && p.DocumentId == product.DocumentId) ||
            (!string.IsNullOrEmpty(product.FipsId) && string.Equals(p.FipsId, product.FipsId, StringComparison.OrdinalIgnoreCase)));

        var inMine = InList(userProducts);

        var allProducts = await _productsApiService.GetAllProductsAsync();
        var activeAll = allProducts
            .Where(p => p.State != null &&
                        p.State.Equals("Active", StringComparison.OrdinalIgnoreCase) &&
                        p.PublishedAt.HasValue &&
                        (string.IsNullOrEmpty(p.Phase) ||
                         (!p.Phase.Equals("Decommissioned", StringComparison.OrdinalIgnoreCase) &&
                          !p.Phase.Equals("Decommissioning", StringComparison.OrdinalIgnoreCase))))
            .Where(p =>
            {
                var types = p.CategoryValues?
                    .Where(cv => cv.CategoryType?.Name?.Trim().Equals("Type", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(cv => cv.Name?.Trim() ?? string.Empty)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();
                if (!types.Any())
                    return true;
                if (types.Count == 1 && types[0].Trim().Equals("Data", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (types.All(t => t.Trim().Equals("Data", StringComparison.OrdinalIgnoreCase)))
                    return false;
                return true;
            })
            .ToList();

        var inAllServicesView = InList(activeAll);

        if (!inMine && !inAllServicesView)
        {
            TempData["ErrorMessage"] = "You do not have access to commission reporting for this product.";
            return RedirectToAction("PerformanceMetrics");
        }

        var productDocumentId = product.DocumentId ?? product.FipsId ?? "";
        var commissions = await _context.Commissions
            .Where(c => c.IsActive)
            .OrderBy(c => c.DueDate)
            .ToListAsync();

        var submissions = await _context.CommissionSubmissions
            .Include(cs => cs.MetricValues)
            .Where(cs => cs.ProductDocumentId == productDocumentId)
            .ToDictionaryAsync(cs => cs.CommissionId, cs => cs);

        var now = DateTime.UtcNow;
        var rows = new List<ProductCommissionPeriodRowViewModel>();
        foreach (var c in commissions)
        {
            submissions.TryGetValue(c.Id, out var submission);
            var isOpen = now >= c.OpenDate && now <= c.DueDate.AddDays(1);
            var isPastDue = now > c.DueDate;
            var status = submission?.Status ?? CommissionSubmissionStatus.NotStarted;
            rows.Add(new ProductCommissionPeriodRowViewModel
            {
                Commission = c,
                Submission = submission,
                Status = status,
                IsOpen = isOpen,
                IsPastDue = isPastDue
            });
        }

        ViewBag.Product = product;
        return View("~/Views/ProductReporting/ProductCommissions/Index.cshtml", rows);
    }

    // GET: ProductReporting/Commission
    public async Task<IActionResult> Commission(
        string view = "mine", 
        string search = "", 
        string phase = "", 
        string businessArea = "", 
        string reportingStatus = "",
        int? commissionId = null,
        bool clearFilters = false)
    {
        // Get active commissions - order by due date (closes first)
        var activeCommissions = await _context.Commissions
            .Where(c => c.IsActive)
            .OrderBy(c => c.DueDate)
            .ToListAsync();

        if (!activeCommissions.Any())
        {
            ViewBag.Message = "No active commissions are currently available.";
            ViewBag.ActiveCommissions = activeCommissions;
            ViewBag.SelectedCommission = null;
            return View("~/Views/ProductReporting/Commission/Index.cshtml", new List<CommissionSubmissionStatusViewModel>());
        }

        // If no commissionId provided, show commission selection page
        if (!commissionId.HasValue)
        {
            ViewBag.ActiveCommissions = activeCommissions;
            ViewBag.SelectedCommission = null;
            return View("~/Views/ProductReporting/Commission/Index.cshtml", new List<CommissionSubmissionStatusViewModel>());
        }

        // Handle clear filters
        if (clearFilters)
        {
            return RedirectToAction("Commission", new { view = view, commissionId = commissionId });
        }

        // Get the current user's email
        var userEmail = User.Identity?.Name;
        
        if (string.IsNullOrEmpty(userEmail))
        {
            _logger.LogWarning("Commission: No user email found for current user");
            TempData["ErrorMessage"] = "Unable to identify user. Please ensure you are logged in.";
            ViewBag.ActiveCommissions = activeCommissions;
            ViewBag.SelectedCommission = null;
            return View("~/Views/ProductReporting/Commission/Index.cshtml", new List<CommissionSubmissionStatusViewModel>());
        }

        // Use selected commission
        var selectedCommission = activeCommissions.FirstOrDefault(c => c.Id == commissionId.Value);

        if (selectedCommission == null)
        {
            TempData["ErrorMessage"] = "Selected commission not found.";
            ViewBag.ActiveCommissions = activeCommissions;
            ViewBag.SelectedCommission = null;
            return View("~/Views/ProductReporting/Commission/Index.cshtml", new List<CommissionSubmissionStatusViewModel>());
        }

        // Check if commission is open
        var now = DateTime.UtcNow;
        var isOpen = now >= selectedCommission.OpenDate && now <= selectedCommission.DueDate.AddDays(1);
        var isPastDue = now > selectedCommission.DueDate;
        var eligibilityCache = await _eligibilityService.LoadEligibilityCacheAsync();

        // Fetch user's products - from service_owner, product_manager, delivery_manager, and reporting_user
        var productsByServiceOwner = await _productsApiService.GetProductsByServiceOwnerAsync(userEmail);
        var productsByProductManager = await _productsApiService.GetProductsByProductManagerAsync(userEmail);
        var productsByDeliveryManager = await _productsApiService.GetProductsByDeliveryManagerAsync(userEmail);
        var productsByReportingUser = await _productsApiService.GetProductsByReportingUserAsync(userEmail);
        
        // Combine and deduplicate products (by FipsId)
        // Then exclude Decommissioned/Decommissioning Phase products
        // Also exclude products where the only Type is "Data" from performance reporting,
        // but keep products that have "Data" alongside another Type.
        var userProducts = productsByServiceOwner
            .Concat(productsByProductManager)
            .Concat(productsByDeliveryManager)
            .Concat(productsByReportingUser)
            .GroupBy(p => p.FipsId)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => g.First())
            .Where(p => string.IsNullOrEmpty(p.Phase) || 
                       (!p.Phase.Equals("Decommissioned", StringComparison.OrdinalIgnoreCase) &&
                        !p.Phase.Equals("Decommissioning", StringComparison.OrdinalIgnoreCase)))
            .Where(p =>
            {
                var types = p.CategoryValues?
                    .Where(cv => cv.CategoryType?.Name?.Trim().Equals("Type", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(cv => cv.Name?.Trim() ?? string.Empty)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();
                
                // If no types, include the product
                if (!types.Any())
                    return true;
                
                // If only "Data" type (even if multiple entries), exclude it
                if (types.Count == 1 && types[0].Trim().Equals("Data", StringComparison.OrdinalIgnoreCase))
                    return false;
                
                // If all types are "Data", exclude it
                if (types.All(t => t.Trim().Equals("Data", StringComparison.OrdinalIgnoreCase)))
                    return false;
                
                // If has "Data" plus other types, include it
                return true;
            })
            .ToList();
            
        _logger.LogInformation("Commission: Found {ServiceOwnerCount} products by service owner, {ProductManagerCount} by product manager, {DeliveryManagerCount} by delivery manager, {ReportingUserCount} by reporting user, {TotalCount} total unique (after Phase filter) for user {UserEmail}", 
            productsByServiceOwner.Count, productsByProductManager.Count, productsByDeliveryManager.Count, productsByReportingUser.Count, userProducts.Count, userEmail);

        // Get all commission submissions for this commission
        var allSubmissions = await _context.CommissionSubmissions
            .Include(cs => cs.MetricValues)
            .Where(cs => cs.CommissionId == selectedCommission.Id)
            .ToDictionaryAsync(cs => cs.ProductDocumentId, cs => cs);

        // Create view models for user's products
        var userProductStatuses = new List<CommissionSubmissionStatusViewModel>();
        foreach (var product in userProducts)
        {
            if (!CommissionReportingProductScope.ProductMatchesCommissionInScopeRules(selectedCommission, product))
                continue;

            if (_eligibilityService.IsProductExcludedForCommission(product, selectedCommission, eligibilityCache))
                continue;

            var submission = allSubmissions.GetValueOrDefault(product.DocumentId ?? "");
            var vm = new CommissionSubmissionStatusViewModel
            {
                Product = product,
                Commission = selectedCommission,
                Submission = submission,
                Status = submission?.Status ?? CommissionSubmissionStatus.NotStarted,
                CompletedMetrics = submission?.MetricValues?.Count(mv => mv.IsComplete) ?? 0,
                TotalMetrics = submission?.MetricValues?.Count ?? 0,
                IsOpen = isOpen,
                IsPastDue = isPastDue
            };
            userProductStatuses.Add(vm);
        }

        // For "All products" view, get all eligible products
        List<CommissionSubmissionStatusViewModel> allProductStatuses = new List<CommissionSubmissionStatusViewModel>();
        int allProductsCount = 0;
        
        if (view == "all")
        {
            var allProducts = await _productsApiService.GetAllProductsAsync();
            var activeProducts = allProducts
                .Where(p => p.State != null && 
                           p.State.Equals("Active", StringComparison.OrdinalIgnoreCase) &&
                           p.PublishedAt.HasValue &&
                           (string.IsNullOrEmpty(p.Phase) || 
                            (!p.Phase.Equals("Decommissioned", StringComparison.OrdinalIgnoreCase) &&
                             !p.Phase.Equals("Decommissioning", StringComparison.OrdinalIgnoreCase))))
                // Exclude products where the only Type is "Data" from performance reporting,
                // but keep products that have "Data" alongside another Type.
                .Where(p =>
                {
                    var types = p.CategoryValues?
                        .Where(cv => cv.CategoryType?.Name?.Trim().Equals("Type", StringComparison.OrdinalIgnoreCase) == true)
                        .Select(cv => cv.Name?.Trim() ?? string.Empty)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList() ?? new List<string>();
                    
                    // If no types, include the product
                    if (!types.Any())
                        return true;
                    
                    // If only "Data" type (even if multiple entries), exclude it
                    if (types.Count == 1 && types[0].Trim().Equals("Data", StringComparison.OrdinalIgnoreCase))
                        return false;
                    
                    // If all types are "Data", exclude it
                    if (types.All(t => t.Trim().Equals("Data", StringComparison.OrdinalIgnoreCase)))
                        return false;
                    
                    // If has "Data" plus other types, include it
                    return true;
                })
                .ToList();
            
            foreach (var product in activeProducts)
            {
                if (!CommissionReportingProductScope.ProductMatchesCommissionInScopeRules(selectedCommission, product))
                    continue;

                if (_eligibilityService.IsProductExcludedForCommission(product, selectedCommission, eligibilityCache))
                    continue;

                var submission = allSubmissions.GetValueOrDefault(product.DocumentId ?? "");
                var vm = new CommissionSubmissionStatusViewModel
                {
                    Product = product,
                    Commission = selectedCommission,
                    Submission = submission,
                    Status = submission?.Status ?? CommissionSubmissionStatus.NotStarted,
                    CompletedMetrics = submission?.MetricValues?.Count(mv => mv.IsComplete) ?? 0,
                    TotalMetrics = submission?.MetricValues?.Count ?? 0,
                    IsOpen = isOpen,
                    IsPastDue = isPastDue
                };
                allProductStatuses.Add(vm);
            }

            allProductsCount = allProductStatuses.Count;
        }

        // Determine which data to show based on view
        List<CommissionSubmissionStatusViewModel> displayData;
        if (view == "mine" || string.IsNullOrEmpty(view))
        {
            displayData = userProductStatuses;
        }
        else if (view == "all")
        {
            displayData = allProductStatuses;
        }
        else
        {
            displayData = userProductStatuses;
        }

        // Apply filters
        if (!string.IsNullOrEmpty(search))
        {
            displayData = displayData.Where(p => 
                p.Product.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (p.Product.FipsId != null && p.Product.FipsId.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(p.Product.Phase) && p.Product.Phase.Contains(search, StringComparison.OrdinalIgnoreCase))
            ).ToList();
        }

        if (!string.IsNullOrEmpty(phase))
        {
            displayData = displayData.Where(p => 
                !string.IsNullOrEmpty(p.Product.Phase) && 
                p.Product.Phase.Equals(phase, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        if (!string.IsNullOrEmpty(businessArea))
        {
            displayData = displayData.Where(p => 
                p.Product.CategoryValues != null &&
                p.Product.CategoryValues.Any(cv => 
                    cv.CategoryType != null &&
                    cv.CategoryType.Name.Equals("Business area", StringComparison.OrdinalIgnoreCase) &&
                    cv.Name.Equals(businessArea, StringComparison.OrdinalIgnoreCase))
            ).ToList();
        }

        if (!string.IsNullOrEmpty(reportingStatus))
        {
            if (reportingStatus == "NotStarted")
            {
                displayData = displayData.Where(p => p.Status == CommissionSubmissionStatus.NotStarted).ToList();
            }
            else if (reportingStatus == "InProgress")
            {
                displayData = displayData.Where(p => p.Status == CommissionSubmissionStatus.InProgress).ToList();
            }
            else if (reportingStatus == "Submitted")
            {
                displayData = displayData.Where(p => p.Status == CommissionSubmissionStatus.Submitted).ToList();
            }
            else if (reportingStatus == "Late")
            {
                displayData = displayData.Where(p => p.Status == CommissionSubmissionStatus.Late).ToList();
            }
        }

        // Calculate counts
        var tasksCount = displayData.Count(p => 
            p.Status == CommissionSubmissionStatus.NotStarted || 
            p.Status == CommissionSubmissionStatus.InProgress);

        var yourProductsCount = userProductStatuses.Count;

        // Get unique values for filter dropdowns
        var allProductsForFilters = view == "all" ? allProductStatuses : userProductStatuses;
        var phases = allProductsForFilters
            .Where(p => !string.IsNullOrEmpty(p.Product.Phase))
            .Select(p => p.Product.Phase!)
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        
        var businessAreas = allProductsForFilters
            .Where(p => p.Product.CategoryValues != null)
            .SelectMany(p => p.Product.CategoryValues!)
            .Where(cv => cv.CategoryType != null && 
                        cv.CategoryType.Name.Equals("Business area", StringComparison.OrdinalIgnoreCase))
            .Select(cv => cv.Name)
            .Distinct()
            .OrderBy(ba => ba)
            .ToList();

        ViewBag.CurrentView = view;
        ViewBag.YourProductsCount = yourProductsCount;
        ViewBag.AllProductsCount = allProductsCount;
        ViewBag.TasksCount = tasksCount;
        ViewBag.ActiveCommissions = activeCommissions;
        ViewBag.SelectedCommission = selectedCommission;
        ViewBag.IsOpen = isOpen;
        ViewBag.IsPastDue = isPastDue;
        
        // Filter values
        ViewBag.CurrentSearch = search;
        ViewBag.CurrentPhase = phase;
        ViewBag.CurrentBusinessArea = businessArea;
        ViewBag.CurrentReportingStatus = reportingStatus;
        ViewBag.Phases = phases;
        ViewBag.BusinessAreas = businessAreas;
        
        return View("~/Views/ProductReporting/Commission/MyServices.cshtml", displayData);
    }

    // POST: ProductReporting/SubmitCommissionSubmission/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitCommissionSubmission(int id, string? comments)
    {
        var submission = await _context.CommissionSubmissions
            .Include(cs => cs.Commission)
            .Include(cs => cs.MetricValues)
                .ThenInclude(cmv => cmv.PerformanceMetric)
            .FirstOrDefaultAsync(cs => cs.Id == id);

        if (submission == null)
        {
            return NotFound();
        }

        if (submission.Status == CommissionSubmissionStatus.Submitted)
        {
            TempData["ErrorMessage"] = "This submission has already been submitted.";
            return RedirectToAction("Commission", new { commissionId = submission.CommissionId });
        }

        // Get the current product to determine which metrics are applicable
        var products = await _productsApiService.GetProductsAsync();
        var product = products?.FirstOrDefault(p => p.DocumentId == submission.ProductDocumentId || p.FipsId == submission.FipsId);
        
        if (product == null)
        {
            TempData["ErrorMessage"] = "Product not found. Please contact support.";
            return RedirectToAction("Commission", new { commissionId = submission.CommissionId });
        }

        // Get applicable metrics based on current product category/type
        var applicableMetrics = await GetApplicableMetricsForProductAsync(product, submission.CommissionId);
        
        // Get existing metric values
        var existingMetricValues = submission.MetricValues
            .Where(mv => mv.PerformanceMetric != null && applicableMetrics.Any(m => m.Id == mv.PerformanceMetric.Id))
            .ToList();

        // Validate that all applicable required metrics are complete
        var incompleteMetrics = new List<string>();
        foreach (var metric in applicableMetrics)
        {
            var metricValue = existingMetricValues.FirstOrDefault(mv => mv.PerformanceMetricId == metric.Id);
            
            // Check if metric is required
            bool isRequired = false;
            try
            {
                var rules = JsonSerializer.Deserialize<ValidationRules>(metric.ValidationRules);
                isRequired = rules?.Required == true && rules?.AllowNull != true;
            }
            catch
            {
                // If we can't parse rules, assume not required
            }

            if (isRequired)
            {
                // Metric is required - check if it's complete
                if (metricValue == null || !metricValue.IsComplete)
                {
                    incompleteMetrics.Add(metric.Title);
                }
            }
        }

        if (incompleteMetrics.Any())
        {
            TempData["ErrorMessage"] = $"Please complete all required metrics before submitting. Missing: {string.Join(", ", incompleteMetrics)}";
            return RedirectToAction("SubmitCommission", new { 
                documentId = submission.ProductDocumentId, 
                commissionId = submission.CommissionId 
            });
        }

        var now = DateTime.UtcNow;
        if (submission.Commission != null && now > submission.Commission.DueDate)
        {
            submission.Status = CommissionSubmissionStatus.Late;
        }
        else
        {
            submission.Status = CommissionSubmissionStatus.Submitted;
        }

        submission.SubmittedDate = now;
        submission.SubmittedBy = User.Identity?.Name ?? "unknown";
        submission.UpdatedAt = now;
        submission.Comments = !string.IsNullOrWhiteSpace(comments) ? comments.Trim() : null;

        await _context.SaveChangesAsync();

        TempData["SuccessMessage"] = "Commission submission completed successfully.";
        return RedirectToAction("Commission", new { commissionId = submission.CommissionId });
    }

    // GET: ProductReporting/SubmitCommission/{documentId}/{commissionId}
    public async Task<IActionResult> SubmitCommission(string documentId, int commissionId)
    {
        if (string.IsNullOrEmpty(documentId))
        {
            return NotFound();
        }

        // Get commission
        var commission = await _context.Commissions.FindAsync(commissionId);
        if (commission == null || !commission.IsActive)
        {
            TempData["ErrorMessage"] = "Commission not found or is not active.";
            return RedirectToAction("Commission");
        }

        // Check if commission is open
        var now = DateTime.UtcNow;
        if (now < commission.OpenDate)
        {
            TempData["WarningMessage"] = $"This commission is not yet open. It will be available from {commission.OpenDate:dd MMM yyyy}.";
            return RedirectToAction("Commission", new { commissionId = commissionId });
        }

        // Try to get product by DocumentId first, then FipsId for backwards compatibility
        var products = await _productsApiService.GetProductsAsync();
        var product = products?.FirstOrDefault(p => 
            p.DocumentId == documentId || p.FipsId == documentId);
        
        if (product == null)
        {
            return NotFound();
        }

        if (!CommissionReportingProductScope.ProductMatchesCommissionInScopeRules(commission, product))
        {
            TempData["ErrorMessage"] = "This product is not in scope for this commission.";
            return RedirectToAction("Commission", new { commissionId });
        }

        var eligibilityCache = await _eligibilityService.LoadEligibilityCacheAsync();
        if (_eligibilityService.IsProductExcludedForCommission(product, commission, eligibilityCache))
        {
            TempData["ErrorMessage"] = "This product is excluded from performance reporting for this commission period.";
            return RedirectToAction("Commission", new { commissionId });
        }

        // Use DocumentId for database operations (primary identifier)
        var productDocumentId = product.DocumentId ?? documentId;

        // Get or create commission submission
        var submission = await _context.CommissionSubmissions
            .Include(cs => cs.MetricValues)
                .ThenInclude(cmv => cmv.PerformanceMetric)
            .FirstOrDefaultAsync(cs => cs.CommissionId == commissionId && cs.ProductDocumentId == productDocumentId);

        if (submission == null)
        {
            submission = new CommissionSubmission
            {
                CommissionId = commissionId,
                ProductDocumentId = productDocumentId,
                FipsId = product.FipsId,
                ProductTitle = product.Title,
                Status = CommissionSubmissionStatus.NotStarted,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.CommissionSubmissions.Add(submission);
            await _context.SaveChangesAsync();
        }

        // Allow viewing submitted submissions - they can be unsubmitted if before due date

        var year = commission.EndDate.Year;
        var month = commission.EndDate.Month;
        var allMetrics = await _context.PerformanceMetrics
            .Where(m => !m.IsDisabled &&
                        (m.ValidFromYear < year ||
                         (m.ValidFromYear == year && m.ValidFromMonth <= month)))
            .OrderBy(m => m.Identifier)
            .ToListAsync();

        var included = CommissionReportingMetricsHelper.ParseIncludedMetricIds(commission.IncludedPerformanceMetricIds);
        if (included != null)
            allMetrics = allMetrics.Where(m => included.Contains(m.Id)).ToList();

        // Filter by phase
        var phaseFilteredMetrics = allMetrics.Where(m => 
        {
            if (string.IsNullOrEmpty(m.ApplicablePhases))
                return true;
            if (string.IsNullOrEmpty(product.Phase))
                return true;
            var applicablePhases = m.ApplicablePhases.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .ToList();
            return applicablePhases.Contains(product.Phase, StringComparer.OrdinalIgnoreCase);
        }).ToList();

        // Filter by type
        var productTypes = new List<string>();
        if (product.CategoryValues != null)
        {
            productTypes = product.CategoryValues
                .Where(cv => cv.CategoryType?.Name?.Equals("Type", StringComparison.OrdinalIgnoreCase) == true)
                .Select(cv => cv.Name)
                .ToList();
        }

        var typeFilteredMetrics = phaseFilteredMetrics.Where(m => 
        {
            if (string.IsNullOrEmpty(m.ApplicableTypes))
                return true;
            if (!productTypes.Any())
                return true;
            var applicableTypes = m.ApplicableTypes.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .ToList();
            return productTypes.Any(pt => applicableTypes.Contains(pt, StringComparer.OrdinalIgnoreCase));
        }).ToList();

        // Get existing metric values
        var existingMetricValues = await _context.CommissionMetricValues
            .Include(cmv => cmv.PerformanceMetric)
            .Where(cmv => cmv.CommissionSubmissionId == submission.Id)
            .ToListAsync();

        // Filter by conditional dependencies
        var metrics = typeFilteredMetrics.Where(m => 
        {
            if (!m.ConditionalOnMetricId.HasValue)
                return true;
            var parentMetricValue = existingMetricValues
                .FirstOrDefault(mv => mv.PerformanceMetricId == m.ConditionalOnMetricId.Value);
            return parentMetricValue != null && !string.IsNullOrWhiteSpace(parentMetricValue.Value);
        }).ToList();

        // Ensure we have a metric value entry for each valid metric
        foreach (var metric in metrics)
        {
            if (!existingMetricValues.Any(mv => mv.PerformanceMetricId == metric.Id))
            {
                var newValue = new CommissionMetricValue
                {
                    CommissionSubmissionId = submission.Id,
                    PerformanceMetricId = metric.Id,
                    IsComplete = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.CommissionMetricValues.Add(newValue);
                existingMetricValues.Add(newValue);
            }
        }

        await _context.SaveChangesAsync();

        // Reload metric values with performance metrics
        var allMetricValues = await _context.CommissionMetricValues
            .Include(cmv => cmv.PerformanceMetric)
            .Where(cmv => cmv.CommissionSubmissionId == submission.Id)
            .ToListAsync();

        // Filter metric values to only include those that are currently applicable
        // This ensures that if the product category changed, we don't show metrics that are no longer applicable
        var applicableMetricIds = metrics.Select(m => m.Id).ToHashSet();
        var metricValues = allMetricValues
            .Where(mv => mv.PerformanceMetric != null && applicableMetricIds.Contains(mv.PerformanceMetric.Id))
            .OrderBy(mv => mv.PerformanceMetric?.Identifier)
            .ToList();

        // Check if commission is past due
        var isPastDue = now > commission.DueDate;
        // Read-only if past due date (regardless of submission status)
        // If submitted but before due date, allow unsubmit to make it editable
        var isReadOnly = isPastDue;

        ViewBag.Product = product;
        ViewBag.Commission = commission;
        ViewBag.Submission = submission;
        ViewBag.IsReadOnly = isReadOnly;
        ViewBag.IsPastDue = isPastDue;
        
        return View("~/Views/ProductReporting/Commission/Submit.cshtml", metricValues);
    }

    // POST: ProductReporting/SaveCommissionMetricValue
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCommissionMetricValue(int id, string? value, bool isNotCaptured = false, string? notCapturedReason = null, string? reasonForDifference = null)
    {
        _logger.LogInformation("SaveCommissionMetricValue called - ID: {Id}, Value: '{Value}', IsNotCaptured: {IsNotCaptured}", 
            id, value ?? "(null)", isNotCaptured);
        
        var metricValue = await _context.CommissionMetricValues
            .Include(cmv => cmv.PerformanceMetric)
            .Include(cmv => cmv.CommissionSubmission)
                .ThenInclude(cs => cs.Commission)
            .FirstOrDefaultAsync(cmv => cmv.Id == id);

        if (metricValue == null)
        {
            return Json(new { success = false, message = "Metric value not found" });
        }

        // Check if submission is editable
        if (metricValue.CommissionSubmission?.Status == CommissionSubmissionStatus.Submitted)
        {
            return Json(new { success = false, message = "This submission has been submitted and cannot be edited" });
        }

        var now = DateTime.UtcNow;
        if (metricValue.CommissionSubmission?.Commission != null && now > metricValue.CommissionSubmission.Commission.DueDate)
        {
            return Json(new { success = false, message = "This commission is past due and cannot be edited" });
        }

        try
        {
            if (isNotCaptured)
            {
                metricValue.Value = string.Empty;
                metricValue.IsComplete = true;
                metricValue.IsNotCaptured = true;
                metricValue.NotCapturedReason = notCapturedReason;
            }
            else
            {
                if (metricValue.PerformanceMetric != null && !string.IsNullOrWhiteSpace(value))
                {
                    var validationResult = ValidateMetricValue(value, metricValue.PerformanceMetric);
                    if (!validationResult.IsValid)
                    {
                        return Json(new { success = false, message = validationResult.ErrorMessage });
                    }
                }

                metricValue.Value = value;
                metricValue.IsComplete = !string.IsNullOrWhiteSpace(value);
                metricValue.IsNotCaptured = false;
                metricValue.NotCapturedReason = null;
            }

            metricValue.ReasonForDifference = reasonForDifference;
            metricValue.UpdatedAt = DateTime.UtcNow;

            // Update submission status
            if (metricValue.CommissionSubmission != null)
            {
                var allMetricValues = await _context.CommissionMetricValues
                    .Where(cmv => cmv.CommissionSubmissionId == metricValue.CommissionSubmission.Id)
                    .ToListAsync();

                var completedCount = allMetricValues.Count(mv => mv.IsComplete);
                var totalCount = allMetricValues.Count;

                if (completedCount == totalCount && totalCount > 0)
                {
                    metricValue.CommissionSubmission.Status = CommissionSubmissionStatus.InProgress;
                }
                else if (completedCount > 0)
                {
                    metricValue.CommissionSubmission.Status = CommissionSubmissionStatus.InProgress;
                }
                else
                {
                    metricValue.CommissionSubmission.Status = CommissionSubmissionStatus.NotStarted;
                }

                metricValue.CommissionSubmission.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving commission metric value {Id}", id);
            return Json(new { success = false, message = "An error occurred while saving the value" });
        }
    }

    // POST: ProductReporting/UnsubmitCommissionSubmission
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnsubmitCommissionSubmission(int id)
    {
        var submission = await _context.CommissionSubmissions
            .Include(cs => cs.Commission)
            .FirstOrDefaultAsync(cs => cs.Id == id);

        if (submission == null)
        {
            TempData["ErrorMessage"] = "Submission not found.";
            return RedirectToAction("Commission");
        }

        // Check if submission is actually submitted
        if (submission.Status != CommissionSubmissionStatus.Submitted && submission.Status != CommissionSubmissionStatus.Late)
        {
            TempData["ErrorMessage"] = "This submission has not been submitted yet.";
            return RedirectToAction("SubmitCommission", new { 
                documentId = submission.ProductDocumentId, 
                commissionId = submission.CommissionId 
            });
        }

        // Check if commission is past due - if so, cannot unsubmit
        var now = DateTime.UtcNow;
        if (submission.Commission != null && now > submission.Commission.DueDate)
        {
            TempData["ErrorMessage"] = "This submission cannot be unsubmitted because the commission due date has passed.";
            return RedirectToAction("SubmitCommission", new { 
                documentId = submission.ProductDocumentId, 
                commissionId = submission.CommissionId 
            });
        }

        try
        {
            // Recalculate status based on current state
            // If all metrics are complete, set to InProgress, otherwise NotStarted
            var metricValues = await _context.CommissionMetricValues
                .Where(mv => mv.CommissionSubmissionId == submission.Id)
                .ToListAsync();
            
            var allComplete = metricValues.All(mv => mv.IsComplete);
            submission.Status = allComplete ? CommissionSubmissionStatus.InProgress : CommissionSubmissionStatus.NotStarted;
            submission.SubmittedDate = null;
            submission.SubmittedBy = null;
            submission.UpdatedAt = now;

            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "Commission submission has been unsubmitted and is now available for editing.";
            return RedirectToAction("SubmitCommission", new { 
                documentId = submission.ProductDocumentId, 
                commissionId = submission.CommissionId 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unsubmitting commission submission");
            TempData["ErrorMessage"] = "An error occurred while unsubmitting the commission submission.";
            return RedirectToAction("SubmitCommission", new { 
                documentId = submission.ProductDocumentId, 
                commissionId = submission.CommissionId 
            });
        }
    }

    // POST: ProductReporting/UpdateCommissionComments
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCommissionComments(int id, string? comments)
    {
        var submission = await _context.CommissionSubmissions
            .Include(cs => cs.Commission)
            .FirstOrDefaultAsync(cs => cs.Id == id);

        if (submission == null)
        {
            TempData["ErrorMessage"] = "Submission not found.";
            return RedirectToAction("Commission");
        }

        // Check if submission is submitted
        if (submission.Status != CommissionSubmissionStatus.Submitted && submission.Status != CommissionSubmissionStatus.Late)
        {
            TempData["ErrorMessage"] = "Comments can only be updated for submitted commissions.";
            return RedirectToAction("SubmitCommission", new { 
                documentId = submission.ProductDocumentId, 
                commissionId = submission.CommissionId 
            });
        }

        // Check if commission is past due - if so, cannot edit
        var now = DateTime.UtcNow;
        if (submission.Commission != null && now > submission.Commission.DueDate)
        {
            TempData["ErrorMessage"] = "Comments cannot be updated because the commission due date has passed.";
            return RedirectToAction("SubmitCommission", new { 
                documentId = submission.ProductDocumentId, 
                commissionId = submission.CommissionId 
            });
        }

        try
        {
            submission.Comments = !string.IsNullOrWhiteSpace(comments) ? comments.Trim() : null;
            submission.UpdatedAt = now;

            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "Comments have been updated successfully.";
            return RedirectToAction("SubmitCommission", new { 
                documentId = submission.ProductDocumentId, 
                commissionId = submission.CommissionId 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating commission comments");
            TempData["ErrorMessage"] = "An error occurred while updating the comments.";
            return RedirectToAction("SubmitCommission", new { 
                documentId = submission.ProductDocumentId, 
                commissionId = submission.CommissionId 
            });
        }
    }

    // GET: ProductReporting/CommissionReporting/{commissionId}
    public async Task<IActionResult> CommissionReporting(int commissionId)
    {
        // Get commission
        var commission = await _context.Commissions.FindAsync(commissionId);
        if (commission == null || !commission.IsActive)
        {
            TempData["ErrorMessage"] = "Commission not found or is not active.";
            return RedirectToAction("Commission");
        }

        // Get all products (only Active, only Published, exclude Decommissioned/Decommissioning Phase)
        var allProducts = await _productsApiService.GetAllProductsAsync();
        var eligibleProducts = allProducts
            .Where(p => p.State != null && 
                       p.State.Equals("Active", StringComparison.OrdinalIgnoreCase) &&
                       p.PublishedAt.HasValue &&
                       (string.IsNullOrEmpty(p.Phase) || 
                        (!p.Phase.Equals("Decommissioned", StringComparison.OrdinalIgnoreCase) &&
                         !p.Phase.Equals("Decommissioning", StringComparison.OrdinalIgnoreCase))))
            .ToList();

        // Get all commission submissions for this commission
        var allSubmissions = await _context.CommissionSubmissions
            .Include(cs => cs.MetricValues)
            .Where(cs => cs.CommissionId == commissionId)
            .ToDictionaryAsync(cs => cs.ProductDocumentId, cs => cs);

        // Calculate overall statistics
        var totalProducts = eligibleProducts.Count;
        var completedProducts = 0;
        var inProgressProducts = 0;
        var notStartedProducts = 0;
        var lateProducts = 0;

        // Group products by business area
        var businessAreaGroups = new Dictionary<string, List<ProductDto>>();
        
        foreach (var product in eligibleProducts)
        {
            var businessArea = product.CategoryValues?
                .FirstOrDefault(cv => cv.CategoryType?.Name?.Equals("Business area", StringComparison.OrdinalIgnoreCase) == true)
                ?.Name ?? "Unassigned";

            if (!businessAreaGroups.ContainsKey(businessArea))
            {
                businessAreaGroups[businessArea] = new List<ProductDto>();
            }
            businessAreaGroups[businessArea].Add(product);
        }

        // Calculate statistics for each product
        var productStatuses = new Dictionary<string, CommissionSubmissionStatus>();
        foreach (var product in eligibleProducts)
        {
            var documentId = product.DocumentId ?? "";
            var submission = allSubmissions.GetValueOrDefault(documentId);
            var status = submission?.Status ?? CommissionSubmissionStatus.NotStarted;
            productStatuses[documentId] = status;

            switch (status)
            {
                case CommissionSubmissionStatus.Submitted:
                    completedProducts++;
                    break;
                case CommissionSubmissionStatus.InProgress:
                    inProgressProducts++;
                    break;
                case CommissionSubmissionStatus.Late:
                    lateProducts++;
                    break;
                case CommissionSubmissionStatus.NotStarted:
                    notStartedProducts++;
                    break;
            }
        }

        // Calculate business area completions
        var businessAreaCompletions = new List<CommissionBusinessAreaCompletion>();
        foreach (var group in businessAreaGroups.OrderBy(g => g.Key))
        {
            var baProducts = group.Value;
            var baTotal = baProducts.Count;
            var baCompleted = 0;
            var baInProgress = 0;
            var baNotStarted = 0;
            var baLate = 0;

            foreach (var product in baProducts)
            {
                var documentId = product.DocumentId ?? "";
                var status = productStatuses.GetValueOrDefault(documentId, CommissionSubmissionStatus.NotStarted);
                
                switch (status)
                {
                    case CommissionSubmissionStatus.Submitted:
                        baCompleted++;
                        break;
                    case CommissionSubmissionStatus.InProgress:
                        baInProgress++;
                        break;
                    case CommissionSubmissionStatus.Late:
                        baLate++;
                        break;
                    case CommissionSubmissionStatus.NotStarted:
                        baNotStarted++;
                        break;
                }
            }

            var baCompletionPercentage = baTotal > 0 ? (decimal)baCompleted / baTotal * 100 : 0;

            businessAreaCompletions.Add(new CommissionBusinessAreaCompletion
            {
                BusinessAreaName = group.Key,
                TotalProducts = baTotal,
                CompletedProducts = baCompleted,
                InProgressProducts = baInProgress,
                NotStartedProducts = baNotStarted,
                LateProducts = baLate,
                CompletionPercentage = Math.Round(baCompletionPercentage, 1)
            });
        }

        var overallCompletionPercentage = totalProducts > 0 ? (decimal)completedProducts / totalProducts * 100 : 0;

        var viewModel = new CommissionReportingViewModel
        {
            Commission = commission,
            TotalProducts = totalProducts,
            CompletedProducts = completedProducts,
            InProgressProducts = inProgressProducts,
            NotStartedProducts = notStartedProducts,
            LateProducts = lateProducts,
            CompletionPercentage = Math.Round(overallCompletionPercentage, 1),
            BusinessAreaCompletions = businessAreaCompletions
        };

        ViewBag.ActiveCommissions = await _context.Commissions
            .Where(c => c.IsActive)
            .OrderByDescending(c => c.StartDate)
            .ToListAsync();

        return View("~/Views/ProductReporting/Commission/Reporting.cshtml", viewModel);
    }

    // GET: ProductReporting/BusinessAreasIndex
    public async Task<IActionResult> BusinessAreasIndex(int? commissionId = null)
    {
        // Get active commissions
        var activeCommissions = await _context.Commissions
            .Where(c => c.IsActive)
            .OrderByDescending(c => c.StartDate)
            .ToListAsync();

        if (!activeCommissions.Any())
        {
            ViewBag.Message = "No active commissions are currently available.";
            return View("~/Views/ProductReporting/Commission/BusinessAreasIndex.cshtml", new BusinessAreasViewModel
            {
                BusinessAreaCompletions = new List<BusinessAreaCompletion>()
            });
        }

        // Use selected commission or default to most recent
        var selectedCommission = commissionId.HasValue
            ? activeCommissions.FirstOrDefault(c => c.Id == commissionId.Value)
            : activeCommissions.FirstOrDefault();

        if (selectedCommission == null)
        {
            TempData["ErrorMessage"] = "Selected commission not found.";
            return RedirectToAction("BusinessAreasIndex");
        }

        // Get all products (only Active, only Published, exclude Decommissioned/Decommissioning Phase)
        // Exclude products where the only Type is "Data" from performance reporting,
        // but keep products that have "Data" alongside another Type.
        var allProducts = await _productsApiService.GetAllProductsAsync();
        var eligibleProducts = allProducts
            .Where(p => p.State != null && 
                       p.State.Equals("Active", StringComparison.OrdinalIgnoreCase) &&
                       p.PublishedAt.HasValue &&
                       (string.IsNullOrEmpty(p.Phase) || 
                        (!p.Phase.Equals("Decommissioned", StringComparison.OrdinalIgnoreCase) &&
                         !p.Phase.Equals("Decommissioning", StringComparison.OrdinalIgnoreCase))))
            .Where(p =>
            {
                var types = p.CategoryValues?
                    .Where(cv => cv.CategoryType?.Name?.Trim().Equals("Type", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(cv => cv.Name?.Trim() ?? string.Empty)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();
                
                // If no types, include the product
                if (!types.Any())
                    return true;
                
                // If only "Data" type (even if multiple entries), exclude it
                if (types.Count == 1 && types[0].Trim().Equals("Data", StringComparison.OrdinalIgnoreCase))
                    return false;
                
                // If all types are "Data", exclude it
                if (types.All(t => t.Trim().Equals("Data", StringComparison.OrdinalIgnoreCase)))
                    return false;
                
                // If has "Data" plus other types, include it
                return true;
            })
            .ToList();

        // Get all commission submissions for this commission
        var allSubmissions = await _context.CommissionSubmissions
            .Include(cs => cs.MetricValues)
            .Where(cs => cs.CommissionId == selectedCommission.Id)
            .ToDictionaryAsync(cs => cs.ProductDocumentId, cs => cs);

        // Group products by business area
        var businessAreaGroups = new Dictionary<string, List<ProductDto>>();
        
        foreach (var product in eligibleProducts)
        {
            var businessArea = product.CategoryValues?
                .FirstOrDefault(cv => cv.CategoryType?.Name?.Equals("Business area", StringComparison.OrdinalIgnoreCase) == true)
                ?.Name ?? "Unassigned";

            if (!businessAreaGroups.ContainsKey(businessArea))
            {
                businessAreaGroups[businessArea] = new List<ProductDto>();
            }
            businessAreaGroups[businessArea].Add(product);
        }

        // Calculate statistics for each product
        var productStatuses = new Dictionary<string, CommissionSubmissionStatus>();
        foreach (var product in eligibleProducts)
        {
            var documentId = product.DocumentId ?? "";
            var submission = allSubmissions.GetValueOrDefault(documentId);
            var status = submission?.Status ?? CommissionSubmissionStatus.NotStarted;
            productStatuses[documentId] = status;
        }

        // Calculate business area completions
        var businessAreaCompletions = new List<BusinessAreaCompletion>();
        foreach (var group in businessAreaGroups.OrderBy(g => g.Key))
        {
            var baProducts = group.Value;
            var baTotal = baProducts.Count;
            var baCompleted = 0;
            var baInProgress = 0;
            var baNotStarted = 0;
            var baLate = 0;

            foreach (var product in baProducts)
            {
                var documentId = product.DocumentId ?? "";
                var status = productStatuses.GetValueOrDefault(documentId, CommissionSubmissionStatus.NotStarted);
                
                switch (status)
                {
                    case CommissionSubmissionStatus.Submitted:
                        baCompleted++;
                        break;
                    case CommissionSubmissionStatus.InProgress:
                        baInProgress++;
                        break;
                    case CommissionSubmissionStatus.Late:
                        baLate++;
                        break;
                    case CommissionSubmissionStatus.NotStarted:
                        baNotStarted++;
                        break;
                }
            }

            var baCompletionPercentage = baTotal > 0 ? (decimal)baCompleted / baTotal * 100 : 0;

            businessAreaCompletions.Add(new BusinessAreaCompletion
            {
                BusinessAreaName = group.Key,
                TotalProducts = baTotal,
                CompletedProducts = baCompleted,
                InProgressProducts = baInProgress,
                NotStartedProducts = baNotStarted,
                LateProducts = baLate,
                CompletionPercentage = Math.Round(baCompletionPercentage, 1)
            });
        }

        // Calculate overall statistics
        var totalProducts = eligibleProducts.Count;
        var completedProducts = businessAreaCompletions.Sum(ba => ba.CompletedProducts);
        var overallCompletionPercentage = totalProducts > 0 ? (decimal)completedProducts / totalProducts * 100 : 0;

        var viewModel = new BusinessAreasViewModel
        {
            Commission = selectedCommission,
            TotalProducts = totalProducts,
            CompletedProducts = completedProducts,
            InProgressProducts = businessAreaCompletions.Sum(ba => ba.InProgressProducts),
            NotStartedProducts = businessAreaCompletions.Sum(ba => ba.NotStartedProducts),
            LateProducts = businessAreaCompletions.Sum(ba => ba.LateProducts),
            CompletionPercentage = Math.Round(overallCompletionPercentage, 1),
            BusinessAreaCompletions = businessAreaCompletions
        };

        ViewBag.ActiveCommissions = activeCommissions;
        ViewBag.SelectedCommission = selectedCommission;

        return View("~/Views/ProductReporting/Commission/BusinessAreasIndex.cshtml", viewModel);
    }

    // GET: ProductReporting/BusinessAreasDetails/{businessAreaName}
    public async Task<IActionResult> BusinessAreasDetails(string businessAreaName, int? commissionId = null)
    {
        if (string.IsNullOrEmpty(businessAreaName))
        {
            TempData["ErrorMessage"] = "Business area name is required.";
            return RedirectToAction("BusinessAreasIndex");
        }

        // Get active commissions
        var activeCommissions = await _context.Commissions
            .Where(c => c.IsActive)
            .OrderByDescending(c => c.StartDate)
            .ToListAsync();

        if (!activeCommissions.Any())
        {
            TempData["ErrorMessage"] = "No active commissions are currently available.";
            return RedirectToAction("BusinessAreasIndex");
        }

        // Use selected commission or default to most recent
        var selectedCommission = commissionId.HasValue
            ? activeCommissions.FirstOrDefault(c => c.Id == commissionId.Value)
            : activeCommissions.FirstOrDefault();

        if (selectedCommission == null)
        {
            TempData["ErrorMessage"] = "Selected commission not found.";
            return RedirectToAction("BusinessAreasIndex");
        }

        // Get all products (only Active, only Published, exclude Decommissioned/Decommissioning Phase)
        var allProducts = await _productsApiService.GetAllProductsAsync();
        var eligibleProducts = allProducts
            .Where(p => p.State != null && 
                       p.State.Equals("Active", StringComparison.OrdinalIgnoreCase) &&
                       p.PublishedAt.HasValue &&
                       (string.IsNullOrEmpty(p.Phase) || 
                        (!p.Phase.Equals("Decommissioned", StringComparison.OrdinalIgnoreCase) &&
                         !p.Phase.Equals("Decommissioning", StringComparison.OrdinalIgnoreCase))))
            // Exclude products where the only Type is "Data" from performance reporting,
            // but keep products that have "Data" alongside another Type.
            .Where(p =>
            {
                var types = p.CategoryValues?
                    .Where(cv => cv.CategoryType?.Name?.Trim().Equals("Type", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(cv => cv.Name?.Trim() ?? string.Empty)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();
                
                // If no types, include the product
                if (!types.Any())
                    return true;
                
                // If only "Data" type (even if multiple entries), exclude it
                if (types.Count == 1 && types[0].Trim().Equals("Data", StringComparison.OrdinalIgnoreCase))
                    return false;
                
                // If all types are "Data", exclude it
                if (types.All(t => t.Trim().Equals("Data", StringComparison.OrdinalIgnoreCase)))
                    return false;
                
                // If has "Data" plus other types, include it
                return true;
            })
            .ToList();

        // Filter products by business area
        var businessAreaProducts = eligibleProducts
            .Where(p =>
            {
                var ba = p.CategoryValues?
                    .FirstOrDefault(cv => cv.CategoryType?.Name?.Equals("Business area", StringComparison.OrdinalIgnoreCase) == true)
                    ?.Name ?? "Unassigned";
                return ba.Equals(businessAreaName, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        // Get all commission submissions for this commission
        var allSubmissions = await _context.CommissionSubmissions
            .Include(cs => cs.MetricValues)
            .Where(cs => cs.CommissionId == selectedCommission.Id)
            .ToDictionaryAsync(cs => cs.ProductDocumentId, cs => cs);

        // Create product status view models
        var productStatuses = new List<BusinessAreaProductStatus>();
        foreach (var product in businessAreaProducts.OrderBy(p => p.Title))
        {
            var documentId = product.DocumentId ?? "";
            var submission = allSubmissions.GetValueOrDefault(documentId);
            var status = submission?.Status ?? CommissionSubmissionStatus.NotStarted;
            var completedMetrics = submission?.MetricValues?.Count(mv => mv.IsComplete) ?? 0;
            var totalMetrics = submission?.MetricValues?.Count ?? 0;

            productStatuses.Add(new BusinessAreaProductStatus
            {
                Product = product,
                Status = status,
                CompletedMetrics = completedMetrics,
                TotalMetrics = totalMetrics,
                Submission = submission
            });
        }

        // Calculate statistics
        var totalProducts = productStatuses.Count;
        var completedProducts = productStatuses.Count(p => p.Status == CommissionSubmissionStatus.Submitted);
        var inProgressProducts = productStatuses.Count(p => p.Status == CommissionSubmissionStatus.InProgress);
        var notStartedProducts = productStatuses.Count(p => p.Status == CommissionSubmissionStatus.NotStarted);
        var lateProducts = productStatuses.Count(p => p.Status == CommissionSubmissionStatus.Late);
        var completionPercentage = totalProducts > 0 ? (decimal)completedProducts / totalProducts * 100 : 0;

        var viewModel = new BusinessAreaDetailsViewModel
        {
            BusinessAreaName = businessAreaName,
            Commission = selectedCommission,
            TotalProducts = totalProducts,
            CompletedProducts = completedProducts,
            InProgressProducts = inProgressProducts,
            NotStartedProducts = notStartedProducts,
            LateProducts = lateProducts,
            CompletionPercentage = Math.Round(completionPercentage, 1),
            ProductStatuses = productStatuses
        };

        ViewBag.ActiveCommissions = activeCommissions;
        ViewBag.SelectedCommission = selectedCommission;

        return View("~/Views/ProductReporting/Commission/BusinessAreasDetails.cshtml", viewModel);
    }

    // GET: ProductReporting/GuidanceOnReporting
    public async Task<IActionResult> GuidanceOnReporting(int? commissionId = null)
    {
        // Get active commissions
        var activeCommissions = await _context.Commissions
            .Where(c => c.IsActive)
            .OrderByDescending(c => c.StartDate)
            .ToListAsync();

        if (!activeCommissions.Any())
        {
            ViewBag.Message = "No active commissions are currently available.";
            ViewBag.ActiveCommissions = activeCommissions;
            ViewBag.SelectedCommission = null;
            return View("~/Views/ProductReporting/Commission/GuidanceOnReporting.cshtml", new List<PerformanceMetric>());
        }

        // Use selected commission or default to most recent
        var selectedCommission = commissionId.HasValue
            ? activeCommissions.FirstOrDefault(c => c.Id == commissionId.Value)
            : activeCommissions.FirstOrDefault();

        if (selectedCommission == null)
        {
            TempData["ErrorMessage"] = "Selected commission not found.";
            ViewBag.ActiveCommissions = activeCommissions;
            ViewBag.SelectedCommission = null;
            return View("~/Views/ProductReporting/Commission/GuidanceOnReporting.cshtml", new List<PerformanceMetric>());
        }

        // Get all performance metrics (excluding disabled ones)
        var metrics = await _context.PerformanceMetrics
            .Where(m => !m.IsDisabled)
            .OrderBy(m => m.Identifier)
            .ToListAsync();

        ViewBag.ActiveCommissions = activeCommissions;
        ViewBag.SelectedCommission = selectedCommission;
        ViewBag.Metrics = metrics;

        return View("~/Views/ProductReporting/Commission/GuidanceOnReporting.cshtml", metrics);
    }
    
    // GET: ProductReporting/ReportOtherProduct
    public async Task<IActionResult> ReportOtherProduct()
    {
        var allProducts = await _productsApiService.GetAllProductsAsync();
        
        // Get current reporting period
        var now = DateTime.UtcNow;
        var currentYear = now.Month == 1 ? now.Year - 1 : now.Year;
        var currentMonth = now.Month == 1 ? 12 : now.Month - 1;
        
        // Load eligibility cache once
        var eligibilityCache = await _eligibilityService.LoadEligibilityCacheAsync();
        
        // Filter to only products requiring reporting
        var eligibleProducts = allProducts
            .Where(p => !string.IsNullOrEmpty(p.FipsId))
            .Where(p => p.State.Equals("Active", StringComparison.OrdinalIgnoreCase))
            .Where(p =>
            {
                var businessArea = p.CategoryValues?
                    .FirstOrDefault(cv => cv.CategoryType?.Name?.Equals("Business area", StringComparison.OrdinalIgnoreCase) == true)
                    ?.Name;
                return _eligibilityService.IsReportingRequired(p.FipsId!, businessArea, currentYear, currentMonth, eligibilityCache);
            })
            .OrderBy(p => p.Title)
            .ToList();
        
        ViewBag.CurrentYear = currentYear;
        ViewBag.CurrentMonth = currentMonth;
        return View("~/Views/ProductReporting/PerformanceMetrics/ReportOtherProduct.cshtml", eligibleProducts);
    }

    // GET: ProductReporting/ProductHistory/{documentId}
    // Supports both DocumentId (primary) and FipsId (legacy) for backwards compatibility
    public async Task<IActionResult> ProductHistory(string documentId)
    {
        if (string.IsNullOrEmpty(documentId))
        {
            return NotFound();
        }

        // Try to get product by DocumentId first, then FipsId for backwards compatibility
        var products = await _productsApiService.GetProductsAsync();
        var product = products?.FirstOrDefault(p => 
            p.DocumentId == documentId || p.FipsId == documentId);
        
        if (product == null)
        {
            return NotFound();
        }

        // Use DocumentId for database operations (primary identifier)
        var productDocumentId = product.DocumentId ?? documentId;
        
        // Get or create returns starting from October 2025 and 1 upcoming month
        var returns = await GetOrCreateReturns(productDocumentId, product.FipsId, 1);

        ViewBag.Product = product;
        return View("~/Views/ProductReporting/PerformanceMetrics/History.cshtml", returns);
    }

    // GET: ProductReporting/SubmitMetrics/{documentId}/2025/10
    // Supports both DocumentId (primary) and FipsId (legacy) for backwards compatibility
    public async Task<IActionResult> SubmitMetrics(string documentId, int year, int month)
    {
        if (string.IsNullOrEmpty(documentId))
        {
            return NotFound();
        }

        // Try to get product by DocumentId first, then FipsId for backwards compatibility
        var products = await _productsApiService.GetProductsAsync();
        var product = products?.FirstOrDefault(p => 
            p.DocumentId == documentId || p.FipsId == documentId);
        
        if (product == null)
        {
            return NotFound();
        }

        // Use DocumentId for database operations (primary identifier)
        var productDocumentId = product.DocumentId ?? documentId;

        // Get or create the return
        var productReturn = await GetOrCreateReturn(productDocumentId, product.FipsId, year, month);

        // Check if return is in a state that allows editing
        if (productReturn.Status == ReturnStatus.Submitted)
        {
            TempData["ErrorMessage"] = "This return has already been submitted and cannot be edited.";
            return RedirectToAction(nameof(ProductHistory), new { documentId = productDocumentId });
        }

        // Allow submissions up to 10 days before the due date
        var dueDate = _returnStatusService.GetReturnDueDate(year, month);
        var now = DateTime.UtcNow;
        var tenDaysBeforeDue = dueDate.AddDays(-10);
        
        // Check if we're within 10 days of the due date or past the period end
        var periodEndDate = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        var canSubmit = now >= periodEndDate || (now >= tenDaysBeforeDue && now <= dueDate.AddDays(1));
        
        if (!canSubmit && productReturn.Status == ReturnStatus.Upcoming)
        {
            TempData["WarningMessage"] = $"This return is not yet due. You can start submitting data from {tenDaysBeforeDue:dd MMM yyyy} (10 days before the due date of {dueDate:dd MMM yyyy}).";
        }

        // Get all performance metrics that are valid for this reporting period
        // A metric is valid if ValidFromYear/Month is <= the reporting period
        var allMetrics = await _context.PerformanceMetrics
            .Where(m => !m.IsDisabled && // Exclude disabled metrics
                   (m.ValidFromYear < year || 
                   (m.ValidFromYear == year && m.ValidFromMonth <= month)))
            .OrderBy(m => m.Identifier)
            .ToListAsync();
        
        // Filter by phase - only include metrics that apply to the product's phase
        var phaseFilteredMetrics = allMetrics.Where(m => 
        {
            // If no phases specified, metric applies to all phases
            if (string.IsNullOrEmpty(m.ApplicablePhases))
                return true;
            
            // If product has no phase, show all metrics
            if (string.IsNullOrEmpty(product.Phase))
                return true;
            
            // Check if product's phase is in the metric's applicable phases
            var applicablePhases = m.ApplicablePhases.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .ToList();
            
            return applicablePhases.Contains(product.Phase, StringComparer.OrdinalIgnoreCase);
        }).ToList();

        // Get all product types from category values
        var productTypes = new List<string>();
        if (product.CategoryValues != null)
        {
            _logger.LogInformation("Product {DocumentId} has {Count} category values", productDocumentId, product.CategoryValues.Count);
            foreach (var cv in product.CategoryValues)
            {
                _logger.LogInformation("  Category: {Name} (Type: {TypeName})", cv.Name, cv.CategoryType?.Name);
            }
            
            productTypes = product.CategoryValues
                .Where(cv => cv.CategoryType?.Name?.Equals("Type", StringComparison.OrdinalIgnoreCase) == true)
                .Select(cv => cv.Name)
                .ToList();
                
            if (productTypes.Any())
            {
                _logger.LogInformation("Product {DocumentId} has Types: {Types}", productDocumentId, string.Join(", ", productTypes));
            }
            else
            {
                _logger.LogWarning("Product {DocumentId} does not have any Type categories assigned", productDocumentId);
            }
        }
        else
        {
            _logger.LogWarning("Product {DocumentId} has no CategoryValues", productDocumentId);
        }

        // Filter by type - only include metrics where ANY of the product's types match ANY of the metric's applicable types
        var typeFilteredMetrics = phaseFilteredMetrics.Where(m => 
        {
            // If no types specified, metric applies to all types
            if (string.IsNullOrEmpty(m.ApplicableTypes))
            {
                _logger.LogInformation("Metric {MetricId} ({Title}) has no type restriction - including", m.Id, m.Title);
                return true;
            }
            
            // If product has no types, show all metrics anyway
            if (!productTypes.Any())
            {
                _logger.LogInformation("Metric {MetricId} ({Title}) has type restriction but product has no types - including anyway", m.Id, m.Title);
                return true;
            }
            
            // Check if ANY of the product's types match ANY of the metric's applicable types
            var applicableTypes = m.ApplicableTypes.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .ToList();
            
            var matches = productTypes.Any(pt => applicableTypes.Contains(pt, StringComparer.OrdinalIgnoreCase));
            if (matches)
            {
                var matchingTypes = productTypes.Where(pt => applicableTypes.Contains(pt, StringComparer.OrdinalIgnoreCase)).ToList();
                _logger.LogInformation("Metric {MetricId} ({Title}) matches product types '{ProductTypes}' - including", 
                    m.Id, m.Title, string.Join(", ", matchingTypes));
            }
            else
            {
                _logger.LogInformation("Metric {MetricId} ({Title}) requires types [{MetricTypes}] but product has [{ProductTypes}] with no overlap - excluding", 
                    m.Id, m.Title, string.Join(", ", applicableTypes), string.Join(", ", productTypes));
            }
            
            return matches;
        }).ToList();
        
        _logger.LogInformation("After phase filtering: {Count} metrics. After type filtering: {Count2} metrics", 
            phaseFilteredMetrics.Count, typeFilteredMetrics.Count);

        // Get existing metric values for this return to check conditional dependencies
        var existingMetricValues = await _context.ProductMetricValues
            .Include(mv => mv.PerformanceMetric)
            .Where(mv => mv.ProductReturnId == productReturn.Id)
            .ToListAsync();

        // Filter by conditional dependencies - only show metrics whose conditions are met
        var metrics = typeFilteredMetrics.Where(m => 
        {
            // If no conditional dependency, always show
            if (!m.ConditionalOnMetricId.HasValue)
                return true;
            
            // Check if the parent metric has a value
            var parentMetricValue = existingMetricValues
                .FirstOrDefault(mv => mv.PerformanceMetricId == m.ConditionalOnMetricId.Value);
            
            // Show if parent metric exists and has a value
            return parentMetricValue != null && !string.IsNullOrWhiteSpace(parentMetricValue.Value);
        }).ToList();

        // Ensure we have a metric value entry for each valid metric
        foreach (var metric in metrics)
        {
            if (!existingMetricValues.Any(mv => mv.PerformanceMetricId == metric.Id))
            {
                var newValue = new ProductMetricValue
                {
                    ProductReturnId = productReturn.Id,
                    PerformanceMetricId = metric.Id,
                    IsComplete = false
                };
                _context.ProductMetricValues.Add(newValue);
                existingMetricValues.Add(newValue);
            }
        }
        
        // Use only the metrics that passed all filters
        var metricValues = existingMetricValues
            .Where(mv => metrics.Any(m => m.Id == mv.PerformanceMetricId))
            .ToList();

        await _context.SaveChangesAsync();

        // Get previous month's values for comparison
        var previousMonth = month == 1 ? 12 : month - 1;
        var previousYear = month == 1 ? year - 1 : year;
        
        var previousReturn = await _context.ProductReturns
            .Include(pr => pr.MetricValues)
            .FirstOrDefaultAsync(pr => (pr.ProductDocumentId == productDocumentId || 
                                       (string.IsNullOrEmpty(pr.ProductDocumentId) && pr.FipsId == product.FipsId)) &&
                                      pr.Year == previousYear && 
                                      pr.Month == previousMonth);
        
        if (previousReturn != null)
        {
            foreach (var metricValue in metricValues)
            {
                var previousMetricValue = previousReturn.MetricValues
                    .FirstOrDefault(mv => mv.PerformanceMetricId == metricValue.PerformanceMetricId);
                
                if (previousMetricValue != null && !string.IsNullOrWhiteSpace(previousMetricValue.Value))
                {
                    metricValue.PreviousValue = previousMetricValue.Value;
                }
            }
        }

        // Allow editing if within 10 days before due date or past period end
        var dueDateForReadOnly = _returnStatusService.GetReturnDueDate(year, month);
        var nowForReadOnly = DateTime.UtcNow;
        var tenDaysBeforeDueForReadOnly = dueDateForReadOnly.AddDays(-10);
        var periodEndDateForReadOnly = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        var canEdit = !(productReturn.Status == ReturnStatus.Submitted) && 
                      (nowForReadOnly >= periodEndDateForReadOnly || (nowForReadOnly >= tenDaysBeforeDueForReadOnly && nowForReadOnly <= dueDateForReadOnly.AddDays(1)));

        ViewBag.Product = product;
        ViewBag.ProductReturn = productReturn;
        ViewBag.IsReadOnly = !canEdit;
        
        return View("~/Views/ProductReporting/PerformanceMetrics/Submit.cshtml", metricValues.OrderBy(mv => mv.PerformanceMetric?.Identifier).ToList());
    }

    // POST: ProductReporting/SaveMetricValue
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveMetricValue(int id, string? value, bool isNotCaptured = false, string? notCapturedReason = null, string? reasonForDifference = null)
    {
        Console.WriteLine("========================================");
        Console.WriteLine($"SaveMetricValue called at {DateTime.Now:HH:mm:ss}");
        Console.WriteLine($"  ID: {id}");
        Console.WriteLine($"  Value: '{value ?? "(null)"}'");
        Console.WriteLine($"  IsNotCaptured: {isNotCaptured}");
        Console.WriteLine($"  NotCapturedReason: '{notCapturedReason ?? "(null)"}'");
        Console.WriteLine($"  ReasonForDifference: '{reasonForDifference ?? "(null)"}'");
        Console.WriteLine("========================================");
        
        _logger.LogInformation("SaveMetricValue called - ID: {Id}, Value: '{Value}', IsNotCaptured: {IsNotCaptured}, NotCapturedReason: '{Reason}'", 
            id, value ?? "(null)", isNotCaptured, notCapturedReason ?? "(null)");
        
        var metricValue = await _context.ProductMetricValues
            .Include(mv => mv.PerformanceMetric)
            .Include(mv => mv.ProductReturn)
            .FirstOrDefaultAsync(mv => mv.Id == id);

        if (metricValue == null)
        {
            return Json(new { success = false, message = "Metric value not found" });
        }

        // Check if return is editable
        if (metricValue.ProductReturn?.Status == ReturnStatus.Submitted)
        {
            return Json(new { success = false, message = "This return has been submitted and cannot be edited" });
        }

        if (metricValue.ProductReturn?.Status == ReturnStatus.Upcoming)
        {
            return Json(new { success = false, message = "This return is not yet due for submission" });
        }

        try
        {
            // If metric is marked as not captured, log it and mark as complete
            if (isNotCaptured)
            {
                _logger.LogInformation(
                    "Metric not captured. FIPS ID: {FipsId}, Metric: {MetricId} ({MetricTitle}), " +
                    "Period: {Year}-{Month}, Reason: {Reason}",
                    metricValue.ProductReturn?.FipsId,
                    metricValue.PerformanceMetricId,
                    metricValue.PerformanceMetric?.Title,
                    metricValue.ProductReturn?.Year,
                    metricValue.ProductReturn?.Month,
                    notCapturedReason ?? "No reason provided"
                );
                
                metricValue.Value = string.Empty;
                metricValue.IsComplete = true;
                metricValue.IsNotCaptured = true;
                metricValue.NotCapturedReason = notCapturedReason;
            }
            else
            {
                // Validate the value based on the metric's validation rules
                if (metricValue.PerformanceMetric != null && !string.IsNullOrWhiteSpace(value))
                {
                    var validationResult = ValidateMetricValue(value, metricValue.PerformanceMetric);
                    if (!validationResult.IsValid)
                    {
                        return Json(new { success = false, message = validationResult.ErrorMessage });
                    }
                }

                // Log reason for significant difference if provided
                if (!string.IsNullOrWhiteSpace(reasonForDifference))
                {
                    _logger.LogInformation(
                        "Metric value change with significant difference. FIPS ID: {FipsId}, Metric: {MetricId} ({MetricTitle}), " +
                        "Period: {Year}-{Month}, New Value: {NewValue}, Reason: {Reason}",
                        metricValue.ProductReturn?.FipsId,
                        metricValue.PerformanceMetricId,
                        metricValue.PerformanceMetric?.Title,
                        metricValue.ProductReturn?.Year,
                        metricValue.ProductReturn?.Month,
                        value ?? "",
                        reasonForDifference
                    );
                }

                metricValue.Value = value ?? string.Empty;
                metricValue.IsNotCaptured = false;
                metricValue.NotCapturedReason = null;
                metricValue.ReasonForDifference = reasonForDifference;
                
                // Mark as complete based on whether value is provided
                metricValue.IsComplete = !string.IsNullOrWhiteSpace(value);
            }
            
            metricValue.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            Console.WriteLine($"✓ Metric value saved successfully!");
            Console.WriteLine($"  → IsComplete: {metricValue.IsComplete}");
            Console.WriteLine($"  → Value: '{metricValue.Value}'");
            Console.WriteLine($"  → IsNotCaptured: {metricValue.IsNotCaptured}");
            Console.WriteLine($"  → NotCapturedReason: '{metricValue.NotCapturedReason ?? "(null)"}'");
            Console.WriteLine($"  → ReasonForDifference: '{metricValue.ReasonForDifference ?? "(null)"}'");
            
            return Json(new { success = true, message = "Value saved successfully" });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ ERROR saving metric value: {ex.Message}");
            _logger.LogError(ex, "Error saving metric value");
            return Json(new { success = false, message = "An error occurred while saving" });
        }
    }

    // POST: ProductReporting/SubmitReturn
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitReturn(int returnId)
    {
        var productReturn = await _context.ProductReturns
            .Include(pr => pr.MetricValues)
            .FirstOrDefaultAsync(pr => pr.Id == returnId);

        if (productReturn == null)
        {
            TempData["ErrorMessage"] = "Return not found";
            return RedirectToAction(nameof(PerformanceMetrics));
        }

        // Check if all metrics are complete
        var incompleteCount = productReturn.MetricValues.Count(mv => !mv.IsComplete);
        if (incompleteCount > 0)
        {
            TempData["ErrorMessage"] = $"Cannot submit: {incompleteCount} metric(s) still need to be completed";
            return RedirectToAction(nameof(SubmitMetrics), new { documentId = productReturn.ProductDocumentId ?? productReturn.FipsId ?? "", year = productReturn.Year, month = productReturn.Month });
        }

        try
        {
            productReturn.Status = ReturnStatus.Submitted;
            productReturn.SubmittedDate = DateTime.UtcNow;
            productReturn.SubmittedBy = User.Identity?.Name ?? "Unknown";
            productReturn.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Return for {productReturn.Month:D2}/{productReturn.Year} has been submitted successfully";
            return RedirectToAction(nameof(ProductHistory), new { documentId = productReturn.ProductDocumentId ?? productReturn.FipsId ?? "" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting return");
            TempData["ErrorMessage"] = "An error occurred while submitting the return";
            return RedirectToAction(nameof(SubmitMetrics), new { documentId = productReturn.ProductDocumentId ?? productReturn.FipsId ?? "", year = productReturn.Year, month = productReturn.Month });
        }
    }

    // POST: ProductReporting/UnsubmitReturn
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnsubmitReturn(int returnId)
    {
        var productReturn = await _context.ProductReturns
            .FirstOrDefaultAsync(pr => pr.Id == returnId);

        if (productReturn == null)
        {
            TempData["ErrorMessage"] = "Return not found";
            return RedirectToAction(nameof(PerformanceMetrics));
        }

        // Check if return is actually submitted
        if (productReturn.Status != ReturnStatus.Submitted)
        {
            TempData["ErrorMessage"] = "This return has not been submitted yet";
            return RedirectToAction(nameof(ProductHistory), new { documentId = productReturn.ProductDocumentId ?? productReturn.FipsId ?? "" });
        }

        try
        {
            // Recalculate status (will be Due or Late depending on due date)
            productReturn.Status = _returnStatusService.CalculateReturnStatus(productReturn.Year, productReturn.Month, null);
            productReturn.SubmittedDate = null;
            productReturn.SubmittedBy = null;
            productReturn.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Return for {new DateTime(productReturn.Year, productReturn.Month, 1).ToString("MMMM yyyy")} has been unsubmitted and is now available for editing";
            return RedirectToAction(nameof(ProductHistory), new { documentId = productReturn.ProductDocumentId ?? productReturn.FipsId ?? "" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unsubmitting return");
            TempData["ErrorMessage"] = "An error occurred while unsubmitting the return";
            return RedirectToAction(nameof(ProductHistory), new { documentId = productReturn.ProductDocumentId ?? productReturn.FipsId ?? "" });
        }
    }

    #region Helper Methods

    private async Task<List<ProductReturn>> GetOrCreateReturns(string productDocumentId, string? fipsId, int upcomingMonths)
    {
        var returns = new List<ProductReturn>();
        var now = DateTime.UtcNow;

        // Get product to check business area - try by DocumentId first, then FipsId
        var products = await _productsApiService.GetProductsAsync();
        var product = products?.FirstOrDefault(p => 
            p.DocumentId == productDocumentId || p.FipsId == productDocumentId || p.FipsId == fipsId);
        
        var businessArea = product?.CategoryValues?
            .FirstOrDefault(cv => cv.CategoryType?.Name?.Equals("Business area", StringComparison.OrdinalIgnoreCase) == true)
            ?.Name;

        // PERFORMANCE OPTIMIZATION: Load eligibility cache once
        var eligibilityCache = await _eligibilityService.LoadEligibilityCacheAsync();

        // Start from October 2025
        var startDate = new DateTime(2025, 10, 1);
        
        // End date is current month + upcoming months, but not before October 2025
        var endDate = now.AddMonths(upcomingMonths);
        if (endDate < startDate)
        {
            endDate = startDate.AddMonths(upcomingMonths);
        }

        for (var date = startDate; 
             date <= endDate; 
             date = date.AddMonths(1))
        {
            // Check if reporting is required (using cached data - NO database query)
            // Use fipsId for eligibility check (legacy compatibility)
            var checkId = fipsId ?? productDocumentId;
            var reportingRequired = _eligibilityService.IsReportingRequired(
                checkId, 
                businessArea, 
                date.Year, 
                date.Month,
                eligibilityCache);
            
            // Skip this period if reporting is not required
            if (!reportingRequired)
            {
                continue;
            }
            
            // Try to find by ProductDocumentId first, then FipsId for backwards compatibility
            var existingReturn = await _context.ProductReturns
                .Include(pr => pr.MetricValues)
                    .ThenInclude(mv => mv.PerformanceMetric)
                .AsSplitQuery()
                .FirstOrDefaultAsync(pr => 
                    (pr.ProductDocumentId == productDocumentId || (string.IsNullOrEmpty(pr.ProductDocumentId) && pr.FipsId == fipsId)) 
                    && pr.Year == date.Year && pr.Month == date.Month);

            if (existingReturn != null)
            {
                existingReturn.Status = _returnStatusService.CalculateReturnStatus(existingReturn.Year, existingReturn.Month, existingReturn.SubmittedDate);
                returns.Add(existingReturn);
            }
            else
            {
                var newReturn = new ProductReturn
                {
                    ProductDocumentId = productDocumentId,
                    FipsId = fipsId, // Keep for backwards compatibility
                    Year = date.Year,
                    Month = date.Month,
                    Status = _returnStatusService.CalculateReturnStatus(date.Year, date.Month, null)
                };
                returns.Add(newReturn);
            }
        }

        return returns.OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).ToList();
    }

    private async Task<ProductReturn> GetOrCreateReturn(string productDocumentId, string? fipsId, int year, int month)
    {
        // Try to find by ProductDocumentId first, then FipsId for backwards compatibility
        var existingReturn = await _context.ProductReturns
            .FirstOrDefaultAsync(pr => 
                (pr.ProductDocumentId == productDocumentId || (string.IsNullOrEmpty(pr.ProductDocumentId) && pr.FipsId == fipsId)) 
                && pr.Year == year && pr.Month == month);

        if (existingReturn != null)
        {
            // Update ProductDocumentId if it was missing
            if (string.IsNullOrEmpty(existingReturn.ProductDocumentId) && !string.IsNullOrEmpty(productDocumentId))
            {
                existingReturn.ProductDocumentId = productDocumentId;
                await _context.SaveChangesAsync();
            }
            
            existingReturn.Status = _returnStatusService.CalculateReturnStatus(year, month, existingReturn.SubmittedDate);
            return existingReturn;
        }

        var newReturn = new ProductReturn
        {
            ProductDocumentId = productDocumentId,
            FipsId = fipsId, // Keep for backwards compatibility
            Year = year,
            Month = month,
            Status = _returnStatusService.CalculateReturnStatus(year, month, null)
        };

        _context.ProductReturns.Add(newReturn);
        await _context.SaveChangesAsync();

        return newReturn;
    }

    private async Task<ProductReturnStatusViewModel?> CreateProductStatusViewModelAsync(
        ProductDto product, 
        Dictionary<string, ProductReturn> returnsDict,
        int currentYear,
        int currentMonth,
        string? userEmail,
        PerformanceReportingEligibilityCache eligibilityCache)
    {
        if (string.IsNullOrEmpty(product.FipsId)) return null;
        
        // Extract business area from category_values
        var businessArea = product.CategoryValues?
            .FirstOrDefault(cv => cv.CategoryType?.Name?.Equals("Business area", StringComparison.OrdinalIgnoreCase) == true)
            ?.Name;
        
        // Check if reporting is required (using cached data - NO database query)
        var reportingRequired = _eligibilityService.IsReportingRequired(
            product.FipsId, 
            businessArea, 
            currentYear, 
            currentMonth,
            eligibilityCache);
        
        // Check if period is excluded and business area has override
        var periodExcluded = eligibilityCache.PeriodExclusions.Any(e => e.Year == currentYear && e.Month == currentMonth);
        var businessAreaReporting = false;
        var isBusinessAreaInScope = false;
        if (!string.IsNullOrEmpty(businessArea))
        {
            // Check if business area is configured for reporting (in scope)
            isBusinessAreaInScope = eligibilityCache.BusinessAreaConfigs.Any(c => 
                c.BusinessAreaName == businessArea &&
                c.IsActive);
            
            // Check if business area reporting applies for current period
            businessAreaReporting = eligibilityCache.BusinessAreaConfigs.Any(c => 
                c.BusinessAreaName == businessArea &&
                c.IsActive &&
                (c.ApplicableFromYear < currentYear || 
                 (c.ApplicableFromYear == currentYear && c.ApplicableFromMonth <= currentMonth)) &&
                (!c.ApplicableUntilYear.HasValue || 
                 c.ApplicableUntilYear.Value > currentYear || 
                 (c.ApplicableUntilYear.Value == currentYear && c.ApplicableUntilMonth >= currentMonth)));
        }
        
        // Determine suspension reason and next reporting period
        string? suspensionReason = null;
        DateTime? nextReportingPeriod = null;
        DateTime? nextReportingPeriodDueDate = null;
        
        if (!reportingRequired && periodExcluded && !businessAreaReporting)
        {
            suspensionReason = "Reporting is not required at this time due to period exclusion";
            var nextPeriod = await _eligibilityService.FindNextActiveReportingPeriodAsync(currentYear, currentMonth, businessArea, eligibilityCache);
            if (nextPeriod.HasValue)
            {
                nextReportingPeriod = new DateTime(nextPeriod.Value.Year, nextPeriod.Value.Month, 1);
                // Calculate the due date for the next reporting period
                nextReportingPeriodDueDate = _returnStatusService.GetReturnDueDate(nextPeriod.Value.Year, nextPeriod.Value.Month);
            }
        }
        
        var currentReturn = returnsDict.TryGetValue(product.FipsId, out var returnValue) ? returnValue : null;
        
        ReturnStatus? status = null;
        int? completedMetrics = null;
        int? totalMetrics = null;
        DateTime? currentPeriodDueDate = null;
        
        // Calculate status if:
        // 1. There's an existing return, OR
        // 2. Reporting is required (includes business area overrides), OR
        // 3. Business area is in scope and has an override (even if period is excluded)
        var shouldCalculateStatus = currentReturn != null || 
                                   reportingRequired || 
                                   (isBusinessAreaInScope && businessAreaReporting);
        
        if (currentReturn != null)
        {
            status = _returnStatusService.CalculateReturnStatus(
                currentReturn.Year, 
                currentReturn.Month, 
                currentReturn.SubmittedDate);
                
            totalMetrics = currentReturn.MetricValues?.Count ?? 0;
            completedMetrics = currentReturn.MetricValues?.Count(mv => mv.IsComplete) ?? 0;
            currentPeriodDueDate = _returnStatusService.GetReturnDueDate(currentReturn.Year, currentReturn.Month);
        }
        else if (shouldCalculateStatus)
        {
            // Check if this period has started yet
            var periodStart = new DateTime(currentYear, currentMonth, 1);
            if (periodStart >= new DateTime(2025, 10, 1))
            {
                status = _returnStatusService.CalculateReturnStatus(currentYear, currentMonth, null);
                currentPeriodDueDate = _returnStatusService.GetReturnDueDate(currentYear, currentMonth);
            }
        }
        
        // Find the user's role for this product
        // Check product_contacts first, then check service_owner and other role fields
        string? userRole = null;
        List<string> roles = new List<string>();
        
        if (!string.IsNullOrEmpty(userEmail))
        {
            // Check product_contacts
            if (product.ProductContacts != null)
            {
                var userContact = product.ProductContacts.FirstOrDefault(pc => 
                    pc.UsersPermissionsUser != null && 
                    !string.IsNullOrEmpty(pc.UsersPermissionsUser.Email) &&
                    pc.UsersPermissionsUser.Email.Equals(userEmail, StringComparison.OrdinalIgnoreCase));
                
                if (!string.IsNullOrEmpty(userContact?.Role))
                {
                    roles.Add(userContact.Role);
                }
            }
            
            // Check service_owner (relation to entra-user)
            if (product.ServiceOwner != null && 
                !string.IsNullOrEmpty(product.ServiceOwner.EmailAddress) &&
                product.ServiceOwner.EmailAddress.Equals(userEmail, StringComparison.OrdinalIgnoreCase))
            {
                roles.Add("Service Owner");
            }
        }
        
        // Combine roles if user has multiple
        userRole = roles.Any() ? string.Join(", ", roles) : null;
        
        return new ProductReturnStatusViewModel
        {
            Product = product,
            CurrentPeriodYear = currentYear,
            CurrentPeriodMonth = currentMonth,
            Status = status,
            CompletedMetrics = completedMetrics,
            TotalMetrics = totalMetrics,
            UserRole = userRole,
            IsReportingRequired = reportingRequired,
            ReportingSuspensionReason = suspensionReason,
            NextReportingPeriod = nextReportingPeriod,
            NextReportingPeriodDueDate = nextReportingPeriodDueDate,
            IsBusinessAreaInScope = isBusinessAreaInScope,
            CurrentPeriodDueDate = currentPeriodDueDate,
            IsPeriodExcluded = periodExcluded,
            HasBusinessAreaOverride = businessAreaReporting
        };
    }

    private Task<List<PerformanceMetric>> GetApplicableMetricsForProductAsync(ProductDto product, int commissionId) =>
        CommissionReportingMetricsHelper.GetApplicableMetricsForProductAsync(_context, product, commissionId);

    private (bool IsValid, string? ErrorMessage) ValidateMetricValue(string value, PerformanceMetric metric) =>
        CommissionReportingMetricsHelper.ValidateMetricValue(value, metric);

    // GET: ProductReporting/ReportingDates
    public async Task<IActionResult> ReportingDates()
    {
        // Get the current user's email
        var userEmail = User.Identity?.Name;
        
        // Get current reporting period (previous month)
        var now = DateTime.UtcNow;
        var currentYear = now.Month == 1 ? now.Year - 1 : now.Year;
        var currentMonth = now.Month == 1 ? 12 : now.Month - 1;
        
        // PERFORMANCE OPTIMIZATION: Load API calls in parallel, but execute database queries sequentially
        // to avoid DbContext concurrency issues
        // Fetch user's products in parallel (these are API calls, not DB queries)
        var userProductsByServiceOwnerTask = _productsApiService.GetProductsByServiceOwnerAsync(userEmail);
        var userProductsByProductManagerTask = _productsApiService.GetProductsByProductManagerAsync(userEmail);
        var userProductsByDeliveryManagerTask = _productsApiService.GetProductsByDeliveryManagerAsync(userEmail);
        var userProductsByReportingUserTask = _productsApiService.GetProductsByReportingUserAsync(userEmail);
        
        // Wait for API calls to complete (these don't use DbContext)
        await Task.WhenAll(
            userProductsByServiceOwnerTask,
            userProductsByProductManagerTask,
            userProductsByDeliveryManagerTask,
            userProductsByReportingUserTask);
        
        // Get results from API calls
        var productsByServiceOwner = await userProductsByServiceOwnerTask;
        var productsByProductManager = await userProductsByProductManagerTask;
        var productsByDeliveryManager = await userProductsByDeliveryManagerTask;
        var productsByReportingUser = await userProductsByReportingUserTask;
        
        // Execute database queries sequentially to avoid DbContext concurrency issues
        // LoadEligibilityCacheAsync makes database queries, so it must complete before other DB queries
        var eligibilityCache = await _eligibilityService.LoadEligibilityCacheAsync();
        
        // Now execute other database queries sequentially
        // Use AsNoTracking() for read-only queries to improve performance
        var overrides = await _context.PerformanceReportingDueDateOverrides
            .AsNoTracking()
            .Where(o => o.IsActive)
            .ToDictionaryAsync(o => (o.ReportingYear, o.ReportingMonth), o => o);
        var periodExclusions = await _context.PerformanceReportingPeriodExclusions
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.Year)
            .ThenBy(e => e.Month)
            .ToListAsync();
        var businessAreaConfigs = await _context.PerformanceReportingBusinessAreaConfigs
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.BusinessAreaName)
            .ThenBy(c => c.ApplicableFromYear)
            .ThenBy(c => c.ApplicableFromMonth)
            .ToListAsync();
        
        // Combine and deduplicate user's products
        var userProducts = productsByServiceOwner
            .Concat(productsByProductManager)
            .Concat(productsByDeliveryManager)
            .Concat(productsByReportingUser)
            .GroupBy(p => p.FipsId)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => g.First())
            .ToList();
        
        // Build list of reporting periods with their due dates
        var startDate = new DateTime(2025, 10, 1); // Reporting started in October 2025
        var endDate = now.AddMonths(12); // Show next 12 months
        var reportingPeriods = new List<(int Year, int Month, DateTime DueDate, bool HasOverride, string? OverrideReason)>();
        
        for (var date = startDate; date <= endDate; date = date.AddMonths(1))
        {
            var year = date.Year;
            var month = date.Month;
            
            // Check if there's an override
            var hasOverride = overrides.TryGetValue((year, month), out var overrideValue);
            
            // Calculate due date (will use override if exists, otherwise default rule)
            var dueDate = _returnStatusService.GetReturnDueDate(year, month);
            
            reportingPeriods.Add((
                year,
                month,
                dueDate,
                hasOverride,
                overrideValue?.Reason
            ));
        }
        
        // Calculate counts efficiently (only for user's products, not all products)
        var fipsIds = userProducts.Where(p => !string.IsNullOrEmpty(p.FipsId)).Select(p => p.FipsId).ToList();
        
        // Only fetch returns without MetricValues (not needed for counts)
        var userReturns = await _context.ProductReturns
            .AsNoTracking()
            .Where(pr => fipsIds.Contains(pr.FipsId) && pr.Year == currentYear && pr.Month == currentMonth)
            .ToDictionaryAsync(pr => pr.FipsId, pr => pr);

        // Calculate counts efficiently - only process user's products
        var tasksCount = 0;
        var yourProductsCount = userProducts.Count;
        
        // Only calculate task count if user has products
        if (userProducts.Any())
        {
            // Create view models for user's products to calculate counts (simplified)
            var userProductStatuses = new List<ProductReturnStatusViewModel>();
            foreach (var product in userProducts)
            {
                var vm = await CreateProductStatusViewModelAsync(product, userReturns, currentYear, currentMonth, userEmail, eligibilityCache);
                if (vm != null)
                {
                    userProductStatuses.Add(vm);
                }
            }
            
            tasksCount = userProductStatuses.Count(p => 
                (p.IsReportingRequired || (p.IsBusinessAreaInScope && p.Status.HasValue)) && 
                (p.Status == ReturnStatus.Due || p.Status == ReturnStatus.Late));
        }
        
        // PERFORMANCE OPTIMIZATION: Get all products count efficiently
        // Count distinct products that have returns for the current period
        // This is a simple count query that doesn't require loading all product data
        var allProductsCount = await _context.ProductReturns
            .AsNoTracking()
            .Where(pr => pr.Year == currentYear && pr.Month == currentMonth && !string.IsNullOrEmpty(pr.FipsId))
            .Select(pr => pr.FipsId)
            .Distinct()
            .CountAsync();
        
        ViewBag.ReportingPeriods = reportingPeriods;
        ViewBag.DefaultRule = "Returns are due by the 3rd working day of the following month";
        ViewBag.PeriodExclusions = periodExclusions;
        ViewBag.BusinessAreaConfigs = businessAreaConfigs;
        ViewBag.TasksCount = tasksCount;
        ViewBag.YourProductsCount = yourProductsCount;
        ViewBag.AllProductsCount = allProductsCount;
        
        return View("~/Views/ProductReporting/PerformanceMetrics/ReportingDates.cshtml");
    }

    // GET: ProductReporting/WhatYouNeedToReport
    public async Task<IActionResult> WhatYouNeedToReport()
    {
        // Get the current user's email
        var userEmail = User.Identity?.Name;
        
        // Get current reporting period (previous month)
        var now = DateTime.UtcNow;
        var currentYear = now.Month == 1 ? now.Year - 1 : now.Year;
        var currentMonth = now.Month == 1 ? 12 : now.Month - 1;
        
        // PERFORMANCE OPTIMIZATION: Load API calls in parallel, but execute database queries sequentially
        // to avoid DbContext concurrency issues
        // Fetch user's products in parallel (these are API calls, not DB queries)
        var userProductsByServiceOwnerTask = _productsApiService.GetProductsByServiceOwnerAsync(userEmail);
        var userProductsByProductManagerTask = _productsApiService.GetProductsByProductManagerAsync(userEmail);
        var userProductsByDeliveryManagerTask = _productsApiService.GetProductsByDeliveryManagerAsync(userEmail);
        var userProductsByReportingUserTask = _productsApiService.GetProductsByReportingUserAsync(userEmail);
        var eligibilityCacheTask = _eligibilityService.LoadEligibilityCacheAsync();
        
        // Wait for API calls to complete
        await Task.WhenAll(
            userProductsByServiceOwnerTask,
            userProductsByProductManagerTask,
            userProductsByDeliveryManagerTask,
            userProductsByReportingUserTask,
            eligibilityCacheTask);
        
        // Get results from API calls
        var productsByServiceOwner = await userProductsByServiceOwnerTask;
        var productsByProductManager = await userProductsByProductManagerTask;
        var productsByDeliveryManager = await userProductsByDeliveryManagerTask;
        var productsByReportingUser = await userProductsByReportingUserTask;
        var eligibilityCache = await eligibilityCacheTask;
        
        // Execute database queries sequentially to avoid DbContext concurrency issues
        // Use AsNoTracking() for read-only queries to improve performance
        var metrics = await _context.PerformanceMetrics
            .AsNoTracking()
            .Where(m => !m.IsDisabled)
            .OrderBy(m => m.Identifier)
            .ToListAsync();
        
        // Combine and deduplicate user's products
        var userProducts = productsByServiceOwner
            .Concat(productsByProductManager)
            .Concat(productsByDeliveryManager)
            .Concat(productsByReportingUser)
            .GroupBy(p => p.FipsId)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => g.First())
            .ToList();
        
        // Calculate counts efficiently (only for user's products, not all products)
        var fipsIds = userProducts.Where(p => !string.IsNullOrEmpty(p.FipsId)).Select(p => p.FipsId).ToList();
        
        // Execute database queries sequentially to avoid DbContext concurrency issues
        // Only fetch returns without MetricValues (not needed for counts)
        var userReturns = await _context.ProductReturns
            .AsNoTracking()
            .Where(pr => fipsIds.Contains(pr.FipsId) && pr.Year == currentYear && pr.Month == currentMonth)
            .ToDictionaryAsync(pr => pr.FipsId, pr => pr);

        // Calculate counts efficiently - only process user's products
        var tasksCount = 0;
        var yourProductsCount = userProducts.Count;
        
        // Only calculate task count if user has products
        if (userProducts.Any())
        {
            // Create view models for user's products to calculate counts
            var userProductStatuses = new List<ProductReturnStatusViewModel>();
            foreach (var product in userProducts)
            {
                var vm = await CreateProductStatusViewModelAsync(product, userReturns, currentYear, currentMonth, userEmail, eligibilityCache);
                if (vm != null)
                {
                    userProductStatuses.Add(vm);
                }
            }
            
            tasksCount = userProductStatuses.Count(p => 
                (p.IsReportingRequired || (p.IsBusinessAreaInScope && p.Status.HasValue)) && 
                (p.Status == ReturnStatus.Due || p.Status == ReturnStatus.Late));
        }
        
        // Execute database queries sequentially to avoid DbContext concurrency issues
        // PERFORMANCE OPTIMIZATION: Get all products count efficiently
        // Count distinct products that have returns for the current period
        // This is a simple count query that doesn't require loading all product data
        var allProductsCount = await _context.ProductReturns
            .AsNoTracking()
            .Where(pr => pr.Year == currentYear && pr.Month == currentMonth && !string.IsNullOrEmpty(pr.FipsId))
            .Select(pr => pr.FipsId)
            .Distinct()
            .CountAsync();
        
        ViewBag.Metrics = metrics;
        ViewBag.TasksCount = tasksCount;
        ViewBag.YourProductsCount = yourProductsCount;
        ViewBag.AllProductsCount = allProductsCount;
        
        return View("~/Views/ProductReporting/PerformanceMetrics/WhatYouNeedToReport.cshtml");
    }

    // GET: ProductReporting/Guidance
    public async Task<IActionResult> Guidance()
    {
        // Get the current user's email
        var userEmail = User.Identity?.Name;
        
        // Get current reporting period (previous month)
        var now = DateTime.UtcNow;
        var currentYear = now.Month == 1 ? now.Year - 1 : now.Year;
        var currentMonth = now.Month == 1 ? 12 : now.Month - 1;
        
        // Load eligibility cache once
        var eligibilityCache = await _eligibilityService.LoadEligibilityCacheAsync();
        
        // Get all metrics
        var metrics = await _context.PerformanceMetrics
            .Where(m => !m.IsDisabled)
            .OrderBy(m => m.Identifier)
            .ToListAsync();
        
        // Get reporting dates information
        var overrides = await _context.PerformanceReportingDueDateOverrides
            .AsNoTracking()
            .Where(o => o.IsActive)
            .ToDictionaryAsync(o => (o.ReportingYear, o.ReportingMonth), o => o);
        var periodExclusions = await _context.PerformanceReportingPeriodExclusions
            .AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.Year)
            .ThenBy(e => e.Month)
            .ToListAsync();
        var businessAreaConfigs = await _context.PerformanceReportingBusinessAreaConfigs
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.ApplicableFromYear)
            .ThenBy(c => c.ApplicableFromMonth)
            .ToListAsync();
        
        // Calculate reporting periods (next 12 months)
        var reportingPeriods = new List<(int Year, int Month, DateTime DueDate, bool HasOverride, string? OverrideReason)>();
        for (int i = 0; i < 12; i++)
        {
            var futureMonth = currentMonth + i;
            var futureYear = currentYear;
            while (futureMonth > 12)
            {
                futureMonth -= 12;
                futureYear += 1;
            }
            
            var hasOverride = overrides.ContainsKey((futureYear, futureMonth));
            var overrideReason = hasOverride ? overrides[(futureYear, futureMonth)].Reason : null;
            var dueDate = _returnStatusService.GetReturnDueDate(futureYear, futureMonth);
            
            reportingPeriods.Add((futureYear, futureMonth, dueDate, hasOverride, overrideReason));
        }
        
        ViewBag.Metrics = metrics;
        ViewBag.ReportingPeriods = reportingPeriods;
        ViewBag.DefaultRule = "Returns are due by the 3rd working day of the following month";
        ViewBag.PeriodExclusions = periodExclusions;
        ViewBag.BusinessAreaConfigs = businessAreaConfigs;
        
        return View("~/Views/ProductReporting/PerformanceMetrics/Guidance.cshtml");
    }

    /// <summary>
    /// Exports commission performance data to Excel for the given commission (default commission 1).
    /// Quarter is fixed as Q3 2025. Columns match the requested commission report format where we have data.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ExportCommission1PerformanceData(int commissionId = 1)
    {
        try
        {
            // Required format Q#_YYYY/YY per data dictionary (e.g. Q3_2025/26)
            const string fixedQuarter = "Q3_2025/26";

            var commission = await _context.Commissions.FindAsync(commissionId);
            if (commission == null)
            {
                TempData["ErrorMessage"] = "Commission not found.";
                return RedirectToAction("Commission", "ProductReporting");
            }

            var submissions = await _context.CommissionSubmissions
                .Include(cs => cs.MetricValues)
                .Where(cs => cs.CommissionId == commissionId)
                .OrderBy(cs => cs.ProductTitle)
                .ToListAsync();

            var performanceMetrics = await _context.PerformanceMetrics
                .Where(pm => !pm.IsDisabled)
                .OrderBy(pm => pm.Identifier)
                .ToListAsync();

            // Map requested column names to performance metrics. Known identifier mappings first, then fuzzy by Title/Identifier.
            var metricsByIdentifier = performanceMetrics.ToDictionary(m => m.Identifier, m => m, StringComparer.OrdinalIgnoreCase);
            string Normalize(string s) => s?.Replace(" ", "_").Replace("-", "_").ToLowerInvariant() ?? "";
            var metricColumnMap = new Dictionary<string, PerformanceMetric?>(StringComparer.OrdinalIgnoreCase)
            {
                ["completed_transactions"] = metricsByIdentifier.GetValueOrDefault("perf-6"),
                ["completed_digital_transactions"] = metricsByIdentifier.GetValueOrDefault("perf-2"),
                ["usat_total_number_of_all_responses"] = metricsByIdentifier.GetValueOrDefault("perf-1"),
                ["started_digital_transactions"] = performanceMetrics.FirstOrDefault(m =>
                    Normalize(m.Identifier).Contains("started") && (Normalize(m.Identifier).Contains("digital") || Normalize(m.Title).Contains("digital")) ||
                    Normalize(m.Title).Contains("started") && Normalize(m.Title).Contains("digital")),
                ["usat_number_of_satisfied_responses"] = performanceMetrics.FirstOrDefault(m =>
                    (Normalize(m.Identifier).Contains("usat") || Normalize(m.Title).Contains("usat") || Normalize(m.Title).Contains("satisfied")) &&
                    (Normalize(m.Title).Contains("satisfied") || Normalize(m.Identifier).Contains("satisfied")) &&
                    !Normalize(m.Title).Contains("very")),
                ["usat_number_of_very_satisfied_responses"] = performanceMetrics.FirstOrDefault(m =>
                    (Normalize(m.Identifier).Contains("usat") || Normalize(m.Title).Contains("usat") || Normalize(m.Title).Contains("very")) &&
                    Normalize(m.Title).Contains("very") && Normalize(m.Title).Contains("satisfied")),
                ["accessibility_compliance"] = performanceMetrics.FirstOrDefault(m =>
                    Normalize(m.Identifier).Contains("accessibility") || Normalize(m.Identifier).Contains("acc") ||
                    Normalize(m.Title).Contains("accessibility") || Normalize(m.Title).Contains("compliance")),
                ["cost_per_transaction_amount"] = performanceMetrics.FirstOrDefault(m =>
                    Normalize(m.Identifier).Contains("cost") || Normalize(m.Title).Contains("cost per transaction") ||
                    (Normalize(m.Title).Contains("cost") && Normalize(m.Title).Contains("transaction"))),
                ["categories_included_in_cpt"] = performanceMetrics.FirstOrDefault(m =>
                    Normalize(m.Title).Contains("categories") && (Normalize(m.Title).Contains("cpt") || Normalize(m.Title).Contains("cost"))),
                ["fte_count"] = performanceMetrics.FirstOrDefault(m =>
                    Normalize(m.Identifier).Contains("fte") || Normalize(m.Title).Contains("fte count") ||
                    (Normalize(m.Title).Contains("fte") && Normalize(m.Title).Contains("count"))),
                ["fte_cost"] = performanceMetrics.FirstOrDefault(m =>
                    Normalize(m.Identifier).Contains("fte") && Normalize(m.Identifier).Contains("cost") ||
                    (Normalize(m.Title).Contains("fte") && Normalize(m.Title).Contains("cost"))),
                ["non_fte_cost"] = performanceMetrics.FirstOrDefault(m =>
                    Normalize(m.Title).Contains("non") && Normalize(m.Title).Contains("fte") ||
                    Normalize(m.Identifier).Contains("non_fte") || Normalize(m.Identifier).Contains("non fte")),
            };

            // Resolve product URLs and type from API (batch by document IDs)
            var documentIds = submissions.Select(s => s.ProductDocumentId).Distinct().ToList();
            var allProducts = await _productsApiService.GetAllProductsAsync();
            var productByDocId = allProducts
                .Where(p => !string.IsNullOrEmpty(p.DocumentId))
                .GroupBy(p => p.DocumentId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // Fixed column order: all columns always present (data or empty)
            var headers = new[]
            {
                "quarter",
                "service_name",
                "transactional_or_non_transactional",
                "completed_transactions",
                "started_digital_transactions",
                "completed_digital_transactions",
                "usat_number_of_satisfied_responses",
                "usat_number_of_very_satisfied_responses",
                "usat_total_number_of_all_responses",
                "accessibility_compliance",
                "cost_per_transaction_amount",
                "categories_included_in_cpt",
                "fte_count",
                "fte_cost",
                "non_fte_cost",
                "govuk_url",
                "comments"
            };

            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Commission performance");

            for (int col = 0; col < headers.Length; col++)
            {
                var cell = worksheet.Cell(1, col + 1);
                cell.Value = headers[col];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#f1f3f5");
                cell.Style.Border.BottomBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
            }

            int row = 2;
            foreach (var submission in submissions)
            {
                var product = productByDocId.GetValueOrDefault(submission.ProductDocumentId);
                var govukUrl = product?.ProductUrl ?? "";
                var transactionalOrNot = "";
                if (product?.CategoryValues != null)
                {
                    var typeCategory = product.CategoryValues
                        .FirstOrDefault(cv => cv.CategoryType?.Name?.Trim().Equals("Type", StringComparison.OrdinalIgnoreCase) == true);
                    if (typeCategory != null)
                    {
                        var typeName = typeCategory.Name?.Trim() ?? "";
                        if (typeName.Equals("Transactional", StringComparison.OrdinalIgnoreCase))
                            transactionalOrNot = "Transactional";
                        else if (typeName.Equals("Non-transactional", StringComparison.OrdinalIgnoreCase))
                            transactionalOrNot = "Non-transactional";
                    }
                }

                for (int c = 0; c < headers.Length; c++)
                {
                    var header = headers[c];
                    string cellValue;
                    if (header == "quarter")
                        cellValue = fixedQuarter;
                    else if (header == "service_name")
                        cellValue = submission.ProductTitle ?? "";
                    else if (header == "transactional_or_non_transactional")
                        cellValue = transactionalOrNot;
                    else if (header == "govuk_url")
                        cellValue = govukUrl;
                    else if (header == "comments")
                        cellValue = submission.Comments?.Trim() ?? "";
                    else if (metricColumnMap.TryGetValue(header, out var metric) && metric != null)
                    {
                        var mv = submission.MetricValues?.FirstOrDefault(m => m.PerformanceMetricId == metric.Id);
                        cellValue = mv == null || mv.IsNotCaptured ? "" : (mv.Value?.Trim() ?? "");
                        if (header == "accessibility_compliance" && !string.IsNullOrEmpty(cellValue))
                        {
                            cellValue = (decimal.TryParse(cellValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var num) && num == 0)
                                ? "Yes" : "No";
                        }
                    }
                    else
                        cellValue = "";

                    worksheet.Cell(row, c + 1).Value = cellValue;
                }

                row++;
            }

            worksheet.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var fileName = $"commission-{commissionId}-performance-data-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xlsx";
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting commission {CommissionId} performance data", commissionId);
            TempData["ErrorMessage"] = "An error occurred while exporting commission performance data.";
            return RedirectToAction("Commission", "ProductReporting");
        }
    }

    // GET: ProductReporting/ExportUserProducts
    public async Task<IActionResult> ExportUserProducts(string search, string phase, string businessArea, string reportingStatus)
    {
        try
        {
            // Get the current user's email
            var userEmail = User.Identity?.Name;

            if (string.IsNullOrEmpty(userEmail))
            {
                TempData["ErrorMessage"] = "Unable to identify user. Please try again.";
                return RedirectToAction("PerformanceMetrics", new { view = "mine" });
            }

            // This is a placeholder - actual export implementation would go here
            // For now, redirect back with a message
            TempData["ErrorMessage"] = "Export functionality is being implemented.";
            return RedirectToAction("PerformanceMetrics", new { view = "mine" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting user products");
            TempData["ErrorMessage"] = "An error occurred while exporting products.";
            return RedirectToAction("PerformanceMetrics", new { view = "mine" });
        }
    }

    // GET: ProductReporting/ExportAllProducts
    public async Task<IActionResult> ExportAllProducts(string search, string phase, string businessArea, string reportingStatus)
    {
        try
        {
            // This is a placeholder - actual export implementation would go here
            // For now, redirect back with a message
            TempData["ErrorMessage"] = "Export functionality is being implemented.";
            return RedirectToAction("PerformanceMetrics", new { view = "all" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting all products");
            TempData["ErrorMessage"] = "An error occurred while exporting products.";
            return RedirectToAction("PerformanceMetrics", new { view = "all" });
        }
    }

    #endregion
}

