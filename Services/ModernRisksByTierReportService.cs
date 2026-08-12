using Compass.Data;
using Compass.Models;
using Compass.Services.Raid;
using Compass.ViewModels.Modern;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services;

/// <summary>Builds the Risks by tier reporting dashboard for risk-team oversight.</summary>
public class ModernRisksByTierReportService
{
    private const int StaleDaysThreshold = 30;
    private const int MitigationPreviewMaxLength = 160;

    private static readonly string[] DefaultLikelihoodLabels =
        ["Very unlikely", "Unlikely", "Possible", "Likely", "Very likely"];

    private static readonly string[] DefaultImpactLabels =
        ["Negligible", "Marginal", "Moderate", "Critical", "Crisis"];

    private readonly CompassDbContext _db;

    public ModernRisksByTierReportService(CompassDbContext db) => _db = db;

    public async Task<ModernRisksByTierReportViewModel> BuildAsync(
        int? directorateId = null,
        bool includeClosed = false,
        CancellationToken cancellationToken = default)
    {
        var today = DateTime.UtcNow.Date;

        var directorates = await _db.Divisions.AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .Select(d => new SelectOption(d.Id, d.Name))
            .ToListAsync(cancellationToken);

        var tiers = await _db.RiskTiers.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);

        var query = _db.Risks.AsNoTracking()
            .Include(r => r.RiskTier)
            .Include(r => r.Project)
                .ThenInclude(p => p!.Directorates)
                    .ThenInclude(pd => pd.Division)
            .Include(r => r.PrimaryProduct)
            .Include(r => r.RiskDivisions)
                .ThenInclude(rd => rd.Division)
            .Include(r => r.Likelihood)
            .Include(r => r.ImpactLevel)
            .Include(r => r.CurrentLikelihood)
            .Include(r => r.CurrentImpactLevel)
            .Include(r => r.ResidualLikelihoodLevel)
            .Include(r => r.ResidualImpactLevel)
            .Include(r => r.RiskStatus)
            .Include(r => r.OwnerUser)
            .Where(r => !r.IsDeleted);

        if (!includeClosed)
            query = query.Where(r => r.ClosedDate == null);

        if (directorateId is { } dir)
        {
            query = query.Where(r =>
                r.RiskDivisions.Any(d => d.DivisionId == dir) ||
                (r.ProjectId != null && r.Project!.Directorates.Any(pd => pd.DivisionId == dir)));
        }

        var risks = await query
            .OrderByDescending(r => r.CurrentScore ?? r.InherentScore ?? (decimal?)r.RiskScore)
            .ThenBy(r => r.Title)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        var likelihoodLookups = await _db.RiskLikelihoods.AsNoTracking()
            .Where(l => l.IsActive)
            .OrderBy(l => l.MatrixScore)
            .ThenBy(l => l.SortOrder)
            .ThenBy(l => l.Id)
            .ToListAsync(cancellationToken);

        var impactLookups = await _db.RiskImpactLevels.AsNoTracking()
            .Where(l => l.IsActive)
            .OrderBy(l => l.MatrixScore)
            .ThenBy(l => l.SortOrder)
            .ThenBy(l => l.Id)
            .ToListAsync(cancellationToken);

        var scale = BuildRatingScaleContext(likelihoodLookups, impactLookups);

        var pendingTierReqs = await _db.RaidEscalationTierChangeRequests.AsNoTracking()
            .Where(x => x.RecordType == "risk" && x.RiskId != null && x.Status == "pending")
            .Include(x => x.FromRiskTier)
            .Include(x => x.ToRiskTier)
            .OrderByDescending(x => x.SubmittedAt)
            .ToListAsync(cancellationToken);

