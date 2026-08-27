namespace Iridium.Tests;

public sealed class WebSecurityUiTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void MyAccountUsesSharedProfileMediaComponents()
    {
        var source = File.ReadAllText(Path.Combine(Root, "Iridium.Web", "Components", "SettingsView.razor"));
        Assert.Contains("<ProfileAvatar", source);
        Assert.Contains("AvatarRevision=\"Account.AvatarRevision\"", source);
        Assert.Contains("<ProfileBanner", source);
        Assert.Contains("BannerRevision=\"Account.BannerRevision\"", source);
        Assert.DoesNotContain("Account.DisplayName[..1]", source);
    }

    [Fact]
    public void UserAndCommunityBannersUseSeparateResponsiveAspectRules()
    {
        var appStyles = File.ReadAllText(Path.Combine(Root, "Iridium.Web", "wwwroot", "css", "app.css"));
        var profileBannerStyles = File.ReadAllText(Path.Combine(Root, "Iridium.UI", "ProfileBanner.razor.css"));
        var anchoredStyles = File.ReadAllText(Path.Combine(Root, "Iridium.Web", "Components", "AnchoredProfileCard.razor.css"));
        var identityStyles = File.ReadAllText(Path.Combine(Root, "Iridium.Web", "Components", "ProfileIdentityCard.razor.css"));

        Assert.Contains("--user-profile-banner-aspect: 5 / 2", appStyles);
        Assert.Contains("--community-banner-aspect: 16 / 9", appStyles);
        Assert.Contains("object-fit:cover", profileBannerStyles);
        Assert.Contains("aspect-ratio:var(--user-profile-banner-aspect)", anchoredStyles);
        Assert.Contains("aspect-ratio:var(--user-profile-banner-aspect)", identityStyles);
        Assert.DoesNotContain("height:5.8rem", anchoredStyles);
        Assert.DoesNotContain("aspect-ratio:5/1", identityStyles);
    }

    [Fact]
    public void EmojiPickerHasAccessibleLabelWithoutVisibleTitleAndCleansUpOutsideHandlers()
    {
        var picker = File.ReadAllText(Path.Combine(Root, "Iridium.Web", "Components", "EmojiPicker.razor"));
        var script = File.ReadAllText(Path.Combine(Root, "Iridium.Web", "wwwroot", "js", "emojiPicker.js"));
        Assert.Contains("aria-label=\"Emoji picker\"", picker);
        Assert.Contains("aria-label=\"Search emoji\"", picker);
        Assert.DoesNotContain("<strong>Emoji</strong>", picker);
        Assert.Contains("element.contains(event.target)", script);
        Assert.Contains("removeEventListener(\"pointerdown\"", script);
        Assert.Contains("removeEventListener(\"keydown\"", script);
    }

    [Fact]
    public void SecurityFormRejectsMismatchedPasswordsBeforeCallingTheNode()
    {
        var source = File.ReadAllText(Path.Combine(Root, "Iridium.Web", "Components", "SecuritySettings.razor"));
        var mismatch = source.IndexOf("_newPassword != _confirmPassword", StringComparison.Ordinal);
        var request = source.IndexOf("Session.ChangePasswordAsync", StringComparison.Ordinal);
        Assert.True(mismatch >= 0 && request > mismatch);
    }

    [Fact]
    public void PasswordResetRequiresExplicitValidatedRecoveryRoute()
    {
        var home = File.ReadAllText(Path.Combine(Root, "Iridium.Web", "Pages", "Home.razor"));
        var authentication = File.ReadAllText(Path.Combine(Root, "Iridium.Web", "Components", "AuthenticationScreen.razor"));

        Assert.Contains("@page \"/recover-password\"", home);
        Assert.Contains("IsRecoveryRoute()", home);
        Assert.Contains("ValidatePasswordRecoveryAsync(token)", home);
        Assert.Contains("_recoveryState = PasswordRecoveryUiState.Valid", home);
        Assert.Contains("RecoveryState == PasswordRecoveryUiState.Valid", authentication);
        Assert.Contains("!string.IsNullOrWhiteSpace(RecoveryToken)", authentication);
        Assert.DoesNotContain("RecoveryToken is not null", authentication);
        Assert.DoesNotContain("_recoveryUsername", home);
        Assert.DoesNotContain("_recoveryUsername", authentication);
        Assert.DoesNotContain("RecoveryToken=\"_recoveryToken\"", home);
        Assert.Contains("RecoveryToken=\"@_recoveryToken\"", home);
    }

    [Fact]
    public void RecoveryCompletionAndCancellationClearTransientUrlState()
    {
        var home = File.ReadAllText(Path.Combine(Root, "Iridium.Web", "Pages", "Home.razor"));
        Assert.Contains("ClearPasswordRecoveryState();", home);
        Assert.Contains("OnRecoveryCancelled=\"CancelPasswordRecovery\"", home);
        Assert.Contains("Navigation.NavigateTo(Navigation.BaseUri, forceLoad: true, replace: true)", home);
        Assert.DoesNotContain("localStorage", home, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sessionStorage", home, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MobileAuthenticationOwnsOneDynamicViewportScrollSurface()
    {
        var styles = File.ReadAllText(Path.Combine(Root, "Iridium.Web", "Components", "AuthenticationScreen.razor.css"));
        var mobile = styles[styles.IndexOf("@media (max-width: 520px)", StringComparison.Ordinal)..];

        Assert.Contains("height:100dvh", mobile);
        Assert.Contains("overflow-y:auto", mobile);
        Assert.Contains(".auth-card { width:100%;height:auto;max-height:none;min-height:100%;overflow:visible", mobile);
        Assert.Contains("env(safe-area-inset-top,0px)", mobile);
        Assert.Contains("env(safe-area-inset-bottom,0px)", mobile);
        Assert.DoesNotContain(".auth-card { width:100%;height:100dvh", mobile);
    }
}
