namespace Iridium.Server.Domain;

public sealed class CommunityEmoji
{
    public Guid Id { get; set; }
    public Guid CommunityId { get; set; }
    public required Community Community { get; set; }
    public required string Name { get; set; }
    public required string ObjectKey { get; set; }
    public required string ContentType { get; set; }
    public bool IsAnimated { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long SizeBytes { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByAccountId { get; set; }
}
