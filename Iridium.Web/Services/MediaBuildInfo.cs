using System.Reflection;

namespace Iridium.Web.Services;

public static class MediaBuildInfo
{
    public static string Id { get; } = typeof(MediaBuildInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(value => value.Key == "IridiumMediaBuildId").Value
        ?? throw new InvalidOperationException("The media build identifier is unavailable.");
}
