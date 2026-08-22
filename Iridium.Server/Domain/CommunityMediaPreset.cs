namespace Iridium.Server.Domain;

public enum CommunityMediaKind { Avatar, Banner }

public sealed class CommunityMediaPreset
{
    public Guid Id { get; set; }
    public Guid CommunityId { get; set; }
    public required Community Community { get; set; }
    public CommunityMediaKind Kind { get; set; }
    public int SlotIndex { get; set; }
    public required string OriginalObjectKey { get; set; }
    public string? ProcessedObjectKey { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double CropX { get; set; }
    public double CropY { get; set; }
    public double Zoom { get; set; } = 1;
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
