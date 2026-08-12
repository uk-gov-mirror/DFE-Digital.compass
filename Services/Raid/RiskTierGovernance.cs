using System.Linq;
using Compass.Models;

namespace Compass.Services.Raid;

/// <summary>
/// Resolves governance intensity for risk tiers (1 = highest — Tier 1 — 3 = lowest for a three-band model).
/// Escalate = move to a lower level number; de-escalate = move to a higher level number.
/// </summary>
public static class RiskTierGovernance
{
    /// <inheritdoc cref="ResolveLevelCore"/>
    public static int ResolveLevel(RiskTier tier, IReadOnlyList<RiskTier> activeTiers)
    {
        var list = activeTiers.Where(t => t.IsActive).ToList();
        return ResolveLevelCore(tier, list);
    }

    /// <summary>
    /// Picks the <see cref="RiskTier.IsProposedTier"/> for governance band <paramref name="targetLevel" /> (1/2/3).
    /// <see cref="TryInferGovernanceLevelFromNameOrCode" /> (Tier 1/2/3 in the name) wins first, then
    /// <see cref="RiskTier.GovernanceLevel" />, then <see cref="ResolveLevel" />, excluding rows where the
    /// name says a <em>different</em> band than <paramref name="targetLevel" /> (so a mis-filed
    /// <see cref="RiskTier.GovernanceLevel" /> cannot steal a band for which another row is named correctly).
    /// </summary>
    public static RiskTier? FindProposedForGovernanceBand(
        IReadOnlyList<RiskTier> proposedTiers,
        IReadOnlyList<RiskTier> allTiers,
        int targetLevel)
    {
        if (proposedTiers.Count == 0 || targetLevel < 1)
            return null;

        static IOrderedEnumerable<RiskTier> OrderTiers(IEnumerable<RiskTier> e) =>
            e.OrderBy(t => t.SortOrder).ThenBy(t => t.Id);

        // A proposed row with GovernanceLevel=2 and Name "Tier 3" (or the opposite) must not win for the wrong
        // band. Prefer unambiguous name/code, then other signals only when they do not contradict the name.
        static bool NameImpliesOtherBand(RiskTier t, int want)
        {
            var n = TryInferGovernanceLevelFromNameOrCode(t);
            return n is int b && b != want;
        }

        var byName = OrderTiers(
                proposedTiers.Where(
                    t => TryInferGovernanceLevelFromNameOrCode(t) == targetLevel))
            .FirstOrDefault();
        if (byName is not null)
            return byName;

        var byGovernance = OrderTiers(
                proposedTiers.Where(
                    t => t.GovernanceLevel == targetLevel && !NameImpliesOtherBand(t, targetLevel)))
            .FirstOrDefault();
        if (byGovernance is not null)
            return byGovernance;

        return OrderTiers(
                proposedTiers.Where(
                    t => ResolveLevel(t, allTiers) == targetLevel && !NameImpliesOtherBand(t, targetLevel)))
            .FirstOrDefault();
    }

    /// <summary>When <see cref="RiskTier.GovernanceLevel"/> is 0, try to read Tier 1/2/3 from <see cref="RiskTier.Name"/> and <see cref="RiskTier.Code"/>
    /// (aligns with risk edit / admin expectations). If not found, returns <c>null</c>.</summary>
    public static int? TryInferGovernanceLevelFromNameOrCode(RiskTier tier)
    {
        var nl = (tier.Name ?? string.Empty).ToLowerInvariant();
        var cl = (tier.Code ?? string.Empty).ToLowerInvariant();
        if (cl == "1" || (nl.Contains("tier 1") && !ContainsWrongTier1(nl)))
            return 1;
        if (cl == "2" || nl.Contains("tier 2"))
            return 2;
        if (cl == "3" || nl.Contains("tier 3"))
            return 3;
        return null;

        static bool ContainsWrongTier1(string nl) =>
            nl.Contains("tier 10") || nl.Contains("tier 11") || nl.Contains("tier 12");
    }

    /// <summary>
    /// Computes level for <paramref name="tier"/> given the full tier list (same rules as escalation UI).
    /// Explicit <see cref="RiskTier.GovernanceLevel"/> wins, then name/code (Tier 1/2/3);
    /// otherwise use legacy sort-based ordering (set explicit levels in admin when in doubt).
    /// </summary>
    public static int ResolveLevelCore(RiskTier tier, IReadOnlyList<RiskTier> activeTiersAll)
    {
        if (tier.GovernanceLevel > 0)
            return tier.GovernanceLevel;

        if (TryInferGovernanceLevelFromNameOrCode(tier) is { } inferred)
            return inferred;

        var operationalOrdered = activeTiersAll.Where(t => t.IsActive && !t.IsProposedTier)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Id)
            .ToList();
        var idx = operationalOrdered.FindIndex(t => t.Id == tier.Id);
        if (idx >= 0)
            return idx + 1;

        var proposedOrdered = activeTiersAll.Where(t => t.IsActive && t.IsProposedTier)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Id)
            .ToList();
        var pi = proposedOrdered.FindIndex(t => t.Id == tier.Id);
        if (pi >= 0)
            return pi + 1;

        return tier.SortOrder > 0 ? tier.SortOrder : 1;
    }

    /// <summary>
    /// Whether <paramref name="from"/> moving to <paramref name="to"/> is an escalation (toward higher governance / lower level number).
    /// </summary>
    public static bool IsEscalation(RiskTier from, RiskTier to, IReadOnlyList<RiskTier> activeTiers)
    {
        var lf = ResolveLevel(from, activeTiers);
        var lt = ResolveLevel(to, activeTiers);
        return lt < lf;
    }

    /// <summary>
    /// Maps a proposed or operational target tier to the <strong>operational</strong> band with the same governance level
    /// (e.g. <c>Tier 2 — Proposed</c> → <c>Tier 2</c> when both share the same level).
    /// </summary>
    public static RiskTier? ResolveOperationalTierMatchingGovernance(
        RiskTier? toTier,
        IReadOnlyList<RiskTier> activeTiers)
    {
        if (toTier == null) return null;
        var list = activeTiers.Where(t => t.IsActive).ToList();
        if (list.Count == 0) return null;

        if (!toTier.IsProposedTier)
            return toTier;

        var targetLevel = ResolveLevel(toTier, list);
        return list
            .Where(t => !t.IsProposedTier)
            .Where(t => ResolveLevel(t, list) == targetLevel)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Id)
            .FirstOrDefault();
    }

    /// <summary>Operational band to apply when Operations approves a tier-change request.</summary>
    public static RiskTier? ResolveApprovalOperationalTier(
        RiskTier? requestedTier,
        IReadOnlyList<RiskTier> activeTiers)
    {
        if (requestedTier == null)
            return null;

        var list = activeTiers.Where(t => t.IsActive).ToList();
        if (list.Count == 0)
            return null;

        var matched = ResolveOperationalTierMatchingGovernance(requestedTier, list);
        if (matched != null)
            return matched;

        if (!requestedTier.IsProposedTier)
            return requestedTier;

        var inferred = TryInferGovernanceLevelFromNameOrCode(requestedTier);
        if (inferred is int band)
        {
            matched = list
                .Where(t => !t.IsProposedTier)
                .Where(t => TryInferGovernanceLevelFromNameOrCode(t) == band)
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.Id)
                .FirstOrDefault();
            if (matched != null)
                return matched;
        }

        return null;
    }
}
