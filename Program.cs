using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Azure.Identity;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Compass.Services;
using Compass.Data;
using Compass.Middlewares;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Razor;
using Compass.Infrastructure;
using System.Threading.RateLimiting;
using System.IO;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

// Serve library static web assets (/_content/<packageId>/...) from referenced NuGet packages
// such as DfEDigital.Frontend.AspNetCore — required for dfe.min.css, dfe-frontend.iife.min.js, logos.
builder.WebHost.UseStaticWebAssets();

// Check for finding retired CMDB entries that are active in CMS
if (args.Length > 0 && args[0] == "--find-retired-mismatch")
{
    await Compass.Scripts.RunFindRetiredScript.Main(args);
    return;
}

// Check for updating retired products to Decommissioned phase
if (args.Length > 0 && args[0] == "--update-retired-products")
{
    await Compass.Scripts.RunUpdateRetiredProducts.Main(args);
    return;
}

// Bulk-import legacy Strapi export JSON into service register (CMDB products)
if (args.Length > 0 && args[0] == "--strapi-legacy-import")
{
    await Compass.Scripts.RunStrapiLegacyImport.Main(args);
    return;
}

// Check for database cleanup command
if (args.Length > 0 && args[0] == "--clean-database")
{
    var cleanConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrEmpty(cleanConnectionString))
    {
        Console.WriteLine("Error: DefaultConnection string not found in configuration.");
        return;
    }
    await Compass.CleanDatabase.RunAsync(cleanConnectionString);
    return;
}

// Check for applying custom course fields migration
if (args.Length > 0 && args[0] == "--apply-custom-course-fields")
{
    var customCourseConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrEmpty(customCourseConnectionString))
    {
        Console.WriteLine("Error: DefaultConnection string not found in configuration.");
        return;
    }

    var optionsBuilder = new DbContextOptionsBuilder<CompassDbContext>();
    optionsBuilder.UseSqlServer(customCourseConnectionString);

    using var context = new CompassDbContext(optionsBuilder.Options);

    Console.WriteLine("Applying CustomCourseFields migration manually...");

    try
    {
        // Try to add columns - if they already exist, SQL will throw an error which we'll catch
        try
        {
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE [TrainingRequests] ADD [CustomCourseProvider] nvarchar(255) NULL");
            Console.WriteLine("✓ Added CustomCourseProvider column");
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 2705 || ex.Message.Contains("already exists"))
        {
            Console.WriteLine("✓ CustomCourseProvider column already exists");
        }

        try
        {
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE [TrainingRequests] ADD [CustomCourseCost] decimal(18,2) NULL");
            Console.WriteLine("✓ Added CustomCourseCost column");
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 2705 || ex.Message.Contains("already exists"))
        {
            Console.WriteLine("✓ CustomCourseCost column already exists");
        }

        try
        {
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE [TrainingRequests] ADD [CustomCourseUrl] nvarchar(500) NULL");
            Console.WriteLine("✓ Added CustomCourseUrl column");
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 2705 || ex.Message.Contains("already exists"))
        {
            Console.WriteLine("✓ CustomCourseUrl column already exists");
        }

        Console.WriteLine("✓ Migration applied successfully!");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error applying migration: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
        throw;
    }
    return;
}

// Check for data seeding command
if (args.Length > 0 && args[0] == "--seed-from-sqlite")
{
    var environment = args.Length > 1 && args[1] == "--environment" && args.Length > 2
        ? args[2]
        : "Development";
    await Compass.SeedFromSQLite.RunAsync(environment);
    return;
}

// Check for GDD Framework seeding command
if (args.Length > 0 && args[0] == "--seed-gdd-framework")
{
    var environment = "Development";
    string? csvFilePath = null;

    for (int i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--environment") environment = args[i + 1];
        if (args[i] == "--csv-file") csvFilePath = args[i + 1];
    }

    await Compass.SeedGddFramework.RunAsync(environment, csvFilePath);
    return;
}

if (args.Length > 0 && args[0] == "--seed-risk-tiers")
{
    var environment = "Development";
    for (var i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--environment")
            environment = args[i + 1];
    }

    await Compass.SeedRiskTiers.RunAsync(environment);
    return;
}

if (args.Length > 0 && args[0] == "--seed-dev-risk-register")
{
    var environment = "Development";
    for (var i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--environment")
            environment = args[i + 1];
    }

    await Compass.SeedDevRiskRegister.RunAsync(environment);
    return;
}

if (args.Length > 0 && args[0] == "--load-test-risks")
{
    var environment = "Development";
    var count = 100;
    var concurrency = 2;
    var delayMs = 2000;
    var baseUrl = "http://localhost:5500";
    var ownerEmail = "andy.jones@education.gov.uk";
    string? apiToken = null;

    for (var i = 1; i < args.Length - 1; i++)
    {
        switch (args[i])
        {
            case "--environment":
                environment = args[i + 1];
                break;
            case "--count":
                count = int.Parse(args[i + 1]);
                break;
            case "--concurrency":
                concurrency = int.Parse(args[i + 1]);
                break;
            case "--delay-ms":
                delayMs = int.Parse(args[i + 1]);
                break;
            case "--base-url":
                baseUrl = args[i + 1];
                break;
            case "--token":
                apiToken = args[i + 1];
                break;
            case "--owner-email":
                ownerEmail = args[i + 1];
                break;
        }
    }

    await Compass.LoadTestRiskCreation.RunAsync(environment, count, concurrency, delayMs, baseUrl, apiToken, ownerEmail);
    return;
}

if (args.Length > 0 && args[0] == "--seed-fips-user-groups")
{
    var environment = "Development";
    string? jsonFilePath = null;
    var fromCms = false;
    var deactivateMissing = false;

    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--environment" && i + 1 < args.Length)
            environment = args[++i];
        else if (args[i] == "--json-file" && i + 1 < args.Length)
            jsonFilePath = args[++i];
        else if (args[i] == "--from-cms")
            fromCms = true;
        else if (args[i] == "--deactivate-missing")
            deactivateMissing = true;
    }

    await Compass.SeedFipsUserGroups.RunAsync(environment, jsonFilePath, fromCms, deactivateMissing);
    return;
}

// Check for migration workbook export command
if (args.Length > 0 && args[0] == "--export-migration-workbook")
{
    var environment = "Development";
    string? outputPath = null;

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--environment" && i + 1 < args.Length)
        {
            environment = args[i + 1];
            i++;
        }
        else if (args[i] == "--output" && i + 1 < args.Length)
        {
            outputPath = args[i + 1];
            i++;
        }
    }

    await Compass.MigrationWorkbookExport.RunAsync(environment, outputPath);
    return;
}

// Check for query GDD roles command
if (args.Length > 0 && args[0] == "--query-gdd-roles")
{
    var environment = args.Length > 2 && args[1] == "--environment" ? args[2] : "Development";

    var builder2 = new Microsoft.Extensions.Configuration.ConfigurationBuilder();
    builder2.AddJsonFile("appsettings.json", optional: false);
    builder2.AddJsonFile($"appsettings.{environment}.json", optional: true);
    var config = builder2.Build();

    var options = new DbContextOptionsBuilder<CompassDbContext>()
        .UseSqlServer(config.GetConnectionString("DefaultConnection"))
        .Options;
    using var db = new CompassDbContext(options);

    var roles = await db.GddRoles.OrderBy(r => r.RoleFamily).ThenBy(r => r.RoleName).ThenBy(r => r.RoleLevel).ToListAsync();
    Console.WriteLine($"Total GDD Roles: {roles.Count}\n");

    Console.WriteLine("By Role Family:\n");
    foreach (var family in roles.GroupBy(r => r.RoleFamily))
    {
        Console.WriteLine($"{family.Key}: {family.Count()} roles");
        foreach (var role in family.Take(8))
        {
            Console.WriteLine($"  - {role.DisplayName}");
        }
        if (family.Count() > 8) Console.WriteLine($"  ... and {family.Count() - 8} more");
        Console.WriteLine();
    }

    Console.WriteLine("\nUnique Role Levels:");
    var uniqueLevels = roles.Select(r => r.RoleLevel).Where(r => !string.IsNullOrEmpty(r)).Distinct().OrderBy(l => l).ToList();
    Console.WriteLine($"Total unique levels: {uniqueLevels.Count}");
    foreach (var level in uniqueLevels.Take(40))
    {
        Console.WriteLine($"  - {level}");
    }

    return;
}

// Check for query Skills command
if (args.Length > 0 && args[0] == "--query-skills")
{
    var environment = args.Length > 2 && args[1] == "--environment" ? args[2] : "Development";

    var builder2 = new Microsoft.Extensions.Configuration.ConfigurationBuilder();
    builder2.AddJsonFile("appsettings.json", optional: false);
    builder2.AddJsonFile($"appsettings.{environment}.json", optional: true);
    var config = builder2.Build();

    var options = new DbContextOptionsBuilder<CompassDbContext>()
        .UseSqlServer(config.GetConnectionString("DefaultConnection"))
        .Options;
    using var db = new CompassDbContext(options);

    var skills = await db.Skills.OrderBy(s => s.SkillName).ToListAsync();
    Console.WriteLine($"Total Skills: {skills.Count}\n");

    Console.WriteLine("First 20 Skills:\n");
    foreach (var skill in skills.Take(20))
    {
        Console.WriteLine($"  - {skill.SkillName}");
    }

    return;
}

// Check for data migration command
if (args.Length > 0 && args[0] == "--migrate-data")
{
    await RunDataMigration(builder);
    return;
}

// Check for populate product document IDs command
if (args.Length > 0 && args[0] == "--populate-product-document-ids")
{
    var environment = args.Length > 1 && args[1] == "--environment" && args.Length > 2
        ? args[2]
        : "Development";
    await Compass.PopulateProductDocumentId.RunAsync(environment);
    return;
}

// Check for Azure SQL → Azure SQL environment migration
if (args.Length > 0 && args[0] == "--migrate-sql")
{
    // Usage: --migrate-sql --source Development --target Test|Production
    string source = "Development";
    string target = "Production";
    bool referenceOnly = false;

    for (int i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--source") source = args[i + 1];
        if (args[i] == "--target") target = args[i + 1];
        if (args[i] == "--reference-only") referenceOnly = true;
    }

    await Compass.AzureSqlEnvironmentMigration.RunAsync(source, target, referenceOnly);
    return;
}

// Add file logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddFile("logs/compass-{Date}.log");

var applicationInsightsConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
var applicationInsightsInstrumentationKey = builder.Configuration["ApplicationInsights:InstrumentationKey"];
if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString)
    || !string.IsNullOrWhiteSpace(applicationInsightsInstrumentationKey))
{
    builder.Services.AddApplicationInsightsTelemetry(options =>
    {
        if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))
            options.ConnectionString = applicationInsightsConnectionString;
    });
    builder.Logging.AddApplicationInsights();
}

// Add services to the container
builder.Services.AddRazorPages();

// Development: persist DP keys so TempData cookies and other protected payloads survive dotnet restarts
// (avoids "payload was invalid" when the in-memory key ring changes).
if (builder.Environment.IsDevelopment())
{
    var keysDir = Path.Combine(builder.Environment.ContentRootPath, "DataProtection-Keys");
    Directory.CreateDirectory(keysDir);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
        .SetApplicationName("Compass");
}

// Configure authentication with Entra ID
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

// Add Controllers with Views
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add<Compass.Filters.NavigationViewFilter>();
    options.Filters.Add<Compass.Filters.ViewAsWriteProtectionFilter>();
})
    .AddMicrosoftIdentityUI();

builder.Services.Configure<RazorViewEngineOptions>(options =>
{
    options.ViewLocationExpanders.Add(new ModernUiViewLocationExpander());
});

builder.Services.AddAuthorization();

// Microsoft Graph app-to-app client using Entra configuration
builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var tenantId = configuration["Entra:TenantId"];
    var clientId = configuration["Entra:ClientId"];
    var clientSecret = configuration["Entra:ClientSecret"];

    if (string.IsNullOrWhiteSpace(tenantId) ||
        string.IsNullOrWhiteSpace(clientId) ||
        string.IsNullOrWhiteSpace(clientSecret))
    {
        throw new InvalidOperationException("Entra configuration is missing TenantId, ClientId, or ClientSecret.");
    }

    var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
    return new Microsoft.Graph.GraphServiceClient(credential);
});

// Configure database - Use Azure SQL for all environments
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

