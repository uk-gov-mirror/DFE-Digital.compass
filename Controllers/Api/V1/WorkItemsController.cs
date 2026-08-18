using System.ComponentModel.DataAnnotations;
using Compass.Attributes;
using Compass.Data;
using Compass.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compass.Controllers.Api.V1;

[ApiController]
[Route("api/v1/[controller]")]
public class WorkItemsController : ControllerBase
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Active", "Paused", "Completed", "Cancelled"
    };

    private readonly CompassDbContext _context;
    private readonly ILogger<WorkItemsController> _logger;

    public WorkItemsController(CompassDbContext context, ILogger<WorkItemsController> logger)
    {
        _context = context;
        _logger = logger;
    }

    [HttpGet]
    [RequireApiPermission("WorkItems", "read")]
    public async Task<IActionResult> GetWorkItems(
        [FromQuery] string? status = null,
        [FromQuery] int? businessAreaId = null,
        [FromQuery] int? phaseId = null,
        [FromQuery] int? ragStatusId = null,
        [FromQuery] int? priorityId = null,
        [FromQuery] int? portfolioId = null,
        [FromQuery] bool? flagship = null,
        [FromQuery] string? showInFips = null,
        [FromQuery] string? q = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (pageSize > 100) pageSize = 100;
        if (page < 1) page = 1;

        var query = _context.Projects.AsNoTracking().Where(p => !p.IsDeleted);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(p => p.Status == status);

        if (businessAreaId.HasValue)
            query = query.Where(p => p.BusinessAreaId == businessAreaId.Value);

        if (phaseId.HasValue)
            query = query.Where(p => p.PhaseId == phaseId.Value);

        if (ragStatusId.HasValue)
            query = query.Where(p => p.RagStatusLookupId == ragStatusId.Value);

        if (priorityId.HasValue)
            query = query.Where(p => p.DeliveryPriorityId == priorityId.Value);

        if (portfolioId.HasValue)
            query = query.Where(p => p.PrimaryOrganizationalGroupId == portfolioId.Value);

        if (flagship.HasValue)
            query = query.Where(p => p.IsFlagship == flagship.Value);

        var showInFipsFilter = ParseYesNoFlag(showInFips);
        if (showInFipsFilter.HasValue)
            query = query.Where(p => p.ShowInFips == showInFipsFilter.Value);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(p =>
                p.Title.Contains(term) ||
                (p.ProjectCode != null && p.ProjectCode.Contains(term)));
        }

        var totalRecords = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalRecords / (double)pageSize);

        var items = await query
            .OrderBy(p => p.Title)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                p.Id,
                p.ProjectCode,
                p.Title,
                p.Aim,
                p.Status,
                p.StartDate,
                targetEndDate = p.TargetDeliveryDate,
                p.ActualDeliveryDate,
                p.IsFlagship,
                p.ShowInFips,
                ragStatus = p.RagStatusLookup == null
                    ? null
                    : new { p.RagStatusLookup.Id, p.RagStatusLookup.Name },
                phase = p.PhaseLookup == null
                    ? null
                    : new { p.PhaseLookup.Id, p.PhaseLookup.Name },
                businessArea = p.BusinessAreaLookup == null
                    ? null
                    : new { p.BusinessAreaLookup.Id, p.BusinessAreaLookup.Name },
                priority = p.DeliveryPriority == null
                    ? null
                    : new { p.DeliveryPriority.Id, p.DeliveryPriority.Name },
                portfolio = p.PrimaryOrganizationalGroup == null
                    ? null
                    : new { p.PrimaryOrganizationalGroup.Id, p.PrimaryOrganizationalGroup.Name },
                primaryContact = p.PrimaryContactUser == null
                    ? null
                    : new { p.PrimaryContactUser.Id, p.PrimaryContactUser.Name, p.PrimaryContactUser.Email },
                tags = p.ProjectWorkItemTags
                    .Where(t => t.WorkItemTagLookup != null && t.WorkItemTagLookup.IsActive)
                    .Select(t => new { t.WorkItemTagLookup.Id, t.WorkItemTagLookup.Name })
                    .ToList(),
                p.CreatedAt,
                p.UpdatedAt
            })
            .ToListAsync();

        return Ok(new
        {
            data = items,
            pagination = new
            {
                currentPage = page,
                pageSize,
                totalPages,
                totalRecords
            }
        });
    }

    [HttpGet("{id:int}")]
    [RequireApiPermission("WorkItems", "read")]
    public async Task<IActionResult> GetWorkItem(int id)
    {
        var project = await _context.Projects
            .AsNoTracking()
            .Include(p => p.RagStatusLookup)
            .Include(p => p.PhaseLookup)
            .Include(p => p.BusinessAreaLookup)
            .Include(p => p.DeliveryPriority)
            .Include(p => p.PrimaryOrganizationalGroup)
            .Include(p => p.PrimaryContactUser)
            .Include(p => p.ActivityTypeLookup)
            .Include(p => p.RiskAppetiteLookup)
            .Include(p => p.ProjectWorkItemTags)
                .ThenInclude(t => t.WorkItemTagLookup)
            .Include(p => p.Directorates)
                .ThenInclude(d => d.Division)
            .Include(p => p.ProjectContacts)
            .Include(p => p.SeniorResponsibleOfficers)
                .ThenInclude(s => s.User)
            .Include(p => p.ServiceOwners)
                .ThenInclude(s => s.User)
            .Include(p => p.PmoContacts)
                .ThenInclude(s => s.User)
            .Include(p => p.ProjectProducts)
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);

        if (project == null)
        {
            return NotFound(new
            {
                error = new
                {
                    code = "NOT_FOUND",
                    message = $"Work item with ID {id} not found"
                }
            });
        }

        var problemStatement = await _context.ProjectProblemStatements
            .AsNoTracking()
            .Where(s => s.ProjectId == id)
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => s.ProblemStatement)
            .FirstOrDefaultAsync();

        var openRiskCount = await _context.Risks.AsNoTracking()
            .CountAsync(r => r.ProjectId == id && !r.IsDeleted && r.Status != "closed");
        var openIssueCount = await _context.Issues.AsNoTracking()
            .CountAsync(i => i.ProjectId == id && !i.IsDeleted && i.Status != "closed" && i.Status != "resolved");
        var milestoneCount = await _context.Milestones.AsNoTracking()
            .CountAsync(m => m.ProjectId == id && !m.IsDeleted);

        var latestMonthly = await _context.ProjectMonthlyUpdates
            .AsNoTracking()
            .Where(u => u.ProjectId == id)
            .OrderByDescending(u => u.Year)
            .ThenByDescending(u => u.Month)
            .Select(u => new
            {
                u.Id,
                u.Year,
                u.Month,
                u.SubmittedAt,
                submittedBy = u.CreatedByName ?? u.CreatedByEmail
            })
            .FirstOrDefaultAsync();

        return Ok(new
        {
            project.Id,
            project.ProjectCode,
            project.Title,
            project.Aim,
            problemStatement,
            project.Status,
            project.StartDate,
            targetEndDate = project.TargetDeliveryDate,
            project.ActualDeliveryDate,
            project.IsFlagship,
            project.ShowInFips,
            project.IsAiInitiative,
            project.IsSubjectToSpendControl,
            ragStatus = project.RagStatusLookup == null
                ? null
                : new { project.RagStatusLookup.Id, project.RagStatusLookup.Name },
            phase = project.PhaseLookup == null
                ? null
                : new { project.PhaseLookup.Id, project.PhaseLookup.Name },
            businessArea = project.BusinessAreaLookup == null
                ? null
                : new { project.BusinessAreaLookup.Id, project.BusinessAreaLookup.Name },
            priority = project.DeliveryPriority == null
                ? null
                : new { project.DeliveryPriority.Id, project.DeliveryPriority.Name },
            portfolio = project.PrimaryOrganizationalGroup == null
                ? null
                : new { project.PrimaryOrganizationalGroup.Id, project.PrimaryOrganizationalGroup.Name },
            activityType = project.ActivityTypeLookup == null
                ? null
                : new { project.ActivityTypeLookup.Id, project.ActivityTypeLookup.Name },
            riskAppetite = project.RiskAppetiteLookup == null
                ? null
                : new { project.RiskAppetiteLookup.Id, project.RiskAppetiteLookup.Name },
            primaryContact = project.PrimaryContactUser == null
                ? null
                : new { project.PrimaryContactUser.Id, project.PrimaryContactUser.Name, project.PrimaryContactUser.Email },
            tags = project.ProjectWorkItemTags
                .Where(t => t.WorkItemTagLookup is { IsActive: true })
                .Select(t => new { t.WorkItemTagLookup.Id, t.WorkItemTagLookup.Name })
                .ToList(),
            directorates = project.Directorates
                .Select(d => new { d.DivisionId, name = d.Division.Name })
                .ToList(),
            contacts = project.ProjectContacts
                .OrderBy(c => c.SortOrder)
                .Select(c => new { c.Id, c.Role, c.Name, c.Email })
                .ToList(),
            seniorResponsibleOfficers = project.SeniorResponsibleOfficers
                .Select(s => new { s.User.Id, s.User.Name, s.User.Email })
                .ToList(),
            serviceOwners = project.ServiceOwners
                .Select(s => new { s.User.Id, s.User.Name, s.User.Email })
                .ToList(),
            pmoContacts = project.PmoContacts
                .Select(s => new { s.User.Id, s.User.Name, s.User.Email })
                .ToList(),
            linkedProducts = project.ProjectProducts
                .Select(pp => new
                {
                    documentId = pp.ProductDocumentId,
                    fipsId = pp.ProductFipsId,
                    title = pp.ProductTitle
                })
                .ToList(),
            counts = new
            {
                openRisks = openRiskCount,
                openIssues = openIssueCount,
                milestones = milestoneCount
            },
            latestMonthlyUpdate = latestMonthly,
            project.CreatedAt,
            project.UpdatedAt
        });
    }

    [HttpPost]
    [RequireApiPermission("WorkItems", "create")]
    public async Task<IActionResult> CreateWorkItem([FromBody] WorkItemCreateDto dto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new
            {
                error = new
                {
                    code = "VALIDATION_ERROR",
                    message = "Invalid request data",
                    details = ModelState.Values.SelectMany(v => v.Errors.Select(e => e.ErrorMessage))
                }
            });
        }

        var status = string.IsNullOrWhiteSpace(dto.Status) ? "Active" : dto.Status.Trim();
        if (!AllowedStatuses.Contains(status))
        {
            return BadRequest(new
            {
                error = new
                {
                    code = "VALIDATION_ERROR",
                    message = "Status must be Active, Paused, Completed or Cancelled"
                }
            });
        }

        if (dto.PrimaryContactUserId is > 0)
        {
            var contactExists = await _context.Users.AsNoTracking()
                .AnyAsync(u => u.Id == dto.PrimaryContactUserId.Value);
            if (!contactExists)
            {
                return BadRequest(new
                {
                    error = new
                    {
                        code = "VALIDATION_ERROR",
                        message = $"Primary contact user with ID {dto.PrimaryContactUserId} not found"
                    }
                });
            }
        }

        var now = DateTime.UtcNow;
        var project = new Project
        {
            ProjectCode = await NextProjectCodeAsync(),
            Title = dto.Title.Trim(),
            Aim = string.IsNullOrWhiteSpace(dto.Aim) ? null : dto.Aim.Trim(),
            Status = status,
            StartDate = dto.StartDate,
            TargetDeliveryDate = dto.TargetEndDate,
            BusinessAreaId = dto.BusinessAreaId,
            PrimaryOrganizationalGroupId = dto.PortfolioId,
            PhaseId = dto.PhaseId,
            DeliveryPriorityId = dto.PriorityId,
            RagStatusLookupId = dto.RagStatusId,
            ActivityTypeLookupId = dto.ActivityTypeId,
            RiskAppetiteLookupId = dto.RiskAppetiteId,
            PrimaryContactUserId = dto.PrimaryContactUserId is > 0 ? dto.PrimaryContactUserId : null,
            IsFlagship = dto.IsFlagship ?? false,
            ShowInFips = dto.ShowInFips ?? false,
            IsAiInitiative = dto.IsAiInitiative ?? false,
            IsSubjectToSpendControl = dto.IsSubjectToSpendControl,
            CreatedAt = now,
            UpdatedAt = now,
            CreationMethod = "API"
        };

        _context.Projects.Add(project);
        await _context.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(dto.ProblemStatement))
        {
            _context.ProjectProblemStatements.Add(new ProjectProblemStatement
            {
                ProjectId = project.Id,
                ProblemStatement = dto.ProblemStatement.Trim(),
                CreatedByEmail = User.Identity?.Name,
                CreatedAt = now,
                UpdatedAt = now
            });
            await _context.SaveChangesAsync();
        }

        if (dto.TagIds is { Count: > 0 })
        {
            var validTagIds = await _context.WorkItemTagLookups.AsNoTracking()
                .Where(t => t.IsActive && dto.TagIds.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync();
            foreach (var tagId in validTagIds.Distinct())
            {
                _context.ProjectWorkItemTags.Add(new ProjectWorkItemTag
                {
                    ProjectId = project.Id,
                    WorkItemTagLookupId = tagId
                });
            }
            await _context.SaveChangesAsync();
        }

        return CreatedAtAction(nameof(GetWorkItem), new { id = project.Id }, new
        {
            project.Id,
            project.ProjectCode,
            project.Title,
            project.Status,
            project.CreatedAt
        });
    }

    [HttpPut("{id:int}")]
    [RequireApiPermission("WorkItems", "update")]
    public async Task<IActionResult> UpdateWorkItem(int id, [FromBody] WorkItemUpdateDto dto)
    {
        var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);
        if (project == null)
        {
            return NotFound(new
            {
                error = new
                {
                    code = "NOT_FOUND",
                    message = $"Work item with ID {id} not found"
                }
            });
        }

        if (dto.Status != null)
        {
            if (!AllowedStatuses.Contains(dto.Status))
            {
                return BadRequest(new
                {
                    error = new
                    {
                        code = "VALIDATION_ERROR",
                        message = "Status must be Active, Paused, Completed or Cancelled"
                    }
                });
            }
            project.Status = dto.Status.Trim();
        }

        if (dto.Title != null) project.Title = dto.Title.Trim();
        if (dto.Aim != null) project.Aim = dto.Aim.Trim();
        if (dto.StartDate.HasValue) project.StartDate = dto.StartDate;
        if (dto.TargetEndDate.HasValue) project.TargetDeliveryDate = dto.TargetEndDate;
        if (dto.ActualDeliveryDate.HasValue) project.ActualDeliveryDate = dto.ActualDeliveryDate;
        if (dto.BusinessAreaId.HasValue) project.BusinessAreaId = dto.BusinessAreaId;
        if (dto.PortfolioId.HasValue) project.PrimaryOrganizationalGroupId = dto.PortfolioId;
        if (dto.PhaseId.HasValue) project.PhaseId = dto.PhaseId;
        if (dto.PriorityId.HasValue) project.DeliveryPriorityId = dto.PriorityId;
        if (dto.RagStatusId.HasValue) project.RagStatusLookupId = dto.RagStatusId;
        if (dto.ActivityTypeId.HasValue) project.ActivityTypeLookupId = dto.ActivityTypeId;
        if (dto.RiskAppetiteId.HasValue) project.RiskAppetiteLookupId = dto.RiskAppetiteId;
        if (dto.PrimaryContactUserId.HasValue)
            project.PrimaryContactUserId = dto.PrimaryContactUserId is > 0 ? dto.PrimaryContactUserId : null;
        if (dto.IsFlagship.HasValue) project.IsFlagship = dto.IsFlagship.Value;
        if (dto.ShowInFips.HasValue) project.ShowInFips = dto.ShowInFips.Value;
        if (dto.IsAiInitiative.HasValue) project.IsAiInitiative = dto.IsAiInitiative.Value;
        if (dto.IsSubjectToSpendControl.HasValue) project.IsSubjectToSpendControl = dto.IsSubjectToSpendControl;

        project.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new
        {
            project.Id,
            project.ProjectCode,
            project.Title,
            project.Status,
            project.UpdatedAt
        });
    }

    [HttpDelete("{id:int}")]
    [RequireApiPermission("WorkItems", "delete")]
    public async Task<IActionResult> DeleteWorkItem(int id)
    {
        var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);
        if (project == null)
        {
            return NotFound(new
            {
                error = new
                {
                    code = "NOT_FOUND",
                    message = $"Work item with ID {id} not found"
                }
            });
        }

        project.IsDeleted = true;
        project.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new { message = "Work item deleted" });
    }

    private async Task<string> NextProjectCodeAsync()
    {
        var lastProject = await _context.Projects
            .OrderByDescending(p => p.ProjectCode)
            .FirstOrDefaultAsync();
        var nextNumber = 1;
        if (lastProject != null && !string.IsNullOrEmpty(lastProject.ProjectCode))
        {
            var parts = lastProject.ProjectCode.Split('-');
            if (parts.Length >= 2 && int.TryParse(parts[^1], out var lastNumber))
                nextNumber = lastNumber + 1;
        }

        return $"DDTDEL-{nextNumber:D4}";
    }

    /// <summary>Accepts true/false, yes/no, and 1/0 (case-insensitive).</summary>
    private static bool? ParseYesNoFlag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var v = value.Trim();
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || v == "1")
            return true;

        if (v.Equals("false", StringComparison.OrdinalIgnoreCase)
            || v.Equals("no", StringComparison.OrdinalIgnoreCase)
            || v == "0")
            return false;

        return null;
    }
}

