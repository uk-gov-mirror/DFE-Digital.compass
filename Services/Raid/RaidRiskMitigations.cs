using Compass.Data;
using Compass.Models;
using Microsoft.EntityFrameworkCore;
using ActionEntity = Compass.Models.Action;

namespace Compass.Services.Raid;

/// <summary>
/// Mitigations may be linked via <see cref="RiskAction"/> and/or <see cref="ActionEntity.RiskId"/>.
/// List and count both so the RAID view matches every way a mitigation can be added.
/// </summary>
public static class RaidRiskMitigations
{
    public sealed record Link(int RiskId, int ActionId, string Title, string? Notes);

    public static IQueryable<ActionEntity> QueryForRisk(CompassDbContext db, int riskId) =>
        db.Actions.Where(a =>
            !a.IsDeleted && (
                a.RiskId == riskId
                || a.RiskActions.Any(ra => ra.RiskId == riskId)));

    public static async Task<List<ActionEntity>> LoadForRiskAsync(
        CompassDbContext db,
        int riskId,
        CancellationToken cancellationToken)
    {
        return await QueryForRisk(db, riskId)
            .AsNoTracking()
            .Include(a => a.AssignedToUser)
            .OrderBy(a => a.DueDate ?? DateTime.MaxValue)
            .ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);
    }

    public static async Task<ActionEntity?> FindForRiskAsync(
        CompassDbContext db,
        int riskId,
        int actionId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        IQueryable<ActionEntity> query = db.Actions
            .Include(a => a.AssignedToUser)
            .Where(a =>
                a.Id == actionId
                && !a.IsDeleted
                && (a.RiskId == riskId || a.RiskActions.Any(ra => ra.RiskId == riskId)));

        if (!tracking)
            query = query.AsNoTracking();

        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Adds junction-only display rows for actions linked solely via <see cref="ActionEntity.RiskId"/>.
    /// Does not write to the database.
    /// </summary>
    public static async Task AttachOrphansInMemoryAsync(
        CompassDbContext db,
        Risk risk,
        CancellationToken cancellationToken)
    {
        risk.RiskActions ??= new List<RiskAction>();
        var linkedIds = risk.RiskActions.Select(ra => ra.ActionId).ToList();

        var orphans = await db.Actions.AsNoTracking()
            .Include(a => a.AssignedToUser)
            .Where(a => !a.IsDeleted && a.RiskId == risk.Id && !linkedIds.Contains(a.Id))
            .ToListAsync(cancellationToken);

        foreach (var action in orphans)
        {
            risk.RiskActions.Add(new RiskAction
            {
                RiskId = risk.Id,
                ActionId = action.Id,
                Action = action
            });
        }
    }

    public static async Task EnsureJunctionAsync(
        CompassDbContext db,
        int riskId,
        int actionId,
        CancellationToken cancellationToken)
    {
        var exists = await db.RiskActions.AnyAsync(
            ra => ra.RiskId == riskId && ra.ActionId == actionId,
            cancellationToken);
        if (!exists)
            db.RiskActions.Add(new RiskAction { RiskId = riskId, ActionId = actionId });
    }

    public static async Task<List<Link>> LoadLinksForRiskIdsAsync(
        CompassDbContext db,
        IReadOnlyCollection<int> riskIds,
        CancellationToken cancellationToken)
    {
        if (riskIds.Count == 0)
            return new List<Link>();

        var ids = riskIds.ToList();

        var viaJunction = await db.RiskActions.AsNoTracking()
            .Where(ra => ids.Contains(ra.RiskId) && ra.Action != null && !ra.Action.IsDeleted)
            .Select(ra => new Link(ra.RiskId, ra.ActionId, ra.Action!.Title, ra.Action.Notes))
            .ToListAsync(cancellationToken);

        var viaFk = await db.Actions.AsNoTracking()
            .Where(a => a.RiskId != null && ids.Contains(a.RiskId.Value) && !a.IsDeleted)
            .Select(a => new Link(a.RiskId!.Value, a.Id, a.Title, a.Notes))
            .ToListAsync(cancellationToken);

        return viaJunction.Concat(viaFk)
            .GroupBy(x => (x.RiskId, x.ActionId))
            .Select(g => g.First())
            .ToList();
    }

    public static Task<List<Link>> LoadLinksForRiskAsync(
        CompassDbContext db,
        int riskId,
        CancellationToken cancellationToken) =>
        LoadLinksForRiskIdsAsync(db, new[] { riskId }, cancellationToken);

    public static async Task<Dictionary<int, int>> CountByRiskIdsAsync(
        CompassDbContext db,
        IReadOnlyCollection<int> riskIds,
        CancellationToken cancellationToken)
    {
        var links = await LoadLinksForRiskIdsAsync(db, riskIds, cancellationToken);
        return links
            .GroupBy(x => x.RiskId)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    public static async Task<int> CountForRiskAsync(
        CompassDbContext db,
        int riskId,
        CancellationToken cancellationToken)
    {
        var links = await LoadLinksForRiskAsync(db, riskId, cancellationToken);
        return links.Count;
    }

    public static async Task<List<int>> ActionIdsForRiskAsync(
        CompassDbContext db,
        int riskId,
        CancellationToken cancellationToken)
    {
        var links = await LoadLinksForRiskAsync(db, riskId, cancellationToken);
        return links.Select(x => x.ActionId).ToList();
    }
}
