using Iridium.Server.Api;
using Iridium.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Communities;

public sealed class CommunityThreadArchiveService(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<CommunityThreadArchiveService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IridiumDbContext>();
                var realtime = scope.ServiceProvider.GetRequiredService<CommunityRealtimePublisher>();
                var changed = await CommunityThreadEndpoints.ArchiveExpiredAsync(db);
                foreach (var communityId in changed)
                    await realtime.PublishAsync(communityId, "threads-auto-archived", db, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Thread auto-archive sweep failed."); }
        }
    }
}
