using MediaEngine.Api.Endpoints;
using MediaEngine.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Picture addresses the Engine hands out carry a change marker, and a versioned address served with
/// the exact copy is kept by the browser (the "second load sends no picture requests" half that the
/// Engine owns; the Dashboard relay test covers the other half).
/// </summary>
public sealed class ArtworkChangeMarkerAddressTests : IDisposable
{
    private static readonly Guid PersonId = Guid.Parse("50000000-0000-0000-0000-000000000005");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tuvima-marker-api-" + Guid.NewGuid().ToString("N"));

    public ArtworkChangeMarkerAddressTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void PersonHeadshotUrl_CarriesAMarkerThatChangesWhenThePhotoIsReplaced()
    {
        var path = WriteFile("headshot.jpg", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = ApiImageUrls.BuildPersonHeadshotUrl(PersonId, path, null);

        WriteFile("headshot.jpg", 140, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var after = ApiImageUrls.BuildPersonHeadshotUrl(PersonId, path, null);

        Assert.StartsWith($"/persons/{PersonId}/headshot?v=", before, StringComparison.Ordinal);
        Assert.StartsWith($"/persons/{PersonId}/headshot?v=", after, StringComparison.Ordinal);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void PersonHeadshotUrl_IsStableWhileThePhotoIsUntouched()
    {
        var path = WriteFile("headshot.jpg", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            ApiImageUrls.BuildPersonHeadshotUrl(PersonId, path, null),
            ApiImageUrls.BuildPersonHeadshotUrl(PersonId, path, null));
    }

    [Fact]
    public void PersonHeadshotUrl_HasNoMarkerWhenTheLocalFileIsMissing()
    {
        var url = ApiImageUrls.BuildPersonHeadshotUrl(PersonId, Path.Combine(_directory, "gone.jpg"), "https://example.test/face.jpg");

        Assert.Equal($"/persons/{PersonId}/headshot", url);
    }

    [Fact]
    public void PersonHeadshotUrl_IsNullWhenThereIsNoPictureAtAll()
    {
        Assert.Null(ApiImageUrls.BuildPersonHeadshotUrl(PersonId, null, null));
    }

    [Fact]
    public void PersonHeadshotUrl_KeepsTheSizeBesideTheMarker()
    {
        var path = WriteFile("headshot.jpg", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var url = ApiImageUrls.BuildPersonHeadshotUrl(PersonId, path, null, "s");

        Assert.StartsWith($"/persons/{PersonId}/headshot?size=s&v=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void CharacterPortraitUrl_CarriesTheMarkerOfTheLocalPortrait()
    {
        var portraitId = Guid.Parse("60000000-0000-0000-0000-000000000006");
        var path = WriteFile("portrait.jpg", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var url = ApiImageUrls.BuildCharacterPortraitUrl(portraitId, path, null);

        Assert.StartsWith($"/library/portraits/{portraitId}?v=", url, StringComparison.Ordinal);
        Assert.Equal($"/library/portraits/{portraitId}", ApiImageUrls.BuildCharacterPortraitUrl(portraitId, null, "https://example.test/p.jpg"));
    }

    [Fact]
    public async Task VersionedAddress_ServedWithTheExactCopy_IsKeptForAYearByTheBrowser()
    {
        var path = WriteJpeg("cover.jpg");
        var marker = ArtworkVersionMarker.ForFile(path);
        var context = NewContext($"?size=s&v={marker}");

        var mode = ArtworkFileResults.ModeFor(context.Request, marker, servedRequestedSize: true);
        await ArtworkFileResults.Local(path, mode)!.ExecuteAsync(context);

        Assert.Equal(ArtworkCacheMode.Immutable, mode);
        Assert.Equal(ArtworkFileResults.ImmutableCacheControl, context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task AddressWithAnOldMarker_AfterThePictureWasEdited_IsCheckedAgainNotKept()
    {
        var path = WriteJpeg("cover.jpg");
        var oldMarker = ArtworkVersionMarker.ForFile(path);
        File.AppendAllText(path, "edited");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        var currentMarker = ArtworkVersionMarker.ForFile(path);
        var context = NewContext($"?size=s&v={oldMarker}");

        var mode = ArtworkFileResults.ModeFor(context.Request, currentMarker, servedRequestedSize: true);
        await ArtworkFileResults.Local(path, mode)!.ExecuteAsync(context);

        Assert.NotEqual(oldMarker, currentMarker);
        Assert.Equal(ArtworkFileResults.RevalidateCacheControl, context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task AddressWithoutAMarker_IsServedCheckedOnEveryUse()
    {
        var path = WriteJpeg("cover.jpg");
        var context = NewContext("?size=s");

        var mode = ArtworkFileResults.ModeFor(context.Request, ArtworkVersionMarker.ForFile(path), servedRequestedSize: true);
        await ArtworkFileResults.Local(path, mode)!.ExecuteAsync(context);

        Assert.Equal(ArtworkFileResults.RevalidateCacheControl, context.Response.Headers.CacheControl.ToString());
        Assert.True(context.Response.Headers.ContainsKey("ETag"));
    }

    private static DefaultHttpContext NewContext(string query)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private string WriteJpeg(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0]);
        return path;
    }

    private string WriteFile(string name, int length, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, new byte[length]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
