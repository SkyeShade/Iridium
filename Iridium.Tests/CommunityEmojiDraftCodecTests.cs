using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class CommunityEmojiDraftCodecTests
{
    [Fact]
    public void FriendlySyntaxDefaultsAlphabeticallyAndNeverExposesStableId()
    {
        var alpha = Community("Alpha");
        var beta = Community("Beta");
        var alphaEmoji = Emoji(alpha.Id, "mudrock");
        var betaEmoji = Emoji(beta.Id, "mudrock");
        AvailableCommunityEmoji[] available = [new(beta, betaEmoji), new(alpha, alphaEmoji)];

        var serialized = CommunityEmojiDraftCodec.Serialize("hello :mudrock:", [], available, null);
        Assert.Contains(alphaEmoji.Id.ToString("N"), serialized);
        Assert.DoesNotContain(betaEmoji.Id.ToString("N"), serialized);
        Assert.Equal("hello :mudrock:", CommunityEmojiDraftCodec.ToUserFacing(serialized));
    }

    [Fact]
    public void ExplicitCollisionSelectionKeepsExactEmojiIdBehindFriendlyText()
    {
        var alpha = Community("Alpha");
        var beta = Community("Beta");
        var alphaEmoji = Emoji(alpha.Id, "mudrock");
        var betaEmoji = Emoji(beta.Id, "mudrock");
        AvailableCommunityEmoji[] available = [new(alpha, alphaEmoji), new(beta, betaEmoji)];
        CommunityEmojiDraftReference[] selected = [new(0, 9, betaEmoji.Id, "mudrock", beta.Id)];

        var serialized = CommunityEmojiDraftCodec.Serialize(":mudrock:", selected, available, null);
        Assert.Equal(CommunityEmojiNames.Token(betaEmoji.Id, "mudrock"), serialized);
        var draft = CommunityEmojiDraftCodec.Deserialize(serialized, available);
        Assert.Equal(":mudrock:", draft.Text);
        Assert.Equal(betaEmoji.Id, Assert.Single(draft.References).EmojiId);
        Assert.Equal(1, CommunityEmojiDraftCodec.CountCharacters(draft.Text, draft.References));
        Assert.Equal(1, MessageText.CountCharacters(serialized));
    }

    [Fact]
    public void CommunityCollisionsAlwaysSortAlphabetically()
    {
        var alpha = Community("Alpha");
        var beta = Community("Beta");
        var gamma = Community("Gamma");
        AvailableCommunityEmoji[] available =
        [
            new(gamma, Emoji(gamma.Id, "same")), new(alpha, Emoji(alpha.Id, "same")),
            new(beta, Emoji(beta.Id, "same"))
        ];
        var ordered = CommunityEmojiDraftCodec.Order(available, beta.Id);
        Assert.Equal(["Alpha", "Beta", "Gamma"], ordered.Select(value => value.Community.Name));
    }

    [Fact]
    public void ReferencePositionsSurviveTypingBeforeFriendlyEmoji()
    {
        var id = Guid.NewGuid();
        var communityId = Guid.NewGuid();
        List<CommunityEmojiDraftReference> references = [new(6, 9, id, "mudrock", communityId)];
        CommunityEmojiDraftCodec.ReconcileReferences("hello :mudrock:", "well hello :mudrock:", references);
        Assert.Equal(11, Assert.Single(references).Start);
    }

    [Fact]
    public void AtomicDocumentSerializesObjectSlotToStableEmojiToken()
    {
        var emojiId = Guid.NewGuid();
        CommunityEmojiDraftReference[] references = [new(6, 1, emojiId, "mudrock", Guid.NewGuid())];
        var document = $"hello {CommunityEmojiDraftCodec.ObjectReplacementCharacter} world";

        var serialized = CommunityEmojiDraftCodec.SerializeDocument(document, references);

        Assert.Equal($"hello {CommunityEmojiNames.Token(emojiId, "mudrock")} world", serialized);
        Assert.Equal(document.EnumerateRunes().Count(), CommunityEmojiDraftCodec.CountCharacters(document, references));
        Assert.Equal(6 + CommunityEmojiNames.Token(emojiId, "mudrock").Length + 1,
            CommunityEmojiDraftCodec.MapDocumentPositionToSerialized(8, references));
    }

    [Fact]
    public void AtomicStandardAndCustomSlotsSerializeAndMapToTheirVisibleValues()
    {
        var emojiId = Guid.NewGuid();
        CommunityEmojiDraftReference[] custom = [new(0, 1, emojiId, "mudrock", Guid.NewGuid())];
        StandardEmojiDraftReference[] standard = [new(1, 1, "1f1e9-1f1f0", "\U0001F1E9\U0001F1F0", "denmark")];
        var document = $"{CommunityEmojiDraftCodec.ObjectReplacementCharacter}{CommunityEmojiDraftCodec.ObjectReplacementCharacter}x";

        var serialized = CommunityEmojiDraftCodec.SerializeDocument(document, custom, standard);

        Assert.Equal($"{CommunityEmojiNames.Token(emojiId, "mudrock")}\U0001F1E9\U0001F1F0x", serialized);
        Assert.Equal(serialized.Length, CommunityEmojiDraftCodec.MapDocumentPositionToSerialized(3, custom, standard));
    }

    [Fact]
    public void StaleCustomReferenceCannotCrashCharacterCountingDuringComposerClear()
    {
        CommunityEmojiDraftReference[] stale = [new(0, 1, Guid.NewGuid(), "mudrock", Guid.NewGuid())];

        Assert.Equal(0, CommunityEmojiDraftCodec.CountCharacters(string.Empty, stale));
    }

    [Fact]
    public void CanonicalMessageTokenRoundTripsStableCustomEmojiIdentity()
    {
        var id = Guid.NewGuid();
        var content = CommunityEmojiNames.Token(id, "mudrock");

        var reference = Assert.Single(CommunityEmojiNames.References(content));
        Assert.Equal(id, reference.EmojiId);
        Assert.Equal("mudrock", reference.Name);
        Assert.Equal(1, MessageText.CountCharacters(content));
    }

    private static CommunityDto Community(string name) =>
        new(Guid.NewGuid(), name, null, Guid.NewGuid(), DateTimeOffset.UtcNow);

    private static CommunityEmojiDto Emoji(Guid communityId, string name) =>
        new(Guid.NewGuid(), communityId, name, "image/webp", false, 64, 64, 100, 1,
            DateTimeOffset.UtcNow, Guid.NewGuid());
}
