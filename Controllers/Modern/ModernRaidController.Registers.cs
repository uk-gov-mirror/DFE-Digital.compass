using Compass.Models;
using Compass.Models.Raid;
using Compass.Services.Fips;
using Compass.Services.Modern;
using Compass.Services.Raid;
using Microsoft.Extensions.DependencyInjection;
using Compass.ViewModels.Modern;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compass.Controllers.Modern;

public partial class ModernRaidController
{
    // ── Register Dashboard ───────────────────────────────────────

    [HttpGet("")]
    [HttpGet("index")]
    [HttpGet("dashboard")]
    [HttpGet("registers")]
    [HttpGet("/ModernRaid")]
    [HttpGet("/ModernRaid/Index")]
    [HttpGet("/ModernRaid/Dashboard")]
    public async Task<IActionResult> Registers(CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-registers");

        var userId = await ResolveCurrentUserIdAsync(cancellationToken);

        var yourRegisterEntities = await GetYourRegistersAsync(userId, cancellationToken);
        var allRegisterEntities = await GetAllRegistersAsync(cancellationToken);

        var registerIds = allRegisterEntities.Select(r => r.Id).ToList();

        var riskCounts = await _db.RaidRegisterRisks
            .Where(rr => registerIds.Contains(rr.RaidRegisterId) && !rr.Risk.IsDeleted && rr.Risk.ClosedDate == null)
            .GroupBy(rr => rr.RaidRegisterId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var issueCounts = await _db.RaidRegisterIssues
            .Where(ri => registerIds.Contains(ri.RaidRegisterId) && !ri.Issue.IsDeleted && ri.Issue.ClosedDate == null)
            .GroupBy(ri => ri.RaidRegisterId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var assumptionCounts = await _db.RaidRegisterAssumptions
            .Where(ra => registerIds.Contains(ra.RaidRegisterId) && !ra.Assumption.IsDeleted)
            .GroupBy(ra => ra.RaidRegisterId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var dependencyCounts = await _db.RaidRegisterDependencies
            .Where(rd => registerIds.Contains(rd.RaidRegisterId) && rd.Dependency.Status != "Resolved" && rd.Dependency.Status != "Cancelled")
            .GroupBy(rd => rd.RaidRegisterId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var nearMissCounts = await _db.RaidRegisterNearMisses
            .Where(rn => registerIds.Contains(rn.RaidRegisterId) && !rn.NearMiss.IsDeleted)
            .GroupBy(rn => rn.RaidRegisterId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var yourRegisters = yourRegisterEntities
            .Select(r => BuildRegisterCardViewModel(
                r,
                userId,
                riskCounts,
                issueCounts,
                assumptionCounts,
                dependencyCounts,
                nearMissCounts))
            .ToList();

        var allRegisters = allRegisterEntities
            .Select(r => BuildRegisterCardViewModel(
                r,
                userId,
                riskCounts,
                issueCounts,
                assumptionCounts,
                dependencyCounts,
                nearMissCounts))
            .ToList();

        var vm = new RaidRegisterDashboardViewModel
        {
            YourRegisters = yourRegisters,
            AllRegisters = allRegisters
        };

        return View("~/Views/Modern/Raid/Registers/Index.cshtml", vm);
    }

    // ── Register Detail ──────────────────────────────────────────

    [HttpGet("registers/{id:int}")]
    public async Task<IActionResult> RegisterDetail(int id, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-registers");

        var userId = await ResolveCurrentUserIdAsync(cancellationToken);

        var register = await _db.RaidRegisters
            .AsNoTracking()
            .Include(r => r.DirectorateLookup)
            .Include(r => r.BusinessAreaLookup)
            .Include(r => r.Directorates).ThenInclude(d => d.DirectorateLookup)
            .Include(r => r.BusinessAreas).ThenInclude(b => b.BusinessAreaLookup)
            .Include(r => r.CreatedByUser)
            .Include(r => r.Users).ThenInclude(u => u.User)
            .Include(r => r.WorkItems).ThenInclude(w => w.Project)
            .Include(r => r.Services).ThenInclude(s => s.FipsService)
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, cancellationToken);

        if (register == null) return NotFound();

        if (!CanViewRegister(userId))
            return Forbid();

        await RaidRegisterScopedEntitySync.SyncAsync(_db, id, userId, cancellationToken);

        var currentUserRole = ResolveUserRegisterRole(register, userId);

        var entityRows = await LoadRaidRegisterEntityRowsAsync(id, cancellationToken);
        var risks = entityRows.Risks;
        var issues = entityRows.Issues;
        var assumptions = entityRows.Assumptions;
        var dependencies = entityRows.Dependencies;
        var nearMisses = entityRows.NearMisses;

        var allActiveRiskTiers = await _db.RiskTiers.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var spreadsheetTierRows = RiskTierSpreadsheet.ResolveRows(allActiveRiskTiers);

        var userEmail = User.Identity?.Name
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? User.FindFirst("preferred_username")?.Value;
        var canEditLockedInherentRatings = !string.IsNullOrWhiteSpace(userEmail)
            && await _permissions.IsCentralOperationsAdminOrSuperAdminAsync(userEmail.Trim());

        var vm = new RaidRegisterDetailViewModel
        {
            Id = register.Id,
            Name = register.Name,
            Description = register.Description,
            DirectorateName = RaidRegisterScopeHelper.FormatDirectorateNames(register),
            BusinessAreaName = RaidRegisterScopeHelper.FormatBusinessAreaNames(register),
            CreatedAt = register.CreatedAt,
            UpdatedAt = register.UpdatedAt,
            CreatedByName = register.CreatedByUser?.Name ?? register.CreatedByUser?.Email ?? "Unknown",
            CurrentUserRole = currentUserRole,
            OpenRiskCount = risks.Count(r => r.ClosedDate == null),
            OpenIssueCount = issues.Count(i => i.ClosedDate == null),
            OpenAssumptionCount = assumptions.Count,
            OpenDependencyCount = dependencies.Count(d => d.Status != "Resolved" && d.Status != "Cancelled"),
            OpenNearMissCount = nearMisses.Count,
            CanEditLockedInherentRatings = canEditLockedInherentRatings,
            Risks = risks,
            Issues = issues,
            Assumptions = assumptions,
            Dependencies = dependencies,
            NearMisses = nearMisses,
            WorkItemNames = register.WorkItems.Select(w => w.Project?.Title ?? $"Work item #{w.ProjectId}").ToList(),
            ServiceNames = register.Services.Select(s => s.FipsService?.DisplayName ?? $"Service #{s.FipsServiceId}").ToList(),
            Users = register.Users.Select(u => new RaidRegisterUserRow
            {
                UserId = u.UserId,
                Email = u.User?.Email ?? "",
                DisplayName = u.User?.Name,
                Role = u.Role
            }).ToList(),

            RiskStatuses = await _db.RiskStatuses.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => new SelectOption(x.Id, x.Label)).ToListAsync(cancellationToken),
            RiskPriorities = await _db.RiskPriorities.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => new SelectOption(x.Id, x.Label)).ToListAsync(cancellationToken),
            RiskLikelihoods = await _db.RiskLikelihoods.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => new SelectOption(x.Id, x.Label)).ToListAsync(cancellationToken),
            RiskImpactLevels = await _db.RiskImpactLevels.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => new SelectOption(x.Id, x.Label)).ToListAsync(cancellationToken),
            RiskProximities = await _db.RiskProximities.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => new SelectOption(x.Id, x.Label)).ToListAsync(cancellationToken),
            RiskCategories = await _db.RiskCategories.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => new SelectOption(x.Id, x.Label)).ToListAsync(cancellationToken),
            IssueStatuses = await _db.IssueStatuses.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => new SelectOption(x.Id, x.Label)).ToListAsync(cancellationToken),
            IssuePriorities = await _db.IssuePriorities.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => new SelectOption(x.Id, x.Label)).ToListAsync(cancellationToken),
            IssueSeverities = await _db.IssueSeverities.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => new SelectOption(x.Id, x.Label)).ToListAsync(cancellationToken),
            IssueCategories = await _db.IssueCategories.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => new SelectOption(x.Id, x.Label)).ToListAsync(cancellationToken),
            NearMissTypes = await _db.NearMissTypes.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => new SelectOption(x.Id, x.Label)).ToListAsync(cancellationToken),
            NearMissSeriousnesses = await _db.NearMissSeriousnesses.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => new SelectOption(x.Id, x.Label)).ToListAsync(cancellationToken),
            NearMissStatuses = await _db.NearMissStatuses.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => new SelectOption(x.Id, x.Label)).ToListAsync(cancellationToken),
            AssumptionStatuses = await _db.AssumptionStatuses.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => new SelectOption(x.Id, x.Label)).ToListAsync(cancellationToken),
            AssumptionCriticalities = await _db.AssumptionCriticalities.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => new SelectOption(x.Id, x.Label)).ToListAsync(cancellationToken),
            RiskTiers = RiskTierSpreadsheet.BuildSpreadsheetSelectOptions(spreadsheetTierRows).ToList(),
            WorkItemOptions = (await RaidEditorProjectOptionsFullAsync(cancellationToken))
                .Select(x => new SelectOption(x.Id, x.Name))
                .ToList(),
            ServiceOptions = (await RaidFipsProductSelectOptionsAsync(cancellationToken))
                .Select(x => new SelectOption(x.Id, x.Name))
                .ToList()
        };

        var layoutService = HttpContext.RequestServices.GetRequiredService<IRaidRegisterSpreadsheetLayoutService>();
        var columnOrders = await layoutService.GetColumnOrdersAsync(cancellationToken);
        vm.SpreadsheetColumnOrders = columnOrders.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);

        return View("~/Views/Modern/Raid/Registers/Detail.cshtml", vm);
    }

    // ── Onboarding Wizard ────────────────────────────────────────

    [HttpGet("registers/create")]
    public async Task<IActionResult> RegisterCreate(int step = 1, int? id = null, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-registers");
        step = Math.Clamp(step, 1, 6);

        RaidRegister? existing = null;
        if (id.HasValue)
        {
            existing = await _db.RaidRegisters
                .Include(r => r.WorkItems)
                .Include(r => r.Services)
                .Include(r => r.Directorates)
                .Include(r => r.BusinessAreas)
                .Include(r => r.Users).ThenInclude(u => u.User)
                .FirstOrDefaultAsync(r => r.Id == id.Value && !r.IsDeleted, cancellationToken);
        }

        var vm = new RaidRegisterOnboardingViewModel
        {
            RegisterId = existing?.Id,
            Step = step,
            Name = existing?.Name ?? "",
            Description = existing?.Description,
            SelectedDirectorateLookupIds = existing != null
                ? RaidRegisterScopeHelper.GetDirectorateIds(existing)
                : new List<int>(),
            SelectedBusinessAreaLookupIds = existing != null
                ? RaidRegisterScopeHelper.GetBusinessAreaIds(existing)
                : new List<int>(),
            SelectedWorkItemIds = existing?.WorkItems.Select(w => w.ProjectId).ToList() ?? new(),
            SelectedServiceIds = existing?.Services.Select(s => s.FipsServiceId).ToList() ?? new(),
            RegisterUsers = existing?.Users.Select(u => new RaidRegisterUserRow
            {
                UserId = u.UserId,
                Email = u.User?.Email ?? "",
                DisplayName = u.User?.Name,
                Role = u.Role
            }).ToList() ?? new()
        };

        await PopulateOnboardingOptionsAsync(vm, cancellationToken);

        return View("~/Views/Modern/Raid/Registers/Create.cshtml", vm);
    }

    [HttpPost("registers/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegisterCreatePost(
        int step,
        RaidRegisterOnboardingViewModel vm,
        CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-registers");

        var userId = await ResolveCurrentUserIdAsync(cancellationToken);
        if (!userId.HasValue)
        {
            TempData["Error"] = "We could not match your sign-in to a user. Try again or contact support.";
            return RedirectToAction(nameof(Registers));
        }

        // Step 1: Create or update the register draft
        RaidRegister register;
        if (vm.RegisterId.HasValue)
        {
            register = await _db.RaidRegisters
                .Include(r => r.WorkItems)
                .Include(r => r.Services)
                .Include(r => r.Directorates)
                .Include(r => r.BusinessAreas)
                .Include(r => r.Users)
                .FirstOrDefaultAsync(r => r.Id == vm.RegisterId.Value && !r.IsDeleted, cancellationToken)
                ?? throw new InvalidOperationException("Register not found.");
        }
        else
        {
            register = new RaidRegister
            {
                CreatedByUserId = userId.Value,
                CreatedAt = DateTime.UtcNow
            };
            _db.RaidRegisters.Add(register);
        }

        switch (step)
        {
            case 1:
                if (string.IsNullOrWhiteSpace(vm.Name))
                {
                    ModelState.AddModelError("Name", "Enter a name for this register");
                    await PopulateOnboardingOptionsAsync(vm, cancellationToken);
                    return View("~/Views/Modern/Raid/Registers/Create.cshtml", vm);
                }
                register.Name = vm.Name.Trim();
                register.Description = vm.Description?.Trim();
                register.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                return RedirectToAction(nameof(RegisterCreate), new { step = 2, id = register.Id });

            case 2:
                RaidRegisterScopeHelper.SyncScope(
                    register,
                    vm.SelectedDirectorateLookupIds,
                    vm.SelectedBusinessAreaLookupIds);
                register.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                return RedirectToAction(nameof(RegisterCreate), new { step = 3, id = register.Id });

            case 3:
                SyncRegisterWorkItems(register, vm.SelectedWorkItemIds);
                register.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                await RaidRegisterScopedEntitySync.SyncAsync(_db, register.Id, userId, cancellationToken);
                return RedirectToAction(nameof(RegisterCreate), new { step = 4, id = register.Id });

            case 4:
                SyncRegisterServices(register, vm.SelectedServiceIds);
                register.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                await RaidRegisterScopedEntitySync.SyncAsync(_db, register.Id, userId, cancellationToken);
                return RedirectToAction(nameof(RegisterCreate), new { step = 5, id = register.Id });

            case 5:
                SyncRegisterUsers(register, vm.RegisterUsers, userId.Value);
                if (!register.Users.Any(u => u.Role == RaidRegisterRole.Owner))
                {
                    var creatorRow = register.Users.FirstOrDefault(u => u.UserId == userId.Value);
                    if (creatorRow != null)
                        creatorRow.Role = RaidRegisterRole.Owner;
                    else
                    {
                        register.Users.Add(new RaidRegisterUser
                        {
                            UserId = userId.Value,
                            Role = RaidRegisterRole.Owner,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
                register.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                return RedirectToAction(nameof(RegisterCreate), new { step = 6, id = register.Id });

            case 6:
                // Ensure creator is an Owner in the users table
                if (!register.Users.Any(u => u.UserId == userId.Value))
                {
                    register.Users.Add(new RaidRegisterUser
                    {
                        UserId = userId.Value,
                        Role = RaidRegisterRole.Owner,
                        CreatedAt = DateTime.UtcNow
                    });
                }
                register.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);

                TempData["SuccessMessage"] = $"RAID register \"{register.Name}\" has been created.";
                return RedirectToAction(nameof(RegisterDetail), new { id = register.Id });

            default:
                return RedirectToAction(nameof(RegisterCreate), new { step = 1, id = register.Id });
        }
    }

    // ── Register Settings (edit scope) ───────────────────────────

    [HttpGet("registers/{id:int}/settings")]
    public async Task<IActionResult> RegisterSettings(int id, CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-registers");

        var userId = await ResolveCurrentUserIdAsync(cancellationToken);
        var register = await _db.RaidRegisters
            .Include(r => r.WorkItems)
            .Include(r => r.Services)
            .Include(r => r.Users).ThenInclude(u => u.User)
            .Include(r => r.CreatedByUser)
            .Include(r => r.DirectorateLookup)
            .Include(r => r.BusinessAreaLookup)
            .Include(r => r.Directorates).ThenInclude(d => d.DirectorateLookup)
            .Include(r => r.BusinessAreas).ThenInclude(b => b.BusinessAreaLookup)
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, cancellationToken);

        if (register == null) return NotFound();
        if (!IsRegisterOwnerOrManager(register, userId))
            return Forbid();

        var (ownerUserId, ownerName, ownerEmail) = ResolveRegisterOwner(register);
        var isRegisterOwner = IsRegisterOwner(register, userId);

        var vm = new RaidRegisterOnboardingViewModel
        {
            RegisterId = register.Id,
            Step = 1,
            Name = register.Name,
            Description = register.Description,
            OwnerUserId = ownerUserId,
            OwnerName = ownerName,
            OwnerEmail = ownerEmail,
            IsRegisterOwner = isRegisterOwner,
            CreatedByName = register.CreatedByUser?.Name ?? register.CreatedByUser?.Email,
            SelectedDirectorateLookupIds = RaidRegisterScopeHelper.GetDirectorateIds(register),
            SelectedBusinessAreaLookupIds = RaidRegisterScopeHelper.GetBusinessAreaIds(register),
            SelectedWorkItemIds = register.WorkItems.Select(w => w.ProjectId).ToList(),
            SelectedServiceIds = register.Services.Select(s => s.FipsServiceId).ToList(),
            RegisterUsers = register.Users.Select(u => new RaidRegisterUserRow
            {
                UserId = u.UserId,
                Email = u.User?.Email ?? "",
                DisplayName = u.User?.Name,
                Role = u.Role
            }).ToList()
        };

        await PopulateOnboardingOptionsAsync(vm, cancellationToken);

        vm.SelectedWorkItemNames = vm.WorkItemOptions?
            .Where(o => vm.SelectedWorkItemIds.Contains(o.Id))
            .Select(o => o.Name).ToList() ?? new List<string>();
        vm.SelectedServiceNames = vm.ServiceOptions?
            .Where(o => vm.SelectedServiceIds.Contains(o.Id))
            .Select(o => o.Name).ToList() ?? new List<string>();
        vm.SelectedDirectorateNames = vm.DirectorateOptions
            .Where(o => vm.SelectedDirectorateLookupIds.Contains(o.Id))
            .Select(o => o.Name).ToList();
        vm.SelectedBusinessAreaNames = vm.BusinessAreaOptions
            .Where(o => vm.SelectedBusinessAreaLookupIds.Contains(o.Id))
            .Select(o => o.Name).ToList();

        return View("~/Views/Modern/Raid/Registers/Settings.cshtml", vm);
    }

    [HttpPost("registers/{id:int}/settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegisterSettingsPost(
        int id,
        string section,
        RaidRegisterOnboardingViewModel vm,
        CancellationToken cancellationToken = default)
    {
        SetRaidChrome("raid-registers");

        var userId = await ResolveCurrentUserIdAsync(cancellationToken);
        var register = await _db.RaidRegisters
            .Include(r => r.WorkItems)
            .Include(r => r.Services)
            .Include(r => r.Directorates)
            .Include(r => r.BusinessAreas)
            .Include(r => r.Users)
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, cancellationToken);

        if (register == null) return NotFound();
        if (!IsRegisterOwnerOrManager(register, userId))
            return Forbid();

        var isRegisterOwner = IsRegisterOwner(register, userId);
        var (currentOwnerUserId, _, _) = ResolveRegisterOwner(register);

        switch (section)
        {
            case "delete":
                if (!isRegisterOwner)
                    return Forbid();

                register.IsDeleted = true;
                register.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                TempData["SuccessMessage"] = $"\"{register.Name}\" has been deleted. Risks, issues and other RAID records were not removed.";
                return RedirectToAction(nameof(Registers));

            case "details":
                if (string.IsNullOrWhiteSpace(vm.Name))
                {
                    TempData["ErrorMessage"] = "Enter a name for this register.";
                    return RedirectToAction(nameof(RegisterSettings), new { id });
                }
                register.Name = vm.Name.Trim();
                register.Description = vm.Description?.Trim();

                if (isRegisterOwner
                    && vm.OwnerUserId.HasValue
                    && vm.OwnerUserId.Value != currentOwnerUserId)
                {
                    var ownerExists = await _db.Users.AsNoTracking()
                        .AnyAsync(u => u.Id == vm.OwnerUserId.Value, cancellationToken);
                    if (!ownerExists)
                    {
                        TempData["ErrorMessage"] = "The selected owner could not be found.";
                        return RedirectToAction(nameof(RegisterSettings), new { id });
                    }
                    SyncRegisterOwner(register, vm.OwnerUserId.Value);
                }
                else if (!isRegisterOwner && vm.OwnerUserId.HasValue && vm.OwnerUserId.Value != currentOwnerUserId)
                {
                    TempData["ErrorMessage"] = "Only the register owner can change the owner.";
                    return RedirectToAction(nameof(RegisterSettings), new { id });
                }
                break;

            case "owner":
                if (!isRegisterOwner)
                    return Forbid();

                if (!vm.OwnerUserId.HasValue)
                {
                    TempData["ErrorMessage"] = "Select a register owner.";
                    return Redirect($"{RegisterSettingsPath(id)}#rs-owner");
                }
                var newOwnerExists = await _db.Users.AsNoTracking()
                    .AnyAsync(u => u.Id == vm.OwnerUserId.Value, cancellationToken);
                if (!newOwnerExists)
                {
                    TempData["ErrorMessage"] = "The selected owner could not be found.";
                    return Redirect($"{RegisterSettingsPath(id)}#rs-owner");
                }
                SyncRegisterOwner(register, vm.OwnerUserId.Value);
                break;

            case "scope":
                RaidRegisterScopeHelper.SyncScope(
                    register,
                    vm.SelectedDirectorateLookupIds,
                    vm.SelectedBusinessAreaLookupIds);
                break;

            case "workitems":
                SyncRegisterWorkItems(register, vm.SelectedWorkItemIds);
                break;

            case "services":
                SyncRegisterServices(register, vm.SelectedServiceIds);
                break;

            case "users":
                SyncRegisterUsers(register, vm.RegisterUsers, userId!.Value);
                break;
        }

        register.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        if (section is "scope" or "workitems" or "services")
            await RaidRegisterScopedEntitySync.SyncAsync(_db, register.Id, userId, cancellationToken);

        TempData["SuccessMessage"] = section switch
        {
            "owner" => "Register owner updated.",
            _ => "Register settings updated."
        };

        return section == "owner"
            ? Redirect($"{RegisterSettingsPath(id)}#rs-owner")
            : RedirectToAction(nameof(RegisterSettings), new { id });
    }

    // ── Helpers ──────────────────────────────────────────────────

    private IQueryable<RaidRegister> RaidRegistersForListQuery() =>
        _db.RaidRegisters.AsNoTracking()
            .Where(r => !r.IsDeleted)
            .Include(r => r.DirectorateLookup)
            .Include(r => r.BusinessAreaLookup)
            .Include(r => r.Directorates).ThenInclude(d => d.DirectorateLookup)
            .Include(r => r.BusinessAreas).ThenInclude(b => b.BusinessAreaLookup)
            .Include(r => r.CreatedByUser)
            .Include(r => r.Users).ThenInclude(u => u.User)
            .Include(r => r.WorkItems).ThenInclude(w => w.Project)
            .Include(r => r.Services).ThenInclude(s => s.FipsService);

    private async Task<List<RaidRegister>> GetYourRegistersAsync(int? userId, CancellationToken cancellationToken)
    {
        if (!userId.HasValue)
            return new List<RaidRegister>();

        var uid = userId.Value;
        return await RaidRegistersForListQuery()
            .Where(r => r.CreatedByUserId == uid || r.Users.Any(u => u.UserId == uid))
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);
    }

    private async Task<List<RaidRegister>> GetAllRegistersAsync(CancellationToken cancellationToken) =>
        await RaidRegistersForListQuery()
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

    private RaidRegisterCardViewModel BuildRegisterCardViewModel(
        RaidRegister r,
        int? userId,
        IReadOnlyDictionary<int, int> riskCounts,
        IReadOnlyDictionary<int, int> issueCounts,
        IReadOnlyDictionary<int, int> assumptionCounts,
        IReadOnlyDictionary<int, int> dependencyCounts,
        IReadOnlyDictionary<int, int> nearMissCounts)
    {
        var role = ResolveUserRegisterRole(r, userId);
        return new RaidRegisterCardViewModel
        {
            Id = r.Id,
            Name = r.Name,
            Description = r.Description,
            DirectorateName = RaidRegisterScopeHelper.FormatDirectorateNames(r),
            BusinessAreaName = RaidRegisterScopeHelper.FormatBusinessAreaNames(r),
            OwnerName = ResolveRegisterOwnerName(r),
            UpdatedAt = r.UpdatedAt,
            OpenRiskCount = riskCounts.GetValueOrDefault(r.Id),
            OpenIssueCount = issueCounts.GetValueOrDefault(r.Id),
            OpenAssumptionCount = assumptionCounts.GetValueOrDefault(r.Id),
            OpenDependencyCount = dependencyCounts.GetValueOrDefault(r.Id),
            OpenNearMissCount = nearMissCounts.GetValueOrDefault(r.Id),
            TotalItemCount = riskCounts.GetValueOrDefault(r.Id)
                + issueCounts.GetValueOrDefault(r.Id)
                + assumptionCounts.GetValueOrDefault(r.Id)
                + dependencyCounts.GetValueOrDefault(r.Id)
                + nearMissCounts.GetValueOrDefault(r.Id),
            WorkItemNames = r.WorkItems
                .Select(w => w.Project?.Title ?? "Unknown")
                .OrderBy(n => n).ToList(),
            ServiceNames = r.Services
                .Select(s => s.FipsService?.DisplayName ?? "Unknown")
                .OrderBy(n => n).ToList(),
            CanManage = role == RaidRegisterRole.Owner || role == RaidRegisterRole.Manager
        };
    }

    private static bool CanViewRegister(int? userId) => userId.HasValue;

    private async Task<bool> CanEditLockedInherentRatingsAsync(CancellationToken ct)
    {
        var email = User.Identity?.Name
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? User.FindFirst("preferred_username")?.Value;
        return !string.IsNullOrWhiteSpace(email)
            && await _permissions.IsCentralOperationsAdminOrSuperAdminAsync(email.Trim());
    }

    private static RaidRegisterRole ResolveUserRegisterRole(RaidRegister register, int? userId)
    {
        if (!userId.HasValue)
            return RaidRegisterRole.Viewer;

        var membership = register.Users.FirstOrDefault(u => u.UserId == userId.Value);
        if (membership != null)
            return membership.Role;

        if (register.CreatedByUserId == userId.Value)
            return RaidRegisterRole.Owner;

        return RaidRegisterRole.Viewer;
    }

    private sealed record RaidRegisterEntityRowSet(
        List<RaidRegisterRiskRow> Risks,
        List<RaidRegisterIssueRow> Issues,
        List<RaidRegisterAssumptionRow> Assumptions,
        List<RaidRegisterDependencyRow> Dependencies,
        List<RaidRegisterNearMissRow> NearMisses);

    private async Task<RaidRegisterEntityRowSet> LoadRaidRegisterEntityRowsAsync(int registerId, CancellationToken cancellationToken)
    {
        var risks = await _db.RaidRegisterRisks.AsNoTracking()
            .Where(rr => rr.RaidRegisterId == registerId && !rr.Risk.IsDeleted)
            .Select(rr => new RaidRegisterRiskRow
            {
                Id = rr.Risk.Id,
                Reference = $"R-{rr.Risk.Id:D4}",
                Title = rr.Risk.Title,
                Description = rr.Risk.Description,
                Status = rr.Risk.RiskStatus != null ? rr.Risk.RiskStatus.Label : rr.Risk.Status,
                StatusId = rr.Risk.RiskStatusId,
                ClosedDate = rr.Risk.ClosedDate,
                Owner = rr.Risk.OwnerUser != null ? rr.Risk.OwnerUser.Name : rr.Risk.OwnerEmail,
                OwnerUserId = rr.Risk.OwnerUserId,
                Tier = rr.Risk.RiskTier != null ? rr.Risk.RiskTier.Name : null,
                TierId = rr.Risk.RiskTierId,
                Category = rr.Risk.RiskCategory != null ? rr.Risk.RiskCategory.Label : null,
                CategoryId = rr.Risk.RiskCategoryId,
                Priority = rr.Risk.RiskPriority != null ? rr.Risk.RiskPriority.Label : null,
                PriorityId = rr.Risk.RiskPriorityId,
                Proximity = rr.Risk.Proximity != null ? rr.Risk.Proximity.Label : null,
                ProximityId = rr.Risk.RiskProximityId,
                ResponseStrategy = rr.Risk.ResponseStrategy,
                Cause = rr.Risk.Cause,
                ImpactIfRealised = rr.Risk.ImpactIfRealised,
                Contingency = rr.Risk.Contingency,
                Assurance = rr.Risk.Assurance,
                FinancialImpact = rr.Risk.FinancialImpact,
                Response = rr.Risk.Response,

                OriginalImpactId = rr.Risk.RiskImpactLevelId,
                OriginalImpact = rr.Risk.ImpactLevel != null ? rr.Risk.ImpactLevel.Label : null,
                OriginalLikelihoodId = rr.Risk.RiskLikelihoodId,
                OriginalLikelihood = rr.Risk.Likelihood != null ? rr.Risk.Likelihood.Label : null,
                InherentScore = rr.Risk.InherentScore,

                CurrentImpactId = rr.Risk.CurrentImpactLevelId,
                CurrentImpact = rr.Risk.CurrentImpactLevel != null ? rr.Risk.CurrentImpactLevel.Label : null,
                CurrentLikelihoodId = rr.Risk.CurrentLikelihoodId,
                CurrentLikelihood = rr.Risk.CurrentLikelihood != null ? rr.Risk.CurrentLikelihood.Label : null,
                CurrentScore = rr.Risk.CurrentScore,

                ResidualImpactId = rr.Risk.ResidualImpactLevelId,
                ResidualImpact = rr.Risk.ResidualImpactLevel != null ? rr.Risk.ResidualImpactLevel.Label : null,
                ResidualLikelihoodId = rr.Risk.ResidualLikelihoodId,
                ResidualLikelihood = rr.Risk.ResidualLikelihoodLevel != null ? rr.Risk.ResidualLikelihoodLevel.Label : null,
                ResidualScore = rr.Risk.ResidualScore,

                ToleranceImpactId = rr.Risk.ToleranceImpactLevelId,
                ToleranceImpact = rr.Risk.ToleranceImpactLevel != null ? rr.Risk.ToleranceImpactLevel.Label : null,
                ToleranceLikelihoodId = rr.Risk.ToleranceLikelihoodId,
                ToleranceLikelihood = rr.Risk.ToleranceLikelihood != null ? rr.Risk.ToleranceLikelihood.Label : null,
                ToleranceScore = rr.Risk.ToleranceScore,

                NextReviewDate = rr.Risk.NextReviewDate,
                CreatedAt = rr.Risk.CreatedAt,
                IdentifiedDate = rr.Risk.IdentifiedDate,
                LastReviewDate = rr.Risk.LastReviewDate,
                UpdatedAt = rr.Risk.UpdatedAt,
                CommentCount = _db.Comments.Count(c => c.EntityType == "Risk" && c.EntityId == rr.Risk.Id && !c.IsDeleted)
            }).ToListAsync(cancellationToken);

        var riskIds = risks.Select(r => r.Id).ToList();
        if (riskIds.Count > 0)
        {
            var mitigationCountByRiskId = await RaidRiskMitigations.CountByRiskIdsAsync(
                _db, riskIds, cancellationToken);
            foreach (var risk in risks)
                risk.MitigationCount = mitigationCountByRiskId.GetValueOrDefault(risk.Id);

            var kriRows = await _db.RiskKeyRiskIndicators.AsNoTracking()
                .Where(k => riskIds.Contains(k.RiskId))
                .OrderBy(k => k.RiskId)
                .ThenBy(k => k.SortOrder)
                .ThenBy(k => k.Id)
                .Select(k => new { k.RiskId, k.Metric, k.Threshold })
                .ToListAsync(cancellationToken);
            var kriByRiskId = kriRows
                .GroupBy(k => k.RiskId)
                .ToDictionary(
                    g => g.Key,
                    g => string.Join("; ", g.Select(k => FormatKriSpreadsheetSummary(k.Metric, k.Threshold)).Where(s => s.Length > 0)));
            var kriCountByRiskId = kriRows
                .GroupBy(k => k.RiskId)
                .ToDictionary(g => g.Key, g => g.Count());

            var riskCommentCounts = await _db.Comments.AsNoTracking()
                .Where(c => c.EntityType == "Risk" && riskIds.Contains(c.EntityId) && !c.IsDeleted)
                .GroupBy(c => c.EntityId)
                .Select(g => new { RiskId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.RiskId, x => x.Count, cancellationToken);

            var mitigationNotesByRisk = await RaidRiskMitigations.LoadLinksForRiskIdsAsync(
                _db, riskIds, cancellationToken);

            foreach (var risk in risks)
            {
                var mitigationUpdateLines = mitigationNotesByRisk
                    .Where(x => x.RiskId == risk.Id)
                    .Sum(x => RiskCommentTimelineBuilder.CountMitigationUpdateLines(x.Notes));
                risk.CommentCount = riskCommentCounts.GetValueOrDefault(risk.Id)
                    + mitigationUpdateLines;
            }

            foreach (var risk in risks)
            {
                risk.KrisSummary = kriByRiskId.GetValueOrDefault(risk.Id);
                risk.KriCount = kriCountByRiskId.GetValueOrDefault(risk.Id);
            }

            var lastCommentUpdates =
                await RiskCommentTimelineBuilder.GetLastCommentUpdateByRiskIdsAsync(_db, riskIds, cancellationToken);
            foreach (var risk in risks)
            {
                if (!lastCommentUpdates.TryGetValue(risk.Id, out var last))
                    continue;
                risk.LastCommentUpdateText = last.PreviewText;
                risk.LastCommentUpdateKind = last.KindLabel;
                risk.LastCommentUpdateAt = last.AtUtc;
            }
        }

        var issues = await _db.RaidRegisterIssues.AsNoTracking()
            .Where(ri => ri.RaidRegisterId == registerId && !ri.Issue.IsDeleted)
            .Select(ri => new RaidRegisterIssueRow
            {
                Id = ri.Issue.Id,
                Reference = $"I-{ri.Issue.Id:D4}",
                Title = ri.Issue.Title,
                Description = ri.Issue.Description,
                Status = ri.Issue.StatusLookup != null ? ri.Issue.StatusLookup.Label : ri.Issue.Status,
                StatusId = ri.Issue.StatusId,
                ClosedDate = ri.Issue.ClosedDate,
                Severity = ri.Issue.SeverityLookup != null ? ri.Issue.SeverityLookup.Label : ri.Issue.Severity,
                SeverityId = ri.Issue.SeverityId,
                Priority = ri.Issue.PriorityLookup != null ? ri.Issue.PriorityLookup.Label : null,
                PriorityId = ri.Issue.PriorityId,
                Category = ri.Issue.CategoryLookup != null ? ri.Issue.CategoryLookup.Label : null,
                CategoryId = ri.Issue.IssueCategoryId,
                Owner = ri.Issue.OwnerUser != null ? ri.Issue.OwnerUser.Name : null,
                OwnerUserId = ri.Issue.OwnerUserId,
                IdentifiedDate = ri.Issue.DetectedDate,
                TargetResolutionDate = ri.Issue.TargetResolutionDate,
                UpdatedAt = ri.Issue.UpdatedAt,
                CommentCount = _db.Comments.Count(c => c.EntityType == "Issue" && c.EntityId == ri.Issue.Id && !c.IsDeleted)
            }).ToListAsync(cancellationToken);

        var assumptions = await _db.RaidRegisterAssumptions.AsNoTracking()
            .Where(ra => ra.RaidRegisterId == registerId && !ra.Assumption.IsDeleted)
            .Select(ra => new RaidRegisterAssumptionRow
            {
                Id = ra.Assumption.Id,
                Reference = $"A-{ra.Assumption.Id:D4}",
                Description = ra.Assumption.Description,
                Status = ra.Assumption.StatusLookup != null ? ra.Assumption.StatusLookup.Label : null,
                StatusId = ra.Assumption.AssumptionStatusId,
                Criticality = ra.Assumption.CriticalityLookup != null ? ra.Assumption.CriticalityLookup.Label : null,
                CriticalityId = ra.Assumption.AssumptionCriticalityId,
                Owner = ra.Assumption.OwnerUser != null ? ra.Assumption.OwnerUser.Name : null,
                OwnerUserId = ra.Assumption.OwnerUserId,
                ReviewDate = ra.Assumption.ReviewDate,
                CreatedAt = ra.Assumption.CreatedAt,
                UpdatedAt = ra.Assumption.UpdatedAt,
                CommentCount = _db.Comments.Count(c => c.EntityType == "Assumption" && c.EntityId == ra.Assumption.Id && !c.IsDeleted)
            }).ToListAsync(cancellationToken);

        var dependencies = await _db.RaidRegisterDependencies.AsNoTracking()
            .Where(rd => rd.RaidRegisterId == registerId)
            .Select(rd => new RaidRegisterDependencyRow
            {
                Id = rd.Dependency.Id,
                Description = rd.Dependency.Description,
                LinkType = rd.Dependency.LinkTypeLookup != null ? rd.Dependency.LinkTypeLookup.Label : rd.Dependency.DependencyType,
                Status = rd.Dependency.Status,
                Owner = rd.Dependency.OwnerUser != null ? rd.Dependency.OwnerUser.Name : null
            }).ToListAsync(cancellationToken);

        var nearMisses = await _db.RaidRegisterNearMisses.AsNoTracking()
            .Where(rn => rn.RaidRegisterId == registerId && !rn.NearMiss.IsDeleted)
            .Select(rn => new RaidRegisterNearMissRow
            {
                Id = rn.NearMiss.Id,
                Reference = rn.NearMiss.Reference,
                Impact = rn.NearMiss.Impact,
                Status = rn.NearMiss.StatusLookup != null ? rn.NearMiss.StatusLookup.Label : null,
                StatusId = rn.NearMiss.NearMissStatusId,
                Seriousness = rn.NearMiss.SeriousnessLookup != null ? rn.NearMiss.SeriousnessLookup.Label : null,
                SeriousnessId = rn.NearMiss.NearMissSeriousnessId,
                Type = rn.NearMiss.TypeLookup != null ? rn.NearMiss.TypeLookup.Label : null,
                TypeId = rn.NearMiss.NearMissTypeId,
                DateLogged = rn.NearMiss.DateLogged,
                UpdatedAt = rn.NearMiss.UpdatedAt,
                CommentCount = _db.Comments.Count(c => c.EntityType == "NearMiss" && c.EntityId == rn.NearMiss.Id && !c.IsDeleted)
            }).ToListAsync(cancellationToken);

        await EnrichRowRelationsAsync(risks, issues, assumptions, nearMisses, cancellationToken);

        var allActiveRiskTiers = await _db.RiskTiers.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var riskTierById = allActiveRiskTiers.ToDictionary(t => t.Id);
        foreach (var risk in risks)
        {
            if (risk.TierId is int tid && riskTierById.TryGetValue(tid, out var tier))
                risk.Tier = RiskTierSpreadsheet.GetDisplayName(tier);
        }

        return new RaidRegisterEntityRowSet(risks, issues, assumptions, dependencies, nearMisses);
    }

    private static bool IsRegisterOwnerOrManager(RaidRegister register, int? userId)
    {
        if (!userId.HasValue) return false;
        if (register.CreatedByUserId == userId.Value) return true;
        return register.Users.Any(u =>
            u.UserId == userId.Value &&
            (u.Role == RaidRegisterRole.Owner || u.Role == RaidRegisterRole.Manager));
    }

    private static string RegisterSettingsPath(int registerId) =>
        $"/modern/raid/registers/{registerId}/settings";

    private static bool IsRegisterOwner(RaidRegister register, int? userId)
    {
        if (!userId.HasValue) return false;
        if (register.Users.Any(u => u.UserId == userId.Value && u.Role == RaidRegisterRole.Owner))
            return true;

        var hasExplicitOwner = register.Users.Any(u => u.Role == RaidRegisterRole.Owner);
        return !hasExplicitOwner && register.CreatedByUserId == userId.Value;
    }

    private async Task PopulateOnboardingOptionsAsync(RaidRegisterOnboardingViewModel vm, CancellationToken cancellationToken)
    {
        vm.DirectorateOptions = await _db.DirectorateLookups.AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .Select(d => new SelectOption(d.Id, d.Name))
            .ToListAsync(cancellationToken);

        vm.BusinessAreaOptions = await _db.BusinessAreaLookups.AsNoTracking()
            .Where(b => b.IsActive)
            .OrderBy(b => b.SortOrder).ThenBy(b => b.Name)
            .Select(b => new SelectOption(b.Id, b.Name))
            .ToListAsync(cancellationToken);

        vm.WorkItemOptions = await _db.Projects.AsNoTracking()
            .Where(p => !p.IsDeleted)
            .OrderBy(p => p.Title)
            .Select(p => new SelectOption(p.Id, p.Title ?? $"Work item #{p.Id}"))
            .ToListAsync(cancellationToken);

        vm.ServiceOptions = await FipsProductRaidQuery
            .BuildActiveServiceRegisterSelectOptionsForRaidAsync(_db, cancellationToken)
            .ContinueWith(t => t.Result.Select(o => new SelectOption(o.Id, o.Name)).ToList(), cancellationToken);

        // Populate review step names
        if (vm.Step == 6 && vm.RegisterId.HasValue)
        {
            vm.SelectedDirectorateNames = vm.DirectorateOptions
                .Where(o => vm.SelectedDirectorateLookupIds.Contains(o.Id))
                .Select(o => o.Name).ToList();
            vm.SelectedBusinessAreaNames = vm.BusinessAreaOptions
                .Where(o => vm.SelectedBusinessAreaLookupIds.Contains(o.Id))
                .Select(o => o.Name).ToList();
            vm.SelectedWorkItemNames = vm.WorkItemOptions
                .Where(o => vm.SelectedWorkItemIds.Contains(o.Id))
                .Select(o => o.Name).ToList();
            vm.SelectedServiceNames = vm.ServiceOptions
                .Where(o => vm.SelectedServiceIds.Contains(o.Id))
                .Select(o => o.Name).ToList();
        }
    }

    private static void SyncRegisterWorkItems(RaidRegister register, List<int> selectedIds)
    {
        var existing = register.WorkItems.Select(w => w.ProjectId).ToHashSet();
        var desired = selectedIds.ToHashSet();

        foreach (var toRemove in register.WorkItems.Where(w => !desired.Contains(w.ProjectId)).ToList())
            register.WorkItems.Remove(toRemove);

        foreach (var toAdd in desired.Where(id => !existing.Contains(id)))
            register.WorkItems.Add(new RaidRegisterWorkItem { ProjectId = toAdd });
    }

    private static void SyncRegisterServices(RaidRegister register, List<int> selectedIds)
    {
        var existing = register.Services.Select(s => s.FipsServiceId).ToHashSet();
        var desired = selectedIds.ToHashSet();

        foreach (var toRemove in register.Services.Where(s => !desired.Contains(s.FipsServiceId)).ToList())
            register.Services.Remove(toRemove);

        foreach (var toAdd in desired.Where(id => !existing.Contains(id)))
            register.Services.Add(new RaidRegisterService { FipsServiceId = toAdd });
    }

    private static (int UserId, string? Name, string? Email) ResolveRegisterOwner(RaidRegister register)
    {
        var ownerEntry = register.Users.FirstOrDefault(u => u.Role == RaidRegisterRole.Owner);
        if (ownerEntry?.User != null)
            return (ownerEntry.UserId, ownerEntry.User.Name, ownerEntry.User.Email);

        if (ownerEntry != null)
            return (ownerEntry.UserId, null, null);

        var creator = register.CreatedByUser;
        return (register.CreatedByUserId, creator?.Name, creator?.Email);
    }

    private static void SyncRegisterOwner(RaidRegister register, int ownerUserId)
    {
        foreach (var u in register.Users.Where(u => u.Role == RaidRegisterRole.Owner && u.UserId != ownerUserId))
            u.Role = RaidRegisterRole.Manager;

        var existing = register.Users.FirstOrDefault(u => u.UserId == ownerUserId);
        if (existing != null)
            existing.Role = RaidRegisterRole.Owner;
        else
        {
            register.Users.Add(new RaidRegisterUser
            {
                UserId = ownerUserId,
                Role = RaidRegisterRole.Owner,
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    private static void SyncRegisterUsers(RaidRegister register, List<RaidRegisterUserRow>? rows, int currentUserId)
    {
        if (rows == null) return;

        var desired = rows
            .Where(r => r.UserId != currentUserId && r.Role != RaidRegisterRole.Owner)
            .ToDictionary(r => r.UserId, r => r.Role);

        foreach (var toRemove in register.Users
            .Where(u => u.Role != RaidRegisterRole.Owner && u.UserId != currentUserId && !desired.ContainsKey(u.UserId))
            .ToList())
            register.Users.Remove(toRemove);

        foreach (var existing in register.Users.Where(u => desired.ContainsKey(u.UserId) && u.Role != RaidRegisterRole.Owner))
            existing.Role = desired[existing.UserId];

        var existingIds = register.Users.Select(u => u.UserId).ToHashSet();
        foreach (var (uid, role) in desired.Where(kv => !existingIds.Contains(kv.Key)))
            register.Users.Add(new RaidRegisterUser
            {
                UserId = uid,
                Role = role,
                CreatedAt = DateTime.UtcNow
            });
    }

    private async Task EnrichRowRelationsAsync(
        List<RaidRegisterRiskRow> risks,
        List<RaidRegisterIssueRow> issues,
        List<RaidRegisterAssumptionRow> assumptions,
        List<RaidRegisterNearMissRow> nearMisses,
        CancellationToken ct)
    {
        var fipsIds = new List<string>();

        Dictionary<int, Risk>? riskEntities = null;
        if (risks.Count > 0)
        {
            var riskIds = risks.Select(r => r.Id).ToList();
            riskEntities = await _db.Risks.AsNoTracking()
                .Where(r => riskIds.Contains(r.Id))
                .Include(r => r.Project)
                .Include(r => r.PrimaryProduct)
                .ToDictionaryAsync(r => r.Id, ct);

            fipsIds.AddRange(riskEntities.Values
                .Where(r => r.PrimaryProduct != null)
                .Select(r => r.PrimaryProduct!.FipsId));
        }

        Dictionary<int, Issue>? issueEntities = null;
        if (issues.Count > 0)
        {
            var issueIds = issues.Select(i => i.Id).ToList();
            issueEntities = await _db.Issues.AsNoTracking()
                .Where(i => issueIds.Contains(i.Id))
                .Include(i => i.Project)
                .Include(i => i.PrimaryProduct)
                .ToDictionaryAsync(i => i.Id, ct);

            fipsIds.AddRange(issueEntities.Values
                .Where(i => i.PrimaryProduct != null)
                .Select(i => i.PrimaryProduct!.FipsId));
        }

        Dictionary<int, Assumption>? assumptionEntities = null;
        if (assumptions.Count > 0)
        {
            var assumptionIds = assumptions.Select(a => a.Id).ToList();
            assumptionEntities = await _db.Assumptions.AsNoTracking()
                .Where(a => assumptionIds.Contains(a.Id))
                .Include(a => a.Project)
                .Include(a => a.PrimaryProduct)
                .ToDictionaryAsync(a => a.Id, ct);

            fipsIds.AddRange(assumptionEntities.Values
                .Where(a => a.PrimaryProduct != null)
                .Select(a => a.PrimaryProduct!.FipsId));
        }

        var cmdbByFipsId = await RaidRegisterRelationEnrichment.LoadCmdbProductsByFipsIdAsync(_db, fipsIds, ct);

        if (riskEntities != null)
        {
            foreach (var row in risks)
            {
                if (!riskEntities.TryGetValue(row.Id, out var risk)) continue;
                ApplyRelationRow(row, risk, RaidRegisterRelationEnrichment.EnrichRiskRelation(risk, Url, "risks", cmdbByFipsId));
            }
        }

        if (issueEntities != null)
        {
            foreach (var row in issues)
            {
                if (!issueEntities.TryGetValue(row.Id, out var issue)) continue;
                ApplyRelationRow(row, issue, RaidRegisterRelationEnrichment.EnrichIssueRelation(issue, Url, "issues", cmdbByFipsId));
            }
        }

        if (assumptionEntities != null)
        {
            foreach (var row in assumptions)
            {
                if (!assumptionEntities.TryGetValue(row.Id, out var assumption)) continue;
                ApplyRelationRow(row, assumption, RaidRegisterRelationEnrichment.EnrichAssumptionRelation(assumption, Url, cmdbByFipsId));
            }
        }

        if (nearMisses.Count > 0)
        {
            var nmIds = nearMisses.Select(n => n.Id).ToList();
            var nmEntities = await _db.NearMisses.AsNoTracking()
                .Where(n => nmIds.Contains(n.Id))
                .Include(n => n.DirectorateLookup)
                .Include(n => n.BusinessAreaLookup)
                .ToDictionaryAsync(n => n.Id, ct);

            foreach (var row in nearMisses)
            {
                if (!nmEntities.TryGetValue(row.Id, out var nm)) continue;
                ApplyRelationRow(row, RaidRegisterRelationEnrichment.EnrichNearMissRelation(nm));
            }
        }
    }

    private static void ApplyRelationRow(RaidRegisterRiskRow row, Risk risk, RaidRegisterRelationParts rel)
    {
        row.RelationKind = rel.Kind;
        row.RelationProjectId = rel.ProjectId;
        row.RelationTarget = rel.Target;
        row.RelationSourceLabel = rel.SourceLabel;
        row.RelationRelatedTitle = rel.RelatedTitle;
        row.RelationRelatedDescription = rel.RelatedDescription;
        row.RelationLinkHref = rel.LinkHref;
        row.AssociationUiKind = ToRaidAssociationUiKind(risk.RaidAssociationKind, risk.ProjectId, risk.PrimaryProductId);
        row.PrimaryProductId = risk.PrimaryProductId;
    }

    private static void ApplyRelationRow(RaidRegisterIssueRow row, Issue issue, RaidRegisterRelationParts rel)
    {
        row.RelationKind = rel.Kind;
        row.RelationProjectId = rel.ProjectId;
        row.RelationTarget = rel.Target;
        row.RelationSourceLabel = rel.SourceLabel;
        row.RelationRelatedTitle = rel.RelatedTitle;
        row.RelationRelatedDescription = rel.RelatedDescription;
        row.RelationLinkHref = rel.LinkHref;
        row.AssociationUiKind = ToRaidAssociationUiKind(issue.RaidAssociationKind, issue.ProjectId, issue.PrimaryProductId);
        row.PrimaryProductId = issue.PrimaryProductId;
    }

    private static void ApplyRelationRow(RaidRegisterAssumptionRow row, Assumption assumption, RaidRegisterRelationParts rel)
    {
        row.RelationKind = rel.Kind;
        row.RelationProjectId = rel.ProjectId;
        row.RelationTarget = rel.Target;
        row.RelationSourceLabel = rel.SourceLabel;
        row.RelationRelatedTitle = rel.RelatedTitle;
        row.RelationRelatedDescription = rel.RelatedDescription;
        row.RelationLinkHref = rel.LinkHref;
        row.AssociationUiKind = ToRaidAssociationUiKind(assumption.RaidAssociationKind, assumption.ProjectId, assumption.PrimaryProductId);
        row.PrimaryProductId = assumption.PrimaryProductId;
    }

    private static void ApplyRelationRow(RaidRegisterNearMissRow row, RaidRegisterRelationParts rel)
    {
        row.RelationKind = rel.Kind;
        row.RelationProjectId = rel.ProjectId;
        row.RelationTarget = rel.Target;
        row.RelationSourceLabel = rel.SourceLabel;
        row.RelationRelatedTitle = rel.RelatedTitle;
        row.RelationRelatedDescription = rel.RelatedDescription;
        row.RelationLinkHref = rel.LinkHref;
    }

    private static string ResolveRegisterOwnerName(RaidRegister register)
    {
        var ownerUser = register.Users
            .FirstOrDefault(u => u.Role == RaidRegisterRole.Owner)?.User;
        if (ownerUser != null)
            return ownerUser.Name ?? ownerUser.Email ?? "Unknown";

        return register.CreatedByUser?.Name
            ?? register.CreatedByUser?.Email
            ?? "Unknown";
    }

    // ── Scope tracking warning API ───────────────────────────────────

    [HttpGet("api/register/{registerId:int}/scope-tracking")]
    public async Task<IActionResult> ApiScopeTracking(
        int registerId,
        [FromQuery] string scopeType,
        [FromQuery] int itemId,
        CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        if (!userId.HasValue)
            return Unauthorized(new { error = "Not signed in" });

        var register = await _db.RaidRegisters.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == registerId && !r.IsDeleted, ct);
        if (register == null)
            return NotFound(new { error = "Register not found" });

        var type = (scopeType ?? "").Trim().ToLowerInvariant();
        if (type == "workitem")
        {
            var project = await _db.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == itemId, ct);
            if (project == null)
                return NotFound(new { error = "Work item not found" });

            var otherRegisterIds = await _db.RaidRegisterWorkItems.AsNoTracking()
                .Where(w => w.ProjectId == itemId && w.RaidRegisterId != registerId && !w.RaidRegister.IsDeleted)
                .Select(w => w.RaidRegisterId)
                .Distinct()
                .ToListAsync(ct);

            var others = await _db.RaidRegisters.AsNoTracking()
                .Where(r => otherRegisterIds.Contains(r.Id))
                .Include(r => r.Users).ThenInclude(u => u.User)
                .Include(r => r.CreatedByUser)
                .ToListAsync(ct);

            if (others.Count == 0)
                return Json(new { hasConflict = false });

            return Json(new
            {
                hasConflict = true,
                itemName = project.Title,
                scopeType = "workitem",
                otherRegisters = others.Select(r => new
                {
                    registerId = r.Id,
                    registerName = r.Name,
                    ownerName = ResolveRegisterOwnerName(r)
                }).ToList()
            });
        }

        if (type == "service")
        {
            var service = await _db.Services.AsNoTracking()
                .FirstOrDefaultAsync(s => s.ServiceId == itemId, ct);
            if (service == null)
                return NotFound(new { error = "Service not found" });

            var otherRegisterIds = await _db.RaidRegisterServices.AsNoTracking()
                .Where(s => s.FipsServiceId == itemId && s.RaidRegisterId != registerId && !s.RaidRegister.IsDeleted)
                .Select(s => s.RaidRegisterId)
                .Distinct()
                .ToListAsync(ct);

            var others = await _db.RaidRegisters.AsNoTracking()
                .Where(r => otherRegisterIds.Contains(r.Id))
                .Include(r => r.Users).ThenInclude(u => u.User)
                .Include(r => r.CreatedByUser)
                .ToListAsync(ct);

            if (others.Count == 0)
                return Json(new { hasConflict = false });

            return Json(new
            {
                hasConflict = true,
                itemName = service.DisplayName ?? service.FipsId,
                scopeType = "service",
                otherRegisters = others.Select(r => new
                {
                    registerId = r.Id,
                    registerName = r.Name,
                    ownerName = ResolveRegisterOwnerName(r)
                }).ToList()
            });
        }

        return BadRequest(new { error = "scopeType must be workitem or service" });
    }

    // ── Track risk/issue in register API ────────────────────────────

    [HttpGet("api/my-registers")]
    public async Task<IActionResult> ApiMyRegisters(CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        if (!userId.HasValue)
            return Json(new { registers = Array.Empty<object>() });

        var uid = userId.Value;
        var registers = await _db.RaidRegisters.AsNoTracking()
            .Where(r => !r.IsDeleted &&
                (r.CreatedByUserId == uid ||
                 r.Users.Any(u => u.UserId == uid && (u.Role == RaidRegisterRole.Owner || u.Role == RaidRegisterRole.Manager))))
            .OrderBy(r => r.Name)
            .Select(r => new { r.Id, r.Name })
            .ToListAsync(ct);

        return Json(new { registers });
    }

    [HttpPost("api/register/{registerId:int}/track")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiTrackInRegister(
        int registerId,
        [FromBody] TrackInRegisterRequest req,
        CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        if (!userId.HasValue)
            return Unauthorized(new { error = "Not signed in" });

        var register = await _db.RaidRegisters
            .Include(r => r.Users)
            .FirstOrDefaultAsync(r => r.Id == registerId && !r.IsDeleted, ct);
        if (register == null)
            return NotFound(new { error = "Register not found" });

        if (!IsRegisterOwnerOrManager(register, userId))
            return Forbid();

        var type = (req.Type ?? "").Trim().ToLowerInvariant();
        var entityId = req.EntityId;

        if (type == "risk")
        {
            var risk = await _db.Risks.AsNoTracking().FirstOrDefaultAsync(r => r.Id == entityId && !r.IsDeleted, ct);
            if (risk == null) return NotFound(new { error = "Risk not found" });

            var alreadyLinked = await _db.RaidRegisterRisks.AnyAsync(
                rr => rr.RaidRegisterId == registerId && rr.RiskId == entityId, ct);
            if (!alreadyLinked)
            {
                _db.RaidRegisterRisks.Add(new RaidRegisterRisk
                {
                    RaidRegisterId = registerId,
                    RiskId = entityId,
                    AddedAt = DateTime.UtcNow,
                    AddedByUserId = userId
                });
                register.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }

            var entityRows = await LoadRaidRegisterEntityRowsAsync(registerId, ct);
            var row = entityRows.Risks.FirstOrDefault(r => r.Id == entityId);
            if (row == null)
                return Json(new { success = true, alreadyLinked });

            return Json(new { success = true, alreadyLinked, risk = MapRiskRowToSpreadsheetApiPayload(row) });
        }

        if (type == "issue")
        {
            var issue = await _db.Issues.AsNoTracking().FirstOrDefaultAsync(i => i.Id == entityId && !i.IsDeleted, ct);
            if (issue == null) return NotFound(new { error = "Issue not found" });

            var alreadyLinked = await _db.RaidRegisterIssues.AnyAsync(
                ri => ri.RaidRegisterId == registerId && ri.IssueId == entityId, ct);
            if (!alreadyLinked)
            {
                _db.RaidRegisterIssues.Add(new RaidRegisterIssue
                {
                    RaidRegisterId = registerId,
                    IssueId = entityId,
                    AddedAt = DateTime.UtcNow,
                    AddedByUserId = userId
                });
                register.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }

            return Json(new { success = true, alreadyLinked });
        }

        return BadRequest(new { error = "Type must be 'risk' or 'issue'" });
    }

    [HttpGet("api/register/{registerId:int}/risks/search")]
    public async Task<IActionResult> ApiSearchRisksForRegister(
        int registerId,
        [FromQuery] string? q,
        [FromQuery] int limit = 15,
        CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        if (!userId.HasValue)
            return Unauthorized(new { error = "Not signed in" });

        var register = await _db.RaidRegisters
            .Include(r => r.Users)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == registerId && !r.IsDeleted, ct);
        if (register == null)
            return NotFound(new { error = "Register not found" });

        if (!IsRegisterOwnerOrManager(register, userId))
            return Forbid();

        var term = (q ?? "").Trim();
        if (term.Length < 2)
            return Json(new { results = Array.Empty<object>() });

        var linkedRiskIds = _db.RaidRegisterRisks
            .Where(rr => rr.RaidRegisterId == registerId)
            .Select(rr => rr.RiskId);

        var risksQuery = _db.Risks.AsNoTracking()
            .Where(r => !r.IsDeleted && !linkedRiskIds.Contains(r.Id));

        if (int.TryParse(term.TrimStart('R', 'r', '-'), out var riskId))
        {
            risksQuery = risksQuery.Where(r =>
                r.Id == riskId ||
                (r.Title != null && r.Title.Contains(term)));
        }
        else
        {
            risksQuery = risksQuery.Where(r => r.Title != null && r.Title.Contains(term));
        }

        var take = Math.Clamp(limit, 1, 30);
        var results = await risksQuery
            .OrderBy(r => r.Title)
            .Take(take)
            .Select(r => new
            {
                id = r.Id,
                reference = $"R-{r.Id:D4}",
                title = r.Title,
                status = r.RiskStatus != null ? r.RiskStatus.Label : r.Status
            })
            .ToListAsync(ct);

        return Json(new { results });
    }

    private static object MapRiskRowToSpreadsheetApiPayload(RaidRegisterRiskRow row) => new
    {
        id = row.Id,
        reference = row.Reference,
        title = row.Title,
        status = row.Status,
        statusId = row.StatusId,
        closedDate = row.ClosedDate,
        tier = row.Tier,
        tierId = row.TierId,
        category = row.Category,
        categoryId = row.CategoryId,
        owner = row.Owner,
        ownerUserId = row.OwnerUserId,
        description = row.Description,
        cause = row.Cause,
        impactIfRealised = row.ImpactIfRealised,
        contingency = row.Contingency,
        assurance = row.Assurance,
        financialImpact = row.FinancialImpact,
        response = row.Response,
        originalImpactId = row.OriginalImpactId,
        originalImpact = row.OriginalImpact,
        originalLikelihoodId = row.OriginalLikelihoodId,
        originalLikelihood = row.OriginalLikelihood,
        inherentScore = row.InherentScore,
        currentImpactId = row.CurrentImpactId,
        currentImpact = row.CurrentImpact,
        currentLikelihoodId = row.CurrentLikelihoodId,
        currentLikelihood = row.CurrentLikelihood,
        currentScore = row.CurrentScore,
        residualImpactId = row.ResidualImpactId,
        residualImpact = row.ResidualImpact,
        residualLikelihoodId = row.ResidualLikelihoodId,
        residualLikelihood = row.ResidualLikelihood,
        residualScore = row.ResidualScore,
        toleranceImpactId = row.ToleranceImpactId,
        toleranceImpact = row.ToleranceImpact,
        toleranceLikelihoodId = row.ToleranceLikelihoodId,
        toleranceLikelihood = row.ToleranceLikelihood,
        toleranceScore = row.ToleranceScore,
        proximityId = row.ProximityId,
        proximity = row.Proximity,
        createdDate = row.CreatedAt.ToString("dd MMM yy"),
        updatedAt = row.UpdatedAt.ToString("dd MMM yy HH:mm"),
        updatedAtIso = row.UpdatedAt.ToString("o"),
        mitigationCount = row.MitigationCount,
        kriCount = row.KriCount,
        commentCount = row.CommentCount,
        lastCommentUpdateText = row.LastCommentUpdateText,
        lastCommentUpdateKind = row.LastCommentUpdateKind,
        lastCommentUpdateAt = row.LastCommentUpdateAt,
        relation = new
        {
            relationKind = row.RelationKind,
            relationTarget = row.RelationTarget,
            associationUiKind = row.AssociationUiKind,
            projectId = row.RelationProjectId,
            primaryProductId = row.PrimaryProductId,
            relationSourceLabel = row.RelationSourceLabel,
            relationRelatedTitle = row.RelationRelatedTitle,
            relationRelatedDescription = row.RelationRelatedDescription,
            relationLinkHref = row.RelationLinkHref
        }
    };

    public class TrackInRegisterRequest
    {
        public string Type { get; set; } = string.Empty;
        public int EntityId { get; set; }
    }

    // ── Inline editing API ─────────────────────────────────────────

    public class RaidFieldUpdateRequest
    {
        public string Field { get; set; } = string.Empty;
        public string? Value { get; set; }
    }

    public sealed class RaidAssociationApiRequest
    {
        public string? AssociationKind { get; set; }
        public int? ProjectId { get; set; }
        public int? PrimaryProductId { get; set; }
    }

    [HttpPost("api/risk/{id:int}/association")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiRiskAssociationUpdate(
        int id,
        [FromBody] RaidAssociationApiRequest req,
        CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        var risk = await _db.Risks
            .Include(r => r.Project)
            .Include(r => r.PrimaryProduct)
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, ct);
        if (risk == null)
            return NotFound(new { error = "Risk not found" });

        ModelState.Clear();
        var bind = await TryBindRaidAssociationAsync(
            req.AssociationKind,
            req.ProjectId,
            req.PrimaryProductId,
            nameof(ModernRaidRiskEditorForm.ProjectId),
            nameof(ModernRaidRiskEditorForm.PrimaryProductId),
            ct);

        if (bind is not { } a)
        {
            var msg = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault()
                ?? "Select a valid relation.";
            return BadRequest(new { error = msg });
        }

        risk.ProjectId = a.ProjectId;
        risk.PrimaryProductId = a.PrimaryProductId;
        risk.RaidAssociationKind = a.StoredKind;
        risk.UpdatedAt = DateTime.UtcNow;
        risk.UpdatedByUserId = userId;
        await _db.SaveChangesAsync(ct);

        return Json(await BuildRiskRelationApiPayloadAsync(risk, ct));
    }

    [HttpPost("api/issue/{id:int}/association")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiIssueAssociationUpdate(
        int id,
        [FromBody] RaidAssociationApiRequest req,
        CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        var issue = await _db.Issues
            .Include(i => i.Project)
            .Include(i => i.PrimaryProduct)
            .FirstOrDefaultAsync(i => i.Id == id && !i.IsDeleted, ct);
        if (issue == null)
            return NotFound(new { error = "Issue not found" });

        ModelState.Clear();
        var bind = await TryBindRaidAssociationAsync(
            req.AssociationKind,
            req.ProjectId,
            req.PrimaryProductId,
            nameof(ModernRaidIssueEditorForm.ProjectId),
            nameof(ModernRaidIssueEditorForm.PrimaryProductId),
            ct);

        if (bind is not { } a)
        {
            var msg = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault()
                ?? "Select a valid relation.";
            return BadRequest(new { error = msg });
        }

        issue.ProjectId = a.ProjectId;
        issue.PrimaryProductId = a.PrimaryProductId;
        issue.RaidAssociationKind = a.StoredKind;
        issue.UpdatedAt = DateTime.UtcNow;
        issue.UpdatedByUserId = userId;
        await _db.SaveChangesAsync(ct);

        return Json(await BuildIssueRelationApiPayloadAsync(issue, ct));
    }

    [HttpPost("api/assumption/{id:int}/association")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiAssumptionAssociationUpdate(
        int id,
        [FromBody] RaidAssociationApiRequest req,
        CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        var assumption = await _db.Assumptions
            .Include(a => a.Project)
            .Include(a => a.PrimaryProduct)
            .FirstOrDefaultAsync(a => a.Id == id && !a.IsDeleted, ct);
        if (assumption == null)
            return NotFound(new { error = "Assumption not found" });

        ModelState.Clear();
        var bind = await TryBindRaidAssociationAsync(
            req.AssociationKind,
            req.ProjectId,
            req.PrimaryProductId,
            nameof(ModernRaidCreateAssumptionForm.ProjectId),
            nameof(ModernRaidCreateAssumptionForm.PrimaryProductId),
            ct);

        if (bind is not { } a)
        {
            var msg = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault()
                ?? "Select a valid relation.";
            return BadRequest(new { error = msg });
        }

        assumption.ProjectId = a.ProjectId;
        assumption.PrimaryProductId = a.PrimaryProductId;
        assumption.RaidAssociationKind = a.StoredKind;
        assumption.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Json(await BuildAssumptionRelationApiPayloadAsync(assumption, ct));
    }

    private async Task EnsureRiskRelationNavigationsAsync(Risk risk, CancellationToken ct)
    {
        if (risk.ProjectId is int projectId && risk.Project == null)
            risk.Project = await _db.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (risk.PrimaryProductId is int productId && risk.PrimaryProduct == null)
            risk.PrimaryProduct = await _db.Services.AsNoTracking()
                .FirstOrDefaultAsync(p => p.ServiceId == productId, ct);
    }

    private async Task EnsureIssueRelationNavigationsAsync(Issue issue, CancellationToken ct)
    {
        if (issue.ProjectId is int projectId && issue.Project == null)
            issue.Project = await _db.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (issue.PrimaryProductId is int productId && issue.PrimaryProduct == null)
            issue.PrimaryProduct = await _db.Services.AsNoTracking()
                .FirstOrDefaultAsync(p => p.ServiceId == productId, ct);
    }

    private async Task EnsureAssumptionRelationNavigationsAsync(Assumption assumption, CancellationToken ct)
    {
        if (assumption.ProjectId is int projectId && assumption.Project == null)
            assumption.Project = await _db.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (assumption.PrimaryProductId is int productId && assumption.PrimaryProduct == null)
            assumption.PrimaryProduct = await _db.Services.AsNoTracking()
                .FirstOrDefaultAsync(p => p.ServiceId == productId, ct);
    }

    private async Task<object> BuildRiskRelationApiPayloadAsync(Risk risk, CancellationToken ct)
    {
        await EnsureRiskRelationNavigationsAsync(risk, ct);

        var fipsIds = risk.PrimaryProduct != null
            ? new[] { risk.PrimaryProduct.FipsId }
            : Array.Empty<string>();
        var cmdb = await RaidRegisterRelationEnrichment.LoadCmdbProductsByFipsIdAsync(_db, fipsIds, ct);
        var rel = RaidRegisterRelationEnrichment.EnrichRiskRelation(risk, Url, "risks", cmdb);
        return MapRelationToApiPayload(risk.RaidAssociationKind, risk.ProjectId, risk.PrimaryProductId, rel);
    }

    private async Task<object> BuildIssueRelationApiPayloadAsync(Issue issue, CancellationToken ct)
    {
        await EnsureIssueRelationNavigationsAsync(issue, ct);

        var fipsIds = issue.PrimaryProduct != null
            ? new[] { issue.PrimaryProduct.FipsId }
            : Array.Empty<string>();
        var cmdb = await RaidRegisterRelationEnrichment.LoadCmdbProductsByFipsIdAsync(_db, fipsIds, ct);
        var rel = RaidRegisterRelationEnrichment.EnrichIssueRelation(issue, Url, "issues", cmdb);
        return MapRelationToApiPayload(issue.RaidAssociationKind, issue.ProjectId, issue.PrimaryProductId, rel);
    }

    private async Task<object> BuildAssumptionRelationApiPayloadAsync(Assumption assumption, CancellationToken ct)
    {
        await EnsureAssumptionRelationNavigationsAsync(assumption, ct);

        var fipsIds = assumption.PrimaryProduct != null
            ? new[] { assumption.PrimaryProduct.FipsId }
            : Array.Empty<string>();
        var cmdb = await RaidRegisterRelationEnrichment.LoadCmdbProductsByFipsIdAsync(_db, fipsIds, ct);
        var rel = RaidRegisterRelationEnrichment.EnrichAssumptionRelation(assumption, Url, cmdb);
        return MapRelationToApiPayload(assumption.RaidAssociationKind, assumption.ProjectId, assumption.PrimaryProductId, rel);
    }

    private static object MapRelationToApiPayload(
        string? storedKind,
        int? projectId,
        int? primaryProductId,
        RaidRegisterRelationParts rel) =>
        new
        {
            relationKind = rel.Kind,
            relationTarget = rel.Target,
            associationUiKind = ToRaidAssociationUiKind(storedKind, projectId, primaryProductId),
            projectId,
            primaryProductId,
            relationSourceLabel = rel.SourceLabel,
            relationRelatedTitle = rel.RelatedTitle,
            relationRelatedDescription = rel.RelatedDescription,
            relationLinkHref = rel.LinkHref
        };

    [HttpPost("api/risk/{id:int}/update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiRiskUpdate(int id, [FromBody] RaidFieldUpdateRequest req, CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        var risk = await _db.Risks
            .Include(r => r.Likelihood)
            .Include(r => r.ImpactLevel)
            .Include(r => r.CurrentLikelihood)
            .Include(r => r.CurrentImpactLevel)
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, ct);
        if (risk == null) return NotFound(new { error = "Risk not found" });

        var field = req.Field?.ToLower();
        int? intVal = int.TryParse(req.Value, out var iv) ? iv : null;

        switch (field)
        {
            case "title":
                risk.Title = req.Value ?? risk.Title;
                break;
            case "description":
                risk.Description = req.Value;
                break;
            case "statusid":
                risk.RiskStatusId = intVal;
                await RaidRiskClosure.SyncClosedDateFromStatusIdAsync(_db, risk, ct);
                break;
            case "priorityid":
                risk.RiskPriorityId = intVal;
                break;
            case "categoryid":
                risk.RiskCategoryId = intVal;
                break;
            case "proximityid":
                risk.RiskProximityId = intVal;
                break;
            case "owneruserid":
                risk.OwnerUserId = intVal;
                break;
            case "responsestrategy":
                risk.ResponseStrategy = req.Value;
                break;

            // Original rating (only settable if not yet set — Central Ops may correct)
            case "originalimpactid":
                if (!risk.RiskImpactLevelId.HasValue)
                    risk.RiskImpactLevelId = intVal;
                else if (await CanEditLockedInherentRatingsAsync(ct))
                    risk.RiskImpactLevelId = intVal;
                else
                    return BadRequest(new { error = "Inherent impact cannot be changed once set. Contact Central Operations if this is wrong." });
                break;
            case "originallikelihoodid":
                if (!risk.RiskLikelihoodId.HasValue)
                    risk.RiskLikelihoodId = intVal;
                else if (await CanEditLockedInherentRatingsAsync(ct))
                    risk.RiskLikelihoodId = intVal;
                else
                    return BadRequest(new { error = "Inherent likelihood cannot be changed once set. Contact Central Operations if this is wrong." });
                break;

            // Current rating (tracks history)
            case "currentimpactid":
                await RecordRatingChangeIfNeeded(risk, "Current", userId, ct);
                risk.CurrentImpactLevelId = intVal;
                break;
            case "currentlikelihoodid":
                await RecordRatingChangeIfNeeded(risk, "Current", userId, ct);
                risk.CurrentLikelihoodId = intVal;
                break;

            // Residual rating
            case "residualimpactid":
                risk.ResidualImpactLevelId = intVal;
                break;
            case "residuallikelihoodid":
                risk.ResidualLikelihoodId = intVal;
                break;

            // Tolerance rating
            case "toleranceimpactid":
                risk.ToleranceImpactLevelId = intVal;
                break;
            case "tolerancelikelihoodid":
                risk.ToleranceLikelihoodId = intVal;
                break;

            case "cause":
                risk.Cause = req.Value;
                break;
            case "impactifrealised":
                risk.ImpactIfRealised = req.Value;
                break;
            case "contingency":
                risk.Contingency = req.Value;
                break;
            case "assurance":
                risk.Assurance = req.Value;
                break;
            case "financialimpact":
                risk.FinancialImpact = req.Value;
                break;
            case "response":
                risk.ResponseStrategy = req.Value;
                break;
            case "tierid":
                if (!intVal.HasValue)
                    return BadRequest(new { error = "Select a tier." });
                var activeTiers = await _db.RiskTiers.AsNoTracking()
                    .Where(x => x.IsActive)
                    .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
                    .ToListAsync(ct);
                var spreadsheetRows = RiskTierSpreadsheet.ResolveRows(activeTiers);
                if (!RiskTierSpreadsheet.AllowedSpreadsheetTierIds(spreadsheetRows).Contains(intVal.Value))
                    return BadRequest(new { error = "Only Tier 3, Tier 2 Proposed, or Tier 1 Proposed can be set from the register. Operational Tier 2 and Tier 1 are assigned after Operations review." });
                risk.RiskTierId = intVal;
                break;

            default:
                return BadRequest(new { error = $"Unknown field: {field}" });
        }

        await RecalculateRiskScores(risk, ct);
        var ratingField = field is "residualimpactid" or "residuallikelihoodid"
            or "toleranceimpactid" or "tolerancelikelihoodid";
        if (ratingField && RaidRiskScoreRules.ResidualExceedsTolerance(risk.ResidualScore, risk.ToleranceScore))
            return BadRequest(new { error = RaidRiskScoreRules.ResidualAboveToleranceMessage });

        risk.UpdatedAt = DateTime.UtcNow;
        risk.UpdatedByUserId = userId;
        await _db.SaveChangesAsync(ct);

        return Json(new
        {
            success = true,
            inherentScore = risk.InherentScore,
            currentScore = risk.CurrentScore,
            residualScore = risk.ResidualScore,
            toleranceScore = risk.ToleranceScore,
            updatedAt = risk.UpdatedAt.ToString("dd MMM yy HH:mm")
        });
    }

    [HttpPost("api/issue/{id:int}/update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiIssueUpdate(int id, [FromBody] RaidFieldUpdateRequest req, CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        var issue = await _db.Issues.FirstOrDefaultAsync(i => i.Id == id && !i.IsDeleted, ct);
        if (issue == null) return NotFound(new { error = "Issue not found" });

        var field = req.Field?.ToLower();
        int? intVal = int.TryParse(req.Value, out var iv) ? iv : null;

        switch (field)
        {
            case "title":
                issue.Title = req.Value ?? issue.Title;
                break;
            case "description":
                issue.Description = req.Value;
                break;
            case "statusid":
                issue.StatusId = intVal;
                break;
            case "priorityid":
                issue.PriorityId = intVal;
                break;
            case "severityid":
                issue.SeverityId = intVal;
                break;
            case "categoryid":
                issue.IssueCategoryId = intVal;
                break;
            case "owneruserid":
                issue.OwnerUserId = intVal;
                break;
            case "targetresolutiondate":
                if (DateTime.TryParse(req.Value, out var dt))
                    issue.TargetResolutionDate = dt;
                else
                    issue.TargetResolutionDate = null;
                break;
            default:
                return BadRequest(new { error = $"Unknown field: {field}" });
        }

        issue.UpdatedAt = DateTime.UtcNow;
        issue.UpdatedByUserId = userId;
        await _db.SaveChangesAsync(ct);

        return Json(new { success = true, updatedAt = issue.UpdatedAt.ToString("dd MMM yy HH:mm") });
    }

    [HttpPost("api/nearmiss/{id:int}/update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiNearMissUpdate(int id, [FromBody] RaidFieldUpdateRequest req, CancellationToken ct = default)
    {
        var nearMiss = await _db.NearMisses.FirstOrDefaultAsync(n => n.Id == id && !n.IsDeleted, ct);
        if (nearMiss == null) return NotFound(new { error = "Near miss not found" });

        var field = req.Field?.ToLower();
        int? intVal = int.TryParse(req.Value, out var iv) ? iv : null;

        switch (field)
        {
            case "impact":
                nearMiss.Impact = req.Value;
                break;
            case "statusid":
                nearMiss.NearMissStatusId = intVal;
                break;
            case "seriousnessid":
                nearMiss.NearMissSeriousnessId = intVal;
                break;
            case "typeid":
                nearMiss.NearMissTypeId = intVal;
                break;
            default:
                return BadRequest(new { error = $"Unknown field: {field}" });
        }

        nearMiss.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Json(new { success = true, updatedAt = nearMiss.UpdatedAt.ToString("dd MMM yy HH:mm") });
    }

    [HttpPost("api/assumption/{id:int}/update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiAssumptionUpdate(int id, [FromBody] RaidFieldUpdateRequest req, CancellationToken ct = default)
    {
        var assumption = await _db.Assumptions.FirstOrDefaultAsync(a => a.Id == id && !a.IsDeleted, ct);
        if (assumption == null) return NotFound(new { error = "Assumption not found" });

        var field = req.Field?.ToLower();
        int? intVal = int.TryParse(req.Value, out var iv) ? iv : null;

        switch (field)
        {
            case "description":
                if (string.IsNullOrWhiteSpace(req.Value))
                    return BadRequest(new { error = "Description is required" });
                assumption.Description = req.Value!.Trim();
                break;
            case "assumptionstatusid":
                assumption.AssumptionStatusId = intVal;
                break;
            case "assumptioncriticalityid":
                assumption.AssumptionCriticalityId = intVal;
                break;
            case "owneruserid":
                assumption.OwnerUserId = intVal;
                break;
            default:
                return BadRequest(new { error = $"Unknown field: {field}" });
        }

        assumption.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Json(new { success = true, updatedAt = assumption.UpdatedAt.ToString("dd MMM yy HH:mm") });
    }

    public class InlineRiskCreateRequest
    {
        public string Title { get; set; } = string.Empty;
    }

    [HttpPost("api/register/{registerId:int}/risk/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiInlineCreateRisk(int registerId, [FromBody] InlineRiskCreateRequest req, CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        if (!userId.HasValue)
            return Unauthorized(new { error = "Not signed in" });

        var register = await _db.RaidRegisters
            .Include(r => r.Users)
            .FirstOrDefaultAsync(r => r.Id == registerId && !r.IsDeleted, ct);
        if (register == null) return NotFound(new { error = "Register not found" });
        if (!IsRegisterOwnerOrManager(register, userId))
            return Forbid();

        var title = req.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(new { error = "Title is required" });

        var openStatus = await _db.RiskStatuses.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Code == "OPEN", ct);

        var tier3 = await _db.RiskTiers.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Code == "TIER3" && t.IsActive && !t.IsProposedTier, ct);

        var risk = new Risk
        {
            Title = title,
            RiskStatusId = openStatus?.Id,
            RiskTierId = tier3?.Id,
            RaidAssociationKind = RaidAssociationKinds.Organisation,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            OwnerUserId = userId,
            CreatedByUserId = userId,
            UpdatedByUserId = userId
        };

        _db.Risks.Add(risk);
        await _db.SaveChangesAsync(ct);

        _db.RaidRegisterRisks.Add(new RaidRegisterRisk
        {
            RaidRegisterId = registerId,
            RiskId = risk.Id,
            AddedAt = DateTime.UtcNow,
            AddedByUserId = userId
        });

        register.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var ownerName = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => u.Name ?? u.Email)
            .FirstOrDefaultAsync(ct) ?? "—";

        var relation = await BuildRiskRelationApiPayloadAsync(risk, ct);

        return Json(new
        {
            success = true,
            id = risk.Id,
            reference = $"R-{risk.Id:D4}",
            title = risk.Title,
            status = openStatus?.Label ?? "Open",
            statusId = openStatus?.Id,
            tier = tier3?.Name ?? "Tier 3",
            tierId = tier3?.Id,
            owner = ownerName,
            ownerUserId = userId,
            createdDate = risk.CreatedAt.ToString("dd MMM yy"),
            updatedAt = risk.UpdatedAt.ToString("dd MMM yy HH:mm"),
            updatedAtIso = risk.UpdatedAt.ToString("o"),
            relation,
            mitigationCount = 0,
            kriCount = 0
        });
    }

    [HttpGet("api/risk/{riskId:int}/comment-timeline")]
    public async Task<IActionResult> ApiRiskCommentTimeline(int riskId, CancellationToken ct = default)
    {
        var exists = await _db.Risks.AsNoTracking()
            .AnyAsync(r => r.Id == riskId && !r.IsDeleted, ct);
        if (!exists)
            return NotFound(new { error = "Risk not found" });

        var items = await RiskCommentTimelineBuilder.BuildTimelineJsonAsync(_db, riskId, ct);
        return Json(items);
    }

    [HttpGet("api/risk/{riskId:int}/mitigations")]
    public async Task<IActionResult> ApiRiskMitigationsList(int riskId, CancellationToken ct = default)
    {
        var risk = await _db.Risks.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == riskId && !r.IsDeleted, ct);
        if (risk == null) return NotFound(new { error = "Risk not found" });

        var actions = await RaidRiskMitigations.LoadForRiskAsync(_db, riskId, ct);
        var mitigations = actions.Select(a => MapMitigationApiItem(a)).ToList();

        return Json(new { riskId, reference = $"R-{riskId:D4}", title = risk.Title, count = mitigations.Count, mitigations });
    }

    public class ApiMitigationAddRequest
    {
        public string? Title { get; set; }
        public int? AssignedToUserId { get; set; }
        public string? TargetDate { get; set; }
    }

    [HttpPost("api/risk/{riskId:int}/mitigations")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiRiskMitigationAdd(int riskId, [FromBody] ApiMitigationAddRequest req, CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        if (!userId.HasValue)
            return Unauthorized(new { error = "Not signed in" });

        var risk = await _db.Risks.FirstOrDefaultAsync(r => r.Id == riskId && !r.IsDeleted, ct);
        if (risk == null) return NotFound(new { error = "Risk not found" });

        var title = RaidFieldLimits.NormalizeNarrative(req.Title) ?? "";
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(new { error = "Enter the mitigation action." });
        if (req.AssignedToUserId is null or <= 0)
            return BadRequest(new { error = "Select an owner." });
        if (!DateTime.TryParse(req.TargetDate, out var targetDate))
            return BadRequest(new { error = "Enter a valid target date." });

        var ownerUser = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == req.AssignedToUserId.Value, ct);
        if (ownerUser == null)
            return BadRequest(new { error = "Select a valid owner." });

        var mitTypeId = await _db.ActionTypes.AsNoTracking()
            .Where(t => t.IsActive && t.Code == "MIT")
            .Select(t => (int?)t.Id)
            .FirstOrDefaultAsync(ct);

        var entity = new Models.Action
        {
            Title = title,
            RiskId = riskId,
            ProjectId = risk.ProjectId,
            PrimaryProductId = risk.PrimaryProductId,
            AssignedToUserId = req.AssignedToUserId,
            AssignedToEmail = ownerUser.Email,
            DueDate = targetDate.Date,
            Status = MitigationStatuses.NotStarted,
            ActionTypeId = mitTypeId,
            SourceType = "risk_mitigation",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Actions.Add(entity);
        await _db.SaveChangesAsync(ct);

        _db.RiskActions.Add(new RiskAction { RiskId = riskId, ActionId = entity.Id });
        risk.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var count = await RaidRiskMitigations.CountForRiskAsync(_db, riskId, ct);

        return Json(new
        {
            success = true,
            mitigationCount = count,
            mitigation = MapMitigationApiItem(entity, ownerUser.Name ?? ownerUser.Email)
        });
    }

    public class ApiMitigationUpdateRequest
    {
        public string? Title { get; set; }
        public int? AssignedToUserId { get; set; }
        public string? TargetDate { get; set; }
        public string? Status { get; set; }
        public string? UpdateNote { get; set; }
    }

    [HttpPost("api/risk/{riskId:int}/mitigations/{actionId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiRiskMitigationUpdate(int riskId, int actionId, [FromBody] ApiMitigationUpdateRequest req, CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        if (!userId.HasValue)
            return Unauthorized(new { error = "Not signed in" });

        var mitigationAction = await RaidRiskMitigations.FindForRiskAsync(
            _db, riskId, actionId, tracking: true, ct);
        if (mitigationAction == null)
            return NotFound(new { error = "Mitigation not found" });

        var title = RaidFieldLimits.NormalizeNarrative(req.Title) ?? "";
        var normalizedStatus = NormalizeMitigationInputStatus(req.Status);
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(new { error = "Enter the mitigation action." });
        if (req.AssignedToUserId is null or <= 0)
            return BadRequest(new { error = "Select an owner." });
        if (!MitigationStatuses.Canonical.Contains(normalizedStatus))
            return BadRequest(new { error = "Select a valid status." });
        if (!DateTime.TryParse(req.TargetDate, out var parsedTargetDate))
            return BadRequest(new { error = "Enter a valid target date." });

        var ownerUser = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == req.AssignedToUserId.Value, ct);
        if (ownerUser == null)
            return BadRequest(new { error = "Select a valid owner." });

        mitigationAction.Title = title;
        mitigationAction.AssignedToUserId = req.AssignedToUserId;
        mitigationAction.AssignedToEmail = ownerUser.Email;
        mitigationAction.DueDate = parsedTargetDate.Date;
        mitigationAction.Status = normalizedStatus;
        mitigationAction.UpdatedAt = DateTime.UtcNow;
        if (normalizedStatus == MitigationStatuses.Complete && mitigationAction.CompletedDate == null)
            mitigationAction.CompletedDate = DateTime.UtcNow.Date;
        if (normalizedStatus != MitigationStatuses.Complete)
            mitigationAction.CompletedDate = null;

        var note = RaidFieldLimits.NormalizeNarrative(req.UpdateNote);
        if (!string.IsNullOrEmpty(note))
        {
            mitigationAction.Notes = AppendMitigationAuditLine(mitigationAction.Notes, note);
        }

        await RaidRiskMitigations.EnsureJunctionAsync(_db, riskId, actionId, ct);

        var risk = await _db.Risks.FirstOrDefaultAsync(r => r.Id == riskId && !r.IsDeleted, ct);
        if (risk != null)
            risk.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Json(new
        {
            success = true,
            mitigation = MapMitigationApiItem(mitigationAction, ownerUser.Name ?? ownerUser.Email)
        });
    }

    private static object MapMitigationApiItem(Models.Action a, string? ownerOverride = null)
    {
        var owner = ownerOverride;
        if (string.IsNullOrEmpty(owner))
            owner = a.AssignedToUser != null ? (a.AssignedToUser.Name ?? a.AssignedToUser.Email) : a.AssignedToEmail;
        return new
        {
            id = a.Id,
            title = a.Title,
            status = a.Status,
            effectiveStatus = MitigationEffectiveStatusForUi(a),
            dueDate = a.DueDate?.ToString("yyyy-MM-dd"),
            dueDateDisplay = a.DueDate?.ToString("d MMM yyyy"),
            assignedToUserId = a.AssignedToUserId,
            owner,
            notes = a.Notes
        };
    }

    [HttpGet("api/risk/{riskId:int}/kris")]
    public async Task<IActionResult> ApiRiskKrisList(int riskId, CancellationToken ct = default)
    {
        var risk = await _db.Risks.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == riskId && !r.IsDeleted, ct);
        if (risk == null) return NotFound(new { error = "Risk not found" });

        var kris = await _db.RiskKeyRiskIndicators.AsNoTracking()
            .Where(k => k.RiskId == riskId)
            .OrderBy(k => k.SortOrder)
            .ThenBy(k => k.Id)
            .ToListAsync(ct);

        var items = kris.Select(MapKriApiItem).ToList();
        return Json(new
        {
            riskId,
            reference = $"R-{riskId:D4}",
            title = risk.Title,
            count = items.Count,
            krisSummary = BuildKrisSummaryFromRows(kris),
            kris = items
        });
    }

    public class ApiKriSaveRequest
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Metric { get; set; }
        public string? Threshold { get; set; }
    }

    [HttpPost("api/risk/{riskId:int}/kris")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiRiskKriAdd(int riskId, [FromBody] ApiKriSaveRequest req, CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        if (!userId.HasValue)
            return Unauthorized(new { error = "Not signed in" });

        var risk = await _db.Risks.FirstOrDefaultAsync(r => r.Id == riskId && !r.IsDeleted, ct);
        if (risk == null) return NotFound(new { error = "Risk not found" });

        if (!TryNormalizeKriInput(req, out var title, out var description, out var metric, out var threshold, out var error))
            return BadRequest(new { error });

        var maxOrder = await _db.RiskKeyRiskIndicators
            .Where(x => x.RiskId == riskId)
            .Select(x => (int?)x.SortOrder)
            .MaxAsync(ct) ?? 0;
        var now = DateTime.UtcNow;

        var entity = new RiskKeyRiskIndicator
        {
            RiskId = riskId,
            Title = title,
            Description = description,
            Metric = metric,
            Threshold = threshold,
            SortOrder = maxOrder + 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.RiskKeyRiskIndicators.Add(entity);
        risk.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var allKris = await LoadKrisForRiskAsync(riskId, ct);
        return Json(new
        {
            success = true,
            kriCount = allKris.Count,
            krisSummary = BuildKrisSummaryFromRows(allKris),
            kri = MapKriApiItem(entity)
        });
    }

    [HttpPost("api/risk/{riskId:int}/kris/{kriId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiRiskKriUpdate(int riskId, int kriId, [FromBody] ApiKriSaveRequest req, CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        if (!userId.HasValue)
            return Unauthorized(new { error = "Not signed in" });

        var entity = await _db.RiskKeyRiskIndicators
            .FirstOrDefaultAsync(k => k.Id == kriId && k.RiskId == riskId, ct);
        if (entity == null)
            return NotFound(new { error = "Key risk indicator not found" });

        if (!TryNormalizeKriInput(req, out var title, out var description, out var metric, out var threshold, out var error))
            return BadRequest(new { error });

        entity.Title = title;
        entity.Description = description;
        entity.Metric = metric;
        entity.Threshold = threshold;
        entity.UpdatedAt = DateTime.UtcNow;

        var risk = await _db.Risks.FirstOrDefaultAsync(r => r.Id == riskId && !r.IsDeleted, ct);
        if (risk != null)
            risk.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var allKris = await LoadKrisForRiskAsync(riskId, ct);
        return Json(new
        {
            success = true,
            kriCount = allKris.Count,
            krisSummary = BuildKrisSummaryFromRows(allKris),
            kri = MapKriApiItem(entity)
        });
    }

    [HttpPost("api/risk/{riskId:int}/kris/{kriId:int}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiRiskKriRemove(int riskId, int kriId, CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        if (!userId.HasValue)
            return Unauthorized(new { error = "Not signed in" });

        var entity = await _db.RiskKeyRiskIndicators
            .FirstOrDefaultAsync(k => k.Id == kriId && k.RiskId == riskId, ct);
        if (entity == null)
            return NotFound(new { error = "Key risk indicator not found" });

        _db.RiskKeyRiskIndicators.Remove(entity);

        var risk = await _db.Risks.FirstOrDefaultAsync(r => r.Id == riskId && !r.IsDeleted, ct);
        if (risk != null)
            risk.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var allKris = await LoadKrisForRiskAsync(riskId, ct);
        return Json(new
        {
            success = true,
            kriCount = allKris.Count,
            krisSummary = BuildKrisSummaryFromRows(allKris)
        });
    }

    private async Task<List<RiskKeyRiskIndicator>> LoadKrisForRiskAsync(int riskId, CancellationToken ct) =>
        await _db.RiskKeyRiskIndicators.AsNoTracking()
            .Where(k => k.RiskId == riskId)
            .OrderBy(k => k.SortOrder)
            .ThenBy(k => k.Id)
            .ToListAsync(ct);

    private static object MapKriApiItem(RiskKeyRiskIndicator k) => new
    {
        id = k.Id,
        title = k.Title,
        description = k.Description,
        metric = k.Metric,
        threshold = k.Threshold,
        summary = FormatKriSpreadsheetSummary(k.Metric, k.Threshold)
    };

    private static string BuildKrisSummaryFromRows(IEnumerable<RiskKeyRiskIndicator> rows) =>
        string.Join("; ", rows
            .Select(k => FormatKriSpreadsheetSummary(k.Metric, k.Threshold))
            .Where(s => s.Length > 0));

    private static bool TryNormalizeKriInput(
        ApiKriSaveRequest req,
        out string title,
        out string? description,
        out string? metric,
        out string? threshold,
        out string error)
    {
        var titleRaw = (req.Title ?? "").Trim();
        var metricNorm = RaidFieldLimits.NormalizeNarrative(req.Metric);
        var thresholdNorm = RaidFieldLimits.NormalizeNarrative(req.Threshold);
        description = RaidFieldLimits.NormalizeNarrative(req.Description);

        if (string.IsNullOrWhiteSpace(titleRaw) && metricNorm == null && thresholdNorm == null)
        {
            error = "Enter a title, metric, or threshold for the key risk indicator.";
            title = "";
            metric = null;
            threshold = null;
            return false;
        }

        var titleSource = !string.IsNullOrWhiteSpace(titleRaw)
            ? titleRaw
            : metricNorm ?? thresholdNorm ?? "KRI";
        title = TruncateKriField(titleSource, 300);
        metric = metricNorm;
        threshold = thresholdNorm;
        error = "";
        return true;
    }

    private static string TruncateKriField(string value, int max) =>
        value.Length <= max ? value : value[..max];

    public class InlineIssueCreateRequest
    {
        public string Title { get; set; } = string.Empty;
    }

    [HttpPost("api/register/{registerId:int}/issue/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiInlineCreateIssue(int registerId, [FromBody] InlineIssueCreateRequest req, CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        if (!userId.HasValue)
            return Unauthorized(new { error = "Not signed in" });

        var register = await _db.RaidRegisters
            .Include(r => r.Users)
            .FirstOrDefaultAsync(r => r.Id == registerId && !r.IsDeleted, ct);
        if (register == null) return NotFound(new { error = "Register not found" });
        if (!IsRegisterOwnerOrManager(register, userId))
            return Forbid();

        var title = req.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(new { error = "Title is required" });

        var openStatus = await _db.IssueStatuses.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Code == "OPEN", ct);

        var issue = new Issue
        {
            Title = title,
            StatusId = openStatus?.Id,
            RaidAssociationKind = RaidAssociationKinds.Organisation,
            DetectedDate = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            OwnerUserId = userId,
            CreatedByUserId = userId,
            UpdatedByUserId = userId
        };

        _db.Issues.Add(issue);
        await _db.SaveChangesAsync(ct);

        _db.RaidRegisterIssues.Add(new RaidRegisterIssue
        {
            RaidRegisterId = registerId,
            IssueId = issue.Id,
            AddedAt = DateTime.UtcNow,
            AddedByUserId = userId
        });

        register.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var ownerName = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => u.Name ?? u.Email)
            .FirstOrDefaultAsync(ct) ?? "—";

        var relation = await BuildIssueRelationApiPayloadAsync(issue, ct);

        return Json(new
        {
            success = true,
            id = issue.Id,
            reference = $"I-{issue.Id:D4}",
            title = issue.Title,
            status = openStatus?.Label ?? "Open",
            statusId = openStatus?.Id,
            owner = ownerName,
            ownerUserId = userId,
            identifiedDate = issue.DetectedDate.ToString("dd MMM yy"),
            updatedAt = issue.UpdatedAt.ToString("dd MMM yy HH:mm"),
            updatedAtIso = issue.UpdatedAt.ToString("o"),
            relation
        });
    }

    public class InlineNearMissCreateRequest
    {
        public string Title { get; set; } = string.Empty;
    }

    [HttpPost("api/register/{registerId:int}/nearmiss/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiInlineCreateNearMiss(int registerId, [FromBody] InlineNearMissCreateRequest req, CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        if (!userId.HasValue)
            return Unauthorized(new { error = "Not signed in" });

        var register = await _db.RaidRegisters
            .Include(r => r.Users)
            .FirstOrDefaultAsync(r => r.Id == registerId && !r.IsDeleted, ct);
        if (register == null) return NotFound(new { error = "Register not found" });
        if (!IsRegisterOwnerOrManager(register, userId))
            return Forbid();

        var impact = req.Title?.Trim();
        if (string.IsNullOrWhiteSpace(impact))
            return BadRequest(new { error = "Impact description is required" });

        var openStatus = await _db.NearMissStatuses.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Code == "OPEN", ct);

        var now = DateTime.UtcNow;
        var nearMiss = new NearMiss
        {
            Reference = string.Empty,
            DateLogged = now,
            Impact = impact,
            NearMissStatusId = openStatus?.Id,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = userId,
            UpdatedByUserId = userId
        };

        _db.NearMisses.Add(nearMiss);
        await _db.SaveChangesAsync(ct);

        nearMiss.Reference = $"NM-{nearMiss.Id:D4}";
        await _db.SaveChangesAsync(ct);

        _db.RaidRegisterNearMisses.Add(new RaidRegisterNearMiss
        {
            RaidRegisterId = registerId,
            NearMissId = nearMiss.Id,
            AddedAt = now,
            AddedByUserId = userId
        });

        register.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var statusLabel = openStatus?.Label ?? "Open";
        var relation = RaidRegisterRelationEnrichment.EnrichNearMissRelation(nearMiss);

        return Json(new
        {
            success = true,
            id = nearMiss.Id,
            reference = nearMiss.Reference,
            impact,
            status = statusLabel,
            statusId = openStatus?.Id,
            dateLogged = nearMiss.DateLogged.ToString("dd MMM yy"),
            updatedAt = nearMiss.UpdatedAt.ToString("dd MMM yy HH:mm"),
            updatedAtIso = nearMiss.UpdatedAt.ToString("o"),
            relation = MapRelationToApiPayload(null, null, null, relation)
        });
    }

    public class InlineAssumptionCreateRequest
    {
        public string Description { get; set; } = string.Empty;
    }

    [HttpPost("api/register/{registerId:int}/assumption/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApiInlineCreateAssumption(int registerId, [FromBody] InlineAssumptionCreateRequest req, CancellationToken ct = default)
    {
        var userId = await ResolveCurrentUserIdAsync(ct);
        if (!userId.HasValue)
            return Unauthorized(new { error = "Not signed in" });

        var register = await _db.RaidRegisters
            .Include(r => r.Users)
            .FirstOrDefaultAsync(r => r.Id == registerId && !r.IsDeleted, ct);
        if (register == null) return NotFound(new { error = "Register not found" });
        if (!IsRegisterOwnerOrManager(register, userId))
            return Forbid();

        var description = req.Description?.Trim();
        if (string.IsNullOrWhiteSpace(description))
            return BadRequest(new { error = "Description is required" });

        var openStatus = await _db.AssumptionStatuses.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Code == "OPEN", ct);
        var defaultCriticality = await _db.AssumptionCriticalities.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder)
            .FirstOrDefaultAsync(ct);

        var now = DateTime.UtcNow;
        var assumption = new Assumption
        {
            Description = description,
            AssumptionStatusId = openStatus?.Id,
            AssumptionCriticalityId = defaultCriticality?.Id,
            RaidAssociationKind = RaidAssociationKinds.Organisation,
            OwnerUserId = userId,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Assumptions.Add(assumption);
        await _db.SaveChangesAsync(ct);

        _db.RaidRegisterAssumptions.Add(new RaidRegisterAssumption
        {
            RaidRegisterId = registerId,
            AssumptionId = assumption.Id,
            AddedAt = now,
            AddedByUserId = userId
        });

        register.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var ownerName = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => u.Name ?? u.Email)
            .FirstOrDefaultAsync(ct) ?? "—";

        var savedAssumption = await _db.Assumptions.AsNoTracking()
            .Include(a => a.Project)
            .Include(a => a.PrimaryProduct)
            .FirstAsync(a => a.Id == assumption.Id, ct);
        var relation = await BuildAssumptionRelationApiPayloadAsync(savedAssumption, ct);

        return Json(new
        {
            success = true,
            id = assumption.Id,
            reference = $"A-{assumption.Id:D4}",
            description,
            status = openStatus?.Label ?? "Open",
            statusId = openStatus?.Id,
            criticality = defaultCriticality?.Label,
            criticalityId = defaultCriticality?.Id,
            owner = ownerName,
            ownerUserId = userId,
            createdDate = assumption.CreatedAt.ToString("dd MMM yy"),
            updatedAt = assumption.UpdatedAt.ToString("dd MMM yy HH:mm"),
            updatedAtIso = assumption.UpdatedAt.ToString("o"),
            relation
        });
    }

    private async Task RecordRatingChangeIfNeeded(Risk risk, string ratingType, int? userId, CancellationToken ct)
    {
        if (risk.CurrentLikelihoodId.HasValue || risk.CurrentImpactLevelId.HasValue)
        {
            _db.RiskRatingHistory.Add(new RiskRatingHistory
            {
                RiskId = risk.Id,
                RatingType = ratingType,
                LikelihoodId = risk.CurrentLikelihoodId,
                ImpactLevelId = risk.CurrentImpactLevelId,
                Score = risk.CurrentScore,
                ChangedByUserId = userId,
                ChangedAt = DateTime.UtcNow
            });
        }
    }

    private async Task RecalculateRiskScores(Risk risk, CancellationToken ct)
    {
        async Task<decimal?> CalcScore(int? impactId, int? likelihoodId)
        {
            if (!impactId.HasValue || !likelihoodId.HasValue) return null;
            var impact = await _db.RiskImpactLevels.AsNoTracking()
                .Where(x => x.Id == impactId.Value)
                .Select(x => (int?)x.MatrixScore).FirstOrDefaultAsync(ct);
            var likelihood = await _db.RiskLikelihoods.AsNoTracking()
                .Where(x => x.Id == likelihoodId.Value)
                .Select(x => (int?)x.MatrixScore).FirstOrDefaultAsync(ct);
            if (impact.HasValue && likelihood.HasValue)
                return impact.Value * likelihood.Value;
            return null;
        }

        risk.InherentScore = await CalcScore(risk.RiskImpactLevelId, risk.RiskLikelihoodId);
        risk.CurrentScore = await CalcScore(risk.CurrentImpactLevelId, risk.CurrentLikelihoodId);
        risk.ResidualScore = await CalcScore(risk.ResidualImpactLevelId, risk.ResidualLikelihoodId);
        risk.ToleranceScore = await CalcScore(risk.ToleranceImpactLevelId, risk.ToleranceLikelihoodId);

        // On first save, copy original → current if current is empty
        if (!risk.CurrentImpactLevelId.HasValue && risk.RiskImpactLevelId.HasValue)
        {
            risk.CurrentImpactLevelId = risk.RiskImpactLevelId;
            risk.CurrentLikelihoodId = risk.RiskLikelihoodId;
            risk.CurrentScore = risk.InherentScore;
        }
    }

    private static string FormatKriSpreadsheetSummary(string? metric, string? threshold)
    {
        var m = string.IsNullOrWhiteSpace(metric) ? null : metric.Trim();
        var t = string.IsNullOrWhiteSpace(threshold) ? null : threshold.Trim();
        if (m != null && t != null)
            return $"{m} (threshold: {t})";
        return m ?? t ?? "";
    }
}
