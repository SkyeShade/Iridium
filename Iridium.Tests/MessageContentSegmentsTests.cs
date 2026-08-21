using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class MessageContentSegmentsTests
{
    [Fact]
    public void PlainAndUnsafeHtmlRemainTextNodes()
    {
        const string content = "line one\n<script>alert(1)</script>";
        var node = Assert.IsType<MessageTextNode>(Assert.Single(MessageContentSegments.Parse(content, null)));
        Assert.Equal(content, node.Text);
    }

    [Theory]
    [InlineData("**bold**", MessageContentKind.Bold)]
    [InlineData("*italic*", MessageContentKind.Italic)]
    [InlineData("_italic_", MessageContentKind.Italic)]
    [InlineData("__underline__", MessageContentKind.Underline)]
    [InlineData("~~strike~~", MessageContentKind.Strikethrough)]
    [InlineData("`code`", MessageContentKind.InlineCode)]
    [InlineData("||spoiler||", MessageContentKind.Spoiler)]
    public void SupportedInlineFormattingProducesStructuredNodes(string content, MessageContentKind kind)
    {
        var node = Assert.IsType<MessageContainerNode>(Assert.Single(MessageContentSegments.Parse(content, null)));
        Assert.Equal(kind, node.Kind);
    }

    [Fact]
    public void CombinedFormattingAndMentionsAreNestedWithoutLosingStableIdentity()
    {
        var accountId = Guid.NewGuid();
        const string content = "***hello @Skye***";
        var start = content.IndexOf("@Skye", StringComparison.Ordinal);
        var root = Assert.IsType<MessageContainerNode>(Assert.Single(MessageContentSegments.Parse(content,
            [new(CommunityMentionKind.Account, accountId, start, 5, "@Skye")])));

        Assert.Equal(MessageContentKind.Bold, root.Kind);
        var italic = Assert.IsType<MessageContainerNode>(Assert.Single(root.Children));
        Assert.Equal(MessageContentKind.Italic, italic.Kind);
        Assert.Equal(accountId, Assert.Single(Descendants(italic).OfType<MessageMentionNode>()).Mention.TargetId);
    }

    [Fact]
    public void SpoilerMayContainMarkdownAndMention()
    {
        var accountId = Guid.NewGuid();
        const string content = "||hidden **bold @Skye**||";
        var start = content.IndexOf("@Skye", StringComparison.Ordinal);
        var spoiler = Assert.IsType<MessageContainerNode>(Assert.Single(MessageContentSegments.Parse(content,
            [new(CommunityMentionKind.Account, accountId, start, 5, "@Skye")])));

        Assert.Equal(MessageContentKind.Spoiler, spoiler.Kind);
        Assert.Contains(Descendants(spoiler), value => value is MessageContainerNode { Kind: MessageContentKind.Bold });
        Assert.Equal(accountId, Assert.Single(Descendants(spoiler).OfType<MessageMentionNode>()).Mention.TargetId);
    }

    [Fact]
    public void InlineAndBlockCodeKeepMarkdownAndMentionsLiteral()
    {
        var accountId = Guid.NewGuid();
        const string content = "`@Skye **literal**`\n```csharp\nConsole.WriteLine(\"@Skye\");\n```";
        var mentions = new[]
        {
            new CommunityMentionDto(CommunityMentionKind.Account, accountId, 1, 5, "@Skye"),
            new CommunityMentionDto(CommunityMentionKind.Account, accountId,
                content.LastIndexOf("@Skye", StringComparison.Ordinal), 5, "@Skye")
        };
        var nodes = MessageContentSegments.Parse(content, mentions);

        Assert.Empty(Descendants(nodes).OfType<MessageMentionNode>());
        Assert.False(MessageText.AllowsMentionAt(content, mentions[0].Start));
        Assert.False(MessageText.AllowsMentionAt(content, mentions[1].Start));
        var block = Assert.Single(Descendants(nodes).OfType<MessageContainerNode>(),
            value => value.Kind == MessageContentKind.CodeBlock);
        Assert.Equal("csharp", block.Language);
        Assert.Contains("Console.WriteLine", MessageContentSegments.PlainText(block.Children));
    }

    [Fact]
    public void SingleAndMultilineQuotesProduceQuoteNodes()
    {
        var single = MessageContentSegments.Parse("> hello\nnormal", null);
        Assert.Contains(single, value => value is MessageContainerNode { Kind: MessageContentKind.Quote });
        var multiline = Assert.IsType<MessageContainerNode>(Assert.Single(
            MessageContentSegments.Parse(">>> hello\nworld", null)));
        Assert.Equal(MessageContentKind.Quote, multiline.Kind);
        Assert.Equal("hello\nworld", MessageContentSegments.PlainText(multiline.Children));
    }

    [Fact]
    public void UnpairedMarkersRemainPlainText()
    {
        const string content = "this || and ** remain visible";
        Assert.Equal(content, MessageContentSegments.PlainText(MessageContentSegments.Parse(content, null)));
    }

    [Fact]
    public void CharacterCountingUsesUnicodeScalarValues()
    {
        Assert.Equal(0, MessageText.CountCharacters(string.Empty));
        Assert.Equal(1, MessageText.CountCharacters("A"));
        Assert.Equal(1, MessageText.CountCharacters("😀"));
        Assert.Equal(3, MessageText.CountCharacters("A😀B"));
        Assert.Equal(9, MessageText.CountCharacters("**hello**"));
    }

    [Fact]
    public void DefaultLimitBoundariesCountSourceCharacters()
    {
        var exact = new string('a', 10_000);
        Assert.Equal(10_000, MessageText.CountCharacters(exact));
        Assert.Equal(10_001, MessageText.CountCharacters(exact + "😀"));
    }

    [Theory]
    [InlineData("**test**", ComposerMarkdownStyle.Bold)]
    [InlineData("*test*", ComposerMarkdownStyle.Italic)]
    [InlineData("_test_", ComposerMarkdownStyle.Italic)]
    [InlineData("__test__", ComposerMarkdownStyle.Underline)]
    [InlineData("~~test~~", ComposerMarkdownStyle.Strikethrough)]
    [InlineData("`test`", ComposerMarkdownStyle.InlineCode)]
    [InlineData("||test||", ComposerMarkdownStyle.Spoiler)]
    public void ComposerPreviewPreservesMarkersAndStylesOnlyInnerSource(string source, ComposerMarkdownStyle style)
    {
        var segments = ComposerMarkdownSegments.Parse(source);
        Assert.Equal(source, string.Concat(segments.Select(value => value.Text)));
        Assert.All(segments.Where(value => value.IsMarker), value => Assert.Equal(ComposerMarkdownStyle.None, value.Style));
        Assert.Contains(segments, value => !value.IsMarker && value.Style.HasFlag(style));
    }

    [Fact]
    public void ComposerPreviewPreservesMultilineRawSourceAndCombinedFormatting()
    {
        const string source = "hello\n***bold italic*** @Skye";
        var segments = ComposerMarkdownSegments.Parse(source);
        Assert.Equal(source, string.Concat(segments.Select(value => value.Text)));
        Assert.Contains(segments, value => value.Style.HasFlag(ComposerMarkdownStyle.Bold) &&
                                           value.Style.HasFlag(ComposerMarkdownStyle.Italic));
    }

    [Theory]
    [InlineData("__*underline italic*__", MessageContentKind.Underline, MessageContentKind.Italic)]
    [InlineData("__**underline bold**__", MessageContentKind.Underline, MessageContentKind.Bold)]
    [InlineData("__***all three***__", MessageContentKind.Underline, MessageContentKind.Bold)]
    public void DiscordCombinedStylesNestSafely(string source, MessageContentKind outer, MessageContentKind inner)
    {
        var root = Assert.IsType<MessageContainerNode>(Assert.Single(MessageContentSegments.Parse(source, null)));
        Assert.Equal(outer, root.Kind);
        Assert.Contains(Descendants(root), value => value is MessageContainerNode container && container.Kind == inner);
        if (source.Contains("***", StringComparison.Ordinal))
            Assert.Contains(Descendants(root), value => value is MessageContainerNode { Kind: MessageContentKind.Italic });
    }

    [Theory]
    [InlineData("# Heading", MessageContentKind.Heading1)]
    [InlineData("## Heading", MessageContentKind.Heading2)]
    [InlineData("### Heading", MessageContentKind.Heading3)]
    [InlineData("-# secondary", MessageContentKind.Subtext)]
    public void LineOnlyDiscordBlocksRequireTheirPrefix(string source, MessageContentKind kind)
    {
        Assert.Equal(kind, Assert.IsType<MessageContainerNode>(
            Assert.Single(MessageContentSegments.Parse(source, null))).Kind);
        Assert.IsType<MessageTextNode>(Assert.Single(MessageContentSegments.Parse("#not-a-heading", null)));
    }

    [Fact]
    public void OrderedUnorderedAndNestedListsRetainDepthAndMarkers()
    {
        var list = Assert.IsType<MessageListNode>(Assert.Single(MessageContentSegments.Parse(
            "- alpha\n  * beta\n1. first\n  2. second", null)));
        Assert.Equal(4, list.Items.Count);
        Assert.Equal([0, 1, 0, 1], list.Items.Select(value => value.Depth));
        Assert.False(list.Items[0].Ordered);
        Assert.True(list.Items[2].Ordered);
    }

    [Fact]
    public void MaskedLinksAllowOnlyHttpAndHttps()
    {
        var safe = Assert.IsType<MessageLinkNode>(Assert.Single(
            MessageContentSegments.Parse("[Open Iridium](https://example.com/path)", null)));
        Assert.Equal("https://example.com/path", safe.Url);
        Assert.DoesNotContain(Descendants(MessageContentSegments.Parse("[bad](javascript:alert(1))", null)),
            value => value is MessageLinkNode);
        Assert.DoesNotContain(Descendants(MessageContentSegments.Parse("[bad](data:text/html,test)", null)),
            value => value is MessageLinkNode);
    }

    [Fact]
    public void EscapedFormattingAndHeadingMarkersRemainLiteral()
    {
        Assert.Equal("**not bold**", MessageContentSegments.PlainText(
            MessageContentSegments.Parse("\\**not bold**", null)));
        var escapedHeading = MessageContentSegments.Parse("\\# not a heading", null);
        Assert.DoesNotContain(escapedHeading, value => value is MessageContainerNode { Kind: MessageContentKind.Heading1 });
        Assert.Equal("# not a heading", MessageContentSegments.PlainText(escapedHeading));
    }

    [Theory]
    [InlineData("# Heading", ComposerMarkdownStyle.Heading1)]
    [InlineData("-# secondary", ComposerMarkdownStyle.Subtext)]
    [InlineData("> quote", ComposerMarkdownStyle.Quote)]
    [InlineData("- list", ComposerMarkdownStyle.List)]
    [InlineData("[link](https://example.com)", ComposerMarkdownStyle.Link)]
    public void ComposerStructuralPreviewPreservesRawSource(string source, ComposerMarkdownStyle style)
    {
        var segments = ComposerMarkdownSegments.Parse(source);
        Assert.Equal(source, string.Concat(segments.Select(value => value.Text)));
        Assert.Contains(segments, value => value.Style.HasFlag(style));
    }

    private static IEnumerable<MessageContentNode> Descendants(MessageContentNode node) => Descendants([node]);
    private static IEnumerable<MessageContentNode> Descendants(IEnumerable<MessageContentNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            IEnumerable<MessageContentNode>? children = node switch
            {
                MessageContainerNode container => container.Children,
                MessageLinkNode link => link.Children,
                MessageListNode list => list.Items,
                MessageListItemNode item => item.Children,
                _ => null
            };
            if (children is null) continue;
            foreach (var child in Descendants(children)) yield return child;
        }
    }
}
