using System.Net;
using System.Net.Mail;
using Iridium.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Iridium.Server.Security;

public interface IRecoveryEmailSender
{
    bool IsConfigured { get; }
    Task<bool> SendPasswordRecoveryAsync(string recipient, Uri recoveryUri,
        CancellationToken cancellationToken = default);
}

public sealed class SmtpRecoveryEmailSender(IOptions<EmailOptions> options, ILogger<SmtpRecoveryEmailSender> logger)
    : IRecoveryEmailSender
{
    private EmailOptions Settings => options.Value;
    public bool IsConfigured => Settings.Enabled && !string.IsNullOrWhiteSpace(Settings.Host) &&
                                !string.IsNullOrWhiteSpace(Settings.FromAddress) && Settings.Port is > 0 and <= 65535;

    public async Task<bool> SendPasswordRecoveryAsync(string recipient, Uri recoveryUri,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return false;
        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(Settings.FromAddress!, Settings.FromName),
                Subject = "Reset your Iridium password",
                Body = $"Open this link to reset your password:\n\n{recoveryUri}\n\n" +
                       "This link expires soon and can be used only once. If you did not request it, ignore this email.",
                IsBodyHtml = false
            };
            message.To.Add(new MailAddress(recipient));
            using var client = new SmtpClient(Settings.Host!, Settings.Port)
            {
                EnableSsl = Settings.UseSsl,
                UseDefaultCredentials = string.IsNullOrWhiteSpace(Settings.Username),
                Credentials = string.IsNullOrWhiteSpace(Settings.Username)
                    ? CredentialCache.DefaultNetworkCredentials
                    : new NetworkCredential(Settings.Username, Settings.Password)
            };
            await client.SendMailAsync(message, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            logger.LogError("Password recovery email delivery failed");
            return false;
        }
    }
}
