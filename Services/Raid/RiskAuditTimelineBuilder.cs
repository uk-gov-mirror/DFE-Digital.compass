using System.Linq;
using System.Text.Json;
using Compass.Data;
using Compass.Models;
using Compass.ViewModels.Modern;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services.Raid;

/// <summary>Builds human-readable timeline rows from <see cref="AuditLog"/> entries for a risk and its mitigation actions.</summary>
public static class RiskAuditTimelineBuilder
{
    public static async Task<IReadOnlyList<RiskAuditTimelineItemVm>> BuildAsync(
        CompassDbContext db,
        int riskId,
        CancellationToken cancellationToken = default)
    {
        var riskIdStr = riskId.ToString();

        var riskLogs = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Entity == nameof(Risk) && a.EntityId == riskIdStr)
            .OrderByDescending(a => a.ChangedUtc)
            .Take(120)
            .ToListAsync(cancellationToken);

        var mitigationActionIds = await RaidRiskMitigations.ActionIdsForRiskAsync(
            db, riskId, cancellationToken);

        var idStrSet = mitigationActionIds.Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal);
        List<AuditLog> actionLogs = new();
        if (idStrSet.Count > 0)
        {
            actionLogs = await db.AuditLogs.AsNoTracking()
                .Where(a => a.Entity == nameof(Compass.Models.Action) && idStrSet.Contains(a.EntityId))
                .OrderByDescending(a => a.ChangedUtc)
                .Take(120)
                .ToListAsync(cancellationToken);
        }

        var lk = await LoadLookupsAsync(db, cancellationToken);

        var items = new List<RiskAuditTimelineItemVm>();

        foreach (var log in riskLogs)
        {
            var row = MapRiskLog(log, lk);
            if (row != null)
                items.Add(row);
        }

        foreach (var log in actionLogs)
        {
            var row = MapActionLog(log, lk);
            if (row != null)
                items.Add(row);
        }

        items.AddRange(await LoadProgressUpdateItemsAsync(db, riskId, cancellationToken));

        return items
            .DistinctBy(i => $"{i.WhenUtc:o}|{i.What}|{i.Detail}")
            .OrderByDescending(i => i.WhenUtc)
            .Take(80)
            .ToList();
    }

    private static async Task<IReadOnlyList<RiskAuditTimelineItemVm>> LoadProgressUpdateItemsAsync(
        CompassDbContext db,
        int riskId,
        CancellationToken cancellationToken)
    {
        var items = new List<RiskAuditTimelineItemVm>();

        var comments = await db.Comments.AsNoTracking()
            .Include(c => c.CreatedByUser)
            .Where(c => c.EntityType == "Risk" && c.EntityId == riskId && !c.IsDeleted)
            .OrderByDescending(c => c.CreatedAt)
            .Take(120)
            .ToListAsync(cancellationToken);

        foreach (var comment in comments)
        {
            items.Add(new RiskAuditTimelineItemVm
            {
                WhenUtc = comment.CreatedAt,
                ActorDisplay = CommentAuthor(comment.CreatedByUser),
                What = "Progress update",
                Detail = comment.CommentText,
                IsAlert = false
            });
        }

        var mitigations = await RaidRiskMitigations.LoadLinksForRiskAsync(db, riskId, cancellationToken);

        foreach (var mitigation in mitigations)
        {
            foreach (var (when, text) in RiskCommentTimelineBuilder.ParseMitigationUpdateLines(mitigation.Notes))
            {
                if (string.IsNullOrWhiteSpace(text) || when == DateTime.MinValue)
                    continue;

                items.Add(new RiskAuditTimelineItemVm
                {
                    WhenUtc = when == DateTime.MinValue ? DateTime.MinValue : when,
                    ActorDisplay = "Mitigation action",
                    What = "Mitigation progress update",
                    Detail = string.IsNullOrWhiteSpace(mitigation.Title)
                        ? text.Trim()
                        : $"{mitigation.Title}: {text.Trim()}",
                    IsAlert = false
                });
            }
        }

        return items;
    }

    private static string CommentAuthor(User? user)
    {
        if (user == null)
            return "Unknown user";
        if (!string.IsNullOrWhiteSpace(user.Name))
            return user.Name.Trim();
        if (!string.IsNullOrWhiteSpace(user.Email))
            return user.Email.Trim();
        return "Unknown user";
    }

    /// <summary>Single plain-English paragraph summarising risk field changes since a point in time (for monthly review).</summary>
    public static async Task<string?> BuildPlainEnglishSummarySinceAsync(
        CompassDbContext db,
        int riskId,
        DateTime sinceUtc,
        CancellationToken cancellationToken = default)
    {
        var riskIdStr = riskId.ToString();
        var logs = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Entity == nameof(Risk) && a.EntityId == riskIdStr && a.Action == "Update" && a.ChangedUtc > sinceUtc)
            .OrderBy(a => a.ChangedUtc)
            .Take(40)
            .ToListAsync(cancellationToken);

        if (logs.Count == 0)
            return null;

        var lk = await LoadLookupsAsync(db, cancellationToken);
        var lines = new List<ChangeLine>();
        foreach (var log in logs)
        {
            var before = ParseJsonDict(log.BeforeJson);
            var after = ParseJsonDict(log.AfterJson);
            lines.AddRange(DiffRisk(before, after, lk));
        }

        lines = lines
            .DistinctBy(l => l.Text, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ConsolidateChangeLinesToParagraph(lines);
    }

    private static string? ConsolidateChangeLinesToParagraph(List<ChangeLine> lines)
    {
        if (lines.Count == 0)
            return null;

        var sentences = new List<string>();

        var scoreLine = lines.FirstOrDefault(l =>
            l.Text.Contains("Risk score", StringComparison.OrdinalIgnoreCase));
        if (scoreLine != null)
            sentences.Add(PlainScoreChange(scoreLine.Text));

        var likelihoodLine = lines.FirstOrDefault(l =>
            l.Text.StartsWith("Likelihood:", StringComparison.OrdinalIgnoreCase));
        var impactLine = lines.FirstOrDefault(l =>
            l.Text.StartsWith("Impact level:", StringComparison.OrdinalIgnoreCase));
        if (likelihoodLine != null || impactLine != null)
        {
            if (likelihoodLine != null && impactLine != null)
                sentences.Add($"{PlainArrowChange("Likelihood", likelihoodLine.Text)} {PlainArrowChange("Impact", impactLine.Text)}".Trim());
            else if (likelihoodLine != null)
                sentences.Add(PlainArrowChange("Likelihood", likelihoodLine.Text));
            else
                sentences.Add(PlainArrowChange("Impact", impactLine!.Text));
        }

        var statusLine = lines.FirstOrDefault(l =>
            l.Text.StartsWith("Status:", StringComparison.OrdinalIgnoreCase));
        if (statusLine != null)
            sentences.Add($"Status was set to {ExtractArrowTo(statusLine.Text)}.");

        var tierLine = lines.FirstOrDefault(l =>
            l.Text.StartsWith("Tier:", StringComparison.OrdinalIgnoreCase));
        if (tierLine != null)
            sentences.Add($"Governance tier was changed ({ExtractArrowFrom(tierLine.Text)}).");

        var proximityLine = lines.FirstOrDefault(l =>
            l.Text.StartsWith("Proximity:", StringComparison.OrdinalIgnoreCase));
        if (proximityLine != null)
            sentences.Add($"How soon this might happen was updated ({ExtractArrowFrom(proximityLine.Text)}).");

        var priorityLine = lines.FirstOrDefault(l =>
            l.Text.StartsWith("Priority:", StringComparison.OrdinalIgnoreCase));
        if (priorityLine != null)
            sentences.Add($"Priority was changed ({ExtractArrowFrom(priorityLine.Text)}).");

        if (lines.Any(l => l.Text.Contains("Ownership", StringComparison.OrdinalIgnoreCase)))
            sentences.Add("The owner or senior responsible officer was changed.");

        if (lines.Any(l => l.Text.Contains("Association", StringComparison.OrdinalIgnoreCase)))
            sentences.Add("Which work item or product this is linked to was changed.");

        if (lines.Any(l => l.Text.Contains("Identified date", StringComparison.OrdinalIgnoreCase)
                         || l.Text.Contains("Next review date", StringComparison.OrdinalIgnoreCase)))
            sentences.Add("Review or identification dates were updated.");

        if (lines.Any(l => l.Text.Contains("Description", StringComparison.OrdinalIgnoreCase)
                         || l.Text.Contains("Title", StringComparison.OrdinalIgnoreCase)
                         || l.Text.Contains("Cause", StringComparison.OrdinalIgnoreCase)
                         || l.Text.Contains("Impact if realised", StringComparison.OrdinalIgnoreCase)))
            sentences.Add("The written description or background detail was updated.");

        if (lines.Any(l => l.Text.Contains("Mitigation", StringComparison.OrdinalIgnoreCase)
                         || l.Text.Contains("response", StringComparison.OrdinalIgnoreCase)))
            sentences.Add("The planned response or mitigation was updated.");

        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sentences) covered.Add(s);

        var remaining = lines.Count(l => !IsLineCoveredBySentences(l, sentences));
        if (remaining > 0 && sentences.Count < 4)
            sentences.Add(remaining == 1
                ? "One other detail on the record was updated."
                : $"{remaining} other details on the record were updated.");

        if (sentences.Count == 0)
            return "The record was updated, but no major rating or status fields changed.";

        return string.Join(" ", sentences.Take(5));
    }

    private static bool IsLineCoveredBySentences(ChangeLine line, List<string> sentences)
    {
        var t = line.Text;
        if (t.Contains("Risk score", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("Likelihood:", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("Impact level:", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("Status:", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("Tier:", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("Proximity:", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("Priority:", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("Ownership", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("Association", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("Identified date", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("Next review date", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("Description", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("Title", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("Mitigation", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string PlainScoreChange(string text)
    {
        var arrow = ExtractArrowFrom(text);
        if (string.IsNullOrEmpty(arrow))
            return "The overall risk score was updated.";
        var parts = arrow.Split('→', StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var from) && int.TryParse(parts[1], out var to))
        {
            if (to < from)
                return $"The overall risk score was reduced from {from} to {to}.";
            if (to > from)
                return $"The overall risk score was increased from {from} to {to}.";
            return $"The overall risk score stayed at {to}.";
        }
        return $"The overall risk score was changed ({arrow}).";
    }

    private static string ExtractArrowFrom(string text)
    {
        var idx = text.IndexOf(':');
        return idx >= 0 ? text[(idx + 1)..].Trim() : text;
    }

    private static string ExtractArrowTo(string text)
    {
        var arrow = ExtractArrowFrom(text);
        var parts = arrow.Split('→', StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? parts[1] : arrow;
    }

    private static string PlainArrowChange(string label, string lineText)
    {
        var arrow = ExtractArrowFrom(lineText);
        var parts = arrow.Split('→', StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
            return $"{label} went from {parts[0]} to {parts[1]}.";
        return $"{label} was updated.";
    }

    private sealed record Lookups(
        IReadOnlyDictionary<int, string> Likelihood,
        IReadOnlyDictionary<int, string> Impact,
        IReadOnlyDictionary<int, string> Status,
        IReadOnlyDictionary<int, string> Tier,
        IReadOnlyDictionary<int, string> Proximity,
        IReadOnlyDictionary<int, string> Priority);

    private static async Task<Lookups> LoadLookupsAsync(CompassDbContext db, CancellationToken ct)
    {
        var lik = await db.RiskLikelihoods.AsNoTracking().Where(x => x.IsActive)
            .ToDictionaryAsync(x => x.Id, x => x.Label, ct);
        var imp = await db.RiskImpactLevels.AsNoTracking().Where(x => x.IsActive)
            .ToDictionaryAsync(x => x.Id, x => x.Label, ct);
        var st = await db.RiskStatuses.AsNoTracking().Where(x => x.IsActive)
            .ToDictionaryAsync(x => x.Id, x => x.Label, ct);
        var tier = await db.RiskTiers.AsNoTracking().Where(x => x.IsActive && !x.IsProposedTier)
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var px = await db.RiskProximities.AsNoTracking().Where(x => x.IsActive)
            .ToDictionaryAsync(x => x.Id, x => x.Label, ct);
        var pr = await db.RiskPriorities.AsNoTracking().Where(x => x.IsActive)
            .ToDictionaryAsync(x => x.Id, x => x.Label, ct);

        return new Lookups(lik, imp, st, tier, px, pr);
    }

    private static RiskAuditTimelineItemVm? MapRiskLog(AuditLog log, Lookups lk)
    {
        var actor = Actor(log);
        var when = log.ChangedUtc;

        return log.Action switch
        {
            "Create" => new RiskAuditTimelineItemVm
            {
                WhenUtc = when,
                ActorDisplay = actor,
                What = "Risk registered in RAID",
                Detail = SummarizeCreate(log.AfterJson),
                IsAlert = false
            },
            "Update" => MapRiskUpdate(log, lk, actor, when),
            "Delete" => new RiskAuditTimelineItemVm
            {
                WhenUtc = when,
                ActorDisplay = actor,
                What = "Risk removed from register",
                Detail = null,
                IsAlert = true
            },
            _ => null
        };
    }

    private static RiskAuditTimelineItemVm? MapRiskUpdate(AuditLog log, Lookups lk, string actor, DateTime when)
    {
        var before = ParseJsonDict(log.BeforeJson);
        var after = ParseJsonDict(log.AfterJson);
        if (before.Count == 0 && after.Count == 0)
            return null;

        var changes = DiffRisk(before, after, lk);
        if (changes.Count == 0)
            return null;

        var headline = BuildHeadline(changes, out var alert);
        var detailParts = changes.Where(c => !c.UseInHeadline || changes.Count > 1).Select(c => c.Text).Distinct().ToList();
        var detail = string.Join(" · ", detailParts.Where(t => !string.Equals(t, headline, StringComparison.Ordinal)));

        return new RiskAuditTimelineItemVm
        {
            WhenUtc = when,
            ActorDisplay = actor,
            What = headline,
            Detail = detail.Length > 0 ? detail : null,
            IsAlert = alert
        };
    }

    private sealed record ChangeLine(string Text, bool UseInHeadline);

    private static string BuildHeadline(List<ChangeLine> changes, out bool alert)
    {
        alert = changes.Any(c => c.Text.Contains("escalat", StringComparison.OrdinalIgnoreCase)
                                 || (c.Text.Contains("score", StringComparison.OrdinalIgnoreCase) && c.Text.Contains('→')));
        var rating = changes.FirstOrDefault(c => c.UseInHeadline);
        if (rating != null)
            return rating.Text;
        return changes[0].Text + (changes.Count > 1 ? $" (+{changes.Count - 1} more)" : "");
    }

    private static List<ChangeLine> DiffRisk(Dictionary<string, JsonElement> before, Dictionary<string, JsonElement> after, Lookups lk)
    {
        var keys = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var lines = new List<ChangeLine>();

        foreach (var key in keys)
        {
            if (string.Equals(key, "UpdatedAt", StringComparison.OrdinalIgnoreCase))
                continue;
            if (key is "ImpactRating" or "LikelihoodRating" or "InherentScore")
                continue;

            before.TryGetValue(key, out var b);
            after.TryGetValue(key, out var a);

            var bs = ElementToString(b);
            var ass = ElementToString(a);
            if (bs == ass)
                continue;

            var line = DescribeRiskField(key, b, a, lk);
            if (line != null)
                lines.Add(line);
        }

        return lines;
    }

    private static ChangeLine? DescribeRiskField(string key, JsonElement? before, JsonElement? after, Lookups lk)
    {
        string Fk(IReadOnlyDictionary<int, string> map, JsonElement? el)
        {
            if (el == null || el.Value.ValueKind == JsonValueKind.Null) return "—";
            if (el.Value.ValueKind == JsonValueKind.Number && el.Value.TryGetInt32(out var id))
                return map.TryGetValue(id, out var lab) ? lab : $"#{id}";
            return ElementToString(el);
        }

        return key switch
        {
            "Title" => new ChangeLine(
                string.IsNullOrEmpty(ElementToString(after))
                    ? "Title cleared"
                    : $"Title updated to \"{TrimShort(ElementToString(after), 80)}\"",
                false),
            "Description" => new ChangeLine("Description updated", false),
            "ResponseStrategy" => new ChangeLine("Mitigation / response text updated", false),
            "RiskLikelihoodId" => new ChangeLine(
                $"Likelihood: {Fk(lk.Likelihood, before)} → {Fk(lk.Likelihood, after)}", true),
            "RiskImpactLevelId" => new ChangeLine(
                $"Impact level: {Fk(lk.Impact, before)} → {Fk(lk.Impact, after)}", true),
            "RiskScore" => new ChangeLine(
                $"Risk score: {ElementToString(before)} → {ElementToString(after)}", true),
            "RiskStatusId" => new ChangeLine(
                $"Status: {Fk(lk.Status, before)} → {Fk(lk.Status, after)}", true),
            "RiskTierId" => new ChangeLine(
                $"Tier: {Fk(lk.Tier, before)} → {Fk(lk.Tier, after)}", true),
            "RiskProximityId" => new ChangeLine(
                $"Proximity: {Fk(lk.Proximity, before)} → {Fk(lk.Proximity, after)}", true),
            "RiskPriorityId" => new ChangeLine(
                $"Priority: {Fk(lk.Priority, before)} → {Fk(lk.Priority, after)}", false),
            "ProjectId" or "PrimaryProductId" or "RaidAssociationKind" => new ChangeLine("Association (work item / product) changed", false),
            "OwnerUserId" or "SroUserId" => new ChangeLine("Ownership changed", false),
            "IdentifiedDate" or "NextReviewDate" => new ChangeLine($"{Friendly(key)} changed", false),
            "Notes" or "HowIdentified" => new ChangeLine($"{Friendly(key)} updated", false),
            "Cause" => new ChangeLine("Cause updated", false),
            "ImpactIfRealised" => new ChangeLine("Impact if realised updated", false),
            "Contingency" => new ChangeLine("Contingency updated", false),
            "Assurance" => new ChangeLine("Assurance updated", false),
            "FinancialImpact" => new ChangeLine("Financial impact updated", false),
            _ => new ChangeLine($"{Friendly(key)} updated", false)
        };
    }

    private static string Friendly(string key) => key switch
    {
        "IdentifiedDate" => "Identified date",
        "NextReviewDate" => "Next review date",
        _ => key
    };

    private static RiskAuditTimelineItemVm? MapActionLog(AuditLog log, Lookups lk)
    {
        var actor = Actor(log);
        var when = log.ChangedUtc;

        return log.Action switch
        {
            "Create" => new RiskAuditTimelineItemVm
            {
                WhenUtc = when,
                ActorDisplay = actor,
                What = "Mitigation action added",
                Detail = SummarizeMitigationCreate(log.AfterJson),
                IsAlert = false
            },
            "Update" => MapActionUpdate(log, actor, when),
            _ => null
        };
    }

    private static RiskAuditTimelineItemVm? MapActionUpdate(AuditLog log, string actor, DateTime when)
    {
        var before = ParseJsonDict(log.BeforeJson);
        var after = ParseJsonDict(log.AfterJson);
        var parts = new List<string>();
        foreach (var key in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(key, "UpdatedAt", StringComparison.Ordinal))
                continue;
            before.TryGetValue(key, out var b);
            after.TryGetValue(key, out var a);
            if (ElementToString(b) == ElementToString(a))
                continue;
            switch (key)
            {
                case "Title":
                    parts.Add($"Action text updated");
                    break;
                case "Status":
                    parts.Add($"Status: {ElementToString(b)} → {ElementToString(a)}");
                    break;
                case "DueDate":
                    parts.Add($"Target date changed");
                    break;
                case "AssignedToEmail":
                    parts.Add("Owner updated");
                    break;
                case "Notes":
                    parts.Add("Notes / progress updated");
                    break;
                default:
                    parts.Add($"{key} updated");
                    break;
            }
        }

        if (parts.Count == 0)
            return null;

        return new RiskAuditTimelineItemVm
        {
            WhenUtc = when,
            ActorDisplay = actor,
            What = "Mitigation action updated",
            Detail = string.Join(" · ", parts.Distinct()),
            IsAlert = parts.Any(p => p.Contains("Overdue", StringComparison.OrdinalIgnoreCase)
                                     || p.Contains("Blocked", StringComparison.OrdinalIgnoreCase))
        };
    }

    private static string? SummarizeMitigationCreate(string? afterJson)
    {
        var d = ParseJsonDict(afterJson);
        if (!d.TryGetValue("Title", out var t))
            return null;
        return TrimShort(ElementToString(t), 160);
    }

    private static string? SummarizeCreate(string? afterJson)
    {
        var d = ParseJsonDict(afterJson);
        if (d.TryGetValue("Title", out var t))
            return TrimShort(ElementToString(t), 120);
        return null;
    }

    private static string Actor(AuditLog log) =>
        !string.IsNullOrWhiteSpace(log.ChangedBy)
            ? log.ChangedBy!.Trim()
            : (!string.IsNullOrWhiteSpace(log.ChangedByEmail) ? log.ChangedByEmail!.Trim() : "System");

    private static Dictionary<string, JsonElement> ParseJsonDict(string? json)
    {
        var d = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return d;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return d;
            foreach (var p in doc.RootElement.EnumerateObject())
                d[p.Name] = p.Value.Clone();
        }
        catch
        {
            /* ignore */
        }

        return d;
    }

    private static string ElementToString(JsonElement? el)
    {
        if (el == null) return "";
        var e = el.Value;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.Number => e.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => e.ToString()
        };
    }

    private static string TrimShort(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }
}
