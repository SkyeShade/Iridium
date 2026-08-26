namespace Iridium.Tests;

public sealed class MessageQolUiContractTests
{
    [Fact]
    public void ShiftHoverUsesOneShellLevelModifierBridgeAndPermissionAwareExistingActions()
    {
        var root = RepositoryRoot();
        var shell = File.ReadAllText(Path.Combine(root, "Iridium.UI", "ApplicationShell.razor"));
        var script = File.ReadAllText(Path.Combine(root, "Iridium.UI", "wwwroot", "js", "mobileConversationSwipe.js"));
        var row = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageRow.razor"));
        var css = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageRow.razor.css"));

        Assert.Contains("wireModifierKeys", shell);
        Assert.Contains("unwireModifierKeys", shell);
        Assert.Contains("window.addEventListener('keydown', keydown)", script);
        Assert.Contains("window.addEventListener('keyup', keyup)", script);
        Assert.Contains("window.addEventListener('blur', blur)", script);
        Assert.Contains("document.addEventListener('visibilitychange', visibility)", script);
        Assert.Contains("ModifierKeys.ShiftPressed && _messageHovered", row);
        Assert.Contains("@if (CanDelete)", row);
        Assert.Contains("@media (hover:hover) and (pointer:fine)", css);
    }

    [Fact]
    public void EmptyComposerArrowUpDefersToSuggestionsAndReusesInlineEditRequest()
    {
        var root = RepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "Iridium.Web", "wwwroot", "js", "chat.js"));
        var composer = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageComposer.razor"));
        var list = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageList.razor"));
        var row = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageRow.razor"));

        Assert.True(script.IndexOf("const mentionMenu", StringComparison.Ordinal) <
                    script.IndexOf("EditLastMessageFromKeyboardAsync", StringComparison.Ordinal));
        Assert.Contains("snapshot.content.length === 0", script);
        Assert.Contains("Suggestions.Count > 0 || EmojiSuggestions.Count > 0", composer);
        Assert.Contains("MessageTimeline.LatestEditableOwn", list);
        Assert.Contains("EditRequest", row);
        Assert.Contains("await BeginEdit()", row);
    }

    [Fact]
    public void ExplicitEditCompletionRequestsTheExistingOneShotComposerFocusOnlyOnDesktop()
    {
        var root = RepositoryRoot();
        var row = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageRow.razor"));
        var list = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageList.razor"));
        var channel = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ChannelView.razor"));
        var direct = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "DirectMessageView.razor"));
        var home = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Pages", "Home.razor"));

        Assert.Equal(2, row.Split("await OnEditCompleted.InvokeAsync()", StringSplitOptions.None).Length - 1);
        var save = Slice(row, "private async Task SaveEditAsync()", "private Task DeleteAsync()");
        var success = save.IndexOf("if (await Edit(new MessageEditSubmission", StringComparison.Ordinal);
        var completed = save.IndexOf("await OnEditCompleted.InvokeAsync()", StringComparison.Ordinal);
        Assert.True(success >= 0 && completed > success);
        Assert.DoesNotContain("OnEditCompleted", save[(save.IndexOf("finally", StringComparison.Ordinal))..]);
        Assert.Contains("OnEditCompleted=\"OnEditCompleted\"", list);
        foreach (var view in new[] { channel, direct })
        {
            var focus = Slice(view, "private async Task FocusComposerAfterEditAsync()", "private void MessagesChanged()");
            Assert.True(focus.IndexOf("if (IsMobileLayout) return", StringComparison.Ordinal) <
                        focus.IndexOf("_focusAfterRender = true", StringComparison.Ordinal));
            Assert.Contains("if (_composer is not null) await _composer.FocusAsync()", view);
        }
        Assert.Contains("IsMobileLayout=\"_isMobileLayout\"", home);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        return source[startIndex..endIndex];
    }

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
