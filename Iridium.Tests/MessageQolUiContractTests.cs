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

    [Fact]
    public void MobileLongPressOpensTheExistingPermissionAwareMessageMenu()
    {
        var root = RepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "Iridium.Web", "wwwroot", "js", "chat.js"));
        var list = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageList.razor"));
        var row = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageRow.razor"));
        var css = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageRow.razor.css"));

        Assert.Contains("messageActionLongPressMilliseconds = 550", script);
        Assert.Contains("mobileLongPressMoveTolerance = 10", script);
        Assert.Contains("event.pointerType !== \"touch\" && event.pointerType !== \"pen\"", script);
        Assert.Contains("longPressMovementExceeded", script);
        Assert.Contains("pointercancel", script);
        Assert.Contains("event.stopImmediatePropagation()", script);
        Assert.Contains("article.message-row[data-message-id]", script);
        Assert.Contains("OpenMessageActionsFromLongPressAsync", script);
        Assert.Contains("CloseMessageActionsFromLongPressAsync", script);
        Assert.Contains("event.key !== \"Escape\"", script);
        Assert.Contains("wireMessageLongPress", list);
        Assert.Contains("unwireMessageLongPress", list);
        Assert.Contains("MessageMenus.Open(id)", list);
        Assert.Contains("message.Kind != MessageKind.User", list);
        Assert.Contains("@onclick=\"ReplyFromMenuAsync\"", row);
        Assert.Contains("@onclick=\"ForwardFromMenu\"", row);
        Assert.Contains("@if (CanEdit)", row);
        Assert.Contains("@if (CanDelete)", row);
        Assert.Contains(".message-row.message-long-pressing", css);
    }

    [Fact]
    public void MobileMessageActionsUseADedicatedBottomSheetWhileDesktopKeepsItsToolbar()
    {
        var root = RepositoryRoot();
        var row = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageRow.razor"));
        var rowCss = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageRow.razor.css"));
        var sheet = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MobileMessageActionSheet.razor"));
        var sheetCss = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MobileMessageActionSheet.razor.css"));
        var script = File.ReadAllText(Path.Combine(root, "Iridium.Web", "wwwroot", "js", "chat.js"));

        Assert.Contains("_menuOpen && !IsMobileLayout", row);
        Assert.Contains("_menuOpen && IsMobileLayout", row);
        Assert.Contains("<MobileMessageActionSheet", row);
        Assert.Contains("OnReply=\"ReplyFromMenuAsync\"", row);
        Assert.Contains("OnForward=\"ForwardFromMenu\"", row);
        Assert.Contains("OnEdit=\"EditFromMenu\"", row);
        Assert.Contains("OnDelete=\"DeleteAsync\"", row);
        Assert.DoesNotContain("@onclick=\"ReplyAsync\"", row[..row.IndexOf("<div class=\"message-actions", StringComparison.Ordinal)]);
        Assert.Contains(".message-actions,.message-menu{display:none}", rowCss);
        Assert.Contains("role=\"dialog\"", sheet);
        Assert.Contains("aria-label=\"Message actions\"", sheet);
        Assert.Contains("await OnClose.InvokeAsync();", sheet);
        Assert.Contains("await action.InvokeAsync();", sheet);
        Assert.Contains("min-height:52px", sheetCss);
        Assert.Contains("env(safe-area-inset-bottom", sheetCss);
        Assert.Contains("max-height:82dvh", sheetCss);
        Assert.Contains("wireMobileMessageActionSheet", script);
        Assert.Contains("shouldDismissMobileMessageActionSheet", script);
        Assert.Contains("requestAnimationFrame(writeDragVisual)", script);
        Assert.Contains("translate3d(0,${drag.renderedY}px,0)", script);
        Assert.Contains("sheet.style.transition = \"none\"", script);
        Assert.Contains("setPointerCapture", script);
        Assert.Contains("releasePointerCapture", script);
        Assert.Contains("mobileMessageSheetBackdropOpacity", script);
        Assert.Contains("mobileMessageSheetSnapMilliseconds = 190", script);
        Assert.Contains("mobileMessageSheetVelocityThreshold = .85", script);
        Assert.Contains("sheet.addEventListener(\"click\", click, true)", script);
        Assert.DoesNotContain("sheet-slide-in 190ms cubic-bezier(.2,.75,.25,1) both", sheetCss);
        Assert.Contains("event.key === \"Escape\"", script);
        Assert.Contains("message-action-sheet-open", script);

        var edit = sheet.IndexOf("<span>Edit</span>", StringComparison.Ordinal);
        var reply = sheet.IndexOf("<span>Reply</span>", StringComparison.Ordinal);
        var forward = sheet.IndexOf("<span>Forward</span>", StringComparison.Ordinal);
        var separator = sheet.IndexOf("class=\"sheet-separator\"", StringComparison.Ordinal);
        var delete = sheet.IndexOf("<span>Delete Message</span>", StringComparison.Ordinal);
        Assert.True(edit >= 0 && reply > edit && forward > reply && separator > forward && delete > separator);
        Assert.Contains("@if (CanEdit)", sheet[..reply]);
        Assert.Contains("@if (CanDelete)", sheet[forward..separator]);
        Assert.DoesNotContain("CanEdit || CanDelete", sheet);
        Assert.Contains("class=\"destructive\"", sheet[separator..delete]);
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
