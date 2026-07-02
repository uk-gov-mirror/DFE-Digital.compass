using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Compass.Data;
using Compass.Models;
using Compass.Services;
using System.Security.Claims;

namespace Compass.Controllers.Admin
{
    [Route("Admin/[controller]")]
    [Authorize]
    public class PerformanceReportingManagementController : Controller
    {
        private readonly CompassDbContext _context;
        private readonly ILogger<PerformanceReportingManagementController> _logger;
        private readonly IPermissionService _permissionService;
        private readonly IProductsApiService _productsApiService;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;

        public PerformanceReportingManagementController(
            CompassDbContext context,
            ILogger<PerformanceReportingManagementController> logger,
            IPermissionService permissionService,
            IProductsApiService productsApiService,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory)
        {
            _context = context;
            _logger = logger;
            _permissionService = permissionService;
            _productsApiService = productsApiService;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
        }

        private string GetUserEmail()
        {
            return User.Identity?.Name 
                ?? User.FindFirst(ClaimTypes.Email)?.Value 
                ?? User.FindFirst("preferred_username")?.Value
                ?? User.FindFirst("email")?.Value
                ?? string.Empty;
        }

        private async Task<bool> IsAuthorizedAsync()
        {
            var userEmail = GetUserEmail();
            if (string.IsNullOrEmpty(userEmail))
                return false;

            return await _permissionService.IsSuperAdminAsync(userEmail) ||
                   await _permissionService.IsInGroupAsync(userEmail, "Central Operations Admin");
        }

        private async Task<List<string>> GetBusinessAreasFromCmsAsync()
        {
            try
            {
                var businessAreas = await _context.BusinessAreaLookups
                    .Where(ba => ba.IsActive)
                    .OrderBy(ba => ba.SortOrder)
                    .ThenBy(ba => ba.Name)
                    .Select(ba => ba.Name)
                    .ToListAsync();
                
                _logger.LogInformation("Found {Count} business areas from admin settings", businessAreas.Count);
                    return businessAreas;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching business areas from admin settings");
            return new List<string>();
            }
        }

        // ========================================
        // MAIN INDEX
        // ========================================

        [HttpGet("")]
        [HttpGet("Index")]
        public async Task<IActionResult> Index()
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            return View("~/Views/Admin/PerformanceReportingManagement/Index.cshtml");
        }

        // ========================================
        // BULK DELETION OF METRICS SUBMISSIONS
        // ========================================

        [HttpGet("BulkDelete")]
        public async Task<IActionResult> BulkDelete()
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            var currentDate = DateTime.UtcNow;
            var startYear = 2025;
            var startMonth = 10;

            var periods = new List<(int Year, int Month, int Count)>();
            
            // Generate list of periods from Oct 2025 to current month + 6 months
            var periodDate = new DateTime(startYear, startMonth, 1);
            var endDate = currentDate.AddMonths(6);

            while (periodDate <= endDate)
            {
                var count = await _context.ProductReturns
                    .Where(pr => pr.Year == periodDate.Year && pr.Month == periodDate.Month)
                    .SelectMany(pr => pr.MetricValues)
                    .CountAsync();

                periods.Add((periodDate.Year, periodDate.Month, count));
                periodDate = periodDate.AddMonths(1);
            }

