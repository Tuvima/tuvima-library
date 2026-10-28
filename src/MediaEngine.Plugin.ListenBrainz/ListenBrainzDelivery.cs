using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using MediaEngine.Plugins;

namespace MediaEngine.Plugin.ListenBrainz;

/// <summary>
/// Sends one queued listen to ListenBrainz with the token the profile linked. The token is read only for the profile the play
/// belongs to, only while sending, and never written anywhere (not into a note, a log or the queue).
/// </summary>
internal sealed class ListenBrainzDelivery : IPluginOutboxDelivery
{
    internal static readonly Uri SubmitListens = new($"https://{ListenBrainzPlugin.Host}/1/submit-listens");

    public string Kind => "outbox-delivery";

    public async Task<PluginDeliveryResult> DeliverAsync(PluginOutboxItem item, IPluginExecutionContext context, CancellationToken cancellationToken)
    {
        var token = await item.ReadCredentialAsync(ListenBrainzPlugin.TokenName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return PluginDeliveryResult.GiveUp("This profile is no longer linked to ListenBrainz.");
        }

        JsonNode? listen;
        try
        {
            listen = JsonNode.Parse(item.PayloadJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return PluginDeliveryResult.GiveUp("A queued play could not be read.");
        }

        var body = new JsonObject
        {
            ["listen_type"] = "single",
            ["payload"] = new JsonArray(listen),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, SubmitListens)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token.Trim());

        HttpResponseMessage response;
        try
        {
            response = await context.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return PluginDeliveryResult.RetryLater("ListenBrainz could not be reached.");
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            // A token that cannot be sent as a header (for example one pasted with a line break). The reason is fixed text so the token is never echoed.
            return PluginDeliveryResult.GiveUp("The saved token could not be used. Link it again on your profile.");
        }

        using (response)
        {
            return Classify(response.StatusCode);
        }
    }

    internal static PluginDeliveryResult Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.OK => PluginDeliveryResult.Delivered(),
        HttpStatusCode.Unauthorized => PluginDeliveryResult.GiveUp("ListenBrainz did not accept the token. Link it again on your profile."),
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => PluginDeliveryResult.GiveUp("ListenBrainz refused this play."),
        HttpStatusCode.TooManyRequests => PluginDeliveryResult.RetryLater("ListenBrainz asked us to slow down."),
        _ => PluginDeliveryResult.RetryLater($"ListenBrainz answered with {(int)status}."),
    };
}
