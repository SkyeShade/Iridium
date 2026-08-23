namespace Iridium.Server.Configuration;

public sealed class AccountSecurityOptions
{
    public const string SectionName = "AccountSecurity";
    public int RecoveryTokenMinutes { get; set; } = 30;
}