            ViewBag.Periods = periods;
            return View("~/Views/Admin/PerformanceReportingManagement/BulkDelete.cshtml");
        }

        [HttpPost("BulkDelete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkDeleteConfirm(int year, int month)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            try
            {
                var returns = await _context.ProductReturns
                    .Include(pr => pr.MetricValues)
                    .Where(pr => pr.Year == year && pr.Month == month)
                    .ToListAsync();

                var totalDeleted = 0;
                foreach (var productReturn in returns)
                {
                    totalDeleted += productReturn.MetricValues.Count;
                    _context.ProductMetricValues.RemoveRange(productReturn.MetricValues);
                    
                    // Reset the return status
                    productReturn.Status = ReturnStatus.Upcoming;
                    productReturn.SubmittedDate = null;
                    productReturn.SubmittedBy = null;
                }

                await _context.SaveChangesAsync();

                var periodName = new DateTime(year, month, 1).ToString("MMMM yyyy");
                _logger.LogInformation("Bulk deleted {Count} metric submissions for {Period} by {User}", 
                    totalDeleted, periodName, GetUserEmail());

                TempData["SuccessMessage"] = $"Successfully deleted {totalDeleted} metric submissions for {periodName}.";
                return RedirectToAction(nameof(BulkDelete));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error bulk deleting metrics for {Year}-{Month}", year, month);
                TempData["ErrorMessage"] = "An error occurred while deleting the submissions.";
                return RedirectToAction(nameof(BulkDelete));
            }
        }

        // ========================================
        // DUE DATE CALENDAR OVERRIDES
        // ========================================

        [HttpGet("DueDateOverrides")]
        public async Task<IActionResult> DueDateOverrides()
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            var overrides = await _context.PerformanceReportingDueDateOverrides
                .Where(o => o.IsActive)
                .OrderByDescending(o => o.ReportingYear)
                .ThenByDescending(o => o.ReportingMonth)
                .ToListAsync();

            return View("~/Views/Admin/PerformanceReportingManagement/DueDateOverrides.cshtml", overrides);
        }

        [HttpGet("CreateDueDateOverride")]
        public async Task<IActionResult> CreateDueDateOverride()
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            return View("~/Views/Admin/PerformanceReportingManagement/CreateDueDateOverride.cshtml");
        }

        [HttpPost("CreateDueDateOverride")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateDueDateOverride(PerformanceReportingDueDateOverride model)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            if (ModelState.IsValid)
            {
                // Check if override already exists for this period
                var existing = await _context.PerformanceReportingDueDateOverrides
                    .FirstOrDefaultAsync(o => o.ReportingYear == model.ReportingYear && 
                                              o.ReportingMonth == model.ReportingMonth);

                if (existing != null)
                {
                    ModelState.AddModelError("", $"An override already exists for {new DateTime(model.ReportingYear, model.ReportingMonth, 1):MMMM yyyy}. Please edit the existing override.");
                    return View("~/Views/Admin/PerformanceReportingManagement/CreateDueDateOverride.cshtml", model);
                }

                model.CreatedBy = GetUserEmail();
                model.UpdatedBy = GetUserEmail();
                model.CreatedAt = DateTime.UtcNow;
                model.UpdatedAt = DateTime.UtcNow;

                _context.PerformanceReportingDueDateOverrides.Add(model);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Created due date override for {Year}-{Month} by {User}", 
                    model.ReportingYear, model.ReportingMonth, GetUserEmail());

                TempData["SuccessMessage"] = $"Successfully created due date override for {new DateTime(model.ReportingYear, model.ReportingMonth, 1):MMMM yyyy}.";
                return RedirectToAction(nameof(DueDateOverrides));
            }

            return View("~/Views/Admin/PerformanceReportingManagement/CreateDueDateOverride.cshtml", model);
        }

        [HttpGet("EditDueDateOverride/{id}")]
        public async Task<IActionResult> EditDueDateOverride(int id)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            var dueDateOverride = await _context.PerformanceReportingDueDateOverrides.FindAsync(id);
            if (dueDateOverride == null)
            {
                return NotFound();
            }

            return View("~/Views/Admin/PerformanceReportingManagement/EditDueDateOverride.cshtml", dueDateOverride);
        }

        [HttpPost("EditDueDateOverride/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditDueDateOverride(int id, PerformanceReportingDueDateOverride model)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            if (id != model.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var existing = await _context.PerformanceReportingDueDateOverrides.FindAsync(id);
                    if (existing == null)
                    {
                        return NotFound();
                    }

                    existing.DueDate = model.DueDate;
                    existing.Reason = model.Reason;
                    existing.IsActive = model.IsActive;
                    existing.UpdatedBy = GetUserEmail();
                    existing.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();

                    _logger.LogInformation("Updated due date override {Id} by {User}", id, GetUserEmail());

                    TempData["SuccessMessage"] = "Successfully updated due date override.";
                    return RedirectToAction(nameof(DueDateOverrides));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating due date override {Id}", id);
                    ModelState.AddModelError("", "An error occurred while updating the override.");
                }
            }

            return View("~/Views/Admin/PerformanceReportingManagement/EditDueDateOverride.cshtml", model);
        }

        [HttpPost("DeleteDueDateOverride/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteDueDateOverride(int id)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            try
            {
                var dueDateOverride = await _context.PerformanceReportingDueDateOverrides.FindAsync(id);
                if (dueDateOverride != null)
                {
                    dueDateOverride.IsActive = false;
                    dueDateOverride.UpdatedBy = GetUserEmail();
                    dueDateOverride.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();

                    _logger.LogInformation("Deleted (deactivated) due date override {Id} by {User}", id, GetUserEmail());

                    TempData["SuccessMessage"] = "Successfully deleted due date override.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting due date override {Id}", id);
                TempData["ErrorMessage"] = "An error occurred while deleting the override.";
            }

            return RedirectToAction(nameof(DueDateOverrides));
        }

        // ========================================
        // BUSINESS AREA REPORTING CONFIGURATION
        // ========================================

        [HttpGet("BusinessAreaConfig")]
        public async Task<IActionResult> BusinessAreaConfig()
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            var configs = await _context.PerformanceReportingBusinessAreaConfigs
                .Where(c => c.IsActive)
                .OrderBy(c => c.BusinessAreaName)
                .ToListAsync();

            return View("~/Views/Admin/PerformanceReportingManagement/BusinessAreaConfig.cshtml", configs);
        }

        [HttpGet("CreateBusinessAreaConfig")]
        public async Task<IActionResult> CreateBusinessAreaConfig()
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            ViewBag.BusinessAreas = await GetBusinessAreasFromCmsAsync();
            return View("~/Views/Admin/PerformanceReportingManagement/CreateBusinessAreaConfig.cshtml");
        }

        [HttpPost("CreateBusinessAreaConfig")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateBusinessAreaConfig(PerformanceReportingBusinessAreaConfig model)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            if (ModelState.IsValid)
            {
                model.CreatedBy = GetUserEmail();
                model.UpdatedBy = GetUserEmail();
                model.CreatedAt = DateTime.UtcNow;
                model.UpdatedAt = DateTime.UtcNow;

                _context.PerformanceReportingBusinessAreaConfigs.Add(model);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Created business area config for {BusinessArea} by {User}", 
                    model.BusinessAreaName, GetUserEmail());

                TempData["SuccessMessage"] = $"Successfully created configuration for {model.BusinessAreaName}.";
                return RedirectToAction(nameof(BusinessAreaConfig));
            }

            ViewBag.BusinessAreas = await GetBusinessAreasFromCmsAsync();
            return View("~/Views/Admin/PerformanceReportingManagement/CreateBusinessAreaConfig.cshtml", model);
        }

        [HttpGet("EditBusinessAreaConfig/{id}")]
        public async Task<IActionResult> EditBusinessAreaConfig(int id)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            var config = await _context.PerformanceReportingBusinessAreaConfigs.FindAsync(id);
            if (config == null)
            {
                return NotFound();
            }

            return View("~/Views/Admin/PerformanceReportingManagement/EditBusinessAreaConfig.cshtml", config);
        }

        [HttpPost("EditBusinessAreaConfig/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditBusinessAreaConfig(int id, PerformanceReportingBusinessAreaConfig model)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            if (id != model.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var existing = await _context.PerformanceReportingBusinessAreaConfigs.FindAsync(id);
                    if (existing == null)
                    {
                        return NotFound();
                    }

                    existing.BusinessAreaName = model.BusinessAreaName;
                    existing.ApplicableFromYear = model.ApplicableFromYear;
                    existing.ApplicableFromMonth = model.ApplicableFromMonth;
                    existing.ApplicableUntilYear = model.ApplicableUntilYear;
                    existing.ApplicableUntilMonth = model.ApplicableUntilMonth;
                    existing.Notes = model.Notes;
                    existing.IsActive = model.IsActive;
                    existing.UpdatedBy = GetUserEmail();
                    existing.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();

                    _logger.LogInformation("Updated business area config {Id} by {User}", id, GetUserEmail());

                    TempData["SuccessMessage"] = "Successfully updated business area configuration.";
                    return RedirectToAction(nameof(BusinessAreaConfig));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating business area config {Id}", id);
                    ModelState.AddModelError("", "An error occurred while updating the configuration.");
                }
            }

            return View("~/Views/Admin/PerformanceReportingManagement/EditBusinessAreaConfig.cshtml", model);
        }

        [HttpPost("DeleteBusinessAreaConfig/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteBusinessAreaConfig(int id)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            try
            {
                var config = await _context.PerformanceReportingBusinessAreaConfigs.FindAsync(id);
                if (config != null)
                {
                    config.IsActive = false;
                    config.UpdatedBy = GetUserEmail();
                    config.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();

                    _logger.LogInformation("Deleted (deactivated) business area config {Id} by {User}", id, GetUserEmail());

                    TempData["SuccessMessage"] = "Successfully deleted business area configuration.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting business area config {Id}", id);
                TempData["ErrorMessage"] = "An error occurred while deleting the configuration.";
            }

            return RedirectToAction(nameof(BusinessAreaConfig));
        }

        // ========================================
        // PRODUCT EXCLUSIONS
        // ========================================

        [HttpGet("ProductExclusions")]
        public async Task<IActionResult> ProductExclusions()
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            var exclusions = await _context.PerformanceReportingProductExclusions
                .Where(e => e.IsActive)
                .OrderBy(e => e.ProductName)
                .ToListAsync();

            return View("~/Views/Admin/PerformanceReportingManagement/ProductExclusions.cshtml", exclusions);
        }

        [HttpGet("CreateProductExclusion")]
        public async Task<IActionResult> CreateProductExclusion()
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            // Get all products for dropdown
            var products = await _productsApiService.GetAllProductsAsync();
            ViewBag.Products = products?.Where(p => !string.IsNullOrEmpty(p.FipsId))
                .OrderBy(p => p.Title)
                .ToList() ?? new List<Compass.Models.ProductDto>();

            return View("~/Views/Admin/PerformanceReportingManagement/CreateProductExclusion.cshtml");
        }

        [HttpPost("CreateProductExclusion")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateProductExclusion(PerformanceReportingProductExclusion model)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            if (ModelState.IsValid)
            {
                // Check if active exclusion already exists for this product
                var existing = await _context.PerformanceReportingProductExclusions
                    .FirstOrDefaultAsync(e => e.FipsId == model.FipsId && e.IsActive);

                if (existing != null)
                {
                    ModelState.AddModelError("", $"An active exclusion already exists for product {model.FipsId}. Please edit the existing exclusion.");
                    
                    var products = await _productsApiService.GetAllProductsAsync();
                    ViewBag.Products = products?.Where(p => !string.IsNullOrEmpty(p.FipsId))
                        .OrderBy(p => p.Title)
                        .ToList() ?? new List<Compass.Models.ProductDto>();
                    
                    return View("~/Views/Admin/PerformanceReportingManagement/CreateProductExclusion.cshtml", model);
                }

                // Get product name
                var allProducts = await _productsApiService.GetAllProductsAsync();
                var product = allProducts?.FirstOrDefault(p => p.FipsId == model.FipsId);
                if (product != null)
                {
                    model.ProductName = product.Title;
                    if (string.IsNullOrWhiteSpace(model.ProductDocumentId))
                        model.ProductDocumentId = product.DocumentId ?? model.FipsId ?? string.Empty;
                }

                model.CreatedBy = GetUserEmail();
                model.UpdatedBy = GetUserEmail();
                model.CreatedAt = DateTime.UtcNow;
                model.UpdatedAt = DateTime.UtcNow;

                _context.PerformanceReportingProductExclusions.Add(model);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Created product exclusion for {FipsId} by {User}", 
                    model.FipsId, GetUserEmail());

                TempData["SuccessMessage"] = $"Successfully created exclusion for {model.ProductName ?? model.FipsId}.";
                return RedirectToAction(nameof(ProductExclusions));
            }

            var productsForView = await _productsApiService.GetAllProductsAsync();
            ViewBag.Products = productsForView?.Where(p => !string.IsNullOrEmpty(p.FipsId))
                .OrderBy(p => p.Title)
                .ToList() ?? new List<Compass.Models.ProductDto>();

            return View("~/Views/Admin/PerformanceReportingManagement/CreateProductExclusion.cshtml", model);
        }

        [HttpGet("EditProductExclusion/{id}")]
        public async Task<IActionResult> EditProductExclusion(int id)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            var exclusion = await _context.PerformanceReportingProductExclusions.FindAsync(id);
            if (exclusion == null)
            {
                return NotFound();
            }

            return View("~/Views/Admin/PerformanceReportingManagement/EditProductExclusion.cshtml", exclusion);
        }

        [HttpPost("EditProductExclusion/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditProductExclusion(int id, PerformanceReportingProductExclusion model)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            if (id != model.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var existing = await _context.PerformanceReportingProductExclusions.FindAsync(id);
                    if (existing == null)
                    {
                        return NotFound();
                    }

                    existing.ExclusionReason = model.ExclusionReason;
                    existing.ExclusionFromYear = model.ExclusionFromYear;
                    existing.ExclusionFromMonth = model.ExclusionFromMonth;
                    existing.ExclusionUntilYear = model.ExclusionUntilYear;
                    existing.ExclusionUntilMonth = model.ExclusionUntilMonth;
                    existing.IsActive = model.IsActive;
                    existing.UpdatedBy = GetUserEmail();
                    existing.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();

                    _logger.LogInformation("Updated product exclusion {Id} by {User}", id, GetUserEmail());

                    TempData["SuccessMessage"] = "Successfully updated product exclusion.";
                    return RedirectToAction(nameof(ProductExclusions));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating product exclusion {Id}", id);
                    ModelState.AddModelError("", "An error occurred while updating the exclusion.");
                }
            }

            return View("~/Views/Admin/PerformanceReportingManagement/EditProductExclusion.cshtml", model);
        }

        [HttpPost("DeleteProductExclusion/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteProductExclusion(int id)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            try
            {
                var exclusion = await _context.PerformanceReportingProductExclusions.FindAsync(id);
                if (exclusion != null)
                {
                    exclusion.IsActive = false;
                    exclusion.UpdatedBy = GetUserEmail();
                    exclusion.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();

                    _logger.LogInformation("Deleted (deactivated) product exclusion {Id} by {User}", id, GetUserEmail());

                    TempData["SuccessMessage"] = "Successfully removed product exclusion.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting product exclusion {Id}", id);
                TempData["ErrorMessage"] = "An error occurred while removing the exclusion.";
            }

            return RedirectToAction(nameof(ProductExclusions));
        }

        // ========================================
        // PERIOD EXCLUSIONS (BASE LEVEL)
        // ========================================

        [HttpGet("PeriodExclusions")]
        public async Task<IActionResult> PeriodExclusions()
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            var exclusions = await _context.PerformanceReportingPeriodExclusions
                .Where(e => e.IsActive)
                .OrderByDescending(e => e.Year)
                .ThenByDescending(e => e.Month)
                .ToListAsync();

            return View("~/Views/Admin/PerformanceReportingManagement/PeriodExclusions.cshtml", exclusions);
        }

        [HttpGet("CreatePeriodExclusion")]
        public async Task<IActionResult> CreatePeriodExclusion()
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            var currentPeriod = DateTime.UtcNow;
            var model = new PerformanceReportingPeriodExclusion
            {
                Year = currentPeriod.Year,
                Month = currentPeriod.Month,
                IsActive = true
            };

            return View("~/Views/Admin/PerformanceReportingManagement/CreatePeriodExclusion.cshtml", model);
        }

        [HttpPost("CreatePeriodExclusion")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreatePeriodExclusion(PerformanceReportingPeriodExclusion model)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            if (ModelState.IsValid)
            {
                // Check if exclusion already exists for this period
                var existing = await _context.PerformanceReportingPeriodExclusions
                    .FirstOrDefaultAsync(e => e.Year == model.Year && e.Month == model.Month);

                if (existing != null)
                {
                    ModelState.AddModelError("", $"An exclusion already exists for {new DateTime(model.Year, model.Month, 1):MMMM yyyy}. Please edit the existing exclusion.");
                    return View("~/Views/Admin/PerformanceReportingManagement/CreatePeriodExclusion.cshtml", model);
                }

                model.CreatedBy = GetUserEmail();
                model.UpdatedBy = GetUserEmail();
                model.CreatedAt = DateTime.UtcNow;
                model.UpdatedAt = DateTime.UtcNow;
                model.IsActive = true;

                _context.PerformanceReportingPeriodExclusions.Add(model);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Created period exclusion for {Year}-{Month} by {User}", 
                    model.Year, model.Month, GetUserEmail());

                TempData["SuccessMessage"] = $"Successfully created period exclusion for {new DateTime(model.Year, model.Month, 1):MMMM yyyy}.";
                return RedirectToAction(nameof(PeriodExclusions));
            }

            return View("~/Views/Admin/PerformanceReportingManagement/CreatePeriodExclusion.cshtml", model);
        }

        [HttpGet("EditPeriodExclusion/{id}")]
        public async Task<IActionResult> EditPeriodExclusion(int id)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            var exclusion = await _context.PerformanceReportingPeriodExclusions.FindAsync(id);
            if (exclusion == null)
            {
                return NotFound();
            }

            return View("~/Views/Admin/PerformanceReportingManagement/EditPeriodExclusion.cshtml", exclusion);
        }

        [HttpPost("EditPeriodExclusion/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditPeriodExclusion(int id, PerformanceReportingPeriodExclusion model)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            if (id != model.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var existing = await _context.PerformanceReportingPeriodExclusions.FindAsync(id);
                    if (existing == null)
                    {
                        return NotFound();
                    }

                    existing.Reason = model.Reason;
                    existing.Notes = model.Notes;
                    existing.IsActive = model.IsActive;
                    existing.UpdatedBy = GetUserEmail();
                    existing.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();

                    _logger.LogInformation("Updated period exclusion {Id} by {User}", id, GetUserEmail());

                    TempData["SuccessMessage"] = "Successfully updated period exclusion.";
                    return RedirectToAction(nameof(PeriodExclusions));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating period exclusion {Id}", id);
                    ModelState.AddModelError("", "An error occurred while updating the exclusion.");
                }
            }

            return View("~/Views/Admin/PerformanceReportingManagement/EditPeriodExclusion.cshtml", model);
        }

        [HttpPost("DeletePeriodExclusion")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePeriodExclusion(int id)
        {
            if (!await IsAuthorizedAsync())
            {
                return Forbid();
            }

            try
            {
                var exclusion = await _context.PerformanceReportingPeriodExclusions.FindAsync(id);
                if (exclusion != null)
                {
                    exclusion.IsActive = false;
                    exclusion.UpdatedBy = GetUserEmail();
                    exclusion.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();

                    _logger.LogInformation("Deleted (deactivated) period exclusion {Id} by {User}", id, GetUserEmail());

                    TempData["SuccessMessage"] = "Successfully deleted period exclusion.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting period exclusion {Id}", id);
                TempData["ErrorMessage"] = "An error occurred while deleting the exclusion.";
            }

            return RedirectToAction(nameof(PeriodExclusions));
        }
    }
}

