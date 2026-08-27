namespace Iridium.Tests;

public sealed class VoiceParticipantPopupTests
{
    [Fact]
    public void SharedParticipantPopupUsesBaseIdentityPresenceAndAccessibleControls()
    {
        var menu = Source("Iridium.UI", "VoiceParticipantMenu.razor");
        var css = Source("Iridium.UI", "VoiceParticipantMenu.razor.css");

        Assert.Contains("AccountId=\"@AccountId\" AvatarRevision=\"@AvatarRevision\" DisplayName=\"@DisplayName\"", menu);
        Assert.Contains("Presence=\"@Presence\" Size=\"small\"", menu);
        Assert.Contains("IridiumIdentity.Format(Username, authority)", menu);
        Assert.Contains("BadgeMode=\"AvatarBadgeMode.PresenceGlyph\"", menu);
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
        Assert.Contains("Presence=\"@Participant.Presence\"", direct);
        Assert.Contains("OnClose=\"CloseParticipantMenu\"", direct);
        var directMenu = ComponentInvocation(direct, "<VoiceParticipantMenu");
        Assert.DoesNotContain("DisplayName=\"callParticipant.DisplayName\"", directMenu);

        Assert.Contains("var member = Members.FirstOrDefault", channel);
        Assert.Contains("DisplayName=\"@(member?.DisplayName ?? participant.DisplayName)\"", channel);
        Assert.Contains("Username=\"@(member?.Username ?? participant.Username)\"", channel);
        Assert.Contains("Presence=\"@(member?.Presence ?? participant.Presence)\"", channel);
        Assert.Contains("AvatarRevision=\"@(member?.AvatarRevision ?? participant.AvatarRevision)\"", channel);
        var communityMenu = ComponentInvocation(channel, "<VoiceParticipantMenu");
        Assert.DoesNotContain("DisplayName=\"participant.DisplayName\"", communityMenu);
        Assert.Contains("Members=\"@(State.Management?.Members ?? [])\"", sidebar);
        Assert.Contains("Members=\"Members\"", category);
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
}
