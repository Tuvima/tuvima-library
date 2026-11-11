namespace MediaEngine.Web.Tests;

public sealed class LargeListVirtualizationTests
{
    [Fact]
    public void SongTable_UsesVirtualizeForLargeTrackLists()
    {
        var markup = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Listen/SongTable.razor"));
        var code = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Listen/SongTable.razor.cs"));

        Assert.Contains("<Virtualize Items=\"@_shown\"", markup, StringComparison.Ordinal);
        Assert.Contains("<Virtualize Items=\"@PhoneItems\"", markup, StringComparison.Ordinal);
        Assert.Contains("@key=\"RowKey(row)\"", markup, StringComparison.Ordinal);
        Assert.Contains("role=\"separator\"", markup, StringComparison.Ordinal);
        Assert.Contains("aria-orientation=\"vertical\"", markup, StringComparison.Ordinal);
        Assert.Contains("ResizeByKeyboardAsync", code, StringComparison.Ordinal);
        Assert.Contains("added != default ? added.LocalDateTime.ToString(\"d\", CultureInfo.CurrentCulture) : \"—\"", code, StringComparison.Ordinal);
        Assert.Contains("row.Plays.ToString(CultureInfo.InvariantCulture) : string.Empty", markup, StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