public class WorkItemCreateDto
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public string? Aim { get; set; }
    public string? ProblemStatement { get; set; }
    public string? Status { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? TargetEndDate { get; set; }
    public int? BusinessAreaId { get; set; }
    public int? PortfolioId { get; set; }
    public int? PhaseId { get; set; }
    public int? PriorityId { get; set; }
    public int? RagStatusId { get; set; }
    public int? ActivityTypeId { get; set; }
    public int? RiskAppetiteId { get; set; }
    public int? PrimaryContactUserId { get; set; }
    public bool? IsFlagship { get; set; }
    public bool? ShowInFips { get; set; }
    public bool? IsAiInitiative { get; set; }
    public bool? IsSubjectToSpendControl { get; set; }
    public List<int>? TagIds { get; set; }
}

public class WorkItemUpdateDto
{
    [MaxLength(200)]
    public string? Title { get; set; }
    public string? Aim { get; set; }
    public string? Status { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? TargetEndDate { get; set; }
    public DateTime? ActualDeliveryDate { get; set; }
    public int? BusinessAreaId { get; set; }
    public int? PortfolioId { get; set; }
    public int? PhaseId { get; set; }
    public int? PriorityId { get; set; }
    public int? RagStatusId { get; set; }
    public int? ActivityTypeId { get; set; }
    public int? RiskAppetiteId { get; set; }
    public int? PrimaryContactUserId { get; set; }
    public bool? IsFlagship { get; set; }
    public bool? ShowInFips { get; set; }
    public bool? IsAiInitiative { get; set; }
    public bool? IsSubjectToSpendControl { get; set; }
}
