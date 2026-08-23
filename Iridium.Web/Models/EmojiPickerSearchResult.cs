using Iridium.Protocol;

namespace Iridium.Web.Models;

public sealed record EmojiPickerSearchResult(
    StandardEmoji? Standard = null,
    CommunityDto? Community = null,
    CommunityEmojiDto? Custom = null)
{
    public string Key => Standard?.ArtworkKey ?? Custom!.Id.ToString("N");
}
