using Iridium.Protocol;

namespace Iridium.Server.Voice;

public interface INodeMediaSessionService
{
    bool Enabled { get; }
    string Provider { get; }
    NodeMediaSessionDto CreateDirectCallSession(Guid callId, Guid accountId);
    NodeMediaSessionDto CreateCommunityVoiceSession(Guid communityId, Guid channelId, Guid accountId,
        bool canPublishScreen);
}
