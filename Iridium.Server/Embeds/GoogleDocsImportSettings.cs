namespace Iridium.Server.Embeds;

public sealed record GoogleDocsImportSettings(TimeSpan SourceFetchTimeout, TimeSpan MediaFetchTimeout)
{
    public TimeSpan FreshFor { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan StaleFor { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan RetryFailureAfter { get; init; } = TimeSpan.FromSeconds(30);

    public static GoogleDocsImportSettings Default { get; } = new(
        GoogleDocsPublishedDocumentService.SourceFetchTimeout,
        GoogleDocsPublishedDocumentService.MediaFetchTimeout);
}
