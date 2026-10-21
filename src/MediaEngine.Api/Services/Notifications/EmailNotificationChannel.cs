using System.Text.Json;
using MediaEngine.Contracts.Notifications;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Notifications;

/// <summary>
/// Sends notifications through the mail server the Library already uses for password reset (Settings, Sign-in). One message goes
/// to each administrator who has an email address. The mail server password is read from the config folder's key file each
/// time, so a changed setting applies without a restart.
/// </summary>
internal sealed class EmailNotificationChannel(
    IConfigurationLoader configuration,
    ILogger<EmailNotificationChannel> logger) : INotificationChannel
{
    public string Id => NotificationChannelIds.Email;

    public string Name => "Email";

    public bool IsConfigured => SmtpMailSender.IsConfigured(Load());

    public async Task<ChannelSendResult> SendAsync(OutgoingNotification message, IReadOnlyList<NotificationRecipient> recipients, CancellationToken ct)
    {
        var settings = Load();
        if (!SmtpMailSender.IsConfigured(settings))
        {
            return new ChannelSendResult(false, "Email is not set up.");
        }

        string? firstError = null;
        var sent = 0;
        foreach (var recipient in recipients)
        {
            var result = await SmtpMailSender.SendAsync(settings, recipient.Email, message.Title, message.Body, ct).ConfigureAwait(false);
            if (result.Success)
            {
                sent++;
            }
            else
            {
                firstError ??= result.Error;
            }
        }

        // One administrator getting it counts as delivered; the channel is only failing when nobody could be reached.
        return sent > 0 ? ChannelSendResult.Sent : new ChannelSendResult(false, firstError ?? "The message could not be sent.");
    }

    private PasswordResetDeliverySettings Load()
    {
        var settings = configuration.LoadCore().Auth.PasswordReset;
        settings.Password = ReadMailPassword();
        return settings;
    }

    private string ReadMailPassword()
    {
        var path = Path.Combine(Path.GetFullPath(configuration.ConfigDirectoryPath), ".secrets", "email.json");
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("smtp_password", out var value) ? value.GetString() ?? string.Empty : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            logger.LogWarning(ex, "The mail server password file could not be read.");
            return string.Empty;
        }
    }
}