// Diagnostic logging (non-sensitive only - never log connection string or credentials)
if (builder.Environment.IsDevelopment())
{
    var currentEnvironment = builder.Environment.EnvironmentName;
    Console.WriteLine($"\n[CONFIG] ASPNETCORE_ENVIRONMENT = {currentEnvironment}");
    var hasConnectionString = !string.IsNullOrEmpty(connectionString);
    Console.WriteLine($"[CONFIG] ConnectionStrings:DefaultConnection configured: {hasConnectionString}");
    if (connectionString != null)
    {
        var catalogMatch = System.Text.RegularExpressions.Regex.Match(connectionString, @"Initial Catalog=([^;]+)");
        if (catalogMatch.Success)
            Console.WriteLine($"[CONFIG] Database catalog: {catalogMatch.Groups[1].Value}");
        Console.WriteLine();
    }
}

if (string.IsNullOrEmpty(connectionString))
{
    throw new InvalidOperationException("Database connection string 'DefaultConnection' not found.");
}

builder.Services.AddSingleton<Compass.Infrastructure.WorkRegisterSqlDiagnosticsInterceptor>();
builder.Services.AddDbContext<CompassDbContext>((sp, options) =>
    options.UseSqlServer(connectionString, sqlServerOptions =>
        {
            sqlServerOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorNumbersToAdd: null);
            sqlServerOptions.CommandTimeout(60);
            sqlServerOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        })
        .AddInterceptors(sp.GetRequiredService<Compass.Infrastructure.WorkRegisterSqlDiagnosticsInterceptor>()));

// Register HTTP clients for API services
builder.Services.Configure<Compass.Configuration.DocsApiExplorerOptions>(
    builder.Configuration.GetSection(Compass.Configuration.DocsApiExplorerOptions.SectionName));
builder.Services.AddHttpClient(nameof(Compass.Services.Docs.ApiExplorerRequestProxyService));
builder.Services.AddScoped<Compass.Services.Docs.IApiExplorerRequestProxyService, Compass.Services.Docs.ApiExplorerRequestProxyService>();

builder.Services.AddHttpClient<ICmsApiService, CmsApiService>();
builder.Services.AddHttpClient<IStandardsCmsApiService, StandardsCmsApiService>();
builder.Services.AddHttpClient<IProductsApiService, ProductsApiService>(client =>
{
    var baseUrl = builder.Configuration["CmsApi:BaseUrl"];
    var apiKey = builder.Configuration["CmsApi:ReadApiKey"];

    if (!string.IsNullOrEmpty(baseUrl))
    {
        client.BaseAddress = new Uri(baseUrl);
    }

    if (!string.IsNullOrEmpty(apiKey))
    {
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }
});

// Register HttpClient for GovernmentDepartmentController
builder.Services.AddHttpClient<Compass.Controllers.GovernmentDepartmentController>();

// Register Service Assessment API service
builder.Services.AddHttpClient<IServiceAssessmentApiService, ServiceAssessmentApiService>((client, sp) =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<ServiceAssessmentApiService>>();
    var baseUrl = configuration["FipsSync:Sas:Endpoint"]
        ?? configuration["ServiceAssessments:ApiUrl"]
        ?? "https://service-assessments.education.gov.uk/api/";

    // Ensure base URL ends with /
    if (!baseUrl.EndsWith("/"))
    {
        baseUrl += "/";
    }

    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "COMPASS-Analysis/1.0");

    return new ServiceAssessmentApiService(client, configuration, logger);
});

// Register services
builder.Services.AddSingleton<SubNavExportResolver>();
builder.Services.AddSingleton<SubNavDataAccessResolver>();
builder.Services.AddScoped<IReturnStatusService, ReturnStatusService>();
builder.Services.AddScoped<IMonthlyUpdateService, MonthlyUpdateService>();
builder.Services.AddScoped<IWeeklyUpdateService, WeeklyUpdateService>();
builder.Services.AddScoped<IPerformanceReportingEligibilityService, PerformanceReportingEligibilityService>();
builder.Services.AddScoped<IGraphService, GraphService>();
builder.Services.AddScoped<IApiTokenService, ApiTokenService>();
builder.Services.AddScoped<Compass.Services.Api.IApiTokenPortalService, Compass.Services.Api.ApiTokenPortalService>();
builder.Services.AddScoped<IAuditLogger, AuditLogger>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddScoped<IViewAsUserService, ViewAsUserService>();
builder.Services.AddScoped<Compass.Filters.ViewAsWriteProtectionFilter>();
builder.Services.AddScoped<IBusinessAreaAdminService, BusinessAreaAdminService>();
builder.Services.AddScoped<IBusinessAreaLeadershipService, BusinessAreaLeadershipService>();
builder.Services.AddScoped<IDirectorateLeadershipService, DirectorateLeadershipService>();
builder.Services.AddScoped<ICompassNotificationEmailLogService, CompassNotificationEmailLogService>();
builder.Services.AddScoped<ICompassNotificationSettingsService, CompassNotificationSettingsService>();
builder.Services.AddScoped<IWorkReportingNotificationService, WorkReportingNotificationService>();
builder.Services.AddScoped<IWorkItemNotificationService, WorkItemNotificationService>();
builder.Services.AddHostedService<WorkReportingNotificationHostedService>();
builder.Services.AddScoped<Compass.Services.Fips.IFipsCmdbDailySyncService, Compass.Services.Fips.FipsCmdbDailySyncService>();
builder.Services.AddHostedService<FipsCmdbDailySyncHostedService>();
builder.Services.AddScoped<IGlobalFeatureToggleService, GlobalFeatureToggleService>();
builder.Services.AddScoped<Compass.Filters.DemandFeatureGateFilter>();
builder.Services.AddScoped<Compass.Filters.StandardsFeatureGateFilter>();
builder.Services.AddScoped<Compass.Filters.RaidFeatureGateFilter>();
builder.Services.AddScoped<Compass.Filters.DdrFeatureGateFilter>();
builder.Services.AddScoped<Compass.Filters.ProjectLegacyRedirectFilter>();
builder.Services.AddScoped<IUserDirectoryService, UserDirectoryService>();
builder.Services.AddScoped<IProjectImportService, ProjectImportService>();
builder.Services.AddScoped<IAuditContextProvider, HttpAuditContextProvider>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IHttpErrorEmailSettingsService, HttpErrorEmailSettingsService>();
builder.Services.AddScoped<IHttpErrorMonitoringService, HttpErrorMonitoringService>();
builder.Services.AddScoped<INudgingService, NudgingService>();
builder.Services.AddScoped<INotificationRuleService, NotificationRuleService>();
builder.Services.AddScoped<IAccessibilityTrainingService, AccessibilityTrainingService>();
builder.Services.AddScoped<Compass.Services.DemandTriage.IDemandTriageService, Compass.Services.DemandTriage.DemandTriageService>();
builder.Services.AddScoped<Compass.Services.Dashboard.IHomeDashboardViewModelBuilder, Compass.Services.Dashboard.HomeDashboardViewModelBuilder>();
builder.Services.AddScoped<Compass.Services.Dashboard.IYourTasksViewModelBuilder, Compass.Services.Dashboard.YourTasksViewModelBuilder>();
builder.Services.Configure<Compass.Configuration.WorkRegisterDiagnosticsOptions>(
    builder.Configuration.GetSection(Compass.Configuration.WorkRegisterDiagnosticsOptions.SectionName));
builder.Services.AddSingleton<Compass.Services.WorkRegisterPerfFileLog>();
builder.Services.AddScoped<Compass.Services.Modern.IModernWorkService, Compass.Services.Modern.ModernWorkService>();
builder.Services.AddScoped<Compass.Services.Modern.IWorkServiceRegisterLinkService, Compass.Services.Modern.WorkServiceRegisterLinkService>();
builder.Services.AddScoped<Compass.Services.Modern.IWorkScopedExcelExportService, Compass.Services.Modern.WorkScopedExcelExportService>();
builder.Services.AddScoped<ModernMonthlyReportService>();
builder.Services.AddScoped<ModernRaidReviewProgressService>();
builder.Services.AddScoped<ModernRisksByTierReportService>();
builder.Services.AddScoped<ModernRaidRegisterCoverageReportService>();
builder.Services.AddScoped<ModernRaidReportingService>();
builder.Services.AddScoped<ModernRaidReportService>();
builder.Services.AddScoped<Compass.Services.Raid.IOperationsRiskEditService, Compass.Services.Raid.OperationsRiskEditService>();
builder.Services.AddScoped<Compass.Services.Raid.IRaidRiskEditorFormService, Compass.Services.Raid.RaidRiskEditorFormService>();
builder.Services.AddScoped<Compass.Services.Raid.IRaidIssueEditorFormService, Compass.Services.Raid.RaidIssueEditorFormService>();
builder.Services.AddScoped<Compass.Services.Raid.IRaidRegisterSpreadsheetLayoutService, Compass.Services.Raid.RaidRegisterSpreadsheetLayoutService>();
builder.Services.AddScoped<CommissionReportingAnalyticsService>();
builder.Services.AddScoped<Compass.Services.DemandPipeline.IDemandScoringFrameworkService, Compass.Services.DemandPipeline.DemandScoringFrameworkService>();
builder.Services.AddScoped<Compass.Services.DdtStandards.IDdtStandardsWorkflowService, Compass.Services.DdtStandards.DdtStandardsWorkflowService>();

// Register HttpClientFactory for PerformanceReportingManagementController
builder.Services.AddHttpClient();

// FIPS Sync Services
builder.Services.Configure<Compass.Models.Fips.FipsSyncConfiguration>(
    builder.Configuration.GetSection("FipsSync"));
builder.Services.Configure<Compass.Configuration.CmsAccessRequestApiOptions>(
    builder.Configuration.GetSection(Compass.Configuration.CmsAccessRequestApiOptions.SectionName));
builder.Services.AddHttpClient<Compass.Services.Fips.ICmdbService, Compass.Services.Fips.CmdbService>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient<Compass.Services.Fips.IStrapiService, Compass.Services.Fips.StrapiService>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddScoped<Compass.Services.Fips.IFipsSyncOrchestrator, Compass.Services.Fips.FipsSyncOrchestrator>();
builder.Services.AddScoped<Compass.Services.Fips.IFipsCmdbProductSyncService, Compass.Services.Fips.FipsCmdbProductSyncService>();
builder.Services.AddScoped<Compass.Services.Fips.IFipsProductWriteService, Compass.Services.Fips.FipsProductWriteService>();
builder.Services.AddScoped<Compass.Services.Fips.IFipsCompletionBulkImportService, Compass.Services.Fips.FipsCompletionBulkImportService>();
builder.Services.AddScoped<Compass.Services.Fips.IFipsStrapiLegacyImportService, Compass.Services.Fips.FipsStrapiLegacyImportService>();
builder.Services.AddScoped<Compass.Services.Fips.IFipsBusinessAreaLookupSyncService, Compass.Services.Fips.FipsBusinessAreaLookupSyncService>();
builder.Services.AddScoped<Compass.Services.Fips.IFipsDirectorateLookupSyncService, Compass.Services.Fips.FipsDirectorateLookupSyncService>();
builder.Services.Configure<Compass.Configuration.EnvironmentSyncOptions>(
    builder.Configuration.GetSection(Compass.Configuration.EnvironmentSyncOptions.SectionName));
builder.Services.AddScoped<Compass.Services.EnvironmentSync.IEnvironmentSyncService, Compass.Services.EnvironmentSync.EnvironmentSyncService>();
builder.Services.AddScoped<Compass.Services.ICmsCompassServiceDataComparisonService, Compass.Services.CmsCompassServiceDataComparisonService>();
builder.Services.AddScoped<Compass.Services.ICmsCompassServiceDataSyncService, Compass.Services.CmsCompassServiceDataSyncService>();
builder.Services.AddHttpClient<Compass.Services.Aiss.IAissSummaryService, Compass.Services.Aiss.AissSummaryService>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient<Compass.Services.Aiss.IAissProductAccessibilityService, Compass.Services.Aiss.AissProductAccessibilityService>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5));

builder.Services.AddHttpContextAccessor();

// Add memory caching
builder.Services.AddMemoryCache(options =>
{
    options.CompactionPercentage = builder.Configuration.GetValue<double>("Caching:MemoryCache:CompactionPercentage", 0.25);
});

// Add session support
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(20);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Name = "Compass.Session";
});

// Respect proxy forwarded headers so RemoteIpAddress reflects the client
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Add rate limiting
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: GetGlobalPartitionKey(httpContext),
            factory: partition => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            }));

    // More conservative named policies for surveys endpoints (per IP)
    options.AddPolicy("ResponsesPolicy", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: GetFipsPartitionKey(httpContext) ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1)
            }));

    options.AddPolicy("SurveysGetPolicy", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: GetFipsPartitionKey(httpContext) ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1)
            }));

    options.AddPolicy("CmsAccessRequestsCreatePolicy", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: GetFipsPartitionKey(httpContext) ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 15,
                Window = TimeSpan.FromMinutes(1)
            }));

    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/json";
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra) ? ra : TimeSpan.FromSeconds(60);
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            error = "rate_limited",
            message = "Too many requests. Try again later.",
            retryAfterSeconds = (int)retryAfter.TotalSeconds
        });
        await context.HttpContext.Response.WriteAsync(payload, token);
    };
});

