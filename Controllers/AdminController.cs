using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Compass.Data;
using Compass.Models;
using Microsoft.AspNetCore.Authorization;
using Compass.Services;
using Compass.ViewModels.Admin;
using Compass.Attributes;
using CsvHelper;
using CsvHelper.Configuration;

namespace Compass.Controllers;

[Authorize]
[RequireAdmin]
public class AdminController : Controller
{
    private readonly CompassDbContext _context;
    private readonly ILogger<AdminController> _logger;
    private readonly IApiTokenService _apiTokenService;
    private readonly IConfiguration _configuration;
    private readonly IProductsApiService _productsApiService;

    private static readonly IReadOnlyList<RaidLookupDefinition> _raidLookupDefinitions = new List<RaidLookupDefinition>
    {
        CreateLookupDefinition<ActionStatus>("action-statuses", "Action statuses", "Workflow states shown on every action."),
        CreateLookupDefinition<ActionPriority>("action-priorities", "Action priorities", "Priority options shared across action listings."),
        CreateLookupDefinition<ActionType>("action-types", "Action types", "Helps teams categorise actions for reporting."),
        CreateLookupDefinition<ActionCategory>("action-categories", "Action categories", "Used to slice actions by category."),
        CreateLookupDefinition<ActionImpactLevel>("action-impact-levels", "Action impact levels", "Impact level choices aligned with RAID reporting."),
        CreateLookupDefinition<ActionReminderFrequency>("action-reminder-frequencies", "Action reminder frequencies", "Determines how often reminders fire for actions."),
        CreateLookupDefinition<ActionEscalationThreshold>("action-escalation-thresholds", "Action escalation thresholds", "Number of days before escalation is triggered."),
        CreateLookupDefinition<IssueStatus>("issue-statuses", "Issue statuses", "Issue workflow states."),
        CreateLookupDefinition<IssuePriority>("issue-priorities", "Issue priorities", "Priority options for issues."),
        CreateLookupDefinition<IssueSeverity>("issue-severities", "Issue severities", "Severity scale mapped to RAID reporting."),
        CreateLookupDefinition<IssueCategory>("issue-categories", "Issue categories", "Issue categorisation used in dashboards."),
        CreateLookupDefinition<DecisionStatus>("decision-statuses", "Decision statuses", "Status values for decisions."),
        CreateLookupDefinition<DecisionPriority>("decision-priorities", "Decision priorities", "Decision priority labels."),
        CreateLookupDefinition<DecisionOutcome>("decision-outcomes", "Decision outcomes", "Possible outcomes recorded when a decision is made."),
        CreateLookupDefinition<DecisionImplementationStatus>("decision-implementation-statuses", "Decision implementation statuses", "Tracks implementation progress."),
        CreateLookupDefinition<RiskStatus>("risk-statuses", "Risk statuses", "Core risk workflow states."),
        CreateLookupDefinition<RiskPriority>("risk-priorities", "Risk priorities", "Priority scale applied to risks."),
        CreateLookupDefinition<RiskLikelihood>("risk-likelihoods", "Risk likelihoods", "Likelihood scale used to calculate scores."),
        CreateLookupDefinition<RiskImpactLevel>("risk-impact-levels", "Risk impact levels", "Impact scale for risks."),
        CreateLookupDefinition<RiskProximity>("risk-proximities", "Risk proximities", "Timeline bands for when a risk may materialise."),
        CreateLookupDefinition<RiskCategory>("risk-categories", "Risk categories", "Categorisation for risk libraries."),
        CreateLookupDefinition<RaidEvidenceType>("raid-evidence-types", "Evidence types", "Shared evidence/documentation types."),
        CreateLookupDefinition<GovernanceBoard>("governance-boards", "Governance boards", "Committees and boards used for RAID escalation."),
        CreateLookupDefinition<DemandRequestStatus>("demand-request-statuses", "Demand request statuses", "Workflow states for demand requests.")
    };

    private static RaidLookupDefinition CreateLookupDefinition<TLookup>(string key, string label, string? description = null)
        where TLookup : RaidLookupBase, new() =>
        new(
            key,
            label,
            ctx => ctx.Set<TLookup>().Cast<RaidLookupBase>(),
            () => new TLookup(),
            description);


    public AdminController(CompassDbContext context, ILogger<AdminController> logger, IApiTokenService apiTokenService, IConfiguration configuration, IProductsApiService productsApiService)
    {
        _context = context;
        _logger = logger;
        _apiTokenService = apiTokenService;
        _configuration = configuration;
        _productsApiService = productsApiService;
    }

    // GET: Admin/Index
    public IActionResult Index()
    {
        return View("~/Views/Admin/Index.cshtml");
    }

    // GET: Admin/ChatbotConversations
    public async Task<IActionResult> ChatbotConversations(int page = 1, int pageSize = 50)
    {
        var totalCount = await _context.ChatConversations.CountAsync();
        var conversations = await _context.ChatConversations
            .Include(c => c.User)
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        ViewBag.TotalCount = totalCount;
        ViewBag.Page = page;
        ViewBag.PageSize = pageSize;
        ViewBag.TotalPages = totalCount > 0 ? (int)Math.Ceiling(totalCount / (double)pageSize) : 1;

        return View(conversations);
    }

    // ==================== RAID SETTINGS ====================

