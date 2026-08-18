using Compass.Helpers;
using Compass.Models.Fips;
using Compass.Services.Fips;
using Microsoft.Extensions.Options;

namespace Compass.Services;

/// <summary>Runs bulk CMDB → service register sync once per UK day and emails a summary.</summary>
public sealed class FipsCmdbDailySyncHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<FipsSyncConfiguration> _options;
    private readonly ILogger<FipsCmdbDailySyncHostedService> _logger;

    public FipsCmdbDailySyncHostedService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<FipsSyncConfiguration> options,
        ILogger<FipsCmdbDailySyncHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (UkDateTime.Now().TimeOfDay >= GetRunAtUkTimeOfDay())
            await RunJobSafelyAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextUkRunTime();
            var nextRunUk = UkDateTime.Now().Add(delay);
            _logger.LogInformation(
                "CMDB daily sync next run at {NextRunUk:yyyy-MM-dd HH:mm} UK (in {Delay})",
                nextRunUk,
                delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await RunJobSafelyAsync(stoppingToken);
        }
    }

    private async Task RunJobSafelyAsync(CancellationToken stoppingToken)
    {
        FipsCmdbDailySyncOutcome outcome;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IFipsCmdbDailySyncService>();
            outcome = await service.RunDailySyncIfDueAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled daily CMDB sync job failed");
            return;
        }

        if (outcome != FipsCmdbDailySyncOutcome.Busy)
            return;

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IFipsCmdbDailySyncService>();
            await service.RunDailySyncIfDueAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled daily CMDB sync retry after busy lock failed");
        }
    }

    private TimeSpan GetRunAtUkTimeOfDay()
    {
        var hour = _options.CurrentValue.DailySyncHourUk;
        if (hour is < 0 or > 23)
            hour = 6;
        return TimeSpan.FromHours(hour);
    }

    private TimeSpan GetDelayUntilNextUkRunTime()
    {
        var nowUk = UkDateTime.Now();
        var nextRunUk = nowUk.Date + GetRunAtUkTimeOfDay();
        if (nowUk >= nextRunUk)
            nextRunUk = nextRunUk.AddDays(1);

        var nextRunUtc = UkDateTime.ToUtc(nextRunUk);
        var delay = nextRunUtc - DateTime.UtcNow;
        return delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(1);
    }
}
