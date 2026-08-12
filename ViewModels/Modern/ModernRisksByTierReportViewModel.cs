namespace Compass.ViewModels.Modern;

/// <summary>Data for <c>/modern/reporting/risks-by-tier</c> — risk-team oversight by governance tier.</summary>
public sealed class ModernRisksByTierReportViewModel
{
    public int? FilterDirectorateId { get; init; }
    public bool IncludeClosed { get; init; }

    public IReadOnlyList<SelectOption> Directorates { get; init; } = Array.Empty<SelectOption>();

    public int OpenRiskCount { get; init; }
    public int ClosedRiskCount { get; init; }
    public int TotalRiskCount { get; init; }
    public int UntieredRiskCount { get; init; }
    public int StaleRiskCount { get; init; }

    /// <summary>Column headers for the directorate matrix (tier display names in sort order).</summary>
    public IReadOnlyList<RisksByTierColumn> TierColumns { get; init; } = Array.Empty<RisksByTierColumn>();

    public IReadOnlyList<RisksByTierDirectorateRow> DirectorateMatrix { get; init; } =
        Array.Empty<RisksByTierDirectorateRow>();

    public IReadOnlyList<RisksByTierGroup> TierGroups { get; init; } = Array.Empty<RisksByTierGroup>();

    /// <summary>Footer totals for the directorate × tier matrix (aligned with <see cref="TierColumns"/>).</summary>
    public RisksByTierDirectorateRow? MatrixTotalRow { get; init; }

    /// <summary>Flat drill keys — <c>dirId|tierId</c> with <c>*</c> for “all” — to risk rows for matrix drill-down.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RisksByTierRiskRow>> DrillRisksByKey { get; init; } =
        new Dictionary<string, IReadOnlyList<RisksByTierRiskRow>>();

    /// <summary>Likelihood scale labels (low → high) for modal rating visualisation.</summary>
    public IReadOnlyList<string> LikelihoodScaleLabels { get; init; } = Array.Empty<string>();

    /// <summary>Impact scale labels (low → high) for modal rating visualisation.</summary>
    public IReadOnlyList<string> ImpactScaleLabels { get; init; } = Array.Empty<string>();

    /// <summary>True when the signed-in user may open Operations tier-change actions (Central Operations Admin or Super admin).</summary>
    public bool CanActionTierChanges { get; init; }
}

public sealed class RisksByTierColumn
{
    public int? TierId { get; init; }
    public string Name { get; init; } = "";
    public bool IsProposedTier { get; init; }
}

public sealed class RisksByTierGroup
{
    public int? TierId { get; init; }
    public string TierName { get; init; } = "";
    public string? TierDescription { get; init; }
    public bool IsProposedTier { get; init; }
    public int SortOrder { get; init; }
    public int RiskCount { get; init; }
    public IReadOnlyList<RisksByTierRiskRow> Risks { get; init; } = Array.Empty<RisksByTierRiskRow>();
}

public sealed class RisksByTierRiskRow
{
    public int Id { get; init; }
    public string Reference { get; init; } = "";
    public string Title { get; init; } = "";
    public string DetailUrl { get; init; } = "#";
    public bool IsClosed { get; init; }

    public decimal? InherentScore { get; init; }
    public decimal? CurrentScore { get; init; }
    public decimal? ResidualScore { get; init; }

    /// <summary>Likelihood and impact labels for residual rating, e.g. <c>Possible × Major</c>.</summary>
    public string ResidualLikelihoodImpact { get; init; } = "—";

    /// <summary>Likelihood and impact labels for current rating.</summary>
    public string CurrentLikelihoodImpact { get; init; } = "—";

    /// <summary>Likelihood and impact labels for inherent rating.</summary>
    public string InherentLikelihoodImpact { get; init; } = "—";

    public string? Mitigation { get; init; }

    public DateTime? LastReviewedAt { get; init; }
    public DateTime LastUpdatedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public int DaysSinceLastUpdate { get; init; }

    public string WorkItemOrProject { get; init; } = "—";
    public string? WorkItemUrl { get; init; }
    public string Directorate { get; init; } = "—";

    /// <summary>Full risk description for the reporting modal.</summary>
    public string? Description { get; init; }

    /// <summary>Full mitigation / response strategy for the reporting modal.</summary>
    public string? MitigationFull { get; init; }

    public string Status { get; init; } = "—";
    public string TierName { get; init; } = "Not set";
    public string Owner { get; init; } = "—";

    public RisksByTierRiskRatingBand Inherent { get; init; } = new();
    public RisksByTierRiskRatingBand Current { get; init; } = new();
    public RisksByTierRiskRatingBand Residual { get; init; } = new();

    /// <summary>worsening, improving, stable, or mixed — compared across inherent, current and residual.</summary>
    public string? ScoreTrend { get; init; }

    public string? ScoreTrendSummary { get; init; }

    /// <summary>True when Operations can approve/reject a pending tier change (proposed tier or pending request).</summary>
    public bool HasTierChangeAction { get; init; }

    /// <summary>When <see cref="HasTierChangeAction"/> is true, whether the queue item is an escalation (vs de-escalation).</summary>
    public bool TierChangeIsEscalation { get; init; }

    public int? PendingTierChangeRequestId { get; init; }

    /// <summary>Operations approve/reject URL when <see cref="HasTierChangeAction"/> and <see cref="TierChangeIsEscalation"/>.</summary>
    public string? EscalationActionUrl { get; init; }

    /// <summary>Operations approve/reject URL when <see cref="HasTierChangeAction"/> and not <see cref="TierChangeIsEscalation"/>.</summary>
    public string? DeescalationActionUrl { get; init; }
}

public sealed class RisksByTierRiskRatingBand
{
    public decimal? Score { get; init; }
    public string LikelihoodLabel { get; init; } = "—";
    public string ImpactLabel { get; init; } = "—";

    /// <summary>1–5 position on the likelihood scale (0 if unknown).</summary>
    public int LikelihoodIndex { get; init; }

    /// <summary>1–5 position on the impact scale (0 if unknown).</summary>
    public int ImpactIndex { get; init; }
}

public sealed class RisksByTierDirectorateRow
{
    public string DirectorateName { get; init; } = "";
    public int? DirectorateId { get; init; }
    public int Total { get; init; }

    /// <summary>Counts aligned with <see cref="ModernRisksByTierReportViewModel.TierColumns"/>.</summary>
    public IReadOnlyList<int> CountsByTier { get; init; } = Array.Empty<int>();
}
