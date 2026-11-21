using System.Text.Json;
using MediaEngine.Api.Services.Notifications;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Every channel type that ships with the Engine (notifications and delivery spec WP3): the exact request it makes, and that
/// no login or link appears in an error message.
/// </summary>
public sealed class NotificationChannelTemplateTests
{
    private static readonly OutgoingNotification Message = new("backup.failed", "A backup did not finish", "Open your Library to see what happened.");
    private readonly ChannelTypeCatalogue _types = new();

    [Fact]
    public void EveryShippedType_HasAnId_PlainWords_AndOnlyKnownFieldsInItsRequest()
    {
        Assert.Equal(["ntfy", "discord", "telegram", "pushover", "slack", "gotify", "webhook", "apprise"], _types.All.Select(type => type.Id));
        foreach (var type in _types.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(type.Summary), type.Id);
            Assert.False(string.IsNullOrWhiteSpace(type.Help), type.Id);
            Assert.False(string.IsNullOrWhiteSpace(type.WhatLeaves), type.Id);
            var keys = type.Fields.Select(field => field.Key).Concat(["title", "body", "event"]).ToHashSet();
            var tokens = System.Text.RegularExpressions.Regex.Matches(type.Request.Url + string.Concat(type.Request.Headers.Values) + type.Request.Body, @"\{([a-z][a-z0-9_]*)\}");
            Assert.All(tokens, token => Assert.Contains(token.Groups[1].Value, keys));
            Assert.Contains(type.Fields, field => field.Required);
        }
    }

    [Fact]
    public async Task Ntfy_PublishesJsonToTheServer_AndSendsTheTokenOnlyWhenOneIsSet()
    {
        var ntfy = _types.Find("ntfy")!;

        using var open = ChannelRequestRenderer.Build(ntfy, new Dictionary<string, string> { ["server"] = "https://ntfy.sh/", ["topic"] = "library-abc123" }, Message);
        Assert.Equal("https://ntfy.sh/", open.RequestUri!.ToString());
        Assert.False(open.Headers.Contains("Authorization"));
        using var body = JsonDocument.Parse(await open.Content!.ReadAsStringAsync());
        Assert.Equal("library-abc123", body.RootElement.GetProperty("topic").GetString());
        Assert.Equal("A backup did not finish", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("Open your Library to see what happened.", body.RootElement.GetProperty("message").GetString());

        using var protectedRequest = ChannelRequestRenderer.Build(ntfy, new Dictionary<string, string> { ["server"] = "https://ntfy.example.test", ["topic"] = "t", ["access_token"] = "tk_secret" }, Message);
        Assert.Equal("Bearer tk_secret", protectedRequest.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Telegram_PutsTheBotTokenInTheAddressAndTheChatInTheBody()
    {
        using var request = ChannelRequestRenderer.Build(_types.Find("telegram")!, new Dictionary<string, string> { ["bot_token"] = "123456:ABC-def", ["chat_id"] = "-100123" }, Message);

        Assert.Equal("https://api.telegram.org/bot123456:ABC-def/sendMessage", request.RequestUri!.ToString());
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal("-100123", body.RootElement.GetProperty("chat_id").GetString());
        Assert.Equal("A backup did not finish\nOpen your Library to see what happened.", body.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Pushover_SendsAFormWithEveryValueEncoded()
    {
        using var request = ChannelRequestRenderer.Build(_types.Find("pushover")!, new Dictionary<string, string> { ["app_token"] = "apptok", ["user_key"] = "userkey" }, new OutgoingNotification("x", "Tom & Jerry", "50% done"));

        Assert.Equal("https://api.pushover.net/1/messages.json", request.RequestUri!.ToString());
        Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("token=apptok&user=userkey&title=Tom+%26+Jerry&message=50%25+done", await request.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Slack_SendsTheTextToTheLink()
    {
        using var request = ChannelRequestRenderer.Build(_types.Find("slack")!, new Dictionary<string, string> { ["url"] = "https://hooks.slack.com/services/T/B/x" }, Message);

        Assert.Equal("https://hooks.slack.com/services/T/B/x", request.RequestUri!.ToString());
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal("*A backup did not finish*\nOpen your Library to see what happened.", body.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Gotify_PostsToTheServerWithTheApplicationToken()
    {
        using var request = ChannelRequestRenderer.Build(_types.Find("gotify")!, new Dictionary<string, string> { ["server"] = "http://192.168.1.10:8080/", ["app_token"] = "Axyz" }, Message);

        Assert.Equal("http://192.168.1.10:8080/message?token=Axyz", request.RequestUri!.ToString());
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal("A backup did not finish", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(5, body.RootElement.GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task Webhook_AndApprise_SendTheirOwnJsonShape()
    {
        using var webhook = ChannelRequestRenderer.Build(_types.Find("webhook")!, new Dictionary<string, string> { ["url"] = "https://example.test/hook" }, Message);
        using var hookBody = JsonDocument.Parse(await webhook.Content!.ReadAsStringAsync());
        Assert.Equal("backup.failed", hookBody.RootElement.GetProperty("event").GetString());
        Assert.Equal("A backup did not finish", hookBody.RootElement.GetProperty("title").GetString());

        using var apprise = ChannelRequestRenderer.Build(_types.Find("apprise")!, new Dictionary<string, string> { ["url"] = "http://apprise.local:8000/notify/library" }, Message);
        using var appriseBody = JsonDocument.Parse(await apprise.Content!.ReadAsStringAsync());
        Assert.Equal("Open your Library to see what happened.", appriseBody.RootElement.GetProperty("body").GetString());
        Assert.True(_types.Find("apprise")!.Advanced);
    }

    [Theory]
    [InlineData("ntfy")]
    [InlineData("discord")]
    [InlineData("telegram")]
    [InlineData("pushover")]
    [InlineData("slack")]
    [InlineData("gotify")]
    [InlineData("webhook")]
    [InlineData("apprise")]
    public async Task AFailedSend_NeverShowsALoginOrLink(string typeId)
    {
        var type = _types.Find(typeId)!;
        var values = type.Fields.ToDictionary(field => field.Key, field => SampleValue(type, field));
        var handler = new FailingHandler();
        var channel = new TemplatedNotificationChannel($"channel:{typeId}", type.Name, type, values, new SingleClientFactory(handler), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var result = await channel.SendAsync(Message, [], CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        foreach (var value in values.Values.Where(value => value.Length > 4))
        {
            Assert.DoesNotContain(value, result.Error, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("secretpart", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueThatCannotBeAnAddressOrToken_IsRefusedWhenSaved()
    {
        var telegram = _types.Find("telegram")!.Fields.First(field => field.Key == "bot_token");
        var discord = _types.Find("discord")!.Fields.First(field => field.Key == "url");

        Assert.NotNull(NotificationChannelService.ValidateValue(telegram, "123/../../x"));
        Assert.NotNull(NotificationChannelService.ValidateValue(telegram, "tok en"));
        Assert.Null(NotificationChannelService.ValidateValue(telegram, "123456:ABC-def_x.y"));
        Assert.NotNull(NotificationChannelService.ValidateValue(discord, "https://discord.com.evil.example/x"));
        Assert.Null(NotificationChannelService.ValidateValue(discord, "https://canary.discord.com/api/webhooks/1/a"));
    }

    private static string SampleValue(ChannelTypeTemplate type, ChannelFieldTemplate field) => field.Kind switch
    {
        "url" => field.HostSuffixes.Count > 0 ? $"https://{field.HostSuffixes[0]}/path/secretpart" : "https://host.example.test/secretpart",
        "token" => "secretpart1234",
        _ => field.Default.Length > 0 ? field.Default : "Library",
    };

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException($"Connection refused to {request.RequestUri}");
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
