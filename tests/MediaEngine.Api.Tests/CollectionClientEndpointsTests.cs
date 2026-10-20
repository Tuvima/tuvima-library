using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// <c>/api/v1/collections</c> and <c>/api/v1/universe</c> for paired apps, run through the real route table, real
/// filters, the real authorised display projection and a real data store; only the paired device's identity is faked.
/// The library holds one Universe (two series), one hand-picked list, and some R-rated items, so the Kids cases can
/// prove that nothing over the limit is listed or counted.
/// </summary>
public sealed partial class CollectionClientEndpointsTests : IDisposable
{
    private const string UniverseQid = "Q4242";
    private static readonly Guid OtherProfile = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_collection_client_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _library = Guid.NewGuid();
    private readonly Guid _account = Guid.NewGuid();
    private readonly Guid _universe = Guid.NewGuid();
    private readonly Guid _alpha = Guid.NewGuid();
    private readonly Guid _beta = Guid.NewGuid();
    private readonly Guid _list = Guid.NewGuid();
    private readonly Guid _privateList = Guid.NewGuid();
    private readonly Guid _adultUniverse = Guid.NewGuid();
    private readonly Guid _adultList = Guid.NewGuid();
    private readonly Guid _disabledList = Guid.NewGuid();
    private readonly Guid _sharedList = Guid.NewGuid();
    private readonly Guid _sharedSeries = Guid.NewGuid();
    private readonly Guid _hiddenSeries = Guid.NewGuid();

