using Iridium.Protocol;
using Iridium.UI;

namespace Iridium.Tests;

public sealed class OptimisticMessageGroupingTests
{
    [Fact]
    public void DefaultAndFallbackPresentationsUseSelectedPfpImageRevisionInsteadOfAccountRevision()
    {
        var accountId = Guid.NewGuid();
        var selectedPfp = AccountPfp(Guid.NewGuid(), revision: 4);
        var defaultMember = Member(accountId, profilePresetId: null, displayName: "Skye", accountRevision: 21);
        var fallbackMember = Member(accountId, profilePresetId: Guid.NewGuid(), displayName: "Character Skye",
            accountRevision: 21);

        var defaultPresentation = OptimisticMessageGrouping.PresentationFor(defaultMember, selectedPfp);
        var fallbackPresentation = OptimisticMessageGrouping.PresentationFor(fallbackMember, selectedPfp);

        Assert.Null(defaultPresentation.ProfilePresetId);
        Assert.Equal(selectedPfp.Id, defaultPresentation.AvatarPresetId);
        Assert.Equal(4, defaultPresentation.AvatarRevision);
        Assert.Equal(fallbackMember.ProfilePresetId, fallbackPresentation.ProfilePresetId);
        Assert.Equal(selectedPfp.Id, fallbackPresentation.AvatarPresetId);
        Assert.Equal(4, fallbackPresentation.AvatarRevision);
    }

