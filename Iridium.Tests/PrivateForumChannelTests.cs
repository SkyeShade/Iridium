namespace Iridium.Tests;

public sealed class PrivateForumChannelTests
{
    [Fact]
    public void ForumOverviewReusesThePrivateChannelSwitchAndAccessEditor()
    {
        var editor = Source("Iridium.Web", "Components", "CommunityPermissionEditor.razor");
        var overview = Slice(editor, "@if (_section == SettingsSection.Overview)",
            "else if (_section == SettingsSection.Permissions)");

        Assert.Contains("<strong>Private Channel</strong>", overview);
        Assert.Contains("Only selected members and roles will be able to view this channel.", overview);
        Assert.Contains("<SettingsSwitch Value=\"_private\"", overview);
        Assert.Contains("Add members or roles", overview);
        Assert.Contains("PrivateAccessEntries", overview);
        Assert.DoesNotContain("type=\"checkbox\"", overview);
        Assert.DoesNotContain("IsPrivateForum", editor);
        Assert.DoesNotContain("ForumVisibility", editor);
    }

    [Fact]
    public void ForumApisAndRealtimeUseTheCanonicalViewChannelsResolver()
    {
        var forums = Source("Iridium.Server", "Api", "CommunityForumEndpoints.cs");
        var tags = Source("Iridium.Server", "Api", "CommunityForumTagEndpoints.cs");
        var authorization = Source("Iridium.Server", "Security", "CommunityAuthorizationService.cs");
        var structure = Source("Iridium.Server", "Api", "CommunityStructureEndpoints.cs");
        var search = Source("Iridium.Server", "Api", "MessageEndpoints.cs");
        var hub = Source("Iridium.Server", "Hubs", "ChatHub.cs");

        Assert.Contains("IsForumVisibleAsync", forums);
        Assert.Contains("if (!access.Has(CommunityPermission.ViewChannels)) return Results.NotFound();", forums);
        Assert.Contains("HasChannelPermissionAsync(communityId, channelId, accountId", forums);
        Assert.Contains("AccessAsync(communityId, channelId", tags);
        Assert.Contains("CommunityPermission.ViewChannels", tags);
        Assert.Contains("value.ParentForumChannelId", authorization);
        Assert.Contains("permissionChannelId = forumChannelId", authorization);
        Assert.Contains("value.PermissionsSyncedToCategory", authorization);
        Assert.Contains("if (!channelAccess.Has(CommunityPermission.ViewChannels)) continue;", structure);
        Assert.DoesNotContain("entity.Kind != CommunityChannelKind.Forum", structure);
        Assert.Contains("AccessibleTextChannelIdsAsync", search);
        Assert.Contains("authorization.GetChannelAccessAsync(communityId, id, accountId, db)", search);
        Assert.Contains("PublishForumPostAsync", hub);
        Assert.Contains("authorization.HasChannelPermissionAsync(post.CommunityId, post.ForumChannelId", hub);
    }

    [Fact]
    public void ForumPrivacyUsesExistingOverwriteProtocolWithoutNewSchema()
    {
        var domain = Source("Iridium.Server", "Domain", "CommunityChannel.cs");
        var context = Source("Iridium.Server", "Persistence", "IridiumDbContext.cs");
        Assert.Contains("PermissionsSyncedToCategory", domain);
        Assert.Contains("CommunityPermissionOverwrites", context);
        Assert.DoesNotContain("IsPrivateForum", domain);
        Assert.DoesNotContain("ForumVisibility", domain);
        Assert.DoesNotContain("ForumPermissionOverwrite", context);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        return source[startIndex..endIndex];
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")), Path.Combine(parts)));
}
