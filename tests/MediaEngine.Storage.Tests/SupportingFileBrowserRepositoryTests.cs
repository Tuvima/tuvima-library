using Dapper;
using MediaEngine.Domain.Models;

namespace MediaEngine.Storage.Tests;

/// <summary>Supporting files (universal editor v5 WP4): search, removing many at once, and following a file that was moved.</summary>
public sealed class SupportingFileBrowserRepositoryTests : IDisposable
{
    private const string Show = "/library/tv/The Last Signal";
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ItemExtraRepository _extras;
    private readonly List<ItemExtra> _found = [];

    public SupportingFileBrowserRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_supporting_browser_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _extras = new ItemExtraRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task Search_FindsAFileByAWordInItsDescription_AndByAWordInItsFileName_IgnoringCase()
    {
        await Seed("character-guide.pdf", "Character guide");
        await Seed("episode-notes.pdf", "Episode notes");
        var notes = (await _extras.GetByPathAsync($"{Show}/episode-notes.pdf"))!;
        await _extras.UpdateOwnedAsync(notes.Id, new ExtraOwnedUpdate { Description = ExtraValue<string>.Set("Lighthouse keeper timeline") });

        var byDescription = await _extras.SearchAsync(["LIGHTHOUSE"], 20);
        var byFileName = await _extras.SearchAsync(["character-guide"], 20);

        Assert.Equal(notes.Id, Assert.Single(byDescription).Id);
        Assert.Equal("Character guide", Assert.Single(byFileName).DisplayTitle);
    }

    [Fact]
    public async Task Search_DoesNotMatchTheFolderNamesAboveAFile()
    {
        await Seed("guide.pdf", "Guide");

        var found = await _extras.SearchAsync(["Signal"], 20);

        Assert.Empty(found);
    }

    [Fact]
    public async Task Search_NeedsEveryWord_AndTreatsPercentAndUnderscoreAsPlainText()
    {
        await Seed("a.pdf", "Season notes");
        await Seed("b.pdf", "Season 100% done");

        Assert.Equal(2, (await _extras.SearchAsync(["season"], 20)).Count);
        Assert.Single(await _extras.SearchAsync(["season", "notes"], 20));
        Assert.Equal("Season 100% done", Assert.Single(await _extras.SearchAsync(["100%"], 20)).DisplayTitle);
        Assert.Empty(await _extras.SearchAsync(["s_ason"], 20));
    }

    [Fact]
    public async Task Search_StopsAtTheLimit()
    {
        for (var index = 0; index < 5; index++)
        {
            await Seed($"worksheet-{index}.pdf", $"Worksheet {index}");
        }

        Assert.Equal(3, (await _extras.SearchAsync(["worksheet"], 3)).Count);
    }

    [Fact]
    public async Task RemoveMany_RemovesTheNamedFiles_AndTheirTags_AndSkipsIdsThatAreNotThere()
    {
        await Seed("a.pdf", "A");
        await Seed("b.pdf", "B");
        await Seed("c.pdf", "C");
        var a = (await _extras.GetByPathAsync($"{Show}/a.pdf"))!;
        var b = (await _extras.GetByPathAsync($"{Show}/b.pdf"))!;
        await _extras.UpdateOwnedAsync(a.Id, new ExtraOwnedUpdate { AddTags = ["maths"] });

        var removed = await _extras.RemoveManyAsync([a.Id, b.Id, Guid.NewGuid()]);

        Assert.Equal(2, removed);
        Assert.Equal(["c"], (await _extras.ListAllInItemFolderAsync(Show)).Select(row => row.FilePath[(Show.Length + 1)..^4]));
        using var connection = _db.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM item_extra_tag_links;"));
    }

    [Fact]
    public async Task RelocateFile_PointsTheRowAtTheNewPlace_AndKeepsEverythingAPersonSet()
    {
        await Seed("guide.pdf", "Guide");
        var row = (await _extras.GetByPathAsync($"{Show}/guide.pdf"))!;
        await _extras.UpdateOwnedAsync(row.Id, new ExtraOwnedUpdate
        {
            Title = ExtraValue<string>.Set("Season guide"),
            GroupName = ExtraValue<string>.Set("Homework"),
            Scope = ExtraValue<ExtraScope>.Set(ExtraScope.ForSeason(2)),
        });

        var moved = await _extras.RelocateFileAsync(row.Id, $"{Show}/Season 02/guide.pdf");

        Assert.True(moved);
        Assert.Null(await _extras.GetByPathAsync($"{Show}/guide.pdf"));
        var after = (await _extras.GetByPathAsync($"{Show}/Season 02/guide.pdf"))!;
        Assert.Equal(row.Id, after.Id);
        Assert.Equal("Season guide", after.DisplayTitle);
        Assert.Equal("Homework", after.GroupName);
        Assert.Equal(ExtraScope.ForSeason(2), after.EffectiveScope);
        Assert.False(await _extras.RelocateFileAsync(Guid.NewGuid(), $"{Show}/elsewhere.pdf"));
    }

    // Every pass lists all the files found so far, as a rescan of the folder would.
    private Task Seed(string fileName, string title)
    {
        _found.Add(New(fileName, title));
        return _extras.SyncItemFolderAsync(Show, _found, []);
    }

    private static ItemExtra New(string fileName, string title) => new(
        Guid.NewGuid(), "lib", Show, $"{Show}/{fileName}", ItemExtraKinds.Document, ItemExtraOrigins.Rule,
        title, null, 0, 10, null, null, DateTimeOffset.UtcNow,
        InferredRole: ExtraRoles.Other,
        InferredScope: ExtraScope.Root);
}
