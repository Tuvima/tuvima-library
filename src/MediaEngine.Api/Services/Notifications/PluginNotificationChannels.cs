using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.Declarative;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Notifications;

/// <summary>Channels that come and go while the Library runs, such as those added by plugins (Developer tools spec WP24).</summary>
internal interface INotificationChannelSource
{
    IReadOnlyList<INotificationChannel> GetChannels();
}

/// <summary>
/// Offers every notification channel a switched-on JSON plugin declares. A channel is one web request to an address the plugin
/// declared and the administrator approved; its logins come from the plugin's secrets store and are never in the file, in the
/// routing page, or in an error message. It takes part in routing, quiet hours, the rate limit and the five-failures switch-off
/// exactly like email.
/// </summary>
internal sealed partial class PluginNotificationChannelSource(
    PluginCatalog catalog,
    IPluginExecutionContextFactory contexts,
    ILogger<PluginNotificationChannelSource> logger) : INotificationChannelSource
{
    public const string IdPrefix = "plugin:";

    public IReadOnlyList<INotificationChannel> GetChannels() =>
    [
        .. catalog.List()
            .Where(registration => registration.Enabled && registration.LoadError is null)
            .SelectMany(registration => registration.Capabilities
                .OfType<DeclarativeNotificationChannelCapability>()
                .Select(capability => (INotificationChannel)new PluginNotificationChannel(registration.Manifest, capability.Channel, contexts, logger))),
    ];

    internal sealed class PluginNotificationChannel(
        PluginManifest manifest,
        DeclarativeNotificationChannel channel,
        IPluginExecutionContextFactory contexts,
        ILogger logger) : INotificationChannel
    {
        private const int MaxFieldLength = 1000;

        public string Id => $"{IdPrefix}{manifest.Id}:{channel.Key}";

        public string Name => $"{channel.Name} ({manifest.Name})";

        public bool IsConfigured => true;

        public async Task<ChannelSendResult> SendAsync(OutgoingNotification message, IReadOnlyList<NotificationRecipient> recipients, CancellationToken ct)
        {
            try
            {
                await using var context = contexts.Create(manifest.Id, $"notification:{channel.Key}");
                using var request = new HttpRequestMessage(HttpMethod.Post, channel.Url);
                foreach (var (name, template) in channel.Headers)
                {
                    var value = await FillHeaderAsync(template, context, ct).ConfigureAwait(false);
                    if (value is null)
                    {
                        return new ChannelSendResult(false, "A login this channel needs has not been entered yet.");
                    }

                    request.Headers.TryAddWithoutValidation(name, value);
                }

                request.Content = new StringContent(Render(channel.BodyTemplate, message, channel.ContentType), Encoding.UTF8, channel.ContentType);
                using var response = await context.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                return response.IsSuccessStatusCode
                    ? ChannelSendResult.Sent
                    : new ChannelSendResult(false, $"{channel.Url.Host} answered {(int)response.StatusCode}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The error text can carry the address or a response body; only plain words travel on.
                logger.LogWarning(ex, "Notification channel {Channel} could not send.", Id);
                return new ChannelSendResult(false, ex is HttpRequestException or PluginLimitExceededException or UnauthorizedAccessException or InvalidOperationException
                    ? $"{channel.Url.Host} could not be reached or refused the request."
                    : "The message could not be sent.");
            }
        }

        private static async Task<string?> FillHeaderAsync(string template, IPluginExecutionContext context, CancellationToken ct)
        {
            var result = new StringBuilder();
            var last = 0;
            foreach (Match match in Token().Matches(template))
            {
                result.Append(template, last, match.Index - last);
                last = match.Index + match.Length;
                var token = match.Groups["token"].Value;
                if (token.StartsWith("secret:", StringComparison.Ordinal))
                {
                    var secret = await context.Secrets.GetAsync(token["secret:".Length..], ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(secret) || secret.Any(char.IsControl))
                    {
                        return null;
                    }

                    result.Append(secret);
                }
                else
                {
                    result.Append(match.Value);
                }
            }

            result.Append(template, last, template.Length - last);
            return result.ToString();
        }

        /// <summary>Fills {title}, {body} and {event}, escaping each value for the content type so a message can never break out of the shape.</summary>
        internal static string Render(string template, OutgoingNotification message, string contentType) =>
            Token().Replace(template, match => match.Groups["token"].Value switch
            {
                "title" => Escape(message.Title, contentType),
                "body" => Escape(message.Body, contentType),
                "event" => Escape(message.EventId, contentType),
                _ => match.Value,
            });

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
    }

    [GeneratedRegex(@"\{(?<token>[a-z][a-z0-9_:\-]{0,63})\}")]
    private static partial Regex Token();
}