    public async Task<IActionResult> RaidSettings(string? lookupKey = null, int? editId = null)
    {
        var descriptor = ResolveRaidLookupDefinition(lookupKey) ?? _raidLookupDefinitions.First();
        var viewModel = await BuildRaidSettingsViewModelAsync(descriptor);

        if (editId.HasValue)
        {
            var entity = await descriptor.Query(_context)
                .FirstOrDefaultAsync(x => x.Id == editId.Value);

            if (entity == null)
            {
                TempData["ErrorMessage"] = "The selected entry could not be found.";
            }
            else
            {
                viewModel.EditEntry = new RaidLookupEditInputModel
                {
                    Id = entity.Id,
                    LookupKey = descriptor.Key,
                    Code = entity.Code,
                    Label = entity.Label,
                    Description = entity.Description,
                    SortOrder = entity.SortOrder,
                    IsActive = entity.IsActive
                };
            }
        }

        return View("~/Views/Admin/Settings/RaidSettings.cshtml", viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRaidLookup([Bind(Prefix = "NewEntry")] RaidLookupEditInputModel input)
    {
        var descriptor = ResolveRaidLookupDefinition(input.LookupKey) ?? _raidLookupDefinitions.First();

        if (!ModelState.IsValid)
        {
            var invalidViewModel = await BuildRaidSettingsViewModelAsync(descriptor, input);
            ViewData["ActiveRaidModal"] = "create";
            return View("~/Views/Admin/Settings/RaidSettings.cshtml", invalidViewModel);
        }

        var entity = descriptor.Factory();

        entity.Code = input.Code.Trim();
        entity.Label = input.Label.Trim();
        entity.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        entity.SortOrder = input.SortOrder;
        entity.IsActive = input.IsActive;
        entity.CreatedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;

        _context.Add(entity);
        await _context.SaveChangesAsync();

        TempData["SuccessMessage"] = $"Added '{entity.Label}' to {descriptor.Label.ToLowerInvariant()}.";
        return RedirectToAction(nameof(RaidSettings), new { lookupKey = descriptor.Key });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateRaidLookup([Bind(Prefix = "EditEntry")] RaidLookupEditInputModel input)
    {
        if (!input.Id.HasValue)
        {
            TempData["ErrorMessage"] = "Invalid RAID lookup identifier.";
            return RedirectToAction(nameof(RaidSettings));
        }

        var descriptor = ResolveRaidLookupDefinition(input.LookupKey) ?? _raidLookupDefinitions.First();

        if (!ModelState.IsValid)
        {
            var invalidViewModel = await BuildRaidSettingsViewModelAsync(descriptor, null, input);
            ViewData["ActiveRaidModal"] = "edit";
            return View("~/Views/Admin/Settings/RaidSettings.cshtml", invalidViewModel);
        }

        var entity = await descriptor.Query(_context)
            .FirstOrDefaultAsync(x => x.Id == input.Id.Value);

        if (entity == null)
        {
            TempData["ErrorMessage"] = "Unable to find the selected RAID lookup entry.";
            return RedirectToAction(nameof(RaidSettings), new { lookupKey = descriptor.Key });
        }

        entity.Code = input.Code.Trim();
        entity.Label = input.Label.Trim();
        entity.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        entity.SortOrder = input.SortOrder;
        entity.IsActive = input.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        TempData["SuccessMessage"] = $"Updated '{entity.Label}'.";
        return RedirectToAction(nameof(RaidSettings), new { lookupKey = descriptor.Key });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRaidLookup(string lookupKey, int id)
    {
        var descriptor = ResolveRaidLookupDefinition(lookupKey);
        if (descriptor == null)
        {
            TempData["ErrorMessage"] = "Unknown RAID lookup.";
            return RedirectToAction(nameof(RaidSettings));
        }

        var entity = await descriptor.Query(_context)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (entity == null)
        {
            TempData["ErrorMessage"] = "The selected entry could not be found.";
            return RedirectToAction(nameof(RaidSettings), new { lookupKey = descriptor.Key });
        }

        try
        {
            _context.Remove(entity);
            await _context.SaveChangesAsync();
            TempData["SuccessMessage"] = $"Deleted '{entity.Label}'.";
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Failed to delete RAID lookup {LookupKey} {LookupId}", descriptor.Key, id);
            TempData["ErrorMessage"] = "Unable to delete this entry because it is currently in use.";
        }

        return RedirectToAction(nameof(RaidSettings), new { lookupKey = descriptor.Key });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SeedRaidLookupDefaults(string lookupKey)
    {
        var descriptor = ResolveRaidLookupDefinition(lookupKey);
        if (descriptor == null)
        {
            TempData["ErrorMessage"] = "Unknown RAID lookup.";
            return RedirectToAction(nameof(RaidSettings));
        }

        if (!RaidLookupSeedData.TryGetValues(descriptor.Key, out var seeds) || seeds.Count == 0)
        {
            TempData["ErrorMessage"] = "There are no recommended values for this lookup.";
            return RedirectToAction(nameof(RaidSettings), new { lookupKey = descriptor.Key });
        }

        var existingCodes = await descriptor.Query(_context)
            .Select(x => x.Code.ToLower())
            .ToListAsync();

        var itemsToAdd = seeds
            .Where(seed => !existingCodes.Contains(seed.Code.ToLowerInvariant()))
            .ToList();

        if (!itemsToAdd.Any())
        {
            TempData["SuccessMessage"] = "All recommended values already exist for this lookup.";
            return RedirectToAction(nameof(RaidSettings), new { lookupKey = descriptor.Key });
        }

        foreach (var seed in itemsToAdd)
        {
            var entity = descriptor.Factory();
            entity.Code = seed.Code;
            entity.Label = seed.Label;
            entity.Description = seed.Description;
            entity.SortOrder = seed.SortOrder;
            entity.IsActive = true;
            entity.CreatedAt = DateTime.UtcNow;
            entity.UpdatedAt = DateTime.UtcNow;

            _context.Add(entity);
        }

        await _context.SaveChangesAsync();

        TempData["SuccessMessage"] = $"Added {itemsToAdd.Count} recommended value{(itemsToAdd.Count == 1 ? string.Empty : "s")}.";
        return RedirectToAction(nameof(RaidSettings), new { lookupKey = descriptor.Key });
    }

    // GET: Admin/Users
    public async Task<IActionResult> Users()
    {
        var users = await _context.Users
            .OrderBy(u => u.Name)
            .ToListAsync();
        
        return View("~/Views/Admin/User/Users.cshtml", users);
    }

    // GET: Admin/CreateUser
    public IActionResult CreateUser()
    {
        return View("~/Views/Admin/User/CreateUser.cshtml");
    }

    // POST: Admin/CreateUser
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUser(User user)
    {
        if (ModelState.IsValid)
        {
            try
            {
                // Set default role - roles are managed through groups in UserManagement
                user.Role = UserRole.Visitor;
                user.CreatedAt = DateTime.UtcNow;
                user.UpdatedAt = DateTime.UtcNow;
                
                _context.Users.Add(user);
                await _context.SaveChangesAsync();
                
                TempData["SuccessMessage"] = $"User '{user.Name}' has been created successfully. Assign them to groups in User Management to grant permissions.";
                return RedirectToAction(nameof(Users));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating user");
                ModelState.AddModelError("", "An error occurred while creating the user. Please try again.");
            }
        }
        
        return View("~/Views/Admin/User/CreateUser.cshtml", user);
    }

    // ==================== USER SATISFACTION (USS) ADMIN ====================

    public IActionResult UserSatisfaction()
    {
        return View("~/Views/Admin/UserSatisfaction/Index.cshtml");
    }

    public async Task<IActionResult> ResponseScales()
    {
        var scales = await _context.ResponseScales
            .Include(s => s.Options.OrderBy(o => o.Ordinal))
            .OrderBy(s => s.Name)
            .ToListAsync();
        
        ViewBag.Scales = scales;
        return View("~/Views/Admin/UserSatisfaction/ResponseScales.cshtml");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateResponseScale(string name, string? description, SurveyInputType inputType, bool isDefault)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["ErrorMessage"] = "Scale name is required.";
            return RedirectToAction(nameof(ResponseScales));
        }
        
        var scale = new ResponseScale
        {
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            InputType = inputType,
            IsDefault = isDefault,
            CreatedUtc = DateTime.UtcNow
        };
        
        _context.ResponseScales.Add(scale);
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Response scale created.";
        return RedirectToAction(nameof(ResponseScales));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddScaleOption(Guid scaleId, string value, string label, int ordinal)
    {
        if (scaleId == Guid.Empty || string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(label))
        {
            TempData["ErrorMessage"] = "Scale, value and label are required.";
            return RedirectToAction(nameof(ResponseScales));
        }
        
        _context.ResponseScaleOptions.Add(new ResponseScaleOption
        {
            ResponseScaleId = scaleId,
            Value = value.Trim(),
            Label = label.Trim(),
            Ordinal = ordinal,
            Active = true
        });
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Option added.";
        return RedirectToAction(nameof(ResponseScales));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateScaleOption(Guid optionId, string label, int ordinal, bool active)
    {
        var option = await _context.ResponseScaleOptions.FindAsync(optionId);
        if (option == null)
        {
            TempData["ErrorMessage"] = "Option not found.";
            return RedirectToAction(nameof(ResponseScales));
        }
        
        option.Label = label.Trim();
        option.Ordinal = ordinal;
        option.Active = active;
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Option updated.";
        return RedirectToAction(nameof(ResponseScales));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteResponseScale(Guid scaleId)
    {
        var scale = await _context.ResponseScales
            .Include(s => s.Options)
            .FirstOrDefaultAsync(s => s.ResponseScaleId == scaleId);
        
        if (scale == null)
        {
            TempData["ErrorMessage"] = "Scale not found.";
            return RedirectToAction(nameof(ResponseScales));
        }
        
        // Check if any questions use this scale
        var questionsUsingScale = await _context.SurveyQuestions
            .AnyAsync(q => q.ResponseScaleId == scaleId);
        
        if (questionsUsingScale)
        {
            TempData["ErrorMessage"] = "Cannot delete scale as it is in use by questions.";
            return RedirectToAction(nameof(ResponseScales));
        }
        
        _context.ResponseScales.Remove(scale);
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Scale deleted.";
        return RedirectToAction(nameof(ResponseScales));
    }

    public async Task<IActionResult> UserSatisfactionQuestions()
    {
        // Get or create default template
        var template = await _context.SurveyTemplates
            .OrderByDescending(t => t.IsDefault)
            .ThenByDescending(t => t.CreatedUtc)
            .FirstOrDefaultAsync();
        
        if (template == null)
        {
            // Create default template if none exists
            template = new SurveyTemplate
            {
                Name = "Default USS Template",
                Version = 1,
                IsDefault = true,
                CreatedUtc = DateTime.UtcNow
            };
            _context.SurveyTemplates.Add(template);
            await _context.SaveChangesAsync();
        }
        
        var questions = await _context.SurveyQuestions
            .Include(q => q.ResponseScale)
            .Include(q => q.Options.OrderBy(o => o.Ordinal))
            .Where(q => q.SurveyTemplateId == template.SurveyTemplateId)
            .OrderBy(q => q.Ordinal)
            .ToListAsync();
        
        var scales = await _context.ResponseScales
            .Include(s => s.Options.OrderBy(o => o.Ordinal))
            .OrderBy(s => s.Name)
            .ToListAsync();
        
        ViewBag.Questions = questions;
        ViewBag.SelectedTemplateId = template.SurveyTemplateId;
        ViewBag.ResponseScales = scales;
        
        return View("~/Views/Admin/UserSatisfaction/Questions.cshtml");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUssTemplate(string name, int version, bool isDefault)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["ErrorMessage"] = "Template name is required.";
            return RedirectToAction(nameof(UserSatisfactionQuestions));
        }
        var template = new SurveyTemplate
        {
            Name = name.Trim(),
            Version = version > 0 ? version : 1,
            IsDefault = isDefault,
            CreatedUtc = DateTime.UtcNow
        };
        _context.SurveyTemplates.Add(template);
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Template created.";
        return RedirectToAction(nameof(UserSatisfactionQuestions), new { templateId = template.SurveyTemplateId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddUssQuestion(string? templateId, string code, string title, string? description, bool mandatory, int weight, int ordinal, string inputType, string? responseScaleId)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(title))
        {
            TempData["ErrorMessage"] = "Code and title are required.";
            return RedirectToAction(nameof(UserSatisfactionQuestions));
        }
        
        if (!Enum.TryParse<SurveyInputType>(inputType, true, out var inputTypeEnum))
        {
            TempData["ErrorMessage"] = "Invalid input type.";
            return RedirectToAction(nameof(UserSatisfactionQuestions));
        }
        
        // Get or create default template
        Guid templateIdGuid;
        if (string.IsNullOrWhiteSpace(templateId) || !Guid.TryParse(templateId, out templateIdGuid) || templateIdGuid == Guid.Empty)
        {
            var template = await _context.SurveyTemplates
                .OrderByDescending(t => t.IsDefault)
                .ThenByDescending(t => t.CreatedUtc)
                .FirstOrDefaultAsync();
            
            if (template == null)
            {
                // Create default template if none exists
                template = new SurveyTemplate
                {
                    Name = "Default USS Template",
                    Version = 1,
                    IsDefault = true,
                    CreatedUtc = DateTime.UtcNow
                };
                _context.SurveyTemplates.Add(template);
                await _context.SaveChangesAsync();
            }
            templateIdGuid = template.SurveyTemplateId;
        }
        
        Guid? responseScaleGuid = null;
        if (!string.IsNullOrWhiteSpace(responseScaleId) && Guid.TryParse(responseScaleId, out var parsedScaleId) && parsedScaleId != Guid.Empty)
        {
            responseScaleGuid = parsedScaleId;
        }
        
        var question = new SurveyQuestion
        {
            SurveyTemplateId = templateIdGuid,
            Code = code.Trim(),
            Title = title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Mandatory = mandatory,
            Weight = weight,
            Ordinal = ordinal,
            InputType = inputTypeEnum,
            ResponseScaleId = responseScaleGuid,
            Active = true
        };
        _context.SurveyQuestions.Add(question);
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Question added.";
        
        // If input type is Select, redirect with question ID so options can be added
        if (inputTypeEnum == SurveyInputType.Select)
        {
            TempData["NewQuestionId"] = question.SurveyQuestionId.ToString();
        }
        
        return RedirectToAction(nameof(UserSatisfactionQuestions));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateUssQuestion(Guid questionId, string title, string? description, bool mandatory, int weight, int ordinal, SurveyInputType inputType, bool active, string? responseScaleId)
    {
        var q = await _context.SurveyQuestions.FindAsync(questionId);
        if (q == null)
        {
            TempData["ErrorMessage"] = "Question not found.";
            return RedirectToAction(nameof(UserSatisfactionQuestions));
        }
        q.Title = title.Trim();
        q.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        q.Mandatory = mandatory;
        q.Weight = weight;
        q.Ordinal = ordinal;
        q.InputType = inputType;
        q.Active = active;
        
        if (!string.IsNullOrWhiteSpace(responseScaleId) && Guid.TryParse(responseScaleId, out var parsedScaleId) && parsedScaleId != Guid.Empty)
        {
            q.ResponseScaleId = parsedScaleId;
        }
        else
        {
            q.ResponseScaleId = null;
        }
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Question updated.";
        return RedirectToAction(nameof(UserSatisfactionQuestions));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddQuestionOption(Guid questionId, string value, string label, int ordinal, int? score)
    {
        var question = await _context.SurveyQuestions.FindAsync(questionId);
        if (question == null)
        {
            TempData["ErrorMessage"] = "Question not found.";
            return RedirectToAction(nameof(UserSatisfactionQuestions));
        }
        
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(label))
        {
            TempData["ErrorMessage"] = "Value and label are required.";
            return RedirectToAction(nameof(UserSatisfactionQuestions));
        }
        
        _context.SurveyOptions.Add(new SurveyOption
        {
            SurveyQuestionId = questionId,
            Value = value.Trim(),
            Label = label.Trim(),
            Ordinal = ordinal,
            Score = score,
            Active = true
        });
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Option added.";
        return RedirectToAction(nameof(UserSatisfactionQuestions));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateQuestionOption(Guid optionId, string label, int ordinal, int? score, bool active)
    {
        var option = await _context.SurveyOptions.FindAsync(optionId);
        if (option == null)
        {
            TempData["ErrorMessage"] = "Option not found.";
            return RedirectToAction(nameof(UserSatisfactionQuestions));
        }
        
        option.Label = label.Trim();
        option.Ordinal = ordinal;
        option.Score = score;
        option.Active = active;
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Option updated.";
        return RedirectToAction(nameof(UserSatisfactionQuestions));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteQuestionOption(Guid optionId)
    {
        var option = await _context.SurveyOptions.FindAsync(optionId);
        if (option == null)
        {
            TempData["ErrorMessage"] = "Option not found.";
            return RedirectToAction(nameof(UserSatisfactionQuestions));
        }
        
        _context.SurveyOptions.Remove(option);
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Option deleted.";
        return RedirectToAction(nameof(UserSatisfactionQuestions));
    }

    public async Task<IActionResult> UserSatisfactionResponses(string? fipsId = null, DateTime? from = null, DateTime? to = null)
    {
        ViewBag.Services = await _context.Services.OrderBy(s => s.FipsId).ToListAsync();
        var query = _context.SurveyResponses
            .Include(r => r.SurveyInstance)
            .ThenInclude(si => si.Service)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(fipsId))
        {
            query = query.Where(r => r.SurveyInstance != null && 
                                     r.SurveyInstance.Service != null && 
                                     r.SurveyInstance.Service.FipsId == fipsId);
        }
        if (from.HasValue)
        {
            query = query.Where(r => r.SubmittedUtc >= from.Value);
        }
        if (to.HasValue)
        {
            query = query.Where(r => r.SubmittedUtc <= to.Value);
        }

        var list = await query.OrderByDescending(r => r.SubmittedUtc).Take(200).ToListAsync();
        var n = list.Count;
        var avg = n > 0 ? Math.Round(list.Average(r => (double)r.UssComputed), 1) : 0;
        var median = n > 0 ? Math.Round(list.Select(r => (double)r.UssComputed).OrderBy(x => x).ElementAt(n / 2), 1) : 0;
        ViewBag.Summary = new { n, avg, median };
        return View("~/Views/Admin/UserSatisfaction/Responses.cshtml", list);
    }

    // GET: Admin/EditUser/5
    public async Task<IActionResult> EditUser(int? id)
    {
        if (id == null)
        {
            return NotFound();
        }

        var user = await _context.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound();
        }

        return View("~/Views/Admin/User/EditUser.cshtml", user);
    }

    // POST: Admin/EditUser/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditUser(int id, User user)
    {
        if (id != user.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                // Get existing user to preserve Role - roles are managed through groups in UserManagement
                var existingUser = await _context.Users.FindAsync(id);
                if (existingUser == null)
                {
                    return NotFound();
                }

                // Only update Name and Email - Role is managed through groups
                existingUser.Name = user.Name;
                existingUser.Email = user.Email;
                existingUser.UpdatedAt = DateTime.UtcNow;
                
                _context.Update(existingUser);
                await _context.SaveChangesAsync();
                
                TempData["SuccessMessage"] = $"User '{user.Name}' has been updated successfully.";
                return RedirectToAction(nameof(Users));
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!UserExists(user.Id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating user");
                ModelState.AddModelError("", "An error occurred while updating the user. Please try again.");
            }
        }
        
        return View("~/Views/Admin/User/EditUser.cshtml", user);
    }

    // GET: Admin/DeleteUser/5
    public async Task<IActionResult> DeleteUser(int? id)
    {
        if (id == null)
        {
            return NotFound();
        }

        var user = await _context.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound();
        }

        return View("~/Views/Admin/User/DeleteUser.cshtml", user);
    }

    // POST: Admin/DeleteUser/5
    [HttpPost, ActionName("DeleteUser")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteUserConfirmed(int id)
    {
        try
        {
            var user = await _context.Users.FindAsync(id);
            if (user != null)
            {
                _context.Users.Remove(user);
                await _context.SaveChangesAsync();
                
                TempData["SuccessMessage"] = $"User '{user.Name}' has been deleted successfully.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting user");
            TempData["ErrorMessage"] = "An error occurred while deleting the user. Please try again.";
        }

        return RedirectToAction(nameof(Users));
    }

    private bool UserExists(int id)
    {
        return _context.Users.Any(e => e.Id == id);
    }

    // ==================== STRATEGIC OBJECTIVES ====================

    // GET: Admin/Objectives
    public async Task<IActionResult> Objectives()
    {
        var objectives = await _context.Objectives
            .Include(o => o.OwnerUser)
            .Include(o => o.ThemeSroUser)
            .Include(o => o.OutcomeSroUser)
            .Where(o => !o.IsDeleted)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();

        ViewBag.Users = new SelectList(
            await _context.Users.OrderBy(u => u.Name).ToListAsync(),
            "Id",
            "Name"
        );
        
        return View("~/Views/Admin/Objective/Index.cshtml", objectives);
    }

    // GET: Admin/ObjectiveDetails/5
    public async Task<IActionResult> ObjectiveDetails(int? id)
    {
        if (id == null)
        {
            return NotFound();
        }

        var objective = await _context.Objectives
            .Include(o => o.OwnerUser)
            .Include(o => o.ThemeSroUser)
            .Include(o => o.OutcomeSroUser)
            .Include(o => o.Risks.Where(r => !r.IsDeleted))
            .Include(o => o.Issues.Where(i => !i.IsDeleted))
            .Include(o => o.Milestones.Where(m => !m.IsDeleted))
            .Include(o => o.Actions.Where(a => !a.IsDeleted))
            .FirstOrDefaultAsync(m => m.Id == id && !m.IsDeleted);

        if (objective == null)
        {
            return NotFound();
        }

        return View("~/Views/Admin/Objective/Details.cshtml", objective);
    }

    // GET: Admin/CreateObjective
    public async Task<IActionResult> CreateObjective()
    {
        ViewBag.Users = new SelectList(await _context.Users.OrderBy(u => u.Name).ToListAsync(), "Id", "Name");
        return View("~/Views/Admin/Objective/Create.cshtml");
    }

    // POST: Admin/CreateObjective
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateObjective([Bind("Title,Theme,Description,OwnerUserId,ThemeSroUserId,OutcomeSroUserId,Status")] Objective objective)
    {
        if (ModelState.IsValid)
        {
            try
            {
                objective.CreatedAt = DateTime.UtcNow;
                objective.UpdatedAt = DateTime.UtcNow;
                objective.IsDeleted = false;
                
                _context.Add(objective);
                await _context.SaveChangesAsync();
                
                TempData["SuccessMessage"] = $"Priority outcome '{objective.Title}' has been created successfully.";
                return RedirectToAction(nameof(Objectives));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating objective");
                ModelState.AddModelError("", "An error occurred while creating the objective. Please try again.");
            }
        }
        
        ViewBag.Users = new SelectList(await _context.Users.OrderBy(u => u.Name).ToListAsync(), "Id", "Name");
        return View("~/Views/Admin/Objective/Create.cshtml", objective);
    }

    // GET: Admin/EditObjective/5
    public async Task<IActionResult> EditObjective(int? id)
    {
        if (id == null)
        {
            return NotFound();
        }

        var objective = await _context.Objectives.FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted);
        if (objective == null)
        {
            return NotFound();
        }

        ViewBag.Users = new SelectList(await _context.Users.OrderBy(u => u.Name).ToListAsync(), "Id", "Name");
        return View("~/Views/Admin/Objective/Edit.cshtml", objective);
    }

    // POST: Admin/EditObjective/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditObjective(int id, [Bind("Id,Title,Theme,Description,OwnerUserId,ThemeSroUserId,OutcomeSroUserId,Status")] Objective objective)
    {
        if (id != objective.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                var existingObjective = await _context.Objectives.FindAsync(id);
                if (existingObjective == null || existingObjective.IsDeleted)
                {
                    return NotFound();
                }

                existingObjective.Title = objective.Title;
                existingObjective.Theme = objective.Theme;
                existingObjective.Description = objective.Description;
                existingObjective.OwnerUserId = objective.OwnerUserId;
                existingObjective.ThemeSroUserId = objective.ThemeSroUserId;
                existingObjective.OutcomeSroUserId = objective.OutcomeSroUserId;
                existingObjective.Status = objective.Status;
                existingObjective.UpdatedAt = DateTime.UtcNow;
                
                await _context.SaveChangesAsync();
                
                TempData["SuccessMessage"] = $"Priority outcome '{objective.Title}' has been updated successfully.";
                return RedirectToAction(nameof(Objectives));
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!ObjectiveExists(objective.Id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating objective");
                ModelState.AddModelError("", "An error occurred while updating the objective. Please try again.");
            }
        }
        
        ViewBag.Users = new SelectList(await _context.Users.OrderBy(u => u.Name).ToListAsync(), "Id", "Name");
        return View("~/Views/Admin/Objective/Edit.cshtml", objective);
    }

    // GET: Admin/DeleteObjective/5
    public async Task<IActionResult> DeleteObjective(int? id)
    {
        if (id == null)
        {
            return NotFound();
        }

        var objective = await _context.Objectives
            .Include(o => o.OwnerUser)
            .FirstOrDefaultAsync(m => m.Id == id && !m.IsDeleted);
            
        if (objective == null)
        {
            return NotFound();
        }

        return View("~/Views/Admin/Objective/Delete.cshtml", objective);
    }

    // POST: Admin/DeleteObjective/5
    [HttpPost, ActionName("DeleteObjective")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteObjectiveConfirmed(int id)
    {
        try
        {
            var objective = await _context.Objectives
                .Include(o => o.Risks)
                .Include(o => o.Issues)
                .Include(o => o.Milestones)
                .Include(o => o.Actions)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted);
                
            if (objective == null)
            {
                TempData["ErrorMessage"] = "Priority outcome not found.";
                return RedirectToAction(nameof(Objectives));
            }

            // Check for related items
            var relatedItemsCount = objective.Risks.Count + objective.Issues.Count + 
                                   objective.Milestones.Count + objective.Actions.Count;
                                   
            if (relatedItemsCount > 0)
            {
                TempData["ErrorMessage"] = $"Cannot delete '{objective.Title}' because it has {relatedItemsCount} related item(s). Please remove or reassign all related items before deleting.";
                return RedirectToAction(nameof(ObjectiveDetails), new { id = id });
            }

            objective.IsDeleted = true;
            objective.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            
            TempData["SuccessMessage"] = $"Priority outcome '{objective.Title}' has been deleted successfully.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting objective");
            TempData["ErrorMessage"] = "An error occurred while deleting the objective. Please try again.";
        }
        
        return RedirectToAction(nameof(Objectives));
    }

    private bool ObjectiveExists(int id)
    {
        return _context.Objectives.Any(e => e.Id == id && !e.IsDeleted);
    }

    // ========================================
    // SETTINGS
    // ========================================

    // GET: Admin/Settings
    public async Task<IActionResult> Settings()
    {
        // Load all lookup data for tabbed interface
        ViewBag.RiskTypes = await _context.RiskTypes.OrderBy(rt => rt.Name).ToListAsync();
        ViewBag.RiskTiers = await _context.RiskTiers.OrderBy(rt => rt.SortOrder).ThenBy(rt => rt.Name).ToListAsync();
        ViewBag.ActionSources = await _context.ActionSources.OrderBy(a_s => a_s.SortOrder).ThenBy(a_s => a_s.Name).ToListAsync();
        ViewBag.WcagCriteria = await _context.WcagCriteria.OrderBy(w => w.Criterion).ToListAsync();
        ViewBag.BusinessAreas = await _context.BusinessAreaLookups.OrderBy(ba => ba.SortOrder).ThenBy(ba => ba.Name).ToListAsync();
        ViewBag.Phases = await _context.PhaseLookups.OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync();
        ViewBag.GddRoles = await _context.GddRoles.OrderBy(r => r.RoleFamily).ThenBy(r => r.RoleName).ThenBy(r => r.RoleLevel).ToListAsync();
        ViewBag.Skills = await _context.Skills.OrderBy(s => s.SkillName).ToListAsync();
        ViewBag.KpiCategories = await _context.KpiCategories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
        
        return View("~/Views/Admin/Settings/Index.cshtml");
    }

    // ========================================
    // SETTINGS - KPI Categories
    // ========================================

    public async Task<IActionResult> KpiCategories()
    {
        var categories = await _context.KpiCategories
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync();

        return View("~/Views/Admin/Settings/KpiCategories.cshtml", categories);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateKpiCategory([Bind("Name,Code,Description,SortOrder,IsActive")] KpiCategory category)
    {
        if (string.IsNullOrWhiteSpace(category.Name))
        {
            TempData["ErrorMessage"] = "Name is required.";
            return RedirectToAction(nameof(KpiCategories));
        }

        try
        {
            category.Name = category.Name.Trim();
            category.Code = SanitiseKpiCategoryCode(category.Code, category.Name);
            category.Description = string.IsNullOrWhiteSpace(category.Description) ? null : category.Description.Trim();
            category.SortOrder = await NormaliseKpiCategorySortOrderAsync(category.SortOrder);
            category.CreatedAt = DateTime.UtcNow;
            category.UpdatedAt = DateTime.UtcNow;

            var codeExists = await _context.KpiCategories.AnyAsync(c => c.Code == category.Code);
            if (codeExists)
            {
                TempData["ErrorMessage"] = $"A KPI category with code '{category.Code}' already exists.";
                return RedirectToAction(nameof(KpiCategories));
            }

            _context.KpiCategories.Add(category);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"KPI category '{category.Name}' created.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating KPI category");
            TempData["ErrorMessage"] = "An error occurred while creating the KPI category.";
        }

        return RedirectToAction(nameof(KpiCategories));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateKpiCategory(int id, string name, string? code, string? description, int sortOrder, bool isActive)
    {
        var category = await _context.KpiCategories.FindAsync(id);
        if (category == null)
        {
            TempData["ErrorMessage"] = "KPI category not found.";
            return RedirectToAction(nameof(KpiCategories));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["ErrorMessage"] = "Name is required.";
            return RedirectToAction(nameof(KpiCategories));
        }

        try
        {
            var normalisedCode = SanitiseKpiCategoryCode(code, name);
            var duplicate = await _context.KpiCategories.AnyAsync(c => c.Code == normalisedCode && c.Id != id);
            if (duplicate)
            {
                TempData["ErrorMessage"] = $"A KPI category with code '{normalisedCode}' already exists.";
                return RedirectToAction(nameof(KpiCategories));
            }

            category.Name = name.Trim();
            category.Code = normalisedCode;
            category.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            if (sortOrder > 0)
            {
                category.SortOrder = sortOrder;
            }
            else if (category.SortOrder == 0)
            {
                category.SortOrder = await NormaliseKpiCategorySortOrderAsync(sortOrder);
            }
            category.IsActive = isActive;
            category.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            TempData["SuccessMessage"] = $"KPI category '{category.Name}' updated.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating KPI category {KpiCategoryId}", id);
            TempData["ErrorMessage"] = "An error occurred while updating the KPI category.";
        }

        return RedirectToAction(nameof(KpiCategories));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteKpiCategory(int id)
    {
        var category = await _context.KpiCategories.FindAsync(id);
        if (category == null)
        {
            TempData["ErrorMessage"] = "KPI category not found.";
            return RedirectToAction(nameof(KpiCategories));
        }

        try
        {
            var codePrefix = $"{category.Code}-";
            var kpiUsage = await _context.Kpis.CountAsync(k => k.Code != null && k.Code.StartsWith(codePrefix));
            if (kpiUsage > 0)
            {
                TempData["ErrorMessage"] = $"Cannot delete '{category.Name}' because it is used by {kpiUsage} KPI(s). Consider deactivating it instead.";
                return RedirectToAction(nameof(KpiCategories));
            }

            _context.KpiCategories.Remove(category);
            await _context.SaveChangesAsync();
            TempData["SuccessMessage"] = $"KPI category '{category.Name}' deleted.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting KPI category {KpiCategoryId}", id);
            TempData["ErrorMessage"] = "An error occurred while deleting the KPI category.";
        }

        return RedirectToAction(nameof(KpiCategories));
    }

    // ========================================
    // SETTINGS - Risk Types
    // ========================================

    // GET: Admin/RiskTypes
    public async Task<IActionResult> RiskTypes()
    {
        var riskTypes = await _context.RiskTypes
            .OrderBy(rt => rt.Name)
            .ToListAsync();
        
        return View("~/Views/Admin/Settings/RiskTypes.cshtml", riskTypes);
    }

    // GET: Admin/CreateRiskType
    public IActionResult CreateRiskType()
    {
        return View("~/Views/Admin/Settings/CreateRiskType.cshtml");
    }

    // POST: Admin/CreateRiskType
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRiskType([Bind("Code,Name,Description,Summary,IsActive")] RiskType riskType)
    {
        if (ModelState.IsValid)
        {
            try
            {
                // Check if code already exists
                if (await _context.RiskTypes.AnyAsync(rt => rt.Code == riskType.Code))
                {
                    ModelState.AddModelError("Code", "A risk type with this code already exists.");
                }
                else
                {
                    riskType.CreatedAt = DateTime.UtcNow;
                    riskType.UpdatedAt = DateTime.UtcNow;
                    _context.Add(riskType);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Risk type '{riskType.Name}' has been created successfully.";
                    return RedirectToAction(nameof(RiskTypes));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating risk type");
                ModelState.AddModelError("", "An error occurred while creating the risk type. Please try again.");
            }
        }
        
        return View("~/Views/Admin/Settings/CreateRiskType.cshtml", riskType);
    }

    // GET: Admin/EditRiskType/5
    public async Task<IActionResult> EditRiskType(int? id)
    {
        if (id == null)
        {
            return NotFound();
        }

        var riskType = await _context.RiskTypes.FindAsync(id);
        if (riskType == null)
        {
            return NotFound();
        }

        return View("~/Views/Admin/Settings/EditRiskType.cshtml", riskType);
    }

    // POST: Admin/EditRiskType/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRiskType(int id, [Bind("Id,Code,Name,Description,Summary,IsActive")] RiskType riskType)
    {
        if (id != riskType.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                // Check if code already exists for a different record
                if (await _context.RiskTypes.AnyAsync(rt => rt.Code == riskType.Code && rt.Id != id))
                {
                    ModelState.AddModelError("Code", "A risk type with this code already exists.");
                }
                else
                {
                    var existingRiskType = await _context.RiskTypes.FindAsync(id);
                    if (existingRiskType == null)
                    {
                        return NotFound();
                    }

                    existingRiskType.Code = riskType.Code;
                    existingRiskType.Name = riskType.Name;
                    existingRiskType.Description = riskType.Description;
                    existingRiskType.Summary = riskType.Summary;
                    existingRiskType.IsActive = riskType.IsActive;
                    existingRiskType.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Risk type '{riskType.Name}' has been updated successfully.";
                    return RedirectToAction(nameof(RiskTypes));
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!RiskTypeExists(riskType.Id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating risk type");
                ModelState.AddModelError("", "An error occurred while updating the risk type. Please try again.");
            }
        }
        
        return View("~/Views/Admin/Settings/EditRiskType.cshtml", riskType);
    }

    // GET: Admin/DeleteRiskType/5
    public async Task<IActionResult> DeleteRiskType(int? id)
    {
        if (id == null)
        {
            return NotFound();
        }

        var riskType = await _context.RiskTypes.FindAsync(id);
        if (riskType == null)
        {
            return NotFound();
        }

        // Check if any risks are using this type
        var riskCount = await _context.RiskRiskTypes.CountAsync(rrt => rrt.RiskTypeId == id);
        ViewBag.RiskCount = riskCount;

        return View("~/Views/Admin/Settings/DeleteRiskType.cshtml", riskType);
    }

    // POST: Admin/DeleteRiskType/5
    [HttpPost, ActionName("DeleteRiskType")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRiskTypeConfirmed(int id)
    {
        try
        {
            var riskType = await _context.RiskTypes.FindAsync(id);
            if (riskType != null)
            {
                // Check if any risks are using this type
                var riskCount = await _context.RiskRiskTypes.CountAsync(rrt => rrt.RiskTypeId == id);
                if (riskCount > 0)
                {
                    TempData["ErrorMessage"] = $"Cannot delete risk type '{riskType.Name}' as it is being used by {riskCount} risk(s). Please reassign those risks first.";
                }
                else
                {
                    _context.RiskTypes.Remove(riskType);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Risk type '{riskType.Name}' has been deleted successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting risk type");
            TempData["ErrorMessage"] = "An error occurred while deleting the risk type. Please try again.";
        }
        
        return RedirectToAction(nameof(RiskTypes));
    }

    private bool RiskTypeExists(int id)
    {
        return _context.RiskTypes.Any(e => e.Id == id);
    }

    // ========================================
    // SETTINGS - Risk Tiers
    // ========================================

    // GET: Admin/RiskTiers
    public async Task<IActionResult> RiskTiers()
    {
        var riskTiers = await _context.RiskTiers
            .OrderBy(rt => rt.SortOrder)
            .ThenBy(rt => rt.Name)
            .ToListAsync();
        
        return View("~/Views/Admin/Settings/RiskTiers.cshtml", riskTiers);
    }

    // GET: Admin/CreateRiskTier
    public IActionResult CreateRiskTier()
    {
        return View("~/Views/Admin/Settings/CreateRiskTier.cshtml");
    }

    // POST: Admin/CreateRiskTier
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRiskTier([Bind("Code,Name,Description,Summary,SortOrder,IsActive")] RiskTier riskTier)
    {
        if (ModelState.IsValid)
        {
            try
            {
                // Check if code already exists
                if (await _context.RiskTiers.AnyAsync(rt => rt.Code == riskTier.Code))
                {
                    ModelState.AddModelError("Code", "A risk tier with this code already exists.");
                }
                else
                {
                    riskTier.CreatedAt = DateTime.UtcNow;
                    riskTier.UpdatedAt = DateTime.UtcNow;
                    _context.Add(riskTier);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Risk tier '{riskTier.Name}' has been created successfully.";
                    return RedirectToAction(nameof(RiskTiers));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating risk tier");
                ModelState.AddModelError("", "An error occurred while creating the risk tier. Please try again.");
            }
        }
        
        return View("~/Views/Admin/Settings/CreateRiskTier.cshtml", riskTier);
    }

    // GET: Admin/EditRiskTier/5
    public async Task<IActionResult> EditRiskTier(int? id)
    {
        if (id == null)
        {
            return NotFound();
        }

        var riskTier = await _context.RiskTiers.FindAsync(id);
        if (riskTier == null)
        {
            return NotFound();
        }

        return View("~/Views/Admin/Settings/EditRiskTier.cshtml", riskTier);
    }

    // POST: Admin/EditRiskTier/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRiskTier(int id, [Bind("Id,Code,Name,Description,Summary,SortOrder,IsActive")] RiskTier riskTier)
    {
        if (id != riskTier.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                // Check if code already exists for a different record
                if (await _context.RiskTiers.AnyAsync(rt => rt.Code == riskTier.Code && rt.Id != id))
                {
                    ModelState.AddModelError("Code", "A risk tier with this code already exists.");
                }
                else
                {
                    var existingRiskTier = await _context.RiskTiers.FindAsync(id);
                    if (existingRiskTier == null)
                    {
                        return NotFound();
                    }

                    existingRiskTier.Code = riskTier.Code;
                    existingRiskTier.Name = riskTier.Name;
                    existingRiskTier.Description = riskTier.Description;
                    existingRiskTier.Summary = riskTier.Summary;
                    existingRiskTier.SortOrder = riskTier.SortOrder;
                    existingRiskTier.IsActive = riskTier.IsActive;
                    existingRiskTier.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Risk tier '{riskTier.Name}' has been updated successfully.";
                    return RedirectToAction(nameof(RiskTiers));
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!RiskTierExists(riskTier.Id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating risk tier");
                ModelState.AddModelError("", "An error occurred while updating the risk tier. Please try again.");
            }
        }
        
        return View("~/Views/Admin/Settings/EditRiskTier.cshtml", riskTier);
    }

    // GET: Admin/DeleteRiskTier/5
    public async Task<IActionResult> DeleteRiskTier(int? id)
    {
        if (id == null)
        {
            return NotFound();
        }

        var riskTier = await _context.RiskTiers.FindAsync(id);
        if (riskTier == null)
        {
            return NotFound();
        }

        // Check if any risks are using this tier
        var riskCount = await _context.Risks.CountAsync(r => r.RiskTierId == id && !r.IsDeleted);
        ViewBag.RiskCount = riskCount;

        return View("~/Views/Admin/Settings/DeleteRiskTier.cshtml", riskTier);
    }

    // POST: Admin/DeleteRiskTier/5
    [HttpPost, ActionName("DeleteRiskTier")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRiskTierConfirmed(int id)
    {
        try
        {
            var riskTier = await _context.RiskTiers.FindAsync(id);
            if (riskTier != null)
            {
                // Check if any risks are using this tier
                var riskCount = await _context.Risks.CountAsync(r => r.RiskTierId == id && !r.IsDeleted);
                if (riskCount > 0)
                {
                    TempData["ErrorMessage"] = $"Cannot delete risk tier '{riskTier.Name}' as it is being used by {riskCount} risk(s). Please reassign those risks first.";
                }
                else
                {
                    _context.RiskTiers.Remove(riskTier);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Risk tier '{riskTier.Name}' has been deleted successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting risk tier");
            TempData["ErrorMessage"] = "An error occurred while deleting the risk tier. Please try again.";
        }
        
        return RedirectToAction(nameof(RiskTiers));
    }

    private bool RiskTierExists(int id)
    {
        return _context.RiskTiers.Any(e => e.Id == id);
    }

    private static string SanitiseKpiCategoryCode(string? value, string? fallbackName)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallbackName : value;
        if (string.IsNullOrWhiteSpace(source))
        {
            return "KPI";
        }

        var filtered = new string(source.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(filtered) ? "KPI" : filtered;
    }

    private async Task<int> NormaliseKpiCategorySortOrderAsync(int sortOrder)
    {
        if (sortOrder > 0)
        {
            return sortOrder;
        }

        var maxSortOrder = await _context.KpiCategories.Select(c => (int?)c.SortOrder).MaxAsync() ?? 0;
        return maxSortOrder + 10;
    }

    // ========================================
    // SETTINGS - Action Sources
    // ========================================

    // GET: Admin/ActionSources
    public async Task<IActionResult> ActionSources()
    {
        var actionSources = await _context.ActionSources
            .OrderBy(a_s => a_s.SortOrder)
            .ThenBy(a_s => a_s.Name)
            .ToListAsync();
        
        return View("~/Views/Admin/Settings/ActionSources.cshtml", actionSources);
    }

    // GET: Admin/DeliveryPriorities
    public async Task<IActionResult> DeliveryPriorities()
    {
        var priorities = await _context.DeliveryPriorities
            .OrderBy(dp => dp.SortOrder)
            .ThenBy(dp => dp.Name)
            .ToListAsync();

        return View("~/Views/Admin/Settings/DeliveryPriorities.cshtml", priorities);
    }

    // POST: Admin/CreateDeliveryPriority
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateDeliveryPriority([Bind("Name,Summary,Description,SortOrder,IsActive,CssClass")] DeliveryPriority deliveryPriority)
    {
        if (ModelState.IsValid)
        {
            var normalisedName = deliveryPriority.Name.Trim();
            if (await _context.DeliveryPriorities
                    .AnyAsync(dp => dp.Name.ToLower() == normalisedName.ToLower()))
            {
                ModelState.AddModelError("Name", "A delivery priority with this name already exists.");
            }
            else
            {
                deliveryPriority.Name = normalisedName;
                deliveryPriority.CreatedAt = DateTime.UtcNow;
                deliveryPriority.UpdatedAt = DateTime.UtcNow;

                if (deliveryPriority.SortOrder == 0)
                {
                    var nextSortOrder = await _context.DeliveryPriorities
                        .Select(dp => (int?)dp.SortOrder)
                        .MaxAsync() ?? 0;
                    deliveryPriority.SortOrder = nextSortOrder + 1;
                }

                _context.DeliveryPriorities.Add(deliveryPriority);
                await _context.SaveChangesAsync();

                TempData["SuccessMessage"] = $"Delivery priority '{deliveryPriority.Name}' has been created.";
                return RedirectToAction(nameof(DeliveryPriorities));
            }
        }

        TempData["ErrorMessage"] = "Unable to create delivery priority. Please fix the errors and try again.";
        return await DeliveryPriorities();
    }

    // POST: Admin/EditDeliveryPriority
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditDeliveryPriority(int id, [Bind("Id,Name,Summary,Description,SortOrder,IsActive,CssClass")] DeliveryPriority deliveryPriority)
    {
        if (id != deliveryPriority.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            var existingPriority = await _context.DeliveryPriorities.FindAsync(id);
            if (existingPriority == null)
            {
                return NotFound();
            }

            var normalisedName = deliveryPriority.Name.Trim();
            var duplicateExists = await _context.DeliveryPriorities
                .AnyAsync(dp => dp.Id != id && dp.Name.ToLower() == normalisedName.ToLower());
            if (duplicateExists)
            {
                ModelState.AddModelError("Name", "A delivery priority with this name already exists.");
            }
            else
            {
                existingPriority.Name = normalisedName;
                existingPriority.Summary = deliveryPriority.Summary;
                existingPriority.Description = deliveryPriority.Description;
                existingPriority.SortOrder = deliveryPriority.SortOrder;
                existingPriority.IsActive = deliveryPriority.IsActive;
                existingPriority.CssClass = deliveryPriority.CssClass;
                existingPriority.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                TempData["SuccessMessage"] = $"Delivery priority '{existingPriority.Name}' has been updated.";
                return RedirectToAction(nameof(DeliveryPriorities));
            }
        }

        TempData["ErrorMessage"] = "Unable to update delivery priority. Please fix the errors and try again.";
        return await DeliveryPriorities();
    }

    // POST: Admin/DeleteDeliveryPriority
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteDeliveryPriority(int id)
    {
        try
        {
            var deliveryPriority = await _context.DeliveryPriorities.FindAsync(id);
            if (deliveryPriority != null)
            {
                // Check if any projects are using this delivery priority
                var projectCount = await _context.Projects.CountAsync(p => p.DeliveryPriorityId == deliveryPriority.Id && !p.IsDeleted);
                if (projectCount > 0)
                {
                    TempData["ErrorMessage"] = $"Cannot delete delivery priority '{deliveryPriority.Name}' as it is being used by {projectCount} project(s).";
                }
                else
                {
                    _context.DeliveryPriorities.Remove(deliveryPriority);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Delivery priority '{deliveryPriority.Name}' has been deleted successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting delivery priority");
            TempData["ErrorMessage"] = "An error occurred while deleting the delivery priority. Please try again.";
        }

        return RedirectToAction(nameof(DeliveryPriorities));
    }

    // GET: Admin/CreateActionSource
    public IActionResult CreateActionSource()
    {
        return View("~/Views/Admin/Settings/CreateActionSource.cshtml");
    }

    // POST: Admin/CreateActionSource
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateActionSource([Bind("Code,Name,Description,Summary,SortOrder,IsActive")] ActionSource actionSource)
    {
        if (ModelState.IsValid)
        {
            try
            {
                if (await _context.ActionSources.AnyAsync(a_s => a_s.Code == actionSource.Code))
                {
                    ModelState.AddModelError("Code", "An action source with this code already exists.");
                }
                else
                {
                    actionSource.CreatedAt = DateTime.UtcNow;
                    actionSource.UpdatedAt = DateTime.UtcNow;
                    _context.Add(actionSource);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Action source '{actionSource.Name}' has been created successfully.";
                    return RedirectToAction(nameof(ActionSources));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating action source");
                ModelState.AddModelError("", "An error occurred while creating the action source. Please try again.");
            }
        }
        
        return View("~/Views/Admin/Settings/CreateActionSource.cshtml", actionSource);
    }

    // GET: Admin/EditActionSource/5
    public async Task<IActionResult> EditActionSource(int? id)
    {
        if (id == null)
        {
            return NotFound();
        }

        var actionSource = await _context.ActionSources.FindAsync(id);
        if (actionSource == null)
        {
            return NotFound();
        }

        return View("~/Views/Admin/Settings/EditActionSource.cshtml", actionSource);
    }

    // POST: Admin/EditActionSource/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditActionSource(int id, [Bind("Id,Code,Name,Description,Summary,SortOrder,IsActive")] ActionSource actionSource)
    {
        if (id != actionSource.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                if (await _context.ActionSources.AnyAsync(a_s => a_s.Code == actionSource.Code && a_s.Id != id))
                {
                    ModelState.AddModelError("Code", "An action source with this code already exists.");
                }
                else
                {
                    var existingActionSource = await _context.ActionSources.FindAsync(id);
                    if (existingActionSource == null)
                    {
                        return NotFound();
                    }

                    existingActionSource.Code = actionSource.Code;
                    existingActionSource.Name = actionSource.Name;
                    existingActionSource.Description = actionSource.Description;
                    existingActionSource.Summary = actionSource.Summary;
                    existingActionSource.SortOrder = actionSource.SortOrder;
                    existingActionSource.IsActive = actionSource.IsActive;
                    existingActionSource.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Action source '{actionSource.Name}' has been updated successfully.";
                    return RedirectToAction(nameof(ActionSources));
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!ActionSourceExists(actionSource.Id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating action source");
                ModelState.AddModelError("", "An error occurred while updating the action source. Please try again.");
            }
        }
        
        return View("~/Views/Admin/Settings/EditActionSource.cshtml", actionSource);
    }

    // GET: Admin/DeleteActionSource/5
    public async Task<IActionResult> DeleteActionSource(int? id)
    {
        if (id == null)
        {
            return NotFound();
        }

        var actionSource = await _context.ActionSources.FindAsync(id);
        if (actionSource == null)
        {
            return NotFound();
        }

        var actionCount = await _context.Actions.CountAsync(a => a.ActionSourceId == id && !a.IsDeleted);
        ViewBag.ActionCount = actionCount;

        return View("~/Views/Admin/Settings/DeleteActionSource.cshtml", actionSource);
    }

    // POST: Admin/DeleteActionSource/5
    [HttpPost, ActionName("DeleteActionSource")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteActionSourceConfirmed(int id)
    {
        try
        {
            var actionSource = await _context.ActionSources.FindAsync(id);
            if (actionSource != null)
            {
                var actionCount = await _context.Actions.CountAsync(a => a.ActionSourceId == id && !a.IsDeleted);
                if (actionCount > 0)
                {
                    TempData["ErrorMessage"] = $"Cannot delete action source '{actionSource.Name}' as it is being used by {actionCount} action(s). Please reassign those actions first.";
                }
                else
                {
                    _context.ActionSources.Remove(actionSource);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Action source '{actionSource.Name}' has been deleted successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting action source");
            TempData["ErrorMessage"] = "An error occurred while deleting the action source. Please try again.";
        }
        
        return RedirectToAction(nameof(ActionSources));
    }

    private bool ActionSourceExists(int id)
    {
        return _context.ActionSources.Any(e => e.Id == id);
    }

    // API Token Management

    [RequireSuperAdmin]
    public async Task<IActionResult> ApiTokens()
    {
        var tokens = await _apiTokenService.GetAllTokensAsync();
        return View("~/Views/Admin/ApiTokens/Index.cshtml", tokens);
    }

    [RequireSuperAdmin]
    public IActionResult CreateApiToken()
    {
        return View("~/Views/Admin/ApiTokens/Create.cshtml");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequireSuperAdmin]
    public async Task<IActionResult> CreateApiToken(string name, string? description, DateTime? expiresAt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["ErrorMessage"] = "Token name is required.";
            return RedirectToAction(nameof(CreateApiToken));
        }

        try
        {
            var userEmail = User.Identity?.Name ?? "unknown";
            var token = await _apiTokenService.CreateTokenAsync(name, description ?? string.Empty, userEmail, expiresAt);
            
            TempData["SuccessMessage"] = "API token created successfully. Make sure to copy the token now - you won't be able to see it again!";
            TempData["NewToken"] = token.Token;
            
            return RedirectToAction(nameof(ConfigurePermissions), new { id = token.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating API token");
            TempData["ErrorMessage"] = "An error occurred while creating the API token.";
            return RedirectToAction(nameof(CreateApiToken));
        }
    }

    [RequireSuperAdmin]
    public async Task<IActionResult> ConfigurePermissions(int id)
    {
        var token = await _apiTokenService.GetByIdAsync(id);
        if (token == null)
        {
            TempData["ErrorMessage"] = "API token not found.";
            return RedirectToAction(nameof(ApiTokens));
        }

        var permissions = await _apiTokenService.GetPermissionsAsync(id);

        var resources = Compass.Services.Api.ApiTokenResourceCatalog.Resources;
        
        ViewBag.Token = token;
        ViewBag.Permissions = permissions;
        ViewBag.Resources = resources;

        return View("~/Views/Admin/ApiTokens/ConfigurePermissions.cshtml");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequireSuperAdmin]
    public async Task<IActionResult> SavePermissions(int id, Dictionary<string, string> permissions)
    {
        try
        {
            var permissionsDict = new Dictionary<string, (bool read, bool create, bool update, bool delete)>();

            foreach (var resource in Compass.Services.Api.ApiTokenResourceCatalog.Resources)
            {
                var read = permissions.ContainsKey($"{resource}_read") && permissions[$"{resource}_read"] == "on";
                var create = permissions.ContainsKey($"{resource}_create") && permissions[$"{resource}_create"] == "on";
                var update = permissions.ContainsKey($"{resource}_update") && permissions[$"{resource}_update"] == "on";
                var delete = permissions.ContainsKey($"{resource}_delete") && permissions[$"{resource}_delete"] == "on";

                if (read || create || update || delete)
                {
                    permissionsDict[resource] = (read, create, update, delete);
                }
            }

            await _apiTokenService.SetPermissionsAsync(id, permissionsDict);

            TempData["SuccessMessage"] = "Permissions updated successfully.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving API token permissions");
            TempData["ErrorMessage"] = "An error occurred while saving permissions.";
        }

        return RedirectToAction(nameof(ConfigurePermissions), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequireSuperAdmin]
    public async Task<IActionResult> RecycleApiToken(int id)
    {
        try
        {
            var token = await _apiTokenService.GetByIdAsync(id);
            if (token == null)
            {
                TempData["ErrorMessage"] = "API token not found.";
                return RedirectToAction(nameof(ApiTokens));
            }

            // Generate new token value
            var newToken = await _apiTokenService.RecycleTokenAsync(id);
            
            TempData["SuccessMessage"] = "API token recycled successfully. Make sure to copy the new token now - you won't be able to see it again!";
            TempData["NewToken"] = newToken;
            
            return RedirectToAction(nameof(ConfigurePermissions), new { id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recycling API token");
            TempData["ErrorMessage"] = "An error occurred while recycling the token.";
            return RedirectToAction(nameof(ConfigurePermissions), new { id });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequireSuperAdmin]
    public async Task<IActionResult> ToggleApiToken(int id)
    {
        try
        {
            var token = await _apiTokenService.GetByIdAsync(id);
            if (token != null)
            {
                var newStatus = !token.IsActive;
                await _apiTokenService.UpdateTokenStatusAsync(id, newStatus);
                TempData["SuccessMessage"] = $"API token {(newStatus ? "activated" : "suspended")} successfully.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling API token status");
            TempData["ErrorMessage"] = "An error occurred while updating the token status.";
        }

        // Check if we came from ConfigurePermissions
        var referer = Request.Headers["Referer"].ToString();
        if (referer.Contains("ConfigurePermissions"))
        {
            return RedirectToAction(nameof(ConfigurePermissions), new { id });
        }

        return RedirectToAction(nameof(ApiTokens));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequireSuperAdmin]
    public async Task<IActionResult> DeleteApiToken(int id)
    {
        try
        {
            await _apiTokenService.DeleteTokenAsync(id);
            TempData["SuccessMessage"] = "API token deleted successfully.";
            return RedirectToAction(nameof(ApiTokens));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting API token");
            TempData["ErrorMessage"] = "An error occurred while deleting the token.";
            
            // Check if we came from ConfigurePermissions
            var referer = Request.Headers["Referer"].ToString();
            if (referer.Contains("ConfigurePermissions"))
            {
                return RedirectToAction(nameof(ConfigurePermissions), new { id });
            }
            
            return RedirectToAction(nameof(ApiTokens));
        }
    }

    [RequireSuperAdmin]
    public async Task<IActionResult> ApiLogs(int? tokenId = null)
    {
        var query = _context.ApiRequestLogs
            .Include(l => l.ApiToken)
            .OrderByDescending(l => l.RequestTimestamp)
            .AsQueryable();

        if (tokenId.HasValue)
        {
            query = query.Where(l => l.ApiTokenId == tokenId.Value);
        }

        var logs = await query.Take(1000).ToListAsync();

        ViewBag.Tokens = await _apiTokenService.GetAllTokensAsync();
        ViewBag.SelectedTokenId = tokenId;

        return View("~/Views/Admin/ApiTokens/Logs.cshtml", logs);
    }

    // ========================================
    // MISSIONS MANAGEMENT
    // ========================================

    // GET: Admin/Missions
    public async Task<IActionResult> Missions()
    {
        var missions = await _context.Missions
            .Include(m => m.OwnerUser)
            .Where(m => !m.IsDeleted)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();
        
        return View("~/Views/Admin/Mission/Index.cshtml", missions);
    }

    // GET: Admin/CreateMission
    public async Task<IActionResult> CreateMission()
    {
        ViewBag.OwnerUsers = await _context.Users
            .OrderBy(u => u.Name)
            .Select(u => new { u.Id, u.Name })
            .ToListAsync();
        
        return View("~/Views/Admin/Mission/Create.cshtml");
    }

    // POST: Admin/CreateMission
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateMission([Bind("Title,Description,Theme,OwnerUserId,StartDate,EndDate,Status")] Mission mission)
    {
        if (ModelState.IsValid)
        {
            try
            {
                mission.CreatedAt = DateTime.UtcNow;
                mission.UpdatedAt = DateTime.UtcNow;
                _context.Add(mission);
                await _context.SaveChangesAsync();
                
                TempData["SuccessMessage"] = $"Mission '{mission.Title}' has been created successfully.";
                return RedirectToAction(nameof(Missions));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating mission");
                TempData["ErrorMessage"] = "An error occurred while creating the mission. Please try again.";
            }
        }

        ViewBag.OwnerUsers = await _context.Users
            .OrderBy(u => u.Name)
            .Select(u => new { u.Id, u.Name })
            .ToListAsync();
        
        return View("~/Views/Admin/Mission/Create.cshtml", mission);
    }

    // GET: Admin/EditMission/5
    public async Task<IActionResult> EditMission(int? id)
    {
        if (id == null)
        {
            return NotFound();
        }

        var mission = await _context.Missions.FindAsync(id);
        if (mission == null || mission.IsDeleted)
        {
            return NotFound();
        }

        ViewBag.OwnerUsers = await _context.Users
            .OrderBy(u => u.Name)
            .Select(u => new { u.Id, u.Name })
            .ToListAsync();

        return View("~/Views/Admin/Mission/Edit.cshtml", mission);
    }

    // POST: Admin/EditMission/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditMission(int id, [Bind("Id,Title,Description,Theme,OwnerUserId,StartDate,EndDate,Status,CreatedAt")] Mission mission)
    {
        if (id != mission.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                mission.UpdatedAt = DateTime.UtcNow;
                _context.Update(mission);
                await _context.SaveChangesAsync();
                
                TempData["SuccessMessage"] = $"Mission '{mission.Title}' has been updated successfully.";
                return RedirectToAction(nameof(Missions));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating mission");
                TempData["ErrorMessage"] = "An error occurred while updating the mission. Please try again.";
            }
        }

        ViewBag.OwnerUsers = await _context.Users
            .OrderBy(u => u.Name)
            .Select(u => new { u.Id, u.Name })
            .ToListAsync();

        return View("~/Views/Admin/Mission/Edit.cshtml", mission);
    }

    // POST: Admin/DeleteMission/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteMission(int id)
    {
        try
        {
            var mission = await _context.Missions.FindAsync(id);
            if (mission != null)
            {
                mission.IsDeleted = true;
                mission.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                
                TempData["SuccessMessage"] = $"Mission '{mission.Title}' has been deleted successfully.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting mission");
            TempData["ErrorMessage"] = "An error occurred while deleting the mission. Please try again.";
        }

        return RedirectToAction(nameof(Missions));
    }

    // ========================================
    // FUNDING SOURCES MANAGEMENT
    // ========================================

    // GET: Admin/FundingSources
    public async Task<IActionResult> FundingSources()
    {
        var fundingSources = await _context.FundingSources
            .OrderBy(fs => fs.SortOrder)
            .ThenBy(fs => fs.Name)
            .ToListAsync();
        
        return View("~/Views/Admin/FundingSource/Index.cshtml", fundingSources);
    }

    // GET: Admin/CreateFundingSource
    public IActionResult CreateFundingSource()
    {
        return View("~/Views/Admin/FundingSource/Create.cshtml");
    }

    // POST: Admin/CreateFundingSource
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateFundingSource([Bind("Code,Name,Description,SortOrder,IsActive")] FundingSource fundingSource)
    {
        if (ModelState.IsValid)
        {
            try
            {
                // Check if code already exists
                if (await _context.FundingSources.AnyAsync(fs => fs.Code == fundingSource.Code))
                {
                    ModelState.AddModelError("Code", "A funding source with this code already exists.");
                }
                else
                {
                    fundingSource.CreatedAt = DateTime.UtcNow;
                    fundingSource.UpdatedAt = DateTime.UtcNow;
                    _context.Add(fundingSource);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Funding source '{fundingSource.Name}' has been created successfully.";
                    return RedirectToAction(nameof(FundingSources));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating funding source");
                TempData["ErrorMessage"] = "An error occurred while creating the funding source. Please try again.";
            }
        }

        return View("~/Views/Admin/FundingSource/Create.cshtml", fundingSource);
    }

    // GET: Admin/EditFundingSource/5
    public async Task<IActionResult> EditFundingSource(int? id)
    {
        if (id == null)
        {
            return NotFound();
        }

        var fundingSource = await _context.FundingSources.FindAsync(id);
        if (fundingSource == null)
        {
            return NotFound();
        }

        return View("~/Views/Admin/FundingSource/Edit.cshtml", fundingSource);
    }

    // POST: Admin/EditFundingSource/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditFundingSource(int id, [Bind("Id,Code,Name,Description,SortOrder,IsActive,CreatedAt")] FundingSource fundingSource)
    {
        if (id != fundingSource.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                // Check if code already exists (excluding current record)
                if (await _context.FundingSources.AnyAsync(fs => fs.Code == fundingSource.Code && fs.Id != id))
                {
                    ModelState.AddModelError("Code", "A funding source with this code already exists.");
                }
                else
                {
                    fundingSource.UpdatedAt = DateTime.UtcNow;
                    _context.Update(fundingSource);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Funding source '{fundingSource.Name}' has been updated successfully.";
                    return RedirectToAction(nameof(FundingSources));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating funding source");
                TempData["ErrorMessage"] = "An error occurred while updating the funding source. Please try again.";
            }
        }

        return View("~/Views/Admin/FundingSource/Edit.cshtml", fundingSource);
    }

    // POST: Admin/DeleteFundingSource/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteFundingSource(int id)
    {
        try
        {
            var fundingSource = await _context.FundingSources.FindAsync(id);
            if (fundingSource != null)
            {
                // Check if any projects are using this funding source
                var projectsUsingSource = await _context.ProjectFundingAllocations.AnyAsync(pfa => pfa.FundingSourceId == id);
                if (projectsUsingSource)
                {
                    TempData["ErrorMessage"] = $"Cannot delete funding source '{fundingSource.Name}' because it is being used by one or more projects.";
                }
                else
                {
                    _context.FundingSources.Remove(fundingSource);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Funding source '{fundingSource.Name}' has been deleted successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting funding source");
            TempData["ErrorMessage"] = "An error occurred while deleting the funding source. Please try again.";
        }

        return RedirectToAction(nameof(FundingSources));
    }

    // ========================================
    // SETTINGS - WCAG Criteria
    // ========================================

    // GET: Admin/WcagCriteria
    public async Task<IActionResult> WcagCriteria()
    {
        var wcagCriteria = await _context.WcagCriteria
            .OrderBy(w => w.Criterion)
            .ToListAsync();
        
        return View("~/Views/Admin/Settings/WcagCriteria.cshtml", wcagCriteria);
    }

    // GET: Admin/CreateWcagCriterion
    public IActionResult CreateWcagCriterion()
    {
        return View("~/Views/Admin/Settings/CreateWcagCriterion.cshtml");
    }

    // POST: Admin/CreateWcagCriterion
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateWcagCriterion([Bind("Criterion,Title,Description,Url,Level,Version,SortOrder,IsActive")] WcagCriterion wcagCriterion)
    {
        if (ModelState.IsValid)
        {
            try
            {
                if (await _context.WcagCriteria.AnyAsync(w => w.Criterion == wcagCriterion.Criterion && w.Version == wcagCriterion.Version))
                {
                    ModelState.AddModelError("Criterion", "A WCAG criterion with this reference and version already exists.");
                }
                else
                {
                    wcagCriterion.CreatedAt = DateTime.UtcNow;
                    wcagCriterion.UpdatedAt = DateTime.UtcNow;
                    _context.Add(wcagCriterion);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"WCAG criterion '{wcagCriterion.Criterion} - {wcagCriterion.Title}' has been created successfully.";
                    return RedirectToAction(nameof(WcagCriteria));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating WCAG criterion");
                ModelState.AddModelError("", "An error occurred while creating the WCAG criterion. Please try again.");
            }
        }
        
        return View("~/Views/Admin/Settings/CreateWcagCriterion.cshtml", wcagCriterion);
    }

    // GET: Admin/EditWcagCriterion/5
    public async Task<IActionResult> EditWcagCriterion(int? id)
    {
        if (id == null)
        {
            return NotFound();
        }

        var wcagCriterion = await _context.WcagCriteria.FindAsync(id);
        if (wcagCriterion == null)
        {
            return NotFound();
        }

        return View("~/Views/Admin/Settings/EditWcagCriterion.cshtml", wcagCriterion);
    }

    // POST: Admin/EditWcagCriterion/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditWcagCriterion(int id, [Bind("Id,Criterion,Title,Description,Url,Level,Version,SortOrder,IsActive")] WcagCriterion wcagCriterion)
    {
        if (id != wcagCriterion.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                if (await _context.WcagCriteria.AnyAsync(w => w.Criterion == wcagCriterion.Criterion && w.Version == wcagCriterion.Version && w.Id != id))
                {
                    ModelState.AddModelError("Criterion", "A WCAG criterion with this reference and version already exists.");
                }
                else
                {
                    var existingCriterion = await _context.WcagCriteria.FindAsync(id);
                    if (existingCriterion == null)
                    {
                        return NotFound();
                    }

                    existingCriterion.Criterion = wcagCriterion.Criterion;
                    existingCriterion.Title = wcagCriterion.Title;
                    existingCriterion.Description = wcagCriterion.Description;
                    existingCriterion.Url = wcagCriterion.Url;
                    existingCriterion.Level = wcagCriterion.Level;
                    existingCriterion.Version = wcagCriterion.Version;
                    existingCriterion.SortOrder = wcagCriterion.SortOrder;
                    existingCriterion.IsActive = wcagCriterion.IsActive;
                    existingCriterion.UpdatedAt = DateTime.UtcNow;
                    
                    _context.Update(existingCriterion);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"WCAG criterion '{wcagCriterion.Criterion} - {wcagCriterion.Title}' has been updated successfully.";
                    return RedirectToAction(nameof(WcagCriteria));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating WCAG criterion");
                TempData["ErrorMessage"] = "An error occurred while updating the WCAG criterion. Please try again.";
            }
        }

        return View("~/Views/Admin/Settings/EditWcagCriterion.cshtml", wcagCriterion);
    }

    // GET: Admin/DeleteWcagCriterion/5
    public async Task<IActionResult> DeleteWcagCriterion(int? id)
    {
        if (id == null)
        {
            return NotFound();
        }

        var wcagCriterion = await _context.WcagCriteria.FindAsync(id);
        if (wcagCriterion == null)
        {
            return NotFound();
        }

        // Check if any issues are using this criterion
        var issueCount = await _context.IssueWcagCriteria.CountAsync(iwc => iwc.WcagCriterionId == id);
        ViewBag.IssueCount = issueCount;

        return View("~/Views/Admin/Settings/DeleteWcagCriterion.cshtml", wcagCriterion);
    }

    // POST: Admin/DeleteWcagCriterion/5
    [HttpPost, ActionName("DeleteWcagCriterion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteWcagCriterionConfirmed(int id)
    {
        try
        {
            var wcagCriterion = await _context.WcagCriteria.FindAsync(id);
            if (wcagCriterion != null)
            {
                // Check if any issues are using this criterion
                var issuesUsingCriterion = await _context.IssueWcagCriteria.AnyAsync(iwc => iwc.WcagCriterionId == id);
                if (issuesUsingCriterion)
                {
                    TempData["ErrorMessage"] = $"Cannot delete WCAG criterion '{wcagCriterion.Criterion} - {wcagCriterion.Title}' because it is being used by one or more accessibility issues.";
                }
                else
                {
                    _context.WcagCriteria.Remove(wcagCriterion);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"WCAG criterion '{wcagCriterion.Criterion} - {wcagCriterion.Title}' has been deleted successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting WCAG criterion");
            TempData["ErrorMessage"] = "An error occurred while deleting the WCAG criterion. Please try again.";
        }

        return RedirectToAction(nameof(WcagCriteria));
    }

    // GET: Admin/SearchWcagCriteria (for autocomplete)
    [HttpGet]
    public async Task<IActionResult> SearchWcagCriteria(string q)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Json(new { results = new object[0] });
        }

        var criteria = await _context.WcagCriteria
            .Where(w => w.IsActive && 
                       (w.Criterion.Contains(q) || 
                        w.Title.Contains(q)))
            .OrderBy(w => w.Criterion)
            .Take(20)
            .Select(w => new
            {
                id = w.Id,
                criterion = w.Criterion,
                title = w.Title,
                level = w.Level,
                version = w.Version,
                text = $"{w.Criterion} - {w.Title} (Level {w.Level})"
            })
            .ToListAsync();

        return Json(new { results = criteria });
    }

    // ========================================
    // SETTINGS - Business Areas
    // ========================================

    // GET: Admin/BusinessAreas
    public async Task<IActionResult> BusinessAreas()
    {
        var businessAreas = await _context.BusinessAreaLookups
            .OrderBy(ba => ba.SortOrder)
            .ThenBy(ba => ba.Name)
            .ToListAsync();
        
        return View("~/Views/Admin/Settings/BusinessAreas.cshtml", businessAreas);
    }

    // POST: Admin/CreateBusinessArea
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateBusinessArea([Bind("Name,Description,SortOrder,IsActive")] BusinessAreaLookup businessArea)
    {
        if (ModelState.IsValid)
        {
            try
            {
                // Check if name already exists
                if (await _context.BusinessAreaLookups.AnyAsync(ba => ba.Name == businessArea.Name))
                {
                    TempData["ErrorMessage"] = "A business area with this name already exists.";
                }
                else
                {
                    businessArea.CreatedAt = DateTime.UtcNow;
                    businessArea.UpdatedAt = DateTime.UtcNow;
                    _context.Add(businessArea);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Business area '{businessArea.Name}' has been created successfully.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating business area");
                TempData["ErrorMessage"] = "An error occurred while creating the business area. Please try again.";
            }
        }

        return RedirectToAction(nameof(BusinessAreas));
    }

    // POST: Admin/EditBusinessArea
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditBusinessArea(int id, [Bind("Id,Name,Description,SortOrder,IsActive")] BusinessAreaLookup businessArea)
    {
        if (id != businessArea.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                // Check if name already exists for a different record
                if (await _context.BusinessAreaLookups.AnyAsync(ba => ba.Name == businessArea.Name && ba.Id != id))
                {
                    TempData["ErrorMessage"] = "A business area with this name already exists.";
                }
                else
                {
                    var existingBusinessArea = await _context.BusinessAreaLookups.FindAsync(id);
                    if (existingBusinessArea == null)
                    {
                        return NotFound();
                    }

                    existingBusinessArea.Name = businessArea.Name;
                    existingBusinessArea.Description = businessArea.Description;
                    existingBusinessArea.SortOrder = businessArea.SortOrder;
                    existingBusinessArea.IsActive = businessArea.IsActive;
                    existingBusinessArea.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Business area '{businessArea.Name}' has been updated successfully.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating business area");
                TempData["ErrorMessage"] = "An error occurred while updating the business area. Please try again.";
            }
        }

        return RedirectToAction(nameof(BusinessAreas));
    }

    // POST: Admin/DeleteBusinessArea
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteBusinessArea(int id)
    {
        try
        {
            var businessArea = await _context.BusinessAreaLookups.FindAsync(id);
            if (businessArea != null)
            {
                // Check if any projects are using this business area
                var projectCount = await _context.Projects.CountAsync(p => p.BusinessAreaId == businessArea.Id && !p.IsDeleted);
                if (projectCount > 0)
                {
                    TempData["ErrorMessage"] = $"Cannot delete business area '{businessArea.Name}' as it is being used by {projectCount} project(s).";
                }
                else
                {
                    _context.BusinessAreaLookups.Remove(businessArea);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Business area '{businessArea.Name}' has been deleted successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting business area");
            TempData["ErrorMessage"] = "An error occurred while deleting the business area. Please try again.";
        }

        return RedirectToAction(nameof(BusinessAreas));
    }

    // GET: api/Admin/BusinessAreas/list — JSON list (do not use api/Admin/BusinessAreas; that URL is served by BusinessAreas() for the HTML admin page)
    [HttpGet]
    [Route("api/Admin/BusinessAreas/list")]
    public async Task<IActionResult> GetBusinessAreasApi()
    {
        try
        {
            var businessAreas = await _context.BusinessAreaLookups
                .Where(ba => ba.IsActive)
                .OrderBy(ba => ba.SortOrder)
                .ThenBy(ba => ba.Name)
                .Select(ba => new
                {
                    id = ba.Id,
                    name = ba.Name,
                    description = ba.Description,
                    sortOrder = ba.SortOrder,
                    isActive = ba.IsActive
                })
                .ToListAsync();

            return Json(businessAreas);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching business areas for API");
            return StatusCode(500, new { error = "An error occurred while fetching business areas." });
        }
    }

    // GET: api/Admin/BusinessAreas/PreviewSync
    [HttpGet]
    [Route("api/Admin/BusinessAreas/PreviewSync")]
    public async Task<IActionResult> PreviewBusinessAreasSync()
    {
        try
        {
            var cmsBusinessAreas = await GetBusinessAreasFromCmsAsync();
            var existingBusinessAreas = await _context.BusinessAreaLookups.ToListAsync();
            
            var matches = new List<object>();
            var newAreas = new List<object>();
            var exactMatches = new List<object>();

            foreach (var cmsBa in cmsBusinessAreas)
            {
                // Try to find a match by exact name first
                var exactMatch = existingBusinessAreas.FirstOrDefault(ba => 
                    ba.Name.Equals(cmsBa.Name, StringComparison.OrdinalIgnoreCase));
                
                if (exactMatch != null)
                {
                    exactMatches.Add(new { cmsName = cmsBa.Name, existingName = exactMatch.Name });
                    continue;
                }

                // Try to match by name variations (e.g., "CXD" -> "Customer Experience and Design")
                var nameMatch = FindMatchingBusinessArea(cmsBa.Name, existingBusinessAreas);
                
                if (nameMatch != null)
                {
                    matches.Add(new { cmsName = cmsBa.Name, existingName = nameMatch.Name });
                    continue;
                }

                // No match found, will be created
                newAreas.Add(new { name = cmsBa.Name, description = cmsBa.Description });
            }

            return Json(new 
            { 
                success = true,
                exactMatches = exactMatches,
                matches = matches,
                newAreas = newAreas
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error previewing business areas sync");
            return Json(new 
            { 
                success = false, 
                message = "An error occurred while previewing sync. Please check the logs." 
            });
        }
    }

    // POST: api/Admin/BusinessAreas/SyncFromCms
    [HttpPost]
    [Route("api/Admin/BusinessAreas/SyncFromCms")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncBusinessAreasFromCms()
    {
        _logger.LogInformation("SyncBusinessAreasFromCms endpoint called");
        try
        {
            // Get confirmed matches from form data
            List<ConfirmedMatch>? confirmedMatches = null;
            
            if (Request.Form.ContainsKey("confirmedMatches"))
            {
                var confirmedMatchesJson = Request.Form["confirmedMatches"].ToString();
                _logger.LogInformation("Received confirmedMatches: {Json}", confirmedMatchesJson);
                
                if (!string.IsNullOrEmpty(confirmedMatchesJson) && confirmedMatchesJson != "[]")
                {
                    try
                    {
                        confirmedMatches = System.Text.Json.JsonSerializer.Deserialize<List<ConfirmedMatch>>(confirmedMatchesJson);
                        _logger.LogInformation("Parsed {Count} confirmed matches from request", confirmedMatches?.Count ?? 0);
                        if (confirmedMatches != null)
                        {
                            foreach (var match in confirmedMatches)
                            {
                                _logger.LogInformation("Confirmed match: '{ExistingName}' -> '{CmsName}'", 
                                    match.ExistingName, match.CmsName);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // If JSON parsing fails, continue without confirmed matches
                        _logger.LogWarning(ex, "Failed to parse confirmedMatches JSON: {Json}", confirmedMatchesJson);
                    }
                }
                else
                {
                    _logger.LogInformation("confirmedMatches is empty or '[]'");
                }
            }
            else
            {
                _logger.LogInformation("No confirmedMatches key in request form");
            }

            var cmsBusinessAreas = await GetBusinessAreasFromCmsAsync();
            _logger.LogInformation("Retrieved {Count} business areas from CMS", cmsBusinessAreas.Count);
            
            // Load existing business areas - ensure they're tracked by EF
            var existingBusinessAreas = await _context.BusinessAreaLookups.ToListAsync();
            _logger.LogInformation("Found {Count} existing business areas in database", existingBusinessAreas.Count);
            
            int created = 0;
            int updated = 0;
            int matched = 0;
            var appliedMatches = new List<object>();

            foreach (var cmsBa in cmsBusinessAreas)
            {
                // Try to find a match by exact name first
                var exactMatch = existingBusinessAreas.FirstOrDefault(ba => 
                    ba.Name.Equals(cmsBa.Name, StringComparison.OrdinalIgnoreCase));
                
                if (exactMatch != null)
                {
                    // Update existing record with CMS data
                    _logger.LogInformation("Exact match found: '{Name}' (Id: {Id}), updating sort order from {OldSort} to {NewSort}", 
                        exactMatch.Name, exactMatch.Id, exactMatch.SortOrder, cmsBa.SortOrder);
                    
                    // Reload from database to ensure we have a tracked entity
                    var trackedEntity = await _context.BusinessAreaLookups.FindAsync(exactMatch.Id);
                    if (trackedEntity != null)
                    {
                        trackedEntity.SortOrder = cmsBa.SortOrder;
                        trackedEntity.UpdatedAt = DateTime.UtcNow;
                        // Keep existing description and IsActive status unless they're empty/null
                        if (string.IsNullOrWhiteSpace(trackedEntity.Description))
                        {
                            trackedEntity.Description = cmsBa.Description;
                        }
                        updated++;
                        matched++;
                    }
                    continue;
                }

                // Try to match by name variations (e.g., "CXD" -> "Customer Experience and Design")
                var nameMatch = FindMatchingBusinessArea(cmsBa.Name, existingBusinessAreas);
                
                if (nameMatch != null)
                {
                    // Check if this match was confirmed (if confirmation was required)
                    var originalName = nameMatch.Name;
                    var needsConfirmation = !nameMatch.Name.Equals(cmsBa.Name, StringComparison.OrdinalIgnoreCase);
                    
                    if (needsConfirmation)
                    {
                        // This match needs confirmation - check if it was confirmed
                        var isConfirmed = confirmedMatches != null && confirmedMatches.Any(cm => 
                            cm.ExistingName.Equals(originalName, StringComparison.OrdinalIgnoreCase) &&
                            cm.CmsName.Equals(cmsBa.Name, StringComparison.OrdinalIgnoreCase));
                        
                        _logger.LogInformation("Match found: '{OriginalName}' -> '{CmsName}', Confirmed: {IsConfirmed}", 
                            originalName, cmsBa.Name, isConfirmed);
                        
                        if (!isConfirmed)
                        {
                            _logger.LogInformation("Skipping unconfirmed match: '{OriginalName}' -> '{CmsName}'", 
                                originalName, cmsBa.Name);
                            continue; // Skip unconfirmed matches
                        }
                    }
                    
                    // Update the matched record with CMS name and data
                    _logger.LogInformation("Updating business area: '{OriginalName}' (Id: {Id}) -> '{CmsName}'", 
                        originalName, nameMatch.Id, cmsBa.Name);
                    
                    // Reload from database to ensure we have a tracked entity
                    var trackedEntity = await _context.BusinessAreaLookups.FindAsync(nameMatch.Id);
                    if (trackedEntity != null)
                    {
                        trackedEntity.Name = cmsBa.Name; // Update to CMS name
                        trackedEntity.SortOrder = cmsBa.SortOrder;
                        trackedEntity.UpdatedAt = DateTime.UtcNow;
                        if (string.IsNullOrWhiteSpace(trackedEntity.Description))
                        {
                            trackedEntity.Description = cmsBa.Description;
                        }
                        updated++;
                        matched++;
                    }
                    
                    if (needsConfirmation)
                    {
                        appliedMatches.Add(new { existingName = originalName, cmsName = cmsBa.Name });
                    }
                    continue;
                }

                // No match found, create new record
                var newBusinessArea = new BusinessAreaLookup
                {
                    Name = cmsBa.Name,
                    Description = cmsBa.Description,
                    SortOrder = cmsBa.SortOrder,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                
                _context.BusinessAreaLookups.Add(newBusinessArea);
                created++;
            }

            _logger.LogInformation("About to save changes. Created: {Created}, Updated: {Updated}, Matched: {Matched}", 
                created, updated, matched);
            
            // Force Entity Framework to detect changes
            _context.ChangeTracker.DetectChanges();
            
            // Log what EF thinks has changed
            var changedEntries = _context.ChangeTracker.Entries()
                .Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Modified || 
                           e.State == Microsoft.EntityFrameworkCore.EntityState.Added)
                .ToList();
            _logger.LogInformation("ChangeTracker found {Count} modified/added entities", changedEntries.Count);
            foreach (var entry in changedEntries)
            {
                if (entry.Entity is BusinessAreaLookup ba)
                {
                    _logger.LogInformation("Entity {Id} ({Name}) state: {State}", ba.Id, ba.Name, entry.State);
                    if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Modified)
                    {
                        foreach (var prop in entry.Properties.Where(p => p.IsModified))
                        {
                            _logger.LogInformation("  Property {Property}: {Original} -> {Current}", 
                                prop.Metadata.Name, prop.OriginalValue, prop.CurrentValue);
                        }
                    }
                }
            }
            
            var saveResult = await _context.SaveChangesAsync();
            _logger.LogInformation("SaveChangesAsync returned: {Result} (created: {Created}, updated: {Updated}, matched: {Matched})", 
                saveResult, created, updated, matched);

            // Verify the changes were actually saved by reloading
            if (updated > 0 || created > 0)
            {
                var verifyCount = await _context.BusinessAreaLookups.CountAsync();
                _logger.LogInformation("Verification: Total business areas in database after save: {Count}", verifyCount);
            }

            return Json(new 
            { 
                success = true, 
                message = $"Sync completed: {created} created, {updated} updated, {matched} matched from CMS. {saveResult} records saved.",
                created = created,
                updated = updated,
                matched = matched,
                matches = appliedMatches,
                recordsSaved = saveResult
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing business areas from CMS");
            return Json(new 
            { 
                success = false, 
                message = "An error occurred while syncing business areas from CMS. Please check the logs." 
            });
        }
    }

    private class ConfirmedMatch
    {
        public string ExistingName { get; set; } = string.Empty;
        public string CmsName { get; set; } = string.Empty;
    }

    private BusinessAreaLookup? FindMatchingBusinessArea(string cmsName, List<BusinessAreaLookup> existingAreas)
    {
        var normalizedCmsName = cmsName.Trim();
        
        foreach (var existing in existingAreas)
        {
            var existingName = existing.Name.Trim();
            
            // Check if names match exactly (case-insensitive)
            if (normalizedCmsName.Equals(existingName, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }
            
            // Handle CXD -> Customer Experience and Design mapping
            // If CMS has "Customer Experience and Design" and Compass has "CXD" (or contains "CXD")
            if (normalizedCmsName.Equals("Customer Experience and Design", StringComparison.OrdinalIgnoreCase))
            {
                // Match with entries containing "CXD" (case-insensitive)
                if (existingName.Contains("CXD", StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }
            }
            
            // If CMS has something with "CXD" and Compass has "Customer Experience and Design"
            if (normalizedCmsName.Contains("CXD", StringComparison.OrdinalIgnoreCase) &&
                existingName.Contains("Customer Experience and Design", StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }
            
            // Check if one name contains the other (for partial matches)
            // This handles cases where names are similar but not exact
            if (normalizedCmsName.Contains(existingName, StringComparison.OrdinalIgnoreCase) ||
                existingName.Contains(normalizedCmsName, StringComparison.OrdinalIgnoreCase))
            {
                // Only match if the shorter name is at least 3 characters (to avoid false matches)
                var shorterLength = Math.Min(normalizedCmsName.Length, existingName.Length);
                if (shorterLength >= 3)
                {
                    return existing;
                }
            }
        }
        
        return null;
    }

    private async Task<List<CmsBusinessArea>> GetBusinessAreasFromCmsAsync()
    {
        var businessAreas = new List<CmsBusinessArea>();
        
        try
        {
            // Use the ProductsApiService which already has working CMS API integration
            var categoryValues = await _productsApiService.GetBusinessAreaCategoryValuesAsync();
            
            foreach (var cv in categoryValues)
            {
                if (!string.IsNullOrEmpty(cv.Name))
                {
                    businessAreas.Add(new CmsBusinessArea
                    {
                        Name = cv.Name,
                        SortOrder = cv.SortOrder ?? 0,
                        Description = null // Description not available in CategoryValueDto
                    });
                }
            }
            
            _logger.LogInformation("Found {Count} business areas from CMS", businessAreas.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching business areas from CMS");
        }
        
        return businessAreas;
    }

    private class CmsBusinessArea
    {
        public string Name { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public string? Description { get; set; }
    }

    // ========================================
    // SETTINGS - Phases
    // ========================================

    // GET: Admin/Phases
    public async Task<IActionResult> Phases()
    {
        var phases = await _context.PhaseLookups
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();
        
        return View("~/Views/Admin/Settings/Phases.cshtml", phases);
    }

    // POST: Admin/CreatePhase
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePhase([Bind("Name,Description,SortOrder,IsActive")] PhaseLookup phase)
    {
        if (ModelState.IsValid)
        {
            try
            {
                // Check if name already exists
                if (await _context.PhaseLookups.AnyAsync(p => p.Name == phase.Name))
                {
                    TempData["ErrorMessage"] = "A phase with this name already exists.";
                }
                else
                {
                    phase.CreatedAt = DateTime.UtcNow;
                    phase.UpdatedAt = DateTime.UtcNow;
                    _context.Add(phase);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Phase '{phase.Name}' has been created successfully.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating phase");
                TempData["ErrorMessage"] = "An error occurred while creating the phase. Please try again.";
            }
        }

        return RedirectToAction(nameof(Phases));
    }

    // POST: Admin/EditPhase
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditPhase(int id, string name, string? description, int sortOrder, bool isActive = false)
    {
        _logger.LogInformation("EditPhase POST called - ID: {Id}, Name: {Name}, Description: {Description}, SortOrder: {SortOrder}, IsActive: {IsActive}", 
            id, name, description, sortOrder, isActive);
        
        // Also check form values directly if model binding failed
        var formId = Request.Form["id"].ToString();
        var formName = Request.Form["name"].ToString();
        var formDescription = Request.Form["description"].ToString();
        var formSortOrder = Request.Form["sortOrder"].ToString();
        var formIsActive = Request.Form["isActive"].ToString();
        
        _logger.LogInformation("Form values - id: {FormId}, name: {FormName}, description: {FormDescription}, sortOrder: {FormSortOrder}, isActive: {FormIsActive}", 
            formId, formName, formDescription, formSortOrder, formIsActive);
        
        try
        {
            // Use form values if model binding didn't work
            if (id == 0 && !string.IsNullOrEmpty(formId) && int.TryParse(formId, out int parsedId))
            {
                id = parsedId;
            }
            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(formName))
            {
                name = formName;
            }
            if (string.IsNullOrEmpty(description) && !string.IsNullOrEmpty(formDescription))
            {
                description = formDescription;
            }
            if (sortOrder == 0 && !string.IsNullOrEmpty(formSortOrder) && int.TryParse(formSortOrder, out int parsedSortOrder))
            {
                sortOrder = parsedSortOrder;
            }
            if (!string.IsNullOrEmpty(formIsActive))
            {
                isActive = formIsActive == "true" || formIsActive.Contains("true");
            }
            
            var existingPhase = await _context.PhaseLookups.FindAsync(id);
            if (existingPhase == null)
            {
                _logger.LogWarning("Phase not found with ID: {Id}", id);
                TempData["ErrorMessage"] = "Phase not found.";
                return RedirectToAction(nameof(Phases));
            }

            // Check if name already exists for a different record
            if (await _context.PhaseLookups.AnyAsync(p => p.Name == name && p.Id != id))
            {
                TempData["ErrorMessage"] = "A phase with this name already exists.";
            }
            else
            {
                _logger.LogInformation("Updating phase {Id} - Name: {Name}, IsActive: {IsActive}", id, name, isActive);
                
                existingPhase.Name = name;
                existingPhase.Description = description;
                existingPhase.SortOrder = sortOrder;
                existingPhase.IsActive = isActive;
                existingPhase.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();
                
                TempData["SuccessMessage"] = $"Phase '{name}' has been updated successfully.";
                _logger.LogInformation("Phase {Id} updated successfully", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating phase {PhaseId}", id);
            TempData["ErrorMessage"] = "An error occurred while updating the phase. Please try again.";
        }

        return RedirectToAction(nameof(Settings));
    }

    // POST: Admin/DeletePhase
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePhase(int id)
    {
        try
        {
            var phase = await _context.PhaseLookups.FindAsync(id);
            if (phase != null)
            {
                // Check if any projects are using this phase
                var projectCount = await _context.Projects.CountAsync(p => p.PhaseId == phase.Id && !p.IsDeleted);
                if (projectCount > 0)
                {
                    TempData["ErrorMessage"] = $"Cannot delete phase '{phase.Name}' as it is being used by {projectCount} project(s).";
                }
                else
                {
                    _context.PhaseLookups.Remove(phase);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Phase '{phase.Name}' has been deleted successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting phase");
            TempData["ErrorMessage"] = "An error occurred while deleting the phase. Please try again.";
        }

        return RedirectToAction(nameof(Phases));
    }

    // ========================================
    // SETTINGS - RAG Statuses
    // ========================================

    // GET: Admin/RagStatuses
    public async Task<IActionResult> RagStatuses()
    {
        var ragStatuses = await _context.RagStatusLookups
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name)
            .ToListAsync();
        
        return View("~/Views/Admin/Settings/RagStatuses.cshtml", ragStatuses);
    }

    // POST: Admin/CreateRagStatus
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRagStatus([Bind("Name,Description,SortOrder,IsActive,CssClass")] RagStatusLookup ragStatus)
    {
        if (ModelState.IsValid)
        {
            try
            {
                // Check if name already exists
                if (await _context.RagStatusLookups.AnyAsync(r => r.Name == ragStatus.Name))
                {
                    TempData["ErrorMessage"] = "A RAG status with this name already exists.";
                }
                else
                {
                    ragStatus.CreatedAt = DateTime.UtcNow;
                    ragStatus.UpdatedAt = DateTime.UtcNow;
                    _context.Add(ragStatus);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"RAG status '{ragStatus.Name}' has been created successfully.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating RAG status");
                TempData["ErrorMessage"] = "An error occurred while creating the RAG status. Please try again.";
            }
        }

        return RedirectToAction(nameof(RagStatuses));
    }

    // POST: Admin/EditRagStatus
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRagStatus(int id, string name, string? description, int sortOrder, bool isActive = false, string? cssClass = null)
    {
        _logger.LogInformation("EditRagStatus POST called - ID: {Id}, Name: {Name}, Description: {Description}, SortOrder: {SortOrder}, IsActive: {IsActive}, CssClass: {CssClass}", 
            id, name, description, sortOrder, isActive, cssClass);
        
        // Also check form values directly if model binding failed
        var formId = Request.Form["id"].ToString();
        var formName = Request.Form["name"].ToString();
        var formDescription = Request.Form["description"].ToString();
        var formSortOrder = Request.Form["sortOrder"].ToString();
        var formIsActive = Request.Form["isActive"].ToString();
        var formCssClass = Request.Form["cssClass"].ToString();
        
        _logger.LogInformation("Form values - id: {FormId}, name: {FormName}, description: {FormDescription}, sortOrder: {FormSortOrder}, isActive: {FormIsActive}, cssClass: {FormCssClass}", 
            formId, formName, formDescription, formSortOrder, formIsActive, formCssClass);
        
        try
        {
            // Use form values if model binding didn't work
            if (id == 0 && !string.IsNullOrEmpty(formId) && int.TryParse(formId, out int parsedId))
            {
                id = parsedId;
            }
            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(formName))
            {
                name = formName;
            }
            if (string.IsNullOrEmpty(description) && !string.IsNullOrEmpty(formDescription))
            {
                description = formDescription;
            }
            if (sortOrder == 0 && !string.IsNullOrEmpty(formSortOrder) && int.TryParse(formSortOrder, out int parsedSortOrder))
            {
                sortOrder = parsedSortOrder;
            }
            if (!string.IsNullOrEmpty(formIsActive))
            {
                isActive = formIsActive == "true" || formIsActive.Contains("true");
            }
            if (string.IsNullOrEmpty(cssClass) && !string.IsNullOrEmpty(formCssClass))
            {
                cssClass = formCssClass;
            }
            
            var existingRagStatus = await _context.RagStatusLookups.FindAsync(id);
            if (existingRagStatus == null)
            {
                _logger.LogWarning("RAG status not found with ID: {Id}", id);
                TempData["ErrorMessage"] = "RAG status not found.";
                return RedirectToAction(nameof(RagStatuses));
            }

            // Check if name already exists for a different record
            if (await _context.RagStatusLookups.AnyAsync(r => r.Name == name && r.Id != id))
            {
                TempData["ErrorMessage"] = "A RAG status with this name already exists.";
            }
            else
            {
                _logger.LogInformation("Updating RAG status {Id} - Name: {Name}, IsActive: {IsActive}, CssClass: {CssClass}", id, name, isActive, cssClass);
                
                existingRagStatus.Name = name;
                existingRagStatus.Description = description;
                existingRagStatus.SortOrder = sortOrder;
                existingRagStatus.IsActive = isActive;
                existingRagStatus.CssClass = cssClass;
                existingRagStatus.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();
                
                TempData["SuccessMessage"] = $"RAG status '{name}' has been updated successfully.";
                _logger.LogInformation("RAG status {Id} updated successfully", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating RAG status {RagStatusId}", id);
            TempData["ErrorMessage"] = "An error occurred while updating the RAG status. Please try again.";
        }

        return RedirectToAction(nameof(RagStatuses));
    }

    // POST: Admin/DeleteRagStatus
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRagStatus(int id)
    {
        try
        {
            var ragStatus = await _context.RagStatusLookups.FindAsync(id);
            if (ragStatus != null)
            {
                // Check if any projects are using this RAG status
                var projectCount = await _context.Projects.CountAsync(p => p.RagStatusLookupId == ragStatus.Id && !p.IsDeleted);
                if (projectCount > 0)
                {
                    TempData["ErrorMessage"] = $"Cannot delete RAG status '{ragStatus.Name}' as it is being used by {projectCount} project(s).";
                }
                else
                {
                    _context.RagStatusLookups.Remove(ragStatus);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"RAG status '{ragStatus.Name}' has been deleted successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting RAG status");
            TempData["ErrorMessage"] = "An error occurred while deleting the RAG status. Please try again.";
        }

        return RedirectToAction(nameof(RagStatuses));
    }

    // ========================================
    // SETTINGS - Business Case Statuses
    // ========================================

    // GET: Admin/BusinessCaseStatuses
    public async Task<IActionResult> BusinessCaseStatuses()
    {
        var statuses = await _context.BusinessCaseStatusLookups
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Name)
            .ToListAsync();
        
        return View("~/Views/Admin/Settings/BusinessCaseStatuses.cshtml", statuses);
    }

    // POST: Admin/CreateBusinessCaseStatus
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateBusinessCaseStatus([Bind("Name,Description,SortOrder,IsActive,CssClass")] BusinessCaseStatusLookup status)
    {
        if (ModelState.IsValid)
        {
            try
            {
                // Check if name already exists
                if (await _context.BusinessCaseStatusLookups.AnyAsync(s => s.Name == status.Name))
                {
                    TempData["ErrorMessage"] = "A business case status with this name already exists.";
                }
                else
                {
                    status.CreatedAt = DateTime.UtcNow;
                    status.UpdatedAt = DateTime.UtcNow;
                    _context.Add(status);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Business case status '{status.Name}' has been created successfully.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating business case status");
                TempData["ErrorMessage"] = "An error occurred while creating the business case status. Please try again.";
            }
        }

        return RedirectToAction(nameof(BusinessCaseStatuses));
    }

    // POST: Admin/EditBusinessCaseStatus
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditBusinessCaseStatus(int id, string name, string? description, int sortOrder, bool isActive = false, string? cssClass = null)
    {
        _logger.LogInformation("EditBusinessCaseStatus POST called - ID: {Id}, Name: {Name}, Description: {Description}, SortOrder: {SortOrder}, IsActive: {IsActive}, CssClass: {CssClass}", 
            id, name, description, sortOrder, isActive, cssClass);
        
        // Also check form values directly if model binding failed
        var formId = Request.Form["id"].ToString();
        var formName = Request.Form["name"].ToString();
        var formDescription = Request.Form["description"].ToString();
        var formSortOrder = Request.Form["sortOrder"].ToString();
        var formIsActive = Request.Form["isActive"].ToString();
        var formCssClass = Request.Form["cssClass"].ToString();
        
        _logger.LogInformation("Form values - id: {FormId}, name: {FormName}, description: {FormDescription}, sortOrder: {FormSortOrder}, isActive: {FormIsActive}, cssClass: {FormCssClass}", 
            formId, formName, formDescription, formSortOrder, formIsActive, formCssClass);
        
        try
        {
            // Use form values if model binding didn't work
            if (id == 0 && !string.IsNullOrEmpty(formId) && int.TryParse(formId, out int parsedId))
            {
                id = parsedId;
            }
            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(formName))
            {
                name = formName;
            }
            if (string.IsNullOrEmpty(description) && !string.IsNullOrEmpty(formDescription))
            {
                description = formDescription;
            }
            if (sortOrder == 0 && !string.IsNullOrEmpty(formSortOrder) && int.TryParse(formSortOrder, out int parsedSortOrder))
            {
                sortOrder = parsedSortOrder;
            }
            if (!string.IsNullOrEmpty(formIsActive))
            {
                isActive = formIsActive == "true" || formIsActive.Contains("true");
            }
            if (string.IsNullOrEmpty(cssClass) && !string.IsNullOrEmpty(formCssClass))
            {
                cssClass = formCssClass;
            }
            
            var existingStatus = await _context.BusinessCaseStatusLookups.FindAsync(id);
            if (existingStatus == null)
            {
                _logger.LogWarning("Business case status not found with ID: {Id}", id);
                TempData["ErrorMessage"] = "Business case status not found.";
                return RedirectToAction(nameof(BusinessCaseStatuses));
            }

            // Check if name already exists for a different record
            if (await _context.BusinessCaseStatusLookups.AnyAsync(s => s.Name == name && s.Id != id))
            {
                TempData["ErrorMessage"] = "A business case status with this name already exists.";
            }
            else
            {
                _logger.LogInformation("Updating business case status {Id} - Name: {Name}, IsActive: {IsActive}, CssClass: {CssClass}", id, name, isActive, cssClass);
                
                existingStatus.Name = name;
                existingStatus.Description = description;
                existingStatus.SortOrder = sortOrder;
                existingStatus.IsActive = isActive;
                existingStatus.CssClass = cssClass;
                existingStatus.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();
                
                TempData["SuccessMessage"] = $"Business case status '{name}' has been updated successfully.";
                _logger.LogInformation("Business case status {Id} updated successfully", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating business case status {StatusId}", id);
            TempData["ErrorMessage"] = "An error occurred while updating the business case status. Please try again.";
        }

        return RedirectToAction(nameof(BusinessCaseStatuses));
    }

    // POST: Admin/DeleteBusinessCaseStatus
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteBusinessCaseStatus(int id)
    {
        try
        {
            var status = await _context.BusinessCaseStatusLookups.FindAsync(id);
            if (status != null)
            {
                // Check if any business cases are using this status
                var businessCaseCount = await _context.BusinessCases.CountAsync(bc => bc.StatusLookupId == status.Id);
                if (businessCaseCount > 0)
                {
                    TempData["ErrorMessage"] = $"Cannot delete business case status '{status.Name}' as it is being used by {businessCaseCount} business case(s).";
                }
                else
                {
                    _context.BusinessCaseStatusLookups.Remove(status);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Business case status '{status.Name}' has been deleted successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting business case status");
            TempData["ErrorMessage"] = "An error occurred while deleting the business case status. Please try again.";
        }

        return RedirectToAction(nameof(BusinessCaseStatuses));
    }

    // ========================================
    // SETTINGS - Activity Types
    // ========================================

    // GET: Admin/ActivityTypes
    public async Task<IActionResult> ActivityTypes()
    {
        var activityTypes = await _context.ActivityTypeLookups
            .OrderBy(at => at.SortOrder)
            .ThenBy(at => at.Name)
            .ToListAsync();
        
        return View("~/Views/Admin/Settings/ActivityTypes.cshtml", activityTypes);
    }

    // POST: Admin/CreateActivityType
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateActivityType([Bind("Name,Description,SortOrder,IsActive")] ActivityTypeLookup activityType)
    {
        if (ModelState.IsValid)
        {
            try
            {
                if (await _context.ActivityTypeLookups.AnyAsync(at => at.Name == activityType.Name))
                {
                    TempData["ErrorMessage"] = "An activity type with this name already exists.";
                }
                else
                {
                    activityType.CreatedAt = DateTime.UtcNow;
                    activityType.UpdatedAt = DateTime.UtcNow;
                    _context.Add(activityType);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Activity type '{activityType.Name}' has been created successfully.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating activity type");
                TempData["ErrorMessage"] = "An error occurred while creating the activity type. Please try again.";
            }
        }

        return RedirectToAction(nameof(ActivityTypes));
    }

    // POST: Admin/EditActivityType
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditActivityType(int id, [Bind("Id,Name,Description,SortOrder,IsActive")] ActivityTypeLookup activityType)
    {
        if (id != activityType.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                if (await _context.ActivityTypeLookups.AnyAsync(at => at.Name == activityType.Name && at.Id != id))
                {
                    TempData["ErrorMessage"] = "An activity type with this name already exists.";
                }
                else
                {
                    var existing = await _context.ActivityTypeLookups.FindAsync(id);
                    if (existing == null)
                    {
                        return NotFound();
                    }

                    existing.Name = activityType.Name;
                    existing.Description = activityType.Description;
                    existing.SortOrder = activityType.SortOrder;
                    existing.IsActive = activityType.IsActive;
                    existing.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Activity type '{activityType.Name}' has been updated successfully.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating activity type");
                TempData["ErrorMessage"] = "An error occurred while updating the activity type. Please try again.";
            }
        }

        return RedirectToAction(nameof(ActivityTypes));
    }

    // POST: Admin/DeleteActivityType
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteActivityType(int id)
    {
        try
        {
            var activityType = await _context.ActivityTypeLookups.FindAsync(id);
            if (activityType != null)
            {
                var projectCount = await _context.Projects.CountAsync(p => p.ActivityTypeLookupId == id && !p.IsDeleted);
                if (projectCount > 0)
                {
                    TempData["ErrorMessage"] = $"Cannot delete activity type '{activityType.Name}' as it is being used by {projectCount} project(s).";
                }
                else
                {
                    _context.ActivityTypeLookups.Remove(activityType);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Activity type '{activityType.Name}' has been deleted successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting activity type");
            TempData["ErrorMessage"] = "An error occurred while deleting the activity type. Please try again.";
        }

        return RedirectToAction(nameof(ActivityTypes));
    }

    // ========================================
    // SETTINGS - Directorates (Redirected to Divisions)
    // ========================================

    // GET: Admin/Directorates
    // Directorates are now managed through Divisions
    public IActionResult Directorates()
    {
        return RedirectToAction("Index", "DivisionBusinessAreaUser", new { area = "" });
    }

    // Note: Directorates are now managed through Divisions
    // Create, Edit, and Delete operations should be done through /Admin/DivisionBusinessAreaUser

    // ========================================
    // SETTINGS - Risk Appetite
    // ========================================

    // GET: Admin/RiskAppetites
    public async Task<IActionResult> RiskAppetites()
    {
        var riskAppetites = await _context.RiskAppetiteLookups
            .OrderBy(ra => ra.SortOrder)
            .ThenBy(ra => ra.Name)
            .ToListAsync();
        
        return View("~/Views/Admin/Settings/RiskAppetites.cshtml", riskAppetites);
    }

    // POST: Admin/CreateRiskAppetite
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRiskAppetite([Bind("Name,Description,SortOrder,IsActive")] RiskAppetiteLookup riskAppetite)
    {
        if (ModelState.IsValid)
        {
            try
            {
                if (await _context.RiskAppetiteLookups.AnyAsync(ra => ra.Name == riskAppetite.Name))
                {
                    TempData["ErrorMessage"] = "A risk appetite with this name already exists.";
                }
                else
                {
                    riskAppetite.CreatedAt = DateTime.UtcNow;
                    riskAppetite.UpdatedAt = DateTime.UtcNow;
                    _context.Add(riskAppetite);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Risk appetite '{riskAppetite.Name}' has been created successfully.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating risk appetite");
                TempData["ErrorMessage"] = "An error occurred while creating the risk appetite. Please try again.";
            }
        }

        return RedirectToAction(nameof(RiskAppetites));
    }

    // POST: Admin/EditRiskAppetite
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRiskAppetite(int id, [Bind("Id,Name,Description,SortOrder,IsActive")] RiskAppetiteLookup riskAppetite)
    {
        if (id != riskAppetite.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                if (await _context.RiskAppetiteLookups.AnyAsync(ra => ra.Name == riskAppetite.Name && ra.Id != id))
                {
                    TempData["ErrorMessage"] = "A risk appetite with this name already exists.";
                }
                else
                {
                    var existing = await _context.RiskAppetiteLookups.FindAsync(id);
                    if (existing == null)
                    {
                        return NotFound();
                    }

                    existing.Name = riskAppetite.Name;
                    existing.Description = riskAppetite.Description;
                    existing.SortOrder = riskAppetite.SortOrder;
                    existing.IsActive = riskAppetite.IsActive;
                    existing.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Risk appetite '{riskAppetite.Name}' has been updated successfully.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating risk appetite");
                TempData["ErrorMessage"] = "An error occurred while updating the risk appetite. Please try again.";
            }
        }

        return RedirectToAction(nameof(RiskAppetites));
    }

    // POST: Admin/DeleteRiskAppetite
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRiskAppetite(int id)
    {
        try
        {
            var riskAppetite = await _context.RiskAppetiteLookups.FindAsync(id);
            if (riskAppetite != null)
            {
                var projectCount = await _context.Projects.CountAsync(p => p.RiskAppetiteLookupId == id && !p.IsDeleted);
                if (projectCount > 0)
                {
                    TempData["ErrorMessage"] = $"Cannot delete risk appetite '{riskAppetite.Name}' as it is being used by {projectCount} project(s).";
                }
                else
                {
                    _context.RiskAppetiteLookups.Remove(riskAppetite);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Risk appetite '{riskAppetite.Name}' has been deleted successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting risk appetite");
            TempData["ErrorMessage"] = "An error occurred while deleting the risk appetite. Please try again.";
        }

        return RedirectToAction(nameof(RiskAppetites));
    }

    // ========================================
    // SETTINGS - GDD Roles
    // ========================================

    // GET: Admin/GddRoles
    public async Task<IActionResult> GddRoles()
    {
        var roles = await _context.GddRoles
            .OrderBy(r => r.RoleFamily)
            .ThenBy(r => r.SortOrder)
            .ToListAsync();
        
        return View("~/Views/Admin/Settings/GddRoles.cshtml", roles);
    }

    // GET: Admin/CreateGddRole
    public IActionResult CreateGddRole()
    {
        return View("~/Views/Admin/Settings/CreateGddRole.cshtml");
    }

    // POST: Admin/CreateGddRole
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGddRole([Bind("RoleFamily,RoleName,RoleLevel,Description,DisplayName,IsActive,SortOrder")] GddRole role)
    {
        if (ModelState.IsValid)
        {
            try
            {
                _context.GddRoles.Add(role);
                await _context.SaveChangesAsync();
                
                TempData["SuccessMessage"] = $"GDD Role '{role.DisplayName}' has been created successfully.";
                _logger.LogInformation("GDD Role created: {DisplayName}", role.DisplayName);
                return RedirectToAction(nameof(GddRoles));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating GDD role");
                TempData["ErrorMessage"] = "An error occurred while creating the GDD role. Please try again.";
            }
        }

        return View("~/Views/Admin/Settings/CreateGddRole.cshtml", role);
    }

    // GET: Admin/EditGddRole/5
    public async Task<IActionResult> EditGddRole(int id)
    {
        var role = await _context.GddRoles.FindAsync(id);
        if (role == null)
        {
            return NotFound();
        }

        return View("~/Views/Admin/Settings/EditGddRole.cshtml", role);
    }

    // POST: Admin/EditGddRole/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditGddRole(int id, [Bind("Id,RoleFamily,RoleName,RoleLevel,Description,DisplayName,IsActive,SortOrder")] GddRole role)
    {
        if (id != role.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                var existingRole = await _context.GddRoles.FindAsync(id);
                if (existingRole != null)
                {
                    existingRole.RoleFamily = role.RoleFamily;
                    existingRole.RoleName = role.RoleName;
                    existingRole.RoleLevel = role.RoleLevel;
                    existingRole.Description = role.Description;
                    existingRole.DisplayName = role.DisplayName;
                    existingRole.IsActive = role.IsActive;
                    existingRole.SortOrder = role.SortOrder;
                    existingRole.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"GDD Role '{role.DisplayName}' has been updated successfully.";
                    _logger.LogInformation("GDD Role {Id} updated successfully", id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating GDD role {RoleId}", id);
                TempData["ErrorMessage"] = "An error occurred while updating the GDD role. Please try again.";
            }

            return RedirectToAction(nameof(GddRoles));
        }

        return View("~/Views/Admin/Settings/EditGddRole.cshtml", role);
    }

    // POST: Admin/DeleteGddRole
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteGddRole(int id)
    {
        try
        {
            var role = await _context.GddRoles.FindAsync(id);
            if (role != null)
            {
                // Check if any staff role returns are using this role
                var usageCount = await _context.StaffRoleReturns.CountAsync(srr => srr.GddRoleId == id);
                if (usageCount > 0)
                {
                    TempData["ErrorMessage"] = $"Cannot delete GDD role '{role.DisplayName}' as it is being used by {usageCount} staff role return(s).";
                }
                else
                {
                    _context.GddRoles.Remove(role);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"GDD Role '{role.DisplayName}' has been deleted successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting GDD role");
            TempData["ErrorMessage"] = "An error occurred while deleting the GDD role. Please try again.";
        }

        return RedirectToAction(nameof(GddRoles));
    }

    // ========================================
    // SETTINGS - Skills
    // ========================================

    // GET: Admin/Skills
    public async Task<IActionResult> Skills()
    {
        var skills = await _context.Skills
            .OrderBy(s => s.SkillName)
            .ToListAsync();
        
        return View("~/Views/Admin/Settings/Skills.cshtml", skills);
    }

    // GET: Admin/CreateSkill
    public IActionResult CreateSkill()
    {
        return View("~/Views/Admin/Settings/CreateSkill.cshtml");
    }

    // POST: Admin/CreateSkill
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSkill([Bind("SkillName,Description,Category,IsActive,SortOrder")] Skill skill)
    {
        if (ModelState.IsValid)
        {
            try
            {
                _context.Skills.Add(skill);
                await _context.SaveChangesAsync();
                
                TempData["SuccessMessage"] = $"Skill '{skill.SkillName}' has been created successfully.";
                _logger.LogInformation("Skill created: {SkillName}", skill.SkillName);
                return RedirectToAction(nameof(Skills));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating skill");
                TempData["ErrorMessage"] = "An error occurred while creating the skill. Please try again.";
            }
        }

        return View("~/Views/Admin/Settings/CreateSkill.cshtml", skill);
    }

    // GET: Admin/EditSkill/5
    public async Task<IActionResult> EditSkill(int id)
    {
        var skill = await _context.Skills.FindAsync(id);
        if (skill == null)
        {
            return NotFound();
        }

        return View("~/Views/Admin/Settings/EditSkill.cshtml", skill);
    }

    // POST: Admin/EditSkill/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditSkill(int id, [Bind("Id,SkillName,Description,Category,IsActive,SortOrder")] Skill skill)
    {
        if (id != skill.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                var existingSkill = await _context.Skills.FindAsync(id);
                if (existingSkill != null)
                {
                    existingSkill.SkillName = skill.SkillName;
                    existingSkill.Description = skill.Description;
                    existingSkill.Category = skill.Category;
                    existingSkill.IsActive = skill.IsActive;
                    existingSkill.SortOrder = skill.SortOrder;
                    existingSkill.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Skill '{skill.SkillName}' has been updated successfully.";
                    _logger.LogInformation("Skill {Id} updated successfully", id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating skill {SkillId}", id);
                TempData["ErrorMessage"] = "An error occurred while updating the skill. Please try again.";
            }

            return RedirectToAction(nameof(Skills));
        }

        return View("~/Views/Admin/Settings/EditSkill.cshtml", skill);
    }

    // POST: Admin/DeleteSkill
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSkill(int id)
    {
        try
        {
            var skill = await _context.Skills.FindAsync(id);
            if (skill != null)
            {
                // Check if any staff role returns are using this skill
                var usageCount = await _context.StaffRoleReturnSkills.CountAsync(srs => srs.SkillId == id);
                if (usageCount > 0)
                {
                    TempData["ErrorMessage"] = $"Cannot delete skill '{skill.SkillName}' as it is being used by {usageCount} staff role return(s).";
                }
                else
                {
                    _context.Skills.Remove(skill);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Skill '{skill.SkillName}' has been deleted successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting skill");
            TempData["ErrorMessage"] = "An error occurred while deleting the skill. Please try again.";
        }

        return RedirectToAction(nameof(Skills));
    }

    // ========================================
    // DDAT FRAMEWORK - Skills
    // ========================================

    // GET: Admin/DdatFrameworkSkills
    public async Task<IActionResult> DdatFrameworkSkills(int? versionId)
    {
        var activeVersion = await _context.DdatFrameworkVersions
            .FirstOrDefaultAsync(v => v.IsActive);

        var versionIdToUse = versionId ?? activeVersion?.Id;
        
        var skills = new List<DdatFrameworkSkill>();
        if (versionIdToUse.HasValue)
        {
            skills = await _context.DdatFrameworkSkills
                .Include(s => s.FrameworkVersion)
                .Include(s => s.GradeMappings)
                .Where(s => s.FrameworkVersionId == versionIdToUse.Value)
                .OrderBy(s => s.SkillName)
                .ToListAsync();
        }

        ViewBag.ActiveVersion = activeVersion;
        ViewBag.Versions = await _context.DdatFrameworkVersions
            .OrderByDescending(v => v.ImportedAt)
            .ToListAsync();
        ViewBag.SelectedVersionId = versionIdToUse;

        return View("~/Views/Admin/DdatFramework/Skills.cshtml", skills);
    }

    // GET: Admin/DdatFrameworkSkills/Details/5
    public async Task<IActionResult> DdatFrameworkSkillDetails(int id)
    {
        var skill = await _context.DdatFrameworkSkills
            .Include(s => s.FrameworkVersion)
            .Include(s => s.GradeMappings)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (skill == null)
        {
            return NotFound();
        }

        return View("~/Views/Admin/DdatFramework/SkillDetails.cshtml", skill);
    }

    // GET: Admin/DdatFrameworkSkills/Edit/5
    public async Task<IActionResult> EditDdatFrameworkSkill(int id)
    {
        var skill = await _context.DdatFrameworkSkills
            .Include(s => s.GradeMappings)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (skill == null)
        {
            return NotFound();
        }

        ViewBag.CapabilityLevels = new[] { "Awareness", "Working", "Practitioner", "Expert" };
        ViewBag.Grades = await _context.Grades
            .Where(g => g.IsActive)
            .OrderBy(g => g.DisplayOrder)
            .ThenBy(g => g.Code)
            .ToListAsync();

        return View("~/Views/Admin/DdatFramework/EditSkill.cshtml", skill);
    }

    // POST: Admin/DdatFrameworkSkills/Edit/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditDdatFrameworkSkill(int id, DdatFrameworkSkill skill, IFormCollection form)
    {
        if (id != skill.Id)
        {
            return NotFound();
        }

        try
        {
            var existingSkill = await _context.DdatFrameworkSkills
                .Include(s => s.GradeMappings)
                .FirstOrDefaultAsync(s => s.Id == id);

            if (existingSkill == null)
            {
                return NotFound();
            }

            // Update skill properties
            existingSkill.SkillName = skill.SkillName;
            existingSkill.SkillDescription = skill.SkillDescription;
            existingSkill.AwarenessDescription = skill.AwarenessDescription;
            existingSkill.WorkingDescription = skill.WorkingDescription;
            existingSkill.PractitionerDescription = skill.PractitionerDescription;
            existingSkill.ExpertDescription = skill.ExpertDescription;
            existingSkill.RolesThatRequireSkill = skill.RolesThatRequireSkill;
            existingSkill.UpdatedAt = DateTime.UtcNow;

            // Update grade mappings
            var gradeMappingKeys = form.Keys.Where(k => k.StartsWith("gradeMappings[") && k.Contains("].capabilityLevel")).ToList();
            if (gradeMappingKeys.Any())
            {
                // Remove existing mappings
                _context.DdatFrameworkSkillGradeMappings.RemoveRange(existingSkill.GradeMappings);

                // Add new mappings
                foreach (var key in gradeMappingKeys)
                {
                    var indexMatch = System.Text.RegularExpressions.Regex.Match(key, @"\[(\d+)\]");
                    if (indexMatch.Success)
                    {
                        var index = indexMatch.Groups[1].Value;
                        var capabilityLevel = form[$"gradeMappings[{index}].capabilityLevel"].ToString();
                        var grade = form[$"gradeMappings[{index}].grade"].ToString();

                        if (!string.IsNullOrWhiteSpace(capabilityLevel) && !string.IsNullOrWhiteSpace(grade))
                        {
                            existingSkill.GradeMappings.Add(new DdatFrameworkSkillGradeMapping
                            {
                                DdatFrameworkSkillId = existingSkill.Id,
                                CapabilityLevel = capabilityLevel,
                                Grade = grade,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            });
                        }
                    }
                }
            }
            else
            {
                // Remove all mappings if none provided
                _context.DdatFrameworkSkillGradeMappings.RemoveRange(existingSkill.GradeMappings);
            }

            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"DDAT Framework Skill '{skill.SkillName}' has been updated successfully.";
            return RedirectToAction(nameof(DdatFrameworkSkills), new { versionId = existingSkill.FrameworkVersionId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating DDAT Framework skill {SkillId}", id);
            TempData["ErrorMessage"] = "An error occurred while updating the skill. Please try again.";
        }

        ViewBag.CapabilityLevels = new[] { "Awareness", "Working", "Practitioner", "Expert" };
        ViewBag.Grades = await _context.Grades
            .Where(g => g.IsActive)
            .OrderBy(g => g.DisplayOrder)
            .ThenBy(g => g.Code)
            .ToListAsync();
        return View("~/Views/Admin/DdatFramework/EditSkill.cshtml", skill);
    }

    // ========================================
    // DDAT FRAMEWORK - Roles
    // ========================================

    // GET: Admin/DdatFrameworkRoles
    public async Task<IActionResult> DdatFrameworkRoles(int? versionId)
    {
        var activeVersion = await _context.DdatFrameworkVersions
            .FirstOrDefaultAsync(v => v.IsActive);

        var versionIdToUse = versionId ?? activeVersion?.Id;

        var roles = new List<DdatFrameworkRole>();
        if (versionIdToUse.HasValue)
        {
            roles = await _context.DdatFrameworkRoles
                .Include(r => r.FrameworkVersion)
                .Include(r => r.RoleSkills)
                .Where(r => r.FrameworkVersionId == versionIdToUse.Value)
                .OrderBy(r => r.RoleFamily)
                .ThenBy(r => r.Role)
                .ThenBy(r => r.RoleLevel)
                .ToListAsync();
        }

        ViewBag.ActiveVersion = activeVersion;
        ViewBag.Versions = await _context.DdatFrameworkVersions
            .OrderByDescending(v => v.ImportedAt)
            .ToListAsync();
        ViewBag.SelectedVersionId = versionIdToUse;

        return View("~/Views/Admin/DdatFramework/Roles.cshtml", roles);
    }

    // GET: Admin/DdatFrameworkRoles/Details/5
    public async Task<IActionResult> DdatFrameworkRoleDetails(int id)
    {
        var role = await _context.DdatFrameworkRoles
            .Include(r => r.FrameworkVersion)
            .Include(r => r.RoleSkills)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (role == null)
        {
            return NotFound();
        }

        return View("~/Views/Admin/DdatFramework/RoleDetails.cshtml", role);
    }

    // ========================================
    // DDAT FRAMEWORK - Import/Sync
    // ========================================

    // GET: Admin/DdatFrameworkImport
    public async Task<IActionResult> DdatFrameworkImport()
    {
        var versions = await _context.DdatFrameworkVersions
            .OrderByDescending(v => v.ImportedAt)
            .ToListAsync();

        ViewBag.Versions = versions;
        ViewBag.ActiveVersion = versions.FirstOrDefault(v => v.IsActive);

        return View("~/Views/Admin/DdatFramework/Import.cshtml");
    }

    // POST: Admin/DdatFrameworkImportFromUrl
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DdatFrameworkImportFromUrl(string skillsCsvUrl, string rolesCsvUrl, string versionIdentifier, string? versionName, string? notes)
    {
        if (string.IsNullOrWhiteSpace(skillsCsvUrl) || string.IsNullOrWhiteSpace(rolesCsvUrl))
        {
            TempData["ErrorMessage"] = "Please provide URLs for both Skills and Roles CSV files.";
            return RedirectToAction(nameof(DdatFrameworkImport));
        }

        if (string.IsNullOrWhiteSpace(versionIdentifier))
        {
            TempData["ErrorMessage"] = "Please provide a version identifier (e.g., '2025-12-05').";
            return RedirectToAction(nameof(DdatFrameworkImport));
        }

        try
        {
            // Check if version already exists
            var existingVersion = await _context.DdatFrameworkVersions
                .FirstOrDefaultAsync(v => v.VersionIdentifier == versionIdentifier);

            if (existingVersion != null)
            {
                TempData["ErrorMessage"] = $"Version '{versionIdentifier}' already exists. Please use a different version identifier.";
                return RedirectToAction(nameof(DdatFrameworkImport));
            }

            // Download CSV files
            using var httpClient = new HttpClient();
            var skillsCsvContent = await httpClient.GetStringAsync(skillsCsvUrl);
            var rolesCsvContent = await httpClient.GetStringAsync(rolesCsvUrl);

            // Save CSV files locally
            var csvDirectory = Path.Combine(Directory.GetCurrentDirectory(), "requirements", "ddat-framework");
            Directory.CreateDirectory(csvDirectory);

            var skillsCsvPath = Path.Combine(csvDirectory, $"Skills-{versionIdentifier}.csv");
            var rolesCsvPath = Path.Combine(csvDirectory, $"Roles-{versionIdentifier}.csv");

            await System.IO.File.WriteAllTextAsync(skillsCsvPath, skillsCsvContent);
            await System.IO.File.WriteAllTextAsync(rolesCsvPath, rolesCsvContent);

            // Create framework version
            var frameworkVersion = new DdatFrameworkVersion
            {
                VersionIdentifier = versionIdentifier,
                VersionName = versionName ?? versionIdentifier,
                SkillsCsvUrl = skillsCsvUrl,
                RolesCsvUrl = rolesCsvUrl,
                SkillsCsvPath = skillsCsvPath,
                RolesCsvPath = rolesCsvPath,
                Notes = notes,
                ImportedBy = User.Identity?.Name ?? "System",
                ImportedAt = DateTime.UtcNow,
                IsActive = false // Will be set to active after successful import
            };

            _context.DdatFrameworkVersions.Add(frameworkVersion);
            await _context.SaveChangesAsync();

            // Import skills
            var skillsCount = await ImportDdatFrameworkSkillsAsync(skillsCsvPath, frameworkVersion.Id);
            frameworkVersion.SkillsCount = skillsCount;

            // Import roles
            var rolesCount = await ImportDdatFrameworkRolesAsync(rolesCsvPath, frameworkVersion.Id);
            frameworkVersion.RolesCount = rolesCount;

            // Deactivate previous versions
            await _context.DdatFrameworkVersions
                .Where(v => v.Id != frameworkVersion.Id && v.IsActive)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.IsActive, false));

            // Activate this version
            frameworkVersion.IsActive = true;
            frameworkVersion.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"DDAT Framework version '{versionIdentifier}' imported successfully. {skillsCount} skills and {rolesCount} roles imported.";
            return RedirectToAction(nameof(DdatFrameworkImport));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing DDAT Framework from URLs");
            TempData["ErrorMessage"] = $"Error importing framework: {ex.Message}";
            return RedirectToAction(nameof(DdatFrameworkImport));
        }
    }

    // POST: Admin/DdatFrameworkImportFromFile
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DdatFrameworkImportFromFile(IFormFile skillsCsvFile, IFormFile rolesCsvFile, string versionIdentifier, string? versionName, string? notes)
    {
        if (skillsCsvFile == null || rolesCsvFile == null)
        {
            TempData["ErrorMessage"] = "Please upload both Skills and Roles CSV files.";
            return RedirectToAction(nameof(DdatFrameworkImport));
        }

        if (string.IsNullOrWhiteSpace(versionIdentifier))
        {
            TempData["ErrorMessage"] = "Please provide a version identifier (e.g., '2025-12-05').";
            return RedirectToAction(nameof(DdatFrameworkImport));
        }

        try
        {
            // Check if version already exists
            var existingVersion = await _context.DdatFrameworkVersions
                .FirstOrDefaultAsync(v => v.VersionIdentifier == versionIdentifier);

            if (existingVersion != null)
            {
                TempData["ErrorMessage"] = $"Version '{versionIdentifier}' already exists. Please use a different version identifier.";
                return RedirectToAction(nameof(DdatFrameworkImport));
            }

            // Save uploaded files
            var csvDirectory = Path.Combine(Directory.GetCurrentDirectory(), "requirements", "ddat-framework");
            Directory.CreateDirectory(csvDirectory);

            var skillsCsvPath = Path.Combine(csvDirectory, $"Skills-{versionIdentifier}.csv");
            var rolesCsvPath = Path.Combine(csvDirectory, $"Roles-{versionIdentifier}.csv");

            using (var stream = new FileStream(skillsCsvPath, FileMode.Create))
            {
                await skillsCsvFile.CopyToAsync(stream);
            }

            using (var stream = new FileStream(rolesCsvPath, FileMode.Create))
            {
                await rolesCsvFile.CopyToAsync(stream);
            }

            // Create framework version
            var frameworkVersion = new DdatFrameworkVersion
            {
                VersionIdentifier = versionIdentifier,
                VersionName = versionName ?? versionIdentifier,
                SkillsCsvPath = skillsCsvPath,
                RolesCsvPath = rolesCsvPath,
                Notes = notes,
                ImportedBy = User.Identity?.Name ?? "System",
                ImportedAt = DateTime.UtcNow,
                IsActive = false
            };

            _context.DdatFrameworkVersions.Add(frameworkVersion);
            await _context.SaveChangesAsync();

            // Import skills
            var skillsCount = await ImportDdatFrameworkSkillsAsync(skillsCsvPath, frameworkVersion.Id);
            frameworkVersion.SkillsCount = skillsCount;

            // Import roles
            var rolesCount = await ImportDdatFrameworkRolesAsync(rolesCsvPath, frameworkVersion.Id);
            frameworkVersion.RolesCount = rolesCount;

            // Deactivate previous versions
            await _context.DdatFrameworkVersions
                .Where(v => v.Id != frameworkVersion.Id && v.IsActive)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.IsActive, false));

            // Activate this version
            frameworkVersion.IsActive = true;
            frameworkVersion.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"DDAT Framework version '{versionIdentifier}' imported successfully. {skillsCount} skills and {rolesCount} roles imported.";
            return RedirectToAction(nameof(DdatFrameworkImport));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing DDAT Framework from files");
            TempData["ErrorMessage"] = $"Error importing framework: {ex.Message}";
            return RedirectToAction(nameof(DdatFrameworkImport));
        }
    }

    // Helper method to import skills from CSV
    private async Task<int> ImportDdatFrameworkSkillsAsync(string csvPath, int frameworkVersionId)
    {
        var skillsCount = 0;
        var existingSkills = await _context.DdatFrameworkSkills
            .Where(s => s.FrameworkVersionId == frameworkVersionId)
            .Select(s => s.SkillName)
            .ToListAsync();

        using var reader = new StreamReader(csvPath);
        using var csv = new CsvHelper.CsvReader(reader, new CsvHelper.Configuration.CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = CsvHelper.Configuration.TrimOptions.Trim
        });

        await foreach (var record in csv.GetRecordsAsync<dynamic>())
        {
            var skillName = ((IDictionary<string, object>)record)["Skill Name"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(skillName))
                continue;

            // Skip if already exists
            if (existingSkills.Contains(skillName))
                continue;

            var skill = new DdatFrameworkSkill
            {
                SkillName = skillName,
                SkillDescription = ((IDictionary<string, object>)record)["Skill Description"]?.ToString()?.Trim(),
                AwarenessDescription = ((IDictionary<string, object>)record)["Awareness"]?.ToString()?.Trim(),
                WorkingDescription = ((IDictionary<string, object>)record)["Working"]?.ToString()?.Trim(),
                PractitionerDescription = ((IDictionary<string, object>)record)["Practitioner"]?.ToString()?.Trim(),
                ExpertDescription = ((IDictionary<string, object>)record)["Expert"]?.ToString()?.Trim(),
                RolesThatRequireSkill = ((IDictionary<string, object>)record)["Roles that require Skill"]?.ToString()?.Trim(),
                FrameworkVersionId = frameworkVersionId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.DdatFrameworkSkills.Add(skill);
            skillsCount++;
        }

        await _context.SaveChangesAsync();
        return skillsCount;
    }

    // Helper method to import roles from CSV
    private async Task<int> ImportDdatFrameworkRolesAsync(string csvPath, int frameworkVersionId)
    {
        var rolesCount = 0;
        var existingRoles = new HashSet<string>();

        using var reader = new StreamReader(csvPath);
        using var csv = new CsvHelper.CsvReader(reader, new CsvHelper.Configuration.CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = CsvHelper.Configuration.TrimOptions.Trim
        });

        DdatFrameworkRole? currentRole = null;
        var roleKey = "";

        await foreach (var record in csv.GetRecordsAsync<dynamic>())
        {
            var roleFamily = ((IDictionary<string, object>)record)["Role Family"]?.ToString()?.Trim() ?? "";
            var role = ((IDictionary<string, object>)record)["Role"]?.ToString()?.Trim() ?? "";
            var roleLevel = ((IDictionary<string, object>)record)["Role Level"]?.ToString()?.Trim() ?? "";
            var newRoleKey = $"{roleFamily}|{role}|{roleLevel}";

            // Create new role if this is a different role/level combination
            if (newRoleKey != roleKey || currentRole == null)
            {
                if (currentRole != null)
                {
                    _context.DdatFrameworkRoles.Add(currentRole);
                    rolesCount++;
                }

                if (!existingRoles.Contains(newRoleKey))
                {
                    currentRole = new DdatFrameworkRole
                    {
                        RoleFamily = roleFamily,
                        Role = role,
                        RoleDescription = ((IDictionary<string, object>)record)["Role Description"]?.ToString()?.Trim(),
                        RoleLevel = roleLevel,
                        RoleLevelDescription = ((IDictionary<string, object>)record)["Role Level Description"]?.ToString()?.Trim(),
                        RoleType = ((IDictionary<string, object>)record)["Role Type"]?.ToString()?.Trim(),
                        FrameworkVersionId = frameworkVersionId,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    roleKey = newRoleKey;
                    existingRoles.Add(newRoleKey);
                }
                else
                {
                    currentRole = null;
                    roleKey = "";
                }
            }

            // Add skill requirement to current role
            if (currentRole != null)
            {
                var skillName = ((IDictionary<string, object>)record)["Skill Name"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(skillName))
                {
                    currentRole.RoleSkills.Add(new DdatFrameworkRoleSkill
                    {
                        SkillName = skillName,
                        SkillDescription = ((IDictionary<string, object>)record)["Skill Description"]?.ToString()?.Trim(),
                        SkillLevel = ((IDictionary<string, object>)record)["Skill Level"]?.ToString()?.Trim() ?? "",
                        SkillLevelDescription = ((IDictionary<string, object>)record)["Skill Level Description"]?.ToString()?.Trim(),
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
            }
        }

        // Add the last role
        if (currentRole != null)
        {
            _context.DdatFrameworkRoles.Add(currentRole);
            rolesCount++;
        }

        await _context.SaveChangesAsync();
        return rolesCount;
    }

    // POST: Admin/DdatFrameworkSync
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DdatFrameworkSync(int versionId)
    {
        try
        {
            var version = await _context.DdatFrameworkVersions.FindAsync(versionId);
            if (version == null)
            {
                TempData["ErrorMessage"] = "Framework version not found.";
                return RedirectToAction(nameof(DdatFrameworkImport));
            }

            if (string.IsNullOrWhiteSpace(version.SkillsCsvUrl) || string.IsNullOrWhiteSpace(version.RolesCsvUrl))
            {
                TempData["ErrorMessage"] = "This version does not have CSV URLs configured for syncing.";
                return RedirectToAction(nameof(DdatFrameworkImport));
            }

            // Archive existing skills and roles
            await _context.DdatFrameworkSkills
                .Where(s => s.FrameworkVersionId == versionId && !s.IsArchived)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(sk => sk.IsArchived, true)
                    .SetProperty(sk => sk.ArchivedAt, DateTime.UtcNow));

            await _context.DdatFrameworkRoles
                .Where(r => r.FrameworkVersionId == versionId && !r.IsArchived)
                .ExecuteUpdateAsync(r => r
                    .SetProperty(ro => ro.IsArchived, true)
                    .SetProperty(ro => ro.ArchivedAt, DateTime.UtcNow));

            // Download and import new data
            using var httpClient = new HttpClient();
            var skillsCsvContent = await httpClient.GetStringAsync(version.SkillsCsvUrl);
            var rolesCsvContent = await httpClient.GetStringAsync(version.RolesCsvUrl);

            // Update local files
            if (!string.IsNullOrWhiteSpace(version.SkillsCsvPath))
            {
                await System.IO.File.WriteAllTextAsync(version.SkillsCsvPath, skillsCsvContent);
            }

            if (!string.IsNullOrWhiteSpace(version.RolesCsvPath))
            {
                await System.IO.File.WriteAllTextAsync(version.RolesCsvPath, rolesCsvContent);
            }

            // Import new skills and roles
            var skillsCount = await ImportDdatFrameworkSkillsAsync(version.SkillsCsvPath ?? "", versionId);
            var rolesCount = await ImportDdatFrameworkRolesAsync(version.RolesCsvPath ?? "", versionId);

            version.SkillsCount = skillsCount;
            version.RolesCount = rolesCount;
            version.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Framework version '{version.VersionIdentifier}' synced successfully. {skillsCount} skills and {rolesCount} roles imported.";
            return RedirectToAction(nameof(DdatFrameworkImport));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing DDAT Framework version {VersionId}", versionId);
            TempData["ErrorMessage"] = $"Error syncing framework: {ex.Message}";
            return RedirectToAction(nameof(DdatFrameworkImport));
        }
    }

    // ========================================
    // SETTINGS - Grades
    // ========================================

    // GET: Admin/Grades
    public async Task<IActionResult> Grades()
    {
        var grades = await _context.Grades
            .OrderBy(g => g.DisplayOrder)
            .ThenBy(g => g.Code)
            .ToListAsync();
        
        return View("~/Views/Admin/Settings/Grades.cshtml", grades);
    }

    // GET: Admin/CreateGrade
    public IActionResult CreateGrade()
    {
        return View("~/Views/Admin/Settings/CreateGrade.cshtml");
    }

    // POST: Admin/CreateGrade
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGrade([Bind("Code,DisplayName,DisplayOrder,IsActive")] Grade grade)
    {
        if (ModelState.IsValid)
        {
            try
            {
                grade.CreatedAt = DateTime.UtcNow;
                grade.UpdatedAt = DateTime.UtcNow;
                _context.Grades.Add(grade);
                await _context.SaveChangesAsync();
                
                TempData["SuccessMessage"] = $"Grade '{grade.Code}' has been created successfully.";
                _logger.LogInformation("Grade created: {Code}", grade.Code);
                return RedirectToAction(nameof(Grades));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating grade");
                TempData["ErrorMessage"] = "An error occurred while creating the grade. Please try again.";
            }
        }

        return View("~/Views/Admin/Settings/CreateGrade.cshtml", grade);
    }

    // GET: Admin/EditGrade/5
    public async Task<IActionResult> EditGrade(int id)
    {
        var grade = await _context.Grades.FindAsync(id);
        if (grade == null)
        {
            return NotFound();
        }

        return View("~/Views/Admin/Settings/EditGrade.cshtml", grade);
    }

    // POST: Admin/EditGrade/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditGrade(int id, [Bind("Id,Code,DisplayName,DisplayOrder,IsActive")] Grade grade)
    {
        if (id != grade.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                var existingGrade = await _context.Grades.FindAsync(id);
                if (existingGrade != null)
                {
                    existingGrade.Code = grade.Code;
                    existingGrade.DisplayName = grade.DisplayName;
                    existingGrade.DisplayOrder = grade.DisplayOrder;
                    existingGrade.IsActive = grade.IsActive;
                    existingGrade.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Grade '{grade.Code}' has been updated successfully.";
                    _logger.LogInformation("Grade {Id} updated successfully", id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating grade {GradeId}", id);
                TempData["ErrorMessage"] = "An error occurred while updating the grade. Please try again.";
            }

            return RedirectToAction(nameof(Grades));
        }

        return View("~/Views/Admin/Settings/EditGrade.cshtml", grade);
    }

    // POST: Admin/DeleteGrade
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteGrade(int id)
    {
        try
        {
            var grade = await _context.Grades.FindAsync(id);
            if (grade != null)
            {
                // Check if grade is being used
                var usageCount = await _context.DdatFrameworkSkillGradeMappings.CountAsync(gm => gm.Grade == grade.Code);
                var userProfileUsageCount = await _context.UserProfessionalProfiles.CountAsync(upp => upp.SubstantiveGrade == grade.Code);
                
                if (usageCount > 0 || userProfileUsageCount > 0)
                {
                    TempData["ErrorMessage"] = $"Cannot delete grade '{grade.Code}' as it is being used by {usageCount + userProfileUsageCount} record(s).";
                }
                else
                {
                    _context.Grades.Remove(grade);
                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Grade '{grade.Code}' has been deleted successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting grade");
            TempData["ErrorMessage"] = "An error occurred while deleting the grade. Please try again.";
        }

        return RedirectToAction(nameof(Grades));
    }

    // ========================================
    // DATA MANAGEMENT
    // ========================================

    // GET: Admin/ClearPerformanceReturns
    [HttpGet]
    public IActionResult ClearPerformanceReturns()
    {
        return View("~/Views/Admin/ClearPerformanceReturns.cshtml");
    }

    // POST: Admin/ClearPerformanceReturns
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearPerformanceReturnsConfirmed()
    {
        try
        {
            _logger.LogWarning("Starting to clear all performance returns - initiated by {User}", User.Identity?.Name);

            // Count before deletion
            var returnsCount = await _context.ProductReturns.CountAsync();
            var valuesCount = await _context.ProductMetricValues.CountAsync();

            _logger.LogInformation("Deleting {ReturnsCount} ProductReturns and {ValuesCount} ProductMetricValues", returnsCount, valuesCount);

            // Delete all product metric values first (they reference ProductReturns)
            _context.ProductMetricValues.RemoveRange(_context.ProductMetricValues);
            await _context.SaveChangesAsync();

            // Delete all product returns
            _context.ProductReturns.RemoveRange(_context.ProductReturns);
            await _context.SaveChangesAsync();

            _logger.LogWarning("Successfully cleared all performance returns - {ReturnsCount} returns and {ValuesCount} values deleted", returnsCount, valuesCount);

            TempData["SuccessMessage"] = $"Successfully cleared {returnsCount} performance returns and {valuesCount} metric values. System will now start from October 2025.";
            return RedirectToAction("Index", "PerformanceMetric");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing performance returns");
            TempData["ErrorMessage"] = "An error occurred while clearing performance returns. Please try again.";
            return RedirectToAction(nameof(ClearPerformanceReturns));
        }
    }

    private RaidLookupDefinition? ResolveRaidLookupDefinition(string? key) =>
        _raidLookupDefinitions.FirstOrDefault(d =>
            string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    private async Task<RaidLookupListViewModel> BuildRaidSettingsViewModelAsync(
        RaidLookupDefinition descriptor,
        RaidLookupEditInputModel? newEntry = null,
        RaidLookupEditInputModel? editEntry = null)
    {
        var items = await descriptor.Query(_context)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Label)
            .Select(x => new RaidLookupListItemViewModel
            {
                Id = x.Id,
                Code = x.Code,
                Label = x.Label,
                Description = x.Description,
                SortOrder = x.SortOrder,
                IsActive = x.IsActive
            })
            .ToListAsync();

        var defaultSort = items.Any() ? items.Max(i => i.SortOrder) + 10 : 0;

        return new RaidLookupListViewModel
        {
            CurrentLookupKey = descriptor.Key,
            CurrentLookupLabel = descriptor.Label,
            CurrentLookupDescription = descriptor.Description,
            Lookups = _raidLookupDefinitions
                .Select(d => new RaidLookupSelectorViewModel
                {
                    Key = d.Key,
                    Label = d.Label
                })
                .ToList(),
            Items = items,
            NewEntry = newEntry ?? new RaidLookupEditInputModel
            {
                LookupKey = descriptor.Key,
                SortOrder = defaultSort,
                IsActive = true
            },
            EditEntry = editEntry,
            CanSeedDefaults = RaidLookupSeedData.Definitions.ContainsKey(descriptor.Key)
        };
    }

    private record RaidLookupDefinition(
        string Key,
        string Label,
        Func<CompassDbContext, IQueryable<RaidLookupBase>> Query,
        Func<RaidLookupBase> Factory,
        string? Description);

    // ========================================
    // PROFESSIONS MANAGEMENT
    // ========================================

    // GET: Admin/Professions
    public async Task<IActionResult> Professions()
    {
        var professions = await _context.DdatProfessions
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();
        
        return View("~/Views/Admin/Professions/Index.cshtml", professions);
    }

    // GET: Admin/Professions/Details/5
    public async Task<IActionResult> ProfessionDetails(int id)
    {
        var profession = await _context.DdatProfessions
            .Include(p => p.ProfessionSkills)
                .ThenInclude(ps => ps.Skill)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (profession == null)
        {
            return NotFound();
        }

        // Get Head of Profession for this profession
        var hop = await _context.HOPS
            .Include(h => h.User)
            .FirstOrDefaultAsync(h => h.DdatProfessionId == id);

        ViewBag.HeadOfProfession = hop;
        ViewBag.AllSkills = await _context.Skills
            .Where(s => s.IsActive)
            .OrderBy(s => s.SkillName)
            .ToListAsync();

        return View("~/Views/Admin/Professions/Details.cshtml", profession);
    }

    // GET: Admin/Professions/Edit/5
    public async Task<IActionResult> EditProfession(int id)
    {
        var profession = await _context.DdatProfessions.FindAsync(id);
        if (profession == null)
        {
            return NotFound();
        }

        return View("~/Views/Admin/Professions/Edit.cshtml", profession);
    }

    // POST: Admin/Professions/Edit/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditProfession(int id, [Bind("Id,Name,Slug,Description,RoleGroup,DisplayOrder,IsActive")] DdatProfession profession)
    {
        if (id != profession.Id)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            try
            {
                var existingProfession = await _context.DdatProfessions.FindAsync(id);
                if (existingProfession != null)
                {
                    existingProfession.Name = profession.Name;
                    existingProfession.Slug = profession.Slug;
                    existingProfession.Description = profession.Description;
                    existingProfession.RoleGroup = profession.RoleGroup;
                    existingProfession.DisplayOrder = profession.DisplayOrder;
                    existingProfession.IsActive = profession.IsActive;
                    existingProfession.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                    
                    TempData["SuccessMessage"] = $"Profession '{profession.Name}' has been updated successfully.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating profession {ProfessionId}", id);
                TempData["ErrorMessage"] = "An error occurred while updating the profession. Please try again.";
            }

            return RedirectToAction(nameof(Professions));
        }

        return View("~/Views/Admin/Professions/Edit.cshtml", profession);
    }

    // POST: Admin/Professions/AssignSkills
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignSkillsToProfession(int professionId, int[] skillIds)
    {
        try
        {
            var profession = await _context.DdatProfessions.FindAsync(professionId);
            if (profession == null)
            {
                TempData["ErrorMessage"] = "Profession not found.";
                return RedirectToAction(nameof(Professions));
            }

            // Remove existing skill assignments
            var existingAssignments = await _context.ProfessionSkills
                .Where(ps => ps.DdatProfessionId == professionId)
                .ToListAsync();
            _context.ProfessionSkills.RemoveRange(existingAssignments);

            // Add new skill assignments
            foreach (var skillId in skillIds)
            {
                var skill = await _context.Skills.FindAsync(skillId);
                if (skill != null)
                {
                    _context.ProfessionSkills.Add(new ProfessionSkill
                    {
                        DdatProfessionId = professionId,
                        SkillId = skillId,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            await _context.SaveChangesAsync();
            
            TempData["SuccessMessage"] = $"Skills have been assigned to profession '{profession.Name}' successfully.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error assigning skills to profession {ProfessionId}", professionId);
            TempData["ErrorMessage"] = "An error occurred while assigning skills. Please try again.";
        }

        return RedirectToAction(nameof(ProfessionDetails), new { id = professionId });
    }

    // ========================================
    // HEADS OF PROFESSION MANAGEMENT
    // ========================================

    // GET: Admin/HeadsOfProfession
    public async Task<IActionResult> HeadsOfProfession()
    {
        var hops = await _context.HOPS
            .Include(h => h.User)
            .Include(h => h.DdatProfession)
            .OrderBy(h => h.DdatProfession != null ? h.DdatProfession.Name : string.Empty)
            .ThenBy(h => h.User != null ? h.User.Name : string.Empty)
            .ToListAsync();

        ViewBag.Professions = await _context.DdatProfessions
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();

        return View("~/Views/Admin/HeadsOfProfession/Index.cshtml", hops);
    }

    // POST: Admin/HeadsOfProfession/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateHeadOfProfession(string userEmail, int professionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userEmail))
            {
                TempData["ErrorMessage"] = "Please select a user from the search results.";
                return RedirectToAction(nameof(HeadsOfProfession));
            }

            // Get or create user by email
            var normalizedEmail = userEmail.ToLowerInvariant().Trim();
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);

            if (user == null)
            {
                // Create user if they don't exist
                user = new User
                {
                    Email = normalizedEmail,
                    Name = normalizedEmail.Split('@')[0].Replace(".", " "),
                    Role = UserRole.Visitor,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.Users.Add(user);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Created new user for Head of Profession: {Email}", normalizedEmail);
            }

            // Check if this assignment already exists
            var existing = await _context.HOPS
                .FirstOrDefaultAsync(h => h.UserId == user.Id && h.DdatProfessionId == professionId);

            if (existing != null)
            {
                TempData["ErrorMessage"] = "This user is already assigned as Head of Profession for this profession.";
                return RedirectToAction(nameof(HeadsOfProfession));
            }

            var hop = new HOPS
            {
                UserId = user.Id,
                DdatProfessionId = professionId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.HOPS.Add(hop);
            await _context.SaveChangesAsync();

            var profession = await _context.DdatProfessions.FindAsync(professionId);
            
            TempData["SuccessMessage"] = $"{user.Name} ({user.Email}) has been assigned as Head of Profession for '{profession?.Name}'.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating Head of Profession");
            TempData["ErrorMessage"] = "An error occurred while creating the Head of Profession assignment. Please try again.";
        }

        return RedirectToAction(nameof(HeadsOfProfession));
    }

    // POST: Admin/HeadsOfProfession/Delete/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteHeadOfProfession(int id)
    {
        try
        {
            var hop = await _context.HOPS
                .Include(h => h.User)
                .Include(h => h.DdatProfession)
                .FirstOrDefaultAsync(h => h.Id == id);

            if (hop != null)
            {
                var userName = hop.User?.Name;
                var professionName = hop.DdatProfession?.Name;

                _context.HOPS.Remove(hop);
                await _context.SaveChangesAsync();

                TempData["SuccessMessage"] = $"{userName} has been removed as Head of Profession for '{professionName}'.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting Head of Profession {HopId}", id);
            TempData["ErrorMessage"] = "An error occurred while deleting the Head of Profession assignment. Please try again.";
        }

        return RedirectToAction(nameof(HeadsOfProfession));
    }

}
