namespace Iridium.Client.Core;

public readonly record struct MessageDraftScope(
    string NodeAuthority,
    Guid AccountId,
    string ConversationKind,
    Guid ConversationId)
{
    public string StorageKey =>
        $"node:{Uri.EscapeDataString(NodeAuthority.Trim().ToLowerInvariant())}:account:{AccountId:N}:{ConversationKind.Trim().ToLowerInvariant()}:{ConversationId:N}";
}

public sealed record MessageDraftEntry(string Content, long UpdatedAtUnixMilliseconds);

public interface IMessageDraftStore
{
    Task<string?> LoadAsync(MessageDraftScope scope, CancellationToken cancellationToken = default);
    Task SaveAsync(MessageDraftScope scope, string content, CancellationToken cancellationToken = default);
    Task RemoveAsync(MessageDraftScope scope, CancellationToken cancellationToken = default);
}
