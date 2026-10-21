using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Settings;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Integration health (Developer tools spec D-D, D-W, WP31): the four health words for applications and webhooks, and the one
/// case that shades the Integrations menu row red.
/// </summary>
public sealed class IntegrationHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnApplicationWithAKeyExpiringSoon_NeedsAttention_AndSaysWhich()
    {
        var app = App(Key("Home Assistant", expires: Now.AddDays(5), lastUsed: Now.AddDays(-1)));

        var health = StateCardHealth.ForApplication(app, Now);

        Assert.Equal(HealthWord.NeedsAttention, health.Word);
        Assert.Contains("Home Assistant", health.LastProblem);
        Assert.Contains("5 days", health.LastProblem);
    }

    [Fact]
    public void AKeyUnusedFor90Days_NeedsAttention_ButARevokedOrExpiredOneSaysNothing()
    {
        Assert.Equal(HealthWord.NeedsAttention,
            StateCardHealth.ForApplication(App(Key("Old", expires: null, lastUsed: Now.AddDays(-120))), Now).Word);
        Assert.Equal(HealthWord.Working,
            StateCardHealth.ForApplication(App(Key("Gone", expires: null, lastUsed: Now.AddDays(-120), revoked: true)), Now).Word);
        Assert.Equal(HealthWord.Working,
            StateCardHealth.ForApplication(App(Key("Lapsed", expires: Now.AddDays(-3), lastUsed: Now.AddDays(-1))), Now).Word);
    }

    [Fact]
    public void ADisabledApplication_IsOff_EvenWithAnOldKey() =>
        Assert.Equal(HealthWord.Off,
            StateCardHealth.ForApplication(App(Key("Old", expires: null, lastUsed: Now.AddDays(-400)), enabled: false), Now).Word);

    [Theory]
    [InlineData(0, HealthWord.Working)]
    [InlineData(2, HealthWord.Working)]
    [InlineData(3, HealthWord.NeedsAttention)]
    [InlineData(4, HealthWord.NeedsAttention)]
    [InlineData(5, HealthWord.Broken)]
    [InlineData(40, HealthWord.Broken)]
    public void AWebhooksHealth_FollowsHowManyDeliveriesInARowFailed(int failures, HealthWord expected) =>
        Assert.Equal(expected, StateCardHealth.ForWebhook(Webhook(enabled: true, failures)).Word);

    [Fact]
    public void ASwitchedOffWebhook_IsNeverRed_HoweverManyDeliveriesFailed() =>
        Assert.Equal(HealthWord.Off, StateCardHealth.ForWebhook(Webhook(enabled: false, failures: 99)).Word);

    [Fact]
    public void AFailingWebhook_ShadesTheIntegrationsRowRedWithBroken_OnlyOnAComputer()
    {
        var desktop = Row(new SettingsMenuContext(true, false, IsMobile: false, 0, false, IntegrationsBroken: true));
        Assert.Equal(MediaSectionAttentionLevel.Blocked, desktop.Attention);
        Assert.Equal("Broken", desktop.AttentionLabel);

        var healthy = Row(new SettingsMenuContext(true, false, IsMobile: false, 0, false));
        Assert.Equal(MediaSectionAttentionLevel.None, healthy.EffectiveAttention);
    }

    private static MediaSectionNavigationItem Row(SettingsMenuContext context) =>
        Flatten(SettingsMenuBuilder.Build(context)).Single(row => row.Label == "Integrations");

    private static IEnumerable<MediaSectionNavigationItem> Flatten(IEnumerable<MediaSectionNavigationGroup> groups) =>
        groups.SelectMany(group => Flatten(group.Items));

    private static IEnumerable<MediaSectionNavigationItem> Flatten(IEnumerable<MediaSectionNavigationItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Flatten(item.Children ?? []))
            {
                yield return child;
            }
        }
    }

    private static ApplicationCredentialResponse Key(string name, DateTimeOffset? expires, DateTimeOffset? lastUsed, bool revoked = false) =>
        new(Guid.NewGuid(), Guid.Empty, name, Now.AddDays(-300), expires, lastUsed, revoked ? Now.AddDays(-1) : null);

    private static ApplicationResponse App(ApplicationCredentialResponse key, bool enabled = true) =>
        new(Guid.NewGuid(), "App", null, ApplicationTypeDto.ServerIntegration, enabled, false, 1, [], [], [key], Now.AddDays(-300), Now, null);

    private static ApplicationWebhookResponse Webhook(bool enabled, int failures) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "https://receiver.example/hook", ["backup.failed"], false, enabled, 1, "HTTP 503", Now, null, failures);
}
