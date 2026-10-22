using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Tests;

public sealed class ArtworkVersionMarkerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tuvima-marker-" + Guid.NewGuid().ToString("N"));

    public ArtworkVersionMarkerTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ForFile_ReturnsTheSameMarkerForTheSameUntouchedFile()
    {
        var path = WritePicture("cover.jpg", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(ArtworkVersionMarker.ForFile(path), ArtworkVersionMarker.ForFile(path));
        Assert.False(string.IsNullOrWhiteSpace(ArtworkVersionMarker.ForFile(path)));
    }

    [Fact]
    public void ForFile_ChangesWhenTheFileIsRewrittenWithADifferentLength()
    {
        var modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var path = WritePicture("cover.jpg", 100, modified);
        var before = ArtworkVersionMarker.ForFile(path);

        WritePicture("cover.jpg", 101, modified);

        Assert.NotEqual(before, ArtworkVersionMarker.ForFile(path));
    }

    [Fact]
    public void ForFile_ChangesWhenTheFileIsRewrittenAtALaterTimeWithTheSameLength()
    {
        var path = WritePicture("cover.jpg", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = ArtworkVersionMarker.ForFile(path);

        WritePicture("cover.jpg", 100, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.NotEqual(before, ArtworkVersionMarker.ForFile(path));
    }

    [Fact]
    public void ForFile_IsNullWhenTheFileIsMissingOrNoPathIsGiven()
    {
        Assert.Null(ArtworkVersionMarker.ForFile(Path.Combine(_directory, "missing.jpg")));
        Assert.Null(ArtworkVersionMarker.ForFile(null));
        Assert.Null(ArtworkVersionMarker.ForFile("  "));
    }

    [Fact]
    public void WithMarker_ReplacesAnExistingMarkerAndKeepsTheSizeAndFragment()
    {
        var result = ArtworkVersionMarker.WithMarker("/stream/artwork/abc?size=m&v=old#top", "new1");

        Assert.Equal("/stream/artwork/abc?size=m&v=new1#top", result);
    }

    [Fact]
    public void WithMarker_AddsAMarkerToAnAddressWithoutAQuery()
    {
        Assert.Equal("/persons/p/headshot?v=abc", ArtworkVersionMarker.WithMarker("/persons/p/headshot", "abc"));
    }

    [Fact]
    public void WithMarker_WithoutAMarkerDropsTheOldOneAndKeepsTheRest()
    {
        Assert.Equal("/stream/artwork/abc?size=s", ArtworkVersionMarker.WithMarker("/stream/artwork/abc?size=s&v=old", null));
    }

    [Fact]
    public void StoredArtworkUrl_ChangesWhenTheOriginalFileIsRewritten()
    {
        var path = WritePicture("original.jpg", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var asset = new EntityAsset { Id = Guid.NewGuid(), LocalImagePath = path };
        var before = ArtworkCanonicalHelper.StoredArtworkUrl(asset, "s");

        WritePicture("original.jpg", 250, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));
        var after = ArtworkCanonicalHelper.StoredArtworkUrl(asset, "s");

        Assert.NotEqual(before, after);
        Assert.StartsWith($"/stream/artwork/{asset.Id}?size=s&v=", before, StringComparison.Ordinal);
        Assert.StartsWith($"/stream/artwork/{asset.Id}?size=s&v=", after, StringComparison.Ordinal);
    }

    [Fact]
    public void StoredArtworkUrl_KeepsTheSameAddressWhileTheFileIsUntouched()
    {
        var path = WritePicture("original.jpg", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var asset = new EntityAsset { Id = Guid.NewGuid(), LocalImagePath = path };

        Assert.Equal(
            ArtworkCanonicalHelper.StoredArtworkUrl(asset, "m"),
            ArtworkCanonicalHelper.StoredArtworkUrl(asset, "m"));
    }

    private string WritePicture(string name, int length, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, new byte[length]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
