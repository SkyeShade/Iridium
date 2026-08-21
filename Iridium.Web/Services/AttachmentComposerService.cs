using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.AspNetCore.Components.Forms;

namespace Iridium.Web.Services;

public readonly record struct ComposerAttachmentKey(string NodeAuthority, Guid AccountId, string Kind, Guid ConversationId);
public sealed record BrowserImageMetadata(string Name, long Size, long LastModified,
    int? Width, int? Height, string? AverageColor);

public enum PendingAttachmentState { Selected, Uploading, Uploaded, Failed }

public sealed class PendingAttachment
{
    public Guid LocalId { get; } = Guid.NewGuid();
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public required string ContentType { get; init; }
    public required IBrowserFile BrowserFile { get; init; }
    public required byte[] BufferedContent { get; init; }
    public string? PreviewUrl { get; set; }
    public PendingAttachmentState State { get; set; }
    public string? Error { get; set; }
    public AttachmentUploadDto? Uploaded { get; set; }
    public bool IsSpoiler { get; set; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? AverageColor { get; init; }
    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

public sealed record AttachmentSelectionResult(IReadOnlyList<PendingAttachment> Attachments, string? Error);

public interface IAttachmentComposerService
{
    IReadOnlyList<PendingAttachment> Get(ComposerAttachmentKey key);
    Task<AttachmentSelectionResult> AddAsync(ComposerAttachmentKey key, IReadOnlyList<IBrowserFile> files,
        IReadOnlyList<BrowserImageMetadata>? imageMetadata = null,
        CancellationToken cancellationToken = default);
    void Remove(ComposerAttachmentKey key, Guid localId);
    void ToggleSpoiler(ComposerAttachmentKey key, Guid localId);
    Task<IReadOnlyList<AttachmentDto>> UploadAsync(ComposerAttachmentKey key, CancellationToken cancellationToken = default);
    IReadOnlyList<AttachmentDto> LocalDtos(ComposerAttachmentKey key);
    Func<CancellationToken, Task<IReadOnlyList<AttachmentDto>>> CreateUploadOperation(ComposerAttachmentKey key);
    void Clear(ComposerAttachmentKey key);
    Task<ServerInfoDto> GetLimitsAsync(ComposerAttachmentKey key, CancellationToken cancellationToken = default);
}

public sealed class AttachmentComposerService(NodeSession nodeSession) : IAttachmentComposerService, IDisposable
{
    private readonly Dictionary<ComposerAttachmentKey, List<PendingAttachment>> _drafts = [];
    private readonly Dictionary<string, ServerInfoDto> _serverInfo = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PendingAttachment> Get(ComposerAttachmentKey key) =>
        _drafts.TryGetValue(key, out var attachments) ? attachments : [];

    public async Task<ServerInfoDto> GetLimitsAsync(ComposerAttachmentKey key, CancellationToken cancellationToken = default)
    {
        if (_serverInfo.TryGetValue(key.NodeAuthority, out var cached)) return cached;
        return _serverInfo[key.NodeAuthority] = await nodeSession.GetServerInfoAsync(cancellationToken);
    }

    public async Task<AttachmentSelectionResult> AddAsync(ComposerAttachmentKey key,
        IReadOnlyList<IBrowserFile> files, IReadOnlyList<BrowserImageMetadata>? imageMetadata = null,
        CancellationToken cancellationToken = default)
    {
        var limits = await GetLimitsAsync(key, cancellationToken);
        var target = _drafts.TryGetValue(key, out var existing) ? existing : _drafts[key] = [];
        string? error = null;
        for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            var file = files[fileIndex];
            if (target.Count >= limits.MaxAttachmentsPerMessage)
            {
                error = $"A message can include up to {limits.MaxAttachmentsPerMessage} files.";
                break;
            }
            if (file.Size > limits.MaxAttachmentBytes)
            {
                error = $"{file.Name} is larger than {FormatSize(limits.MaxAttachmentBytes)}.";
                continue;
            }
            try
            {
                await using var source = file.OpenReadStream(limits.MaxAttachmentBytes, cancellationToken);
                using var buffer = new MemoryStream((int)Math.Min(file.Size, int.MaxValue));
                await source.CopyToAsync(buffer, cancellationToken);
                var metadata = imageMetadata is not null && fileIndex < imageMetadata.Count &&
                               imageMetadata[fileIndex].Name == file.Name && imageMetadata[fileIndex].Size == file.Size
                    ? imageMetadata[fileIndex] : null;
                var pending = new PendingAttachment
                {
                    FileName = file.Name, SizeBytes = file.Size,
                    ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                    BrowserFile = file, BufferedContent = buffer.ToArray(),
                    Width = metadata?.Width, Height = metadata?.Height, AverageColor = metadata?.AverageColor
                };
                target.Add(pending);
                if (pending.IsImage) pending.PreviewUrl = await CreatePreviewAsync(file, cancellationToken);
            }
            catch (Exception exception)
            {
                error = $"{file.Name} could not be read: {exception.GetBaseException().Message}";
            }
        }
        return new(target, error);
    }

    public void Remove(ComposerAttachmentKey key, Guid localId)
    {
        if (!_drafts.TryGetValue(key, out var attachments)) return;
        foreach (var attachment in attachments.Where(value => value.LocalId == localId)) Release(attachment);
        attachments.RemoveAll(value => value.LocalId == localId);
        if (attachments.Count == 0) _drafts.Remove(key);
    }

    public void ToggleSpoiler(ComposerAttachmentKey key, Guid localId)
    {
        var attachment = Get(key).FirstOrDefault(value => value.LocalId == localId);
        if (attachment is not null) attachment.IsSpoiler = !attachment.IsSpoiler;
    }

    public async Task<IReadOnlyList<AttachmentDto>> UploadAsync(ComposerAttachmentKey key,
        CancellationToken cancellationToken = default)
    {
        if (!_drafts.TryGetValue(key, out var attachments) || attachments.Count == 0) return [];
        return await UploadAsync(key, attachments, cancellationToken);
    }

    public IReadOnlyList<AttachmentDto> LocalDtos(ComposerAttachmentKey key) =>
        !_drafts.TryGetValue(key, out var attachments) ? [] : attachments.Select(value => new AttachmentDto(
            value.LocalId, value.FileName, value.ContentType, value.SizeBytes, string.Empty,
            value.Width, value.Height, value.AverageColor,
            LocalPreviewUrl: value.PreviewUrl, IsSpoiler: value.IsSpoiler)).ToArray();

    public Func<CancellationToken, Task<IReadOnlyList<AttachmentDto>>> CreateUploadOperation(ComposerAttachmentKey key)
    {
        var captured = _drafts.TryGetValue(key, out var attachments) ? attachments : [];
        return cancellationToken => UploadAsync(key, captured, cancellationToken);
    }

    private async Task<IReadOnlyList<AttachmentDto>> UploadAsync(ComposerAttachmentKey key, List<PendingAttachment> attachments,
        CancellationToken cancellationToken)
    {
        if (attachments.Count == 0) return [];
        var limits = await GetLimitsAsync(key, cancellationToken);
        if (attachments.Count > limits.MaxAttachmentsPerMessage) throw new InvalidOperationException("Too many attachments are selected.");
        var result = new List<AttachmentDto>(attachments.Count);
        foreach (var attachment in attachments)
        {
            if (attachment.SizeBytes > limits.MaxAttachmentBytes) throw new InvalidOperationException($"{attachment.FileName} exceeds the Node file limit.");
            try
            {
                attachment.State = PendingAttachmentState.Uploading;
                if (attachment.Uploaded is null)
                {
                    await using var stream = new MemoryStream(attachment.BufferedContent, writable: false);
                    attachment.Uploaded = await nodeSession.UploadAttachmentAsync(stream, attachment.FileName,
                        attachment.ContentType, attachment.IsSpoiler, attachment.Width, attachment.Height,
                        attachment.AverageColor, cancellationToken);
                }
                attachment.State = PendingAttachmentState.Uploaded;
                attachment.Error = null;
                result.Add(new(attachment.Uploaded.Id, attachment.Uploaded.OriginalFileName,
                    attachment.Uploaded.ContentType, attachment.Uploaded.SizeBytes,
                    $"api/attachments/{attachment.Uploaded.Id}", attachment.Uploaded.Width,
                    attachment.Uploaded.Height, attachment.Uploaded.AverageColor, LocalPreviewUrl: attachment.PreviewUrl,
                    IsSpoiler: attachment.Uploaded.IsSpoiler,
                    PreviewDownloadUrl: attachment.Uploaded.PreviewContentType is null
                        ? null : $"api/attachments/{attachment.Uploaded.Id}/preview",
                    PreviewContentType: attachment.Uploaded.PreviewContentType,
                    PreviewSizeBytes: attachment.Uploaded.PreviewSizeBytes));
            }
            catch (Exception exception)
            {
                attachment.State = PendingAttachmentState.Failed;
                attachment.Error = exception.Message;
                throw;
            }
        }
        return result;
    }

    public void Clear(ComposerAttachmentKey key) => _drafts.Remove(key);

    public void Dispose()
    {
        foreach (var attachment in _drafts.Values.SelectMany(value => value)) Release(attachment);
        _drafts.Clear();
        _serverInfo.Clear();
    }

    private static void Release(PendingAttachment attachment)
    {
        Array.Clear(attachment.BufferedContent);
        attachment.PreviewUrl = null;
    }

    private static async Task<string?> CreatePreviewAsync(IBrowserFile file, CancellationToken cancellationToken)
    {
        try
        {
            var preview = await file.RequestImageFileAsync("image/jpeg", 320, 240);
            await using var stream = preview.OpenReadStream(512 * 1024, cancellationToken);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            return $"data:image/jpeg;base64,{Convert.ToBase64String(memory.ToArray())}";
        }
        catch { return null; }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 => $"{bytes / 1024d / 1024d:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B"
    };
}
