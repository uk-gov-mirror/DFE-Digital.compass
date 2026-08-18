using System.ComponentModel.DataAnnotations;
using Compass.Attributes;
using Compass.Data;
using Compass.Models;
using Compass.Models.Fips;
using Compass.Services.Fips;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compass.Controllers.Api.V1;

[ApiController]
[Route("api/v1/[controller]")]
public class ServiceRegisterController : ControllerBase
{
    private readonly CompassDbContext _context;
    private readonly IFipsProductWriteService _fipsProductWrite;
    private readonly ILogger<ServiceRegisterController> _logger;

    public ServiceRegisterController(
        CompassDbContext context,
        IFipsProductWriteService fipsProductWrite,
        ILogger<ServiceRegisterController> logger)
    {
        _context = context;
        _fipsProductWrite = fipsProductWrite;
        _logger = logger;
    }

    /// <summary>
    /// List FIPS / service register (CMDB) products. Grant <c>read</c> on resource <c>ServiceRegister</c> for the API token.
    /// Use <c>status=Active</c> with <c>excludeEnterprise=true</c> for the AISS standard catalogue; <c>numericId</c> looks up by
    /// <see cref="CMDBProduct.UniqueID"/> (FIPS numeric id) and ignores status filters so onboarding search finds a row
    /// even when enterprise or sync left it Inactive/Rejected. <c>GET .../products/enterprise-active</c> lists
    /// enterprise-flagged products excluding retired/rejected (includes New and Active).
    /// Optional <c>categoryIds</c> (repeat param): product matches if it has any of those FIPS categorisation items.
    /// Optional <c>channelIds</c>, <c>typeIds</c>, <c>businessAreaIds</c>, <c>userGroupIds</c>, <c>contactRoleIds</c>:
    /// product must satisfy each non-empty dimension (within a dimension, any selected id may match).
    /// Optional <c>q</c>: contains match on product title (trimmed; collation-dependent).
    /// </summary>
    [HttpGet("products")]
    [RequireApiPermission("ServiceRegister", "read")]
    public async Task<IActionResult> GetProducts(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        [FromQuery] string[]? status = null,
        [FromQuery] bool? enterpriseOnly = null,
        [FromQuery] bool? excludeEnterprise = null,
        [FromQuery] int? numericId = null,
        [FromQuery] int[]? categoryIds = null,
        [FromQuery] int[]? channelIds = null,
        [FromQuery] int[]? typeIds = null,
        [FromQuery] int[]? businessAreaIds = null,
        [FromQuery] int[]? userGroupIds = null,
        [FromQuery] int[]? contactRoleIds = null,
        [FromQuery] string? q = null,
        CancellationToken cancellationToken = default)
    {
        if (pageSize > 1000) pageSize = 1000;
        if (pageSize < 1) pageSize = 50;
        if (page < 1) page = 1;

        List<CMDBProductStatus>? statusFilter = null;
        if (status is { Length: > 0 })
        {
            statusFilter = new List<CMDBProductStatus>();
            foreach (var s in status)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (!Enum.TryParse<CMDBProductStatus>(s.Trim(), true, out var st))
                {
                    return BadRequest(new
                    {
                        error = new
                        {
                            code = "INVALID_STATUS",
                            message = $"Unknown status value '{s}'. Use New, Active, Inactive, or Rejected."
                        }
                    });
                }
                if (!statusFilter.Contains(st)) statusFilter.Add(st);
            }
        }

        var baseQuery = _context.CMDBProducts.AsNoTracking();
        if (numericId.HasValue)
        {
            baseQuery = baseQuery.Where(p => p.UniqueID == numericId.Value);
        }
        else
        {
            if (statusFilter is { Count: > 0 })
                baseQuery = baseQuery.Where(p => statusFilter.Contains(p.Status));
            if (enterpriseOnly == true)
                baseQuery = baseQuery.Where(p => p.IsEnterpriseService);
            else if (excludeEnterprise == true)
                baseQuery = baseQuery.Where(p => !p.IsEnterpriseService);
        }

        baseQuery = ApplyProductFilters(
            baseQuery,
            categoryIds,
            channelIds,
            typeIds,
            businessAreaIds,
            userGroupIds,
            contactRoleIds,
            q);

