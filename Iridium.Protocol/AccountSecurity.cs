namespace Iridium.Protocol;

public static class AccountSecurityLimits
{
    public const int MaximumPasswordLength = 256;
    public const int MinimumPasswordLength = 8;
    public const int MaximumRecoveryEmailLength = 320;
    public const int MaximumRecoveryTokenLength = 256;
}

public sealed record AccountSecurityStatusDto(
    bool HasRecoveryEmail,
    string? MaskedRecoveryEmail,
    bool RecoveryDeliveryAvailable = true);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword, string ConfirmNewPassword);
public sealed record UpdateRecoveryEmailRequest(string CurrentPassword, string? RecoveryEmail);
public sealed record PasswordRecoveryRequest(string Username);
public sealed record CompletePasswordRecoveryRequest(
    string Username,
    string Token,
    string NewPassword,
    string ConfirmNewPassword);
public sealed record PasswordRecoveryRequestResultDto(string Message);
public sealed record ValidatePasswordRecoveryRequest(string Token);
public sealed record PasswordRecoveryValidationResultDto(bool IsValid, string Message);
