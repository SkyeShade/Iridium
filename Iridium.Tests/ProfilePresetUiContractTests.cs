namespace Iridium.Tests;

public sealed class ProfilePresetUiContractTests
{
    [Fact]
    public void AvatarsUseOwnCommunityProfilePopupAndDynamicManagementList()
    {
        var root = WorkspaceRoot();
        var manager = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "AvatarManagerModal.razor"));
        var imageEditor = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "AvatarEditorModal.razor"));
        var singleImageEditor = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "SinglePfpEditor.razor"));
        var profile = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "AnchoredProfileCard.razor"));
        var home = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Pages", "Home.razor"));
        var channelMapper = File.ReadAllText(Path.Combine(root, "Iridium.Server", "Api", "ChannelMessageMapper.cs"));
        var directMapper = File.ReadAllText(Path.Combine(root, "Iridium.Server", "Api", "DirectMessageMapper.cs"));

        Assert.Contains("@foreach (var preset in _presets)", manager);
        Assert.Contains("Add Avatar", manager);
        Assert.Contains("Using default profile picture", manager);
        Assert.Contains("class=\"avatar-pfp-trigger\"", manager);
        Assert.Contains("aria-label=\"Edit avatar profile picture\"", manager);
        Assert.Contains("ProfilePresetTargetId=\"pfpTarget.Id\"", manager);
        Assert.Contains("ProfilePresetCommunityId=\"CommunityId\"", manager);
        Assert.Contains("GetProfilePresetsAsync(CommunityId)", manager);
        Assert.Contains("Avatars for @CommunityName", manager);
        Assert.DoesNotContain("Upload PFP", manager);
        Assert.Contains("@if (!SinglePictureMode)", imageEditor);
        Assert.Contains("<SinglePfpEditor", imageEditor);
        Assert.Contains("Remove Custom PFP", imageEditor);
        Assert.Contains("ClearProfilePresetAvatarAsync", imageEditor);
        Assert.Contains("!SinglePictureMode", imageEditor);
        Assert.Contains("@for (var slot = 0; slot < ProfileAvatarLimits.MaximumPresets; slot++)", imageEditor);
        Assert.Contains("class=\"crop-stage", singleImageEditor);
        Assert.Contains("class=\"crop-shade\"", singleImageEditor);
        Assert.Contains("capturePointer", singleImageEditor);
        Assert.Contains("State.Pan", singleImageEditor);
        Assert.Contains("State.SetZoom", singleImageEditor);
        Assert.Contains("opacity:0", singleImageEditor);
        Assert.DoesNotContain("ProfileAvatarLimits.MaximumPresets", singleImageEditor);
        Assert.Contains("Choose Community Avatar", profile);
        Assert.Contains("Default Profile", profile);
        Assert.Contains("SetCommunityProfileAsync", profile);
        Assert.Contains("GetProfilePresetsAsync(CommunityContext!.Community.Id)", profile);
        Assert.Contains("OnEditAvatars", profile);
        Assert.DoesNotContain("Community Profile", home);
        Assert.Contains("ResolveDisplayName", channelMapper);
        Assert.Contains("member.Nickname", channelMapper);
        Assert.DoesNotContain("ProfilePreset", directMapper);
    }

    [Fact]
    public void HistoryViewsPassPinnedIntentAndAuthoritativeReconciliationRevision()
    {
        var root = WorkspaceRoot();
        foreach (var file in new[] { "ChannelView.razor", "DirectMessageView.razor" })
        {
            var source = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", file));
            Assert.Contains("ReconciliationRevision=\"Messaging.RecentReconciliationRevision\"", source);
            Assert.Contains("FollowLatest=\"_isPinnedToLatest\"", source);
        }
    }

    private static string WorkspaceRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Iridium.sln"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the Iridium workspace.");
    }
}
