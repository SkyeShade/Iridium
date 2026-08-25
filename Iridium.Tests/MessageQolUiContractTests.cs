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

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