var app = builder.Build();

// Configure port for local development
if (app.Environment.IsDevelopment())
{
    app.Urls.Add("http://localhost:5500");
}

app.UseForwardedHeaders();

app.UseMiddleware<HeadRequestAsGetMiddleware>();
app.UseMiddleware<HttpErrorMonitoringMiddleware>();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Configure HTTPS redirection
// Skip in development since we're running on HTTP only (localhost:5500)
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseStaticFiles();
// After static files so missing /css|/js assets return 404, not an HTML error page (breaks MIME types + CSP).
app.UseStatusCodePagesWithReExecute("/Home/NotFound");

// Add security headers
app.Use(async (context, next) =>
{
    if (!app.Environment.IsDevelopment())
    {
        context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains; preload";
    }
    else
    {
        context.Response.Headers["Strict-Transport-Security"] = "max-age=300; includeSubDomains";
    }

    var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    context.Items["Nonce"] = nonce;

    var req = context.Request;
    // Iframe embed (?embed=1): allow same-origin framing for work edit modals. Do not read Request.Form here
    // (would consume the body before MVC model binding on POST).
    var isEmbedRequest =
        string.Equals(req.Query["embed"].FirstOrDefault(), "1", StringComparison.Ordinal);

    // connect-src: note CSP host wildcards match one subdomain label only — e.g.
    // *.applicationinsights.azure.com does NOT match region.in.applicationinsights.azure.com.
    var connectSrc =
        "'self' " +
        "https://cdnjs.cloudflare.com https://cdn.jsdelivr.net " +
        "https://www.clarity.ms https://*.clarity.ms https://c.bing.com https://*.bing.com " +
        "https://www.google-analytics.com https://*.google-analytics.com https://region1.google-analytics.com " +
        "https://login.microsoftonline.com https://login.live.com " +
        "https://fonts.googleapis.com https://fonts.gstatic.com " +
        "https://dc.services.visualstudio.com https://rt.services.visualstudio.com " +
        "https://*.applicationinsights.azure.com https://*.in.applicationinsights.azure.com " +
        "https://*.applicationinsights.microsoft.com " +
        "https://*.monitor.azure.com https://js.monitor.azure.com " +
        "https://browser.events.data.microsoft.com https://*.events.data.microsoft.com " +
        "https://web.vortex.data.microsoft.com " +
        "https://*.livediagnostics.monitor.azure.com wss://*.livediagnostics.monitor.azure.com";
    // Local / non-prod: same as Development launch profile and Properties/launchSettings "Test" profile.
    var apiExplorerHosts = app.Configuration
        .GetSection(Compass.Configuration.DocsApiExplorerOptions.SectionName)
        .Get<Compass.Configuration.DocsApiExplorerOptions>();
    if (apiExplorerHosts != null)
    {
        foreach (var host in apiExplorerHosts.AllConnectHosts())
            connectSrc += " " + host;
    }
    connectSrc += " https://*.education.gov.uk https://*.azurewebsites.net";
    var requestOrigin = $"{req.Scheme}://{req.Host.Value}";
    if (!connectSrc.Contains(requestOrigin, StringComparison.OrdinalIgnoreCase))
        connectSrc += " " + requestOrigin;

    if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Test"))
    {
        connectSrc += " ws://localhost:* ws://127.0.0.1:* wss://localhost:* wss://127.0.0.1:*";
        // Non-production: allow external telemetry/tooling endpoints that vary by region/tenant.
        // Keep production CSP strict and explicitly allow-listed.
        connectSrc += " https: http: wss: ws:";
    }
    // Broader default-src helps preconnect / directives that fall back to default-src (e.g. Clarity load-balancing).
    var defaultSrc =
        "'self' https://fonts.googleapis.com https://fonts.gstatic.com " +
        "https://www.clarity.ms https://*.clarity.ms https://c.bing.com";
    var csp =
        $"default-src {defaultSrc}; " +
        $"script-src 'self' 'nonce-{nonce}' 'strict-dynamic' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com https://cdn.datatables.net https://www.clarity.ms https://*.clarity.ms https://www.googletagmanager.com; " +
        "style-src 'self' 'unsafe-inline' https://rsms.me https://fonts.googleapis.com https://cdnjs.cloudflare.com https://cdn.jsdelivr.net https://cdn.datatables.net; " +
        "img-src 'self' data: https:; " +
        "font-src 'self' data: https://rsms.me https://fonts.gstatic.com https://cdnjs.cloudflare.com; " +
        $"connect-src {connectSrc}; " +
        (isEmbedRequest ? "frame-ancestors 'self'; " : "frame-ancestors 'none'; ") +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "object-src 'none'";
    if (!app.Environment.IsDevelopment())
    {
        csp += "; upgrade-insecure-requests";
    }
    context.Response.Headers["Content-Security-Policy"] = csp;

    context.Response.Headers["X-Frame-Options"] = isEmbedRequest ? "SAMEORIGIN" : "DENY";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

    await next();
});

app.UseRouting();

app.UseMiddleware<Compass.Middleware.WorkRegisterResponseDiagnosticsMiddleware>();

// API token authentication and logging (must be before UseAuthentication for API routes)
app.UseMiddleware<ApiAuthenticationMiddleware>();
app.UseMiddleware<ApiLoggingMiddleware>();

app.UseAuthentication();
app.UseMiddleware<EnsureCompassUserMiddleware>();
app.UseAuthorization();

app.UseSession();
app.UseRateLimiter();

// Map controllers with attribute routing (must be called to enable attribute routing)
app.MapControllers();

// Area routes (must come before default routes)
app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

// Friendly URLs for operational / commission reporting (must be before default route)
app.MapControllerRoute(
    name: "performanceProduct",
    pattern: "Performance/Product/{productId}",
    defaults: new { controller = "ProductReporting", action = "ProductCommissions" });

app.MapControllerRoute(
    name: "performanceRoot",
    pattern: "Performance",
    defaults: new { controller = "ModernPerformance", action = "Index" });

// Default MVC routes — register BEFORE the api/ prefix route so Url.Action / tag helpers
// generate /Controller/Action/... instead of /api/Controller/Action/... for conventional controllers.
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Explicit api/ prefix for callers that need it (incoming /api/... still matches here)
app.MapControllerRoute(
    name: "api",
    pattern: "api/{controller}/{action=Index}/{id?}");

app.MapRazorPages();

// Initialize database
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<CompassDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var superAdminEmail = config["Authentication:SuperAdminEmail"];

    try
    {
        logger.LogInformation("Starting database initialization...");

        // Test database connection first
        if (!await context.Database.CanConnectAsync())
        {
            var errorMsg = "Cannot connect to database. Please check connection string and firewall settings.";
            logger.LogError(errorMsg);
            throw new InvalidOperationException(errorMsg);
        }
        logger.LogInformation("Database connection successful");

        // Repair: empty AddWeeklyWorkReporting migration recorded in history but tables never created (--no-build artefact).
        await RemoveOrphanedEmptyWeeklyWorkReportingMigrationAsync(context, logger);

        // Apply any pending migrations
        logger.LogInformation("Applying database migrations...");
        await context.Database.MigrateAsync();
        logger.LogInformation("Database migrations applied successfully");

        // Repair: CMDBProducts.IsEnterpriseService when history exists but column missing (restored DB, partial deploy).
        await EnsureCMDBProductIsEnterpriseServiceColumnAsync(context, logger);

        // Repair: notification tables when migration was not applied (e.g. migration metadata out of sync)
        await EnsureCompassNotificationTablesAsync(context, logger);

        // Repair: MatrixScore on RAID likelihood / impact lookups (migration sometimes skipped vs. model snapshot)
        await EnsureRaidLikelihoodImpactMatrixScoreColumnsAsync(context, logger);

        // Repair: Feature.AccessMode + FeatureUserAllows when history says applied but objects are missing (restored DB, etc.)
        await EnsureFeatureAccessModeAndUserAllowAsync(context, logger);

        // Repair: API token self-service tables when AddApiTokenSelfService was recorded but empty (generated with --no-build).
        await EnsureApiTokenSelfServiceTablesAsync(context, logger);

        // Repair: weekly work reporting tables when AddWeeklyWorkReporting was recorded but empty (generated with --no-build).
        await EnsureWeeklyWorkReportingTablesAsync(context, logger);

        // Seed statement templates if they don't exist
        await SeedStatementTemplatesAsync(context);

        // Seed RBAC initial data (groups, features, super admin) - super admin email from config only
        await SeedRbacInitialDataAsync(context, superAdminEmail);

        // Seed Service Standards (GOV.UK Service Standards 1-14)
        await SeedServiceStandardsAsync(context);

        // Seed DDaT Professions
        await SeedDdatProfessionsAsync(context);

        // Seed Technology Code of Practice
        await SeedTechnologyCodeOfPracticeAsync(context);

        // Seed Grades
        await SeedGradesAsync(context);

        logger.LogInformation("Compass database initialized successfully");
        Console.WriteLine("Compass database initialized successfully");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Compass database initialization error: {Message}", ex.Message);
        Console.WriteLine($"Compass database initialization error: {ex.Message}");
        Console.WriteLine($"Stack trace: {ex.StackTrace}");
        if (ex.InnerException != null)
        {
            Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
        }
        throw;
    }
}

app.Run();

/// <summary>
/// Ensures <c>MatrixScore</c> exists on lookup tables when the DB predates migration
/// <see cref="Compass.Migrations.AddRiskLikelihoodImpactMatrixScore"/> or history is out of sync (restored DB, etc.).
/// </summary>
/// <summary>
/// Ensures Compass notification settings / email log tables exist when <see cref="Compass.Migrations.AddCompassNotificationManagement"/>
/// did not run (missing from migration history, restored DB, etc.).
/// </summary>
/// <summary>
/// Ensures API token self-service schema exists when <see cref="Compass.Migrations.AddApiTokenSelfService"/>
/// was applied as an empty migration (e.g. generated with --no-build before models compiled).
/// </summary>
static async Task EnsureApiTokenSelfServiceTablesAsync(CompassDbContext context, ILogger logger)
{
    try
    {
        await context.Database.ExecuteSqlRawAsync("""
            IF COL_LENGTH(N'dbo.ApiTokens', N'OwnerEmail') IS NULL
                ALTER TABLE [dbo].[ApiTokens] ADD [OwnerEmail] nvarchar(256) NULL;
            IF COL_LENGTH(N'dbo.ApiTokens', N'Environment') IS NULL
                ALTER TABLE [dbo].[ApiTokens] ADD [Environment] nvarchar(10) NULL;
            IF COL_LENGTH(N'dbo.ApiTokens', N'ProjectSlug') IS NULL
                ALTER TABLE [dbo].[ApiTokens] ADD [ProjectSlug] nvarchar(50) NULL;
            IF COL_LENGTH(N'dbo.ApiTokens', N'AccessTier') IS NULL
                ALTER TABLE [dbo].[ApiTokens] ADD [AccessTier] nvarchar(20) NULL;
            IF COL_LENGTH(N'dbo.ApiTokens', N'IsSelfService') IS NULL
                ALTER TABLE [dbo].[ApiTokens] ADD [IsSelfService] bit NOT NULL CONSTRAINT [DF_ApiTokens_IsSelfService] DEFAULT 0;

            IF OBJECT_ID(N'dbo.ApiTokenRequests', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[ApiTokenRequests] (
                    [Id] int NOT NULL IDENTITY(1,1),
                    [RequestorEmail] nvarchar(256) NOT NULL,
                    [Environment] nvarchar(10) NOT NULL,
                    [ProjectSlug] nvarchar(50) NOT NULL,
                    [Justification] nvarchar(2000) NULL,
                    [Status] int NOT NULL,
                    [PermissionsJson] nvarchar(max) NOT NULL,
                    [IsReadOnlyAllData] bit NOT NULL,
                    [ReviewedByEmail] nvarchar(256) NULL,
                    [ReviewedAt] datetime2 NULL,
                    [ReviewNotes] nvarchar(2000) NULL,
                    [IssuedApiTokenId] int NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    CONSTRAINT [PK_ApiTokenRequests] PRIMARY KEY CLUSTERED ([Id]),
                    CONSTRAINT [FK_ApiTokenRequests_ApiTokens_IssuedApiTokenId] FOREIGN KEY ([IssuedApiTokenId])
                        REFERENCES [dbo].[ApiTokens]([Id]) ON DELETE SET NULL
                );
            END

            IF OBJECT_ID(N'dbo.ApiTokenMembers', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[ApiTokenMembers] (
                    [Id] int NOT NULL IDENTITY(1,1),
                    [ApiTokenId] int NOT NULL,
                    [UserEmail] nvarchar(256) NOT NULL,
                    [AddedByEmail] nvarchar(256) NOT NULL,
                    [AddedAt] datetime2 NOT NULL,
                    CONSTRAINT [PK_ApiTokenMembers] PRIMARY KEY CLUSTERED ([Id]),
                    CONSTRAINT [FK_ApiTokenMembers_ApiTokens_ApiTokenId] FOREIGN KEY ([ApiTokenId])
                        REFERENCES [dbo].[ApiTokens]([Id]) ON DELETE CASCADE
                );
            END

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ApiTokens_Name' AND object_id = OBJECT_ID(N'dbo.ApiTokens'))
                CREATE UNIQUE INDEX [IX_ApiTokens_Name] ON [dbo].[ApiTokens]([Name]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ApiTokens_OwnerEmail' AND object_id = OBJECT_ID(N'dbo.ApiTokens'))
                CREATE NONCLUSTERED INDEX [IX_ApiTokens_OwnerEmail] ON [dbo].[ApiTokens]([OwnerEmail]);
            IF OBJECT_ID(N'dbo.ApiTokenMembers', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ApiTokenMembers_ApiTokenId_UserEmail' AND object_id = OBJECT_ID(N'dbo.ApiTokenMembers'))
                CREATE UNIQUE INDEX [IX_ApiTokenMembers_ApiTokenId_UserEmail] ON [dbo].[ApiTokenMembers]([ApiTokenId], [UserEmail]);
            IF OBJECT_ID(N'dbo.ApiTokenRequests', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ApiTokenRequests_Status' AND object_id = OBJECT_ID(N'dbo.ApiTokenRequests'))
                CREATE NONCLUSTERED INDEX [IX_ApiTokenRequests_Status] ON [dbo].[ApiTokenRequests]([Status]);
            IF OBJECT_ID(N'dbo.ApiTokenRequests', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ApiTokenRequests_RequestorEmail' AND object_id = OBJECT_ID(N'dbo.ApiTokenRequests'))
                CREATE NONCLUSTERED INDEX [IX_ApiTokenRequests_RequestorEmail] ON [dbo].[ApiTokenRequests]([RequestorEmail]);
            IF OBJECT_ID(N'dbo.ApiTokenRequests', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ApiTokenRequests_IssuedApiTokenId' AND object_id = OBJECT_ID(N'dbo.ApiTokenRequests'))
                CREATE NONCLUSTERED INDEX [IX_ApiTokenRequests_IssuedApiTokenId] ON [dbo].[ApiTokenRequests]([IssuedApiTokenId]);
            """);
        logger.LogInformation("Ensured API token self-service tables and columns (if missing).");
    }
    catch (Microsoft.Data.SqlClient.SqlException ex)
    {
        logger.LogWarning(ex,
            "Could not ensure API token self-service schema (non-fatal if already exists): {Message}",
            ex.Message);
    }
}

