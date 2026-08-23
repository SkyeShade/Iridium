using Iridium.Server.Calls;
using Iridium.Server.Persistence;
using Iridium.Server.Security;

namespace Iridium.Server.Api;

public static class WebRtcEndpoints
{
    public static IEndpointRouteBuilder MapWebRtcEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/webrtc/ice-configuration", GetIceConfigurationAsync)
            .WithName("GetWebRtcIceConfiguration");
        return endpoints;
    }

    private static async Task<IResult> GetIceConfigurationAsync(
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions,
        IWebRtcIceConfigurationService configuration)
    {
        var session = await sessions.GetAsync(context, db);
        return session is null ? Results.Unauthorized() : Results.Ok(configuration.Create(session.AccountId));
    }
}