    [Fact]
    public void MatchingConfirmedPresentationGroupsPendingCommunityMessageImmediately()
    {
        var authorId = Guid.NewGuid();
        var confirmed = Message(authorId, At, "Aria", 7, historical: true);
        var pending = Message(authorId, At.AddSeconds(5), "Aria", 7, pending: true);
        var presentation = Presentation(Guid.NewGuid(), "Aria", Guid.NewGuid(), 7);
        var presentations = Presentations((confirmed, presentation), (pending, presentation));

        Assert.False(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending, presentations));
    }

    [Fact]
    public void CanonicalReplacementReturnsToCanonicalGroupingAndRemainsCompact()
    {
        var authorId = Guid.NewGuid();
        var confirmed = Message(authorId, At, "Aria", 7, historical: true);
        var pending = Message(authorId, At.AddSeconds(5), "Aria", 7, pending: true);
        var presentation = Presentation(Guid.NewGuid(), "Aria", Guid.NewGuid(), 7);
        var presentations = Presentations((confirmed, presentation), (pending, presentation));
        var canonical = pending with
        {
            Id = Guid.NewGuid(),
            DeliveryState = MessageDeliveryState.Confirmed,
            Author = pending.Author with
            {
                AvatarSnapshotMessageId = Guid.NewGuid(),
                HasHistoricalSnapshot = true
            }
        };

        Assert.False(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending, presentations));
        Assert.False(OptimisticMessageGrouping.StartsNewGroup(confirmed, canonical, presentations));
    }

    [Fact]
    public void ConfirmedRowAfterPendingSiblingUsesTransientIdentityDuringHandoff()
    {
        var authorId = Guid.NewGuid();
        var presentation = Presentation(null, "Skye", Guid.NewGuid(), 2);
        var pending = Message(authorId, At.AddSeconds(2), "Skye", 19, pending: true);
        var confirmed = Confirm(Message(authorId, At.AddSeconds(1), "Skye", 2, pending: true), At.AddSeconds(3));
        var presentations = Presentations((pending, presentation), (confirmed, presentation));

        Assert.False(OptimisticMessageGrouping.StartsNewGroup(pending, confirmed, presentations));
    }

    [Fact]
    public void RapidConfirmationsNeverExposeConfirmedGroupStartForSamePresentation()
    {
        var authorId = Guid.NewGuid();
        var presentation = Presentation(null, "Skye", Guid.NewGuid(), 2);
        var first = Message(authorId, At, "Skye", 2, historical: true);
        var second = Message(authorId, At.AddSeconds(1), "Skye", 19, pending: true);
        var third = Message(authorId, At.AddSeconds(2), "Skye", 19, pending: true);
        var fourth = Message(authorId, At.AddSeconds(3), "Skye", 19, pending: true);
        var presentations = Presentations((first, presentation), (second, presentation),
            (third, presentation), (fourth, presentation));

        AssertEveryFollowingRowGrouped([first, second, third, fourth], presentations);
        second = Confirm(second, At.AddSeconds(4));
        AssertEveryFollowingRowGrouped([first, third, fourth, second], presentations);
        third = Confirm(third, At.AddSeconds(5));
        AssertEveryFollowingRowGrouped([first, fourth, second, third], presentations);
        fourth = Confirm(fourth, At.AddSeconds(6));
        AssertEveryFollowingRowGrouped([first, second, third, fourth], presentations);
    }

    [Theory]
    [InlineData("GM Skye", 7)]
    [InlineData("Aria", 8)]
    public void ChangedCommunityAvatarPresentationStartsPendingGroup(string displayName, long avatarRevision)
    {
        var authorId = Guid.NewGuid();
        var confirmed = Message(authorId, At, "Aria", 7, historical: true);
        var pending = Message(authorId, At.AddSeconds(5), displayName, avatarRevision, pending: true);

        Assert.True(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending));
    }

    [Fact]
    public void SameDefaultProfileGroupsDespiteSnapshotAndAccountRevisionDifference()
    {
        var authorId = Guid.NewGuid();
        var accountAvatarId = Guid.NewGuid();
        var confirmed = Message(authorId, At, "Skye", 2, historical: true);
        var pending = Message(authorId, At.AddSeconds(5), "Skye", 19, pending: true);
        var defaultProfile = Presentation(null, "Skye", accountAvatarId, 2);

        Assert.False(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending,
            Presentations((pending, defaultProfile))));
    }

    [Fact]
    public void CommunityAvatarAccountPfpFallbackUsesSelectedImageRevision()
    {
        var authorId = Guid.NewGuid();
        var communityProfileId = Guid.NewGuid();
        var selectedAccountPfpId = Guid.NewGuid();
        var confirmed = Message(authorId, At, "Character Skye", 4, historical: true);
        var pending = Message(authorId, At.AddSeconds(5), "Character Skye", 21, pending: true);
        var fallbackPresentation = Presentation(communityProfileId, "Character Skye", selectedAccountPfpId, 4);

        Assert.False(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending,
            Presentations((pending, fallbackPresentation))));
    }

    [Fact]
    public void DefaultProfileWithNonOriginalSelectedPfpGroupsThroughAcknowledgement()
    {
        var authorId = Guid.NewGuid();
        var selectedSavedPfpId = Guid.NewGuid();
        var confirmed = Message(authorId, At, "Skye", 44, historical: true);
        var pending = Message(authorId, At.AddSeconds(5), "Skye", 103, pending: true);
        var presentation = Presentation(null, "Skye", selectedSavedPfpId, 44);
        var presentations = Presentations((pending, presentation));

        Assert.False(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending, presentations));
        var acknowledged = ConfirmWithRevision(pending, At.AddSeconds(6), 44);
        Assert.False(OptimisticMessageGrouping.StartsNewGroup(confirmed, acknowledged, presentations));
    }

    [Fact]
    public void GenuineDefaultPfpRevisionChangeStartsPendingGroup()
    {
        var authorId = Guid.NewGuid();
        var confirmed = Message(authorId, At, "Skye", 4, historical: true);
        var pending = Message(authorId, At.AddSeconds(5), "Skye", 22, pending: true);
        var changedPresentation = Presentation(null, "Skye", Guid.NewGuid(), 5);

        Assert.True(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending,
            Presentations((pending, changedPresentation))));
    }

    [Fact]
    public void CommunityAvatarAssignmentChangesAlwaysStartPendingGroup()
    {
        var authorId = Guid.NewGuid();
        var avatarA = Presentation(Guid.NewGuid(), "Skye", Guid.NewGuid(), 1);
        var avatarB = Presentation(Guid.NewGuid(), "Skye", Guid.NewGuid(), 1);
        var defaultProfile = Presentation(null, "Skye", avatarA.AvatarPresetId, 1);
        var confirmed = Message(authorId, At, "Skye", 1, historical: true);
        var pending = Message(authorId, At.AddSeconds(5), "Skye", 1, pending: true);

        Assert.True(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending,
            Presentations((confirmed, avatarA), (pending, avatarB))));
        Assert.True(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending,
            Presentations((confirmed, avatarA), (pending, defaultProfile))));
        Assert.True(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending,
            Presentations((confirmed, defaultProfile), (pending, avatarA))));

        var confirmedAvatarB = Confirm(Message(authorId, At.AddSeconds(4), "Skye", 1, pending: true),
            At.AddSeconds(6));
        Assert.True(OptimisticMessageGrouping.StartsNewGroup(pending, confirmedAvatarB,
            Presentations((pending, avatarA), (confirmedAvatarB, avatarB))));
    }

    [Fact]
    public void DifferentAuthorStartsPendingGroup()
    {
        var confirmed = Message(Guid.NewGuid(), At, "Aria", 7, historical: true);
        var pending = Message(Guid.NewGuid(), At.AddSeconds(5), "Aria", 7, pending: true);

        Assert.True(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending));
    }

    [Fact]
    public void PendingMessageOutsideGroupingWindowStartsGroup()
    {
        var authorId = Guid.NewGuid();
        var confirmed = Message(authorId, At, "Aria", 7, historical: true);
        var pending = Message(authorId, At.AddMinutes(1).AddTicks(1), "Aria", 7, pending: true);

        Assert.True(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending));
    }

    [Fact]
    public void PendingReplyStartsGroup()
    {
        var authorId = Guid.NewGuid();
        var confirmed = Message(authorId, At, "Aria", 7, historical: true);
        var pending = Message(authorId, At.AddSeconds(5), "Aria", 7, pending: true) with
        {
            ReplyTo = new(confirmed.Id, authorId, "Aria", "Earlier", false)
        };

        Assert.True(OptimisticMessageGrouping.StartsNewGroup(confirmed, pending));
    }

    [Fact]
    public void SystemMessageInterruptsPendingGrouping()
    {
        var authorId = Guid.NewGuid();
        var system = Message(authorId, At, "Skye", 12) with { Kind = MessageKind.CallStarted };
        var pending = Message(authorId, At.AddSeconds(5), "Skye", 12, pending: true);

        Assert.True(OptimisticMessageGrouping.StartsNewGroup(system, pending));
    }

    [Fact]
    public void FirstPendingMessageStartsGroup()
    {
        var pending = Message(Guid.NewGuid(), At, "Aria", 7, pending: true);

        Assert.True(OptimisticMessageGrouping.StartsNewGroup(null, pending));
    }

    [Fact]
    public void MatchingLegacyDefaultPresentationGroupsPendingMessage()
    {
        var authorId = Guid.NewGuid();
        var legacy = Message(authorId, At, "Skye", 12);
        var pending = Message(authorId, At.AddSeconds(5), "Skye", 12, pending: true);

        Assert.False(OptimisticMessageGrouping.StartsNewGroup(legacy, pending));
    }

    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-27T12:00:00Z");

    private static CommunityMemberDto Member(Guid accountId, Guid? profilePresetId, string displayName,
        long accountRevision) => new(accountId, "user", displayName, null, null, null, At, false,
        PublicPresence.Online, [], ProfilePresetId: profilePresetId, AvatarRevision: accountRevision);

    private static AccountAvatarPresetDto AccountPfp(Guid id, long revision) => new(id, 1, "avatar", revision,
        "image/webp", 512, 512, 0, 0, 1, At, At);

    private static OptimisticCommunityAuthorPresentation Presentation(Guid? profilePresetId, string displayName,
        Guid? avatarPresetId, long avatarRevision) =>
        new(profilePresetId, displayName, avatarPresetId, avatarRevision);

    private static IReadOnlyDictionary<Guid, OptimisticCommunityAuthorPresentation> Presentations(
        params (ChannelMessageDto Message, OptimisticCommunityAuthorPresentation Presentation)[] values) =>
        values.ToDictionary(value => value.Message.ClientMessageId!.Value, value => value.Presentation);

    private static ChannelMessageDto Confirm(ChannelMessageDto message, DateTimeOffset createdAt) =>
        ConfirmWithRevision(message, createdAt, 2);

    private static ChannelMessageDto ConfirmWithRevision(ChannelMessageDto message, DateTimeOffset createdAt,
        long avatarRevision) => message with
    {
        Id = Guid.NewGuid(),
        CreatedAt = createdAt,
        DeliveryState = MessageDeliveryState.Confirmed,
        Author = message.Author with
        {
            AvatarRevision = avatarRevision,
            AvatarSnapshotMessageId = Guid.NewGuid(),
            HasHistoricalSnapshot = true
        }
    };

    private static void AssertEveryFollowingRowGrouped(IReadOnlyList<ChannelMessageDto> messages,
        IReadOnlyDictionary<Guid, OptimisticCommunityAuthorPresentation> presentations)
    {
        for (var index = 1; index < messages.Count; index++)
            Assert.False(OptimisticMessageGrouping.StartsNewGroup(messages[index - 1], messages[index], presentations));
    }

    private static ChannelMessageDto Message(Guid authorId, DateTimeOffset at, string displayName,
        long avatarRevision, bool historical = false, bool pending = false) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        new(authorId, "user", displayName, AvatarRevision: avatarRevision,
            AvatarSnapshotMessageId: historical ? Guid.NewGuid() : null,
            HasHistoricalSnapshot: historical),
        "hello",
        at,
        null,
        false,
        null,
        ClientMessageId: Guid.NewGuid(),
        DeliveryState: pending ? MessageDeliveryState.Pending : MessageDeliveryState.Confirmed);
}