        var latestPendingByRiskId = pendingTierReqs
            .GroupBy(x => x.RiskId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var approvedTierReqs = await _db.RaidEscalationTierChangeRequests.AsNoTracking()
            .Where(x => x.RecordType == "risk" && x.RiskId != null && x.Status == "approved")
            .Include(x => x.FromRiskTier)
            .Include(x => x.ToRiskTier)
            .OrderByDescending(x => x.DecidedAt ?? x.SubmittedAt)
            .ToListAsync(cancellationToken);

        var latestApprovedByRiskId = approvedTierReqs
            .GroupBy(x => x.RiskId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var buildContext = new RisksByTierBuildContext
        {
            Scale = scale,
            ActiveTiers = tiers,
            LatestPendingByRiskId = latestPendingByRiskId,
            LatestApprovedByRiskId = latestApprovedByRiskId
        };

        var paired = risks.Select(r => (Entity: r, Row: MapRow(r, today, buildContext))).ToList();
        var rows = paired.Select(p => p.Row).ToList();

        var openCount = risks.Count(r => r.ClosedDate == null);
        var closedCount = risks.Count - openCount;
        var untiered = risks.Count(r => r.RiskTierId == null);
        var stale = rows.Count(r => !r.IsClosed && r.DaysSinceLastUpdate >= StaleDaysThreshold);

        var usedTier = paired.Select(p => p.Entity.RiskTierId).ToHashSet();
        var matrixTiers = tiers
            .Where(t => !t.IsProposedTier || usedTier.Contains(t.Id))
            .ToList();

        var matrixColumns = matrixTiers
            .Select(t => new RisksByTierColumn
            {
                TierId = t.Id,
                Name = t.Name,
                IsProposedTier = t.IsProposedTier
            })
            .Append(new RisksByTierColumn { TierId = null, Name = "Not set", IsProposedTier = false })
            .ToList();

        var directorateMatrix = BuildDirectorateMatrix(paired, matrixTiers, directorates);
        var matrixTotalRow = BuildMatrixTotalRow(directorateMatrix, matrixColumns.Count);
        var drillRisks = BuildDrillRisksByKey(paired);

        return new ModernRisksByTierReportViewModel
        {
            FilterDirectorateId = directorateId,
            IncludeClosed = includeClosed,
            Directorates = directorates,
            OpenRiskCount = openCount,
            ClosedRiskCount = closedCount,
            TotalRiskCount = risks.Count,
            UntieredRiskCount = untiered,
            StaleRiskCount = stale,
            TierColumns = matrixColumns,
            DirectorateMatrix = directorateMatrix,
            MatrixTotalRow = matrixTotalRow,
            DrillRisksByKey = drillRisks,
            TierGroups = BuildTierGroups(tiers, paired),
            LikelihoodScaleLabels = scale.LikelihoodLabels,
            ImpactScaleLabels = scale.ImpactLabels
        };
    }

    private sealed class RiskRatingScaleContext
    {
        public IReadOnlyList<string> LikelihoodLabels { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ImpactLabels { get; init; } = Array.Empty<string>();
        public IReadOnlyList<RiskLikelihood> LikelihoodLookups { get; init; } = Array.Empty<RiskLikelihood>();
        public IReadOnlyList<RiskImpactLevel> ImpactLookups { get; init; } = Array.Empty<RiskImpactLevel>();
    }

    private sealed class RisksByTierBuildContext
    {
        public RiskRatingScaleContext Scale { get; init; } = new();
        public IReadOnlyList<RiskTier> ActiveTiers { get; init; } = Array.Empty<RiskTier>();
        public IReadOnlyDictionary<int, RaidEscalationTierChangeRequest> LatestPendingByRiskId { get; init; } =
            new Dictionary<int, RaidEscalationTierChangeRequest>();
        public IReadOnlyDictionary<int, RaidEscalationTierChangeRequest> LatestApprovedByRiskId { get; init; } =
            new Dictionary<int, RaidEscalationTierChangeRequest>();
    }

    private static RiskRatingScaleContext BuildRatingScaleContext(
        IReadOnlyList<RiskLikelihood> likelihoodLookups,
        IReadOnlyList<RiskImpactLevel> impactLookups) =>
        new()
        {
            LikelihoodLabels = BuildScaleLabels(likelihoodLookups, DefaultLikelihoodLabels),
            ImpactLabels = BuildScaleLabels(impactLookups, DefaultImpactLabels),
            LikelihoodLookups = likelihoodLookups,
            ImpactLookups = impactLookups
        };

    private static string[] BuildScaleLabels<T>(IReadOnlyList<T> lookups, string[] defaults)
        where T : RaidLookupBase
    {
        if (lookups.Count == 5)
            return lookups.Select(l => l.Label.Trim()).ToArray();

        return defaults;
    }

    private static string DrillKey(int? directorateId, int? tierId) =>
        $"{directorateId?.ToString() ?? "*"}|{tierId?.ToString() ?? "*"}";

    private static Dictionary<string, IReadOnlyList<RisksByTierRiskRow>> BuildDrillRisksByKey(
        IReadOnlyList<(Risk Entity, RisksByTierRiskRow Row)> paired)
    {
        var buckets = new Dictionary<string, List<RisksByTierRiskRow>>(StringComparer.Ordinal);

        void Add(string key, RisksByTierRiskRow row)
        {
            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<RisksByTierRiskRow>();
                buckets[key] = list;
            }
            list.Add(row);
        }

        foreach (var (entity, row) in paired)
        {
            var dirId = ResolvePrimaryDivisionId(entity);
            var tierId = entity.RiskTierId;
            Add(DrillKey(dirId, tierId), row);
            Add(DrillKey(dirId, null), row);
            Add(DrillKey(null, tierId), row);
            Add(DrillKey(null, null), row);
        }

        return buckets.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<RisksByTierRiskRow>)kv.Value,
            StringComparer.Ordinal);
    }

    private static RisksByTierDirectorateRow? BuildMatrixTotalRow(
        IReadOnlyList<RisksByTierDirectorateRow> matrix,
        int tierColumnCount)
    {
        if (matrix.Count == 0 || tierColumnCount == 0)
            return null;

        var counts = new int[tierColumnCount];
        var total = 0;
        foreach (var row in matrix)
        {
            for (var i = 0; i < tierColumnCount && i < row.CountsByTier.Count; i++)
                counts[i] += row.CountsByTier[i];
            total += row.Total;
        }

        return new RisksByTierDirectorateRow
        {
            DirectorateName = "Total",
            DirectorateId = null,
            Total = total,
            CountsByTier = counts
        };
    }

    private static List<RisksByTierGroup> BuildTierGroups(
        IReadOnlyList<RiskTier> tiers,
        IReadOnlyList<(Risk Entity, RisksByTierRiskRow Row)> paired)
    {
        var groups = new List<RisksByTierGroup>();

        foreach (var tier in tiers)
        {
            var tierRows = paired
                .Where(p => p.Entity.RiskTierId == tier.Id)
                .Select(p => p.Row)
                .ToList();

            // Keep operational tiers visible even when empty; skip empty proposed bands.
            if (tierRows.Count == 0 && tier.IsProposedTier)
                continue;

            groups.Add(new RisksByTierGroup
            {
                TierId = tier.Id,
                TierName = tier.Name,
                TierDescription = string.IsNullOrWhiteSpace(tier.Summary)
                    ? tier.Description
                    : tier.Summary,
                IsProposedTier = tier.IsProposedTier,
                SortOrder = tier.SortOrder,
                RiskCount = tierRows.Count,
                Risks = tierRows
            });
        }

        var untiered = paired
            .Where(p => p.Entity.RiskTierId == null)
            .Select(p => p.Row)
            .ToList();

        if (untiered.Count > 0)
        {
            groups.Add(new RisksByTierGroup
            {
                TierId = null,
                TierName = "Not set",
                TierDescription = "Risks without a governance tier assigned.",
                IsProposedTier = false,
                SortOrder = int.MaxValue,
                RiskCount = untiered.Count,
                Risks = untiered
            });
        }

        return groups;
    }

    private static List<RisksByTierDirectorateRow> BuildDirectorateMatrix(
        IReadOnlyList<(Risk Entity, RisksByTierRiskRow Row)> paired,
        IReadOnlyList<RiskTier> tiers,
        IReadOnlyList<SelectOption> directorates)
    {
        var tierKeys = tiers.Select(t => (int?)t.Id).Append(null).ToList();
        var dirNameById = directorates.ToDictionary(d => d.Id, d => d.Name);
        var buckets = new Dictionary<(int? DirId, int? TierId), int>();

        foreach (var (entity, _) in paired)
        {
            var dirId = ResolvePrimaryDivisionId(entity);
            var key = (dirId, entity.RiskTierId);
            buckets[key] = buckets.GetValueOrDefault(key) + 1;
        }

        var dirIds = buckets.Keys
            .Select(k => k.DirId)
            .Distinct()
            .ToList();

        var matrix = new List<RisksByTierDirectorateRow>();
        foreach (var dirId in dirIds)
        {
            var counts = tierKeys
                .Select(tierId => buckets.GetValueOrDefault((dirId, tierId)))
                .ToList();
            var total = counts.Sum();
            if (total == 0)
                continue;

            var name = dirId is int id && dirNameById.TryGetValue(id, out var n) ? n : "Not set";
            matrix.Add(new RisksByTierDirectorateRow
            {
                DirectorateId = dirId,
                DirectorateName = name,
                Total = total,
                CountsByTier = counts
            });
        }

        return matrix
            .OrderByDescending(r => r.Total)
            .ThenBy(r => r.DirectorateName == "Not set" ? "zzzzzz" : r.DirectorateName)
            .ToList();
    }

    private static RisksByTierRiskRow MapRow(Risk r, DateTime today, RisksByTierBuildContext buildContext)
    {
        var scale = buildContext.Scale;
        var daysSince = Math.Max(0, (today - r.UpdatedAt.Date).Days);
        var relation = RaidRegisterTableFormatting.BuildRiskRelation(r);
        var workLabel = relation.Kind switch
        {
            RaidRegisterRelationKinds.Organisation => "Organisation",
            RaidRegisterRelationKinds.Work => relation.Target ?? (r.ProjectId is int pid ? $"Work item #{pid}" : "—"),
            RaidRegisterRelationKinds.Fips => relation.Target ?? "Product",
            _ => "—"
        };
        string? workUrl = relation.Kind == RaidRegisterRelationKinds.Work && relation.ProjectId is int projectId
            ? $"/modern/work/detail/{projectId}"
            : null;

        var inherentScore = r.InherentScore ?? (r.RiskScore > 0 ? r.RiskScore : null);
        var inherent = BuildRatingBand(
            r.Likelihood,
            r.ImpactLevel,
            r.LikelihoodRating,
            r.ImpactRating,
            inherentScore,
            scale);
        var current = BuildRatingBand(
            r.CurrentLikelihood ?? r.Likelihood,
            r.CurrentImpactLevel ?? r.ImpactLevel,
            r.LikelihoodRating,
            r.ImpactRating,
            r.CurrentScore ?? inherentScore,
            scale);
        var residual = BuildRatingBand(
            r.ResidualLikelihoodLevel,
            r.ResidualImpactLevel,
            r.ResidualLikelihood,
            r.ResidualImpact,
            r.ResidualScore,
            scale);

        var (scoreTrend, scoreTrendSummary) = BuildScoreTrend(inherent.Score, current.Score, residual.Score);
        var (hasTierChangeAction, tierChangeIsEscalation, pendingRequestId) =
            ResolveTierChangeAction(r, buildContext);

        string? escalationActionUrl = null;
        string? deescalationActionUrl = null;
        if (hasTierChangeAction)
        {
            var tab = tierChangeIsEscalation ? "escalations" : "deescalations";
            var actionUrl = pendingRequestId is int reqId
                ? $"/modern/operations/raid/escalations/action/{reqId}?returnTab={tab}"
                : $"/modern/operations/raid/escalations/action/risk/{r.Id}?returnTab={tab}";

            if (tierChangeIsEscalation)
                escalationActionUrl = actionUrl;
            else
                deescalationActionUrl = actionUrl;
        }

        return new RisksByTierRiskRow
        {
            Id = r.Id,
            Reference = $"R-{r.Id:D4}",
            Title = r.Title,
            DetailUrl = $"/modern/raid/risks/{r.Id}",
            IsClosed = r.ClosedDate != null,
            InherentScore = inherent.Score,
            CurrentScore = current.Score,
            ResidualScore = residual.Score,
            Inherent = inherent,
            Current = current,
            Residual = residual,
            ScoreTrend = scoreTrend,
            ScoreTrendSummary = scoreTrendSummary,
            ResidualLikelihoodImpact = FormatBandPair(residual),
            CurrentLikelihoodImpact = FormatBandPair(current),
            InherentLikelihoodImpact = FormatBandPair(inherent),
            Mitigation = Truncate(r.ResponseStrategy, MitigationPreviewMaxLength),
            MitigationFull = NormalizeText(r.ResponseStrategy),
            Description = NormalizeText(r.Description),
            Status = r.RiskStatus?.Label ?? r.Status ?? "—",
            TierName = r.RiskTier?.Name ?? "Not set",
            Owner = FormatOwner(r),
            LastReviewedAt = r.LastReviewDate,
            LastUpdatedAt = r.UpdatedAt,
            CreatedAt = r.CreatedAt,
            DaysSinceLastUpdate = daysSince,
            WorkItemOrProject = workLabel,
            WorkItemUrl = workUrl,
            Directorate = FormatDirectorates(r),
            HasTierChangeAction = hasTierChangeAction,
            TierChangeIsEscalation = tierChangeIsEscalation,
            PendingTierChangeRequestId = pendingRequestId,
            EscalationActionUrl = escalationActionUrl,
            DeescalationActionUrl = deescalationActionUrl
        };
    }

    private static (bool hasAction, bool isEscalation, int? requestId) ResolveTierChangeAction(
        Risk r,
        RisksByTierBuildContext buildContext)
    {
        if (r.ClosedDate != null)
            return (false, false, null);

        buildContext.LatestPendingByRiskId.TryGetValue(r.Id, out var pendingReq);
        var onProposedTier = r.RiskTier?.IsProposedTier == true;

        if (pendingReq == null && !onProposedTier)
            return (false, false, null);

        buildContext.LatestApprovedByRiskId.TryGetValue(r.Id, out var lastApproved);

        var isEscalation = pendingReq != null
            ? IsTierChangeEscalation(pendingReq, buildContext.ActiveTiers)
            : ClassifyOrphanProposedTierChange(r, lastApproved, buildContext.ActiveTiers);

        return (true, isEscalation, pendingReq?.Id);
    }

    private static RiskTier? ResolveTier(RiskTier? tier, int? tierId, IReadOnlyList<RiskTier> tiers) =>
        tier ?? (tierId is int id ? tiers.FirstOrDefault(t => t.Id == id) : null);

    private static bool IsTierChangeEscalation(
        RaidEscalationTierChangeRequest req,
        IReadOnlyList<RiskTier> tiers)
    {
        var from = ResolveTier(req.FromRiskTier, req.FromRiskTierId, tiers);
        var to = ResolveTier(req.ToRiskTier, req.ToRiskTierId, tiers);
        if (from == null || to == null)
            return true;

        return RiskTierGovernance.IsEscalation(from, to, tiers);
    }

    private static bool ClassifyOrphanProposedTierChange(
        Risk risk,
        RaidEscalationTierChangeRequest? lastApproved,
        IReadOnlyList<RiskTier> tiers)
    {
        if (risk.RiskTier is not { IsProposedTier: true } proposed)
            return true;

        var from = ResolveTier(lastApproved?.FromRiskTier, lastApproved?.FromRiskTierId, tiers);
        if (from != null)
            return RiskTierGovernance.IsEscalation(from, proposed, tiers);

        return true;
    }

    private static RisksByTierRiskRatingBand BuildRatingBand(
        RiskLikelihood? likelihood,
        RiskImpactLevel? impact,
        int? legacyLikelihood,
        int? legacyImpact,
        decimal? score,
        RiskRatingScaleContext scale)
    {
        var likelihoodIndex = ResolveLikelihoodIndex(
            likelihood,
            legacyLikelihood,
            scale.LikelihoodLookups,
            scale.LikelihoodLabels);
        var impactIndex = ResolveImpactIndex(
            impact,
            legacyImpact,
            scale.ImpactLookups,
            scale.ImpactLabels);

        var likelihoodLabel = ResolveLikelihoodLabel(likelihood, likelihoodIndex, scale.LikelihoodLabels);
        var impactLabel = ResolveImpactLabel(impact, impactIndex, scale.ImpactLabels);

        var resolvedScore = score;
        if (!resolvedScore.HasValue && likelihoodIndex > 0 && impactIndex > 0)
            resolvedScore = likelihoodIndex * impactIndex;

        return new RisksByTierRiskRatingBand
        {
            Score = resolvedScore,
            LikelihoodLabel = likelihoodLabel,
            ImpactLabel = impactLabel,
            LikelihoodIndex = likelihoodIndex,
            ImpactIndex = impactIndex
        };
    }

    private static string FormatBandPair(RisksByTierRiskRatingBand band)
    {
        if (band.LikelihoodLabel == "—" && band.ImpactLabel == "—")
            return "—";

        return $"{band.LikelihoodLabel} × {band.ImpactLabel}";
    }

    private static int ResolveLikelihoodIndex(
        RiskLikelihood? lookup,
        int? legacy,
        IReadOnlyList<RiskLikelihood> orderedLookups,
        IReadOnlyList<string> scaleLabels) =>
        ResolveLookupIndex(lookup?.MatrixScore, lookup?.Label, lookup?.Id, legacy, orderedLookups, scaleLabels);

    private static int ResolveImpactIndex(
        RiskImpactLevel? lookup,
        int? legacy,
        IReadOnlyList<RiskImpactLevel> orderedLookups,
        IReadOnlyList<string> scaleLabels) =>
        ResolveLookupIndex(lookup?.MatrixScore, lookup?.Label, lookup?.Id, legacy, orderedLookups, scaleLabels);

    private static int ResolveLookupIndex<T>(
        int? matrixScore,
        string? label,
        int? lookupId,
        int? legacy,
        IReadOnlyList<T> orderedLookups,
        IReadOnlyList<string> scaleLabels)
        where T : RaidLookupBase
    {
        if (matrixScore is >= 1 and <= 5)
            return matrixScore.Value;

        if (lookupId is int id && orderedLookups.Count > 0)
        {
            var pos = orderedLookups.ToList().FindIndex(x => x.Id == id);
            if (pos >= 0)
                return MapPositionToFive(pos, orderedLookups.Count);
        }

        var labelIdx = MatchLabelIndex(label, scaleLabels);
        if (labelIdx > 0)
            return labelIdx;

        if (legacy is >= 1 and <= 5)
            return legacy.Value;

        return 0;
    }

    private static int MapPositionToFive(int zeroBasedIndex, int count)
    {
        if (count <= 1)
            return 3;

        return Math.Clamp(
            (int)Math.Round((zeroBasedIndex + 1) * 5.0 / count, MidpointRounding.AwayFromZero),
            1,
            5);
    }

    private static int MatchLabelIndex(string? label, IReadOnlyList<string> scaleLabels)
    {
        if (string.IsNullOrWhiteSpace(label))
            return 0;

        for (var i = 0; i < scaleLabels.Count; i++)
        {
            if (label.Trim().Equals(scaleLabels[i], StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }

        return 0;
    }

    private static string ResolveLikelihoodLabel(
        RiskLikelihood? lookup,
        int index,
        IReadOnlyList<string> scaleLabels) =>
        ResolveLookupLabel(lookup?.Label, index, scaleLabels);

    private static string ResolveImpactLabel(
        RiskImpactLevel? lookup,
        int index,
        IReadOnlyList<string> scaleLabels) =>
        ResolveLookupLabel(lookup?.Label, index, scaleLabels);

    private static string ResolveLookupLabel(string? lookupLabel, int index, IReadOnlyList<string> scaleLabels)
    {
        if (!string.IsNullOrWhiteSpace(lookupLabel))
            return lookupLabel.Trim();

        if (index >= 1 && index <= scaleLabels.Count)
            return scaleLabels[index - 1];

        return "—";
    }

    private static (string? trend, string? summary) BuildScoreTrend(
        decimal? inherent,
        decimal? current,
        decimal? residual)
    {
        if (!inherent.HasValue && !current.HasValue && !residual.HasValue)
            return (null, null);

        var c = current ?? inherent;
        var comparisons = new List<int>();

        void AddComparison(decimal? from, decimal? to)
        {
            if (!from.HasValue || !to.HasValue)
                return;

            if (to > from) comparisons.Add(1);
            else if (to < from) comparisons.Add(-1);
            else comparisons.Add(0);
        }

        AddComparison(inherent, c);
        AddComparison(c, residual);
        AddComparison(inherent, residual);

        if (comparisons.Count == 0)
            return ("stable", BuildScoreTrendSummary("stable", inherent, c, residual));

        var ups = comparisons.Count(x => x > 0);
        var downs = comparisons.Count(x => x < 0);

        string trend;
        if (ups > 0 && downs > 0)
            trend = "mixed";
        else if (ups > downs)
            trend = "worsening";
        else if (downs > ups)
            trend = "improving";
        else
            trend = "stable";

        return (trend, BuildScoreTrendSummary(trend, inherent, c, residual));
    }

    private static string BuildScoreTrendSummary(
        string trend,
        decimal? inherent,
        decimal? current,
        decimal? residual)
    {
        var intro =
            "Inherent is the first assessment when the risk was recorded. " +
            "Current reflects the situation now. " +
            "Residual is what remains after controls and mitigation. ";

        var detail = trend switch
        {
            "worsening" =>
                "Overall, scores increase between ratings — the risk may be getting worse or controls may not be fully effective.",
            "improving" =>
                "Overall, scores decrease between ratings — controls appear to be reducing the risk.",
            "mixed" =>
                "Scores move in different directions between ratings — review inherent, current and residual separately.",
            _ =>
                "Scores are unchanged across the recorded ratings."
        };

        var parts = new List<string>();
        if (inherent.HasValue) parts.Add($"inherent {FormatScoreValue(inherent)}");
        if (current.HasValue) parts.Add($"current {FormatScoreValue(current)}");
        if (residual.HasValue) parts.Add($"residual {FormatScoreValue(residual)}");

        if (parts.Count >= 2)
            detail += " Recorded scores: " + string.Join(", ", parts) + ".";

        return intro + detail;
    }

    private static string FormatScoreValue(decimal? score) =>
        score.HasValue ? score.Value.ToString("0") : "—";

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatOwner(Risk r)
    {
        if (r.OwnerUser != null)
        {
            if (!string.IsNullOrWhiteSpace(r.OwnerUser.Name))
                return r.OwnerUser.Name.Trim();

            var fullName = string.Join(" ",
                    new[] { r.OwnerUser.FirstName, r.OwnerUser.LastName }
                        .Where(s => !string.IsNullOrWhiteSpace(s)))
                .Trim();
            if (!string.IsNullOrWhiteSpace(fullName))
                return fullName;

            if (!string.IsNullOrWhiteSpace(r.OwnerUser.Email))
                return r.OwnerUser.Email.Trim();
        }

        return string.IsNullOrWhiteSpace(r.OwnerEmail) ? "—" : r.OwnerEmail.Trim();
    }

    private static string FormatDirectorates(Risk r)
    {
        var fromJunction = r.RiskDivisions
            .Where(d => d.Division != null)
            .Select(d => d.Division!.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        if (fromJunction.Count > 0)
            return string.Join("; ", fromJunction);

        var fromProject = r.Project?.Directorates?
            .Where(pd => pd.Division != null)
            .Select(pd => pd.Division!.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        if (fromProject is { Count: > 0 })
            return string.Join("; ", fromProject);

        return "—";
    }

    private static int? ResolvePrimaryDivisionId(Risk r)
    {
        var fromJunction = r.RiskDivisions.Select(d => d.DivisionId).Distinct().OrderBy(id => id).ToList();
        if (fromJunction.Count > 0)
            return fromJunction[0];

        var fromProject = r.Project?.Directorates?
            .Select(pd => pd.DivisionId)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        return fromProject is { Count: > 0 } ? fromProject[0] : null;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
            return trimmed;
        return trimmed[..(maxLength - 1)].TrimEnd() + "…";
    }
}
