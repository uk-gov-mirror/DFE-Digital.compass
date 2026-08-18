using Compass.Data;
using Compass.Models.Fips;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services.Fips;

/// <summary>
/// Default CMDB sync rules migrated from legacy Strapi sync
/// (<c>sync-app/services/strapiService.js</c> — <c>applyRejectionCriteria</c>).
/// Applied per CMDB entry during Compass <see cref="FipsCmdbProductSyncService"/> sync.
/// </summary>
public static class FipsCmdbSyncDefaultRules
{
    /// <summary>Parent categories that set Strapi state to Rejected (maps to CMDB parent name in Compass).</summary>
    private static readonly string[] RejectedParentCategories =
    [
        "End User Computing",
        "Corporate services",
        "Estates (Buildings)",
        "Archived Systems",
        "IT for the IT department",
        "Voice and Data Network",
        "Shared IT core services",
        "zBusiness Operations (do not use)"
    ];

    public const string BusinessServiceEnterpriseRuleName = "Service classification: Business Service";

    public static FipsCmdbSyncRule CreateBusinessServiceEnterpriseRule(DateTime utcNow) =>
        new()
        {
            Name = BusinessServiceEnterpriseRuleName,
            FieldScope = FipsCmdbSyncRuleScopes.ServiceClassification,
            MatchKind = FipsCmdbSyncRuleMatchKinds.Contains,
            Pattern = "Business Service",
            Action = FipsCmdbSyncRuleActions.SetEnterpriseService,
            TargetStatus = CMDBProductStatus.Rejected,
            SortOrder = 5,
            IsActive = true,
            CreatedAt = utcNow,
            UpdatedAt = utcNow
        };

    public static IReadOnlyList<FipsCmdbSyncRule> CreateSeedRules(DateTime utcNow)
    {
        var rules = new List<FipsCmdbSyncRule>
        {
            CreateBusinessServiceEnterpriseRule(utcNow),
            new()
            {
                Name = "Title contains (PP)",
                FieldScope = FipsCmdbSyncRuleScopes.Title,
                MatchKind = FipsCmdbSyncRuleMatchKinds.Contains,
                Pattern = "(PP)",
                TargetStatus = CMDBProductStatus.Rejected,
                SortOrder = 10,
                IsActive = true,
                CreatedAt = utcNow,
                UpdatedAt = utcNow
            }
        };

        var order = 20;
        foreach (var category in RejectedParentCategories)
        {
            rules.Add(new FipsCmdbSyncRule
            {
                Name = $"Parent: {category}",
                FieldScope = FipsCmdbSyncRuleScopes.ParentName,
                MatchKind = FipsCmdbSyncRuleMatchKinds.Contains,
                Pattern = category,
                TargetStatus = CMDBProductStatus.Rejected,
                SortOrder = order,
                IsActive = true,
                CreatedAt = utcNow,
                UpdatedAt = utcNow
            });
            order += 10;
        }

        return rules;
    }

    public static async Task EnsureSeededAsync(CompassDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.FipsCmdbSyncRules.AnyAsync(cancellationToken))
        {
            var now = DateTime.UtcNow;
            foreach (var rule in CreateSeedRules(now))
                db.FipsCmdbSyncRules.Add(rule);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var exists = await db.FipsCmdbSyncRules.AnyAsync(
            r => r.Action == FipsCmdbSyncRuleActions.SetEnterpriseService
                 && r.FieldScope == FipsCmdbSyncRuleScopes.ServiceClassification
                 && r.Pattern == "Business Service",
            cancellationToken);
        if (exists)
            return;

        db.FipsCmdbSyncRules.Add(CreateBusinessServiceEnterpriseRule(DateTime.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
    }
}
