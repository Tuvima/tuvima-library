using System.Text.Json;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A plugin manifest says where its plugin connects (<c>network</c>), and the Library refuses a manifest whose
/// declaration does not fit the internet permission or is not a plain host.
/// </summary>
public sealed class PluginNetworkDestinationTests
{
    private static readonly PluginNetworkDestination Good = new() { Host = "api.example.com", Sends = "Searches for titles" };

    [Fact]
    public void AManifest_ReadsItsNetworkListFromSnakeCaseJson()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>("""
            {
              "id": "acme.lore",
              "name": "Lore",
              "permissions": ["network.http"],
              "network": [
                { "host": "www.wikidata.org", "sends": "Item IDs", "max_requests_per_minute": 60 },
                { "host": "*.fandom.com", "sends": "Page list requests" }
              ]
            }
            """)!;

        Assert.Equal(2, manifest.Network.Count);
        Assert.Equal("www.wikidata.org", manifest.Network[0].Host);
        Assert.Equal("Item IDs", manifest.Network[0].Sends);
        Assert.Equal(60, manifest.Network[0].MaxRequestsPerMinute);
        Assert.Equal("*.fandom.com", manifest.Network[1].Host);
        Assert.Null(manifest.Network[1].MaxRequestsPerMinute);
        Assert.Null(PluginCompatibility.ValidateManifest(manifest));
    }

    [Fact]
    public void AManifestWithoutANetworkList_HasNoDestinations_AndIsFineWithoutTheInternetPermission()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>("""{ "id": "acme.local", "name": "Local", "permissions": ["media.read"] }""")!;

        Assert.Empty(manifest.Network);
        Assert.Null(PluginCompatibility.ValidateManifest(manifest));
    }

    [Theory]
    [InlineData("api.example.com")]
    [InlineData("API.Example.com")]
    [InlineData("*.fandom.com")]
    [InlineData("nas")]
    [InlineData("a-b.example.co.uk")]
    [InlineData("192.168.1.20")]
    public void AHost_IsAnExactNameOrOneLeadingWildcardLabel(string host) =>
        Assert.Null(PluginNetworkRules.ValidateHost(host));

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("*.com")]
    [InlineData("*.*.example.com")]
    [InlineData("api.*.example.com")]
    [InlineData("https://api.example.com")]
    [InlineData("api.example.com/path")]
    [InlineData("api.example.com:8443")]
    [InlineData("api.example.com?x=1")]
    [InlineData(" api.example.com")]
    [InlineData("api..example.com")]
    [InlineData(".example.com")]
    [InlineData("-api.example.com")]
    [InlineData("api_.example.com")]
    public void AHost_WithASchemePathPortOrBareWildcardOrOddLabels_IsRefused(string host) =>
        Assert.NotNull(PluginNetworkRules.ValidateHost(host));

    [Fact]
    public void ThePluginThatDeclaresTheInternet_MustListAtLeastOneDestination()
    {
        var problem = PluginNetworkRules.Validate([PluginPermissionIds.NetworkHttp], []);

        Assert.NotNull(problem);
        Assert.Contains("network.http", problem, StringComparison.Ordinal);
        Assert.Contains("no network destinations", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ADestination_WithoutTheInternetPermission_IsRefused()
    {
        var problem = PluginNetworkRules.Validate([PluginPermissionIds.MediaRead], [Good]);

        Assert.NotNull(problem);
        Assert.Contains("does not declare the network.http permission", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ADestinationWithAMalformedHost_IsRefusedWithAPlainMessageNamingTheHost()
    {
        var problem = PluginNetworkRules.Validate(
            [PluginPermissionIds.NetworkHttp],
            [new PluginNetworkDestination { Host = "https://api.example.com/x", Sends = "Titles" }]);

        Assert.NotNull(problem);
        Assert.Contains("https://api.example.com/x", problem, StringComparison.Ordinal);
        Assert.Contains("scheme", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ADestination_MustSayWhatItSends(string sends)
    {
        var problem = PluginNetworkRules.Validate(
            [PluginPermissionIds.NetworkHttp],
            [new PluginNetworkDestination { Host = "api.example.com", Sends = sends }]);

        Assert.NotNull(problem);
        Assert.Contains("what it sends", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatADestinationSends_IsAtMostTwoHundredCharacters()
    {
        PluginNetworkDestination Sending(int length) => new() { Host = "api.example.com", Sends = new string('x', length) };

        Assert.Null(PluginNetworkRules.Validate([PluginPermissionIds.NetworkHttp], [Sending(200)]));
        Assert.NotNull(PluginNetworkRules.Validate([PluginPermissionIds.NetworkHttp], [Sending(201)]));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(600, true)]
    [InlineData(601, false)]
    [InlineData(-5, false)]
    public void TheRequestRate_IsBetweenOneAndSixHundredPerMinute(int rate, bool accepted)
    {
        var problem = PluginNetworkRules.Validate(
            [PluginPermissionIds.NetworkHttp],
            [new PluginNetworkDestination { Host = "api.example.com", Sends = "Titles", MaxRequestsPerMinute = rate }]);

        Assert.Equal(accepted, problem is null);
    }

    [Fact]
    public void TheSameHostListedTwice_IsRefused()
    {
        var problem = PluginNetworkRules.Validate(
            [PluginPermissionIds.NetworkHttp],
            [Good, Good with { Host = "API.example.com", Sends = "More titles" }]);

        Assert.NotNull(problem);
        Assert.Contains("more than once", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestThatBreaksTheNetworkRules_FailsTheManifestCheck()
    {
        var withoutDestinations = new PluginManifest { Id = "p", Name = "P", Permissions = [PluginPermissionIds.NetworkHttp] };

        Assert.NotNull(PluginCompatibility.ValidateManifest(withoutDestinations));
        Assert.Null(PluginCompatibility.ValidateManifest(withoutDestinations with { Network = [Good] }));
    }
}