    public CollectionClientEndpointsTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_database);
        CreateAccount();
        SeedLibrary();
        // The older tests are about visibility and sources, not spoilers, so the profile starts on "Everything"; the spoiler
        // tests choose "Up to where I am" themselves.
        SetSpoilerLevel(SpoilerLevels.Everything);
    }

    [Fact]
    public async Task Routes_AreReadOnly_AndNeedTheLibraryReadScope()
    {
        await using var app = await StartAsync();
        var routes = app.Routes
            .Where(route => (route.RoutePattern.RawText ?? string.Empty).StartsWith("/api/v1/collections", StringComparison.Ordinal)
                || (route.RoutePattern.RawText ?? string.Empty).StartsWith("/api/v1/universe", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            ["/api/v1/collections/", "/api/v1/collections/{id:guid}", "/api/v1/collections/{id:guid}/items", "/api/v1/universe/{qid}"],
            routes.Select(route => route.RoutePattern.RawText!).Order(StringComparer.Ordinal));
        foreach (var route in routes)
        {
            Assert.Equal(["GET"], route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
            Assert.Contains(route.Metadata.OfType<ClientScopeRequirementMetadata>(),
                metadata => metadata.Scope == ClientApiScopes.LibraryRead);
        }
    }

    [Fact]
    public async Task NoWriteGraphOrLoreRoutes_AndNoPersonalMediaOrLocalAssetAccess()
    {
        await using var app = await StartAsync();
        using var write = await app.Client.PostAsync($"/api/v1/collections/{_list:D}/items", new StringContent("{}"));
        using var delete = await app.Client.DeleteAsync($"/api/v1/collections/{_list:D}");
        using var graph = await app.Client.GetAsync($"/api/v1/universe/{UniverseQid}/graph");
        using var lore = await app.Client.GetAsync($"/api/v1/universe/{UniverseQid}/lore-sources");

        Assert.Contains(write.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Contains(delete.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Equal(HttpStatusCode.NotFound, graph.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, lore.StatusCode);

        // Collections carry Work-backed items only. Personal-media sources (galleries, Documents) and the local asset
        // index are not read by these routes, so a profile that has Documents turned off cannot see a document here.
        foreach (var source in new[]
                 {
                     "src/MediaEngine.Api/Endpoints/CollectionClientEndpoints.cs",
                     "src/MediaEngine.Api/Services/ReadServices/UniverseClientReadService.cs",
                 })
        {
            var text = File.ReadAllText(Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", source)));
            foreach (var forbidden in new[] { "PersonalMedia", "LocalAsset", "ViewGallery", "ViewProfile", "IUniverseGraph", "LoreRepository", "MapPost", "MapPut", "MapDelete", "MapPatch" })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Catalogue_ListsTheUniverseAndTheListWithTheirGroupTypes_AndOmitsAuthoringFields()
    {
        await using var app = await StartAsync();

        var page = await GetJsonAsync(app, "/api/v1/collections");
        var items = page["items"]!.AsArray();

        // The universe rollup, the hand-picked list, the all-adult list and the list shared with this profile.
        Assert.Equal(5, items.Count);
        Assert.Equal(5, page["total_count"]!.GetValue<int>());
        Assert.False(page["has_more"]!.GetValue<bool>());
        var universe = items.Single(item => item!["id"]!.GetValue<Guid>() == _universe)!;
        var list = items.Single(item => item!["id"]!.GetValue<Guid>() == _list)!;
        Assert.Equal("universe", universe["group_type"]!.GetValue<string>());
        Assert.Equal(UniverseQid, universe["universe_qid"]!.GetValue<string>());
        Assert.Equal("Test Universe", universe["name"]!.GetValue<string>());
        Assert.Equal(7, universe["item_count"]!.GetValue<int>());
        Assert.Equal("list", list["group_type"]!.GetValue<string>());
        Assert.Equal("Weekend picks", list["name"]!.GetValue<string>());
        Assert.Equal(3, list["item_count"]!.GetValue<int>());
        foreach (var item in items)
        {
            Assert.Null(item!["rule_json"]);
            Assert.Empty(item["selected_profile_ids"]!.AsArray());
            Assert.Null(item["profile_id"]);
            Assert.False(item["can_edit"]!.GetValue<bool>());
            Assert.False(item["can_delete"]!.GetValue<bool>());
            Assert.False(item["can_share"]!.GetValue<bool>());
        }

        // A private list that belongs to somebody else is not offered.
        Assert.DoesNotContain(items, item => item!["id"]!.GetValue<Guid>() == _privateList);
        // A disabled collection is not in the catalogue.
        Assert.DoesNotContain(items, item => item!["id"]!.GetValue<Guid>() == _disabledList);
    }

    [Fact]
    public async Task Catalogue_UniverseQid_OpensTheUniverseRoute_AndOnlyUniversesCarryOne()
    {
        await using var app = await StartAsync();

        var page = await GetJsonAsync(app, "/api/v1/collections");
        var items = page["items"]!.AsArray();
        var universeEntries = items.Where(item => item!["universe_qid"] is not null).ToList();

        // Only Universe entries carry a QID (and the "universe" group type); lists, shows and series never do.
        Assert.Contains(universeEntries, item => item!["id"]!.GetValue<Guid>() == _universe);
        Assert.All(universeEntries, item => Assert.Equal("universe", item!["group_type"]!.GetValue<string>()));
        Assert.All(items.Where(item => item!["universe_qid"] is null), item =>
            Assert.NotEqual("universe", item!["group_type"]!.GetValue<string>()));
        Assert.Null(items.Single(item => item!["id"]!.GetValue<Guid>() == _list)!["universe_qid"]);

        // Every QID the catalogue hands out opens through the Universe route for the same profile.
        foreach (var entry in universeEntries)
        {
            var qid = entry!["universe_qid"]!.GetValue<string>();
            Assert.Matches("^Q[0-9]+$", qid);
            using var response = await app.Client.GetAsync($"/api/v1/universe/{qid}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The single-entry route agrees with the list.
        var single = await GetJsonAsync(app, $"/api/v1/collections/{_universe:D}");
        Assert.Equal(UniverseQid, single["universe_qid"]!.GetValue<string>());
        Assert.Equal("universe", single["group_type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Catalogue_NeverOffersAUniverseQidTheProfileCannotOpen()
    {
        await using var app = await StartAsync();
        SetContentLimit("PG");

        var page = await GetJsonAsync(app, "/api/v1/collections");

        // The all-adult Universe is hidden from the limited profile, so neither its entry nor its QID appears.
        Assert.DoesNotContain("Q5000", page.ToJsonString(), StringComparison.Ordinal);
        foreach (var item in page["items"]!.AsArray().Where(item => item!["universe_qid"] is not null))
        {
            using var response = await app.Client.GetAsync($"/api/v1/universe/{item!["universe_qid"]!.GetValue<string>()}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("Q4242", "Q4242")]
    [InlineData(" Q4242 ", "Q4242")]
    [InlineData("q4242", null)]
    [InlineData("Q42x", null)]
    [InlineData("Q", null)]
    [InlineData("P31", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeQid_OnlyAcceptsWellFormedQids(string? input, string? expected) =>
        Assert.Equal(expected, UniverseClientReadService.NormalizeQid(input));

    [Fact]
    public void NativeCopy_OnlyCarriesAUniverseQidTheCallerVerified_AndSeriesHaveNone()
    {
        var series = new MediaEngine.Contracts.Collections.CollectionManagementCatalogDto { GroupType = "series" };
        var unverified = new MediaEngine.Contracts.Collections.CollectionManagementCatalogDto { GroupType = "collection", UniverseQid = "Q1" };

        Assert.Null(ManagedCollectionMapper.ForNativeClient(series).UniverseQid);
        Assert.Equal("series", ManagedCollectionMapper.ForNativeClient(series).GroupType);
        var withheld = ManagedCollectionMapper.ForNativeClient(unverified);
        Assert.Null(withheld.UniverseQid);
        Assert.Equal("collection", withheld.GroupType);
        var verified = ManagedCollectionMapper.ForNativeClient(unverified, "Q1");
        Assert.Equal("Q1", verified.UniverseQid);
        Assert.Equal("universe", verified.GroupType);
    }

    [Fact]
    public async Task Catalogue_PagesByCursor()
    {
        await using var app = await StartAsync();

        var ids = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await GetJsonAsync(app, "/api/v1/collections?limit=2" + (cursor is null ? "" : $"&cursor={cursor}"));
            Assert.Equal(5, page["total_count"]!.GetValue<int>());
            ids.AddRange(page["items"]!.AsArray().Select(item => item!["id"]!.GetValue<Guid>()));
            cursor = page["next_cursor"]?.GetValue<string>();
            Assert.Equal(cursor is not null, page["has_more"]!.GetValue<bool>());
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(5, ids.Distinct().Count());

        using var bad = await app.Client.GetAsync("/api/v1/collections?cursor=not-a-number");
        using var negative = await app.Client.GetAsync("/api/v1/collections?cursor=-1");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
    }

    [Fact]
    public async Task Summary_ReturnsTheEntry_AndUnknownAndNotVisibleCollectionsGiveTheSameNotFound()
    {
        await using var app = await StartAsync();

        var summary = await GetJsonAsync(app, $"/api/v1/collections/{_list:D}");
        var unknown = await SnapshotAsync(app, $"/api/v1/collections/{Guid.NewGuid():D}");
        var hidden = await SnapshotAsync(app, $"/api/v1/collections/{_privateList:D}");
        var unknownItems = await SnapshotAsync(app, $"/api/v1/collections/{Guid.NewGuid():D}/items");
        var hiddenItems = await SnapshotAsync(app, $"/api/v1/collections/{_privateList:D}/items");

        Assert.Equal("Weekend picks", summary["name"]!.GetValue<string>());
        Assert.Equal("list", summary["group_type"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal(unknown, hidden);
        Assert.Equal(HttpStatusCode.NotFound, unknownItems.Status);
        Assert.Equal(unknownItems, hiddenItems);
        Assert.Equal(unknown, unknownItems);
    }

    [Fact]
    public async Task ACollectionWithNothingVisible_IsNotFoundAtEveryOffset_UnlikeAnOutOfRangeCursorOnOneWithItems()
    {
        SetContentLimit("PG");
        await using var app = await StartAsync();
        var unknown = await SnapshotAsync(app, $"/api/v1/collections/{Guid.NewGuid():D}/items");

        // Only R-rated items: not in the catalogue, and /items answers like an unknown collection however far in the cursor is.
        var catalogue = await GetJsonAsync(app, "/api/v1/collections");
        Assert.DoesNotContain(catalogue["items"]!.AsArray(), item => item!["id"]!.GetValue<Guid>() == _adultList);
        foreach (var query in new[] { "", "?cursor=0", "?cursor=1", "?cursor=24&limit=5" })
        {
            Assert.Equal(unknown, await SnapshotAsync(app, $"/api/v1/collections/{_adultList:D}/items{query}"));
        }

        // A collection that does have visible items may give an empty page past its end.
        var pastEnd = await GetJsonAsync(app, $"/api/v1/collections/{_list:D}/items?cursor=50");
        Assert.Empty(pastEnd["items"]!.AsArray());
        Assert.False(pastEnd["has_more"]!.GetValue<bool>());
        Assert.Equal(2, pastEnd["total_count"]!.GetValue<int>());

        // The same list is real for an adult, so only the limit hides it.
        SetContentLimit(null);
        Assert.Single((await GetJsonAsync(app, $"/api/v1/collections/{_adultList:D}/items"))["items"]!.AsArray());
    }

    [Fact]
    public async Task ADisabledCollection_IsNotFoundEverywhere_LikeAnUnknownOne()
    {
        await using var app = await StartAsync();
        var unknown = await SnapshotAsync(app, $"/api/v1/collections/{Guid.NewGuid():D}");
        var unknownItems = await SnapshotAsync(app, $"/api/v1/collections/{Guid.NewGuid():D}/items");

        Assert.Equal(unknown, await SnapshotAsync(app, $"/api/v1/collections/{_disabledList:D}"));
        Assert.Equal(unknownItems, await SnapshotAsync(app, $"/api/v1/collections/{_disabledList:D}/items"));
        Assert.Equal(unknownItems, await SnapshotAsync(app, $"/api/v1/collections/{_disabledList:D}/items?cursor=1"));

        // Enabled again, the very same list pages normally, so being disabled is the only difference.
        using var connection = _database.CreateConnection();
        connection.Execute("UPDATE collections SET is_enabled = 1 WHERE id = @id", new { id = _disabledList });
        Assert.Single((await GetJsonAsync(app, $"/api/v1/collections/{_disabledList:D}/items"))["items"]!.AsArray());
    }

    [Fact]
    public async Task APrivateListSharedWithThisProfile_IsVisible_WhileOneKeptPrivateStaysHidden()
    {
        await using var app = await StartAsync();

        var catalogue = await GetJsonAsync(app, "/api/v1/collections");
        var shared = catalogue["items"]!.AsArray().Single(item => item!["id"]!.GetValue<Guid>() == _sharedList)!;
        var summary = await GetJsonAsync(app, $"/api/v1/collections/{_sharedList:D}");
        var items = await GetJsonAsync(app, $"/api/v1/collections/{_sharedList:D}/items");

        Assert.Equal("list", shared["group_type"]!.GetValue<string>());
        Assert.Equal(1, shared["item_count"]!.GetValue<int>());
        Assert.Equal("Shared with you", summary["name"]!.GetValue<string>());
        Assert.Equal("Alpha One", Assert.Single(items["items"]!.AsArray())!["title"]!.GetValue<string>());
        // The owner's profile id and the audience list are not shown to the person it was shared with.
        Assert.Null(shared["profile_id"]);
        Assert.Empty(shared["selected_profile_ids"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await SnapshotAsync(app, $"/api/v1/collections/{_privateList:D}/items")).Status);

        // Take the sharing away and it disappears like an unknown list.
        using var connection = _database.CreateConnection();
        connection.Execute("DELETE FROM collection_profile_audience WHERE collection_id = @id", new { id = _sharedList });
        Assert.Equal(
            await SnapshotAsync(app, $"/api/v1/collections/{Guid.NewGuid():D}/items"),
            await SnapshotAsync(app, $"/api/v1/collections/{_sharedList:D}/items"));
    }

    [Fact]
    public async Task Items_PageInOrder_WithoutRepeatsOrGaps()
    {
        await using var app = await StartAsync();

        var first = await GetJsonAsync(app, $"/api/v1/collections/{_list:D}/items?limit=2");
        var second = await GetJsonAsync(app, $"/api/v1/collections/{_list:D}/items?limit=2&cursor={first["next_cursor"]!.GetValue<string>()}");
        var all = await GetJsonAsync(app, $"/api/v1/collections/{_list:D}/items?limit=10");

        Assert.Equal(2, first["items"]!.AsArray().Count);
        Assert.True(first["has_more"]!.GetValue<bool>());
        Assert.Single(second["items"]!.AsArray());
        Assert.False(second["has_more"]!.GetValue<bool>());
        Assert.Equal(
            all["items"]!.AsArray().Select(item => item!["work_id"]!.GetValue<Guid>()),
            first["items"]!.AsArray().Concat(second["items"]!.AsArray()).Select(item => item!["work_id"]!.GetValue<Guid>()));
        Assert.Equal(3, all["items"]!.AsArray().Count);

        // The rollup lists every member of both series, and no page repeats an item.
        var rollup = await GetJsonAsync(app, $"/api/v1/collections/{_universe:D}/items?limit=4");
        var rest = await GetJsonAsync(app, $"/api/v1/collections/{_universe:D}/items?limit=4&cursor={rollup["next_cursor"]!.GetValue<string>()}");
        var titles = rollup["items"]!.AsArray().Concat(rest["items"]!.AsArray()).Select(item => item!["title"]!.GetValue<string>()).ToArray();
        Assert.Equal(7, titles.Length);
        Assert.Equal(7, titles.Distinct().Count());
        Assert.DoesNotContain("Delta One", titles);
    }

    [Fact]
    public async Task KidsProfile_DoesNotSeeAnRRatedItemInACollection_NorItsCount()
    {
        SetContentLimit("PG");
        await using var app = await StartAsync();

        var catalogue = await GetJsonAsync(app, "/api/v1/collections");
        var items = catalogue["items"]!.AsArray();
        var list = await GetJsonAsync(app, $"/api/v1/collections/{_list:D}");
        var listItems = await GetJsonAsync(app, $"/api/v1/collections/{_list:D}/items?limit=10");
        var rollupItems = await GetJsonAsync(app, $"/api/v1/collections/{_universe:D}/items?limit=10");

        Assert.Equal(2, list["item_count"]!.GetValue<int>());
        Assert.Equal(2, items.Single(item => item!["id"]!.GetValue<Guid>() == _list)!["item_count"]!.GetValue<int>());
        Assert.Equal(5, items.Single(item => item!["id"]!.GetValue<Guid>() == _universe)!["item_count"]!.GetValue<int>());
        Assert.Equal(["Alpha One", "Beta One"], listItems["items"]!.AsArray().Select(item => item!["title"]!.GetValue<string>()).Order());
        var rollupTitles = rollupItems["items"]!.AsArray().Select(item => item!["title"]!.GetValue<string>()).ToArray();
        Assert.Equal(5, rollupTitles.Length);
        Assert.DoesNotContain("Alpha Two", rollupTitles);
        Assert.DoesNotContain("Beta Three", rollupTitles);

        // Nothing over the limit leaks through any total, count or name either.
        foreach (var text in new[] { catalogue.ToJsonString(), list.ToJsonString(), listItems.ToJsonString(), rollupItems.ToJsonString() })
        {
            Assert.DoesNotContain("Alpha Two", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Beta Three", text, StringComparison.Ordinal);
        }

        // The same device without the limit sees everything, so the filter is the only difference.
        SetContentLimit(null);
        var unrestricted = await GetJsonAsync(app, $"/api/v1/collections/{_list:D}/items?limit=10");
        Assert.Equal(3, unrestricted["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task KidsProfile_ASeriesOfOnlyRRatedItems_IsAbsentJustLikeAnUnknownOne()
    {
        SetContentLimit("PG");
        await using var app = await StartAsync();

        var catalogue = await GetJsonAsync(app, "/api/v1/collections");
        Assert.DoesNotContain(catalogue["items"]!.AsArray(), item => item!["id"]!.GetValue<Guid>() == _adultUniverse);

        var hidden = await SnapshotAsync(app, $"/api/v1/collections/{_adultUniverse:D}");
        var unknown = await SnapshotAsync(app, $"/api/v1/collections/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.Status);
        Assert.Equal(unknown, hidden);

        var hiddenItems = await SnapshotAsync(app, $"/api/v1/collections/{_adultUniverse:D}/items");
        var unknownItems = await SnapshotAsync(app, $"/api/v1/collections/{Guid.NewGuid():D}/items");
        Assert.Equal(HttpStatusCode.NotFound, hiddenItems.Status);
        Assert.Equal(unknownItems, hiddenItems);
        Assert.Equal(unknown, hiddenItems);

        var hiddenUniverse = await SnapshotAsync(app, "/api/v1/universe/Q5000");
        var unknownUniverse = await SnapshotAsync(app, "/api/v1/universe/Q999999");
        Assert.Equal(HttpStatusCode.NotFound, hiddenUniverse.Status);
        Assert.Equal(unknownUniverse, hiddenUniverse);

        // An adult profile does see it.
        SetContentLimit(null);
        using var adult = await app.Client.GetAsync("/api/v1/universe/Q5000");
        Assert.Equal(HttpStatusCode.OK, adult.StatusCode);
    }

    [Fact]
    public async Task Universe_ReturnsMemberShelvesAsDisplayShelves_WithoutGraphEdges()
    {
        await using var app = await StartAsync();

        var universe = await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}");

        Assert.Equal(UniverseQid, universe["qid"]!.GetValue<string>());
        Assert.Equal("Test Universe", universe["name"]!.GetValue<string>());
        Assert.Equal("universe", universe["group_type"]!.GetValue<string>());
        Assert.Equal(7, universe["item_count"]!.GetValue<int>());
        var shelves = universe["shelves"]!.AsArray();
        // Gamma is shared with exactly this profile so it is a member; Delta belongs to somebody else and is not.
        Assert.Equal(["Series Alpha", "Series Beta", "Series Gamma"], shelves.Select(shelf => shelf!["shelf"]!["title"]!.GetValue<string>()).Order());
        var alpha = shelves.Single(shelf => shelf!["collection_id"]!.GetValue<Guid>() == _alpha)!;
        Assert.Equal("series", alpha["group_type"]!.GetValue<string>());
        Assert.Equal(3, alpha["item_count"]!.GetValue<int>());
        Assert.Equal(
            ["Alpha One", "Alpha Three", "Alpha Two"],
            alpha["shelf"]!["items"]!.AsArray().Select(card => card!["title"]!.GetValue<string>()).Order());
        Assert.Equal($"/api/v1/display/groups/{_alpha:D}", alpha["shelf"]!["seeAllRoute"]!.GetValue<string>());
        foreach (var forbidden in new[] { "\"nodes\"", "\"edges\"", "entity_count", "character_count", "lore" })
        {
            Assert.DoesNotContain(forbidden, universe.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        }

        using var notAUniverse = await app.Client.GetAsync("/api/v1/universe/not-a-qid");
        using var series = await app.Client.GetAsync("/api/v1/universe/Q4243");
        Assert.Equal(HttpStatusCode.NotFound, notAUniverse.StatusCode);
        // Q4243 is a Series' own identifier, not a Universe.
        Assert.Equal(HttpStatusCode.NotFound, series.StatusCode);
    }

    [Fact]
    public async Task KidsProfile_UniverseShelvesAndCountsExcludeRRatedItems()
    {
        SetContentLimit("PG");
        await using var app = await StartAsync();

        var universe = await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}");
        var shelves = universe["shelves"]!.AsArray();

        Assert.Equal(5, universe["item_count"]!.GetValue<int>());
        Assert.Equal(2, shelves.Single(shelf => shelf!["collection_id"]!.GetValue<Guid>() == _alpha)!["item_count"]!.GetValue<int>());
        Assert.Equal(2, shelves.Single(shelf => shelf!["collection_id"]!.GetValue<Guid>() == _beta)!["item_count"]!.GetValue<int>());
        var titles = shelves.SelectMany(shelf => shelf!["shelf"]!["items"]!.AsArray()).Select(card => card!["title"]!.GetValue<string>()).ToArray();
        Assert.Equal(5, titles.Length);
        Assert.DoesNotContain("Alpha Two", titles);
        Assert.DoesNotContain("Beta Three", titles);
        Assert.DoesNotContain("Alpha Two", universe.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Beta Three", universe.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeCopy_KeepsEveryReadFieldAndOnlyBlanksTheAuthoringOnes()
    {
        await using var app = await StartAsync();
        var catalog = app.Services.GetRequiredService<CollectionCatalogReadService>();
        var profile = await new ProfileRepository(_database).GetByIdAsync(MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
        var source = (await catalog.GetCatalogAsync(profile)).Single(entry => entry.Id == _list);

        var original = JsonSerializer.SerializeToNode(source)!.AsObject();
        var copy = JsonSerializer.SerializeToNode(ManagedCollectionMapper.ForNativeClient(source))!.AsObject();

        var blanked = new Dictionary<string, string>
        {
            ["cover_artwork_url"] = "null",
            ["background_artwork_url"] = "null",
            ["banner_artwork_url"] = "null",
            ["logo_artwork_url"] = "null",
            ["profile_id"] = "null",
            ["selected_profile_ids"] = "[]",
            ["rule_json"] = "null",
            ["rule_hash"] = "null",
            ["refresh_schedule"] = "null",
            ["can_edit"] = "false",
            ["can_share"] = "false",
            ["can_delete"] = "false",
            ["can_rename"] = "false",
            ["can_toggle_global"] = "false",
        };
        Assert.Equal(
            original.Select(pair => pair.Key).Order(),
            copy.Select(pair => pair.Key).Order());
        foreach (var (key, value) in original)
        {
            var expected = blanked.TryGetValue(key, out var blank) ? blank : value?.ToJsonString() ?? "null";
            Assert.True(
                expected == (copy[key]?.ToJsonString() ?? "null"),
                $"'{key}' should be {expected} but was {copy[key]?.ToJsonString() ?? "null"}.");
        }
    }

    [Fact]
    public async Task Responses_MatchTheCheckedInContractFixtures()
    {
        await using var app = await StartAsync();

        AssertSameShape(
            await GetJsonAsync(app, "/api/v1/collections"),
            ReadFixture("collections.json"),
            "collections");
        AssertSameShape(
            await GetJsonAsync(app, $"/api/v1/collections/{_list:D}/items?limit=2"),
            ReadFixture("collection-items.json"),
            "collection-items");
        AssertSameShape(
            await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}"),
            ReadFixture("universe.json"),
            "universe");
    }

    [Fact]
    public async Task UniverseTheProfileCannotSee_IsStillNotFound()
    {
        // The adult Universe has its own cast and adaptation relationship; a limited profile must still get the plain not-found.
        var grownUp = AddWork("Movies", "Grown-up Three", "R", _adultUniverse, ".mkv");
        SetWorkQid(grownUp, "Q9001");
        var entity = AddEntity("Q5000", "Q9100", "Grown-up Hero", "Character");
        LinkEntity(entity, "Q9001");
        var person = AddPerson("Grown-up Actor");
        AddPerformance(person, entity, "Q9001");
        SetContentLimit("PG");
        await using var app = await StartAsync();

        var hidden = await SnapshotAsync(app, "/api/v1/universe/Q5000");
        var unknown = await SnapshotAsync(app, "/api/v1/universe/Q999999");

        Assert.Equal(HttpStatusCode.NotFound, hidden.Status);
        Assert.Equal(unknown, hidden);
        Assert.DoesNotContain("Grown-up Actor", hidden.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupplementaryMembers_AreListedUnderAlsoInThisWorld_NotInTheNumberedRun()
    {
        var extra = AddWork("Movies", "Alpha Extra", "PG", _alpha, ".mkv");
        AddManifestPlacement(_alpha, "Q4243", "Q7001", extra, "Supplementary");
        await using var app = await StartAsync();

        var universe = await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}");
        var shelves = universe["shelves"]!.AsArray();
        var alpha = shelves.Single(shelf => shelf!["collection_id"]!.GetValue<Guid>() == _alpha)!;

        Assert.Equal(3, alpha["item_count"]!.GetValue<int>());
        Assert.Equal(
            ["Alpha One", "Alpha Three", "Alpha Two"],
            alpha["shelf"]!["items"]!.AsArray().Select(card => card!["title"]!.GetValue<string>()).Order());
        Assert.Equal(1, alpha["also_in_this_world_count"]!.GetValue<int>());
        var also = alpha["also_in_this_world"]!;
        Assert.Equal("Also in this world", also["title"]!.GetValue<string>());
        Assert.Equal(["Alpha Extra"], also["items"]!.AsArray().Select(card => card!["title"]!.GetValue<string>()));
        // Every other shelf has no Also row, and the Universe counts each item once.
        var beta = shelves.Single(shelf => shelf!["collection_id"]!.GetValue<Guid>() == _beta)!;
        Assert.Null(beta["also_in_this_world"]);
        Assert.Equal(0, beta["also_in_this_world_count"]!.GetValue<int>());
        Assert.Equal(8, universe["item_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task AMusicSeries_IsNotSplitIntoAlsoInThisWorld_LikeTheItemPage()
    {
        var album = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, parent_collection_id)
                VALUES (@album, 'Series Music', 'Series', 'library', 'query', 1, @universe);
                """,
                new { album, universe = _universe });
        }

        AddWork("Music", "Track One", "G", album, ".flac");
        var bonus = AddWork("Music", "Track Two", "G", album, ".flac");
        AddManifestPlacement(album, "Q4300", "Q7002", bonus, "Supplementary");
        await using var app = await StartAsync();

        var universe = await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}");
        var shelf = universe["shelves"]!.AsArray().Single(entry => entry!["collection_id"]!.GetValue<Guid>() == album)!;

        // Only Book, Comic and Movie Series set supplementary members apart, as on the item page.
        Assert.Equal(2, shelf["item_count"]!.GetValue<int>());
        Assert.Equal(0, shelf["also_in_this_world_count"]!.GetValue<int>());
        Assert.Null(shelf["also_in_this_world"]);
    }

    [Fact]
    public async Task SeeAll_ListsTheUniverseShelfRun_WithAlsoInThisWorldMembersInTheirOwnShelf()
    {
        var extra = AddWork("Movies", "Alpha Extra", "PG", _alpha, ".mkv");
        AddManifestPlacement(_alpha, "Q4243", "Q7001", extra, "Supplementary");
        await using var app = await StartAsync();

        var universe = await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}");
        var alpha = universe["shelves"]!.AsArray().Single(shelf => shelf!["collection_id"]!.GetValue<Guid>() == _alpha)!;
        var seeAll = await GetJsonAsync(app, alpha["shelf"]!["seeAllRoute"]!.GetValue<string>());
        var seeAllShelves = seeAll["shelves"]!.AsArray();

        // The see-all run is the Universe shelf's run: same items, same order, same count.
        var run = seeAllShelves[0]!;
        Assert.Equal(
            alpha["shelf"]!["items"]!.AsArray().Select(card => card!["id"]!.GetValue<Guid>()),
            run["items"]!.AsArray().Select(card => card!["id"]!.GetValue<Guid>()));
        Assert.Equal(alpha["item_count"]!.GetValue<int>(), run["items"]!.AsArray().Count);
        Assert.Equal("3 items", seeAll["subtitle"]!.GetValue<string>());
        var also = Assert.Single(seeAllShelves.Skip(1))!;
        Assert.Equal("Also in this world", also["title"]!.GetValue<string>());
        Assert.Equal(["Alpha Extra"], also["items"]!.AsArray().Select(card => card!["title"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ASeriesWithOnlyAlsoInThisWorldMembers_HasAnEmptyRun_AndSeeAllHasNoEmptyRunShelf()
    {
        var companions = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, parent_collection_id)
                VALUES (@companions, 'Series Companions', 'Series', 'library', 'query', 1, @universe);
                """,
                new { companions, universe = _universe });
        }

        var guide = AddWork("Movies", "Companion Guide", "G", companions, ".mkv");
        AddManifestPlacement(companions, "Q4301", "Q7003", guide, "Supplementary");
        await using var app = await StartAsync();

        var universe = await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}");
        var shelf = universe["shelves"]!.AsArray().Single(entry => entry!["collection_id"]!.GetValue<Guid>() == companions)!;
        Assert.Equal(0, shelf["item_count"]!.GetValue<int>());
        Assert.Empty(shelf["shelf"]!["items"]!.AsArray());
        Assert.Equal(1, shelf["also_in_this_world_count"]!.GetValue<int>());

        var seeAll = await GetJsonAsync(app, $"/api/v1/display/groups/{companions:D}");
        var only = Assert.Single(seeAll["shelves"]!.AsArray())!;
        Assert.Equal("Also in this world", only["title"]!.GetValue<string>());
        Assert.Equal("1 item", seeAll["subtitle"]!.GetValue<string>());
    }

    [Fact]
    public async Task HeroTotals_CountSeriesWorksAndFormatsTheProfileCanSee()
    {
        await using var app = await StartAsync();

        var universe = await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}");

        Assert.Equal(3, universe["series_count"]!.GetValue<int>());
        Assert.Equal(7, universe["item_count"]!.GetValue<int>());
        Assert.Equal(["Book", "Movie"], universe["media_types"]!.AsArray().Select(kind => kind!.GetValue<string>()).Order());

        // A G-rated limit hides every Alpha item (PG and R), so the whole Series drops out of the totals.
        SetContentLimit("G");
        var limited = await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}");

        Assert.Equal(2, limited["series_count"]!.GetValue<int>());
        Assert.Equal(3, limited["item_count"]!.GetValue<int>());
        Assert.Equal(["Book", "Movie"], limited["media_types"]!.AsArray().Select(kind => kind!.GetValue<string>()).Order());
    }

    [Fact]
    public async Task Cast_IsOnePersonPerRow_WithTheCharactersTheyPlay()
    {
        var (first, second, _) = SeedCast();
        await using var app = await StartAsync();

        var cast = (await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}"))["cast"]!.AsArray();

        // Ann plays two characters across three works and is listed once, ahead of the one-work cast in name order.
        Assert.Equal(
            ["Ann Actor", "Bob Actor", "Carl Actor", "Dan Actor"],
            cast.Select(member => member!["name"]!.GetValue<string>()));
        var ann = cast[0]!;
        Assert.Equal(first, ann["person_id"]!.GetValue<Guid>());
        Assert.Equal(["Hero", "Villain"], ann["characters"]!.AsArray().Select(character => character!["label"]!.GetValue<string>()));
        Assert.Equal(["Q8200", "Q8201"], ann["characters"]!.AsArray().Select(character => character!["qid"]!.GetValue<string>()));
        Assert.Equal(3, ann["work_count"]!.GetValue<int>());
        Assert.Equal(second, cast[1]!["person_id"]!.GetValue<Guid>());
        Assert.Equal(["Villain"], cast[1]!["characters"]!.AsArray().Select(character => character!["label"]!.GetValue<string>()));
        Assert.Equal(1, cast[1]!["work_count"]!.GetValue<int>());
        Assert.Equal(
            ["characters", "headshot_url", "name", "person_id", "work_count"],
            ((JsonObject)ann).Select(pair => pair.Key).Order());
    }

    [Fact]
    public async Task Cast_OmitsCreditsFromWorksTheProfileCannotSee()
    {
        SeedCast();
        SetContentLimit("PG");
        await using var app = await StartAsync();

        var universe = await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}");
        var cast = universe["cast"]!.AsArray();

        // Carl and Dan only appear in the R-rated work, so they are gone; Ann's count no longer includes it.
        Assert.DoesNotContain("Carl Actor", universe.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Dan Actor", universe.ToJsonString(), StringComparison.Ordinal);
        var ann = cast.Single(member => member!["name"]!.GetValue<string>() == "Ann Actor")!;
        Assert.Equal(2, ann["work_count"]!.GetValue<int>());
        Assert.DoesNotContain("Hidden Villain", universe.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adaptations_AreEmpty_WhenNoAdaptationRelationshipExists()
    {
        SeedCast();
        // A relationship that is not an adaptation, between two visible things.
        AddRelationship("Q8100", "member_of", "Q8101");
        await using var app = await StartAsync();

        var universe = await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}");

        Assert.Empty(universe["adaptations"]!.AsArray());
    }

    [Fact]
    public async Task Adaptations_ListEachRealAdaptationEdge_BetweenThingsTheProfileCanSee()
    {
        SeedCast();
        AddRelationship("Q8100", "based_on", "Q8101");
        AddRelationship("Q8100", "inspired_by", "Q8102");
        await using var app = await StartAsync();

        var rows = (await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}"))["adaptations"]!.AsArray();

        Assert.Equal(2, rows.Count);
        var basedOn = rows.Single(row => row!["relationship"]!.GetValue<string>() == "based on")!;
        Assert.Equal("Q8100", basedOn["qid"]!.GetValue<string>());
        Assert.Equal("The Film", basedOn["label"]!.GetValue<string>());
        Assert.Equal("Object", basedOn["media_type"]!.GetValue<string>());
        Assert.Equal("Q8101", basedOn["source_qid"]!.GetValue<string>());
        Assert.Equal("The Novel", basedOn["source_label"]!.GetValue<string>());
        Assert.Equal("inspired by", rows.Single(row => row!["source_qid"]!.GetValue<string>() == "Q8102")!["relationship"]!.GetValue<string>());

        // An edge to something only the R-rated work shows is not listed for a limited profile.
        SetContentLimit("PG");
        var limited = (await GetJsonAsync(app, $"/api/v1/universe/{UniverseQid}"))["adaptations"]!.AsArray();
        Assert.Equal(["Q8101"], limited.Select(row => row!["source_qid"]!.GetValue<string>()));
    }

    [Fact]
    public async Task OldCastAndAdaptationActions_ReturnTheSameShapeAsBefore()
    {
        SeedCast();
        AddRelationship("Q8100", "based_on", "Q8101");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO narrative_roots (qid, label, level, created_at) VALUES (@qid, 'Test Universe', 'Universe', '2026-01-01 00:00:00');",
                new { qid = UniverseQid });
        }

        await using var app = await StartAsync(mapGraphActions: true);

        var cast = await GetJsonAsync(app, $"/api/v1/graph-actions/universe/{UniverseQid}/cast");
        Assert.Equal(["characters", "universe_qid"], cast.Select(pair => pair.Key).Order());
        Assert.Equal(UniverseQid, cast["universe_qid"]!.GetValue<string>());
        var hero = cast["characters"]!.AsArray().Single(character => character!["qid"]!.GetValue<string>() == "Q8200")!;
        Assert.Equal(["description", "image", "label", "performers", "qid"], ((JsonObject)hero).Select(pair => pair.Key).Order());
        Assert.Equal("Hero", hero["label"]!.GetValue<string>());
        // The old action still lists every performer of a visible character, with the work each credit names.
        var performers = hero["performers"]!.AsArray();
        Assert.Equal(["Ann Actor", "Ann Actor", "Ann Actor", "Carl Actor"], performers.Select(row => row!["name"]!.GetValue<string>()).Order());
        Assert.Equal(
            ["headshot_url", "name", "person_id", "work_qid", "year"],
            ((JsonObject)performers[0]!).Select(pair => pair.Key).Order());

        var tree = await GetJsonAsync(app, $"/api/v1/graph-actions/universe/{UniverseQid}/adaptations");
        Assert.Equal(["universe_qid", "works"], tree.Select(pair => pair.Key).Order());
        var root = tree["works"]!.AsArray().Single()!;
        Assert.Equal(
            ["children", "cover_image", "label", "media_type", "qid", "relationship_to_parent", "source", "year"],
            ((JsonObject)root).Select(pair => pair.Key).Order());
        Assert.Equal("Q8101", root["qid"]!.GetValue<string>());
        Assert.Null(root["relationship_to_parent"]);
        Assert.Null(root["source"]);
        var child = root["children"]!.AsArray().Single()!;
        Assert.Equal("Q8100", child["qid"]!.GetValue<string>());
        Assert.Equal("based on", child["relationship_to_parent"]!.GetValue<string>());
        Assert.Equal("Wikidata", child["source"]!.GetValue<string>());

        // A catalogue-visible QID that is no narrative root keeps the action's not-found text.
        using var unknown = await app.Client.GetAsync("/api/v1/graph-actions/universe/Q4243/adaptations");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("Narrative root 'Q4243' not found.", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // Seeds ------------------------------------------------------------------------------------------------------

    private void SeedLibrary()
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles (id, display_name, avatar_color, role, created_at) VALUES (@id, 'Other', '#7C4DFF', 'StandardUser', CURRENT_TIMESTAMP);",
            new { id = OtherProfile });
        connection.Execute(
            """
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, wikidata_qid, created_at)
            VALUES (@shadow, 'Shadow series', 'Series', 'library', 'query', 1, @qid, '2000-01-01 00:00:00');
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, wikidata_qid, parent_collection_id)
            VALUES (@universe, 'Test Universe', 'Universe', 'library', 'materialized', 1, @qid, NULL),
                   (@alpha, 'Series Alpha', 'Series', 'library', 'query', 1, 'Q4243', @universe),
                   (@beta, 'Series Beta', 'Series', 'library', 'query', 1, NULL, @universe),
                   (@adultUniverse, 'Adult Universe', 'Universe', 'library', 'materialized', 1, 'Q5000', NULL);
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, membership_mode, owner_kind, audience)
            VALUES (@list, 'Weekend picks', 'Custom', 'library', 'materialized', 1, 'Manual', 'Library', 'Everyone');
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, membership_mode, owner_kind, audience, profile_id)
            VALUES (@privateList, 'Secret list', 'Custom', 'user', 'materialized', 1, 'Manual', 'Profile', 'Private', @other),
                   (@sharedList, 'Shared with you', 'Custom', 'user', 'materialized', 1, 'Manual', 'Profile', 'SelectedProfiles', @other);
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, membership_mode, owner_kind, audience)
            VALUES (@adultList, 'Adults only', 'Custom', 'library', 'materialized', 1, 'Manual', 'Library', 'Everyone'),
                   (@disabledList, 'Retired list', 'Custom', 'library', 'materialized', 0, 'Manual', 'Library', 'Everyone');
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, membership_mode, owner_kind, audience, profile_id, parent_collection_id)
            VALUES (@sharedSeries, 'Series Gamma', 'Series', 'user', 'query', 1, 'Smart', 'Profile', 'SelectedProfiles', @other, @universe),
                   (@hiddenSeries, 'Series Delta', 'Series', 'user', 'query', 1, 'Smart', 'Profile', 'Private', @other, @universe);
            INSERT INTO collection_profile_audience (collection_id, profile_id)
            VALUES (@sharedList, @me), (@sharedSeries, @me);
            """,
            new
            {
                shadow = Guid.NewGuid(),
                adultList = _adultList,
                disabledList = _disabledList,
                sharedList = _sharedList,
                sharedSeries = _sharedSeries,
                hiddenSeries = _hiddenSeries,
                me = MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
                universe = _universe,
                alpha = _alpha,
                beta = _beta,
                adultUniverse = _adultUniverse,
                list = _list,
                privateList = _privateList,
                other = OtherProfile,
                qid = UniverseQid
            });

        var alphaOne = AddWork("Movies", "Alpha One", "PG", _alpha, ".mkv");
        var alphaTwo = AddWork("Movies", "Alpha Two", "R", _alpha, ".mkv");
        AddWork("Movies", "Alpha Three", "PG", _alpha, ".mkv");
        var betaOne = AddWork("Books", "Beta One", "G", _beta, ".epub");
        AddWork("Books", "Beta Two", "G", _beta, ".epub");
        AddWork("Books", "Beta Three", "R", _beta, ".epub");
        AddWork("Movies", "Gamma One", "G", _sharedSeries, ".mkv");
        AddWork("Movies", "Delta One", "G", _hiddenSeries, ".mkv");

        // A second universe whose series holds only adult items, with its own series so it joins the catalogue.
        var adultSeries = Guid.NewGuid();
        var adultSeriesTwo = Guid.NewGuid();
        using (var adultConnection = _database.CreateConnection())
        {
            adultConnection.Execute(
                """
                INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, parent_collection_id)
                VALUES (@one, 'Adult Series One', 'Series', 'library', 'query', 1, @parent),
                       (@two, 'Adult Series Two', 'Series', 'library', 'query', 1, @parent);
                """,
                new { one = adultSeries, two = adultSeriesTwo, parent = _adultUniverse });
        }

        AddWork("Movies", "Grown-up One", "R", adultSeries, ".mkv");
        var grownUpTwo = AddWork("Movies", "Grown-up Two", "R", adultSeriesTwo, ".mkv");

        using var items = _database.CreateConnection();
        items.Execute(
            """
            INSERT INTO collection_items (id, collection_id, work_id, sort_order)
            VALUES (@i1, @list, @alphaOne, 0), (@i2, @list, @alphaTwo, 1), (@i3, @list, @betaOne, 2),
                   (@i4, @privateList, @alphaOne, 0), (@i5, @sharedList, @alphaOne, 0),
                   (@i6, @adultList, @grownUpTwo, 0), (@i7, @disabledList, @alphaOne, 0);
            """,
            new
            {
                i1 = Guid.NewGuid(),
                i2 = Guid.NewGuid(),
                i3 = Guid.NewGuid(),
                i4 = Guid.NewGuid(),
                i5 = Guid.NewGuid(),
                i6 = Guid.NewGuid(),
                i7 = Guid.NewGuid(),
                sharedList = _sharedList,
                adultList = _adultList,
                disabledList = _disabledList,
                grownUpTwo,
                list = _list,
                privateList = _privateList,
                alphaOne,
                alphaTwo,
                betaOne,
            });
    }

    private Guid AddWork(string mediaType, string title, string rating, Guid collection, string extension)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state, collection_id)
            VALUES (@work, @mediaType, 'standalone', 'accepted', @collection);
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP, @library);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@work, 'title', @title, CURRENT_TIMESTAMP),
                   (@asset, 'title', @title, CURRENT_TIMESTAMP),
                   (@work, 'content_rating', @rating, CURRENT_TIMESTAMP);
            """,
            new
            {
                work,
                edition,
                asset,
                mediaType,
                collection,
                title,
                rating,
                hash = asset.ToString("N"),
                path = $"C:/library/{asset:N}{extension}",
                library = _library.ToString("D"),
            });
        return work;
    }

    /// <summary>
    /// Two visible works (QW1, QW2) and one R-rated work (QW3) in Series Alpha. Hero (QW1, QW2, QW3), Villain (QW1) and
    /// Hidden Villain (QW3 only) are characters. Ann plays Hero in QW1/QW2/QW3 and Villain in QW1; Bob plays Villain in QW1;
    /// Carl plays Hero only in QW3; Dan plays Hidden Villain in QW3. Q8100 "The Film", Q8101 "The Novel" and Q8102
    /// "The Short" are things seen in QW1 (Q8102 only in QW3).
    /// </summary>
    private (Guid Ann, Guid Bob, Guid Carl) SeedCast()
    {
        var one = AddWork("Movies", "Cast One", "PG", _alpha, ".mkv");
        var two = AddWork("Movies", "Cast Two", "PG", _alpha, ".mkv");
        var hiddenWork = AddWork("Movies", "Cast Three", "R", _alpha, ".mkv");
        SetWorkQid(one, "QW1");
        SetWorkQid(two, "QW2");
        SetWorkQid(hiddenWork, "QW3");

        var hero = AddEntity(UniverseQid, "Q8200", "Hero", "Character");
        var villain = AddEntity(UniverseQid, "Q8201", "Villain", "Character");
        var hiddenVillain = AddEntity(UniverseQid, "Q8202", "Hidden Villain", "Character");
        LinkEntity(hero, "QW1");
        LinkEntity(hero, "QW2");
        LinkEntity(hero, "QW3");
        LinkEntity(villain, "QW1");
        LinkEntity(hiddenVillain, "QW3");
        LinkEntity(AddEntity(UniverseQid, "Q8100", "The Film", "Object"), "QW1");
        LinkEntity(AddEntity(UniverseQid, "Q8101", "The Novel", "Object"), "QW1");
        LinkEntity(AddEntity(UniverseQid, "Q8102", "The Short", "Object"), "QW3");

        var ann = AddPerson("Ann Actor");
        var bob = AddPerson("Bob Actor");
        var carl = AddPerson("Carl Actor");
        var dan = AddPerson("Dan Actor");
        AddPerformance(ann, hero, "QW1");
        AddPerformance(ann, hero, "QW2");
        AddPerformance(ann, hero, "QW3");
        AddPerformance(ann, villain, "QW1");
        AddPerformance(bob, villain, "QW1");
        AddPerformance(carl, hero, "QW3");
        AddPerformance(dan, hiddenVillain, "QW3");
        return (ann, bob, carl);
    }

    private void SetWorkQid(Guid work, string qid)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("UPDATE works SET wikidata_qid = @qid WHERE id = @work;", new { qid, work });
    }

    private Guid AddEntity(string universeQid, string qid, string label, string subType)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO fictional_entities (id, wikidata_qid, label, entity_sub_type, fictional_universe_qid, created_at)
            VALUES (@id, @qid, @label, @subType, @universeQid, '2026-01-01 00:00:00');
            """,
            new { id, qid, label, subType, universeQid });
        return id;
    }

    private void LinkEntity(Guid entity, string workQid)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO fictional_entity_work_links (id, appearance_key, entity_id, work_qid)
            VALUES (@id, @key, @entity, @workQid);
            """,
            new { id = Guid.NewGuid(), key = $"{entity:N}:{workQid}", entity, workQid });
    }

    private Guid AddPerson(string name)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO persons (id, name, created_at) VALUES (@id, @name, '2026-01-01 00:00:00');",
            new { id, name });
        return id;
    }

    private void AddPerformance(Guid person, Guid entity, string workQid)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO character_performer_links (person_id, fictional_entity_id, work_qid) VALUES (@person, @entity, @workQid);",
            new { person, entity, workQid });
    }

    private void AddRelationship(string subjectQid, string type, string objectQid)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO entity_relationships (id, statement_key, subject_qid, relationship_type, object_qid, discovered_at)
            VALUES (@id, @key, @subjectQid, @type, @objectQid, '2026-01-01 00:00:00');
            """,
            new { id = Guid.NewGuid(), key = $"{subjectQid}|{type}|{objectQid}", subjectQid, type, objectQid });
    }

    private void AddManifestPlacement(Guid collection, string seriesQid, string itemQid, Guid work, string scope)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO series_manifest_items
                (id, collection_id, series_qid, item_qid, order_source, linked_work_id, last_hydrated_at, membership_scope, ownership_state)
            VALUES (@id, @collection, @seriesQid, @itemQid, 'test', @work, CURRENT_TIMESTAMP, @scope, 'Owned');
            """,
            new { id = Guid.NewGuid(), collection, seriesQid, itemQid, work, scope });
    }

    private void CreateAccount()
    {
        var accounts = new AccountRepository(_database);
        accounts.CreateAccountAsync(
            new Account
            {
                Id = _account,
                Email = $"{_account:N}@example.com",
                NormalizedEmail = $"{_account:N}@EXAMPLE.COM",
                IsEnabled = true,
                AuthorizationVersion = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            new AccountProfileGrant
            {
                AccountId = _account,
                ProfileId = MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
                IsDefault = true,
                IsEnabled = true,
                AuthorizationVersion = 1,
                GrantedAt = DateTimeOffset.UtcNow,
            },
            new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch, AccountFeatureId.Listen },
            new HashSet<Guid> { _library }).GetAwaiter().GetResult();
    }

    private void SetContentLimit(string? limit)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "UPDATE profiles SET content_limit=@limit, content_limit_allow_unrated=0 WHERE id=@id",
            new { limit, id = MediaEngine.Domain.Aggregates.Profile.SeedProfileId });
    }

    // Requests ---------------------------------------------------------------------------------------------------

    private static async Task<JsonObject> GetJsonAsync(TestApplication app, string path)
    {
        using var response = await app.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} returned {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body)!.AsObject();
    }

    // The trace id is the one part of a problem answer that is different on every request.
    private static async Task<(HttpStatusCode Status, string Body, string? ContentType)> SnapshotAsync(TestApplication app, string path)
    {
        using var response = await app.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        if (body.Length > 0 && JsonNode.Parse(body) is JsonObject problem)
        {
            problem.Remove("traceId");
            body = problem.ToJsonString();
        }

        return (response.StatusCode, body, response.Content.Headers.ContentType?.ToString());
    }

    // Fixtures ---------------------------------------------------------------------------------------------------

    private static JsonNode ReadFixture(string fileName) => JsonNode.Parse(File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures", "native-client-v1", fileName))))!;

    /// <summary>The fixture and the live answer must name the same properties at every level (first array element).</summary>
    private static void AssertSameShape(JsonNode actual, JsonNode fixture, string path)
    {
        switch (actual)
        {
            case JsonObject actualObject when fixture is JsonObject fixtureObject:
                Assert.True(
                    actualObject.Select(pair => pair.Key).Order().SequenceEqual(fixtureObject.Select(pair => pair.Key).Order()),
                    $"{path}: live properties [{string.Join(",", actualObject.Select(pair => pair.Key).Order())}] differ from fixture [{string.Join(",", fixtureObject.Select(pair => pair.Key).Order())}].");
                foreach (var (key, value) in actualObject)
                {
                    if (value is not null && fixtureObject[key] is { } fixtureValue)
                    {
                        AssertSameShape(value, fixtureValue, $"{path}.{key}");
                    }
                }

                break;
            case JsonArray actualArray when fixture is JsonArray fixtureArray:
                if (actualArray.Count > 0 && fixtureArray.Count > 0 && actualArray[0] is { } first && fixtureArray[0] is { } fixtureFirst)
                {
                    AssertSameShape(first, fixtureFirst, $"{path}[]");
                }

                break;
        }
    }

    // Host -------------------------------------------------------------------------------------------------------

    private async Task<TestApplication> StartAsync(bool mapGraphActions = false)
    {
        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient,
            true,
            AccountId: _account,
            ActiveProfileId: MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            ApplicationId: Guid.NewGuid(),
            DeviceId: Guid.NewGuid(),
            AccountEnabled: true,
            GrantEnabled: true,
            ApplicationEnabled: true,
            AccountAuthorizationVersion: 1,
            GrantAuthorizationVersion: 1,
            ApplicationAuthorizationVersion: 1);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddLogging();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(authority));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new LibraryAccess(_library));
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowAuthorizationEvaluator>();
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        builder.Services.AddSingleton<IAccountRepository, AccountRepository>();
        builder.Services.AddSingleton<IProfileRepository, ProfileRepository>();
        builder.Services.AddSingleton<ICollectionRepository, CollectionRepository>();
        builder.Services.AddSingleton<ISeriesManifestRepository, SeriesManifestRepository>();
        builder.Services.AddSingleton<IPersonRepository, PersonRepository>();
        builder.Services.AddSingleton<IArtworkPaletteService, ArtworkPaletteService>();
        builder.Services.AddSingleton<ICollectionMediaLookupReadService, CollectionMediaLookupReadService>();
        builder.Services.AddSingleton<CollectionCatalogReadService>();
        builder.Services.AddSingleton<DisplayWorkProjectionReader>();
        builder.Services.AddSingleton<DisplayCardBuilder>();
        builder.Services.AddSingleton<IRawDisplayProjectionReadService, LiveProjection>();
        builder.Services.AddScoped<AuthorizedDisplayProjectionReadService>();
        builder.Services.AddScoped<IDisplayProjectionReadService>(provider => new CountingProjection(
            provider.GetRequiredService<AuthorizedDisplayProjectionReadService>(),
            () => Interlocked.Increment(ref _projectionLoads)));
        builder.Services.AddSingleton<IFictionalEntityRepository, FictionalEntityRepository>();
        builder.Services.AddSingleton<IEntityRelationshipRepository, EntityRelationshipRepository>();
        builder.Services.AddSingleton<INarrativeRootRepository, NarrativeRootRepository>();
        builder.Services.AddSingleton<IViewerProgressRepository, ViewerProgressRepository>();
        builder.Services.AddSingleton<IUserPlaybackSettingsService, UserPlaybackSettingsService>();
        builder.Services.AddScoped<CharacterFirstSeenReader>();
        builder.Services.AddScoped<CharacterSpoilerService>();
        builder.Services.AddScoped<UniverseConnectionsReadService>();
        // The other graph actions are mapped too when asked, so they need to bind; only the graph action reads the era actors.
        builder.Services.AddSingleton<IUniverseGraphQueryService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IEraActorResolverService, NoEraActors>();
        builder.Services.AddSingleton<ILoreDeltaService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IPluginLoreRepository, PluginLoreRepository>();
        builder.Services.AddScoped<UniverseClientReadService>();
        // The Universe page's own read (mapped with the graph actions) and what it adds to the helper-app read.
        builder.Services.AddSingleton<IProfileSequencePreferencesRepository, ProfileSequencePreferencesRepository>();
        builder.Services.AddSingleton<IUniverseEnrichmentStatusRepository, UniverseEnrichmentStatusRepository>();
        builder.Services.AddSingleton<IConfigurationLoader>(_ => new LibraryPreferencesLoader(() => _libraryPreferences));
        builder.Services.AddScoped<UniversePageReadService>();
        builder.Services.AddSingleton<UniverseLinkCache>();
        builder.Services.AddSingleton<DisplayShelfBuilder>();
        builder.Services.AddScoped<DisplayComposerService>();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCollectionClientEndpoints();
        // The shelf's see-all action calls exactly this read (DisplayEndpoints "GetDisplayGroup").
        app.MapGet("/api/v1/display/groups/{groupId:guid}", async (Guid groupId, DisplayComposerService display, CancellationToken ct) =>
            await display.BuildGroupAsync(groupId, includeCatalog: false, profileId: null, ct) is { } page
                ? Results.Ok(page)
                : Results.NotFound());
        if (mapGraphActions)
        {
            // The older Universe graph actions behind a test-only prefix, to prove their answers did not change.
            app.MapGroup("/api/v1/graph-actions").MapUniverseGraphEndpoints();
            MapUniverseMembershipProbe(app);
        }

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApplication(app, new Uri(addresses!.Addresses.Single()));
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }

    /// <summary>Reads the projection fresh every time so a changed content limit or new row shows on the next request.</summary>
    private sealed class LiveProjection(DisplayWorkProjectionReader reader) : IRawDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => reader.LoadAsync(ct);
        public Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct) => reader.LoadAsync(ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }

    /// <summary>The graph action asks for era actors only with a timeline year; none is known here.</summary>
    private sealed class NoEraActors : IEraActorResolverService
    {
        public Task<ActorResolution?> ResolveActorForEraAsync(string characterQid, int? timelineYear = null, CancellationToken ct = default) =>
            Task.FromResult<ActorResolution?>(null);

        public Task<IReadOnlyDictionary<string, ActorResolution>> ResolveActorsForEraAsync(
            IEnumerable<string> characterQids, int? timelineYear = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ActorResolution>>(new Dictionary<string, ActorResolution>());
    }

    private sealed class FixedAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(authority);
    }

    private sealed class LibraryAccess(Guid libraryId) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(
            RequestAuthority authority, AccountFeatureId feature, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(
            RequestAuthority authority, Guid requestedLibraryId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(requestedLibraryId == libraryId
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.MissingLibraryGrant));

        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(
            RequestAuthority authority, bool requireSurfaceUnlock, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
    }

    private sealed class AllowAuthorizationEvaluator : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(
            RequestAuthority authority,
            AuthorizationRequirement requirement,
            ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "collection-client-test")], Scheme.Name)),
                Scheme.Name)));
    }

    private sealed class TestApplication(WebApplication app, Uri address) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new() { BaseAddress = address };

        public IServiceProvider Services => app.Services;

        public IReadOnlyList<RouteEndpoint> Routes =>
            ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
