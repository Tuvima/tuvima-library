using System.Security.Cryptography;
using System.Text.Json;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.DevSupport;

/// <summary>
/// Development-only, repeatable View fixture runner. Remote images are free-licensed
/// Wikimedia Commons files; the harness supplies deterministic metadata so upstream
/// EXIF changes cannot make the assertions flaky.
/// </summary>
public sealed class ViewPhotoHarnessService(
    IHttpClientFactory httpClientFactory,
    ViewLibraryService viewLibrary,
    ViewStorageService viewStorage,
    ILocalAssetRepository assets,
    IViewDiscoveryRepository discovery,
    ILogger<ViewPhotoHarnessService> logger)
{
    public static readonly Guid ProfileId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string ProvenanceSource = "tuvima-dev-harness:reviewed-fixture";

    private static readonly Fixture[] Fixtures =
    [
        new("portrait-khwairakpam-chaoba.jpg", "Khwairakpam Chaoba portrait", "https://commons.wikimedia.org/wiki/Special:Redirect/file/A_portrait_of_Khwairakpam_Chaoba.jpg?width=1280", "https://commons.wikimedia.org/wiki/File:A_portrait_of_Khwairakpam_Chaoba.jpg", "CC0 1.0", "Singh, Prafullo", new DateTimeOffset(2021, 1, 15, 10, 30, 0, TimeSpan.Zero), 24.8170, 93.9368, "Imphal, India", ["Khwairakpam Chaoba"]),
        new("family-outdoors.jpg", "Family posing outdoors", "https://commons.wikimedia.org/wiki/Special:Redirect/file/Elderly_family_posing_outdoors_(16350013894).jpg?width=1280", "https://commons.wikimedia.org/wiki/File:Elderly_family_posing_outdoors_(16350013894).jpg", "CC BY 2.0", "simpleinsomnia", new DateTimeOffset(2015, 4, 2, 16, 45, 0, TimeSpan.Zero), 47.6062, -122.3321, "Seattle, United States", ["Harness Person A", "Harness Person B"]),
        new("chicago-sunset.jpg", "Chicago skyline at sunset", "https://commons.wikimedia.org/wiki/Special:Redirect/file/Chicago_Skyline_At_Sunset_United_States_Cityscape_Photography_(112263307).jpeg?width=1280", "https://commons.wikimedia.org/wiki/File:Chicago_Skyline_At_Sunset_United_States_Cityscape_Photography_(112263307).jpeg", "CC BY 3.0", "Giuseppe Milo", new DateTimeOffset(2018, 9, 21, 18, 12, 0, TimeSpan.Zero), 41.8781, -87.6298, "Chicago, United States", []),
        new("paris-eiffel.jpg", "Eiffel Tower", "https://commons.wikimedia.org/wiki/Special:Redirect/file/Eiffelturm_Europameisterschaft_(166775095).jpeg?width=1280", "https://commons.wikimedia.org/wiki/File:Eiffelturm_Europameisterschaft_(166775095).jpeg", "CC BY 3.0", "Dronepicr", new DateTimeOffset(2016, 6, 10, 12, 0, 0, TimeSpan.Zero), 48.8584, 2.2945, "Paris, France", []),
        new("tokyo-shibuya.jpg", "Shibuya street", "https://commons.wikimedia.org/wiki/Special:Redirect/file/Shibuya_Tokyo_Color_Street_Photography_(240747567).jpeg?width=1280", "https://commons.wikimedia.org/wiki/File:Shibuya_Tokyo_Color_Street_Photography_(240747567).jpeg", "CC BY 3.0", "Giuseppe Milo", new DateTimeOffset(2019, 11, 3, 20, 5, 0, TimeSpan.Zero), 35.6595, 139.7005, "Tokyo, Japan", []),
        new("sydney-opera-house.jpg", "Sydney Opera House from Harbour Bridge", "https://commons.wikimedia.org/wiki/Special:Redirect/file/MC_Sydney_Opera_House.jpg?width=1280", "https://commons.wikimedia.org/wiki/File:MC_Sydney_Opera_House.jpg", "CC BY 2.5", "Christian Mehlführer", new DateTimeOffset(2004, 10, 27, 9, 20, 0, TimeSpan.Zero), -33.852722, 151.210444, "Sydney, Australia", []),
        new("machu-picchu.jpg", "Machu Picchu in the morning", "https://commons.wikimedia.org/wiki/Special:Redirect/file/Machu_Picchu,_Peru_(4585251287).jpg?width=1280", "https://commons.wikimedia.org/wiki/File:Machu_Picchu,_Peru_(4585251287).jpg", "CC BY-SA 2.0", "Nimmi Solomon", new DateTimeOffset(2010, 4, 15, 7, 40, 0, TimeSpan.Zero), -13.1631, -72.5450, "Machu Picchu, Peru", []),
        new("cape-town-table-mountain.jpg", "Table Mountain above Camps Bay", "https://commons.wikimedia.org/wiki/Special:Redirect/file/Table_Mountain,_Cape_Town_(_1050261).jpg?width=1280", "https://commons.wikimedia.org/wiki/File:Table_Mountain,_Cape_Town_(_1050261).jpg", "CC BY-SA 4.0", "Matti Blume", new DateTimeOffset(2018, 7, 17, 12, 41, 0, TimeSpan.Zero), -33.959099, 18.405302, "Cape Town, South Africa", []),
        new("new-york-skyline-night.jpg", "New York skyline at night", "https://commons.wikimedia.org/wiki/Special:Redirect/file/New_York_skyline_at_night.jpg?width=1280", "https://commons.wikimedia.org/wiki/File:New_York_skyline_at_night.jpg", "CC BY 4.0", "Azeusss", new DateTimeOffset(2024, 5, 31, 21, 45, 0, TimeSpan.Zero), 40.716042, -74.031356, "New York, United States", []),
        new("mexico-city-skyline.jpg", "Mexico City skyline", "https://commons.wikimedia.org/wiki/Special:Redirect/file/Mexico_City_skyline.jpg?width=1280", "https://commons.wikimedia.org/wiki/File:Mexico_City_skyline.jpg", "CC BY-SA 4.0", "Josh Baumgartner", new DateTimeOffset(2017, 5, 16, 16, 23, 0, TimeSpan.Zero), 19.4326, -99.1332, "Mexico City, Mexico", [])
    ];

    public async Task<ViewPhotoHarnessReport> RunAsync(CancellationToken ct)
    {
        var space = await viewStorage.EnsurePersonalSpaceAsync(ProfileId, ct);
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "TuvimaLibraryPhotoHarness/1.0 (development fixture; https://github.com/Tuvima/tuvima-library)");
        var results = new List<ViewPhotoFixtureResult>();
        var density = ViewDensityFixtures.Create();
        var densityByName = density.ToDictionary(sample => sample.FileName);
        var allFixtures = Fixtures.Concat(density.Select(sample => new Fixture(
            sample.FileName, $"[Test] {sample.LocationName} {sample.Number:D3}", "",
            "tuvima:synthetic-places-density-v1", "Synthetic test fixture", "Tuvima Library",
            sample.CapturedAt, sample.Latitude, sample.Longitude, sample.LocationName, []))).ToArray();

        foreach (var fixture in allFixtures)
        {
            try
            {
                var synthetic = densityByName.TryGetValue(fixture.FileName, out var sample);
                var bytes = synthetic ? ViewDensityFixtures.Render(sample!) : await client.GetByteArrayAsync(fixture.DownloadUrl, ct);
                await using var stream = new MemoryStream(bytes, writable: false);
                var upload = await viewLibrary.UploadAsync(ProfileId, $"harness-{fixture.FileName}", stream, ct);
                var location = assets.ResolveContent(upload.ItemId)
                    ?? throw new InvalidOperationException("Uploaded fixture has no resolvable content source.");
                var existing = assets.Find(upload.ItemId)
                    ?? throw new InvalidOperationException("Uploaded fixture was not readable after indexing.");
                await assets.UpsertAsync(new LocalAssetRegistration(
                    space.LibraryId, space.Id, ProfileId, LocalAssetMediaKinds.Image, fixture.Title,
                    fixture.CapturedAt,
                    [new LocalAssetFileRegistration(location.FilePath, location.ContentHash,
                        Path.GetFileName(location.FilePath), location.MimeType, location.ByteSize,
                        File.GetLastWriteTimeUtc(location.FilePath), LocalAssetFileRoles.Primary,
                        SourceId: location.SourceId, DeviceId: location.DeviceId)],
                    existing.Width, existing.Height, DeviceMake: "Tuvima Harness",
                    DeviceModel: "Deterministic Fixture", Latitude: fixture.Latitude,
                    Longitude: fixture.Longitude, LocationName: fixture.LocationName,
                    MetadataJson: JsonSerializer.Serialize(new
                    {
                        fixture.SourcePage,
                        fixture.License,
                        fixture.Author,
                        sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                        metadata_mode = "deterministic-harness"
                    }), ExistingItemId: upload.ItemId), ct);
                // The managed upload folder is watched and can be re-indexed from the file's
                // current timestamp. Preserve the fixture's intentional historical capture date
                // as an override so the sample Places timeline stays deterministic.
                await assets.UpdateCapturedAtAsync(
                    upload.ItemId, fixture.CapturedAt, resetToEmbedded: false, ct);
                await assets.UpdateLocationAsync(upload.ItemId,
                    new LocalAssetLocationUpdate(fixture.Latitude, fixture.Longitude, fixture.LocationName), ct);
                foreach (var person in fixture.People)
                {
                    await assets.AddAnnotationAsync(upload.ItemId,
                        new LocalAssetAnnotation("person_name", person, ProvenanceSource,
                            Confidence: 1, ProvenanceJson: JsonSerializer.Serialize(new
                            { fixture.SourcePage, assertion = "manual test fixture; not face recognition" }),
                            ReviewedAt: DateTimeOffset.UtcNow), ct);
                }

                await assets.ReplaceTagsAsync(upload.ItemId,
                    ["view-photo-harness", synthetic ? "synthetic-density-fixture" : "free-stock", fixture.LocationName], ct);
                results.Add(new ViewPhotoFixtureResult(fixture.FileName, upload.ItemId, "passed", null,
                    fixture.SourcePage, fixture.License, fixture.Author));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "View photo harness fixture {Fixture} failed", fixture.FileName);
                results.Add(new ViewPhotoFixtureResult(fixture.FileName, null, "failed", ex.Message,
                    fixture.SourcePage, fixture.License, fixture.Author));
            }
        }

        // Adding/removing is tested through reversible product lifecycle state.
        var lifecyclePassed = false;
        if (results.FirstOrDefault(result => result.ItemId.HasValue)?.ItemId is { } lifecycleId)
        {
            await assets.SetLifecycleStateAsync(lifecycleId, LocalAssetLifecycleState.Trashed, ct);
            var trashed = assets.Find(lifecycleId)?.TrashedAt is not null;
            await assets.SetLifecycleStateAsync(lifecycleId, LocalAssetLifecycleState.Active, ct);
            lifecyclePassed = trashed && assets.Find(lifecycleId)?.TrashedAt is null;
        }

        var places = discovery.QueryPlaces(new ViewPlaceDiscoveryQuery([space.LibraryId], 100), ct);
        var people = discovery.QueryPeople(new ViewPeopleDiscoveryQuery([space.LibraryId], 100), ct);
        var passed = results.All(result => result.Status == "passed")
                     && results.Select(result => result.ItemId).Distinct().Count() == allFixtures.Length
                     && lifecyclePassed && places.Items.Count >= Fixtures.Select(fixture => fixture.LocationName).Distinct().Count()
                     && density.GroupBy(sample => sample.LocationName).All(group =>
                         places.Items.Where(place => place.Name == group.Key).Sum(place => place.AssetCount) >= group.Count())
                     && people.Items.Count >= Fixtures.Sum(fixture => fixture.People.Length);
        return new ViewPhotoHarnessReport(passed, ProfileId, space.LibraryId, results,
            places.Items.Count, people.Items.Count, lifecyclePassed,
            "People are reviewed fixture annotations; automatic face recognition is not asserted.");
    }

    private sealed record Fixture(string FileName, string Title, string DownloadUrl,
        string SourcePage, string License, string Author, DateTimeOffset CapturedAt,
        double Latitude, double Longitude, string LocationName, string[] People);
}

public sealed record ViewPhotoFixtureResult(string FileName, Guid? ItemId, string Status,
    string? Error, string SourcePage, string License, string Author);

public sealed record ViewPhotoHarnessReport(bool Passed, Guid ProfileId, Guid LibraryId,
    IReadOnlyList<ViewPhotoFixtureResult> Fixtures, int PlaceGroups, int PeopleGroups,
    bool TrashRestorePassed, string PeopleEvidenceNote);
