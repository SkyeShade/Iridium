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
            .Include(value => value.Account)
            .Include(value => value.ProfilePreset).ThenInclude(value => value!.AvatarPreset)
            .SingleAsync(value => value.CommunityId == communityId && value.AccountId == accountId,
                cancellationToken);
        var profile = ChannelMessageMapper.ValidPreset(member);
        var avatar = profile?.AvatarPreset;
        if (avatar is null && member.Account.ActiveAvatarPresetId is { } activeAvatarId)
            avatar = await db.AccountAvatarPresets.SingleOrDefaultAsync(value =>
                value.Id == activeAvatarId && value.AccountId == accountId, cancellationToken);

        message.AuthorDisplayNameSnapshot = ChannelMessageMapper.ResolveDisplayName(member);
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
