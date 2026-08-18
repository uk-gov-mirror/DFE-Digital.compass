namespace Compass.Models.Fips;

/// <summary>Constants for <see cref="FipsSyncHistory"/> rows written by Compass service-register CMDB sync.</summary>
public static class FipsCmdbCompassSyncHistory
{
    public const string SyncType = "CMDB to Compass";
    public const string SourceEnvironment = "CMDB";
    public const string TargetEnvironment = "Compass";
    public const string ScheduledInitiatedBy = "Scheduled daily job";
    public const string StatusRunning = "Running";
    public const string StatusCompleted = "Completed";
    public const string StatusFailed = "Failed";
}
