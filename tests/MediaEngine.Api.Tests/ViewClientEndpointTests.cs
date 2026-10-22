using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The real <c>/api/v1/view</c> routes for paired native apps, wired to a real data store, real files and a device whose
/// authority can be swapped between calls. A dozen photos and a personal video are spread over four people.
/// </summary>
public sealed class ViewClientEndpointTests : IAsyncLifetime
{
    private static readonly Guid AppId = Guid.Parse("00000000-0000-0000-0000-000000000004");

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-view-client-{Guid.NewGuid():N}.db");
    private readonly string _thumbnailRoot = Path.Combine(Path.GetTempPath(), $"tuvima-view-client-thumbs-{Guid.NewGuid():N}");
    private readonly string _mediaRoot = Path.Combine(Path.GetTempPath(), $"tuvima-view-client-media-{Guid.NewGuid():N}");
    private readonly ActorBox _actor = new();
    private DatabaseConnection _database = null!;
    private LocalAssetRepository _assets = null!;
    private ViewPersonalSpaceRepository _spaces = null!;
    private ViewProfileRepository _policies = null!;
    private ViewGalleryRepository _galleries = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private int _fileNumber;

    private Person _ana = null!;
    private Person _ben = null!;
    private Person _noDocuments = null!;
    private Guid _anaVideo;
    private readonly List<Guid> _anaTimeline = [];

