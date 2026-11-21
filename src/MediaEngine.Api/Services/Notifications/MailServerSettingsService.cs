using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Notifications;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Notifications;

/// <summary>
/// Reads, tests and saves the one mail server the Library sends through (Notifications and delivery spec WP1). The settings live
/// in the <c>auth.password_reset</c> section of the core config and the password in the config folder's key file, which is where
/// the Dashboard's password reset already reads them, so one form feeds both.
/// </summary>
internal sealed class MailServerSettingsService(IConfigurationLoader configuration, ILogger<MailServerSettingsService> logger)
{
    private static readonly JsonSerializerOptions SecretJson = new() { WriteIndented = true };
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public MailServerSettingsDto Get()
    {
        var settings = LoadSaved();
        return new MailServerSettingsDto
        {
            Configured = SmtpMailSender.IsConfigured(settings),
            Host = settings.SmtpHost,
            Port = settings.SmtpPort,
            Encrypted = settings.UseStartTls,
            Username = settings.Username,
            FromAddress = settings.FromAddress,
            FromName = settings.FromName,
            HasPassword = !string.IsNullOrEmpty(settings.Password),
        };
    }

    /// <summary>Plain-words problem with the form, or null when it can be tried.</summary>
    public static string? Validate(MailServerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Host) || request.Host.Any(char.IsWhiteSpace))
        {
            return "Enter the mail server's address, such as smtp.gmail.com.";
        }

        if (request.Port is < 1 or > 65535)
        {
            return "The port is a number between 1 and 65535, usually 587.";
        }

        if (!IsAddress(request.FromAddress))
        {
            return "Enter the email address the messages should come from.";
        }

        return null;
    }

    /// <summary>Sends one message to the signed-in administrator with the form's values, without saving anything.</summary>
    public async Task<NotificationTestResponse> TestAsync(MailServerRequest request, string recipientEmail, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
        {
            return new NotificationTestResponse { Success = false, Message = problem };
        }

        if (!IsAddress(recipientEmail))
        {
            return new NotificationTestResponse { Success = false, Message = "Your account has no email address to send the test to." };
        }

        var settings = ToSettings(request, LoadSaved().Password);
        var result = await SmtpMailSender.SendAsync(
            settings, recipientEmail, NotificationEventCatalogue.TestTitle, "This is a test message from your Library. Nothing is wrong.", ct).ConfigureAwait(false);
        return new NotificationTestResponse
        {
            Success = result.Success,
            Message = result.Success ? $"A test message was sent to {recipientEmail}." : result.Error ?? "The test message could not be sent.",
        };
    }

    /// <summary>Saves the form. Returns a plain-words problem, or null when it was saved.</summary>
    public async Task<string?> SaveAsync(MailServerRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var core = configuration.LoadCore();
            var saved = ToSettings(request, ReadPassword());
            core.Auth.PasswordReset.Mode = "Smtp";
            core.Auth.PasswordReset.SmtpHost = saved.SmtpHost;
            core.Auth.PasswordReset.SmtpPort = saved.SmtpPort;
            core.Auth.PasswordReset.UseStartTls = saved.UseStartTls;
            core.Auth.PasswordReset.Username = saved.Username;
            core.Auth.PasswordReset.FromAddress = saved.FromAddress;
            core.Auth.PasswordReset.FromName = saved.FromName;
            if (request.Password is not null)
            {
                await WritePasswordAsync(request.Password, ct).ConfigureAwait(false);
            }

            configuration.SaveCore(core);
            return null;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static PasswordResetDeliverySettings ToSettings(MailServerRequest request, string savedPassword) => new()
    {
        Mode = "Smtp",
        SmtpHost = request.Host.Trim(),
        SmtpPort = request.Port,
        UseStartTls = request.Encrypted,
        Username = request.Username.Trim(),
        Password = request.Password ?? savedPassword,
        FromAddress = request.FromAddress.Trim(),
        FromName = string.IsNullOrWhiteSpace(request.FromName) ? "Library" : request.FromName.Trim(),
    };

    private PasswordResetDeliverySettings LoadSaved()
    {
        var settings = configuration.LoadCore().Auth.PasswordReset;
        settings.Password = ReadPassword();
        return settings;
    }

    private static bool IsAddress(string value) => MailAddress.TryCreate(value?.Trim(), out var address) && address.Address == value!.Trim();

    private string SecretPath => Path.Combine(Path.GetFullPath(configuration.ConfigDirectoryPath), ".secrets", "email.json");

    private string ReadPassword()
    {
        if (!File.Exists(SecretPath))
        {
            return string.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<MailSecrets>(File.ReadAllText(SecretPath))?.SmtpPassword ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "The mail server password file could not be read.");
            return string.Empty;
        }
    }

    private async Task WritePasswordAsync(string password, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SecretPath)!);
        var temporary = $"{SecretPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new MailSecrets { SmtpPassword = password }, SecretJson), ct).ConfigureAwait(false);
            File.Move(temporary, SecretPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed class MailSecrets
    {
        [JsonPropertyName("smtp_password")]
        public string SmtpPassword { get; set; } = string.Empty;
    }
}
