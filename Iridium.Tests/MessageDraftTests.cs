using Iridium.Client.Core;

namespace Iridium.Tests;

public sealed class MessageDraftTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public async Task ChannelDraftsRestoreIndependentlyAfterSwitchingAwayAndBack()
    {
        var store = new MemoryDraftStore();
        var account = Guid.NewGuid();
        var channelA = Scope("alpha.example", account, "channel", Guid.NewGuid());
        var channelB = Scope("alpha.example", account, "channel", Guid.NewGuid());

        await store.SaveAsync(channelA, "need to finish this later");
        await store.SaveAsync(channelB, "random thought");

        Assert.Equal("random thought", await store.LoadAsync(channelB));
        Assert.Equal("need to finish this later", await store.LoadAsync(channelA));
    }

    [Fact]
    public async Task DirectMessageDraftsRestoreIndependentlyAfterSwitchingAwayAndBack()
    {
        var store = new MemoryDraftStore();
        var account = Guid.NewGuid();
        var alice = Scope("alpha.example", account, "direct", Guid.NewGuid());
        var bob = Scope("alpha.example", account, "direct", Guid.NewGuid());

        await store.SaveAsync(alice, "hey are you free");
        await store.SaveAsync(bob, "hello Bob");

        Assert.Equal("hello Bob", await store.LoadAsync(bob));
        Assert.Equal("hey are you free", await store.LoadAsync(alice));
    }

    [Fact]
    public async Task ScopeSeparatesKindsNodesAndAccountsEvenWhenConversationIdsOverlap()
    {
        var conversationId = Guid.NewGuid();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var channel = Scope("alpha.example", accountA, "channel", conversationId);
        var direct = Scope("alpha.example", accountA, "direct", conversationId);
        var otherNode = Scope("beta.example", accountA, "channel", conversationId);
        var otherAccount = Scope("alpha.example", accountB, "channel", conversationId);

        Assert.Equal(4, new[] { channel.StorageKey, direct.StorageKey, otherNode.StorageKey, otherAccount.StorageKey }
            .Distinct(StringComparer.Ordinal).Count());
        Assert.Contains($"account:{accountA:N}", channel.StorageKey);
        Assert.Contains($"channel:{conversationId:N}", channel.StorageKey);

        var store = new MemoryDraftStore();
        await store.SaveAsync(channel, "channel on alpha for A");
        await store.SaveAsync(direct, "DM on alpha for A");
        await store.SaveAsync(otherNode, "channel on beta for A");
        await store.SaveAsync(otherAccount, "channel on alpha for B");
        Assert.Equal("channel on alpha for A", await store.LoadAsync(channel));
        Assert.Equal("DM on alpha for A", await store.LoadAsync(direct));
        Assert.Equal("channel on beta for A", await store.LoadAsync(otherNode));
        Assert.Equal("channel on alpha for B", await store.LoadAsync(otherAccount));
    }

    [Fact]
    public async Task EmptyDraftRemovesStoredEntryAndMarkdownRoundTripsExactly()
    {
        var store = new MemoryDraftStore();
        var scope = Scope("alpha.example", Guid.NewGuid(), "channel", Guid.NewGuid());
        const string markdown = "*`test`*\n**bold** ~~strike~~";

        await store.SaveAsync(scope, markdown);
        Assert.Equal(markdown, await store.LoadAsync(scope));
        await store.SaveAsync(scope, "  \r\n");
        Assert.Null(await store.LoadAsync(scope));
    }

    [Fact]
    public void DraftSourceSerializerPreservesMarkdownAndRestoresVisibleEmojiSource()
    {
        var emojiId = Guid.NewGuid();
        var communityId = Guid.NewGuid();
        var document = $"*`test`* {CommunityEmojiDraftCodec.ObjectReplacementCharacter} {CommunityEmojiDraftCodec.ObjectReplacementCharacter}";
        var customStart = document.IndexOf(CommunityEmojiDraftCodec.ObjectReplacementCharacter);
        var standardStart = document.LastIndexOf(CommunityEmojiDraftCodec.ObjectReplacementCharacter);

        var source = CommunityEmojiDraftCodec.SerializeDraftSource(document,
            [new(customStart, 1, emojiId, "wave", communityId)],
            [new(standardStart, 1, "wave", "👋", "wave")]);

        Assert.Equal("*`test`* :wave: 👋", source);
    }

    [Fact]
    public void EmptyContentEditablePlaceholderCanonicalizesToEmptyComposerSource()
    {
        var chat = Source("Iridium.Web", "wwwroot", "js", "chat.js");
        var composer = Source("Iridium.Web", "Components", "MessageComposer.razor");

        Assert.Contains("node.nodeName === \"BR\"", chat);
        Assert.Contains("const placeholderOnly = result.tokens.length === 0", chat);
        Assert.Contains("result.content = \"\"", chat);
        Assert.Contains("result.caret = 0", chat);
        Assert.Contains("composerPlaceholderCharacters", chat);
        Assert.Contains("private int RemainingCharacters => _maxMessageCharacters - CommunityEmojiDraftCodec.CountCharacters(_content", composer);
        Assert.Contains("if (string.IsNullOrWhiteSpace(source)) await Drafts.RemoveAsync", composer);
    }

    [Fact]
    public void ComposerDebouncesRestoresBeforeFocusAndClearsOnlyInsideSuccessfulSend()
    {
        var composer = Source("Iridium.Web", "Components", "MessageComposer.razor");
        var focus = Slice(composer, "public async Task FocusAsync()", "public async Task InsertMentionAsync");
        var submit = Slice(composer, "public async Task SubmitFromKeyboardAsync()", "private async Task FilesSelectedAsync");

        Assert.Contains("TimeSpan.FromMilliseconds(300)", composer);
        Assert.True(focus.IndexOf("await EnsureDraftLoadedAsync()", StringComparison.Ordinal) <
                    focus.IndexOf("focusComposer", StringComparison.Ordinal));
        var success = submit.IndexOf("if (succeeded)", StringComparison.Ordinal);
        var remove = submit.IndexOf("await RemoveStoredDraftAsync()", StringComparison.Ordinal);
        Assert.True(success >= 0 && remove > success);
        Assert.DoesNotContain("RemoveStoredDraftAsync", submit[..success]);
        Assert.Contains("catch (Exception exception)", submit);
    }

    [Fact]
    public void DraftStorageIsOneClientLocalNamespaceAndNeverAppearsInServerCode()
    {
        var storage = Source("Iridium.Web", "Services", "BrowserClientStorage.cs");
        Assert.Contains("iridium.messageDrafts.v1", storage);
        Assert.Contains("MaximumMessageDrafts = 500", storage);
        Assert.Contains("loadValue", storage);
        Assert.Contains("string.IsNullOrWhiteSpace(content)", storage);

        var serverSources = Directory.EnumerateFiles(Path.Combine(Root, "Iridium.Server"), "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        Assert.DoesNotContain(serverSources.Select(File.ReadAllText), source =>
            source.Contains("messageDraft", StringComparison.OrdinalIgnoreCase));
    }

    private static MessageDraftScope Scope(string node, Guid account, string kind, Guid conversation) =>
        new(node, account, kind, conversation);

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from);
        return source[from..to];
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private sealed class MemoryDraftStore : IMessageDraftStore
    {
        private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);

        public Task<string?> LoadAsync(MessageDraftScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(_drafts.GetValueOrDefault(scope.StorageKey));

        public Task SaveAsync(MessageDraftScope scope, string content, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(content)) _drafts.Remove(scope.StorageKey);
            else _drafts[scope.StorageKey] = content;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(MessageDraftScope scope, CancellationToken cancellationToken = default)
        {
            _drafts.Remove(scope.StorageKey);
            return Task.CompletedTask;
        }
    }
}
