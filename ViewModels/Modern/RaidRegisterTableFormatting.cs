using Compass.Models;
using Compass.Models.Modern.Work;
using Compass.Services.Raid;

namespace Compass.ViewModels.Modern;

/// <summary>Shared labels for RAID register list tables (risks / issues).</summary>
public static class RaidRegisterRelationKinds
{
    public const string Organisation = "Organisation";
    public const string Work = "Work";
    public const string Fips = "FIPS";
    public const string Unknown = "Unknown";
}

/// <summary>Maps risks/issues to relation column values (Work / FIPS / Organisation).</summary>
public static class RaidRegisterTableFormatting
{
    public static string? FormatRiskBusinessAreaLabels(Risk r)
    {
        var fromJunction = r.RiskBusinessAreas
            .Where(x => x.BusinessAreaLookup != null)
            .Select(x => x.BusinessAreaLookup!.Name)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        if (fromJunction.Count > 0)
            return string.Join("; ", fromJunction);

        if (r.Project?.BusinessAreaLookup != null)
            return r.Project.BusinessAreaLookup.Name;

        return string.IsNullOrWhiteSpace(r.BusinessArea) ? null : r.BusinessArea;
    }

    public static string? FormatIssueBusinessAreaLabels(Issue i)
    {
        var fromJunction = i.IssueBusinessAreas
            .Where(x => x.BusinessAreaLookup != null)
            .Select(x => x.BusinessAreaLookup!.Name)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        if (fromJunction.Count > 0)
            return string.Join("; ", fromJunction);

        if (i.Project?.BusinessAreaLookup != null)
            return i.Project.BusinessAreaLookup.Name;

        return string.IsNullOrWhiteSpace(i.BusinessArea) ? null : i.BusinessArea;
    }

    public static RaidRegisterRelationParts BuildRiskRelation(Risk r)
    {
        var storedKind = r.RaidAssociationKind;
        var hasProductRow = r.PrimaryProductId.HasValue && r.PrimaryProduct != null;

        if (storedKind == RaidAssociationKinds.Organisation)
            return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Organisation, null, null);

