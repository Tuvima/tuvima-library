using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Services.Configuration;

namespace MediaEngine.Web.Services.Integration;

public sealed class PasswordResetEmailSender(
    PasswordResetDeliverySettings settings,
    DashboardConfigurationReader configuration,
    ILogger<PasswordResetEmailSender> logger)
{
    // Read on every use so a changed public address applies without restarting the Dashboard.
    private string PublicAddressValue => configuration.LoadNetwork().Remote.PublicHostname?.Trim() ?? string.Empty;

    public bool IsConfigured => settings.Mode.Equals("Smtp", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(settings.SmtpHost)
        && !string.IsNullOrWhiteSpace(settings.FromAddress)
        && PublicAddress.IsValid(PublicAddressValue);

    public async Task<bool> SendAsync(string email, string token, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return false;
        }

        var baseUri = new Uri(PublicAddressValue.TrimEnd('/') + "/", UriKind.Absolute);
        var resetUri = new Uri(baseUri, $"auth/reset?token={Uri.EscapeDataString(token)}");
        return await SendMessageAsync(
            email,
            "Reset your Library password",
            $"A password reset was requested for your Library account.\n\nOpen this link within 30 minutes:\n{resetUri}\n\nIf you did not request this, you can ignore this email.",
            ct).ConfigureAwait(false);
    }

    public Task<bool> SendTestAsync(string email, CancellationToken ct) => SendMessageAsync(
        email,
        "Library email test",
        "Email delivery is configured correctly for this Library server. No action is required.",
        ct);

    private async Task<bool> SendMessageAsync(string email, string subject, string body, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return false;
        }

        var result = await SmtpMailSender.SendAsync(settings, email, subject, body, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            logger.LogError("Password reset email delivery failed: {Reason}", result.Error);
        }

        return result.Success;
    }
}