/// <summary>
/// Removes migration history for the empty <c>20260702100939_AddWeeklyWorkReporting</c> migration when tables were never created.
/// </summary>
static async Task RemoveOrphanedEmptyWeeklyWorkReportingMigrationAsync(CompassDbContext context, ILogger logger)
{
    try
    {
        await context.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID(N'dbo.WeeklyWorkReportingConfigs', N'U') IS NULL
               AND EXISTS (
                   SELECT 1 FROM [__EFMigrationsHistory]
                   WHERE [MigrationId] = N'20260702100939_AddWeeklyWorkReporting')
                DELETE FROM [__EFMigrationsHistory]
                WHERE [MigrationId] = N'20260702100939_AddWeeklyWorkReporting';
            """);
        logger.LogInformation("Removed orphaned empty AddWeeklyWorkReporting migration history (if applicable).");
    }
    catch (Microsoft.Data.SqlClient.SqlException ex)
    {
        logger.LogWarning(ex,
            "Could not remove orphaned AddWeeklyWorkReporting migration history: {Message}",
            ex.Message);
    }
}

/// <summary>
/// Ensures weekly work reporting schema exists when <see cref="Compass.Migrations.AddWeeklyWorkReporting"/>
/// was applied as an empty migration (e.g. generated with --no-build before models compiled).
/// </summary>
static async Task EnsureWeeklyWorkReportingTablesAsync(CompassDbContext context, ILogger logger)
{
    try
    {
        await context.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID(N'dbo.WeeklyWorkReportingConfigs', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[WeeklyWorkReportingConfigs] (
                    [Id] int NOT NULL IDENTITY(1,1),
                    [PeriodStartDayOfWeek] int NOT NULL,
                    [PeriodEndDayOfWeek] int NOT NULL,
                    [DueDayOfWeek] int NOT NULL,
                    [DueWeekOffset] int NOT NULL,
                    [IsActive] bit NOT NULL,
                    [UpdatedAt] datetime2 NOT NULL,
                    CONSTRAINT [PK_WeeklyWorkReportingConfigs] PRIMARY KEY CLUSTERED ([Id])
                );
                SET IDENTITY_INSERT [dbo].[WeeklyWorkReportingConfigs] ON;
                INSERT INTO [dbo].[WeeklyWorkReportingConfigs]
                    ([Id], [PeriodStartDayOfWeek], [PeriodEndDayOfWeek], [DueDayOfWeek], [DueWeekOffset], [FirstReportingPeriodStart], [IsActive], [UpdatedAt])
                VALUES (1, 1, 5, 5, 0, '2026-06-29T00:00:00.0000000Z', 1, '2026-07-02T00:00:00.0000000Z');
                SET IDENTITY_INSERT [dbo].[WeeklyWorkReportingConfigs] OFF;
            END

            IF COL_LENGTH(N'dbo.WeeklyWorkReportingConfigs', N'FirstReportingPeriodStart') IS NULL
            BEGIN
                ALTER TABLE [dbo].[WeeklyWorkReportingConfigs]
                    ADD [FirstReportingPeriodStart] datetime2 NOT NULL
                    CONSTRAINT [DF_WeeklyWorkReportingConfigs_FirstReportingPeriodStart] DEFAULT '2026-06-29T00:00:00.0000000Z';
            END

            IF OBJECT_ID(N'dbo.WeeklyWorkReportingScopeProjects', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[WeeklyWorkReportingScopeProjects] (
                    [Id] int NOT NULL IDENTITY(1,1),
                    [ProjectId] int NOT NULL,
                    [AddedAt] datetime2 NOT NULL,
                    [AddedByEmail] nvarchar(450) NULL,
                    CONSTRAINT [PK_WeeklyWorkReportingScopeProjects] PRIMARY KEY CLUSTERED ([Id]),
                    CONSTRAINT [FK_WeeklyWorkReportingScopeProjects_Projects_ProjectId] FOREIGN KEY ([ProjectId])
                        REFERENCES [dbo].[Projects]([Id]) ON DELETE CASCADE
                );
            END

            IF OBJECT_ID(N'dbo.WeeklyWorkReportingScopeProjects', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_WeeklyWorkReportingScopeProjects_ProjectId' AND object_id = OBJECT_ID(N'dbo.WeeklyWorkReportingScopeProjects'))
                CREATE UNIQUE INDEX [IX_WeeklyWorkReportingScopeProjects_ProjectId] ON [dbo].[WeeklyWorkReportingScopeProjects]([ProjectId]);

            IF OBJECT_ID(N'dbo.ProjectWeeklyWorkUpdates', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[ProjectWeeklyWorkUpdates] (
                    [Id] int NOT NULL IDENTITY(1,1),
                    [ProjectId] int NOT NULL,
                    [IsoYear] int NOT NULL,
                    [IsoWeek] int NOT NULL,
                    [WeekStartDate] datetime2 NOT NULL,
                    [WeekEndDate] datetime2 NOT NULL,
                    [Narrative] nvarchar(4000) NOT NULL,
                    [CreatedByEntraId] nvarchar(450) NULL,
                    [CreatedByName] nvarchar(450) NULL,
                    [CreatedByEmail] nvarchar(450) NULL,
                    [CreatedByUserId] int NULL,
                    [UpdatedByUserId] int NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    [UpdatedAt] datetime2 NULL,
                    [SubmittedAt] datetime2 NULL,
                    [WeeklyPermFte] decimal(18,2) NULL,
                    [WeeklyMspFte] decimal(18,2) NULL,
                    [PeopleNarrative] nvarchar(4000) NULL,
                    [DraftRagStatusLookupId] int NULL,
                    [DraftRagJustification] nvarchar(4000) NULL,
                    [DraftPathToGreen] nvarchar(4000) NULL,
                    CONSTRAINT [PK_ProjectWeeklyWorkUpdates] PRIMARY KEY CLUSTERED ([Id]),
                    CONSTRAINT [FK_ProjectWeeklyWorkUpdates_Projects_ProjectId] FOREIGN KEY ([ProjectId])
                        REFERENCES [dbo].[Projects]([Id]) ON DELETE CASCADE,
                    CONSTRAINT [FK_ProjectWeeklyWorkUpdates_RagStatusLookups_DraftRagStatusLookupId] FOREIGN KEY ([DraftRagStatusLookupId])
                        REFERENCES [dbo].[RagStatusLookups]([Id]),
                    CONSTRAINT [FK_ProjectWeeklyWorkUpdates_Users_CreatedByUserId] FOREIGN KEY ([CreatedByUserId])
                        REFERENCES [dbo].[Users]([Id]),
                    CONSTRAINT [FK_ProjectWeeklyWorkUpdates_Users_UpdatedByUserId] FOREIGN KEY ([UpdatedByUserId])
                        REFERENCES [dbo].[Users]([Id])
                );
            END

            IF OBJECT_ID(N'dbo.ProjectWeeklyWorkUpdates', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProjectWeeklyWorkUpdates_ProjectId_IsoYear_IsoWeek' AND object_id = OBJECT_ID(N'dbo.ProjectWeeklyWorkUpdates'))
                CREATE UNIQUE INDEX [IX_ProjectWeeklyWorkUpdates_ProjectId_IsoYear_IsoWeek] ON [dbo].[ProjectWeeklyWorkUpdates]([ProjectId], [IsoYear], [IsoWeek]);
            IF OBJECT_ID(N'dbo.ProjectWeeklyWorkUpdates', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProjectWeeklyWorkUpdates_CreatedByUserId' AND object_id = OBJECT_ID(N'dbo.ProjectWeeklyWorkUpdates'))
                CREATE NONCLUSTERED INDEX [IX_ProjectWeeklyWorkUpdates_CreatedByUserId] ON [dbo].[ProjectWeeklyWorkUpdates]([CreatedByUserId]);
            IF OBJECT_ID(N'dbo.ProjectWeeklyWorkUpdates', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProjectWeeklyWorkUpdates_DraftRagStatusLookupId' AND object_id = OBJECT_ID(N'dbo.ProjectWeeklyWorkUpdates'))
                CREATE NONCLUSTERED INDEX [IX_ProjectWeeklyWorkUpdates_DraftRagStatusLookupId] ON [dbo].[ProjectWeeklyWorkUpdates]([DraftRagStatusLookupId]);
            IF OBJECT_ID(N'dbo.ProjectWeeklyWorkUpdates', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProjectWeeklyWorkUpdates_UpdatedByUserId' AND object_id = OBJECT_ID(N'dbo.ProjectWeeklyWorkUpdates'))
                CREATE NONCLUSTERED INDEX [IX_ProjectWeeklyWorkUpdates_UpdatedByUserId] ON [dbo].[ProjectWeeklyWorkUpdates]([UpdatedByUserId]);
            """);
        logger.LogInformation("Ensured weekly work reporting tables (if missing).");
    }
    catch (Microsoft.Data.SqlClient.SqlException ex)
    {
        logger.LogWarning(ex,
            "Could not ensure weekly work reporting schema (non-fatal if already exists): {Message}",
            ex.Message);
    }
}

