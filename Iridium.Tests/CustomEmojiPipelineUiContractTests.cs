namespace Iridium.Tests;

public sealed class CustomEmojiPipelineUiContractTests
{
    [Fact]
    public void ForumPickerUsesAccountMembershipProviderAndPermissionPolicy()
    {
        var settings = Source("Iridium.Web", "Components", "ForumTagSettings.razor");
        var picker = Source("Iridium.Web", "Components", "EmojiPicker.razor");
        var service = Source("Iridium.Client.Core", "CommunityEmojiService.cs");

        Assert.Contains("Emojis.GetAvailableAsync(Community)", settings);
        Assert.Contains("AllowExternalEmoji=\"AllowExternalEmoji\"", settings);
        Assert.Contains("Emojis.GetAvailableAsync(Community)", picker);
        Assert.Contains("_session.Communities", service);
        Assert.Contains("membershipsChanged", service);
        Assert.Contains("_nodeAddress", service);
        Assert.Contains("Changed?.Invoke(Guid.Empty)", service);
        Assert.Contains("DistinctBy(value => value.Id)", service);
    }

    [Fact]
    public void PickerAndAutocompleteConvergeOnStableComposerTokenAndPermissionFilteredSources()
    {
        var composer = Source("Iridium.Web", "Components", "MessageComposer.razor");

        Assert.Contains("new ComposerEmojiToken(0, emojiId, selection.Name, communityId", composer);
        Assert.Contains("new ComposerEmojiToken(0, source.Emoji.Id, source.Emoji.Name, source.Community.Id", composer);
        Assert.Contains("AllowExternalEmoji=\"CanUseExternalEmoji\"", composer);
        Assert.Contains("RebuildEmojiIndex()", composer);
    }

    [Fact]
    public void SuccessfulSendClearsCustomReferencesBeforeAwaitingRenderCallbacks()
    {
        var composer = Source("Iridium.Web", "Components", "MessageComposer.razor");
        var contentClear = composer.IndexOf("_content = string.Empty;", StringComparison.Ordinal);
        var referenceClear = composer.IndexOf("_emojiReferences.Clear();", contentClear, StringComparison.Ordinal);
        var callback = composer.IndexOf("await TypingActivityChanged.InvokeAsync(false);", contentClear,
            StringComparison.Ordinal);

        Assert.True(contentClear >= 0);
        Assert.True(referenceClear > contentClear);
        Assert.True(callback > referenceClear);
    }

    [Fact]
    public void ServerValidatesStableCustomEmojiIdentityMembershipAndExternalPermission()
    {
        var hub = Source("Iridium.Server", "Hubs", "ChatHub.cs");

        Assert.Contains("CommunityEmojiNames.References(content)", hub);
        Assert.Contains("authorization.IsMemberAsync(emoji.CommunityId, accountId, db)", hub);
        Assert.Contains("CommunityPermission.UseExternalEmoji", hub);
        Assert.Contains("ValidateCommunityEmojiUseAsync(content, session.AccountId, communityId, channelId)", hub);
        Assert.Contains("ValidateCommunityEmojiUseAsync(content, session.AccountId)", hub);
    }

    private static string Source(params string[] segments)
    {
        var root = RepositoryRoot();
        return File.ReadAllText(Path.Combine([root, .. segments]));
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, ".."));
}
