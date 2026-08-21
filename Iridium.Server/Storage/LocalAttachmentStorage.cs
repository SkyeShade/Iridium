using Microsoft.Extensions.Options;
using Iridium.Server.Configuration;

namespace Iridium.Server.Storage;

public sealed class LocalAttachmentStorage(IOptions<NodeOptions> options, IWebHostEnvironment environment) : IAttachmentStorage
{
    private readonly string _root = Path.GetFullPath(Path.Combine(environment.ContentRootPath,
        options.Value.AttachmentStoragePath ?? Path.Combine("data", "attachments")));

    public async Task StoreAsync(string objectKey, Stream content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var path = Resolve(objectKey);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await content.CopyToAsync(output, cancellationToken);
    }

    public Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var path = Resolve(objectKey);
        Stream? result = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true)
            : null;
        return Task.FromResult(result);
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var path = Resolve(objectKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string Resolve(string objectKey)
    {
        if (objectKey.Length != 32 || objectKey.Any(value => !Uri.IsHexDigit(value)))
            throw new InvalidOperationException("The attachment object key is invalid.");
        return Path.Combine(_root, objectKey);
    }
}
