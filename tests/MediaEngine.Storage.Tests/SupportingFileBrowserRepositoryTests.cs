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

    [Fact]
    public async Task Search_IgnoresCaseForEveryLetter_NotJustAToZ()
    {
        await Seed("École-plan.pdf", "École guide");
        await Seed("other.pdf", "Straße map");

        Assert.Equal("École guide", Assert.Single(await _extras.SearchAsync(["école"], 20)).DisplayTitle);
        Assert.Equal("École guide", Assert.Single(await _extras.SearchAsync(["ÉCOLE"], 20)).DisplayTitle);
        Assert.Equal("École guide", Assert.Single(await _extras.SearchAsync(["éCOLE-plan"], 20)).DisplayTitle);
        Assert.Equal("Straße map", Assert.Single(await _extras.SearchAsync(["MAP"], 20)).DisplayTitle);
    }

    [Fact]
    public async Task Search_ReadsPageAfterPage_InTheSameOrder_ThroughTheOffset()
    {
        for (var index = 0; index < 7; index++)
        {
            await Seed($"worksheet-{index}.pdf", $"Worksheet {index}");
        }

        var first = await _extras.SearchAsync(["worksheet"], 3);
        var second = await _extras.SearchAsync(["worksheet"], 3, offset: 3);
        var third = await _extras.SearchAsync(["worksheet"], 3, offset: 6);
        var past = await _extras.SearchAsync(["worksheet"], 3, offset: 9);

        Assert.Equal(
            Enumerable.Range(0, 7).Select(index => $"Worksheet {index}"),
            first.Concat(second).Concat(third).Select(row => row.DisplayTitle));
        Assert.Single(third);
        Assert.Empty(past);
    }

    [Fact]
    public async Task RelocateFile_ToANewPlace_MarksTheRowAsPlacedByAPerson_AndRecordsWhereItNowSits()
    {
        await Seed("guide.pdf", "Guide");
        var row = (await _extras.GetByPathAsync($"{Show}/guide.pdf"))!;

        await _extras.RelocateFileAsync(row.Id, $"{Show}/Season 02/guide.pdf", ExtraScope.ForSeason(2));

        var after = (await _extras.GetByPathAsync($"{Show}/Season 02/guide.pdf"))!;
        Assert.Equal(ItemExtraOrigins.UserExtra, after.Origin);
        Assert.Equal(ExtraScope.ForSeason(2), after.InferredScope);
        Assert.Null(after.Scope);
    }

    [Fact]
    public async Task Restore_PutsBackTheFieldsTagsAndPathAChangeTouched()
    {
        await Seed("guide.pdf", "Guide");
        var before = (await _extras.GetByPathAsync($"{Show}/guide.pdf"))!;
        await _extras.UpdateOwnedAsync(before.Id, new ExtraOwnedUpdate { AddTags = ["maths", "old"] });
        before = (await _extras.GetByPathAsync($"{Show}/guide.pdf"))!;

        await _extras.UpdateOwnedAsync(before.Id, new ExtraOwnedUpdate
        {
            GroupName = ExtraValue<string>.Set("Homework"),
            Scope = ExtraValue<ExtraScope>.Set(ExtraScope.ForSeason(2)),
            AddTags = ["new"],
            RemoveTags = ["old"],
        });
        await _extras.RelocateFileAsync(before.Id, $"{Show}/Season 02/guide.pdf", ExtraScope.ForSeason(2));

        var restored = await _extras.RestoreAsync([before]);

        Assert.Equal(1, restored);
        Assert.Null(await _extras.GetByPathAsync($"{Show}/Season 02/guide.pdf"));
        var after = (await _extras.GetByPathAsync($"{Show}/guide.pdf"))!;
        Assert.Equal(ItemExtraOrigins.Rule, after.Origin);
        Assert.Null(after.GroupName);
        Assert.Null(after.Scope);
        Assert.Equal(ExtraScope.Root, after.InferredScope);
        Assert.Equal(["maths", "old"], after.TagList.Order());
    }

    [Fact]
    public async Task ListFilesUnderFolders_ReturnsEveryMediaFileBelowEachFolder_InOneCall_IgnoringCaseAndSlashDirection()
    {
        InsertAsset("/library/tv/The Last Signal/Season 01/E01.mkv");
        InsertAsset("/library/tv/The Last Signal/Pilot.mkv");
        InsertAsset("/library/tv/Other Show/E01.mkv");
        InsertAsset("/library/tv/The Last Signal Extras/Bonus.mkv");

        var files = await new MediaAssetRepository(_db).ListFilesUnderFoldersAsync(
            ["/LIBRARY/tv/the last signal", "\\library\\tv\\Other Show"]);

        Assert.Equal(
            ["/library/tv/Other Show/E01.mkv", "/library/tv/The Last Signal/Pilot.mkv", "/library/tv/The Last Signal/Season 01/E01.mkv"],
            files.Select(file => file.FilePath).Order(StringComparer.Ordinal));
        Assert.Empty(await new MediaAssetRepository(_db).ListFilesUnderFoldersAsync([]));
    }

    private void InsertAsset(string path)
    {
        using var connection = _db.CreateConnection();
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        connection.Execute("INSERT INTO works (id, media_type) VALUES (@work, 'TV');", new { work = GuidSql.ToBlob(work) });
        connection.Execute("INSERT INTO editions (id, work_id) VALUES (@edition, @work);", new { edition = GuidSql.ToBlob(edition), work = GuidSql.ToBlob(work) });
        var asset = Guid.NewGuid();
        connection.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@asset, @edition, @hash, @path);",
            new { asset = GuidSql.ToBlob(asset), edition = GuidSql.ToBlob(edition), hash = $"hash-{asset:N}", path });
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
