using Iridium.Client.Core;

namespace Iridium.Tests;

public sealed class SavedNodeStateTests
{
    [Fact]
    public async Task PersistentDefaultIsAddedSelectedAndSaved()
    {
        var store = new MemoryNodeStore();
        var state = new SavedNodeState(store);

        var selected = await state.InitializeAsync(
            new SavedNode("https://iridiumonline.net/", null), persistDefaultNode: true);

        Assert.Equal("https://iridiumonline.net", selected.Address);
        Assert.Same(selected, state.Nodes[0]);
        Assert.Single(state.Nodes);
        Assert.Single(store.Nodes);
    }

    [Fact]
    public async Task ExistingNormalizedDefaultIsNotDuplicatedAndKeepsItsLabel()
    {
        var store = new MemoryNodeStore([
            new SavedNode("HTTPS://CHAT.EXAMPLE.COM/", "My Node"),
            new SavedNode("https://elsewhere.example", "Elsewhere")
        ]);
        var state = new SavedNodeState(store);

        var selected = await state.InitializeAsync(
            new SavedNode("https://chat.example.com", null), persistDefaultNode: true);

        Assert.Equal(2, state.Nodes.Count);
        Assert.Equal("My Node", selected.Label);
        Assert.Equal("https://chat.example.com", selected.Address, ignoreCase: true);
        Assert.Same(selected, state.Nodes[0]);
    }

    [Fact]
    public async Task AddingTrailingSlashVariantReturnsExistingNode()
    {
        var store = new MemoryNodeStore([new SavedNode("https://chat.example.com", "Chat")]);
        var state = new SavedNodeState(store);
        await state.InitializeAsync(new SavedNode("https://current.example", null), persistDefaultNode: true);

        var added = await state.AddAsync("https://chat.example.com/");

        Assert.Equal(2, state.Nodes.Count);
        Assert.Equal("Chat", added.Label);
    }

    [Fact]
    public async Task DevelopmentDefaultRemainsLocalAndIsNotPersisted()
    {
        var store = new MemoryNodeStore();
        var state = new SavedNodeState(store);

        var selected = await state.InitializeAsync(
            new SavedNode("https://localhost:7008", "Local Iridium Node", true));

        Assert.True(selected.IsLocal);
        Assert.Empty(store.Nodes);
    }

    private sealed class MemoryNodeStore(IEnumerable<SavedNode>? initial = null) : ISavedNodeStore
    {
        public IReadOnlyList<SavedNode> Nodes { get; private set; } = initial?.ToArray() ?? [];

        public Task<IReadOnlyList<SavedNode>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Nodes);

        public Task SaveAsync(IReadOnlyList<SavedNode> nodes, CancellationToken cancellationToken = default)
        {
            Nodes = nodes.ToArray();
            return Task.CompletedTask;
        }
    }
}
