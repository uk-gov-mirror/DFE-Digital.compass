namespace Compass.ViewModels.Modern;

/// <summary>
/// One radio option for risk escalation / de-escalation (target tier is applied via Operations approval).
/// </summary>
public sealed record ModernRaidEscalationTierChoice(
    int TargetTierId,
    string ChoiceId,
    string PrimaryLabel,
    string? Hint);

/// <summary>Pending tier change shown on risk detail until Operations decides.</summary>
public sealed record RiskPendingTierChangeVm(string RequestLine);

public sealed class ModernRaidEscalationRequestViewModel
{
    public string RecordType { get; init; } = "risk";
    public int RecordId { get; init; }
    public string Reference { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string CurrentTierLabel { get; init; } = "—";
    public string CurrentStatusLabel { get; init; } = "—";
    public string CurrentRatingLabel { get; init; } = "—";
    public string? CurrentDetail { get; init; }
    public DateTime? UpdatedAt { get; init; }

    /// <summary>Database id of the risk tier currently on the risk (null if unassigned).</summary>
    public int? CurrentTierId { get; init; }

    /// <summary>Radios for proposed target tier only (Tier 1 / Tier 2 cannot be set arbitrarily — requests go through Operations RAID).</summary>
    public IReadOnlyList<ModernRaidEscalationTierChoice> TierChoices { get; init; } = Array.Empty<ModernRaidEscalationTierChoice>();

    /// <summary>No tier on the risk, or no adjacent tiers configured.</summary>
    public string? TierChoicesBlockedReason { get; init; }

    /// <summary>When set, a tier-change request is awaiting Operations approval.</summary>
    public bool HasPendingTierChangeRequest { get; init; }

    public int? PendingRequestId { get; init; }
    public string? PendingFromTierLabel { get; init; }
    public string? PendingToTierLabel { get; init; }
    public string? PendingRationale { get; init; }
    public string? PendingRequestedBy { get; init; }
    public DateTime? PendingSubmittedAt { get; init; }

    /// <summary>True when the signed-in user submitted the pending request and may cancel it.</summary>
    public bool CanCancelPendingRequest { get; init; }
}

public sealed record ModernRaidEscalationPendingRow(
    int? RequestId,
    string RecordType,
    int RecordId,
    string Reference,
    string Title,
    string RequestedChange,
    /// <summary>Governance tier before the proposed move (snapshot).</summary>
    string CurrentTierLabel,
    /// <summary>Requested target tier label.</summary>
    string ProposedTierLabel,
    /// <summary>True when moving to higher governance (Tier 3 → Tier 2 → Tier 1).</summary>
    bool IsEscalation,
    string RequestedBy,
    DateTime RequestedAt,
    string ReasonSnippet,
    /// <summary>Full rationale when available (for tooltip / modal).</summary>
    string? FullReason);

public sealed record ModernRaidEscalationCurrentRow(
    string RecordType,
    int RecordId,
    string Reference,
    string Title,
    string PreviousTier,
    string CurrentTier,
    string EscalationPath,
    string? ApprovedBy,
    DateTime? ApprovedAt,
    string? Owner,
    DateTime UpdatedAt);

/// <summary>Row in Operations → RAID → Active risks (open, non-deleted risks).</summary>
public sealed record ModernRaidActiveRiskRow(
    int Id,
    string Reference,
    string Title,
    string? TierName,
    string? StatusLabel,
    DateTime UpdatedAt);

public sealed class ModernRaidEscalationManagementViewModel
{
    public string ActiveTab { get; init; } = "escalations";

    public int PendingApprovalCount { get; init; }
    public int PendingEscalationsCount { get; init; }
    public int PendingDeescalationsCount { get; init; }
    public int CurrentlyEscalatedCount { get; init; }
    public int ActiveRisksCount { get; init; }
    public IReadOnlyList<ModernRaidEscalationPendingRow> PendingEscalations { get; init; } = Array.Empty<ModernRaidEscalationPendingRow>();
    public IReadOnlyList<ModernRaidEscalationPendingRow> PendingDeescalations { get; init; } = Array.Empty<ModernRaidEscalationPendingRow>();
    public IReadOnlyList<ModernRaidEscalationCurrentRow> Current { get; init; } = Array.Empty<ModernRaidEscalationCurrentRow>();
    public IReadOnlyList<ModernRaidActiveRiskRow> ActiveRisks { get; init; } = Array.Empty<ModernRaidActiveRiskRow>();
}

/// <summary>Operational tier (non-proposed) for the reject path.</summary>
public sealed record ModernRaidEscalationRejectTierOption(int Id, string Name, int SortOrder);

/// <summary>Single pending tier-change request: risk context + current vs requested tier (approval applies configured operational band in line with the request).</summary>
public sealed class ModernRaidEscalationReviewViewModel
{
    public int RequestId { get; init; }
    public int RiskId { get; init; }
    public string Reference { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int RiskScore { get; init; }
    public string InherentLabel { get; init; } = "—";
    public string StatusLabel { get; init; } = "—";
    /// <summary>Tier the risk was on when the request was raised (from the request snapshot). While pending, the live risk may already point at the <em>proposed</em> target — do not use that for this label.</summary>
    public string CurrentRiskTierLabel { get; init; } = "—";
    public string RequestedToTierLabel { get; init; } = "—";
    /// <summary>Operational band name for the pre-selected “decline / keep” option (same list as the reject select).</summary>
    public string DefaultRejectTierName { get; init; } = "—";
    public string? Rationale { get; init; }
    public string SubmittedByDisplay { get; init; } = "—";
    public DateTime SubmittedAt { get; init; }
    public string RiskDetailUrl { get; init; } = "#";
    public string ListReturnUrl { get; init; } = "";
    public string ListReturnTab { get; init; } = "escalations";
    public bool IsEscalation { get; init; } = true;

    /// <summary>Active <strong>operational</strong> (non-proposed) tiers for “reject — set band” select.</summary>
    public IReadOnlyList<ModernRaidEscalationRejectTierOption> RejectTierOptions { get; init; } =
        Array.Empty<ModernRaidEscalationRejectTierOption>();

    /// <summary>Pre-selected tier for reject (current operational band, or best match from request snapshot).</summary>
    public int? RejectTierDefaultId { get; init; }
}
