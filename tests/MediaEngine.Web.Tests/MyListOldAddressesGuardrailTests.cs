namespace MediaEngine.Web.Tests;

/// <summary>
/// My List spec D13: My Library and My List became one page at /my-list, and the old addresses are removed, not redirected
/// (Look v3 D17, no forwarding before release). Nothing in the Dashboard may route to or link to them.
/// </summary>
public sealed class MyListOldAddressesGuardrailTests
{
    private static readonly string[] RemovedAddresses = ["/my-library", "view=my-list", "/all/personal/my-list"];

    [Fact]
    public void NoDashboardSourceNamesAnOldMyLibraryOrMyListAddress()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var sep = Path.DirectorySeparatorChar;
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(web, "*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".razor", StringComparison.Ordinal)
                                    || path.EndsWith(".cs", StringComparison.Ordinal)
                                    || path.EndsWith(".js", StringComparison.Ordinal)
                                    || path.EndsWith(".css", StringComparison.Ordinal)
                                    || path.EndsWith(".resx", StringComparison.Ordinal))
                     .Where(path => !path.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
                                    && !path.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
                                    && !path.Contains($"{sep}lib{sep}", StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(path);
            offenders.AddRange(RemovedAddresses
                .Where(address => source.Contains(address, StringComparison.OrdinalIgnoreCase))
                .Select(address => $"{Path.GetRelativePath(web, path).Replace(sep, '/')}: {address}"));
        }

        Assert.True(offenders.Count == 0, "The old My Library and My List addresses are removed (D13): " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheOldPersonalPageFilesAreGone()
    {
        var pages = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Components", "Pages");
        Assert.False(File.Exists(Path.Combine(pages, "MyLibrary.razor")));
        Assert.False(File.Exists(Path.Combine(pages, "MyLibrary.razor.css")));
        Assert.True(File.Exists(Path.Combine(pages, "MyListPage.razor")));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
