using System.Collections.Generic;
using Compass.Data;
using Compass.Models;
using Compass.Services;
using Compass.Services.Dashboard;
using Compass.Services.Fips;
using Compass.ViewModels.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compass.Controllers.Modern;

/// <summary>Serves the modern UI shell at <c>/modern/dashboard</c> using the same dashboard data as <see cref="HomeController"/>.</summary>
[Authorize]
[Route("modern")]
public class ModernDashboardController : Controller
{
    private readonly ILogger<ModernDashboardController> _logger;
    private readonly CompassDbContext _context;
    private readonly IWebHostEnvironment _environment;
    private readonly IHomeDashboardViewModelBuilder _dashboardBuilder;
    private readonly IGlobalFeatureToggleService _globalFeatureToggle;
    private readonly IViewAsUserService _viewAsUser;

    public ModernDashboardController(
        ILogger<ModernDashboardController> logger,
        CompassDbContext context,
        IWebHostEnvironment environment,
        IHomeDashboardViewModelBuilder dashboardBuilder,
        IGlobalFeatureToggleService globalFeatureToggle,
        IViewAsUserService viewAsUser)
    {
        _logger = logger;
        _context = context;
        _environment = environment;
        _dashboardBuilder = dashboardBuilder;
        _globalFeatureToggle = globalFeatureToggle;
        _viewAsUser = viewAsUser;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Index(string? testRole = null, int? testUserId = null)
    {
        try
        {
            var userEmail = User.Identity?.Name;

            if (string.IsNullOrEmpty(userEmail))
            {
                _logger.LogWarning("Modern dashboard: No user email found");
                TempData["ErrorMessage"] = "Unable to identify the current user.";
                return View("~/Views/Modern/Dashboard/Index.cshtml", new HomeDashboardViewModel());
            }

            var currentUser = await _context.Users
                .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower());

            if (currentUser == null)
            {
                _logger.LogWarning("Modern dashboard: User not found in database for email: {Email}", userEmail);
                TempData["ErrorMessage"] = "User account not found. Please contact an administrator.";
                return View("~/Views/Modern/Dashboard/Index.cshtml", new HomeDashboardViewModel());
            }

            if (!string.IsNullOrEmpty(testRole) && _environment.IsDevelopment())
            {
                if (testRole == "clear")
                {
                    Response.Cookies.Delete("TestDashboardRole");
                }
                else
                {
                    Response.Cookies.Append("TestDashboardRole", testRole, new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = false,
                        SameSite = SameSiteMode.Lax,
                        Expires = DateTimeOffset.UtcNow.AddDays(1)
                    });
                }
                return RedirectToAction(nameof(Index));
            }

            if (testUserId.HasValue && _environment.IsDevelopment())
            {
                if (testUserId.Value == 0)
                {
                    Response.Cookies.Delete("TestDashboardUserId");
                }
                else
                {
                    var testUser = await _context.Users.FindAsync(testUserId.Value);
                    if (testUser != null)
                    {
                        Response.Cookies.Append("TestDashboardUserId", testUserId.Value.ToString(), new CookieOptions
                        {
                            HttpOnly = true,
                            Secure = false,
                            SameSite = SameSiteMode.Lax,
                            Expires = DateTimeOffset.UtcNow.AddDays(1)
                        });
                    }
                    else
                    {
                        TempData["ErrorMessage"] = "Test user not found.";
                    }
                }
                return RedirectToAction(nameof(Index));
            }

            var (effectiveUser, effectiveUserEmail) = await ResolveEffectiveDashboardUserAsync(
                currentUser, userEmail);

            var preference = await _dashboardBuilder.GetOrCreateDashboardPreferenceAsync(effectiveUser);
            var showRaidIssues = ViewBag.ShowRaidNavigation as bool? == true;
            var viewModel = await _dashboardBuilder.BuildDashboardViewModelAsync(
                effectiveUser, effectiveUserEmail, preference, Url, HttpContext, showRaidIssues);

            var fipsRegisterEnabled =
                await _globalFeatureToggle.IsFeatureEnabledForPrincipalAsync(FeatureCodes.Fips, User);
            ViewBag.FipsServiceRegisterEnabled = fipsRegisterEnabled;
            if (fipsRegisterEnabled)
            {
                var myRegisterProducts = await FipsProductListingHelper.BuildMyProductDtosForDashboardAsync(
                    _context, effectiveUserEmail);
                viewModel.MyProducts = myRegisterProducts;
                viewModel.Metrics.ProductCount = myRegisterProducts.Count;
            }

            ViewBag.DashboardServiceRegisterProductIdByCmdbKey = null;

            ViewBag.MainNavSection = "home";
            return View("~/Views/Modern/Dashboard/Index.cshtml", viewModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading modern dashboard");
            TempData["ErrorMessage"] = "An error occurred while loading the dashboard.";
            return View("~/Views/Modern/Dashboard/Index.cshtml", new HomeDashboardViewModel());
        }
    }

    /// <summary>View-as target when active; otherwise dev test cookie or signed-in user.</summary>
    private async Task<(User User, string Email)> ResolveEffectiveDashboardUserAsync(
        User signedInUser,
        string signedInEmail)
    {
        if (_viewAsUser.IsActive(HttpContext))
        {
            var active = _viewAsUser.GetActive(HttpContext);
            if (active != null)
            {
                var viewAsUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == active.UserId);
                if (viewAsUser != null)
                {
                    _logger.LogInformation(
                        "Modern dashboard: View-as active for {ViewAsEmail} ({Id})",
                        viewAsUser.Email,
                        viewAsUser.Id);
                    return (viewAsUser, viewAsUser.Email);
                }
            }
        }

        if (_environment.IsDevelopment()
            && Request.Cookies.TryGetValue("TestDashboardUserId", out var testUserIdValue)
            && int.TryParse(testUserIdValue, out var testUserIdInt)
            && testUserIdInt > 0)
        {
            var testUser = await _context.Users.FindAsync(testUserIdInt);
            if (testUser != null)
            {
                _logger.LogInformation(
                    "Modern dashboard: Using test user {TestEmail} ({Id}) instead of {Email}",
                    testUser.Email,
                    testUserIdInt,
                    signedInEmail);
                return (testUser, testUser.Email);
            }
        }

        return (signedInUser, signedInEmail);
    }
}
