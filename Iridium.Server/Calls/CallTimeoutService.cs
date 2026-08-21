using Iridium.Protocol;
using Iridium.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Iridium.Server.Calls;

public sealed class CallTimeoutService(ICallService calls, IHubContext<ChatHub> hub, ILogger<CallTimeoutService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var call in calls.ExpireRingingCalls())
            {
                logger.LogInformation("Voice call {CallId} timed out while ringing.", call.Id);
                var groups = call.Participants.Select(value => ChatHub.AccountGroup(value.AccountId)).ToArray();
                await hub.Clients.Groups(groups).SendAsync(VoiceCallHubContract.Cancelled,
                    new CallStateEvent(call.Id, CallState.Cancelled, "No answer"), stoppingToken);
            }
            foreach (var call in calls.ExpireAbandonedActiveCalls())
            {
                logger.LogInformation("Voice call {CallId} ended after significant signaling loss.", call.Id);
                var groups = call.Participants.Select(value => ChatHub.AccountGroup(value.AccountId)).ToArray();
                await hub.Clients.Groups(groups).SendAsync(VoiceCallHubContract.Ended,
                    new CallStateEvent(call.Id, CallState.Ended, "Signaling connection lost"), stoppingToken);
            }
        }
    }
}
