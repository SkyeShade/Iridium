namespace Iridium.Tests;

public sealed class ProfileSurfaceLayoutTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void IdentityAvatarIsASiblingLayerAboveTheBannerAndBody()
    {
        var markup = Source("Iridium.Web", "Components", "ProfileIdentityCard.razor");
        var styles = Source("Iridium.Web", "Components", "ProfileIdentityCard.razor.css");
        var bannerStart = markup.IndexOf("<div class=\"profile-card-banner\">", StringComparison.Ordinal);
        var bannerClose = markup.IndexOf("</div>", bannerStart, StringComparison.Ordinal);
        var avatarStart = markup.IndexOf("<div class=\"profile-card-avatar-position\">", StringComparison.Ordinal);

        Assert.True(bannerStart >= 0 && bannerClose > bannerStart && avatarStart > bannerClose);
        Assert.Contains("isolation:isolate;display:grid", styles);
        Assert.Contains(".profile-card-banner{position:relative;z-index:0;grid-column:1;grid-row:1;width:100%;aspect-ratio:var(--user-profile-banner-aspect);overflow:hidden", styles);
        Assert.Contains(".profile-card-avatar-position{position:relative;z-index:2;grid-column:1;grid-row:1", styles);
        Assert.Contains(".profile-card-body{position:relative;z-index:1", styles);
    }

    [Fact]
    public void EditAndAccountMenuUseExplicitAvatarScaleVariants()
    {
        var styles = Source("Iridium.Web", "Components", "ProfileIdentityCard.razor.css");
        var edit = Source("Iridium.Web", "Components", "EditProfileModal.razor");
        var bannerEditor = Source("Iridium.Web", "Components", "BannerEditorModal.razor");
        var popup = Source("Iridium.Web", "Components", "ProfilePopup.razor");

        Assert.Contains("--profile-card-avatar-size:4rem", styles);
        Assert.Contains(".profile-identity-card.edit-preview-card{--profile-card-avatar-size:4.5rem}", styles);
        Assert.Contains(".profile-identity-card.account-menu-card{--profile-card-avatar-size:5.25rem}", styles);
        Assert.Contains("Class=\"edit-preview-card\"", edit);
        Assert.Contains("Class=\"edit-preview-card\"", bannerEditor);
        Assert.Contains("Class=\"account-menu-card\"", popup);
    }

    [Fact]
    public void DesktopAccountMenuMatchesSidebarWidthWhileMobileKeepsRoomierLayout()
    {
        var styles = Source("Iridium.Web", "Components", "ProfilePopup.razor.css");

        var appStyles = Source("Iridium.Web", "wwwroot", "css", "app.css");
        var shellStyles = Source("Iridium.UI", "ApplicationShell.razor.css");
        var panelStyles = Source("Iridium.UI", "ProfilePanel.razor.css");

        Assert.Contains("--desktop-profile-region-width: calc(var(--desktop-community-rail-width) + var(--desktop-secondary-sidebar-width))", appStyles);
        Assert.Contains("grid-template-columns: var(--desktop-community-rail-width) var(--desktop-secondary-sidebar-width)", shellStyles);
        Assert.Contains("margin:var(--bottom-control-inset)", panelStyles);
        Assert.Contains("width:calc(var(--desktop-profile-region-width) - var(--bottom-control-inset) - var(--bottom-control-inset))", styles);
        Assert.Contains("padding: 0 0 5.3rem var(--bottom-control-inset)", styles);
        Assert.Contains("width:min(23rem,calc(100vw - 2rem))", styles);
        Assert.Contains("text-overflow: ellipsis", styles);
    }

    [Fact]
    public void AccountMenuHasMobileOnlyStickyCloseControlUsingExistingCancelAction()
    {
        var markup = Source("Iridium.Web", "Components", "ProfilePopup.razor");
        var styles = Source("Iridium.Web", "Components", "ProfilePopup.razor.css");
        var mobile = styles[styles.IndexOf("@media (max-width: 860px)", StringComparison.Ordinal)..];

        Assert.Contains("aria-label=\"Close profile menu\" @onclick=\"OnCancel\"", markup);
        Assert.Contains(".mobile-profile-close{display:none}", styles);
        Assert.Contains(".mobile-profile-close{position:sticky;z-index:3;top:.65rem;width:2.75rem;height:2.75rem;display:grid", mobile);
    }

    [Fact]
    public void MyAccountUsesBalancedCappedGeometry()
    {
        var styles = Source("Iridium.Web", "Components", "SettingsView.razor.css");
        var markup = Source("Iridium.Web", "Components", "SettingsView.razor");

        Assert.Contains("width:min(38rem,100%)", styles);
        Assert.Contains("aspect-ratio:var(--user-profile-banner-aspect)", styles);
        Assert.Contains("flex:0 0 6.5rem;width:6.5rem;height:6.5rem", styles);
        Assert.Contains("margin-top:-3.35rem", styles);
        Assert.Contains("class=\"account-identity\"", markup);
    }

    [Fact]
    public void AnchoredMemberCardGeometryRemainsIndependent()
    {
        var anchored = Source("Iridium.Web", "Components", "AnchoredProfileCard.razor.css");

        Assert.Contains("width:18.5rem", anchored);
        Assert.Contains("margin:-2.6rem 0 0 .85rem", anchored);
        Assert.DoesNotContain("edit-preview-card", anchored);
        Assert.DoesNotContain("account-menu-card", anchored);
    }

    [Fact]
    public void MessageAndMemberAnchoredCardsOverlayOnlyANonBaseActiveChatAvatar()
    {
        var identity = Source("Iridium.Web", "Components", "ProfileIdentityCard.razor");
        var accountPopup = Source("Iridium.Web", "Components", "ProfilePopup.razor");
        var anchored = Source("Iridium.Web", "Components", "AnchoredProfileCard.razor");
        var styles = Source("Iridium.Web", "Components", "AnchoredProfileCard.razor.css");
        var avatarStyles = Source("Iridium.UI", "ProfileAvatar.razor.css");
        var memberSidebar = Source("Iridium.Web", "Components", "CommunityMemberSidebar.razor");
        var messageRow = Source("Iridium.Web", "Components", "MessageRow.razor");
        var friends = Source("Iridium.Web", "Components", "FriendsView.razor");
        var direct = Source("Iridium.Web", "Components", "DirectParticipantSidebar.razor");

        Assert.DoesNotContain("active-chat-avatar-badge", identity);
        Assert.DoesNotContain("ActiveChatAvatarPresetId", accountPopup);
        Assert.Contains("private string EffectiveDisplayName => DisplayName;", anchored);
        Assert.Contains("private Guid? EffectiveAvatarPresetId => AvatarPresetId;", anchored);
        Assert.Contains("SelectedChatAvatarPresetId", anchored);
        Assert.Contains("@if (ShowActiveAvatarBadge)", anchored);
        Assert.Contains("private bool ShowActiveAvatarBadge => OptimisticMessageGrouping.HasActivePersona(SelectedChatProfilePresetId);", anchored);
        Assert.Contains("TargetMember?.ProfilePresetId", anchored);
        Assert.Contains("<div class=\"profile-avatar-shell\">", anchored);
        Assert.Contains("<span class=\"active-chat-avatar-badge\"", anchored);
        Assert.DoesNotContain("RenderFragment ActiveAvatarBadge", anchored);
        Assert.DoesNotContain("builder.OpenElement", anchored);
        Assert.Contains(".active-chat-avatar-badge", styles);
        Assert.Contains("--active-avatar-size:calc(var(--profile-avatar-size) / 2)", styles);
        Assert.Contains(".profile-avatar-shell", styles);
        Assert.Contains("overflow:visible", styles);
        Assert.Contains("top:-.1rem", styles);
        Assert.Contains("right:-.55rem", styles);
        Assert.Contains("z-index:3", styles);
        Assert.Contains(".presence { position:absolute; right:var(--badge-offset); bottom:var(--badge-offset)", avatarStyles);
        Assert.Contains("ActiveChatAvatarPresetId=\"profile.ActiveChatAvatarPresetId\"", memberSidebar);
        Assert.Contains("ActiveChatAvatarPresetId=\"CurrentCommunityAuthor?.ActiveChatAvatarPresetId\"", messageRow);
        Assert.DoesNotContain("ActiveChatAvatarPresetId", friends);
        Assert.DoesNotContain("ActiveChatAvatarPresetId", direct);
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));
}