        if (storedKind == RaidAssociationKinds.Product || hasProductRow)
        {
            var label = r.PrimaryProduct != null
                ? (r.PrimaryProduct.DisplayName ?? r.PrimaryProduct.FipsId)
                : (r.FipsId ?? r.ProductDocumentId ??
                   (r.PrimaryProductId.HasValue ? $"Product #{r.PrimaryProductId}" : null));
            return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Fips, null, label);
        }

        if (storedKind == RaidAssociationKinds.WorkItem || r.ProjectId.HasValue)
            return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Work, r.ProjectId, r.Project?.Title);

        if (!string.IsNullOrEmpty(r.Project?.Title))
            return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Work, r.ProjectId, r.Project.Title);

        return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Unknown, null, null);
    }

    public static RaidRegisterRelationParts BuildIssueRelation(Issue i)
    {
        var storedKind = i.RaidAssociationKind;
        var hasProductRow = i.PrimaryProductId.HasValue && i.PrimaryProduct != null;

        if (storedKind == RaidAssociationKinds.Organisation)
            return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Organisation, null, null);

        if (storedKind == RaidAssociationKinds.Product || hasProductRow)
        {
            var label = i.PrimaryProduct != null
                ? (i.PrimaryProduct.DisplayName ?? i.PrimaryProduct.FipsId)
                : (i.FipsId ?? i.ProductDocumentId ??
                   (i.PrimaryProductId.HasValue ? $"Product #{i.PrimaryProductId}" : null));
            return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Fips, null, label);
        }

        if (storedKind == RaidAssociationKinds.WorkItem || i.ProjectId.HasValue)
            return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Work, i.ProjectId, i.Project?.Title);

        if (!string.IsNullOrEmpty(i.Project?.Title))
            return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Work, i.ProjectId, i.Project.Title);

        return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Unknown, null, null);
    }

    public static string RiskScoreBandClass(decimal? score)
    {
        if (!score.HasValue) return string.Empty;
        var s = score.Value;
        if (s >= 20) return "raid-ss-score-badge--highest";
        if (s >= 15) return "raid-ss-score-badge--elevated";
        if (s >= 8) return "raid-ss-score-badge--medium";
        return "raid-ss-score-badge--lower";
    }

    public static string RiskRefScoreIndicatorClass(decimal? currentScore) =>
        RiskScoreLeftBorderClass(currentScore);

    /// <summary>Left-edge colour for a 0–25 risk score (ref column and each rating group).</summary>
    public static string RiskScoreLeftBorderClass(decimal? score)
    {
        if (!score.HasValue) return string.Empty;
        var s = score.Value;
        if (s >= 20) return "raid-ss-score-edge--highest";
        if (s >= 15) return "raid-ss-score-edge--elevated";
        if (s >= 8) return "raid-ss-score-edge--medium";
        return "raid-ss-score-edge--lower";
    }

    public static string SpreadsheetBadgeLabel(string? label, bool uppercase = false)
    {
        if (string.IsNullOrWhiteSpace(label)) return "—";
        return uppercase ? label.ToUpperInvariant() : label;
    }

    public static string FormatLikelihoodImpactPair(string? likelihood, string? impact)
    {
        var lik = string.IsNullOrWhiteSpace(likelihood) ? null : likelihood.Trim();
        var imp = string.IsNullOrWhiteSpace(impact) ? null : impact.Trim();
        if (lik == null && imp == null) return "—";
        return $"{lik ?? "—"} × {imp ?? "—"}";
    }

    public static RisksByTierRiskRow ToRiskInfoModalPayload(
        RaidRegisterRiskRow r,
        string? directorateName,
        IReadOnlyList<SelectOption>? likelihoodScale = null,
        IReadOnlyList<SelectOption>? impactScale = null)
    {
        var workLabel = r.RelationKind switch
        {
            RaidRegisterRelationKinds.Organisation => "Organisation",
            RaidRegisterRelationKinds.Work => r.RelationTarget ?? "—",
            RaidRegisterRelationKinds.Fips => r.RelationTarget ?? "Product",
            _ => r.RelationTarget ?? "—"
        };
        string? workUrl = r.RelationKind == RaidRegisterRelationKinds.Work && r.RelationProjectId is int pid
            ? $"/modern/work/detail/{pid}"
            : r.RelationLinkHref;

        static int ScaleIndex(IReadOnlyList<SelectOption>? options, int? id, string? label)
        {
            if (options == null || options.Count == 0) return 0;
            if (id.HasValue)
            {
                for (var i = 0; i < options.Count; i++)
                {
                    if (options[i].Id == id.Value) return i + 1;
                }
            }
            if (!string.IsNullOrWhiteSpace(label))
            {
                for (var i = 0; i < options.Count; i++)
                {
                    if (string.Equals(options[i].Name, label.Trim(), StringComparison.OrdinalIgnoreCase))
                        return i + 1;
                }
            }
            return 0;
        }

        RisksByTierRiskRatingBand Band(string? likelihood, string? impact, decimal? score, int? likelihoodId, int? impactId) => new()
        {
            Score = score,
            LikelihoodLabel = string.IsNullOrWhiteSpace(likelihood) ? "—" : likelihood.Trim(),
            ImpactLabel = string.IsNullOrWhiteSpace(impact) ? "—" : impact.Trim(),
            LikelihoodIndex = ScaleIndex(likelihoodScale, likelihoodId, likelihood),
            ImpactIndex = ScaleIndex(impactScale, impactId, impact)
        };

        var inherent = Band(r.OriginalLikelihood, r.OriginalImpact, r.InherentScore, r.OriginalLikelihoodId, r.OriginalImpactId);
        var current = Band(r.CurrentLikelihood, r.CurrentImpact, r.CurrentScore, r.CurrentLikelihoodId, r.CurrentImpactId);
        var residual = Band(r.ResidualLikelihood, r.ResidualImpact, r.ResidualScore, r.ResidualLikelihoodId, r.ResidualImpactId);
        var daysSince = Math.Max(0, (DateTime.UtcNow.Date - r.UpdatedAt.Date).Days);

        return new RisksByTierRiskRow
        {
            Id = r.Id,
            Reference = r.Reference,
            Title = r.Title,
            DetailUrl = $"/modern/raid/risks/{r.Id}",
            IsClosed = r.ClosedDate.HasValue || RaidRiskClosure.LooksClosed(null, r.Status, r.Status),
            InherentScore = r.InherentScore,
            CurrentScore = r.CurrentScore,
            ResidualScore = r.ResidualScore,
            Inherent = inherent,
            Current = current,
            Residual = residual,
            InherentLikelihoodImpact = FormatLikelihoodImpactPair(r.OriginalLikelihood, r.OriginalImpact),
            CurrentLikelihoodImpact = FormatLikelihoodImpactPair(r.CurrentLikelihood, r.CurrentImpact),
            ResidualLikelihoodImpact = FormatLikelihoodImpactPair(r.ResidualLikelihood, r.ResidualImpact),
            Mitigation = r.ResponseStrategy ?? r.Response,
            MitigationFull = r.ResponseStrategy ?? r.Response,
            Description = r.Description,
            Status = r.Status ?? "—",
            TierName = string.IsNullOrWhiteSpace(r.Tier) ? "Not set" : r.Tier,
            Owner = r.Owner ?? "—",
            LastReviewedAt = r.LastReviewDate,
            LastUpdatedAt = r.UpdatedAt,
            CreatedAt = r.CreatedAt,
            DaysSinceLastUpdate = daysSince,
            WorkItemOrProject = workLabel,
            WorkItemUrl = workUrl,
            Directorate = string.IsNullOrWhiteSpace(directorateName) ? "—" : directorateName
        };
    }

    public static RaidRegisterRelationParts BuildAssumptionRelation(Assumption a)
    {
        var storedKind = a.RaidAssociationKind;
        var hasProductRow = a.PrimaryProductId.HasValue && a.PrimaryProduct != null;

        if (storedKind == RaidAssociationKinds.Organisation)
            return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Organisation, null, null);

        if (storedKind == RaidAssociationKinds.Product || hasProductRow)
        {
            var label = a.PrimaryProduct != null
                ? (a.PrimaryProduct.DisplayName ?? a.PrimaryProduct.FipsId)
                : (a.PrimaryProductId.HasValue ? $"Product #{a.PrimaryProductId}" : null);
            return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Fips, null, label);
        }

        if (storedKind == RaidAssociationKinds.WorkItem || a.ProjectId.HasValue)
            return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Work, a.ProjectId, a.Project?.Title);

        if (!string.IsNullOrEmpty(a.Project?.Title))
            return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Work, a.ProjectId, a.Project.Title);

        return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Unknown, null, null);
    }

    public static RaidRegisterRelationParts BuildNearMissRelation(NearMiss nm)
    {
        var labels = new List<string>();
        if (nm.DirectorateLookup != null && !string.IsNullOrWhiteSpace(nm.DirectorateLookup.Name))
            labels.Add(nm.DirectorateLookup.Name);
        if (nm.BusinessAreaLookup != null && !string.IsNullOrWhiteSpace(nm.BusinessAreaLookup.Name))
            labels.Add(nm.BusinessAreaLookup.Name);

        var target = labels.Count > 0 ? string.Join(" · ", labels) : null;
        return new RaidRegisterRelationParts(RaidRegisterRelationKinds.Organisation, null, target);
    }

    /// <summary>Operational Tier 2/1 (post–Operations review) are read-only on the register spreadsheet.</summary>
    public static bool IsSpreadsheetRiskTierEditable(int? tierId, IReadOnlyList<SelectOption> spreadsheetTierOptions)
    {
        if (!tierId.HasValue)
            return true;
        return spreadsheetTierOptions.Any(o => o.Id == tierId.Value);
    }
}

public readonly record struct RaidRegisterRelationParts(
    string Kind,
    int? ProjectId,
    string? Target,
    string? WorkDetailSection = null,
    string? SourceLabel = null,
    string? RelatedTitle = null,
    string? RelatedDescription = null,
    string? LinkHref = null,
    /// <summary>UI radio value: work, product, organisation.</summary>
    string? AssociationUiKind = null,
    int? PrimaryProductId = null);
