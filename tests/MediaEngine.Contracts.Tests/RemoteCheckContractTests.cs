using System.Reflection;
using System.Text.Json;
using MediaEngine.Contracts.Settings;

namespace MediaEngine.Contracts.Tests;

/// <summary>The away-from-home check's wire shape and its fixed finding keys (the public docs have one entry per key).</summary>
public sealed class RemoteCheckContractTests
{
    [Fact]
    public void TheResult_UsesSnakeCaseNamesOnTheWire()
    {
        var result = new RemoteCheckResultDto
        {
            CheckedAt = DateTimeOffset.Parse("2026-11-20T12:00:00Z"),
            Suggestion = RemoteCheckSuggestions.OwnAddress,
            SuggestionReason = "Reason.",
            Findings =
            [
                new RemoteCheckFindingDto
                {
                    Key = RemoteCheckFindingKeys.ProxyNotTrusted,
                    Status = RemoteCheckStatuses.Warn,
                    Title = "Title",
                    Detail = "Detail",
                    NextStep = null,
                    FixAction = RemoteCheckFixActions.TrustProxy,
                },
            ],
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));

        Assert.Equal(["checked_at", "suggestion", "suggestion_reason", "findings"],
            json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        var finding = json.RootElement.GetProperty("findings")[0];
        Assert.Equal(["key", "status", "title", "detail", "next_step", "fix_action"],
            finding.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(JsonValueKind.Null, finding.GetProperty("next_step").ValueKind);
        Assert.Equal("trust-proxy", finding.GetProperty("fix_action").GetString());
    }

    [Fact]
    public void TheFindingKeys_AreTheFifteenAgreedNamesAndAllListed()
    {
        string[] expected =
        [
            "tailscale_not_installed", "tailscale_not_signed_in", "tailscale_https_off", "tailscale_ready", "shared_address",
            "router_door_unavailable", "router_door_ready", "public_address_unreachable", "proxy_not_trusted",
            "certificate_invalid", "certificate_expiring", "name_not_pointing_here", "no_outside_visitors_seen",
            "profiles_without_password", "admin_without_password",
        ];

        var declared = typeof(RemoteCheckFindingKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.Equal(expected.OrderBy(key => key), declared.OrderBy(key => key));
        Assert.Equal(expected, RemoteCheckFindingKeys.All);
    }

    [Fact]
    public void TheSuggestionsAndStatuses_AreTheAgreedWords()
    {
        Assert.Equal(["tailscale", "own-address", "not-now"],
            new[] { RemoteCheckSuggestions.Tailscale, RemoteCheckSuggestions.OwnAddress, RemoteCheckSuggestions.NotNow });
        Assert.Equal(["ok", "info", "warn", "fail"],
            new[] { RemoteCheckStatuses.Ok, RemoteCheckStatuses.Info, RemoteCheckStatuses.Warn, RemoteCheckStatuses.Fail });
    }

    [Fact]
    public void TheOneClickFixResult_UsesSnakeCaseNamesOnTheWire()
    {
        var result = new RemoteFixResultDto { Action = RemoteCheckFixActions.TrustProxy, Changed = true, ProxyPort = 5017, RestartRequired = true };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));

        Assert.Equal(["action", "changed", "proxy_port", "restart_required"],
            json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("trust-proxy", json.RootElement.GetProperty("action").GetString());
        Assert.Equal(5017, json.RootElement.GetProperty("proxy_port").GetInt32());
    }
}