    public async Task InitializeAsync()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _assets = new LocalAssetRepository(_database);
        _spaces = new ViewPersonalSpaceRepository(_database);
        _policies = new ViewProfileRepository(_database);
        _galleries = new ViewGalleryRepository(_database);
        Directory.CreateDirectory(_thumbnailRoot);
        Directory.CreateDirectory(_mediaRoot);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(_actor);
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<ILocalAssetRepository>(_assets);
        builder.Services.AddSingleton<IViewProfileRepository>(_policies);
        builder.Services.AddSingleton<IViewPersonalSpaceRepository>(_spaces);
        builder.Services.AddSingleton<IViewGalleryRepository>(_galleries);
        builder.Services.AddSingleton<IViewSharedLibraryRepository, ViewSharedLibraryRepository>();
        builder.Services.AddSingleton<IProfileRepository, ProfileRepository>();
        builder.Services.AddSingleton<IViewScopeStore, ViewScopePersistenceService>();
        builder.Services.AddSingleton<IViewScopeResolver, ViewScopeResolver>();
        builder.Services.AddSingleton<IViewResourceStore, ViewResourcePersistenceService>();
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator>(new ConsentEvaluator(_actor));
        builder.Services.AddScoped<IViewResourceAuthorizationService, ViewResourceAuthorizationService>();
        builder.Services.AddSingleton<IViewAssetQueryBackend, ViewAssetQueryService>();
        builder.Services.AddSingleton<IViewSmartGalleryQueryService, ViewSmartGalleryQueryService>();
        builder.Services.AddScoped<IViewQueryOrchestrator, ViewQueryOrchestrator>();
        builder.Services.AddScoped<IViewRequestProfileContext, HttpViewRequestProfileContext>();
        builder.Services.AddSingleton<IViewDiscoveryRepository, ViewDiscoveryRepository>();
        builder.Services.AddScoped<ViewDiscoveryService>();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new SwitchableAuthorityResolver(_actor));
        builder.Services.AddSingleton(new ViewThumbnailService(
            new AssetPathService(_thumbnailRoot), NullLogger<ViewThumbnailService>.Instance));
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapViewClientEndpoints();
        await _app.StartAsync();
        var address = new Uri(_app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _client = new HttpClient { BaseAddress = address };

        await SeedAsync();
        _actor.Current = _ana.Authority;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        foreach (var directory in new[] { _thumbnailRoot, _mediaRoot }.Where(Directory.Exists))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Ana: seven photos over three months, one personal video, a document. Ben: five photos. The documents-off person
    // keeps two photos and a document that carries a place, a person and a saved Documents view.
    private async Task SeedAsync()
    {
        _ana = await CreatePerson("StandardUser");
        _ben = await CreatePerson("StandardUser");
        _noDocuments = await CreatePerson("StandardUser");

        var january = new DateTimeOffset(2026, 1, 20, 9, 0, 0, TimeSpan.Zero);
        var february = new DateTimeOffset(2026, 2, 11, 9, 0, 0, TimeSpan.Zero);
        var march = new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
        _anaTimeline.Add(await AddPhoto(_ana, january, "Paris", 48.8566, 2.3522, "Ana"));
        _anaTimeline.Add(await AddPhoto(_ana, january.AddHours(2), "Paris", 48.8570, 2.3530, "Ana"));
        _anaTimeline.Add(await AddPhoto(_ana, february, "Lisbon", 38.7223, -9.1393));
        _anaTimeline.Add(await AddPhoto(_ana, february.AddDays(1)));
        _anaTimeline.Add(await AddPhoto(_ana, february.AddDays(2)));
        _anaTimeline.Add(await AddPhoto(_ana, march));
        _anaTimeline.Add(await AddPhoto(_ana, march.AddDays(1)));
        _anaVideo = await AddVideo(_ana, march.AddDays(2));
        _anaTimeline.Add(_anaVideo);
        _ana.Document = await AddDocument(_ana, "Tax return", "Berlin", 52.52, 13.405, "Clerk");

        for (var index = 0; index < 5; index++)
        {
            _ben.Photos.Add(await AddPhoto(_ben, february.AddDays(index), index == 0 ? "Berlin" : null,
                index == 0 ? 52.52 : null, index == 0 ? 13.405 : null, index == 0 ? "Ben" : null));
        }

        _ben.Document = await AddDocument(_ben, "Ben's lease", null, null, null);

        _noDocuments.Photos.Add(await AddPhoto(_noDocuments, january, "Rome", 41.9028, 12.4964, "Nona"));
        _noDocuments.Photos.Add(await AddPhoto(_noDocuments, february));
        _noDocuments.Document = await AddDocument(_noDocuments, "Passport scan", "Oslo", 59.9139, 10.7522, "Passport Person");
    }

    // ------------------------------------------------------------------ timeline

    [Fact]
    public async Task ATimelineListsOnlyThePairedPersonsPhotosAndVideosNewestFirstByMonth()
    {
        var page = await _client.GetFromJsonAsync<ViewClientTimelinePageDto>("/api/v1/view/timeline");

        Assert.NotNull(page);
        Assert.False(page!.HasMore);
        Assert.Null(page.NextCursor);
        Assert.Equal([(2026, 3), (2026, 2), (2026, 1)], page.Months.Select(month => (month.Year, month.Month)));
        Assert.Equal([3, 3, 2], page.Months.Select(month => month.Items.Count));
        var ids = page.Months.SelectMany(month => month.Items).Select(item => item.Id).ToList();
        Assert.Equal(_anaTimeline.Order(), ids.Order());
        Assert.DoesNotContain(_ana.Document, ids);
        Assert.DoesNotContain(_ben.Photos.Concat(_noDocuments.Photos), id => ids.Contains(id));

        // Newest first inside and across months.
        var taken = page.Months.SelectMany(month => month.Items).Select(item => item.CapturedAt!.Value).ToList();
        Assert.Equal(taken.OrderByDescending(value => value), taken);
        var video = page.Months[0].Items.Single(item => item.Id == _anaVideo);
        Assert.Equal(LocalAssetMediaKinds.Video, video.MediaKind);
        Assert.All(page.Months.SelectMany(month => month.Items), item =>
        {
            Assert.Equal($"/api/v1/view/items/{item.Id:D}/thumbnail", item.ThumbnailUrl);
            Assert.Equal($"/api/v1/view/items/{item.Id:D}/original", item.ContentUrl);
            Assert.Null(item.DocumentInfo);
            Assert.Null(item.Snippet);
            // A paired app can never move anything to Trash.
            Assert.False(item.CanTrash);
        });
    }

    [Fact]
    public async Task TheTimelinePagesWithAnOpaqueCursorWithoutRepeatingOrSkipping()
    {
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var route = "/api/v1/view/timeline?limit=3" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = (await _client.GetFromJsonAsync<ViewClientTimelinePageDto>(route))!;
            seen.AddRange(page.Months.SelectMany(month => month.Items).Select(item => item.Id));
            pages++;
            Assert.True(page.HasMore == (page.NextCursor is not null));
            cursor = page.NextCursor;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(_anaTimeline.Count, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(_anaTimeline.Order(), seen.Order());

        using var invalid = await _client.GetAsync("/api/v1/view/timeline?cursor=not-a-cursor");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        // Asking for nothing or for too much is clamped, never an error and never more than the cap.
        var clamped = (await _client.GetFromJsonAsync<ViewClientTimelinePageDto>("/api/v1/view/timeline?limit=0"))!;
        Assert.Single(clamped.Months.SelectMany(month => month.Items));
        Assert.True(clamped.HasMore);
    }

    [Fact]
    public async Task NoScopeOrProfileTheCallerSuppliesEverChangesWhoseSpaceIsRead()
    {
        var own = await _client.GetStringAsync("/api/v1/view/timeline");
        foreach (var query in new[]
        {
            $"?scope=profile&scopeProfileId={_ben.ProfileId:D}",
            "?scope=shared",
            $"?profileId={_ben.ProfileId:D}",
            $"?scope=mine&scopeProfileId={_ben.ProfileId:D}",
        })
        {
            Assert.Equal(own, await _client.GetStringAsync("/api/v1/view/timeline" + query));
        }

        // The same goes for the discovery and gallery lists.
        var places = await _client.GetStringAsync("/api/v1/view/places");
        Assert.Equal(places, await _client.GetStringAsync($"/api/v1/view/places?scope=profile&scopeProfileId={_ben.ProfileId:D}"));
        var galleries = await _client.GetStringAsync("/api/v1/view/galleries");
        Assert.Equal(galleries, await _client.GetStringAsync($"/api/v1/view/galleries?profileId={_ben.ProfileId:D}"));
    }

    // ------------------------------------------------------------------ one person's items only

    [Fact]
    public async Task ADevicePairedToOneProfileCannotReadAnotherProfilesItemThumbnailPreviewOriginalOrPlayback_AndForeignIdsAnswerLikeUnknownOnes()
    {
        var benPhoto = _ben.Photos[0];
        var unknown = Guid.NewGuid();

        // Ana's device asks for Ben's photo, Ben's document and an id nobody has: one and the same answer each time.
        foreach (var route in new[] { "", "/thumbnail", "/preview", "/original", "/playback" })
        {
            var foreign = await Answer($"/api/v1/view/items/{benPhoto:D}{route}");
            var foreignDocument = await Answer($"/api/v1/view/items/{_ben.Document:D}{route}");
            var missing = await Answer($"/api/v1/view/items/{unknown:D}{route}");
            Assert.Equal(HttpStatusCode.NotFound, missing.Status);
            Assert.Equal(missing, foreign);
            Assert.Equal(missing, foreignDocument);
        }

        // Ben's device cannot read Ana's video through the item, its files or its playback.
        _actor.Current = _ben.Authority;
        foreach (var route in new[] { "", "/thumbnail", "/preview", "/original", "/playback" })
        {
            var foreign = await Answer($"/api/v1/view/items/{_anaVideo:D}{route}");
            var missing = await Answer($"/api/v1/view/items/{unknown:D}{route}");
            Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
            Assert.Equal(missing, foreign);
        }

        // Each person still reads their own.
        using var own = await _client.GetAsync($"/api/v1/view/items/{benPhoto:D}/original");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        _actor.Current = _ana.Authority;
        using var ana = await _client.GetAsync($"/api/v1/view/items/{_anaTimeline[0]:D}/original");
        Assert.Equal(HttpStatusCode.OK, ana.StatusCode);
    }

    [Fact]
    public async Task ItemDetailCarriesSizeTakenDateCameraLocationAndKind()
    {
        var photo = (await _client.GetFromJsonAsync<LocalAssetDto>($"/api/v1/view/items/{_anaTimeline[0]:D}"))!;
        Assert.Equal(LocalAssetMediaKinds.Image, photo.MediaKind);
        Assert.Equal(64, photo.Width);
        Assert.Equal(48, photo.Height);
        Assert.Equal(new DateTimeOffset(2026, 1, 20, 9, 0, 0, TimeSpan.Zero), photo.CapturedAt);
        Assert.Equal("Pixel", photo.DeviceMake);
        Assert.Equal("9 Pro", photo.DeviceModel);
        Assert.Equal(48.8566, photo.Latitude);
        Assert.Equal(2.3522, photo.Longitude);
        Assert.Equal("Paris", photo.LocationName);
        Assert.Contains(photo.Files, file => file.ByteSize > 0);
        Assert.Equal($"/api/v1/view/items/{photo.Id:D}/thumbnail", photo.ThumbnailUrl);

        var video = (await _client.GetFromJsonAsync<LocalAssetDto>($"/api/v1/view/items/{_anaVideo:D}"))!;
        Assert.Equal(LocalAssetMediaKinds.Video, video.MediaKind);
        Assert.Equal(12.5, video.DurationSeconds);
        Assert.Equal("h264", video.VideoCodec);
    }

    [Fact]
    public async Task ThumbnailAndPreviewSizesAreFixedChoicesAndAnswerRealJpegs()
    {
        var photo = _anaTimeline[0];
        foreach (var route in new[]
        {
            $"/api/v1/view/items/{photo:D}/thumbnail", $"/api/v1/view/items/{photo:D}/thumbnail?size=160",
            $"/api/v1/view/items/{photo:D}/thumbnail?size=640", $"/api/v1/view/items/{photo:D}/preview",
            $"/api/v1/view/items/{photo:D}/preview?size=1280",
        })
        {
            using var response = await _client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            using var decoded = SKBitmap.Decode(bytes);
            Assert.NotNull(decoded);
        }

        foreach (var route in new[]
        {
            $"/api/v1/view/items/{photo:D}/thumbnail?size=100", $"/api/v1/view/items/{photo:D}/thumbnail?size=1280",
            $"/api/v1/view/items/{photo:D}/preview?size=640", $"/api/v1/view/items/{photo:D}/preview?size=-1",
        })
        {
            using var response = await _client.GetAsync(route);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    // ------------------------------------------------------------------ originals and playback

    [Fact]
    public async Task TheSignedOriginalRouteFollowsThePlaybackGrantRules()
    {
        var photo = _anaTimeline[0];

        // A device that holds view.personal.read but not view.originals.read gets thumbnails, never the original or
        // a playback manifest, and the same refusal on both routes.
        _actor.Consents = [ApplicationPermissionIds.ViewPersonalRead.Value, ApplicationPermissionIds.ViewGalleriesRead.Value];
        using (var thumbnail = await _client.GetAsync($"/api/v1/view/items/{photo:D}/thumbnail"))
        {
            Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        }

        foreach (var id in new[] { photo, _anaVideo, _ben.Photos[0], Guid.NewGuid() })
        {
            var original = await Answer($"/api/v1/view/items/{id:D}/original");
            var playback = await Answer($"/api/v1/view/items/{id:D}/playback");
            Assert.Equal(HttpStatusCode.Forbidden, original.Status);
            Assert.Equal(original.Status, playback.Status);
        }

        // With the consent both routes answer the same for every kind of caller and item.
        _actor.Consents = AllViewConsents();
        using (var original = await _client.GetAsync($"/api/v1/view/items/{photo:D}/original"))
        {
            Assert.Equal(HttpStatusCode.OK, original.StatusCode);
            Assert.Equal("image/jpeg", original.Content.Headers.ContentType?.MediaType);
            Assert.True(original.Headers.CacheControl is { Private: true, NoCache: true });
            Assert.Equal("nosniff", Assert.Single(original.Headers.GetValues("X-Content-Type-Options")));
        }

        var authorities = new Dictionary<string, RequestAuthority>
        {
            ["a revoked or unknown token"] = new(PrincipalKind.Anonymous, false),
            ["a signed-in browser session"] = _ana.Authority with { PrincipalKind = PrincipalKind.Human, ApplicationId = null, DeviceId = null },
            ["a service application"] = new(PrincipalKind.ServiceApplication, true, ApplicationId: AppId, ApplicationEnabled: true, ApplicationIsAdministrator: true),
            ["a paired app with no device"] = _ana.Authority with { DeviceId = null },
        };
        foreach (var (name, authority) in authorities)
        {
            _actor.Current = authority;
            foreach (var id in new[] { photo, _anaVideo })
            {
                var original = await Answer($"/api/v1/view/items/{id:D}/original");
                var playback = await Answer($"/api/v1/view/items/{id:D}/playback");
                Assert.True(original.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"{name}: original answered {original.Status}.");
                Assert.True(original.Status == playback.Status, $"{name}: original {original.Status} but playback {playback.Status}.");
            }
        }
    }

    [Fact]
    public async Task APersonalVideoPlaysByDirectStreamOfTheOriginalWithRangeSupport()
    {
        var manifest = (await _client.GetFromJsonAsync<PlaybackManifestDto>($"/api/v1/view/items/{_anaVideo:D}/playback"))!;
        Assert.Equal(_anaVideo, manifest.AssetId);
        Assert.Equal(PlaybackDeliveryModes.DirectStream, manifest.RecommendedDelivery);
        Assert.True(manifest.DirectPlaySupported);
        Assert.Equal($"/api/v1/view/items/{_anaVideo:D}/original", manifest.DirectStreamUrl);
        Assert.Null(manifest.HlsUrl);
        Assert.Equal(12.5, manifest.DurationSeconds);
        Assert.Equal("h264", manifest.Technical.VideoCodec);
        Assert.Equal(".mp4", manifest.SourceExtension);

        using var request = new HttpRequestMessage(HttpMethod.Get, manifest.DirectStreamUrl);
        request.Headers.Range = new RangeHeaderValue(0, 9);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(10, (await response.Content.ReadAsByteArrayAsync()).Length);

        // A photo has no playback.
        using var photo = await _client.GetAsync($"/api/v1/view/items/{_anaTimeline[0]:D}/playback");
        Assert.Equal(HttpStatusCode.NotFound, photo.StatusCode);
    }

    [Fact]
    public async Task OnlyAPairedAppWithTheViewConsentMayUseTheseRoutes()
    {
        var routes = new[]
        {
            "/api/v1/view/timeline", "/api/v1/view/galleries", $"/api/v1/view/galleries/{Guid.NewGuid():D}/items",
            "/api/v1/view/people", "/api/v1/view/people/ana/items", "/api/v1/view/places",
            $"/api/v1/view/items/{_anaTimeline[0]:D}", $"/api/v1/view/items/{_anaTimeline[0]:D}/thumbnail",
            $"/api/v1/view/items/{_anaTimeline[0]:D}/preview", $"/api/v1/view/items/{_anaTimeline[0]:D}/original",
            $"/api/v1/view/items/{_anaVideo:D}/playback",
        };

        _actor.Current = new RequestAuthority(PrincipalKind.Anonymous, false);
        foreach (var route in routes)
        {
            using var response = await _client.GetAsync(route);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{route} answered {(int)response.StatusCode} for no token.");
        }

        // A person signed in to the Dashboard is not a paired app.
        _actor.Current = _ana.Authority with { PrincipalKind = PrincipalKind.Human, ApplicationId = null, DeviceId = null };
        foreach (var route in routes)
        {
            using var response = await _client.GetAsync(route);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{route} answered {(int)response.StatusCode} for a browser session.");
        }

        // A paired app that was never given the View consent is refused with the scope it lacks.
        _actor.Current = _ana.Authority;
        _actor.Consents = [ApplicationPermissionIds.LibraryRead.Value];
        foreach (var route in routes)
        {
            using var response = await _client.GetAsync(route);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{route} answered {(int)response.StatusCode} without consent.");
            Assert.Contains("insufficient_scope", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // Nothing here can change anything: only GET is mapped.
        _actor.Consents = AllViewConsents();
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            using var request = new HttpRequestMessage(method, $"/api/v1/view/items/{_anaTimeline[0]:D}") { Content = JsonContent.Create(new { }) };
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }

    // ------------------------------------------------------------------ galleries, people, places

    [Fact]
    public async Task GalleriesListTheOwnersAndSharedOnesAndPageTheirItemsByCursor()
    {
        var space = (await _spaces.GetByOwnerAsync(_ana.ProfileId))!;
        var trip = await _galleries.CreateAsync(new CreateViewGalleryCommand(_ana.ProfileId, space.Id, "Trip", ViewGalleryKind.Manual));
        await _galleries.AddItemsAsync(trip.Id, _anaTimeline.Take(5).ToList(), false);
        var benSpace = (await _spaces.GetByOwnerAsync(_ben.ProfileId))!;
        var bens = await _galleries.CreateAsync(new CreateViewGalleryCommand(_ben.ProfileId, benSpace.Id, "Ben's private", ViewGalleryKind.Manual));
        await _galleries.AddItemsAsync(bens.Id, _ben.Photos.Take(2).ToList(), false);
        var shared = await _galleries.CreateAsync(new CreateViewGalleryCommand(_ben.ProfileId, benSpace.Id, "For Ana", ViewGalleryKind.Manual));
        await _galleries.AddItemsAsync(shared.Id, [_ben.Photos[1]], false);
        await _galleries.ReplaceSharesAsync(shared.Id, [(_ana.ProfileId, ViewGallerySharePermission.View)]);

        var list = (await _client.GetFromJsonAsync<ViewGalleryListResponse>("/api/v1/view/galleries"))!;
        Assert.Equal([trip.Id], list.Owned.Select(gallery => gallery.Id));
        Assert.Equal(5, list.Owned[0].ItemCount);
        Assert.Equal([shared.Id], list.SharedWithYou.Select(gallery => gallery.Id));
        Assert.DoesNotContain(list.Owned.Concat(list.SharedWithYou), gallery => gallery.Id == bens.Id);

        // Items come back as full photo records, newest first, and page without repeats.
        var first = (await _client.GetFromJsonAsync<ViewClientItemPageDto>($"/api/v1/view/galleries/{trip.Id:D}/items?limit=3"))!;
        Assert.Equal(3, first.Items.Count);
        Assert.True(first.HasMore);
        var second = (await _client.GetFromJsonAsync<ViewClientItemPageDto>(
            $"/api/v1/view/galleries/{trip.Id:D}/items?limit=3&cursor={Uri.EscapeDataString(first.NextCursor!)}"))!;
        Assert.Equal(2, second.Items.Count);
        Assert.False(second.HasMore);
        Assert.Equal(_anaTimeline.Take(5).Order(), first.Items.Concat(second.Items).Select(item => item.Id).Order());

        // Ben's own, unshared Gallery and one nobody has are the same 404.
        var foreign = await Answer($"/api/v1/view/galleries/{bens.Id:D}/items");
        var unknown = await Answer($"/api/v1/view/galleries/{Guid.NewGuid():D}/items");
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal(unknown, foreign);

        // The shared one reads only what was shared, and its photo opens.
        var sharedItems = (await _client.GetFromJsonAsync<ViewClientItemPageDto>($"/api/v1/view/galleries/{shared.Id:D}/items"))!;
        Assert.Equal([_ben.Photos[1]], sharedItems.Items.Select(item => item.Id));
        using var sharedPhoto = await _client.GetAsync($"/api/v1/view/items/{_ben.Photos[1]:D}/thumbnail");
        Assert.Equal(HttpStatusCode.OK, sharedPhoto.StatusCode);
        var notShared = await Answer($"/api/v1/view/items/{_ben.Photos[0]:D}/thumbnail");
        Assert.Equal(HttpStatusCode.NotFound, notShared.Status);
    }

    [Fact]
    public async Task PeoplePlacesAndPersonItemsStayInsideTheCallersOwnSpaceAndNeverIncludeADocument()
    {
        var people = (await _client.GetFromJsonAsync<ViewPeoplePageDto>("/api/v1/view/people"))!;
        Assert.Equal(["Ana"], people.Items.Select(person => person.DisplayName));
        Assert.Equal(2, people.Items[0].AssetCount);
        Assert.DoesNotContain(people.Items, person => person.DisplayName is "Ben" or "Clerk" or "Nona");

        var places = (await _client.GetFromJsonAsync<ViewPlacesPageDto>("/api/v1/view/places"))!;
        var names = places.Items.Select(place => place.Name).Order().ToList();
        Assert.Contains("Paris", names);
        Assert.Contains("Lisbon", names);
        Assert.DoesNotContain("Berlin", names);
        Assert.All(places.Items, place => Assert.True(place.Latitude != 0 && place.Longitude != 0));
        Assert.DoesNotContain(places.Items, place => place.RepresentativeAssetId == _ana.Document);

        var items = (await _client.GetFromJsonAsync<ViewClientItemPageDto>("/api/v1/view/people/ana/items"))!;
        Assert.Equal(_anaTimeline.Take(2).Order(), items.Items.Select(item => item.Id).Order());

        // Ben's person and a name nobody has answer alike: this person's space holds neither.
        var ben = await Answer("/api/v1/view/people/ben/items");
        var nobody = await Answer("/api/v1/view/people/nobody-at-all/items");
        Assert.Equal(nobody, ben);
        Assert.DoesNotContain(_ben.Photos[0].ToString("D"), ben.Body, StringComparison.OrdinalIgnoreCase);
        var clerk = await Answer("/api/v1/view/people/clerk/items");
        Assert.Equal(nobody, clerk);
    }

    // ------------------------------------------------------------------ Documents

    [Fact]
    public async Task AProfileWithoutDocumentsLearnsOfNoDocumentThroughTimelineGalleriesCountsPeoplePlacesItemsOrFiles()
    {
        await _policies.SavePolicyAsync(ViewProfilePolicy.Default(_noDocuments.ProfileId) with { DocumentsEnabled = false });
        var space = (await _spaces.GetByOwnerAsync(_noDocuments.ProfileId))!;
        var mixed = await _galleries.CreateAsync(new CreateViewGalleryCommand(
            _noDocuments.ProfileId, space.Id, "Mixed", ViewGalleryKind.Manual, CoverItemId: _noDocuments.Document));
        await _galleries.AddItemsAsync(mixed.Id, [_noDocuments.Photos[0], _noDocuments.Document], true);
        var savedView = await _galleries.CreateAsync(new CreateViewGalleryCommand(
            _noDocuments.ProfileId, space.Id, "Old saved view", ViewGalleryKind.Smart, SmartRuleJson: DocumentViewRule()));
        _actor.Current = _noDocuments.Authority;
        var document = _noDocuments.Document.ToString("D");

        var timeline = await Answer("/api/v1/view/timeline");
        Assert.Equal(HttpStatusCode.OK, timeline.Status);
        Assert.Equal(_noDocuments.Photos.Order(), TimelineIds(timeline.Body).Order());
        AssertNoDocumentTrace(timeline.Body, document);

        // Galleries: no saved Documents view, no document in a count, no document as a cover.
        var galleries = await Answer("/api/v1/view/galleries");
        AssertNoDocumentTrace(galleries.Body, document);
        var list = JsonSerializer.Deserialize<ViewGalleryListResponse>(galleries.Body)!;
        Assert.DoesNotContain(list.Owned, gallery => gallery.Id == savedView.Id);
        var mixedGallery = list.Owned.Single(gallery => gallery.Id == mixed.Id);
        Assert.Equal(1, mixedGallery.ItemCount);
        Assert.Null(mixedGallery.CoverItemId);

        var mixedItems = await Answer($"/api/v1/view/galleries/{mixed.Id:D}/items");
        Assert.Equal(HttpStatusCode.OK, mixedItems.Status);
        AssertNoDocumentTrace(mixedItems.Body, document);
        Assert.Equal([_noDocuments.Photos[0]], JsonSerializer.Deserialize<ViewClientItemPageDto>(mixedItems.Body)!.Items.Select(item => item.Id));
        var savedViewItems = await Answer($"/api/v1/view/galleries/{savedView.Id:D}/items");
        var unknownGallery = await Answer($"/api/v1/view/galleries/{Guid.NewGuid():D}/items");
        Assert.Equal(unknownGallery, savedViewItems);

        // People and places never carry the document's person or place; asking for them by name finds nothing.
        var people = await Answer("/api/v1/view/people");
        var places = await Answer("/api/v1/view/places");
        Assert.DoesNotContain("Passport Person", people.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Oslo", places.Body, StringComparison.Ordinal);
        Assert.Contains("Nona", people.Body, StringComparison.Ordinal);
        Assert.Contains("Rome", places.Body, StringComparison.Ordinal);
        Assert.Equal(await Answer("/api/v1/view/people/nobody-at-all/items"), await Answer("/api/v1/view/people/passport%20person/items"));

        // The document itself, and every file made from it, is exactly what an id nobody has answers.
        foreach (var route in new[] { "", "/thumbnail", "/preview", "/original", "/playback", "/thumbnail?size=160" })
        {
            var asked = await Answer($"/api/v1/view/items/{document}{route}");
            var unknown = await Answer($"/api/v1/view/items/{Guid.NewGuid():D}{route}");
            Assert.Equal(HttpStatusCode.NotFound, asked.Status);
            Assert.Equal(unknown, asked);
        }
    }

    // ------------------------------------------------------------------ per-kind permission (photos, videos, documents)

    private static readonly string[] ItemRoutes = ["", "/thumbnail", "/preview", "/original"];

    [Fact]
    public async Task AProfileWithPhotosOffGetsNotFoundForAPhotosItemThumbnailPreviewOriginalAndDownload()
    {
        await _policies.SavePolicyAsync(ViewProfilePolicy.Default(_noDocuments.ProfileId) with { PhotosEnabled = false });
        _actor.Current = _noDocuments.Authority;
        var photo = _noDocuments.Photos[0];

        foreach (var route in ItemRoutes)
        {
            var asked = await Answer($"/api/v1/view/items/{photo:D}{route}");
            var unknown = await Answer($"/api/v1/view/items/{Guid.NewGuid():D}{route}");
            Assert.Equal(HttpStatusCode.NotFound, asked.Status);
            Assert.Equal(unknown, asked);
        }

        using var scope = _app.Services.CreateScope();
        var access = PhotoAccess(scope);
        Assert.Equal(PhotoKeepAccess.NotFound, await access.EvaluateAsync(_noDocuments.Authority, photo));
        Assert.DoesNotContain(photo, (await access.EvaluateManyAsync(_noDocuments.Authority, _noDocuments.Photos)).Visible);

        // Someone whose Photos are still on is unaffected.
        _actor.Current = _ana.Authority;
        Assert.Equal(HttpStatusCode.OK, (await Answer($"/api/v1/view/items/{_anaTimeline[0]:D}")).Status);
        Assert.Equal(PhotoKeepAccess.Allowed, await access.EvaluateAsync(_ana.Authority, _anaTimeline[0]));
    }

    [Fact]
    public async Task AJpegInADocumentsFolderGetsNotFoundOnEveryRouteForAProfileWithDocumentsOff_AndStaysAvailableToOthers()
    {
        var receipt = await AddPhoto(_noDocuments, new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero),
            contentKind: ViewContentKinds.Documents);
        var anasReceipt = await AddPhoto(_ana, new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero),
            contentKind: ViewContentKinds.Documents);
        await _policies.SavePolicyAsync(ViewProfilePolicy.Default(_noDocuments.ProfileId) with { DocumentsEnabled = false });

        _actor.Current = _noDocuments.Authority;
        foreach (var route in ItemRoutes)
        {
            var asked = await Answer($"/api/v1/view/items/{receipt:D}{route}");
            var unknown = await Answer($"/api/v1/view/items/{Guid.NewGuid():D}{route}");
            Assert.Equal(HttpStatusCode.NotFound, asked.Status);
            Assert.Equal(unknown, asked);
        }

        using var scope = _app.Services.CreateScope();
        var access = PhotoAccess(scope);
        Assert.Equal(PhotoKeepAccess.NotFound, await access.EvaluateAsync(_noDocuments.Authority, receipt));
        Assert.DoesNotContain(receipt, (await access.EvaluateManyAsync(_noDocuments.Authority, [receipt])).Visible);

        // A profile with Documents on still reads the same kind of item.
        _actor.Current = _ana.Authority;
        foreach (var route in ItemRoutes)
        {
            Assert.Equal(HttpStatusCode.OK, (await Answer($"/api/v1/view/items/{anasReceipt:D}{route}")).Status);
        }

        Assert.Equal(PhotoKeepAccess.Allowed, await access.EvaluateAsync(_ana.Authority, anasReceipt));
    }

    [Fact]
    public async Task ThePhotosSurfaceNeverShowsADocumentToAnyoneEvenAPersonWhoMayUseDocuments()
    {
        var document = _ana.Document.ToString("D");
        var timeline = await Answer("/api/v1/view/timeline");
        AssertNoDocumentTrace(timeline.Body, document);
        foreach (var route in new[] { "", "/thumbnail", "/preview", "/original", "/playback" })
        {
            var asked = await Answer($"/api/v1/view/items/{document}{route}");
            var unknown = await Answer($"/api/v1/view/items/{Guid.NewGuid():D}{route}");
            Assert.Equal(unknown, asked);
        }

        var places = await Answer("/api/v1/view/places");
        var people = await Answer("/api/v1/view/people");
        Assert.DoesNotContain("Berlin", places.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Clerk", people.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASharedGalleryNeverCarriesTheOwnersSpaceRuleRemovedLocationPeopleOrTags()
    {
        var benPhoto = _ben.Photos[0];
        await _assets.ReplaceTagsAsync(benPhoto, ["Secret tag"]);
        var benSpace = (await _spaces.GetByOwnerAsync(_ben.ProfileId))!;
        var manual = await _galleries.CreateAsync(new CreateViewGalleryCommand(_ben.ProfileId, benSpace.Id, "Manual share", ViewGalleryKind.Manual));
        await _galleries.AddItemsAsync(manual.Id, [benPhoto], false);
        await _galleries.ReplaceSharesAsync(manual.Id, [(_ana.ProfileId, ViewGallerySharePermission.View)]);
        var smart = await _galleries.CreateAsync(new CreateViewGalleryCommand(
            _ben.ProfileId, benSpace.Id, "Smart share", ViewGalleryKind.Smart, SmartRuleJson: ImageRule()));
        await _galleries.ReplaceSharesAsync(smart.Id, [(_ana.ProfileId, ViewGallerySharePermission.View)]);
        // Ben removes the photo's location: the place is gone, but the photo still carries where it was embedded.
        Assert.True(await _assets.UpdateLocationAsync(benPhoto, new LocalAssetLocationUpdate(null, null)));

        // The owner still sees everything of their own.
        _actor.Current = _ben.Authority;
        var own = (await _client.GetFromJsonAsync<LocalAssetDto>($"/api/v1/view/items/{benPhoto:D}"))!;
        Assert.Null(own.Latitude);
        Assert.NotNull(own.EmbeddedLatitude);
        Assert.NotNull(own.EmbeddedLongitude);
        Assert.Equal(["Ben"], own.People);
        Assert.Equal(["Secret tag"], own.Tags);
        Assert.Equal(_ben.ProfileId, own.OwnerProfileId);
        var ownGalleries = (await _client.GetFromJsonAsync<ViewGalleryListResponse>("/api/v1/view/galleries"))!;
        var ownSmart = ownGalleries.Owned.Single(gallery => gallery.Id == smart.Id);
        Assert.NotNull(ownSmart.SmartRuleJson);
        Assert.Equal(benSpace.Id, ownSmart.PersonalSpaceId);

        // The person it was shared with sees the gallery and the photo, but nothing from Ben's space.
        _actor.Current = _ana.Authority;
        var galleries = (await _client.GetFromJsonAsync<ViewGalleryListResponse>("/api/v1/view/galleries"))!;
        Assert.Equal(new[] { manual.Id, smart.Id }.Order(), galleries.SharedWithYou.Select(gallery => gallery.Id).Order());
        Assert.All(galleries.SharedWithYou, gallery =>
        {
            Assert.Null(gallery.SmartRuleJson);
            Assert.Equal(Guid.Empty, gallery.OwnerProfileId);
            Assert.Equal(Guid.Empty, gallery.PersonalSpaceId);
        });
        Assert.DoesNotContain(benSpace.Id.ToString("D"), await _client.GetStringAsync("/api/v1/view/galleries"), StringComparison.OrdinalIgnoreCase);

        var viaGallery = (await _client.GetFromJsonAsync<ViewClientItemPageDto>($"/api/v1/view/galleries/{manual.Id:D}/items"))!;
        var viaItem = (await _client.GetFromJsonAsync<LocalAssetDto>($"/api/v1/view/items/{benPhoto:D}"))!;
        foreach (var shared in new[] { Assert.Single(viaGallery.Items), viaItem })
        {
            Assert.Equal(benPhoto, shared.Id);
            Assert.Null(shared.Latitude);
            Assert.Null(shared.EmbeddedLatitude);
            Assert.Null(shared.EmbeddedLongitude);
            Assert.Null(shared.EmbeddedCapturedAt);
            Assert.Null(shared.OwnerProfileId);
            Assert.Null(shared.PersonalSpaceId);
            Assert.Empty(shared.People!);
            Assert.Empty(shared.Tags);
        }
    }

    [Fact]
    public async Task ThumbnailsAndPreviewsAreMarkedPrivateSoNoSharedCacheKeepsThem()
    {
        foreach (var route in new[] { "thumbnail", "thumbnail?size=160", "preview", "preview?size=1280" })
        {
            using var response = await _client.GetAsync($"/api/v1/view/items/{_anaTimeline[0]:D}/{route}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl is { Private: true, Public: false }, $"{route} is not marked private.");
        }
    }

    [Fact]
    public async Task ASavedDocumentsViewWithABadCursorAnswersLikeAGalleryNobodyHas()
    {
        await _policies.SavePolicyAsync(ViewProfilePolicy.Default(_noDocuments.ProfileId) with { DocumentsEnabled = false });
        var space = (await _spaces.GetByOwnerAsync(_noDocuments.ProfileId))!;
        var savedView = await _galleries.CreateAsync(new CreateViewGalleryCommand(
            _noDocuments.ProfileId, space.Id, "Old saved view", ViewGalleryKind.Smart, SmartRuleJson: DocumentViewRule()));
        var ordinary = await _galleries.CreateAsync(new CreateViewGalleryCommand(
            _noDocuments.ProfileId, space.Id, "Ordinary", ViewGalleryKind.Manual));
        _actor.Current = _noDocuments.Authority;

        foreach (var cursor in new[] { "", "?cursor=not-a-cursor", "?cursor=%00%00" })
        {
            var savedViewAnswer = await Answer($"/api/v1/view/galleries/{savedView.Id:D}/items{cursor}");
            var nobodysAnswer = await Answer($"/api/v1/view/galleries/{Guid.NewGuid():D}/items{cursor}");
            Assert.Equal(HttpStatusCode.NotFound, nobodysAnswer.Status);
            Assert.Equal(nobodysAnswer, savedViewAnswer);
        }

        // A real Gallery of their own still says plainly that the cursor is bad.
        var bad = await Answer($"/api/v1/view/galleries/{ordinary.Id:D}/items?cursor=not-a-cursor");
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
    }

    // ------------------------------------------------------------------ route inventory

    [Fact]
    public void EveryClientViewRouteIsReadOnlyAuthenticatedAndNamesTheScopeItNeeds()
    {
        var endpoints = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/view", StringComparison.Ordinal) == true)
            .ToList();

        var routes = endpoints
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .Order().ToList();
        Assert.Equal(new[]
        {
            "GET /api/v1/view/galleries",
            "GET /api/v1/view/galleries/{id:guid}/items",
            "GET /api/v1/view/items/{id:guid}",
            "GET /api/v1/view/items/{id:guid}/original",
            "GET /api/v1/view/items/{id:guid}/playback",
            "GET /api/v1/view/items/{id:guid}/preview",
            "GET /api/v1/view/items/{id:guid}/thumbnail",
            "GET /api/v1/view/people",
            "GET /api/v1/view/people/{key}/items",
            "GET /api/v1/view/places",
            "GET /api/v1/view/timeline",
        }, routes);

        Assert.All(endpoints, endpoint =>
        {
            Assert.Contains(endpoint.Metadata.OfType<IAuthorizeData>(), data => data.Policy == AuthPolicies.Authenticated);
            Assert.NotNull(endpoint.Metadata.GetMetadata<ClientScopeRequirementMetadata>());
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        });
        var originals = endpoints.Where(endpoint =>
            endpoint.RoutePattern.RawText!.EndsWith("/original", StringComparison.Ordinal)
            || endpoint.RoutePattern.RawText.EndsWith("/playback", StringComparison.Ordinal));
        Assert.All(originals, endpoint => Assert.Equal(ApplicationPermissionIds.ViewOriginalsRead.Value,
            endpoint.Metadata.GetMetadata<ClientScopeRequirementMetadata>()!.Scope));
    }

    // ------------------------------------------------------------------ keeping photos on the device

    [Fact]
    public async Task KeepingAPhotoOnTheDevice_IsJudgedLikeThePreviewRoute_AndForeignOrDocumentIdsAnswerLikeUnknownOnes()
    {
        using var scope = _app.Services.CreateScope();
        var access = new ViewPhotoDownloadAccess(
            scope.ServiceProvider.GetRequiredService<IViewResourceAuthorizationService>(),
            scope.ServiceProvider.GetRequiredService<IViewResourceStore>(),
            _assets,
            scope.ServiceProvider.GetRequiredService<IViewProfileRepository>());

        // Ana's own photos and video may be kept.
        Assert.Equal(PhotoKeepAccess.Allowed, await access.EvaluateAsync(_ana.Authority, _anaTimeline[0]));
        Assert.Equal(PhotoKeepAccess.Allowed, await access.EvaluateAsync(_ana.Authority, _anaVideo));

        // Ben's photo and document, her own document and an unknown id are one and the same answer.
        foreach (var id in new[] { _ben.Photos[0], _ben.Document, _ana.Document, Guid.NewGuid() })
        {
            Assert.Equal(PhotoKeepAccess.NotFound, await access.EvaluateAsync(_ana.Authority, id));
        }

        // A person without Documents cannot learn that a document exists: it reads as unknown, like for everyone.
        Assert.Equal(PhotoKeepAccess.NotFound, await access.EvaluateAsync(_noDocuments.Authority, _noDocuments.Document));
        Assert.Equal(PhotoKeepAccess.Allowed, await access.EvaluateAsync(_noDocuments.Authority, _noDocuments.Photos[0]));

        // Ben's own device follows the same rules for Ana's items.
        Assert.Equal(PhotoKeepAccess.NotFound, await access.EvaluateAsync(_ben.Authority, _anaTimeline[0]));
    }

    [Fact]
    public async Task KeepingAPhotoOnTheDevice_NeedsTheViewPersonalReadScopeOnTheToken()
    {
        using var scope = _app.Services.CreateScope();
        var access = new ViewPhotoDownloadAccess(
            scope.ServiceProvider.GetRequiredService<IViewResourceAuthorizationService>(),
            scope.ServiceProvider.GetRequiredService<IViewResourceStore>(),
            _assets,
            scope.ServiceProvider.GetRequiredService<IViewProfileRepository>());

        _actor.Consents = [ApplicationPermissionIds.LibraryRead.Value];
        foreach (var id in new[] { _anaTimeline[0], _ben.Photos[0], Guid.NewGuid() })
        {
            Assert.Equal(PhotoKeepAccess.ScopeMissing, await access.EvaluateAsync(_ana.Authority, id));
        }

        // Originals consent alone is not photo consent, and thumbnails need only the personal scope.
        _actor.Consents = [ApplicationPermissionIds.ViewPersonalRead.Value];
        Assert.Equal(PhotoKeepAccess.Allowed, await access.EvaluateAsync(_ana.Authority, _anaTimeline[0]));

        Assert.Equal(PhotoKeepAccess.ScopeMissing, await access.EvaluateAsync(new RequestAuthority(PrincipalKind.Anonymous, false), _anaTimeline[0]));
    }

    [Fact]
    public async Task TheBulkPhotoCheck_GivesTheSameVisibilityAsTheOneByOneCheck_AndAMissingFileDoesNotHideAPhoto()
    {
        // Ben shares one of his photos with Ana through a Gallery, so Ana's reach includes a photo that is not hers.
        var benSpace = (await _spaces.GetByOwnerAsync(_ben.ProfileId))!;
        var shared = await _galleries.CreateAsync(new CreateViewGalleryCommand(_ben.ProfileId, benSpace.Id, "For Ana", ViewGalleryKind.Manual));
        await _galleries.AddItemsAsync(shared.Id, [_ben.Photos[1]], false);
        await _galleries.ReplaceSharesAsync(shared.Id, [(_ana.ProfileId, ViewGallerySharePermission.View)]);

        // The volume holding one of Ana's photos is unmounted for now.
        var unmounted = _anaTimeline[1];
        File.Delete(_assets.ResolveContent(unmounted)!.FilePath);

        using var scope = _app.Services.CreateScope();
        var access = new ViewPhotoDownloadAccess(
            scope.ServiceProvider.GetRequiredService<IViewResourceAuthorizationService>(),
            scope.ServiceProvider.GetRequiredService<IViewResourceStore>(),
            _assets,
            scope.ServiceProvider.GetRequiredService<IViewProfileRepository>());
        var ids = _anaTimeline
            .Concat(_ben.Photos)
            .Concat(_noDocuments.Photos)
            .Concat([_ana.Document, _ben.Document, _noDocuments.Document, Guid.NewGuid(), Guid.NewGuid()])
            .ToList();

        foreach (var caller in new[] { _ana, _ben, _noDocuments })
        {
            var bulk = await access.EvaluateManyAsync(caller.Authority, ids);
            Assert.False(bulk.ScopeMissing);
            foreach (var id in ids)
            {
                var single = await access.EvaluateAsync(caller.Authority, id);
                Assert.Equal(single is PhotoKeepAccess.Allowed or PhotoKeepAccess.FileMissing, bulk.Visible.Contains(id));
            }
        }

        var ana = await access.EvaluateManyAsync(_ana.Authority, ids);
        Assert.Contains(unmounted, ana.Visible);
        Assert.Equal(PhotoKeepAccess.FileMissing, await access.EvaluateAsync(_ana.Authority, unmounted));
        // Her own photos and video, and the one Ben shared; not his others, nor any document, nor an unknown id.
        Assert.Equal(_anaTimeline.Append(_ben.Photos[1]).Order(), ana.Visible.Order());

        // Withdrawing the share hides it again; the missing file never did.
        await _galleries.ReplaceSharesAsync(shared.Id, []);
        var withdrawn = await access.EvaluateManyAsync(_ana.Authority, ids);
        Assert.DoesNotContain(_ben.Photos[1], withdrawn.Visible);
        Assert.Contains(unmounted, withdrawn.Visible);
    }

    [Fact]
    public async Task APhotoWhoseFileSourceIsGoneForGood_IsHiddenByBothChecks_WhileAMissingFileIsOnlyAGap()
    {
        var gone = _anaTimeline[2];
        var unmounted = _anaTimeline[3];
        File.Delete(_assets.ResolveContent(unmounted)!.FilePath);
        using (var connection = _database.CreateConnection())
        {
            // The index no longer has any source for the photo's primary file: not a gap, the photo is lost.
            connection.Execute(
                "DELETE FROM local_file_sources WHERE file_id IN (SELECT file_id FROM local_item_files WHERE item_id = @gone);",
                new { gone });
        }

        using var scope = _app.Services.CreateScope();
        var access = PhotoAccess(scope);
        Assert.Equal(PhotoKeepAccess.NotFound, await access.EvaluateAsync(_ana.Authority, gone));
        Assert.Equal(PhotoKeepAccess.FileMissing, await access.EvaluateAsync(_ana.Authority, unmounted));

        var bulk = await access.EvaluateManyAsync(_ana.Authority, _anaTimeline);
        Assert.DoesNotContain(gone, bulk.Visible);
        Assert.Contains(unmounted, bulk.Visible);
        Assert.Equal(_anaTimeline.Count - 1, bulk.Visible.Count);
    }

    [Fact]
    public async Task TheBulkPhotoCheck_HandlesMoreThanOneChunkWithRepeatedIds()
    {
        using var scope = _app.Services.CreateScope();
        var access = PhotoAccess(scope);

        // 1,300 ids: real ones placed before, across and after the 500-id chunk boundaries, each repeated, among unknown ids.
        var ids = new List<Guid>();
        ids.AddRange(_anaTimeline.Take(3));
        ids.AddRange(Enumerable.Range(0, 497).Select(_ => Guid.NewGuid()));
        ids.AddRange(_anaTimeline.Skip(3).Take(3));
        ids.AddRange(Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()));
        ids.AddRange(_anaTimeline);
        ids.AddRange(_ben.Photos);
        ids.AddRange(_anaTimeline);
        ids.AddRange(Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()));
        ids.Add(Guid.Empty);
        Assert.True(ids.Count > 1_000);

        var bulk = await access.EvaluateManyAsync(_ana.Authority, ids);
        Assert.False(bulk.ScopeMissing);
        Assert.Equal(_anaTimeline.Order(), bulk.Visible.Order());
        foreach (var id in _anaTimeline.Concat(_ben.Photos).Concat(ids.Skip(3).Take(5)))
        {
            Assert.Equal(
                await access.EvaluateAsync(_ana.Authority, id) is PhotoKeepAccess.Allowed or PhotoKeepAccess.FileMissing,
                bulk.Visible.Contains(id));
        }
    }

    [Fact]
    public async Task ArchivedTrashedAndHiddenPhotos_AreJudgedTheSameByTheBulkAndTheSingleCheck()
    {
        var archived = _anaTimeline[0];
        var trashed = _anaTimeline[1];
        var hidden = _anaTimeline[2];
        using (var connection = _database.CreateConnection())
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            connection.Execute("UPDATE local_items SET archived_at = @now WHERE id = @archived;", new { now, archived });
            connection.Execute("UPDATE local_items SET trashed_at = @now WHERE id = @trashed;", new { now, trashed });
            connection.Execute("UPDATE local_items SET hidden = 1 WHERE id = @hidden;", new { hidden });
        }

        using var scope = _app.Services.CreateScope();
        var access = PhotoAccess(scope);
        var bulk = await access.EvaluateManyAsync(_ana.Authority, _anaTimeline);
        foreach (var id in _anaTimeline)
        {
            Assert.Equal(
                await access.EvaluateAsync(_ana.Authority, id) is PhotoKeepAccess.Allowed or PhotoKeepAccess.FileMissing,
                bulk.Visible.Contains(id));
        }

        // Both paths agree that such a photo the person still owns is theirs to keep (the timeline's own filters do not apply).
        Assert.Contains(archived, bulk.Visible);
        Assert.Contains(trashed, bulk.Visible);
        Assert.Contains(hidden, bulk.Visible);
    }

    private ViewPhotoDownloadAccess PhotoAccess(IServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<IViewResourceAuthorizationService>(),
        scope.ServiceProvider.GetRequiredService<IViewResourceStore>(),
        _assets,
        scope.ServiceProvider.GetRequiredService<IViewProfileRepository>());

    [Fact]
    public async Task TheBulkPhotoCheck_NeedsThePersonalReadScopeAndAnAuthenticatedCaller()
    {
        using var scope = _app.Services.CreateScope();
        var access = new ViewPhotoDownloadAccess(
            scope.ServiceProvider.GetRequiredService<IViewResourceAuthorizationService>(),
            scope.ServiceProvider.GetRequiredService<IViewResourceStore>(),
            _assets,
            scope.ServiceProvider.GetRequiredService<IViewProfileRepository>());

        _actor.Consents = [ApplicationPermissionIds.LibraryRead.Value];
        Assert.True((await access.EvaluateManyAsync(_ana.Authority, _anaTimeline)).ScopeMissing);
        Assert.Empty((await access.EvaluateManyAsync(_ana.Authority, _anaTimeline)).Visible);

        _actor.Consents = [ApplicationPermissionIds.ViewPersonalRead.Value];
        Assert.Equal(_anaTimeline.Order(), (await access.EvaluateManyAsync(_ana.Authority, _anaTimeline)).Visible.Order());
        Assert.True((await access.EvaluateManyAsync(new RequestAuthority(PrincipalKind.Anonymous, false), _anaTimeline)).ScopeMissing);

        // Nothing asked about is nothing visible.
        var none = await access.EvaluateManyAsync(_ana.Authority, []);
        Assert.False(none.ScopeMissing);
        Assert.Empty(none.Visible);
    }

    // ------------------------------------------------------------------ helpers

    private static string[] AllViewConsents() =>
    [
        ApplicationPermissionIds.ViewPersonalRead.Value,
        ApplicationPermissionIds.ViewOriginalsRead.Value,
        ApplicationPermissionIds.ViewGalleriesRead.Value,
    ];

    private static string ImageRule() =>
        ViewSmartGalleryRules.Serialize(CollectionRuleDefinition.SingleGroup(
        [
            new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "image" },
        ]));

    private static string DocumentViewRule() =>
        ViewSmartGalleryRules.Serialize(CollectionRuleDefinition.SingleGroup(
        [
            new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "document" },
        ]));

    private static IEnumerable<Guid> TimelineIds(string body)
    {
        var page = JsonSerializer.Deserialize<ViewClientTimelinePageDto>(body)!;
        return page.Months.SelectMany(month => month.Items).Select(item => item.Id);
    }

    private static void AssertNoDocumentTrace(string body, string documentId)
    {
        Assert.DoesNotContain(documentId, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Passport scan", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Tax return", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"document\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("document_info\":{", body, StringComparison.Ordinal);
    }

    private sealed record Reply(HttpStatusCode Status, string? ContentType, string Body);

    private async Task<Reply> Answer(string route)
    {
        using var response = await _client.GetAsync(route);
        var body = await response.Content.ReadAsStringAsync();
        // A trace id is the only thing that may differ between two otherwise identical problem answers.
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.Ordinal) == true
            && JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) is { } fields)
        {
            fields.Remove("traceId");
            body = JsonSerializer.Serialize(fields.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value));
        }

        return new Reply(response.StatusCode, response.Content.Headers.ContentType?.MediaType, body);
    }

    private sealed class Person(Guid profileId, Guid libraryId, RequestAuthority authority)
    {
        public Guid ProfileId { get; } = profileId;
        public Guid LibraryId { get; } = libraryId;
        public RequestAuthority Authority { get; } = authority;
        public Guid Document { get; set; }
        public List<Guid> Photos { get; } = [];
    }

    private async Task<Person> CreatePerson(string role)
    {
        var profileId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
                VALUES (@profileId, @name, '#7C4DFF', @role, @now);
                """, new { profileId, name = $"Profile {profileId:N}", role, now = DateTimeOffset.UtcNow });
        }

        await _spaces.CreateAsync(profileId, libraryId);
        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, AccountId: Guid.NewGuid(), ActiveProfileId: profileId,
            ApplicationId: AppId, DeviceId: Guid.NewGuid(), AccountEnabled: true, GrantEnabled: true, ApplicationEnabled: true);
        return new Person(profileId, libraryId, authority);
    }

    private Guid AddSource(Person person, Guid spaceId)
    {
        var sourceId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO view_sources
                (id, scope_kind, personal_space_id, library_id, source_type, name, source_key, storage_mode,
                 relative_path, external_path, include_subdirectories, enabled, created_at, updated_at)
            VALUES (@sourceId, 'personal', @spaceId, @libraryId, 'browser_upload', 'Uploads', @sourceKey, 'managed',
                    'Personal/Uploads/test', NULL, 1, 1, @now, @now);
            """, new { sourceId, spaceId, libraryId = person.LibraryId, sourceKey = $"upload:{sourceId:N}", now = DateTimeOffset.UtcNow });
        return sourceId;
    }

    private string WriteFile(string extension, byte[] contents)
    {
        var path = Path.Combine(_mediaRoot, $"file-{Interlocked.Increment(ref _fileNumber):D3}{extension}");
        File.WriteAllBytes(path, contents);
        return path;
    }

    private static byte[] Jpeg()
    {
        using var bitmap = new SKBitmap(64, 48);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    private static string Hash() => $"{Guid.NewGuid():N}{Guid.NewGuid():N}";

    private async Task<Guid> AddPhoto(Person person, DateTimeOffset takenAt, string? place = null,
        double? latitude = null, double? longitude = null, string? personName = null, string? contentKind = null)
    {
        var space = (await _spaces.GetByOwnerAsync(person.ProfileId))!;
        var path = WriteFile(".jpg", Jpeg());
        var itemId = (await _assets.UpsertAsync(new LocalAssetRegistration(
            person.LibraryId, space.Id, person.ProfileId, LocalAssetMediaKinds.Image, $"Photo {_fileNumber}", takenAt,
            [new LocalAssetFileRegistration(path, Hash(), Path.GetFileName(path), "image/jpeg", new FileInfo(path).Length,
                DateTimeOffset.UtcNow) with { SourceId = AddSource(person, space.Id) }],
            Width: 64, Height: 48, DeviceMake: "Pixel", DeviceModel: "9 Pro",
            Latitude: latitude, Longitude: longitude, LocationName: place, ContentKind: contentKind))).ItemId;
        if (personName is not null)
        {
            await _assets.ReplacePeopleAsync(itemId, [personName]);
        }

        return itemId;
    }

    private async Task<Guid> AddVideo(Person person, DateTimeOffset takenAt)
    {
        var space = (await _spaces.GetByOwnerAsync(person.ProfileId))!;
        var path = WriteFile(".mp4", Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray());
        return (await _assets.UpsertAsync(new LocalAssetRegistration(
            person.LibraryId, space.Id, person.ProfileId, LocalAssetMediaKinds.Video, "Birthday", takenAt,
            [new LocalAssetFileRegistration(path, Hash(), Path.GetFileName(path), "video/mp4", new FileInfo(path).Length,
                DateTimeOffset.UtcNow) with { SourceId = AddSource(person, space.Id) }],
            Width: 1920, Height: 1080, DurationSeconds: 12.5, VideoCodec: "h264", FrameRate: 30))).ItemId;
    }

    private async Task<Guid> AddDocument(Person person, string title, string? place,
        double? latitude, double? longitude, string? personName = null)
    {
        var space = (await _spaces.GetByOwnerAsync(person.ProfileId))!;
        var path = WriteFile(".pdf", "%PDF-1.4 test"u8.ToArray());
        var itemId = (await _assets.UpsertAsync(new LocalAssetRegistration(
            person.LibraryId, space.Id, person.ProfileId, LocalAssetMediaKinds.Document, title,
            new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero),
            [new LocalAssetFileRegistration(path, Hash(), Path.GetFileName(path), "application/pdf", new FileInfo(path).Length,
                DateTimeOffset.UtcNow) with { SourceId = AddSource(person, space.Id) }],
            Latitude: latitude, Longitude: longitude, LocationName: place))).ItemId;
        if (personName is not null)
        {
            await _assets.ReplacePeopleAsync(itemId, [personName]);
        }

        return itemId;
    }

    private sealed class ActorBox
    {
        public RequestAuthority Current { get; set; } = new(PrincipalKind.Anonymous, false);

        /// <summary>What the device token was consented to; the Application is assumed to hold all of it.</summary>
        public IReadOnlyCollection<string> Consents { get; set; } = AllViewConsents();
    }

    private sealed class SwitchableAuthorityResolver(ActorBox actor) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(actor.Current);
    }

    /// <summary>
    /// Stands in for the live evaluator: an Application permission is allowed only when the device token was consented
    /// to it (a delegated or service caller), and account features are allowed. The View resource rules under test are real.
    /// </summary>
    private sealed class ConsentEvaluator(ActorBox actor) : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken ct = default) =>
            ValueTask.FromResult(requirement.ApplicationPermission is { } permission
                && !actor.Consents.Contains(permission.Value)
                    ? AuthorizationDecision.Deny(AuthorizationDenialReason.MissingPermission)
                    : AuthorizationDecision.Allow());
    }

    /// <summary>Signs a request in whenever the swapped-in authority is authenticated; otherwise it is the 401 a bad token gets.</summary>
    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ActorBox actor)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(actor.Current.IsAuthenticated
                ? AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "view-client-test")], Scheme.Name)),
                    Scheme.Name))
                : AuthenticateResult.NoResult());
    }
}
