using Compass.Data;
using Compass.Models;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services.Raid;

/// <summary>
/// Closed-risk detection for RAID registers. Status-only closures (no <see cref="Risk.ClosedDate"/>)
/// still belong on the closed tab.
/// </summary>
public static class RaidRiskClosure
{
    public static bool LooksClosed(string? code, string? label, string? status) =>
        ContainsClosedToken(code) || ContainsClosedToken(label) || ContainsClosedToken(status);

    public static bool IsClosed(Risk risk) =>
        risk.ClosedDate.HasValue
        || LooksClosed(risk.RiskStatus?.Code, risk.RiskStatus?.Label, risk.Status);

    public static IQueryable<Risk> WhereClosed(IQueryable<Risk> query) =>
        query.Where(r =>
            r.ClosedDate != null
            || (r.RiskStatus != null && (
                r.RiskStatus.Code == "CLOSED"
                || (r.RiskStatus.Label != null && EF.Functions.Like(r.RiskStatus.Label, "%closed%"))))
            || (r.Status != null && EF.Functions.Like(r.Status, "%closed%")));

    public static IQueryable<Risk> WhereOpen(IQueryable<Risk> query) =>
        query.Where(r =>
            r.ClosedDate == null
            && (r.RiskStatus == null || (
                r.RiskStatus.Code != "CLOSED"
                && (r.RiskStatus.Label == null || !EF.Functions.Like(r.RiskStatus.Label, "%closed%"))))
            && (r.Status == null || !EF.Functions.Like(r.Status, "%closed%")));

    /// <summary>
    /// Keep <see cref="Risk.ClosedDate"/> in sync when status is changed from the editor or spreadsheet.
    /// </summary>
    public static void SyncClosedDate(Risk risk, RiskStatus? status, DateTime utcNow)
    {
        if (LooksClosed(status?.Code, status?.Label, risk.Status))
        {
            if (!risk.ClosedDate.HasValue)
                risk.ClosedDate = utcNow;
            return;
        }

        risk.ClosedDate = null;
    }

    public static async Task SyncClosedDateFromStatusIdAsync(
        CompassDbContext db,
        Risk risk,
        CancellationToken cancellationToken)
    {
        RiskStatus? status = null;
        if (risk.RiskStatusId is > 0)
        {
            status = await db.RiskStatuses.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == risk.RiskStatusId.Value, cancellationToken);
            if (status != null && !string.IsNullOrWhiteSpace(status.Label))
                risk.Status = TruncateLower(status.Label, 20);
        }

        SyncClosedDate(risk, status, DateTime.UtcNow);
    }

    private static bool ContainsClosedToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return value.Trim().Contains("closed", StringComparison.OrdinalIgnoreCase);
    }

    private static string TruncateLower(string s, int max)
    {
        var t = s.Trim().ToLowerInvariant();
        return t.Length <= max ? t : t[..max];
    }
}
