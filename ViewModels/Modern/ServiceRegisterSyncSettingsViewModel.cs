using Compass.Models.Fips;
using Compass.Services.Fips;

namespace Compass.ViewModels.Modern;

public sealed class ServiceRegisterSyncSettingsViewModel
{
    public List<FipsCmdbSyncRule> Rules { get; init; } = [];

    /// <summary>Tab counts and active tab for service register sub-navigation.</summary>
    public FipsProductsViewModel SubNav { get; init; } = new() { ActiveTab = "sync" };

    public bool CanSyncFromCmdb { get; init; }

    public FipsCompletionImportResult? LastImportResult { get; init; }

    public FipsCompletionImportResult? LastStrapiImportResult { get; init; }

    public string? LastBulkSyncAtDisplay { get; init; }

    public string? LastBulkSyncByDisplay { get; init; }

    public string? LastBulkSyncStatusDisplay { get; init; }

    public bool DailySyncEnabled { get; init; }

    public string? DailySyncScheduleDisplay { get; init; }

    public string? DailySyncSummaryEmail { get; init; }
}
