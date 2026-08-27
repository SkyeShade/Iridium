using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class ProfileBannerRenderingTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void UserProfileBannerGeometryIsFiveToTwoAndResponsive()
    {
        Assert.Equal(2.5, ProfileBannerLimits.AspectRatio);
        Assert.Equal(200, 500 / ProfileBannerLimits.AspectRatio);
        Assert.Equal(120, 300 / ProfileBannerLimits.AspectRatio);
        Assert.Equal(ProfileBannerLimits.AspectRatio,
            ProfileBannerLimits.ProcessedWidth / (double)ProfileBannerLimits.ProcessedHeight);
    }

    [Fact]
    public void UserBannerSurfacesUseTheScopedAspectTokenWithoutFixedHeights()
    {
        var app = Source("Iridium.Web", "wwwroot", "css", "app.css");
        var identity = Source("Iridium.Web", "Components", "ProfileIdentityCard.razor.css");
        var anchored = Source("Iridium.Web", "Components", "AnchoredProfileCard.razor.css");
        var settings = Source("Iridium.Web", "Components", "SettingsView.razor.css");
        var editor = Source("Iridium.Web", "Components", "BannerEditorModal.razor.css");

        Assert.Contains("--user-profile-banner-aspect: 5 / 2", app);
        Assert.Contains("--community-banner-aspect: 16 / 9", app);
        Assert.Contains(".profile-card-banner{position:relative;z-index:0;grid-column:1;grid-row:1;width:100%;aspect-ratio:var(--user-profile-banner-aspect)", identity);
        Assert.Contains(".profile-banner { position:relative; width:100%; aspect-ratio:var(--user-profile-banner-aspect)", anchored);
        Assert.Contains(".account-banner{position:relative;z-index:0;width:auto;aspect-ratio:var(--user-profile-banner-aspect)", settings);
        Assert.Contains(".preset-slot{position:relative;width:100%;min-width:0;aspect-ratio:var(--user-profile-banner-aspect)", editor);
        Assert.Contains(".banner-editor.community .preset-slot{aspect-ratio:var(--community-banner-aspect)}", editor);
        Assert.DoesNotContain(".profile-banner { position:relative; height:", anchored);
        Assert.DoesNotContain(".account-banner{position:relative;height:", settings);
    }

    [Fact]
    public void EveryUserProfileCardRoutesItsBannerThroughTheSharedRenderer()
    {
        var identity = Source("Iridium.Web", "Components", "ProfileIdentityCard.razor");
        var anchored = Source("Iridium.Web", "Components", "AnchoredProfileCard.razor");
        var settings = Source("Iridium.Web", "Components", "SettingsView.razor");
        var banner = Source("Iridium.UI", "ProfileBanner.razor");
        var bannerStyles = Source("Iridium.UI", "ProfileBanner.razor.css");

        Assert.Contains("<ProfileBanner AccountId=\"AccountId\"", identity);
        Assert.Contains("<ProfileBanner AccountId=\"AccountId\"", anchored);
        Assert.Contains("<ProfileBanner AccountId=\"Account.Id\"", settings);
        Assert.Contains("<AvatarCroppedPreview State=\"EditState\"", banner);
        Assert.Contains("BannerState(banner, banner.SourceUrl)", banner);
        Assert.Contains("<AvatarCroppedPreview State=\"BannerState(banner)\"", banner);
        Assert.Contains("object-fit:cover", bannerStyles);
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));
}
