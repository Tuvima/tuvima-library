using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// An album Apple sells only as an edition (Expanded, 20th Anniversary) still gets chosen for the plain tagged album when
/// it is the only such album on offer; with two different editions on offer we cannot tell which one the file is, so none is taken.
/// </summary>
public sealed class AppleEditionAlbumTests
{
    private static JsonArray Albums(params string[] names) =>
        new(names.Select((name, i) => (JsonNode)new JsonObject
        {
            ["collectionId"] = 100 + i,
            ["artistName"] = "Nirvana",
            ["collectionName"] = name,
        }).ToArray());

    private static string? Select(string tagged, params string[] offered)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config/providers/apple_api.json"));
        var config = JsonSerializer.Deserialize<ProviderConfiguration>(File.ReadAllText(path))!;
        var services = new ServiceCollection();
        services.AddHttpClient(config.Name);
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var adapter = new ConfigDrivenAdapter(config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);
        var strategy = config.SearchStrategies!.Single(s => s.Name == "music_song_album_search");
        var request = new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Music,
            Title = tagged,
            Artist = "Nirvana",
            Album = tagged,
            BaseUrl = "https://itunes.apple.com",
        };
        var method = typeof(ConfigDrivenAdapter).GetMethod("TrySelectConfiguredCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var picked = (JsonNode?)method.Invoke(adapter, [Albums(offered), strategy, request]);
        return picked?["collectionName"]?.GetValue<string>();
    }

    [Fact]
    public void OnlyEditionOnOffer_IsTakenForThePlainTaggedAlbum()
        => Assert.Equal("In Utero (20th Anniversary Edition)", Select("In Utero", "In Utero (20th Anniversary Edition)"));

    [Fact]
    public void TwoDifferentEditionsOnOffer_AreNotGuessedBetween()
        => Assert.Null(Select("In Utero", "In Utero (20th Anniversary Edition)", "In Utero (Super Deluxe)"));

    [Fact]
    public void AnExactTrackListStillWinsOverAnEdition()
        => Assert.Equal("In Utero", Select("In Utero", "In Utero (Super Deluxe)", "In Utero"));

    [Fact]
    public void ADifferentAlbumIsNeverTaken()
        => Assert.Null(Select("In Utero", "Nevermind (Super Deluxe)"));

    [Fact]
    public void AmpersandAndTheWordAnd_AreTheSameWordInAnArtistName()
        => Assert.Equal(1.0, MediaEngine.Providers.Services.RetailTextSimilarity.ComputeWordOverlap(
            "Prince & The Revolution", "Prince and The Revolution"));
}