static async Task EnsureCompassNotificationTablesAsync(
    CompassDbContext context,
    ILogger logger)
{
    try
    {
        await context.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID(N'dbo.CompassNotificationSettings', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[CompassNotificationSettings] (
                    [Id] int NOT NULL IDENTITY(1,1),
                    [EventKey] nvarchar(100) NOT NULL,
                    [IsEnabled] bit NOT NULL,
                    [RecipientFlags] int NOT NULL,
                    [UpdatedAtUtc] datetime2 NOT NULL,
                    CONSTRAINT [PK_CompassNotificationSettings] PRIMARY KEY CLUSTERED ([Id])
                );
            END
            IF OBJECT_ID(N'dbo.CompassNotificationSettings', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CompassNotificationSettings_EventKey' AND object_id = OBJECT_ID(N'dbo.CompassNotificationSettings'))
                CREATE UNIQUE INDEX [IX_CompassNotificationSettings_EventKey] ON [dbo].[CompassNotificationSettings]([EventKey]);

            IF OBJECT_ID(N'dbo.CompassNotificationEmailLogs', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[CompassNotificationEmailLogs] (
                    [Id] bigint NOT NULL IDENTITY(1,1),
                    [SentAtUtc] datetime2 NOT NULL,
                    [RecipientEmail] nvarchar(256) NOT NULL,
                    [RecipientName] nvarchar(256) NULL,
                    [EventKey] nvarchar(100) NOT NULL,
                    [Subject] nvarchar(500) NOT NULL,
                    [Body] nvarchar(max) NOT NULL,
                    [ContextReference] nvarchar(200) NULL,
                    [SendSucceeded] bit NOT NULL,
                    [ErrorMessage] nvarchar(2000) NULL,
                    CONSTRAINT [PK_CompassNotificationEmailLogs] PRIMARY KEY CLUSTERED ([Id])
                );
            END
            IF OBJECT_ID(N'dbo.CompassNotificationEmailLogs', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CompassNotificationEmailLogs_EventKey' AND object_id = OBJECT_ID(N'dbo.CompassNotificationEmailLogs'))
                CREATE NONCLUSTERED INDEX [IX_CompassNotificationEmailLogs_EventKey] ON [dbo].[CompassNotificationEmailLogs]([EventKey]);
            IF OBJECT_ID(N'dbo.CompassNotificationEmailLogs', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CompassNotificationEmailLogs_SentAtUtc' AND object_id = OBJECT_ID(N'dbo.CompassNotificationEmailLogs'))
                CREATE NONCLUSTERED INDEX [IX_CompassNotificationEmailLogs_SentAtUtc] ON [dbo].[CompassNotificationEmailLogs]([SentAtUtc]);
            """);
        logger.LogInformation("Ensured CompassNotificationSettings / CompassNotificationEmailLogs tables (if missing).");
    }
    catch (Microsoft.Data.SqlClient.SqlException ex)
    {
        logger.LogWarning(ex,
            "Could not ensure Compass notification tables (non-fatal if already exist): {Message}",
            ex.Message);
    }
}

static async Task EnsureRaidLikelihoodImpactMatrixScoreColumnsAsync(
    CompassDbContext context,
    ILogger logger)
{
    try
    {
        await context.Database.ExecuteSqlRawAsync("""
            IF COL_LENGTH(N'dbo.RiskLikelihoods', N'MatrixScore') IS NULL
                ALTER TABLE [dbo].[RiskLikelihoods] ADD [MatrixScore] int NOT NULL CONSTRAINT DF_RiskLikelihoods_MatrixScore DEFAULT (3);
            IF COL_LENGTH(N'dbo.RiskImpactLevels', N'MatrixScore') IS NULL
                ALTER TABLE [dbo].[RiskImpactLevels] ADD [MatrixScore] int NOT NULL CONSTRAINT DF_RiskImpactLevels_MatrixScore DEFAULT (3);
            """);
        logger.LogInformation("Ensured MatrixScore columns on RiskLikelihoods / RiskImpactLevels (if missing).");
    }
    catch (Microsoft.Data.SqlClient.SqlException ex)
    {
        logger.LogWarning(ex,
            "Could not ensure MatrixScore columns on RAID lookup tables (non-fatal if columns already exist): {Message}",
            ex.Message);
    }
}

/// <summary>
/// Ensures <c>IsEnterpriseService</c> on <c>CMDBProducts</c> when the migration row exists but the column does not (restored DB, etc.).
/// Without this, EF queries against <see cref="Compass.Models.Fips.CMDBProduct"/> fail (invalid column) and service register lists break.
/// Uses <c>sys.columns</c> — <c>COL_LENGTH('dbo.Table', ...)</c> is unreliable for schema-qualified names on some servers.
/// </summary>
static async Task EnsureCMDBProductIsEnterpriseServiceColumnAsync(
    CompassDbContext context,
    ILogger logger)
{
    var provider = context.Database.ProviderName ?? "";
    if (!provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogInformation(
            "Skipping CMDBProducts.IsEnterpriseService DDL repair for provider {Provider}; rely on EF migrations for this database.",
            provider);
        return;
    }

    try
    {
        await context.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID(N'dbo.CMDBProducts', N'U') IS NOT NULL
               AND NOT EXISTS (
                   SELECT 1
                   FROM sys.columns c
                   WHERE c.object_id = OBJECT_ID(N'dbo.CMDBProducts', N'U')
                     AND c.name = N'IsEnterpriseService')
            BEGIN
                ALTER TABLE [dbo].[CMDBProducts] ADD [IsEnterpriseService] bit NOT NULL
                    CONSTRAINT [DF_CMDBProducts_IsEnterpriseService] DEFAULT (0);
            END
            """);
        logger.LogInformation("Ensured CMDBProducts.IsEnterpriseService column exists (if it was missing).");
    }
    catch (Exception ex)
    {
        logger.LogError(ex,
            "Could not add CMDBProducts.IsEnterpriseService. Apply EF migration {Migration} or add the column manually; CMDB product queries will fail until then.",
            "20260429151249_AddCMDBProductIsEnterpriseService");
    }
}

/// <summary>
/// Ensures <c>AccessMode</c> on <c>Features</c> and the <c>FeatureUserAllows</c> table when
/// the AddFeatureAccessModeAndUserAllow migration is in history but objects are missing (e.g. restored DB).
/// </summary>
static async Task EnsureFeatureAccessModeAndUserAllowAsync(CompassDbContext context, ILogger logger)
{
    // SQL Server: a column added with ALTER in a batch is not visible to other statements in the
    // same batch (parse/compile), so add and backfill must be separate round-trips.
    try
    {
        await context.Database.ExecuteSqlRawAsync("""
            IF COL_LENGTH(N'dbo.Features', N'AccessMode') IS NULL
                ALTER TABLE [dbo].[Features] ADD [AccessMode] int NOT NULL CONSTRAINT [DF_Features_AccessMode] DEFAULT (1);
            """);
        await context.Database.ExecuteSqlRawAsync("""
            IF COL_LENGTH(N'dbo.Features', N'AccessMode') IS NOT NULL
                UPDATE [dbo].[Features] SET [AccessMode] = CASE WHEN [IsActive] = 1 THEN 1 ELSE 0 END;
            """);
        await context.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID(N'dbo.FeatureUserAllows', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[FeatureUserAllows] (
                    [Id] int NOT NULL IDENTITY(1,1),
                    [FeatureId] int NOT NULL,
                    [UserId] int NOT NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    CONSTRAINT [PK_FeatureUserAllows] PRIMARY KEY CLUSTERED ([Id]),
                    CONSTRAINT [FK_FeatureUserAllows_Features_FeatureId] FOREIGN KEY ([FeatureId]) REFERENCES [dbo].[Features] ([Id]) ON DELETE CASCADE,
                    CONSTRAINT [FK_FeatureUserAllows_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([Id]) ON DELETE CASCADE
                );
                CREATE UNIQUE NONCLUSTERED INDEX [IX_FeatureUserAllows_FeatureId_UserId]
                    ON [dbo].[FeatureUserAllows] ([FeatureId], [UserId]);
                CREATE NONCLUSTERED INDEX [IX_FeatureUserAllows_UserId]
                    ON [dbo].[FeatureUserAllows] ([UserId]);
            END
            """);
        await context.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID(N'dbo.FeatureGroupAllows', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[FeatureGroupAllows] (
                    [Id] int NOT NULL IDENTITY(1,1),
                    [FeatureId] int NOT NULL,
                    [GroupId] int NOT NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    CONSTRAINT [PK_FeatureGroupAllows] PRIMARY KEY CLUSTERED ([Id]),
                    CONSTRAINT [FK_FeatureGroupAllows_Features_FeatureId] FOREIGN KEY ([FeatureId]) REFERENCES [dbo].[Features] ([Id]) ON DELETE CASCADE,
                    CONSTRAINT [FK_FeatureGroupAllows_Groups_GroupId] FOREIGN KEY ([GroupId]) REFERENCES [dbo].[Groups] ([Id]) ON DELETE CASCADE
                );
                CREATE UNIQUE NONCLUSTERED INDEX [IX_FeatureGroupAllows_FeatureId_GroupId]
                    ON [dbo].[FeatureGroupAllows] ([FeatureId], [GroupId]);
                CREATE NONCLUSTERED INDEX [IX_FeatureGroupAllows_GroupId]
                    ON [dbo].[FeatureGroupAllows] ([GroupId]);
            END
            """);
        logger.LogInformation("Ensured Features.AccessMode, FeatureUserAllows, and FeatureGroupAllows (if missing).");
    }
    catch (Microsoft.Data.SqlClient.SqlException ex)
    {
        logger.LogWarning(ex,
            "Could not ensure Features.AccessMode / FeatureUserAllows (non-fatal if already in sync): {Message}",
            ex.Message);
    }
}

static async Task SeedRbacInitialDataAsync(Compass.Data.CompassDbContext context, string? superAdminEmail)
{
    const string superAdminGroupName = "Super admin";
    const string centralOpsAdminGroupName = "Central Operations Admin";

    // Check if initial seeding has been done (check for Super admin group)
    var superAdminGroup = await context.Groups
        .FirstOrDefaultAsync(g => g.Name == superAdminGroupName);

    var needsInitialSeeding = superAdminGroup == null;

    if (needsInitialSeeding)
    {
        Console.WriteLine("Seeding RBAC initial data...");
    }
    else
    {
        Console.WriteLine("Checking for additional RBAC groups...");
    }

    Compass.Models.User? superAdmin = null;
    if (!string.IsNullOrWhiteSpace(superAdminEmail))
    {
        superAdmin = await context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == superAdminEmail.ToLower());

        if (superAdmin == null)
        {
            superAdmin = new Compass.Models.User
            {
                Email = superAdminEmail.Trim(),
                Name = "Super Admin",
                Role = Compass.Models.UserRole.Visitor,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            context.Users.Add(superAdmin);
            await context.SaveChangesAsync();
            Console.WriteLine($"✓ Created super admin user");
        }
    }
    else
    {
        Console.WriteLine("Skipping super admin user creation: Authentication:SuperAdminEmail not configured.");
    }

    // Create Super admin group (if it doesn't exist)
    if (superAdminGroup == null)
    {
        superAdminGroup = new Compass.Models.Group
        {
            Name = superAdminGroupName,
            Description = "Super administrator group with full system access including API management",
            IsActive = true,
            IsSystemGroup = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedBy = "System",
            UpdatedBy = "System"
        };
        context.Groups.Add(superAdminGroup);
        await context.SaveChangesAsync();
        Console.WriteLine($"✓ Created group: {superAdminGroupName}");
    }

    // Assign super admin user to Super admin group (only if super admin email is configured)
    if (superAdmin != null)
    {
        var superAdminUserGroup = await context.UserGroups
            .FirstOrDefaultAsync(ug => ug.UserId == superAdmin.Id && ug.GroupId == superAdminGroup.Id);

        if (superAdminUserGroup == null)
        {
            superAdminUserGroup = new Compass.Models.UserGroup
            {
                UserId = superAdmin.Id,
                GroupId = superAdminGroup.Id,
                AssignedAt = DateTime.UtcNow,
                AssignedBy = "System"
            };
            context.UserGroups.Add(superAdminUserGroup);
            await context.SaveChangesAsync();
            Console.WriteLine($"✓ Assigned super admin to {superAdminGroupName} group");
        }
    }

    // Create Central Operations Admin group (if it doesn't exist)
    var centralOpsGroup = await context.Groups
        .FirstOrDefaultAsync(g => g.Name == centralOpsAdminGroupName);

    if (centralOpsGroup == null)
    {
        centralOpsGroup = new Compass.Models.Group
        {
            Name = centralOpsAdminGroupName,
            Description = "Default administrative group with full access to all features",
            IsActive = true,
            IsSystemGroup = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedBy = "System",
            UpdatedBy = "System"
        };
        context.Groups.Add(centralOpsGroup);
        await context.SaveChangesAsync();
        Console.WriteLine($"✓ Created group: {centralOpsAdminGroupName}");
    }

    // Create default features if they don't exist
    var defaultFeatures = new[]
    {
        new { Code = "delivery_reporting", Name = "Delivery Reporting", Description = "Delivery reporting functionality" },
        new { Code = "risks", Name = "Risks", Description = "Risk management functionality" },
        new { Code = "issues", Name = "Issues", Description = "Issue management functionality" },
        new { Code = "actions", Name = "Actions", Description = "Action management functionality" },
        new { Code = "objectives", Name = "Objectives", Description = "Objective management functionality" },
        new { Code = "projects", Name = "Projects", Description = "Project management functionality" },
        new { Code = "accessibility", Name = "Accessibility", Description = "Accessibility management functionality" },
        new { Code = "surveys", Name = "Surveys", Description = "Survey management functionality" },
        new { Code = "enterprise_reporting", Name = "Enterprise Reporting", Description = "Enterprise reporting functionality" },
        new { Code = "group_management", Name = "Group Management", Description = "Group and permission management functionality" }
    };

    foreach (var featureData in defaultFeatures)
    {
        var feature = await context.Features
            .FirstOrDefaultAsync(f => f.Code == featureData.Code);

        if (feature == null)
        {
            feature = new Compass.Models.Feature
            {
                Name = featureData.Name,
                Code = featureData.Code,
                Description = featureData.Description,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            context.Features.Add(feature);
            await context.SaveChangesAsync();
            Console.WriteLine($"✓ Created feature: {featureData.Name}");
        }

        // Grant all permissions to Central Operations Admin group for this feature
        foreach (Compass.Models.PermissionType permission in Enum.GetValues<Compass.Models.PermissionType>())
        {
            // Check if permission already exists
            var exists = await context.GroupFeaturePermissions
                .AnyAsync(gfp => gfp.GroupId == centralOpsGroup.Id &&
                                gfp.FeatureId == feature.Id &&
                                gfp.Permission == permission);

            if (!exists)
            {
                var groupFeaturePermission = new Compass.Models.GroupFeaturePermission
                {
                    GroupId = centralOpsGroup.Id,
                    FeatureId = feature.Id,
                    Permission = permission,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = "System"
                };
                context.GroupFeaturePermissions.Add(groupFeaturePermission);
            }
        }
    }

    await context.SaveChangesAsync();

    // Assign super admin to Central Operations Admin group (if not already assigned)
    if (superAdmin != null)
    {
        var existingUserGroup = await context.UserGroups
            .FirstOrDefaultAsync(ug => ug.UserId == superAdmin.Id && ug.GroupId == centralOpsGroup.Id);

        if (existingUserGroup == null)
        {
            var userGroup = new Compass.Models.UserGroup
            {
                UserId = superAdmin.Id,
                GroupId = centralOpsGroup.Id,
                AssignedAt = DateTime.UtcNow,
                AssignedBy = "System"
            };
            context.UserGroups.Add(userGroup);
            await context.SaveChangesAsync();
            Console.WriteLine($"✓ Assigned super admin to {centralOpsAdminGroupName} group");
        }
    }

    // Create Standards feature
    var standardsFeature = await context.Features
        .FirstOrDefaultAsync(f => f.Code == "ddt_standards");

    if (standardsFeature == null)
    {
        standardsFeature = new Compass.Models.Feature
        {
            Name = "DDT Standards",
            Code = "ddt_standards",
            Description = "DDT Standards management functionality",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Features.Add(standardsFeature);
        await context.SaveChangesAsync();
        Console.WriteLine($"✓ Created feature: DDT Standards");
    }

    // Grant all permissions to Central Operations Admin group for standards feature
    foreach (Compass.Models.PermissionType permission in Enum.GetValues<Compass.Models.PermissionType>())
    {
        var exists = await context.GroupFeaturePermissions
            .AnyAsync(gfp => gfp.GroupId == centralOpsGroup.Id &&
                            gfp.FeatureId == standardsFeature.Id &&
                            gfp.Permission == permission);

        if (!exists)
        {
            var groupFeaturePermission = new Compass.Models.GroupFeaturePermission
            {
                GroupId = centralOpsGroup.Id,
                FeatureId = standardsFeature.Id,
                Permission = permission,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "System"
            };
            context.GroupFeaturePermissions.Add(groupFeaturePermission);
        }
    }

    // Demand product feature — global on/off from Admin → System → Feature settings
    var demandFeature = await context.Features
        .FirstOrDefaultAsync(f => f.Code == Compass.Models.FeatureCodes.Demand);

    if (demandFeature == null)
    {
        demandFeature = new Compass.Models.Feature
        {
            Name = "Demand",
            Code = Compass.Models.FeatureCodes.Demand,
            Description = "Demand pipeline, triage, and related product areas",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Features.Add(demandFeature);
        await context.SaveChangesAsync();
        Console.WriteLine("✓ Created feature: Demand");
    }

    foreach (Compass.Models.PermissionType permission in Enum.GetValues<Compass.Models.PermissionType>())
    {
        var exists = await context.GroupFeaturePermissions
            .AnyAsync(gfp => gfp.GroupId == centralOpsGroup.Id &&
                            gfp.FeatureId == demandFeature.Id &&
                            gfp.Permission == permission);

        if (!exists)
        {
            context.GroupFeaturePermissions.Add(new Compass.Models.GroupFeaturePermission
            {
                GroupId = centralOpsGroup.Id,
                FeatureId = demandFeature.Id,
                Permission = permission,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "System"
            });
        }
    }

    // Standards modern UI — global on/off from Admin → System → Feature settings
    var standardsToggleFeature = await context.Features
        .FirstOrDefaultAsync(f => f.Code == Compass.Models.FeatureCodes.Standards);

    if (standardsToggleFeature == null)
    {
        standardsToggleFeature = new Compass.Models.Feature
        {
            Name = "Standards",
            Code = Compass.Models.FeatureCodes.Standards,
            Description = "Modern Standards area (/modern/standards), DDT Standards, Functional Standards",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Features.Add(standardsToggleFeature);
        await context.SaveChangesAsync();
        Console.WriteLine("✓ Created feature: Standards (global toggle)");
    }

    // FIPS service register — global on/off from Admin → Feature settings (database CMDB vs CMS products)
    var fipsToggleFeature = await context.Features
        .FirstOrDefaultAsync(f => f.Code == Compass.Models.FeatureCodes.Fips);

    if (fipsToggleFeature == null)
    {
        fipsToggleFeature = new Compass.Models.Feature
        {
            Name = "FIPS service register",
            Code = Compass.Models.FeatureCodes.Fips,
            Description = "Service register / CMDB-synced products in Compass vs CMS products",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Features.Add(fipsToggleFeature);
        await context.SaveChangesAsync();
        Console.WriteLine("✓ Created feature: FIPS service register (global toggle)");
    }

    foreach (Compass.Models.PermissionType permission in Enum.GetValues<Compass.Models.PermissionType>())
    {
        var exists = await context.GroupFeaturePermissions
            .AnyAsync(gfp => gfp.GroupId == centralOpsGroup.Id &&
                            gfp.FeatureId == fipsToggleFeature.Id &&
                            gfp.Permission == permission);

        if (!exists)
        {
            context.GroupFeaturePermissions.Add(new Compass.Models.GroupFeaturePermission
            {
                GroupId = centralOpsGroup.Id,
                FeatureId = fipsToggleFeature.Id,
                Permission = permission,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "System"
            });
        }
    }

    // Create Standards role groups
    var standardOwnerGroupName = "Standard Owners";
    var standardApproverGroupName = "Standard Approvers";
    var standardPublisherGroupName = "Standard Publishers";

    // Standard Owners - can draft and submit standards
    var standardOwnerGroup = await context.Groups
        .FirstOrDefaultAsync(g => g.Name == standardOwnerGroupName);

    if (standardOwnerGroup == null)
    {
        standardOwnerGroup = new Compass.Models.Group
        {
            Name = standardOwnerGroupName,
            Description = "Users who can draft and submit standards for review",
            IsActive = true,
            IsSystemGroup = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedBy = "System",
            UpdatedBy = "System"
        };
        context.Groups.Add(standardOwnerGroup);
        await context.SaveChangesAsync();
        Console.WriteLine($"✓ Created group: {standardOwnerGroupName}");

        // Grant Create and View permissions to Standard Owners
        var ownerPermissions = new[] { Compass.Models.PermissionType.View, Compass.Models.PermissionType.Create };
        foreach (var permission in ownerPermissions)
        {
            var groupFeaturePermission = new Compass.Models.GroupFeaturePermission
            {
                GroupId = standardOwnerGroup.Id,
                FeatureId = standardsFeature.Id,
                Permission = permission,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "System"
            };
            context.GroupFeaturePermissions.Add(groupFeaturePermission);
        }
        await context.SaveChangesAsync();
        Console.WriteLine($"✓ Granted permissions to {standardOwnerGroupName}");
    }

    // Standard Approvers - can approve/reject standards
    var standardApproverGroup = await context.Groups
        .FirstOrDefaultAsync(g => g.Name == standardApproverGroupName);

    if (standardApproverGroup == null)
    {
        standardApproverGroup = new Compass.Models.Group
        {
            Name = standardApproverGroupName,
            Description = "Users who can approve or reject standards submitted for review",
            IsActive = true,
            IsSystemGroup = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedBy = "System",
            UpdatedBy = "System"
        };
        context.Groups.Add(standardApproverGroup);
        await context.SaveChangesAsync();
        Console.WriteLine($"✓ Created group: {standardApproverGroupName}");

        // Grant View and Update permissions to Standard Approvers
        var approverPermissions = new[] { Compass.Models.PermissionType.View, Compass.Models.PermissionType.Update };
        foreach (var permission in approverPermissions)
        {
            var groupFeaturePermission = new Compass.Models.GroupFeaturePermission
            {
                GroupId = standardApproverGroup.Id,
                FeatureId = standardsFeature.Id,
                Permission = permission,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "System"
            };
            context.GroupFeaturePermissions.Add(groupFeaturePermission);
        }
        await context.SaveChangesAsync();
        Console.WriteLine($"✓ Granted permissions to {standardApproverGroupName}");
    }

    // Standard Publishers - can publish approved standards
    var standardPublisherGroup = await context.Groups
        .FirstOrDefaultAsync(g => g.Name == standardPublisherGroupName);

    if (standardPublisherGroup == null)
    {
        standardPublisherGroup = new Compass.Models.Group
        {
            Name = standardPublisherGroupName,
            Description = "Users who can publish approved standards",
            IsActive = true,
            IsSystemGroup = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedBy = "System",
            UpdatedBy = "System"
        };
        context.Groups.Add(standardPublisherGroup);
        await context.SaveChangesAsync();
        Console.WriteLine($"✓ Created group: {standardPublisherGroupName}");

        // Grant View and Update permissions to Standard Publishers
        var publisherPermissions = new[] { Compass.Models.PermissionType.View, Compass.Models.PermissionType.Update };
        foreach (var permission in publisherPermissions)
        {
            var groupFeaturePermission = new Compass.Models.GroupFeaturePermission
            {
                GroupId = standardPublisherGroup.Id,
                FeatureId = standardsFeature.Id,
                Permission = permission,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "System"
            };
            context.GroupFeaturePermissions.Add(groupFeaturePermission);
        }
        await context.SaveChangesAsync();
        Console.WriteLine($"✓ Granted permissions to {standardPublisherGroupName}");
    }

    await context.SaveChangesAsync();
    Console.WriteLine("✓ RBAC initial data seeding completed");
}

static async Task SeedStatementTemplatesAsync(Compass.Data.CompassDbContext context)
{
    // Check if templates already exist
    if (await context.StatementTemplates.AnyAsync())
    {
        return; // Templates already seeded
    }

    // Get the markdown template files
    var docsPath = Path.Combine(Directory.GetCurrentDirectory(), "docs");
    var compliantPath = Path.Combine(docsPath, "statement.md");
    var nonCompliantPath = Path.Combine(docsPath, "non-compliant.md");

    // Seed compliant template
    if (File.Exists(compliantPath))
    {
        var compliantContent = await File.ReadAllTextAsync(compliantPath);
        var compliantTemplate = new Compass.Models.StatementTemplate
        {
            Name = "Compliant",
            Version = 1,
            Content = compliantContent,
            Description = "Accessibility statement template for fully compliant products",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "System",
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = "System"
        };
        context.StatementTemplates.Add(compliantTemplate);
    }

    // Seed non-compliant template (used for both partially-compliant and non-compliant)
    if (File.Exists(nonCompliantPath))
    {
        var nonCompliantContent = await File.ReadAllTextAsync(nonCompliantPath);
        var nonCompliantTemplate = new Compass.Models.StatementTemplate
        {
            Name = "Non-compliant",
            Version = 1,
            Content = nonCompliantContent,
            Description = "Accessibility statement template for partially-compliant and non-compliant products",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "System",
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = "System"
        };
        context.StatementTemplates.Add(nonCompliantTemplate);
    }

    if (context.ChangeTracker.HasChanges())
    {
        await context.SaveChangesAsync();
        Console.WriteLine("✓ Statement templates seeded successfully");
    }
}

static async Task SeedServiceStandardsAsync(Compass.Data.CompassDbContext context)
{
    // Check if Service Standards already exist
    if (await context.ServiceStandards.AnyAsync())
    {
        Console.WriteLine("Service Standards already seeded, skipping...");
        return;
    }

    Console.WriteLine("Seeding GOV.UK Service Standards...");

    var standards = new[]
    {
        new Compass.Models.ServiceStandard { StandardNumber = 1, Title = "Understand users and their needs", Slug = "understand-users-needs", Summary = "Research to develop a deep knowledge of who the users are and what that means for the design of the service.", DisplayOrder = 1, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.ServiceStandard { StandardNumber = 2, Title = "Solve a whole problem for users", Slug = "solve-whole-problem", Summary = "Work towards creating a service that solves one whole problem for users, working across organisational boundaries.", DisplayOrder = 2, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.ServiceStandard { StandardNumber = 3, Title = "Provide a joined up experience across all channels", Slug = "joined-up-experience", Summary = "Provide a consistent experience wherever users encounter your service, including online, phone, paper and face to face.", DisplayOrder = 3, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.ServiceStandard { StandardNumber = 4, Title = "Make the service simple to use", Slug = "make-service-simple", Summary = "Build a service that's simple, intuitive and comprehensible. And test it with users to make sure it works for them.", DisplayOrder = 4, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.ServiceStandard { StandardNumber = 5, Title = "Make sure everyone can use the service", Slug = "everyone-can-use", Summary = "Build a service that's accessible to all users, including those who use assistive technologies.", DisplayOrder = 5, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.ServiceStandard { StandardNumber = 6, Title = "Have a multidisciplinary team", Slug = "multidisciplinary-team", Summary = "Put in place a sustainable multidisciplinary team that can design, build and operate the service.", DisplayOrder = 6, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.ServiceStandard { StandardNumber = 7, Title = "Use agile ways of working", Slug = "agile-ways-working", Summary = "Use agile, iterative and user-centred methods. Keep improving the service based on user research and feedback.", DisplayOrder = 7, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.ServiceStandard { StandardNumber = 8, Title = "Iterate and improve frequently", Slug = "iterate-improve", Summary = "Make sure you have the capacity, resources and technical flexibility to iterate and improve the service frequently.", DisplayOrder = 8, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.ServiceStandard { StandardNumber = 9, Title = "Create a secure service which protects users' privacy", Slug = "secure-service", Summary = "Evaluate what data the service will be collecting, storing and providing. Assess the privacy implications and put appropriate controls in place.", DisplayOrder = 9, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.ServiceStandard { StandardNumber = 10, Title = "Define what success looks like and publish performance data", Slug = "define-success", Summary = "Identify performance indicators for the service, collect performance data and publish it so users can see how the service is performing.", DisplayOrder = 10, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.ServiceStandard { StandardNumber = 11, Title = "Choose the right tools and technology", Slug = "right-tools-technology", Summary = "Choose tools and technology that let you create a high quality service in a cost effective way. Minimise the cost of changing direction in future.", DisplayOrder = 11, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.ServiceStandard { StandardNumber = 12, Title = "Make new source code open", Slug = "open-source-code", Summary = "Make all new source code open and reusable, and publish it under appropriate licences.", DisplayOrder = 12, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.ServiceStandard { StandardNumber = 13, Title = "Use and contribute to open standards, common components and patterns", Slug = "open-standards", Summary = "Use open standards and common government platforms where available. When this isn't possible, create new ones and make them open.", DisplayOrder = 13, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.ServiceStandard { StandardNumber = 14, Title = "Operate a reliable service", Slug = "reliable-service", Summary = "Make sure your service can be operated reliably and can be continuously improved based on user needs.", DisplayOrder = 14, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
    };

    context.ServiceStandards.AddRange(standards);
    await context.SaveChangesAsync();
    Console.WriteLine($"✓ Seeded {standards.Length} Service Standards successfully");
}

static async Task SeedDdatProfessionsAsync(Compass.Data.CompassDbContext context)
{
    // Check if DDaT Professions already exist
    if (await context.DdatProfessions.AnyAsync())
    {
        Console.WriteLine("DDaT Professions already seeded, skipping...");
        return;
    }

    Console.WriteLine("Seeding DDaT Professions from Government Digital and Data Profession Capability Framework...");

    var professions = new[]
    {
        // Architecture roles
        new Compass.Models.DdatProfession { Name = "Business architect", Slug = "business-architect", RoleGroup = "Architecture roles", Description = "Business architects design and guide business architecture to align technology with business strategy.", DisplayOrder = 10, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Data architect", Slug = "data-architect", RoleGroup = "Architecture roles", Description = "Data architects design and guide data architecture to ensure data is managed effectively across the organisation.", DisplayOrder = 20, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Enterprise architect", Slug = "enterprise-architect", RoleGroup = "Architecture roles", Description = "Enterprise architects design and guide enterprise architecture to align technology with business strategy across the organisation.", DisplayOrder = 30, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Network architect", Slug = "network-architect", RoleGroup = "Architecture roles", Description = "Network architects design and guide network architecture to ensure reliable and secure network infrastructure.", DisplayOrder = 40, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Security architect", Slug = "security-architect", RoleGroup = "Architecture roles", Description = "Security architects design and guide security architecture to protect systems and data.", DisplayOrder = 50, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Solution architect", Slug = "solution-architect", RoleGroup = "Architecture roles", Description = "Solution architects design and guide solution architecture to meet specific business needs.", DisplayOrder = 60, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Technical architect", Slug = "technical-architect", RoleGroup = "Architecture roles", Description = "Technical architects design and guide technical architecture to ensure systems are built using appropriate technologies.", DisplayOrder = 70, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        
        // Chief digital and data roles
        new Compass.Models.DdatProfession { Name = "Chief data officer", Slug = "chief-data-officer", RoleGroup = "Chief digital and data roles", Description = "Chief data officers lead data strategy and governance across the organisation.", DisplayOrder = 100, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Chief digital and information officer", Slug = "chief-digital-information-officer", RoleGroup = "Chief digital and data roles", Description = "Chief digital and information officers lead digital transformation and information management strategy.", DisplayOrder = 110, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Chief information security officer", Slug = "chief-information-security-officer", RoleGroup = "Chief digital and data roles", Description = "Chief information security officers lead information security strategy and governance.", DisplayOrder = 120, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Chief technology officer", Slug = "chief-technology-officer", RoleGroup = "Chief digital and data roles", Description = "Chief technology officers lead technology strategy and innovation across the organisation.", DisplayOrder = 130, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        
        // Data roles
        new Compass.Models.DdatProfession { Name = "Analytics engineer", Slug = "analytics-engineer", RoleGroup = "Data roles", Description = "Analytics engineers build and maintain data analytics infrastructure and pipelines.", DisplayOrder = 200, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Data analyst", Slug = "data-analyst", RoleGroup = "Data roles", Description = "Data analysts analyse data to provide insights and support decision-making.", DisplayOrder = 210, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Data engineer", Slug = "data-engineer", RoleGroup = "Data roles", Description = "Data engineers design and build systems for collecting, storing and processing data.", DisplayOrder = 220, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Data ethicist", Slug = "data-ethicist", RoleGroup = "Data roles", Description = "Data ethicists ensure ethical use of data and algorithms in government services.", DisplayOrder = 230, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Data governance manager", Slug = "data-governance-manager", RoleGroup = "Data roles", Description = "Data governance managers establish and maintain data governance frameworks and policies.", DisplayOrder = 240, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Data scientist", Slug = "data-scientist", RoleGroup = "Data roles", Description = "Data scientists use advanced analytics and machine learning to extract insights from data.", DisplayOrder = 250, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Digital evaluator", Slug = "digital-evaluator", RoleGroup = "Data roles", Description = "Digital evaluators assess the impact and effectiveness of digital services and products.", DisplayOrder = 260, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Machine learning engineer", Slug = "machine-learning-engineer", RoleGroup = "Data roles", Description = "Machine learning engineers design, build and deploy machine learning models and systems.", DisplayOrder = 270, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Performance analyst", Slug = "performance-analyst", RoleGroup = "Data roles", Description = "Performance analysts measure and analyse service performance to drive improvements.", DisplayOrder = 280, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        
        // IT operations roles
        new Compass.Models.DdatProfession { Name = "Application operations engineer", Slug = "application-operations-engineer", RoleGroup = "IT operations roles", Description = "Application operations engineers maintain and support applications in production environments.", DisplayOrder = 300, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Business relationship manager", Slug = "business-relationship-manager", RoleGroup = "IT operations roles", Description = "Business relationship managers build relationships between IT and business stakeholders.", DisplayOrder = 310, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Change and release manager", Slug = "change-release-manager", RoleGroup = "IT operations roles", Description = "Change and release managers coordinate changes to IT systems and manage releases.", DisplayOrder = 320, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Command and control centre manager", Slug = "command-control-centre-manager", RoleGroup = "IT operations roles", Description = "Command and control centre managers oversee IT operations centres and incident response.", DisplayOrder = 330, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "End user computing engineer", Slug = "end-user-computing-engineer", RoleGroup = "IT operations roles", Description = "End user computing engineers support and maintain end user devices and software.", DisplayOrder = 340, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Incident manager", Slug = "incident-manager", RoleGroup = "IT operations roles", Description = "Incident managers coordinate response to IT incidents and service disruptions.", DisplayOrder = 350, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Infrastructure engineer", Slug = "infrastructure-engineer", RoleGroup = "IT operations roles", Description = "Infrastructure engineers design, build and maintain IT infrastructure.", DisplayOrder = 360, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Infrastructure operations engineer", Slug = "infrastructure-operations-engineer", RoleGroup = "IT operations roles", Description = "Infrastructure operations engineers operate and maintain IT infrastructure systems.", DisplayOrder = 370, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "IT service manager", Slug = "it-service-manager", RoleGroup = "IT operations roles", Description = "IT service managers ensure IT services meet business needs and service level agreements.", DisplayOrder = 380, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Problem manager", Slug = "problem-manager", RoleGroup = "IT operations roles", Description = "Problem managers identify and resolve root causes of recurring IT incidents.", DisplayOrder = 390, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Service desk manager", Slug = "service-desk-manager", RoleGroup = "IT operations roles", Description = "Service desk managers oversee IT service desk operations and support teams.", DisplayOrder = 400, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Service transition manager", Slug = "service-transition-manager", RoleGroup = "IT operations roles", Description = "Service transition managers coordinate the transition of services into live operations.", DisplayOrder = 410, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        
        // Product and delivery roles
        new Compass.Models.DdatProfession { Name = "Business analyst", Slug = "business-analyst", RoleGroup = "Product and delivery roles", Description = "Business analysts help organisations improve their processes, products, services and software.", DisplayOrder = 500, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Delivery manager", Slug = "delivery-manager", RoleGroup = "Product and delivery roles", Description = "Delivery managers are responsible for ensuring teams deliver products and services successfully.", DisplayOrder = 510, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Digital portfolio manager", Slug = "digital-portfolio-manager", RoleGroup = "Product and delivery roles", Description = "Digital portfolio managers oversee portfolios of digital products and services.", DisplayOrder = 520, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Product manager", Slug = "product-manager", RoleGroup = "Product and delivery roles", Description = "Product managers are responsible for the success of a product or service.", DisplayOrder = 530, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Programme delivery manager", Slug = "programme-delivery-manager", RoleGroup = "Product and delivery roles", Description = "Programme delivery managers coordinate delivery of complex programmes across multiple teams.", DisplayOrder = 540, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Service owner", Slug = "service-owner", RoleGroup = "Product and delivery roles", Description = "Service owners are accountable for the end-to-end delivery and operation of services.", DisplayOrder = 550, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        
        // Quality assurance testing (QAT) roles
        new Compass.Models.DdatProfession { Name = "Quality assurance test analyst", Slug = "quality-assurance-test-analyst", RoleGroup = "Quality assurance testing (QAT) roles", Description = "Quality assurance test analysts test software to ensure it meets quality standards.", DisplayOrder = 600, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Test engineer", Slug = "test-engineer", RoleGroup = "Quality assurance testing (QAT) roles", Description = "Test engineers design and implement automated testing solutions.", DisplayOrder = 610, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Test manager", Slug = "test-manager", RoleGroup = "Quality assurance testing (QAT) roles", Description = "Test managers lead testing teams and ensure quality assurance processes are effective.", DisplayOrder = 620, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        
        // Software development roles
        new Compass.Models.DdatProfession { Name = "Development operations (DevOps) engineer", Slug = "devops-engineer", RoleGroup = "Software development roles", Description = "DevOps engineers bridge development and operations to enable continuous delivery.", DisplayOrder = 700, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Frontend developer", Slug = "frontend-developer", RoleGroup = "Software development roles", Description = "Frontend developers build user-facing interfaces for web applications.", DisplayOrder = 710, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Software developer", Slug = "software-developer", RoleGroup = "Software development roles", Description = "Software developers design, build, test and maintain software systems.", DisplayOrder = 720, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        
        // User-centred design roles
        new Compass.Models.DdatProfession { Name = "Accessibility specialist", Slug = "accessibility-specialist", RoleGroup = "User-centred design roles", Description = "Accessibility specialists ensure services are accessible to all users, including those with disabilities.", DisplayOrder = 800, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Content designer", Slug = "content-designer", RoleGroup = "User-centred design roles", Description = "Content designers make sure users get the information they need in a clear and accessible way.", DisplayOrder = 810, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Content strategist", Slug = "content-strategist", RoleGroup = "User-centred design roles", Description = "Content strategists develop content strategies to meet user and business needs.", DisplayOrder = 820, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Graphic designer", Slug = "graphic-designer", RoleGroup = "User-centred design roles", Description = "Graphic designers create visual designs for digital services and products.", DisplayOrder = 830, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Interaction designer", Slug = "interaction-designer", RoleGroup = "User-centred design roles", Description = "Interaction designers design the way users interact with services.", DisplayOrder = 840, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Service designer", Slug = "service-designer", RoleGroup = "User-centred design roles", Description = "Service designers design end-to-end services that meet user needs and business objectives.", DisplayOrder = 850, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "Technical writer", Slug = "technical-writer", RoleGroup = "User-centred design roles", Description = "Technical writers create clear and accessible technical documentation for users and developers.", DisplayOrder = 860, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.DdatProfession { Name = "User researcher", Slug = "user-researcher", RoleGroup = "User-centred design roles", Description = "User researchers help teams understand users' needs, behaviours, and motivations.", DisplayOrder = 870, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
    };

    context.DdatProfessions.AddRange(professions);
    await context.SaveChangesAsync();
    Console.WriteLine($"✓ Seeded {professions.Length} DDaT Professions successfully");
}

static async Task SeedGradesAsync(Compass.Data.CompassDbContext context)
{
    if (await context.Grades.AnyAsync())
    {
        Console.WriteLine("Grades already exist, skipping seed");
        return;
    }

    var grades = new[]
    {
        new Compass.Models.Grade { Code = "AA", DisplayName = "Administrative Assistant", DisplayOrder = 1, IsActive = true },
        new Compass.Models.Grade { Code = "AO", DisplayName = "Administrative Officer", DisplayOrder = 2, IsActive = true },
        new Compass.Models.Grade { Code = "EO", DisplayName = "Executive Officer", DisplayOrder = 3, IsActive = true },
        new Compass.Models.Grade { Code = "HEO", DisplayName = "Higher Executive Officer", DisplayOrder = 4, IsActive = true },
        new Compass.Models.Grade { Code = "SEO", DisplayName = "Senior Executive Officer", DisplayOrder = 5, IsActive = true },
        new Compass.Models.Grade { Code = "G7", DisplayName = "Grade 7", DisplayOrder = 6, IsActive = true },
        new Compass.Models.Grade { Code = "G6", DisplayName = "Grade 6", DisplayOrder = 7, IsActive = true },
        new Compass.Models.Grade { Code = "G6 HOP", DisplayName = "Grade 6 Head of Profession", DisplayOrder = 8, IsActive = true },
        new Compass.Models.Grade { Code = "SCS1", DisplayName = "Senior Civil Service Pay Band 1", DisplayOrder = 9, IsActive = true },
        new Compass.Models.Grade { Code = "SCS2", DisplayName = "Senior Civil Service Pay Band 2", DisplayOrder = 10, IsActive = true },
        new Compass.Models.Grade { Code = "SCS3", DisplayName = "Senior Civil Service Pay Band 3", DisplayOrder = 11, IsActive = true }
    };

    await context.Grades.AddRangeAsync(grades);
    await context.SaveChangesAsync();
    Console.WriteLine($"✓ Seeded {grades.Length} Grades");
}

static async Task SeedTechnologyCodeOfPracticeAsync(Compass.Data.CompassDbContext context)
{
    // Check if Technology Code of Practice points already exist
    if (await context.TechnologyCodeOfPractice.AnyAsync())
    {
        Console.WriteLine("Technology Code of Practice already seeded, skipping...");
        return;
    }

    Console.WriteLine("Seeding Technology Code of Practice...");

    var points = new[]
    {
        new Compass.Models.TechnologyCodeOfPractice { PointNumber = 1, Title = "Define user needs", Slug = "define-user-needs", Summary = "Understand your users and their needs. Develop knowledge of your users and what that means for your technology project or programme.", GuidanceUrl = "https://www.gov.uk/guidance/the-technology-code-of-practice/define-user-needs", DisplayOrder = 1, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.TechnologyCodeOfPractice { PointNumber = 2, Title = "Make things accessible and inclusive", Slug = "make-things-accessible", Summary = "Make sure your technology, infrastructure and systems are accessible and inclusive for all users.", GuidanceUrl = "https://www.gov.uk/guidance/the-technology-code-of-practice/make-things-accessible", DisplayOrder = 2, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.TechnologyCodeOfPractice { PointNumber = 3, Title = "Be open and use open source", Slug = "be-open-use-open-source", Summary = "Publish your code and use open source software to improve transparency, flexibility and accountability.", GuidanceUrl = "https://www.gov.uk/guidance/the-technology-code-of-practice/be-open-and-use-open-source", DisplayOrder = 3, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.TechnologyCodeOfPractice { PointNumber = 4, Title = "Make use of open standards", Slug = "make-use-of-open-standards", Summary = "Build technology that uses open standards to ensure your technology works and communicates with other technology, and can easily be upgraded and expanded.", GuidanceUrl = "https://www.gov.uk/guidance/the-technology-code-of-practice/make-use-of-open-standards", DisplayOrder = 4, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.TechnologyCodeOfPractice { PointNumber = 5, Title = "Use cloud first", Slug = "use-cloud-first", Summary = "Consider using public cloud solutions first as stated in the Cloud First policy.", GuidanceUrl = "https://www.gov.uk/guidance/the-technology-code-of-practice/use-cloud-first", DisplayOrder = 5, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.TechnologyCodeOfPractice { PointNumber = 6, Title = "Make things secure", Slug = "make-things-secure", Summary = "Keep systems and data safe with the appropriate level of security.", GuidanceUrl = "https://www.gov.uk/guidance/the-technology-code-of-practice/make-things-secure", DisplayOrder = 6, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.TechnologyCodeOfPractice { PointNumber = 7, Title = "Make privacy integral", Slug = "make-privacy-integral", Summary = "Make sure users rights are protected by integrating privacy as an essential part of your system.", GuidanceUrl = "https://www.gov.uk/guidance/the-technology-code-of-practice/make-privacy-integral", DisplayOrder = 7, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.TechnologyCodeOfPractice { PointNumber = 8, Title = "Share, reuse and collaborate", Slug = "share-reuse-collaborate", Summary = "Avoid duplicating effort and unnecessary costs by collaborating across government and sharing and reusing technology, data, and services.", GuidanceUrl = "https://www.gov.uk/guidance/the-technology-code-of-practice/share-reuse-and-collaborate", DisplayOrder = 8, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.TechnologyCodeOfPractice { PointNumber = 9, Title = "Integrate and adapt technology", Slug = "integrate-adapt-technology", Summary = "Your technology should work with existing technologies, processes and infrastructure in your organisation, and adapt to future demands.", GuidanceUrl = "https://www.gov.uk/guidance/the-technology-code-of-practice/integrate-and-adapt-technology", DisplayOrder = 9, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.TechnologyCodeOfPractice { PointNumber = 10, Title = "Make better use of data", Slug = "make-better-use-of-data", Summary = "Use data more effectively by improving your technology, infrastructure and processes.", GuidanceUrl = "https://www.gov.uk/guidance/the-technology-code-of-practice/make-better-use-of-data", DisplayOrder = 10, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.TechnologyCodeOfPractice { PointNumber = 11, Title = "Define your purchasing strategy", Slug = "define-purchasing-strategy", Summary = "Your purchasing strategy must show you've considered commercial and technology aspects, and contractual limitations.", GuidanceUrl = "https://www.gov.uk/guidance/the-technology-code-of-practice/define-your-purchasing-strategy", DisplayOrder = 11, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.TechnologyCodeOfPractice { PointNumber = 12, Title = "Make your technology sustainable", Slug = "make-technology-sustainable", Summary = "Increase sustainability throughout the lifecycle of your technology.", GuidanceUrl = "https://www.gov.uk/guidance/the-technology-code-of-practice/make-your-technology-sustainable", DisplayOrder = 12, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new Compass.Models.TechnologyCodeOfPractice { PointNumber = 13, Title = "Meet the Service Standard", Slug = "meet-service-standard", Summary = "If you're building a service as part of your technology project or programme you will also need to meet the Service Standard.", GuidanceUrl = "https://www.gov.uk/service-manual/service-standard", DisplayOrder = 13, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
    };

    context.TechnologyCodeOfPractice.AddRange(points);
    await context.SaveChangesAsync();
    Console.WriteLine($"✓ Seeded {points.Length} Technology Code of Practice points successfully");
}

static async Task RunDataMigration(WebApplicationBuilder builder)
{
    Console.WriteLine("=== Compass Data Migration Utility ===\n");

    // Build minimal services needed for migration
    builder.Services.AddDbContext<CompassDbContext>(options =>
        options.UseSqlite("Data Source=compass.db"), ServiceLifetime.Transient);

    var sqlConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrEmpty(sqlConnectionString))
    {
        Console.WriteLine("Error: DefaultConnection string not found in configuration.");
        Console.WriteLine("Please ensure appsettings.Production.json has the Azure SQL connection string.");
        return;
    }

    builder.Services.AddDbContext<CompassDbContext>(options =>
        options.UseSqlServer(sqlConnectionString), ServiceLifetime.Transient);

    var app = builder.Build();

    using var scope = app.Services.CreateScope();
    var serviceProvider = scope.ServiceProvider;

    // Create SQLite context (source)
    var sqliteOptions = new DbContextOptionsBuilder<CompassDbContext>()
        .UseSqlite("Data Source=compass.db")
        .Options;
    using var sourceDb = new CompassDbContext(sqliteOptions);

    // Create Azure SQL context (target)
    var sqlServerOptions = new DbContextOptionsBuilder<CompassDbContext>()
        .UseSqlServer(sqlConnectionString)
        .Options;
    using var targetDb = new CompassDbContext(sqlServerOptions);

    // Test connections
    Console.WriteLine("Testing database connections...");
    try
    {
        await sourceDb.Database.CanConnectAsync();
        Console.WriteLine("✓ Connected to SQLite source database");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"✗ Failed to connect to SQLite: {ex.Message}");
        return;
    }

    try
    {
        await targetDb.Database.CanConnectAsync();
        Console.WriteLine("✓ Connected to Azure SQL target database");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"✗ Failed to connect to Azure SQL: {ex.Message}");
        Console.WriteLine("Please verify the connection string and ensure the Azure SQL firewall allows your IP.");
        return;
    }

    Console.WriteLine("\nStarting migration...\n");
    await Compass.DataMigrationUtility.MigrateDataAsync(sourceDb, targetDb);
}

static string? GetFipsPartitionKey(HttpContext httpContext)
{
    try
    {
        // Prefer route value for GET /api/v1/surveys/{fipsId}
        if (httpContext.Request.RouteValues.TryGetValue("fipsId", out var routeVal))
        {
            var s = routeVal?.ToString();
            if (!string.IsNullOrWhiteSpace(s)) return $"fips:{s}";
        }
    }
    catch { }
    return null;
}

static string GetGlobalPartitionKey(HttpContext httpContext)
{
    var identityName = httpContext.User.Identity?.IsAuthenticated == true
        ? httpContext.User.Identity?.Name
        : null;
    if (!string.IsNullOrWhiteSpace(identityName))
    {
        return $"user:{identityName}";
    }

    string? forwardedFor = httpContext.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedValues)
        ? forwardedValues.FirstOrDefault()
        : null;

    if (!string.IsNullOrWhiteSpace(forwardedFor))
    {
        var firstIp = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstIp))
        {
            return $"ip:{firstIp}";
        }
    }

    var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString();
    if (!string.IsNullOrWhiteSpace(remoteIp))
    {
        return $"ip:{remoteIp}";
    }

    return $"host:{httpContext.Request.Headers.Host}";
}

