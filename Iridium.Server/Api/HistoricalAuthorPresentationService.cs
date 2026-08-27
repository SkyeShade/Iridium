using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Api;

public sealed class HistoricalAuthorPresentationService(IridiumDbContext db)
{
    public async Task CaptureAsync(ChannelMessage message, Guid communityId, Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var member = await db.CommunityMembers
            .Include(value => value.Account).ThenInclude(value => value.AvatarPresets)
            .Include(value => value.ProfilePreset).ThenInclude(value => value!.AvatarPreset)
            .SingleAsync(value => value.CommunityId == communityId && value.AccountId == accountId,
                cancellationToken);
        var avatar = ChannelMessageMapper.ResolveMessageAuthorAvatarPreset(member);

        message.AuthorDisplayNameSnapshot = ChannelMessageMapper.ResolveMessageAuthorDisplayName(member);
        if (avatar is null) return;
        message.AuthorAvatarObjectKeySnapshot = avatar.ProcessedObjectKey ?? avatar.OriginalObjectKey;
        message.AuthorAvatarContentTypeSnapshot = avatar.ContentType;
        message.AuthorAvatarWidthSnapshot = avatar.Width;
        message.AuthorAvatarHeightSnapshot = avatar.Height;
        message.AuthorAvatarCropXSnapshot = avatar.CropX;
        message.AuthorAvatarCropYSnapshot = avatar.CropY;
        message.AuthorAvatarZoomSnapshot = avatar.Zoom;
        message.AuthorAvatarRevisionSnapshot = avatar.Revision;
    }
}
