using System.Runtime.CompilerServices;

namespace Iridium.Tests;

public sealed class FirefoxCompatibilityContractTests
{
    private static readonly string Root = SolutionRoot();

    [Fact]
    public void ClipboardUsesFeatureDetectionAndPasteDoesNotRequireAsyncClipboardRead()
    {
        var compatibility = Source("Iridium.Web", "wwwroot", "js", "browserCompatibility.js");
        var index = Source("Iridium.Web", "wwwroot", "index.html");
        var chat = Source("Iridium.Web", "wwwroot", "js", "chat.js");
        var settings = Source("Iridium.Web", "Components", "CommunitySettingsModal.razor");
        var invite = Source("Iridium.Web", "Components", "InvitePeopleModal.razor");

        Assert.Contains("typeof navigator.clipboard?.writeText === \"function\"", compatibility);
        Assert.Contains("document.execCommand(\"copy\")", compatibility);
        Assert.Contains("activeElement.focus({ preventScroll: true })", compatibility);
        Assert.Contains("js/browserCompatibility.js", index);
        Assert.Contains("iridiumBrowserCompatibility.copyText", settings);
        Assert.Contains("iridiumBrowserCompatibility.copyText", invite);
        Assert.Contains("composerClipboardFiles(event.clipboardData)", chat);
        Assert.Contains("event.clipboardData?.getData(\"text/plain\")", chat);
        Assert.DoesNotContain("navigator.clipboard.read", chat);
        Assert.Contains("item.getAsFile?.()", chat);
        Assert.Contains("Array.from(clipboardData.files || [])", chat);
    }

    [Fact]
    public void PointerAndViewportPathsAreGuardedWithoutUserAgentSniffing()
    {
        var avatar = Source("Iridium.Web", "wwwroot", "js", "avatarEditor.js");
        var floating = Source("Iridium.Web", "wwwroot", "js", "floatingStreamViewer.js");
        var chat = Source("Iridium.Web", "wwwroot", "js", "chat.js");
        var swipe = Source("Iridium.UI", "wwwroot", "js", "mobileConversationSwipe.js");

        Assert.Contains("element?.setPointerCapture?.(pointerId)", avatar);
        Assert.Contains("try { handle.setPointerCapture?.(pointerId); } catch", floating);
        Assert.Contains("try { candidate.element.setPointerCapture?.(event.pointerId); } catch", chat);
        Assert.Contains("try { dragging.row.setPointerCapture?.(event.pointerId); } catch", chat);
        Assert.Contains("window.visualViewport?.height ?? window.innerHeight", swipe);
        Assert.Contains("window.visualViewport?.addEventListener", swipe);
        Assert.DoesNotContain("navigator.userAgent", avatar + floating + chat + swipe);
    }

    [Fact]
    public void FirefoxScrollbarRulesAccompanyChromiumRules()
    {
        var app = Source("Iridium.Web", "wwwroot", "css", "app.css");
        var composer = Source("Iridium.Web", "Components", "MessageComposer.razor.css");
        var markdown = Source("Iridium.Web", "Components", "MarkdownSourceEditor.razor.css");

        Assert.Contains("* { scrollbar-width: auto; scrollbar-color:", app);
        Assert.Contains("*::-webkit-scrollbar", app);
        Assert.Contains(".composer-rich-editor{scrollbar-width:none}", composer);
        Assert.Contains(".composer-rich-editor::-webkit-scrollbar", composer);
        Assert.Contains(".markdown-source-input{scrollbar-width:none}", markdown);
        Assert.Contains(".markdown-source-input::-webkit-scrollbar", markdown);
    }

    [Fact]
    public void MediaEmbedsAndCacheUsePortableCapabilityPaths()
    {
        var liveKit = Source("Iridium.Web", "wwwroot", "js", "liveKitMedia.js");
        var capability = Source("Iridium.Web", "wwwroot", "js", "screenShareCapability.js");
        var cache = Source("Iridium.Web", "wwwroot", "js", "messageHistoryCache.js");
        var index = Source("Iridium.Web", "wwwroot", "index.html");
        var youtube = Source("Iridium.Web", "Components", "YouTubeEmbedCard.razor");

        Assert.Contains("typeof navigator?.mediaDevices?.getDisplayMedia === \"function\"", capability);
        Assert.Contains("if (!navigator.mediaDevices?.getDisplayMedia)", liveKit);
        Assert.Contains("videoCodec: \"vp8\"", liveKit);
        Assert.Contains("indexedDB.open(databaseName, schemaVersion)", cache);
        Assert.Contains("opening.onupgradeneeded", cache);
        Assert.Contains("frame-src https://www.youtube-nocookie.com", index);
        Assert.Contains("sandbox=\"allow-scripts allow-same-origin allow-presentation allow-popups\"", youtube);
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));
    private static string SolutionRoot([CallerFilePath] string sourceFile = "") =>
        Directory.GetParent(Path.GetDirectoryName(sourceFile)!)!.FullName;
}
