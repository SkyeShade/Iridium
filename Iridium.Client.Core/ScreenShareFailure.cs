namespace Iridium.Client.Core;

public enum ScreenShareFailureKind
{
    Unsupported,
    Cancelled,
    PermissionDenied,
    Unexpected
}

public static class ScreenShareFailure
{
    public const string UnsupportedMessage = "Screen sharing is not supported by this browser or device.";
    public const string PermissionDeniedMessage = "Permission to share your screen was denied.";
    public const string UnexpectedMessage = "Unable to start screen sharing.";

    public static ScreenShareFailureKind Classify(Exception exception)
    {
        var detail = ExceptionDetail(exception);
        if (ContainsAny(detail, "DeviceUnsupportedError", "NotSupportedError", "getDisplayMedia not supported",
                "does not support screen capture", "display capture is unavailable"))
            return ScreenShareFailureKind.Unsupported;
        if (ContainsAny(detail, "AbortError", "picker cancelled", "picker canceled", "selection cancelled",
                "selection canceled", "user cancelled", "user canceled"))
            return ScreenShareFailureKind.Cancelled;
        if (ContainsAny(detail, "NotAllowedError", "PermissionDeniedError", "permission denied", "not allowed"))
            return ScreenShareFailureKind.PermissionDenied;
        return ScreenShareFailureKind.Unexpected;
    }

    public static string? UserMessage(Exception exception) => Classify(exception) switch
    {
        ScreenShareFailureKind.Unsupported => UnsupportedMessage,
        ScreenShareFailureKind.Cancelled => null,
        ScreenShareFailureKind.PermissionDenied => PermissionDeniedMessage,
        _ => UnexpectedMessage
    };

    private static string ExceptionDetail(Exception exception)
    {
        var values = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException!)
            values.Add($"{current.GetType().Name}: {current.Message}");
        return string.Join(" | ", values);
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}