        var totalRecords = await baseQuery.CountAsync(cancellationToken);
        var totalPages = (int)Math.Ceiling(totalRecords / (double)pageSize);

        var products = await IncludeProductListGraph(
                baseQuery
                    .OrderBy(p => p.Title)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize))
            .ToListAsync(cancellationToken);

        var rows = products.Select(MapProductListRow).ToList();

        return Ok(new
        {
            data = rows,
            pagination = new
            {
                currentPage = page,
                pageSize,
                totalPages,
                totalRecords
            }
        });
    }

    /// <summary>
    /// Active FIPS categorisation items (with group names) for catalogue filters and browse. Grant <c>read</c> on <c>ServiceRegister</c>.
    /// </summary>
    [HttpGet("categorisation-items")]
    [RequireApiPermission("ServiceRegister", "read")]
    public async Task<IActionResult> GetCategorisationItems(CancellationToken cancellationToken = default)
    {
        var rows = await _context.FipsCategorisationItems.AsNoTracking()
            .Where(i => i.Active && i.Group.Active)
            .OrderBy(i => i.Group.DisplayOrder)
            .ThenBy(i => i.DisplayOrder)
            .ThenBy(i => i.Name)
            .Select(i => new
            {
                id = i.Id,
                name = i.Name,
                description = i.Description,
                groupId = i.FipsCategorisationGroupId,
                groupName = i.Group.Name,
                groupDescription = i.Group.Description,
                displayOrder = i.DisplayOrder
            })
            .ToListAsync(cancellationToken);

        return Ok(new { data = rows });
    }

    /// <summary>
    /// All FIPS lookup values (same payload as <c>GET /api/products/fips/configuration</c>) via the Bearer <c>/api/v1</c> pipeline.
    /// </summary>
    [HttpGet("fips")]
    [HttpGet("fips/configuration")]
    [RequireApiPermission("ServiceRegister", "read")]
    public async Task<IActionResult> GetFipsConfigurationBundle(CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(new
            {
                channels = await QueryFipsChannelsAsync(cancellationToken),
                types = await QueryFipsTypesAsync(cancellationToken),
                businessAreas = await QueryFipsBusinessAreasAsync(cancellationToken),
                userGroups = await QueryFipsUserGroupsAsync(cancellationToken),
                contactRoles = await QueryFipsContactRolesAsync(cancellationToken),
                categorisationGroups = await QueryFipsCategorisationAsync(cancellationToken)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading FIPS configuration bundle (v1)");
            return StatusCode(500, new { error = "An error occurred while loading FIPS configuration" });
        }
    }

    [HttpGet("fips/channels")]
    [RequireApiPermission("ServiceRegister", "read")]
    public async Task<IActionResult> GetFipsChannelsV1(CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await QueryFipsChannelsAsync(cancellationToken);
            return Ok(new { data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading FIPS channels (v1)");
            return StatusCode(500, new { error = "An error occurred while loading FIPS channels" });
        }
    }

    [HttpGet("fips/types")]
    [RequireApiPermission("ServiceRegister", "read")]
    public async Task<IActionResult> GetFipsTypesV1(CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await QueryFipsTypesAsync(cancellationToken);
            return Ok(new { data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading FIPS types (v1)");
            return StatusCode(500, new { error = "An error occurred while loading FIPS types" });
        }
    }

    [HttpGet("fips/business-areas")]
    [RequireApiPermission("ServiceRegister", "read")]
    public async Task<IActionResult> GetFipsBusinessAreasV1(CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await QueryFipsBusinessAreasAsync(cancellationToken);
            return Ok(new { data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading FIPS business areas (v1)");
            return StatusCode(500, new { error = "An error occurred while loading FIPS business areas" });
        }
    }

    [HttpGet("fips/user-groups")]
    [RequireApiPermission("ServiceRegister", "read")]
    public async Task<IActionResult> GetFipsUserGroupsV1(CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await QueryFipsUserGroupsAsync(cancellationToken);
            return Ok(new { data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading FIPS user groups (v1)");
            return StatusCode(500, new { error = "An error occurred while loading FIPS user groups" });
        }
    }

    [HttpGet("fips/contact-roles")]
    [RequireApiPermission("ServiceRegister", "read")]
    public async Task<IActionResult> GetFipsContactRolesV1(CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await QueryFipsContactRolesAsync(cancellationToken);
            return Ok(new { data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading FIPS contact roles (v1)");
            return StatusCode(500, new { error = "An error occurred while loading FIPS contact roles" });
        }
    }

    [HttpGet("fips/categorisation")]
    [RequireApiPermission("ServiceRegister", "read")]
    public async Task<IActionResult> GetFipsCategorisationNestedV1(CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await QueryFipsCategorisationAsync(cancellationToken);
            return Ok(new { data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading FIPS categorisation (v1)");
            return StatusCode(500, new { error = "An error occurred while loading FIPS categorisation" });
        }
    }

    /// <summary>
    /// Service register products flagged as enterprise (<see cref="CMDBProduct.IsEnterpriseService"/>), excluding retired (Inactive)
    /// and excluded (Rejected). Includes <see cref="CMDBProductStatus.New"/> and <see cref="CMDBProductStatus.Active"/> — CMDB sync
    /// often leaves rows as New. Same optional FIPS filters as <see cref="GetProducts"/> (<c>categoryIds</c>, <c>channelIds</c>, …).
    /// Grant <c>read</c> on <c>ServiceRegister</c> for the API token.
    /// </summary>
    [HttpGet("products/enterprise-active")]
    [RequireApiPermission("ServiceRegister", "read")]
    public async Task<IActionResult> GetEnterpriseActiveProducts(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        [FromQuery] int[]? categoryIds = null,
        [FromQuery] int[]? channelIds = null,
        [FromQuery] int[]? typeIds = null,
        [FromQuery] int[]? businessAreaIds = null,
        [FromQuery] int[]? userGroupIds = null,
        [FromQuery] int[]? contactRoleIds = null,
        [FromQuery] string? q = null,
        CancellationToken cancellationToken = default)
    {
        if (pageSize > 1000) pageSize = 1000;
        if (pageSize < 1) pageSize = 50;
        if (page < 1) page = 1;

        var baseQuery = _context.CMDBProducts.AsNoTracking()
            .Where(p => p.IsEnterpriseService
                        && p.Status != CMDBProductStatus.Inactive
                        && p.Status != CMDBProductStatus.Rejected);

        baseQuery = ApplyProductFilters(
            baseQuery,
            categoryIds,
            channelIds,
            typeIds,
            businessAreaIds,
            userGroupIds,
            contactRoleIds,
            q);

        var totalRecords = await baseQuery.CountAsync(cancellationToken);
        var totalPages = (int)Math.Ceiling(totalRecords / (double)pageSize);

        var products = await IncludeProductListGraph(
                baseQuery
                    .OrderBy(p => p.Title)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize))
            .ToListAsync(cancellationToken);

        var rows = products.Select(MapProductListRow).ToList();

        return Ok(new
        {
            data = rows,
            pagination = new
            {
                currentPage = page,
                pageSize,
                totalPages,
                totalRecords
            }
        });
    }

    /// <summary>
    /// Get a single FIPS / service register product by id. Grant <c>read</c> on resource <c>ServiceRegister</c> for the API token.
    /// </summary>
    [HttpGet("products/{id:guid}")]
    [RequireApiPermission("ServiceRegister", "read")]
    public async Task<IActionResult> GetProduct(Guid id, CancellationToken cancellationToken = default)
    {
        var p = await IncludeProductListGraph(
                _context.CMDBProducts.AsNoTracking().Where(x => x.Id == id))
            .FirstOrDefaultAsync(cancellationToken);

        if (p == null)
        {
            return NotFound(new
            {
                error = new
                {
                    code = "NOT_FOUND",
                    message = "Product not found."
                }
            });
        }

        var row = MapProductListRow(p);

        return Ok(new { data = row });
    }

    /// <summary>
    /// Update a product’s service URL. Grant <c>update</c> on resource <c>ServiceRegister</c> for the API token.
    /// </summary>
    [HttpPatch("products/{id:guid}")]
    [RequireApiPermission("ServiceRegister", "update")]
    public async Task<IActionResult> PatchProduct(
        Guid id,
        [FromBody] ServiceRegisterProductUrlRequest? body,
        CancellationToken cancellationToken = default)
    {
        if (body == null)
        {
            return BadRequest(new
            {
                error = new
                {
                    code = "INVALID_REQUEST",
                    message = "Request body is required (JSON: { \"productUrl\": \"...\" })."
                }
            });
        }

        var normalized = string.IsNullOrWhiteSpace(body.ProductUrl) ? null : body.ProductUrl.Trim();
        if (normalized != null && normalized.Length > 2000)
        {
            return BadRequest(new
            {
                error = new
                {
                    code = "VALIDATION_ERROR",
                    message = "Product URL must be at most 2000 characters."
                }
            });
        }

        var token = HttpContext.Items["ApiToken"] as ApiToken;
        var actorEmail = token != null ? $"api-token:{token.Id}" : "api-token";
        var display = token != null && !string.IsNullOrWhiteSpace(token.Name)
            ? TruncateForAudit("API: " + token.Name)
            : "API token";

        var outcome = await _fipsProductWrite.TryUpdateProductUrlOnlyAsync(
            id, actorEmail, display, normalized, cancellationToken);

        if (outcome.NotFound)
        {
            return NotFound(new
            {
                error = new
                {
                    code = "NOT_FOUND",
                    message = "Product not found."
                }
            });
        }

        _logger.LogInformation("Service register product URL API update for {ProductId}, changes: {Changes}", id, outcome.Changes);

        return Ok(new
        {
            data = new
            {
                id,
                productUrl = normalized,
                message = outcome.Changes.Count == 0 ? "No change (URL already matches)." : "Product URL updated."
            }
        });
    }

    private async Task<List<FipsLookupApiRow>> QueryFipsChannelsAsync(CancellationToken cancellationToken)
    {
        var rows = await _context.FipsChannels.AsNoTracking()
            .OrderBy(x => x.DisplayOrder).ThenBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, x.Description, x.DisplayOrder, x.Active })
            .ToListAsync(cancellationToken);
        return rows.Select(x => new FipsLookupApiRow(x.Id, x.Name, x.Description, x.DisplayOrder, x.Active)).ToList();
    }

    private async Task<List<FipsLookupApiRow>> QueryFipsTypesAsync(CancellationToken cancellationToken)
    {
        var rows = await _context.FipsTypes.AsNoTracking()
            .OrderBy(x => x.DisplayOrder).ThenBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, x.Description, x.DisplayOrder, x.Active })
            .ToListAsync(cancellationToken);
        return rows.Select(x => new FipsLookupApiRow(x.Id, x.Name, x.Description, x.DisplayOrder, x.Active)).ToList();
    }

    private async Task<List<FipsBusinessAreaApiRow>> QueryFipsBusinessAreasAsync(CancellationToken cancellationToken)
    {
        var rows = await _context.FipsBusinessAreas.AsNoTracking()
            .OrderBy(x => x.DisplayOrder).ThenBy(x => x.Name)
            .Select(x => new { x.Id, x.BusinessAreaLookupId, x.Name, x.Description, x.DisplayOrder, x.Active })
            .ToListAsync(cancellationToken);
        return rows.Select(x => new FipsBusinessAreaApiRow(
            x.Id,
            x.BusinessAreaLookupId,
            x.Name,
            x.Description,
            x.DisplayOrder,
            x.Active)).ToList();
    }

    private async Task<List<FipsUserGroupApiRow>> QueryFipsUserGroupsAsync(CancellationToken cancellationToken)
    {
        var roots = await _context.FipsUserGroups.AsNoTracking()
            .Include(g => g.Children)
            .Include(g => g.Synonyms)
            .Where(g => g.ParentId == null)
            .OrderBy(g => g.DisplayOrder)
            .ToListAsync(cancellationToken);

        return roots.Select(g => new FipsUserGroupApiRow(
                g.Id,
                g.Name,
                g.Description,
                g.DisplayOrder,
                g.Active,
                g.Children.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).Select(c => c.Name).ToList(),
                g.Synonyms.Select(s => s.Synonym).OrderBy(s => s).ToList()))
            .ToList();
    }

    private async Task<List<FipsContactRoleApiRow>> QueryFipsContactRolesAsync(CancellationToken cancellationToken)
    {
        var rows = await _context.FipsContactRoles.AsNoTracking()
            .OrderBy(x => x.DisplayOrder)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.Description,
                x.AllowMultiple,
                x.DisplayOrder,
                x.Active
            })
            .ToListAsync(cancellationToken);
        return rows.Select(x => new FipsContactRoleApiRow(
            x.Id,
            x.Name,
            x.Description,
            x.AllowMultiple,
            x.DisplayOrder,
            x.Active)).ToList();
    }

    private async Task<List<FipsCategorisationGroupApiRow>> QueryFipsCategorisationAsync(CancellationToken cancellationToken)
    {
        var groups = await _context.FipsCategorisationGroups.AsNoTracking()
            .Include(g => g.Items)
            .OrderBy(g => g.DisplayOrder).ThenBy(g => g.Name)
            .ToListAsync(cancellationToken);

        return groups.Select(g => new FipsCategorisationGroupApiRow(
                g.Id,
                g.Name,
                g.Description,
                g.DisplayOrder,
                g.Active,
                g.Items
                    .OrderBy(i => i.DisplayOrder).ThenBy(i => i.Name)
                    .Select(i => new FipsCategorisationItemApiRow(
                        i.Id,
                        i.Name,
                        i.Description,
                        i.DisplayOrder,
                        i.Active,
                        i.FipsCategorisationGroupId))
                    .ToList()))
            .ToList();
    }

    private sealed record FipsLookupApiRow(int Id, string Name, string? Description, int DisplayOrder, bool Active);

    private sealed record FipsBusinessAreaApiRow(
        int Id,
        int? BusinessAreaLookupId,
        string Name,
        string? Description,
        int DisplayOrder,
        bool Active);

    private sealed record FipsUserGroupApiRow(
        int Id,
        string Name,
        string? Description,
        int DisplayOrder,
        bool Active,
        IReadOnlyList<string> Children,
        IReadOnlyList<string> Synonyms);

    private sealed record FipsContactRoleApiRow(
        int Id,
        string Name,
        string? Description,
        bool AllowMultiple,
        int DisplayOrder,
        bool Active);

    private sealed record FipsCategorisationGroupApiRow(
        int Id,
        string Name,
        string? Description,
        int DisplayOrder,
        bool Active,
        IReadOnlyList<FipsCategorisationItemApiRow> Items);

    private sealed record FipsCategorisationItemApiRow(
        int Id,
        string Name,
        string? Description,
        int DisplayOrder,
        bool Active,
        int CategorisationGroupId);

    private static string TruncateForAudit(string s) => s.Length > 200 ? s[..200] : s;

    private static IQueryable<CMDBProduct> IncludeProductListGraph(IQueryable<CMDBProduct> query) =>
        query
            .Include(p => p.Phase)
            .Include(p => p.BusinessAreas)
            .ThenInclude(ba => ba.FipsBusinessArea)
            .Include(p => p.Channels)
            .ThenInclude(c => c.FipsChannel)
            .Include(p => p.Types)
            .ThenInclude(t => t.FipsType)
            .Include(p => p.Contacts)
            .ThenInclude(c => c.FipsContactRole)
            .Include(p => p.CategorisationItems)
            .ThenInclude(ci => ci.FipsCategorisationItem)
            .ThenInclude(i => i.Group);

    private static IQueryable<CMDBProduct> ApplyProductFilters(
        IQueryable<CMDBProduct> query,
        int[]? categoryIds,
        int[]? channelIds,
        int[]? typeIds,
        int[]? businessAreaIds,
        int[]? userGroupIds,
        int[]? contactRoleIds,
        string? q)
    {
        if (categoryIds is { Length: > 0 })
        {
            var ids = categoryIds.Where(id => id > 0).Distinct().ToArray();
            if (ids.Length > 0)
                query = query.Where(p => p.CategorisationItems.Any(ci => ids.Contains(ci.FipsCategorisationItemId)));
        }

        if (channelIds is { Length: > 0 })
        {
            var ids = channelIds.Where(id => id > 0).Distinct().ToArray();
            if (ids.Length > 0)
                query = query.Where(p => p.Channels.Any(c => ids.Contains(c.FipsChannelId)));
        }

        if (typeIds is { Length: > 0 })
        {
            var ids = typeIds.Where(id => id > 0).Distinct().ToArray();
            if (ids.Length > 0)
                query = query.Where(p => p.Types.Any(t => ids.Contains(t.FipsTypeId)));
        }

        if (businessAreaIds is { Length: > 0 })
        {
            var ids = businessAreaIds.Where(id => id > 0).Distinct().ToArray();
            if (ids.Length > 0)
                query = query.Where(p => p.BusinessAreas.Any(b => ids.Contains(b.FipsBusinessAreaId)));
        }

        if (userGroupIds is { Length: > 0 })
        {
            var ids = userGroupIds.Where(id => id > 0).Distinct().ToArray();
            if (ids.Length > 0)
                query = query.Where(p => p.UserGroups.Any(u => ids.Contains(u.FipsUserGroupId)));
        }

        if (contactRoleIds is { Length: > 0 })
        {
            var ids = contactRoleIds.Where(id => id > 0).Distinct().ToArray();
            if (ids.Length > 0)
                query = query.Where(p => p.Contacts.Any(c => ids.Contains(c.FipsContactRoleId)));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(p => p.Title.Contains(term));
        }

        return query;
    }

    private static object MapProductListRow(CMDBProduct p) =>
        new
        {
            id = p.Id,
            uniqueId = p.UniqueID,
            lastUpdated = p.UpdatedAt,
            productName = p.Title,
            phase = p.Phase?.Name,
            businessArea = string.Join(", ", p.BusinessAreas
                .OrderBy(ba => ba.FipsBusinessArea.Name)
                .Select(ba => ba.FipsBusinessArea.Name)),
            productUrl = p.ProductURL,
            status = p.Status.ToString(),
            isEnterpriseService = p.IsEnterpriseService,
            longDescription = ResolveLongDescription(p),
            channels = p.Channels
                .Where(c => c.FipsChannel != null)
                .OrderBy(c => c.FipsChannel.DisplayOrder)
                .ThenBy(c => c.FipsChannel.Name)
                .Select(c => new
                {
                    id = c.FipsChannelId,
                    name = c.FipsChannel.Name
                })
                .ToList(),
            types = p.Types
                .Where(t => t.FipsType != null)
                .OrderBy(t => t.FipsType.DisplayOrder)
                .ThenBy(t => t.FipsType.Name)
                .Select(t => new
                {
                    id = t.FipsTypeId,
                    name = t.FipsType.Name
                })
                .ToList(),
            categories = p.CategorisationItems
                .OrderBy(ci => ci.FipsCategorisationItem.Group.DisplayOrder)
                .ThenBy(ci => ci.FipsCategorisationItem.DisplayOrder)
                .ThenBy(ci => ci.FipsCategorisationItem.Name)
                .Select(ci => new
                {
                    id = ci.FipsCategorisationItemId,
                    name = ci.FipsCategorisationItem.Name,
                    groupId = ci.FipsCategorisationItem.FipsCategorisationGroupId,
                    groupName = ci.FipsCategorisationItem.Group.Name
                })
                .ToList(),
            contacts = p.Contacts
                .OrderBy(c => c.FipsContactRole != null ? c.FipsContactRole.Name : "")
                .ThenBy(c => c.UserEmail)
                .Select(c => new
                {
                    role = c.FipsContactRole != null ? c.FipsContactRole.Name : null,
                    roleId = c.FipsContactRoleId,
                    email = c.UserEmail,
                    name = c.UserName,
                    canManage = c.CanManage
                })
                .ToList()
        };

    /// <summary>User-edited description when set; otherwise the CMDB-synced description — exposed to FIPS as one long description.</summary>
    private static string? ResolveLongDescription(CMDBProduct p)
    {
        if (!string.IsNullOrWhiteSpace(p.UserDescription))
            return p.UserDescription.Trim();
        if (!string.IsNullOrWhiteSpace(p.CMDBDescription))
            return p.CMDBDescription.Trim();
        return null;
    }
}

public class ServiceRegisterProductUrlRequest
{
    /// <summary>Public URL of the product (live service). Omit or set null/empty to clear.</summary>
    [StringLength(2000)]
    public string? ProductUrl { get; set; }
}
