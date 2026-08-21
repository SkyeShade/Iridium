using Iridium.Protocol;
using Microsoft.Extensions.Options;

namespace Iridium.Server.Configuration;

public interface ICommunityLimitsService
{
    CommunityLimitsDto GetEffectiveLimits(Guid? communityId = null);
}

public sealed class CommunityLimitsService(IOptions<NodeOptions> options) : ICommunityLimitsService
{
    public CommunityLimitsDto GetEffectiveLimits(Guid? communityId = null)
    {
        // communityId is intentionally part of this boundary so future entitlement overrides
        // can be resolved here without changing message or settings UI code.
        _ = communityId;
        return new(options.Value.MaxMessageCharacters, options.Value.MaxAttachmentBytes,
            options.Value.MaxAttachmentsPerMessage);
    }
}
