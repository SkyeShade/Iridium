using Iridium.Protocol;

namespace Iridium.Server.Domain;

public sealed class MessageReaction
{
    public Guid MessageId { get; set; }
    public Guid AccountId { get; set; }
    public required string EmojiKey { get; set; }
    public ReactionEmojiKind EmojiKind { get; set; }
    public string? StandardEmojiValue { get; set; }
    public Guid? CustomEmojiId { get; set; }
    public string? CustomEmojiNameSnapshot { get; set; }
    public string? CustomEmojiContentTypeSnapshot { get; set; }
    public bool CustomEmojiAnimatedSnapshot { get; set; }
    public int CustomEmojiWidthSnapshot { get; set; }
    public int CustomEmojiHeightSnapshot { get; set; }
    public long CustomEmojiRevisionSnapshot { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required ChannelMessage Message { get; set; }
    public required NodeAccount Account { get; set; }
}
