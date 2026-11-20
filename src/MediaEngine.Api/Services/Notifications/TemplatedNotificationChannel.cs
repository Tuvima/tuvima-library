using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.Services.Notifications;

/// <summary>Turns a channel type's request template, the person's form values and one message into a web request.</summary>
internal static partial class ChannelRequestRenderer
{
    private const int MaxFieldLength = 1000;

    public static HttpRequestMessage Build(ChannelTypeTemplate type, IReadOnlyDictionary<string, string> values, OutgoingNotification message)
    {
        var template = type.Request;
        var url = Token().Replace(template.Url, match => UrlValue(type, values, match.Groups["token"].Value) ?? match.Value);
        var request = new HttpRequestMessage(new HttpMethod(template.Method), url);
        foreach (var (name, headerTemplate) in template.Headers)
        {
            // A header that needs an optional value the person left blank (an access token) is simply not sent.
            if (Token().Matches(headerTemplate).Any(match => !IsKnown(values, match.Groups["token"].Value)))
            {
                continue;
            }

            var value = Token().Replace(headerTemplate, match => Value(values, message, match.Groups["token"].Value, "text/plain") ?? match.Value);
            if (value.Any(char.IsControl))
            {
                throw new InvalidOperationException("A header value held a control character.");
            }

            request.Headers.TryAddWithoutValidation(name, value);
        }

        var body = Token().Replace(template.Body, match => Value(values, message, match.Groups["token"].Value, template.ContentType) ?? match.Value);
        request.Content = new StringContent(body, Encoding.UTF8, template.ContentType);
        return request;
    }

    /// <summary>Kind <c>url</c> fields go in whole (an address); anything else in an address is escaped.</summary>
    private static string? UrlValue(ChannelTypeTemplate type, IReadOnlyDictionary<string, string> values, string token)
    {
        if (!values.TryGetValue(token, out var value))
        {
            return null;
        }

        var input = type.Fields.FirstOrDefault(candidate => candidate.Key == token);
        // Addresses go in whole, and so do access tokens (their characters were checked when saved: a bot token holds a colon).
        return input?.Kind == "url" ? value.Trim().TrimEnd('/') : input?.Kind == "token" ? value.Trim() : Uri.EscapeDataString(value.Trim());
    }

    private static bool IsKnown(IReadOnlyDictionary<string, string> values, string token) =>
        token is "title" or "body" or "event" || (values.TryGetValue(token, out var value) && !string.IsNullOrEmpty(value));

    private static string? Value(IReadOnlyDictionary<string, string> values, OutgoingNotification message, string token, string contentType) => token switch
    {
        "title" => Escape(message.Title, contentType),
        "body" => Escape(message.Body, contentType),
        "event" => Escape(message.EventId, contentType),
        _ => values.TryGetValue(token, out var value) ? Escape(value, contentType) : null,
    };

    private static string Escape(string value, string contentType)
    {
        var text = value.Length > MaxFieldLength ? value[..MaxFieldLength] : value;
        return contentType switch
        {
            "application/json" => JsonSerializer.Serialize(text)[1..^1],
            "application/x-www-form-urlencoded" => WebUtility.UrlEncode(text),
            _ => text,
        };
    }

    [GeneratedRegex(@"\{(?<token>[a-z][a-z0-9_]{0,63})\}")]
    private static partial Regex Token();
}

/// <summary>
/// One channel the person added from Settings (a Discord channel, an ntfy topic): a channel type's request filled with that
/// channel's own values. It takes part in routing, quiet hours, the rate limit and the five-failures switch-off like email.
/// Logins and links never appear in an error message.
/// </summary>
internal sealed class TemplatedNotificationChannel(
    string id,
    string name,
    ChannelTypeTemplate type,
    IReadOnlyDictionary<string, string> values,
    IHttpClientFactory httpClients,
    ILogger logger) : INotificationChannel
{
    public const string HttpClientName = "notification-channels";

    public string Id => id;

    public string Name => name;

    public string TypeId => type.Id;

    public bool IsConfigured => type.Fields.Where(input => input.Required).All(input => values.TryGetValue(input.Key, out var value) && !string.IsNullOrWhiteSpace(value));

    public bool NeedsRecipients => false;

    public async Task<ChannelSendResult> SendAsync(OutgoingNotification message, IReadOnlyList<NotificationRecipient> recipients, CancellationToken ct)
    {
        try
        {
            using var request = ChannelRequestRenderer.Build(type, values, message);
            using var client = httpClients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? ChannelSendResult.Sent
                : new ChannelSendResult(false, $"{type.Name} answered {(int)response.StatusCode}. Check the {FirstFieldLabel()}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The exception text can carry the address; only plain words travel on.
            logger.LogWarning("Notification channel {Channel} ({Type}) could not send: {Kind}.", id, type.Id, ex.GetType().Name);
            return new ChannelSendResult(false, ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException
                ? $"{type.Name} could not be reached or refused the request."
                : "The message could not be sent.");
        }
    }

    private string FirstFieldLabel() => (type.Fields.FirstOrDefault(field => field.Required)?.Label ?? "settings").ToLowerInvariant();
}
