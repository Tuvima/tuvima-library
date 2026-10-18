using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Persons;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientPersonCreditsTests
{
    [Fact]
    public void PersonAliasContract_UsesRolesArrayInsteadOfSingularRole()
    {
        var json = JsonSerializer.Serialize(new PersonAliasItemResponse
        {
            Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Name = "Alias Name",
            Roles = ["Author", "Illustrator"],
        });

        Assert.Contains("\"roles\":[\"Author\",\"Illustrator\"]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"role\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetProfilesAsync_MapsSharedContractAndPreservesRole()
    {
        const string json = """
            [
              {
                "id": "00000000-0000-0000-0000-000000000001",
                "display_name": "Owner",
                "avatar_color": "#7C4DFF",
                "role": "Administrator",
                "created_at": "2026-07-26T12:00:00Z",
                "navigation_config": null,
                "avatar_image_url": "/profiles/00000000-0000-0000-0000-000000000001/avatar"
              }
            ]
            """;

        using var httpClient = CreateHttpClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        var profile = Assert.Single(await client.GetProfilesAsync());

        Assert.Equal("Administrator", profile.Role);
        Assert.Equal(
            "http://localhost:61495/profiles/00000000-0000-0000-0000-000000000001/avatar",
            profile.AvatarImageUrl);
    }

    [Fact]
    public async Task GetPersonLibraryCreditsAsync_NormalizesCreditAndCharacterArtwork()
    {
        const string json = """
            [
              {
                "work_id": "11111111-1111-1111-1111-111111111111",
                "collection_id": "22222222-2222-2222-2222-222222222222",
                "media_type": "Movies",
                "title": "Dune",
                "cover_url": "/stream/work-cover",
                "year": "2021",
                "role": "Actor",
                "characters": [
                  {
                    "fictional_entity_id": "33333333-3333-3333-3333-333333333333",
                    "character_name": "Paul Atreides",
                    "portrait_url": "stream/paul-portrait"
                  }
                ]
              }
            ]
            """;

        using var httpClient = CreateHttpClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        var credits = await client.GetPersonLibraryCreditsAsync(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        var credit = Assert.Single(credits);
        Assert.Equal("http://localhost:61495/stream/work-cover", credit.CoverUrl);
        Assert.Equal("Paul Atreides", Assert.Single(credit.Characters).CharacterName);
        Assert.Equal("http://localhost:61495/stream/paul-portrait", credit.Characters[0].PortraitUrl);
    }

    [Fact]
    public async Task GetWorkCastAsync_NormalizesActorAndCharacterImages()
    {
        const string json = """
            [
              {
                "person_id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "name": "Timothee Chalamet",
                "actor_person_id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "actor_name": "Timothee Chalamet",
                "headshot_url": "/stream/person-headshot",
                "character_name": "Paul Atreides",
                "characters": [
                  {
                    "fictional_entity_id": "33333333-3333-3333-3333-333333333333",
                    "character_name": "Paul Atreides",
                    "portrait_url": "/stream/paul-portrait"
                  }
                ]
              }
            ]
            """;

        using var httpClient = CreateHttpClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        var cast = await client.GetWorkCastAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        var castCredit = Assert.Single(cast);
        Assert.Equal("http://localhost:61495/stream/person-headshot", castCredit.HeadshotUrl);
        Assert.Equal("http://localhost:61495/stream/person-headshot", castCredit.ActorHeadshotUrl);
        Assert.Equal("http://localhost:61495/stream/paul-portrait", castCredit.CharacterImageUrl);
        Assert.Equal("http://localhost:61495/stream/paul-portrait", Assert.Single(castCredit.Characters).PortraitUrl);
    }

    [Fact]
    public async Task GetPersonAliasesAsync_NormalizesLocalHeadshotRoutes()
    {
        const string json = """
            {
              "person_id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "person_name": "Timothee Chalamet",
              "is_pseudonym": false,
              "aliases": [
                {
                  "id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                  "name": "Alias Name",
                  "roles": ["Author", "Illustrator"],
                  "headshot_url": "/persons/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/headshot",
                  "is_pseudonym": true,
                  "wikidata_qid": "Q123",
                  "relationship": "pen_name"
                }
              ]
            }
            """;

        using var httpClient = CreateHttpClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        var aliases = await client.GetPersonAliasesAsync(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        Assert.NotNull(aliases);
        var alias = Assert.Single(aliases!.Aliases);
        Assert.Equal(["Author", "Illustrator"], alias.Roles);
        Assert.Equal(
            "/engine-image/persons/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/headshot",
            alias.HeadshotUrl);
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("http://localhost:61495"),
        };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
