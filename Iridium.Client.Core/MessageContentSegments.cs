using System.Text;
using System.Text.RegularExpressions;
using Iridium.Protocol;

namespace Iridium.Client.Core;

public enum MessageContentKind
{
    Bold, Italic, Underline, Strikethrough, InlineCode, CodeBlock, Quote, Spoiler,
    Heading1, Heading2, Heading3, Subtext
}

public abstract record MessageContentNode;
public sealed record MessageTextNode(string Text) : MessageContentNode;
public sealed record MessageMentionNode(CommunityMentionDto Mention) : MessageContentNode;
public sealed record MessageLinkNode(IReadOnlyList<MessageContentNode> Children, string Url) : MessageContentNode;
public sealed record MessageListItemNode(bool Ordered, string Marker, int Depth,
    IReadOnlyList<MessageContentNode> Children) : MessageContentNode;
public sealed record MessageListNode(IReadOnlyList<MessageListItemNode> Items) : MessageContentNode;
public sealed record MessageContainerNode(MessageContentKind Kind, IReadOnlyList<MessageContentNode> Children,
    int? SpoilerId = null, string? Language = null) : MessageContentNode;

public static partial class MessageContentSegments
{
    public static IReadOnlyList<MessageContentNode> Parse(string content,
        IReadOnlyList<CommunityMentionDto>? mentions) => new Parser(content, mentions).Parse();

    public static string PlainText(IEnumerable<MessageContentNode> nodes)
    {
        var result = new StringBuilder();
        Append(nodes, result);
        return result.ToString();

        static void Append(IEnumerable<MessageContentNode> values, StringBuilder target)
        {
            foreach (var value in values)
                switch (value)
                {
                    case MessageTextNode text: target.Append(text.Text); break;
                    case MessageMentionNode mention: target.Append(mention.Mention.DisplayText); break;
                    case MessageLinkNode link: Append(link.Children, target); break;
                    case MessageListNode list:
                        foreach (var item in list.Items) { Append(item.Children, target); target.AppendLine(); }
                        break;
                    case MessageListItemNode item: Append(item.Children, target); break;
                    case MessageContainerNode container: Append(container.Children, target); break;
                }
        }
    }

