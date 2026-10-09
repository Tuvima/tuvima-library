namespace MediaEngine.Web.Tests;

public sealed class LargeListVirtualizationTests
{
    [Fact]
    public void ListenSongTable_UsesVirtualizeForLargeTrackLists()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Listen/ListenSongTable.razor"));

        Assert.Contains("<Virtualize Items=\"@DisplayTracks\"", source, StringComparison.Ordinal);
        Assert.Contains("@key=\"track.Id\"", source, StringComparison.Ordinal);
        Assert.Contains("role=\"separator\"", source, StringComparison.Ordinal);
        Assert.Contains("aria-orientation=\"vertical\"", source, StringComparison.Ordinal);
        Assert.Contains("ResizeColumnByKeyboard", source, StringComparison.Ordinal);
        Assert.Contains("createdAt == default ? \"—\"", source, StringComparison.Ordinal);
        Assert.Contains("GetPlayCount(work).ToString(CultureInfo.InvariantCulture) : string.Empty", source, StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
