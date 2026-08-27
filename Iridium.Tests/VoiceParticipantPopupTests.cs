namespace Iridium.Tests;

public sealed class VoiceParticipantPopupTests
{
    [Fact]
    public void SharedParticipantPopupUsesBaseIdentityWithoutPresenceAndKeepsAccessibleControls()
    {
        var menu = Source("Iridium.UI", "VoiceParticipantMenu.razor");
        var css = Source("Iridium.UI", "VoiceParticipantMenu.razor.css");

        Assert.Contains("AccountId=\"@AccountId\" AvatarRevision=\"@AvatarRevision\" DisplayName=\"@DisplayName\"", menu);
        Assert.Contains("BadgeMode=\"AvatarBadgeMode.None\" Tooltip=\"@DisplayName\"", menu);
        Assert.Contains("IridiumIdentity.Format(Username, authority)", menu);
        Assert.DoesNotContain("AvatarBadgeMode.PresenceGlyph", menu);
        Assert.DoesNotContain("voice-participant-presence", menu);
        Assert.DoesNotContain("PresenceLabel", menu);
        Assert.Contains("aria-label=\"Close participant controls\"", menu);
        Assert.Contains("args.Key == \"Escape\" ? CloseAsync()", menu);
        Assert.Contains("class=\"voice-user-popup-layer\" @onclick=\"CloseAsync\"", menu);
        Assert.Contains("VoiceParticipantPreference.MinimumVolumePercent", menu);
        Assert.Contains("VoiceParticipantPreference.MaximumVolumePercent", menu);
        Assert.Contains("Preferences.SetLocallyMutedAsync(AccountId, muted)", menu);
        Assert.Contains("Preferences.SetVolumeAsync(AccountId, value)", menu);
        Assert.DoesNotContain("DisplayName=\"DisplayName\"", menu);
        Assert.DoesNotContain("accent-color", css);
        Assert.Contains(".iridium-switch input:checked + span", css);
        Assert.Contains("appearance: none", css);
        Assert.Contains("::-webkit-slider-runnable-track", css);
        Assert.Contains("::-moz-range-progress", css);
        Assert.Contains("text-overflow: ellipsis", css);
    }

    [Fact]
    public void DmAndCommunityCallSitesUseTheSamePopupWithRealIdentityBindings()
    {
        var direct = Source("Iridium.Web", "Components", "DirectVoiceCallStage.razor");
        var channel = Source("Iridium.UI", "ChannelRow.razor");
        var sidebar = Source("Iridium.Web", "Components", "CommunitySidebar.razor");
        var category = Source("Iridium.Web", "Components", "CommunityCategoryTreeNode.razor");

        Assert.Contains("<VoiceParticipantMenu", direct);
        Assert.Contains("DisplayName=\"@Participant.DisplayName\" Username=\"@Participant.Username\"", direct);
        Assert.Contains("OnClose=\"CloseParticipantMenu\"", direct);
        var directMenu = ComponentInvocation(direct, "<VoiceParticipantMenu");
        Assert.DoesNotContain("DisplayName=\"callParticipant.DisplayName\"", directMenu);

        Assert.Contains("var member = Members.FirstOrDefault", channel);
        Assert.Contains("DisplayName=\"@(member?.DisplayName ?? participant.DisplayName)\"", channel);
        Assert.Contains("Username=\"@(member?.Username ?? participant.Username)\"", channel);
        Assert.Contains("AvatarRevision=\"@(member?.AvatarRevision ?? participant.AvatarRevision)\"", channel);
        var communityMenu = ComponentInvocation(channel, "<VoiceParticipantMenu");
        Assert.DoesNotContain("DisplayName=\"participant.DisplayName\"", communityMenu);
        Assert.Contains("Members=\"@(State.Management?.Members ?? [])\"", sidebar);
        Assert.Contains("Members=\"Members\"", category);
    }

    [Fact]
    public void ActiveVoiceAndStreamAvatarsSuppressPresenceWithoutRemovingSpeakingState()
    {
        var direct = Source("Iridium.Web", "Components", "DirectVoiceCallStage.razor");
        var channel = Source("Iridium.UI", "ChannelRow.razor");
        var viewer = Source("Iridium.Web", "Components", "VoiceStreamViewer.razor");
        var normalDm = Source("Iridium.Web", "Components", "DirectMessageView.razor");
        var normalMembers = Source("Iridium.Web", "Components", "CommunityMemberSidebar.razor");

        Assert.Equal(Count(direct, "<ProfileAvatar"), Count(direct, "BadgeMode=\"AvatarBadgeMode.None\""));
        Assert.Equal(Count(channel, "<ProfileAvatar"), Count(channel, "BadgeMode=\"AvatarBadgeMode.None\""));
        Assert.Equal(Count(viewer, "<ProfileAvatar"), Count(viewer, "BadgeMode=\"AvatarBadgeMode.None\""));
        Assert.Contains("callParticipant.IsSpeaking ? \"speaking\"", direct);
        Assert.Contains("participant.Speaking ? \"speaking\"", channel);
        Assert.Contains("participant.IsSpeaking ? \"speaking\"", viewer);
        Assert.Contains("participant.Speaking ? \"speaking\"", viewer);

        Assert.Contains("Presence=\"@Conversation.OtherParticipant.Presence\"", normalDm);
        Assert.Contains("Presence=\"@member.Presence\"", normalMembers);
        Assert.DoesNotContain("AvatarBadgeMode.None", normalDm);
        Assert.DoesNotContain("AvatarBadgeMode.None", normalMembers);
    }

    private static string Source(params string[] parts)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine([root, .. parts]));
    }

    private static string ComponentInvocation(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        var end = source.IndexOf("/>", start, StringComparison.Ordinal);
        return source[start..(end + 2)];
    }

    private static int Count(string source, string value) =>
        (source.Length - source.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
}
