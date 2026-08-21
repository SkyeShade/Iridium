namespace Iridium.Server.Storage;

public interface IAttachmentStorage
{
    Task StoreAsync(string objectKey, Stream content, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default);
}
