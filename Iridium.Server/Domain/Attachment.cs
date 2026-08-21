namespace Iridium.Server.Domain;

public sealed class Attachment
{
    public Guid Id { get; set; }
    public Guid UploaderAccountId { get; set; }
    public Guid? ChannelMessageId { get; set; }
    public Guid? DirectMessageId { get; set; }
    public required string OriginalFileName { get; set; }
    public required string OriginalObjectKey { get; set; }
    public string? PreviewObjectKey { get; set; }
    public required string OriginalContentType { get; set; }
    public string? PreviewContentType { get; set; }
    public long OriginalSizeBytes { get; set; }
    public long? PreviewSizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? AverageColor { get; set; }
    public bool IsSpoiler { get; set; }
    public required NodeAccount UploaderAccount { get; set; }
    public ChannelMessage? ChannelMessage { get; set; }
    public DirectMessage? DirectMessage { get; set; }
}
