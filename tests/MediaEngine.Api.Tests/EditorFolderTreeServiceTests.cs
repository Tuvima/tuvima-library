using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>The folder button's tree (universal editor v5, WP3, section 5): every file of the title's folder marked by what Library makes of it.</summary>
public sealed class EditorFolderTreeServiceTests : IDisposable
{
    private readonly EditorHeaderTestWorld _world = new("The Expanse");

    [Fact]
    public async Task ANfoFile_IsMarkedHidden_AndTheTitlesOwnFilesAreMarkedMainAndSupporting()
    {
        var episode = SeedSeasonTwo();
        _world.Write("Season 02/The Expanse - S02E01.mkv", "video");
        _world.Write("Season 02/The Expanse - S02E01.nfo", "<episodedetails/>");
        _world.Write("Season 02/guide.pdf", "pdf");
        _world.Write("Season 02/mystery.bin", "?");
        var guide = await _world.SupportingFileAsync("Season 02/guide.pdf", ExtraScope.ForSeason(2));

        var tree = await _world.FolderTree().ReadAsync(episode, "Season 02", 0);

        Assert.NotNull(tree);
        Assert.Equal("The Expanse", tree.RootName);
        Assert.Equal("Season 02", tree.Folder);
        Assert.Equal(string.Empty, tree.ParentFolder);
        Assert.False(tree.ReadOnly);
        Assert.Equal(4, tree.Total);
        var video = Assert.Single(tree.Entries, entry => entry.Name.EndsWith(".mkv", StringComparison.Ordinal));
        Assert.Equal(EditorFolderEntryStates.Main, video.State);
        Assert.Equal(episode, video.ItemId);
        Assert.Equal("Season 02/The Expanse - S02E01.mkv", video.Path);
        var nfo = Assert.Single(tree.Entries, entry => entry.Name.EndsWith(".nfo", StringComparison.Ordinal));
        Assert.Equal(EditorFolderEntryStates.Hidden, nfo.State);
        Assert.Null(nfo.ItemId);
        var pdf = Assert.Single(tree.Entries, entry => entry.Name == "guide.pdf");
        Assert.Equal(EditorFolderEntryStates.Supporting, pdf.State);
        Assert.Equal(guide, pdf.SupportingFileId);
        var unknown = Assert.Single(tree.Entries, entry => entry.Name == "mystery.bin");
        Assert.Equal(EditorFolderEntryStates.Hidden, unknown.State);
    }

    [Fact]
    public async Task APdfNobodyHasSortedYet_IsUnsorted()
    {
        var episode = SeedSeasonTwo();
        _world.Write("Season 02/loose.pdf", "pdf");

        var tree = await _world.FolderTree().ReadAsync(episode, "Season 02", 0);

        Assert.Equal(EditorFolderEntryStates.Unsorted, Assert.Single(tree!.Entries, entry => entry.Name == "loose.pdf").State);
    }

    [Fact]
    public async Task TheTitlesFolder_ListsFoldersFirst_ThenFiles_InNaturalOrder()
    {
        var episode = SeedSeasonTwo();
        _world.Write("Season 2/a.mkv");
        _world.Write("Season 10/a.mkv");
        _world.Write("Season 9/a.mkv");
        _world.Write("Episode 2.mkv");
        _world.Write("Episode 10.mkv");
        _world.Write("Episode 1.mkv");

        var tree = await _world.FolderTree().ReadAsync(episode, null, 0);

        Assert.Equal(string.Empty, tree!.Folder);
        Assert.Null(tree.ParentFolder);
        Assert.Equal(
            ["Season 2", "Season 9", "Season 10", "Episode 1.mkv", "Episode 2.mkv", "Episode 10.mkv"],
            tree.Entries.Select(entry => entry.Name));
        Assert.All(tree.Entries.Take(3), entry => Assert.True(entry.IsFolder));
        Assert.All(tree.Entries.Take(3), entry => Assert.Null(entry.State));
    }

    [Fact]
    public async Task AFolderWithMoreFilesThanAPage_IsPagedHundredAtATime()
    {
        var episode = SeedSeasonTwo();
        for (var number = 0; number < 130; number++)
        {
            _world.Write($"Season 02/file-{number:000}.mkv");
        }

        var service = _world.FolderTree();
        var first = await service.ReadAsync(episode, "Season 02", 0);
        var second = await service.ReadAsync(episode, "Season 02", 100);

        Assert.Equal(130, first!.Total);
        Assert.Equal(100, first.Entries.Count);
        Assert.True(first.HasMore);
        Assert.Equal(30, second!.Entries.Count);
        Assert.False(second.HasMore);
        Assert.Equal(100, second.Offset);
        Assert.Equal("file-100.mkv", second.Entries[0].Name);
    }

