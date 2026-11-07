namespace MediaEngine.Api.Tests;

/// <summary>
/// Every write action that could change a Series' fields, match or picture asks the Series guard (or the collection type) first and
/// answers with the plain reason (universal editor v5 D7, D24). These read the route sources, so a new write route that forgets the
/// check shows up here.
/// </summary>
public sealed class SeriesReadOnlyEndpointTests
{
    private static string Route(string file, string marker)
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Api/Endpoints", file));
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{marker} not found in {file}");
        var next = source.IndexOf("group.Map", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    [Theory]
    [InlineData("ItemCanonicalEndpoints.cs", "group.MapPut(\"/{entityId:guid}/display-overrides\"", "IsSeriesAsync(entityId, ct)", "SeriesIsReadOnly")]
    [InlineData("MetadataEndpoints.cs", "group.MapPut(\"/{entityId:guid}/override\"", "IsSeriesAsync(entityId, ct)", "SeriesIsReadOnly")]
    [InlineData("MetadataEndpoints.cs", "group.MapPost(\"/{entityId:guid}/cover\"", "IsSeriesAsync(entityId, ct)", "SeriesHasNoArtwork")]
    [InlineData("MetadataEndpoints.cs", "group.MapPost(\"/{entityId:guid}/artwork/{assetType}\"", "IsSeriesAsync(entityId, ct)", "SeriesHasNoArtwork")]
    [InlineData("CollectionEndpoints.cs", "group.MapPost(\"/{id:guid}/artwork/{slot}\"", "CollectionType.Series or CollectionType.ContentGroup", "SeriesHasNoArtwork")]
    [InlineData("CollectionEndpoints.cs", "group.MapDelete(\"/{id:guid}/artwork/{slot}\"", "CollectionType.Series or CollectionType.ContentGroup", "SeriesHasNoArtwork")]
    [InlineData("CollectionEndpoints.cs", "group.MapPut(\"/{id:guid}/display-overrides\"", "CollectionType.Series or CollectionType.ContentGroup", "SeriesIsReadOnly")]
    public void AWriteRoute_RefusesASeries_WithThePlainReason(string file, string marker, string check, string reason)
    {
        var route = Route(file, marker);

        Assert.Contains(check, route, StringComparison.Ordinal);
        Assert.Contains(reason, route, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
