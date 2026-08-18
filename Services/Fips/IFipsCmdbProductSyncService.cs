using Compass.Models.Fips;

namespace Compass.Services.Fips;

/// <summary>Incremental progress while <see cref="IFipsCmdbProductSyncService.SyncActiveServiceOfferingsAsync"/> runs (for UI streaming).</summary>
public sealed class FipsCmdbSyncProgressUpdate
{
    public const string PhasePreparing = "preparing";
    public const string PhaseLoadingCmdb = "loading_cmdb";
    public const string PhaseProcessing = "processing";

    public string Phase { get; init; } = "";
    public string? Message { get; init; }
    public int? Processed { get; init; }
    public int? Total { get; init; }
}

public sealed class FipsCmdbSyncedProduct
{
    public Guid Id { get; init; }
    public string Title { get; init; } = "";
    public CMDBProductStatus Status { get; init; }
}

public sealed class FipsCmdbBulkSyncRunInfo
{
    public DateTime StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public string Status { get; init; } = "";
    public string? InitiatedBy { get; init; }
    public int ProductsCreated { get; init; }
    public int ProductsUpdated { get; init; }
    public int ErrorsEncountered { get; init; }
}

public sealed class FipsCmdbProductSyncResult
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int SkippedRetired { get; set; }
    public int SkippedNoSysId { get; set; }
    /// <summary>Reserved; sync now creates Compass rows for unmatched CMDB sys_ids.</summary>
    public int SkippedNoLocalMatch { get; set; }
    /// <summary>Rows whose status was set by an active <see cref="FipsCmdbSyncRule"/> during this run.</summary>
    public int StatusSetByRules { get; set; }
    public int Errors { get; set; }
    public List<string> ErrorSamples { get; } = new();
    /// <summary>Products created in this run (after rules). Use <see cref="NewProductsNeedingInfo"/> for entries still New.</summary>
    public List<FipsCmdbSyncedProduct> CreatedProducts { get; } = new();
    /// <summary>Count of service-register rows in New status after this run.</summary>
    public int NewStatusCount { get; set; }
    /// <summary>True when another bulk CMDB sync is already in progress.</summary>
    public bool AlreadyRunning { get; set; }

    public IEnumerable<FipsCmdbSyncedProduct> NewProductsNeedingInfo =>
        CreatedProducts.Where(p => p.Status == CMDBProductStatus.New);
}

public sealed class FipsCmdbSingleProductSyncResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public bool StatusSetByRule { get; init; }
}

public interface IFipsCmdbProductSyncService
{
    /// <summary>
    /// Imports active CMDB service offerings into <see cref="CMDBProduct"/> rows (create when no local match, otherwise update).
    /// Updates title, CMDB description, and CMDB-sourced contacts only; does not change categories, phase, or other Compass-only fields on existing rows.
    /// Inactive (retired) Compass products are skipped. A JSON snapshot of each CMDB row is stored on the product; optional rules may set status to Rejected or Inactive.
    /// </summary>
    Task<FipsCmdbProductSyncResult> SyncActiveServiceOfferingsAsync(
        string triggeredByEmail,
        CancellationToken cancellationToken = default,
        Func<FipsCmdbSyncProgressUpdate, ValueTask>? reportProgress = null);

    /// <summary>
    /// Fetches one CMDB row by <see cref="CMDBProduct.CMDBID"/> and applies the same update as the bulk sync.
    /// Skips when the product is <see cref="CMDBProductStatus.Inactive"/> (same as bulk behaviour).
    /// </summary>
    Task<FipsCmdbSingleProductSyncResult> SyncSingleProductAsync(Guid compassProductId, string triggeredByEmail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears Compass-managed register fields on all non-retired products so a subsequent CMDB sync can reapply rules.
    /// Does not change title, CMDB description, contacts, categories, or user description.
    /// </summary>
    Task<FipsCmdbProductResetResult> ResetAllProductsForCmdbResyncAsync(
        string triggeredByEmail,
        CancellationToken cancellationToken = default);

    /// <summary>Most recent bulk CMDB → Compass run, including in-progress.</summary>
    Task<FipsCmdbBulkSyncRunInfo?> GetLastBulkRunAsync(CancellationToken cancellationToken = default);
}

public sealed class FipsCmdbProductResetResult
{
    public int ProductsReset { get; set; }
    public int SkippedInactive { get; set; }
}