    [Fact]
    public async Task ASubFolder_KnowsItsParent()
    {
        var episode = SeedSeasonTwo();
        _world.Write("Season 02/Extras/trailer.mkv");

        var nested = await _world.FolderTree().ReadAsync(episode, "Season 02/Extras", 0);

        Assert.Equal("Season 02/Extras", nested!.Folder);
        Assert.Equal("Season 02", nested.ParentFolder);
        Assert.Equal("Season 02/Extras/trailer.mkv", Assert.Single(nested.Entries).Path);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("Season 02/../../elsewhere")]
    [InlineData("/etc")]
    [InlineData("C:/Windows")]
    [InlineData("Season 02/./..")]
    public async Task AFolderOutsideTheTitle_IsNeverRead(string folder)
    {
        var episode = SeedSeasonTwo();
        Directory.CreateDirectory(Path.Combine(_world.Library, "elsewhere"));

        Assert.Null(await _world.FolderTree().ReadAsync(episode, folder, 0));
    }

    [Fact]
    public async Task AShortcutToAFolderOutsideTheTitle_IsNotFollowedOrListed()
    {
        var episode = SeedSeasonTwo();
        _world.Write("Season 02/The Expanse - S02E01.mkv", "video");
        var elsewhere = Path.Combine(_world.Library, "Another title");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "secret.mkv"), "someone else's video");
        if (!TryLinkDirectory(Path.Combine(_world.TitleFolder, "Shortcut"), elsewhere))
        {
            return;
        }

        var tree = await _world.FolderTree().ReadAsync(episode, null, 0);
        var through = await _world.FolderTree().ReadAsync(episode, "Shortcut", 0);

        Assert.DoesNotContain(tree!.Entries, entry => entry.Name == "Shortcut");
        Assert.Null(through);
    }

    [Fact]
    public async Task AShortcutToAFileOutsideTheTitle_AndABrokenShortcut_AreLeftOutWithoutAnError()
    {
        var episode = SeedSeasonTwo();
        _world.Write("Season 02/The Expanse - S02E01.mkv", "video");
        var outside = Path.Combine(_world.Library, "outside.mkv");
        File.WriteAllText(outside, "x");
        var linkedFile = Path.Combine(_world.TitleFolder, "Season 02", "outside-link.mkv");
        var brokenLink = Path.Combine(_world.TitleFolder, "Season 02", "broken-link.mkv");
        try
        {
            File.CreateSymbolicLink(linkedFile, outside);
            File.CreateSymbolicLink(brokenLink, Path.Combine(_world.Library, "missing.mkv"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var tree = await _world.FolderTree().ReadAsync(episode, "Season 02", 0);

        Assert.Equal(["The Expanse - S02E01.mkv"], tree!.Entries.Select(entry => entry.Name));
        Assert.Equal(1, tree.Total);
    }

    [Fact]
    public async Task AShortcutToAFolderInsideTheTitle_IsStillListed()
    {
        var episode = SeedSeasonTwo();
        _world.Write("Season 02/The Expanse - S02E01.mkv", "video");
        if (!TryLinkDirectory(Path.Combine(_world.TitleFolder, "Season Two"), Path.Combine(_world.TitleFolder, "Season 02")))
        {
            return;
        }

        var tree = await _world.FolderTree().ReadAsync(episode, null, 0);

        Assert.Contains(tree!.Entries, entry => entry.Name == "Season Two" && entry.IsFolder);
    }

    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // This machine does not allow shortcuts to be made (Windows without developer mode); there is nothing to prove here.
            return false;
        }
    }

    [Fact]
    public async Task AFolderThatDoesNotExist_IsNotFound()
    {
        var episode = SeedSeasonTwo();

        Assert.Null(await _world.FolderTree().ReadAsync(episode, "Season 99", 0));
    }

    [Fact]
    public async Task AFolderInAReadOnlyLibrary_IsMarkedReadOnly()
    {
        using var readOnly = new EditorHeaderTestWorld("Arrival", LibrarySourceAccessModes.ReadOnly);
        var movie = readOnly.Work("Movies", "standalone", "Arrival");
        readOnly.File(movie, "Arrival (2016).mkv");
        readOnly.Write("Arrival (2016).mkv");

        var tree = await readOnly.FolderTree().ReadAsync(movie, null, 0);

        Assert.True(tree!.ReadOnly);
        Assert.Equal(EditorFolderEntryStates.Main, Assert.Single(tree.Entries).State);
    }

    private Guid SeedSeasonTwo()
    {
        var show = _world.Work("TV", "parent", "The Expanse");
        var season = _world.Work("TV", "parent", "Season 2", show, 2);
        var episode = _world.Work("TV", "child", "Safe", season, 1);
        _world.File(episode, "Season 02/The Expanse - S02E01.mkv");
        return episode;
    }

    public void Dispose() => _world.Dispose();
}
