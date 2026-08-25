using System.Globalization;
using System.Text.RegularExpressions;
using Compass.Data;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services.Raid;

/// <summary>Latest risk comment or mitigation progress line for register spreadsheet display.</summary>
public sealed record RiskLastCommentUpdate(string? PreviewText, string? KindLabel, DateTime? AtUtc);

/// <summary>
/// Builds a unified timeline of risk comments and mitigation progress updates (stored in action notes).
/// </summary>
public static class RiskCommentTimelineBuilder
{
    private const int SpreadsheetPreviewMaxLength = 120;
    private static readonly Regex AuditLineRegex = new(
        @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}) UTC — (.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static int CountMitigationUpdateLines(string? notes) =>
        ParseMitigationUpdateLines(notes).Count;

    public static IReadOnlyList<(DateTime WhenUtc, string Text)> ParseMitigationUpdateLines(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return Array.Empty<(DateTime, string)>();

        var results = new List<(DateTime, string)>();
        foreach (var rawLine in notes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = AuditLineRegex.Match(rawLine);
            if (match.Success
                && DateTime.TryParseExact(
                    match.Groups[1].Value,
                    "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var when))
            {
                results.Add((when, match.Groups[2].Value.Trim()));
                continue;
            }

            // Legacy or truncated notes: treat whole line as text with unknown date.
            if (!string.IsNullOrWhiteSpace(rawLine))
                results.Add((DateTime.MinValue, rawLine));
        }

        return results;
    }

    public static async Task<List<object>> BuildTimelineJsonAsync(
        CompassDbContext db,
        int riskId,
        CancellationToken cancellationToken = default)
    {
        var comments = await db.Comments.AsNoTracking()
            .Include(c => c.CreatedByUser)
            .Where(c => c.EntityType == "Risk" && c.EntityId == riskId && !c.IsDeleted)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.CommentText,
                c.CreatedAt,
                CreatedByUser = c.CreatedByUser != null
                    ? new { c.CreatedByUser.Id, c.CreatedByUser.Name, c.CreatedByUser.Email }
                    : null
            })
            .ToListAsync(cancellationToken);

        var mitigations = await RaidRiskMitigations.LoadLinksForRiskAsync(db, riskId, cancellationToken);

        var items = new List<(DateTime SortAt, object Payload)>();

        foreach (var c in comments)
        {
            items.Add((c.CreatedAt, new
            {
                kind = "comment",
                id = c.Id,
                commentText = c.CommentText,
                createdAt = c.CreatedAt,
                createdByUser = c.CreatedByUser
            }));
        }

        foreach (var m in mitigations)
        {
            var lineIndex = 0;
            foreach (var (when, text) in ParseMitigationUpdateLines(m.Notes))
            {
                var sortAt = when == DateTime.MinValue ? DateTime.MinValue : when;
                items.Add((sortAt, new
                {
                    kind = "mitigation-update",
                    id = $"m-{m.ActionId}-{lineIndex}",
                    commentText = text,
                    createdAt = when == DateTime.MinValue ? (DateTime?)null : when,
                    mitigationId = m.ActionId,
                    mitigationTitle = m.Title
                }));
                lineIndex++;
            }
        }

        return items
            .OrderByDescending(x => x.SortAt)
            .Select(x => x.Payload)
            .ToList();
    }

    public static string? FormatSpreadsheetDisplay(RiskLastCommentUpdate? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.PreviewText))
            return null;

        var text = TruncateForSpreadsheet(item.PreviewText);
        if (item.AtUtc.HasValue)
        {
            var kind = string.IsNullOrWhiteSpace(item.KindLabel) ? "Update" : item.KindLabel;
            return $"{item.AtUtc.Value:dd MMM yy} — {kind}: {text}";
        }

        var labelOnly = string.IsNullOrWhiteSpace(item.KindLabel) ? text : $"{item.KindLabel}: {text}";
        return labelOnly;
    }

    public static async Task<IReadOnlyDictionary<int, RiskLastCommentUpdate>> GetLastCommentUpdateByRiskIdsAsync(
        CompassDbContext db,
        IReadOnlyCollection<int> riskIds,
        CancellationToken cancellationToken = default)
    {
        if (riskIds.Count == 0)
            return new Dictionary<int, RiskLastCommentUpdate>();

        var idList = riskIds.Distinct().ToList();
        var bestByRisk = idList.ToDictionary(id => id, _ => (RiskLastCommentUpdate?)null);

        var comments = await db.Comments.AsNoTracking()
            .Where(c => c.EntityType == "Risk" && idList.Contains(c.EntityId) && !c.IsDeleted)
            .Select(c => new { c.EntityId, c.CommentText, c.CreatedAt })
            .ToListAsync(cancellationToken);

        foreach (var c in comments)
        {
            ConsiderCandidate(bestByRisk, c.EntityId, c.CreatedAt, c.CommentText, "Comment");
        }

        var mitigationNotes = await RaidRiskMitigations.LoadLinksForRiskIdsAsync(
            db, idList, cancellationToken);

        foreach (var m in mitigationNotes)
        {
            foreach (var (when, text) in ParseMitigationUpdateLines(m.Notes))
            {
                if (when == DateTime.MinValue)
                    ConsiderCandidate(bestByRisk, m.RiskId, null, text, "Mitigation update");
                else
                    ConsiderCandidate(bestByRisk, m.RiskId, when, text, "Mitigation update");
            }
        }

        return bestByRisk.ToDictionary(
            kv => kv.Key,
            kv => kv.Value ?? new RiskLastCommentUpdate(null, null, null));
    }

    private static void ConsiderCandidate(
        Dictionary<int, RiskLastCommentUpdate?> bestByRisk,
        int riskId,
        DateTime? atUtc,
        string? text,
        string kindLabel)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var candidate = new RiskLastCommentUpdate(
            text.Trim(),
            kindLabel,
            atUtc);

        if (!bestByRisk.TryGetValue(riskId, out var current) || current == null)
        {
            bestByRisk[riskId] = candidate;
            return;
        }

        var currentSort = current.AtUtc ?? DateTime.MinValue;
        var candidateSort = candidate.AtUtc ?? DateTime.MinValue;
        if (candidateSort >= currentSort)
            bestByRisk[riskId] = candidate;
    }

    private static string TruncateForSpreadsheet(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= SpreadsheetPreviewMaxLength)
            return trimmed;
        return trimmed[..(SpreadsheetPreviewMaxLength - 1)] + "…";
    }

    public static async Task<int> CountTimelineItemsAsync(
        CompassDbContext db,
        int riskId,
        CancellationToken cancellationToken = default)
    {
        var commentCount = await db.Comments.AsNoTracking()
            .CountAsync(c => c.EntityType == "Risk" && c.EntityId == riskId && !c.IsDeleted, cancellationToken);

        var mitigationNotes = await RaidRiskMitigations.LoadLinksForRiskAsync(db, riskId, cancellationToken);

        var mitigationLineCount = mitigationNotes.Sum(m => CountMitigationUpdateLines(m.Notes));
        return commentCount + mitigationLineCount;
    }
}
