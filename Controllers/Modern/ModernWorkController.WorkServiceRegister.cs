using Compass.Models;
using Compass.Models.Fips;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compass.Controllers.Modern;

public partial class ModernWorkController
{
    [HttpGet("{id:int}/service-register/pick-products")]
    public async Task<IActionResult> PickServiceRegisterProducts(int id, [FromQuery] string? q, CancellationToken ct)
    {
        var email = User.Identity?.Name;
        if (string.IsNullOrEmpty(email))
            return Unauthorized();
        if (!await _workServiceRegisterLinks.CanLinkFromWorkItemAsync(id, email, ct))
            return new JsonResult(new { error = "Access denied." }) { StatusCode = 403 };

        var term = (q ?? "").Trim();
        if (term.Length < 2)
            return Json(new { results = Array.Empty<object>() });

        var results = await _context.CMDBProducts
            .AsNoTracking()
            .Where(p =>
                p.Status != CMDBProductStatus.Rejected &&
                ((p.Title != null && p.Title.Contains(term)) ||
                 (p.CMDBID != null && p.CMDBID.Contains(term)) ||
                 p.UniqueID.ToString().Contains(term)))
            .OrderBy(p => p.Title)
            .Take(20)
            .Select(p => new
            {
                id = p.Id.ToString(),
                title = p.Title,
                subtitle = p.CMDBID,
                uniqueId = p.UniqueID,
                serviceOwner = p.Contacts
                    .Where(c => c.FipsContactRole != null && c.FipsContactRole.Name == "Service Owner")
                    .Select(c => c.UserName ?? c.UserEmail)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return Json(new { results });
    }

    [HttpPost("{id:int}/service-register/link")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkServiceRegisterProduct(int id, Guid cmdbProductId, CancellationToken ct)
    {
        var email = User.Identity?.Name;
        if (string.IsNullOrEmpty(email))
            return Unauthorized();

        var outcome = await _workServiceRegisterLinks.LinkAsync(id, cmdbProductId, email, ct);
        if (!outcome.Success)
        {
            TempData["ErrorMessage"] = outcome.Error ?? "Could not link service register entry.";
            return Redirect(Url.Action(nameof(Detail), new { id })! + "#wd-service-register");
        }

        TempData["SuccessMessage"] = "Service register entry linked.";
        return Redirect(Url.Action(nameof(Detail), new { id })! + "#wd-service-register");
    }

    [HttpPost("{id:int}/service-register/{projectProductId:int}/unlink")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnlinkServiceRegisterProduct(int id, int projectProductId, CancellationToken ct)
    {
        var email = User.Identity?.Name;
        if (string.IsNullOrEmpty(email))
            return Unauthorized();

        var outcome = await _workServiceRegisterLinks.UnlinkAsync(projectProductId, email, ct);
        if (!outcome.Success)
        {
            TempData["ErrorMessage"] = outcome.Error ?? "Could not remove link.";
            return Redirect(Url.Action(nameof(Detail), new { id })! + "#wd-service-register");
        }

        TempData["SuccessMessage"] = "Service register entry unlinked.";
        return Redirect(Url.Action(nameof(Detail), new { id })! + "#wd-service-register");
    }

    [HttpPost("{id:int}/fips")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateShowInFips(int id, bool? showInFips, CancellationToken ct)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, ct);
        if (deny != null)
            return deny;

        var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, ct);
        if (project == null)
            return NotFound();

        project.ShowInFips = showInFips == true;
        project.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        TempData["SuccessMessage"] = project.ShowInFips
            ? "This work item will be shown in FIPS."
            : "This work item will not be shown in FIPS.";
        return Redirect(Url.Action(nameof(Detail), new { id })! + "#wd-service-register");
    }
}
