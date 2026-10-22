using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Authentication;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Services;

/// <summary>The result of one mail send. <see cref="Error"/> is plain words with no secrets or server details.</summary>
public sealed record MailSendResult(bool Success, string? Error)
{
    public static readonly MailSendResult Sent = new(true, null);
}

/// <summary>
/// Sends one plain-text email through the Library's configured mail server. Shared by password reset (from the Dashboard) and
/// the notification service (from the Engine), so there is one place that knows how to talk to the mail server.
/// </summary>
public static class SmtpMailSender
{
    public static bool IsConfigured(PasswordResetDeliverySettings settings) =>
        settings.Mode.Equals("Smtp", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(settings.SmtpHost)
        && settings.SmtpPort is > 0 and <= 65535
        && !string.IsNullOrWhiteSpace(settings.FromAddress);

    public static async Task<MailSendResult> SendAsync(
        PasswordResetDeliverySettings settings,
        string toAddress,
        string subject,
        string body,
        CancellationToken ct)
    {
        if (!IsConfigured(settings))
        {
            return new MailSendResult(false, "Email is not set up.");
        }

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(settings.FromAddress, settings.FromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = false,
            };
            message.To.Add(new MailAddress(toAddress));
            using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
            {
                EnableSsl = settings.UseStartTls,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Timeout = 30_000,
            };
            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                client.Credentials = new NetworkCredential(settings.Username, settings.Password);
            }

            await client.SendMailAsync(message, ct).ConfigureAwait(false);
            return MailSendResult.Sent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SmtpException ex)
        {
            return new MailSendResult(false, Describe(ex));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or SocketException or IOException or AuthenticationException or OperationCanceledException)
        {
            return new MailSendResult(false, "The mail server could not be reached.");
        }
    }

    internal static string Describe(SmtpException ex) => ex.StatusCode switch
    {
        SmtpStatusCode.MustIssueStartTlsFirst => "The mail server needs an encrypted connection.",
        SmtpStatusCode.ClientNotPermitted or SmtpStatusCode.MailboxUnavailable => "The mail server refused the message.",
        SmtpStatusCode.GeneralFailure => "The mail server could not be reached.",
        _ when ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) => "The mail server did not accept the sign-in.",
        _ => "The mail server could not send the message.",
    };
}