    public static bool IsSafeLink(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));

    private sealed class Parser
    {
        private readonly string _content;
        private readonly CommunityMentionDto[] _mentions;
        private int _spoilerId;

        public Parser(string content, IReadOnlyList<CommunityMentionDto>? mentions)
        {
            _content = content;
            _mentions = (mentions ?? []).OrderBy(value => value.Start)
                .Where(value => value.Start >= 0 && value.Length > 0 && value.Start + value.Length <= content.Length)
                .ToArray();
        }

        public IReadOnlyList<MessageContentNode> Parse()
        {
            var nodes = new List<MessageContentNode>();
            var cursor = 0;
            while (cursor < _content.Length)
            {
                var lineEnd = LineEnd(cursor);
                var lineLength = lineEnd - cursor;
                if (_content.AsSpan(cursor, lineLength).StartsWith("```"))
                {
                    ParseFence(cursor, lineEnd, nodes, out cursor);
                    continue;
                }
                if (_content.AsSpan(cursor, lineLength).StartsWith(">>>"))
                {
                    var start = cursor + 3;
                    if (start < _content.Length && _content[start] == ' ') start++;
                    nodes.Add(new MessageContainerNode(MessageContentKind.Quote, ParseInline(start, _content.Length)));
                    break;
                }
                if (_content.AsSpan(cursor, lineLength).StartsWith("> "))
                {
                    ParseQuotes(cursor, nodes, out cursor);
                    continue;
                }
                if (TryLineContainer(cursor, lineEnd, out var lineNode))
                {
                    nodes.Add(lineNode);
                    cursor = AfterLine(lineEnd);
                    continue;
                }
                if (TryList(cursor, out var list, out var afterList))
                {
                    nodes.Add(list);
                    cursor = afterList;
                    continue;
                }

                var next = AfterLine(lineEnd);
                nodes.AddRange(ParseInline(cursor, next));
                cursor = next;
            }
            return MergeText(nodes);
        }

        private void ParseFence(int opening, int headerEnd, List<MessageContentNode> target, out int next)
        {
            var language = _content[(opening + 3)..headerEnd].Trim();
            var contentStart = AfterLine(headerEnd);
            var closing = FindLineFence(contentStart);
            if (closing < 0)
            {
                target.AddRange(ParseInline(opening, _content.Length));
                next = _content.Length;
                return;
            }
            var codeEnd = closing;
            if (codeEnd > contentStart && _content[codeEnd - 1] == '\n') codeEnd--;
            if (codeEnd > contentStart && _content[codeEnd - 1] == '\r') codeEnd--;
            target.Add(new MessageContainerNode(MessageContentKind.CodeBlock,
                [new MessageTextNode(_content[contentStart..codeEnd])], Language: language.Length == 0 ? null : language));
            next = AfterLine(LineEnd(closing));
        }

        private int FindLineFence(int start)
        {
            var cursor = start;
            while (cursor < _content.Length)
            {
                var end = LineEnd(cursor);
                if (_content.AsSpan(cursor, end - cursor).StartsWith("```")) return cursor;
                cursor = AfterLine(end);
            }
            return -1;
        }

        private void ParseQuotes(int start, List<MessageContentNode> target, out int next)
        {
            var children = new List<MessageContentNode>();
            var cursor = start;
            while (cursor < _content.Length)
            {
                var end = LineEnd(cursor);
                if (!_content.AsSpan(cursor, end - cursor).StartsWith("> ")) break;
                if (children.Count > 0) children.Add(new MessageTextNode("\n"));
                children.AddRange(ParseInline(cursor + 2, end));
                cursor = AfterLine(end);
            }
            target.Add(new MessageContainerNode(MessageContentKind.Quote, MergeText(children)));
            next = cursor;
        }

        private bool TryLineContainer(int start, int end, out MessageContainerNode node)
        {
            (string Prefix, MessageContentKind Kind)[] values =
            [
                ("### ", MessageContentKind.Heading3), ("## ", MessageContentKind.Heading2),
                ("# ", MessageContentKind.Heading1), ("-# ", MessageContentKind.Subtext)
            ];
            foreach (var value in values)
            {
                if (!_content.AsSpan(start, end - start).StartsWith(value.Prefix)) continue;
                node = new(value.Kind, ParseInline(start + value.Prefix.Length, end));
                return true;
            }
            node = null!;
            return false;
        }

        private bool TryList(int start, out MessageListNode list, out int next)
        {
            var items = new List<MessageListItemNode>();
            var cursor = start;
            while (cursor < _content.Length)
            {
                var end = LineEnd(cursor);
                var line = _content[cursor..end];
                var match = ListLine().Match(line);
                if (!match.Success) break;
                var spaces = match.Groups[1].Length;
                var marker = match.Groups[2].Value;
                var contentStart = cursor + match.Groups[3].Index;
                items.Add(new(marker[0] is not '-' and not '*', marker, spaces / 2,
                    ParseInline(contentStart, end)));
                cursor = AfterLine(end);
            }
            list = new(items);
            next = cursor;
            return items.Count > 0;
        }

        private IReadOnlyList<MessageContentNode> ParseInline(int start, int end)
        {
            var nodes = new List<MessageContentNode>();
            var text = new StringBuilder();
            var cursor = start;
            while (cursor < end)
            {
                if (_content[cursor] == '\\' && cursor + 1 < end &&
                    MessageMarkdownGrammar.TryEscapedMarker(_content, cursor + 1, end, out var escaped))
                {
                    text.Append(escaped);
                    cursor += escaped.Length + 1;
                    continue;
                }

                var mention = _mentions.FirstOrDefault(value => value.Start == cursor && value.Start + value.Length <= end);
                if (mention is not null)
                {
                    FlushText(); nodes.Add(new MessageMentionNode(mention)); cursor += mention.Length; continue;
                }

                if (_content[cursor] == '[' && TryLink(cursor, end, out var link, out var linkNext))
                {
                    FlushText(); nodes.Add(link); cursor = linkNext; continue;
                }
                if (TryAutomaticLink(cursor, end, out var automaticLink, out var automaticNext))
                {
                    FlushText(); nodes.Add(automaticLink); cursor = automaticNext; continue;
                }

                if (MessageMarkdownGrammar.TryDelimiter(_content, cursor, end, out var delimiter, out var closing))
                {
                    FlushText();
                    var innerStart = cursor + delimiter.Marker.Length;
                    IReadOnlyList<MessageContentNode> children = delimiter.ContentKind == MessageContentKind.InlineCode
                        ? [new MessageTextNode(_content[innerStart..closing])]
                        : ParseInline(innerStart, closing);
                    if (delimiter.Combined) children = [new MessageContainerNode(MessageContentKind.Italic, children)];
                    nodes.Add(new MessageContainerNode(delimiter.ContentKind, children,
                        delimiter.ContentKind == MessageContentKind.Spoiler ? _spoilerId++ : null));
                    cursor = closing + delimiter.Marker.Length;
                    continue;
                }
                text.Append(_content[cursor++]);
            }
            FlushText();
            return MergeText(nodes);

            void FlushText()
            {
                if (text.Length == 0) return;
                nodes.Add(new MessageTextNode(text.ToString())); text.Clear();
            }
        }

        private bool TryLink(int cursor, int end, out MessageLinkNode link, out int next)
        {
            var labelEnd = _content.IndexOf(']', cursor + 1);
            if (labelEnd <= cursor + 1 || labelEnd + 1 >= end || _content[labelEnd + 1] != '(')
            { link = null!; next = cursor; return false; }
            var urlEnd = _content.IndexOf(')', labelEnd + 2);
            if (urlEnd < 0 || urlEnd >= end) { link = null!; next = cursor; return false; }
            var url = _content[(labelEnd + 2)..urlEnd].Trim();
            if (!IsSafeLink(url)) { link = null!; next = cursor; return false; }
            link = new(ParseInline(cursor + 1, labelEnd), url);
            next = urlEnd + 1;
            return true;
        }

        private bool TryAutomaticLink(int cursor, int end, out MessageLinkNode link, out int next)
        {
            link = null!; next = cursor;
            var remaining = _content.AsSpan(cursor, end - cursor);
            if (!remaining.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !remaining.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
            var length = 0;
            while (length < remaining.Length && !char.IsWhiteSpace(remaining[length])) length++;
            while (length > 0 && remaining[length - 1] is '.' or ',' or ';' or '!' or '?' or ')' or ']') length--;
            if (length == 0) return false;
            var url = remaining[..length].ToString();
            if (!IsSafeLink(url)) return false;
            link = new([new MessageTextNode(url)], url);
            next = cursor + length;
            return true;
        }

        private int LineEnd(int start)
        {
            var value = _content.IndexOf('\n', start);
            return value < 0 ? _content.Length : value;
        }
        private int AfterLine(int lineEnd) => lineEnd < _content.Length ? lineEnd + 1 : lineEnd;

        private static IReadOnlyList<MessageContentNode> MergeText(IEnumerable<MessageContentNode> source)
        {
            var result = new List<MessageContentNode>();
            foreach (var node in source)
                if (node is MessageTextNode text && result.LastOrDefault() is MessageTextNode previous)
                    result[^1] = new MessageTextNode(previous.Text + text.Text);
                else result.Add(node);
            return result;
        }
    }

    [GeneratedRegex(@"^( *)([-*]|\d+\.) +(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ListLine();
}
