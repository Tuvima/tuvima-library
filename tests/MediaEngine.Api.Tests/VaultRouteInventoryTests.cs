using System.Reflection;
using System.Text.RegularExpressions;
using MediaEngine.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Every Engine route that takes the id of a file, a title or another catalogue entity is either cleared through the catalogue
/// authorization (which answers "not found" for a Vault item to anyone whose NSFW mode is off) or is on a short, reasoned list of
/// routes that check the Vault themselves or can only reach a Vault item through a gate named here (Videos spec D17, WP17). A new
/// route that takes such an id cannot be added without saying which of the two it is, so a Vault leak cannot arrive unnoticed.
/// </summary>
public sealed class VaultRouteInventoryTests
{
    private static readonly Regex ItemIdParameter = new(
        @"\{(?<name>(asset|work|entity|edition|mediaAsset|item|owner|parent|series|universe|collection|file)[A-Za-z]*Id|id|entityId|assetId|workId)(:[a-z]+)?\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Route prefix -> why a Vault item cannot leak through it. Keep each reason specific; a vague one is a review failure.
    private static readonly (string Prefix, string Reason)[] Allowed =
    [
        ("/api/v1/devices/current/downloads/photos/", "View photos are a separate index that never holds Vault items"),
        ("/api/v1/player/audiobooks/", "Audiobook player routes; the work is checked by the player catalogue scope"),
        ("/api/v1/profile-state/", "Reactions and saves are checked against the authorized projection of the signed-in profile"),
        ("/reader/", "EPUB annotations for books; Vault libraries hold videos only"),
        ("/api/v1/collections/", "Items are limited by the authorized projection"),
        ("/api/v1/display/artwork/assets/", "Shared artwork only; Vault artwork is kept apart in vault_artwork"),
        ("/api/v1/display/artwork/media-assets/", "The handler authorizes the file through the catalogue"),
        ("/api/v1/display/artwork/universes/", "Universe artwork listing; entities are limited by the authorized projection"),
        ("/api/v1/vault/", "The whole group is behind the Vault gate and answers not found when NSFW mode is off"),
        ("/assets/", "Capabilities answer empty exactly as for an unknown id"),
        ("/entity-editor/universes/", "Universe entity editor; entity ids come from the authorized projection"),
        ("/ingestion/refresh-schedule/", "Refresh schedules for universes and series, never single files"),
        ("/metadata/", "The handler authorizes the owning work before answering"),
        ("/read/resolve/", "The handler goes through the catalogue authorization service"),
        ("/works/", "The handler goes through the catalogue authorization service"),
        ("/review/", "Vault files never get review items; the review actions are covered by their own tests"),
        ("/library/excluded-files/", "The excluded-files service drops Vault rows (ExcludedFileService)"),
        ("/library/items/recycle-bin/", "The recycle-bin service is covered by its own tests"),
        ("/maintenance/retag-sweep/retry/", "Answers the same for any id"),
        ("/view/", "The View index is separate and never enters the Work and Edition graph"),
        ("/api/v1/view/", "The View index is separate and never enters the Work and Edition graph"),
        ("/profiles/", "Profile ids, not catalogue items"),
        ("/settings/tools/", "Tool installers, not catalogue items"),
        ("/operations/", "Gated by VaultOperationGate; a Vault operation answers like a missing one"),
    ];

    [Fact]
    public async Task EveryRouteTakingAnItemId_IsClearedByTheCatalogue_OrIsListedWithTheReasonItIsSafe()
    {
        var build = typeof(MappedEndpointInventoryTests).GetMethod("BuildEndpointOnlyApplication", BindingFlags.NonPublic | BindingFlags.Static)!;
        await using var app = (WebApplication)build.Invoke(null, null)!;
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>();

        var uncleared = new List<string>();
        foreach (var route in routes)
        {
            var pattern = (route.RoutePattern.RawText ?? string.Empty).TrimEnd('/');
            if (!ItemIdParameter.IsMatch(pattern))
            {
                continue;
            }

            var cleared = route.Metadata.OfType<CatalogueAssetAccessMetadata>().Any()
                || route.Metadata.OfType<CatalogueEntityAccessMetadata>().Any()
                || route.Metadata.OfType<CatalogueAnyEntityAccessMetadata>().Any()
                || route.Metadata.OfType<CatalogueArtworkAccessMetadata>().Any()
                || route.Metadata.OfType<CatalogueQidAccessMetadata>().Any()
                || route.Metadata.OfType<CatalogueCharacterPortraitAccessMetadata>().Any();
            if (cleared || Allowed.Any(entry => pattern.StartsWith(entry.Prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var methods = string.Join("|", route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []);
            uncleared.Add($"{methods} {pattern}");
        }

        Assert.True(
            uncleared.Count == 0,
            "These routes take an item id but are neither cleared through the catalogue nor listed with a reason:"
            + Environment.NewLine + string.Join(Environment.NewLine, uncleared.Order()));
    }

    [Fact]
    public void EveryAllowedPrefix_HasAReason()
    {
        Assert.All(Allowed, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Reason), entry.Prefix));
    }
}
