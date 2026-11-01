using System.Reflection;
using System.Text.RegularExpressions;
using MediaEngine.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Every Engine route that takes the id of a file, a title or another catalogue entity is either cleared through the catalogue
/// authorization (which answers "not found" for a Vault item to anyone whose Vault mode is off) or is on a short, reasoned list of
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
        ("/reader/", "EPUB annotations for books; Vault libraries hold videos only"),
        ("/api/v1/display/artwork/assets/", "Shared artwork only; Vault artwork is kept apart in vault_artwork"),
        ("/api/v1/display/artwork/media-assets/", "The handler authorizes the file through the catalogue"),
        ("/api/v1/display/artwork/universes/", "Universe artwork listing; entities are limited by the authorized projection"),
        ("/api/v1/vault/", "The whole group is behind the Vault gate and answers not found when Vault mode is off"),
        ("/assets/", "Capabilities answer empty exactly as for an unknown id"),
        ("/api/v1/extras/", "An Extra opens only when the caller may open a file of its title (CatalogueResourceAuthorizationService), and a Vault library has no Extras"),
        ("/entity-editor/universes/", "Universe entity editor; entity ids come from the authorized projection"),
        ("/ingestion/refresh-schedule/", "Refresh schedules for universes and series, never single files"),
        ("/read/resolve/", "The handler goes through the catalogue authorization service"),
        ("/review/", "Vault files never get review items; the review actions are covered by their own tests"),
        ("/library/excluded-files/", "The excluded-files service drops Vault rows (ExcludedFileService)"),
        ("/library/unmatched/", "Administrator-only; every route answers not found for a library that is unknown or a Vault library, and for a file in one (UnmatchedEndpoints)"),
        ("/library/items/recycle-bin/", "The recycle-bin service is covered by its own tests"),
        ("/maintenance/retag-sweep/retry/", "Answers the same for any id"),
        ("/view/", "The View index is separate and never enters the Work and Edition graph"),
        ("/api/v1/view/", "The View index is separate and never enters the Work and Edition graph"),
        ("/profiles/", "Profile ids, not catalogue items"),
        ("/settings/tools/", "Tool installers, not catalogue items"),
        ("/operations/", "Gated by VaultOperationGate; a Vault operation answers like a missing one"),
    ];

    // Exact route patterns, for the few routes whose whole prefix cannot be vouched for: a new route under /works/, /collections/,
    // /metadata/ or /profile-state/ that takes an item id has to be listed here with its own reason (security review L14).
    private static readonly (string Pattern, string Reason)[] AllowedRoutes =
    [
        ("/settings/vault-moves/{id:guid}", "The id is a move job, not an item; the route is unlocked-administrator only (404 otherwise) and returns counts with no title, path or item id"),
        ("/api/v1/profile-state/reactions/{entityKind}/{entityId:guid}", "Reactions are checked against the authorized projection of the signed-in profile"),
        ("/api/v1/profile-state/reactions/{entityKind}/{entityId:guid}/{reaction}", "Reactions are checked against the authorized projection of the signed-in profile"),
        ("/api/v1/profile-state/saved/{entityKind}/{entityId:guid}", "Saves are checked against the authorized projection of the signed-in profile"),
        ("/api/v1/display/lanes/{place}/hidden/{collectionId:guid}", "Only stores the profile's own hide for a lane it already has; no item or file is read"),
        ("/api/v1/collections/{id:guid}", "Items are limited by the authorized projection"),
        ("/api/v1/collections/{id:guid}/items", "Items are limited by the authorized projection"),
        ("/collections/{id:guid}/set", "The id is a Set, a parent collection with no items or files of its own; the service answers not found for anything that is not a Set the caller may edit, and the route needs collections-write"),
        ("/collections/{id:guid}/pack-file", "The id is a Set, which holds no items or files; the export carries only names and rules, never a title, path or item id, and the service answers not found for anything that is not a Set the caller may edit"),
        ("/metadata/{entityId:guid}/tvdb-match/previews/{token}", "The handler authorizes the owning work (EvaluateEntityAsync) before answering"),
        ("/works/{workId:guid}", "The handler restricts the work to the files the catalogue authorization allows, and answers not found for none"),
        ("/works/{workId:guid}/editions", "The handler restricts the work to the files the catalogue authorization allows, and answers not found for none"),
        ("/works/{workId:guid}/cast", "The handler answers not found unless the catalogue authorization allows a file of the work"),
        ("/library/editor-subjects/{id:guid}", "People, Series, curated collections and Universes are never files, so a Vault item cannot be one; the handler still asks the edit session's catalogue refusal check for the id (EditSessionAccessService) and answers not found for anything that is not such a subject"),
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
            if (cleared
                || Allowed.Any(entry => pattern.StartsWith(entry.Prefix, StringComparison.OrdinalIgnoreCase))
                || AllowedRoutes.Any(entry => string.Equals(pattern, entry.Pattern, StringComparison.OrdinalIgnoreCase)))
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

    [Fact]
    public void EveryAllowedRoute_HasAReason_AndNoBroadPrefixIsLeftForTheRoutesThatNeedAnExactEntry()
    {
        Assert.All(AllowedRoutes, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Reason), entry.Pattern));
        var broad = new[] { "/works/", "/metadata/", "/api/v1/collections/", "/api/v1/profile-state/" };
        Assert.Empty(Allowed.Select(entry => entry.Prefix).Intersect(broad, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EveryExactAllowedRoute_StillExists_SoTheListCannotGoStale()
    {
        var build = typeof(MappedEndpointInventoryTests).GetMethod("BuildEndpointOnlyApplication", BindingFlags.NonPublic | BindingFlags.Static)!;
        await using var app = (WebApplication)build.Invoke(null, null)!;
        var patterns = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Select(route => (route.RoutePattern.RawText ?? string.Empty).TrimEnd('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(AllowedRoutes, entry => Assert.Contains(entry.Pattern, patterns));
    }
}
