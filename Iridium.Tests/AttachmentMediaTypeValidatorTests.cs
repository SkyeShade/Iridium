using Iridium.Server.Security;
using Iridium.Server.Storage;

namespace Iridium.Tests;

public sealed class AttachmentMediaTypeValidatorTests
{
    private readonly AttachmentMediaTypeValidator _validator = new();

    [Fact]
    public async Task ValidMp4SignatureIsCanonicalizedFromDeclaredOrGenericMime()
    {
        Assert.Equal("video/mp4", await _validator.ValidateAsync(new MemoryStream(Mp4()), "video/mp4"));
        Assert.Equal("video/mp4", await _validator.ValidateAsync(new MemoryStream(Mp4()), "application/octet-stream"));
    }

    [Fact]
    public async Task InvalidDeclaredMp4IsRejectedAndCannotReachVideoRenderer()
    {
        await Assert.ThrowsAsync<AttachmentMediaValidationException>(() =>
            _validator.ValidateAsync(new MemoryStream("not an mp4"u8.ToArray()), "video/mp4"));
    }

    [Fact]
    public async Task NonVideoMimeRemainsUnchanged()
    {
        Assert.Equal("application/pdf", await _validator.ValidateAsync(new MemoryStream("pdf"u8.ToArray()), "application/pdf"));
    }

    [Fact]
    public void PlaybackTokensAreAttachmentScopedTamperProofAndExpiring()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var service = new AttachmentPlaybackTokenService(time);
        var attachment = Guid.NewGuid();
        var account = Guid.NewGuid();
        var issued = service.Issue(attachment, account);

        Assert.True(service.TryValidate(attachment, issued.Token, out var resolved));
        Assert.Equal(account, resolved);
        Assert.False(service.TryValidate(Guid.NewGuid(), issued.Token, out _));
        Assert.False(service.TryValidate(attachment, issued.Token + "x", out _));
        time.UtcNow = issued.ExpiresAt.AddSeconds(1);
        Assert.False(service.TryValidate(attachment, issued.Token, out _));
    }

    internal static byte[] Mp4()
    {
        var bytes = new byte[64];
        bytes[3] = 24;
        "ftyp"u8.CopyTo(bytes.AsSpan(4));
        "mp42"u8.CopyTo(bytes.AsSpan(8));
        "isom"u8.CopyTo(bytes.AsSpan(16));
        return bytes;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
