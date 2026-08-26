namespace Iridium.Client.Core;

public static class MessageHistoryFollowLatest
{
    public static bool ShouldFollow(bool isPinnedToLatest, long previousRevision, long currentRevision) =>
        isPinnedToLatest && previousRevision >= 0 && currentRevision != previousRevision;
}
