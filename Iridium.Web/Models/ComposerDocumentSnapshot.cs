namespace Iridium.Web.Models;

public sealed record ComposerEmojiToken(int Start, Guid? EmojiId, string Name, Guid? CommunityId,
    string? MediaUrl = null, int Width = 1, int Height = 1, string? StandardArtworkKey = null,
    string? Glyph = null);

public sealed record ComposerDocumentSnapshot(string? Content, int Caret, IReadOnlyList<ComposerEmojiToken> Tokens);
