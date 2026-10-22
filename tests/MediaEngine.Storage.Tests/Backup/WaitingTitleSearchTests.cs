using Dapper;
using MediaEngine.Storage.Backup;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests.Backup;

/// <summary>
/// Global search lists a profile's own items that are waiting for their file after a restore ("File missing"). The query reads the
/// waiting area only: one profile, personal state, never a withheld title, never a View item, each title once.
/// </summary>
public sealed class WaitingTitleSearchTests : IDisposable
{
    private static readonly DateTimeOffset RestoredAt = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Stamp = "2026-10-21T10:00:00Z";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_waiting_search_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly WaitingPersonalStateRepository _repository;

    public WaitingTitleSearchTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new WaitingPersonalStateRepository(_database);
    }

    [Fact]
    public void SearchWaitingTitles_FindsAProfilesWaitingTitle_ByNameOrParentName_IgnoringCase()
    {
        var maya = AddProfile("Maya");
        Wait(maya, Title("Pilot", "Severance", 2022, "TV"));
        Wait(maya, Title("Dune", null, 2021, "Movies"));

        var byName = _repository.SearchWaitingTitles(GuidSql.FromDb(maya), "dUNE", 10);
        var byParent = _repository.SearchWaitingTitles(GuidSql.FromDb(maya), "severance", 10);

        var dune = Assert.Single(byName);
        Assert.Equal(new WaitingTitle("Dune", null, 2021, "Movies"), dune);
        var pilot = Assert.Single(byParent);
        Assert.Equal(new WaitingTitle("Pilot", "Severance", 2022, "TV"), pilot);
    }

    [Fact]
    public void SearchWaitingTitles_DoesNotShowAnotherProfilesTitle()
    {
        var maya = AddProfile("Maya");
        var theo = AddProfile("Theo");
        Wait(maya, Title("Dune", null, 2021, "Movies"));

        Assert.Empty(_repository.SearchWaitingTitles(GuidSql.FromDb(theo), "Dune", 10));
        Assert.Empty(_repository.SearchWaitingTitles(Guid.NewGuid(), "Dune", 10));
    }

    [Fact]
    public void SearchWaitingTitles_NeverShowsAWithheldRow()
    {
        var maya = AddProfile("Maya");
        Wait(maya, Title("Dune", null, 2021, "Movies") with { Withheld = true });
        Wait(maya, Title("Dunes of Mars", null, 2020, "Movies"));
        using (var connection = _database.CreateConnection())
        {
            // A withheld row carries no name at all; a flag alone must also be enough to keep a row out.
            connection.Execute("UPDATE waiting_personal_state SET is_withheld = 1 WHERE titles_json LIKE '%Dunes of Mars%'");
        }

        Assert.Empty(_repository.SearchWaitingTitles(GuidSql.FromDb(maya), "Dune", 10));
    }

    [Theory]
    [InlineData("LocalItem")]
    [InlineData("Collection")]
    [InlineData("Person")]
    [InlineData("FictionalEntity")]
    [InlineData("ArtworkAsset")]
    [InlineData("Other")]
    public void SearchWaitingTitles_OnlyListsWorkEditionAndMediaAssetTitles(string target)
    {
        var maya = AddProfile("Maya");
        Wait(maya, Title("Dune", null, 2021, "Movies") with { Target = target });

        Assert.Empty(_repository.SearchWaitingTitles(GuidSql.FromDb(maya), "Dune", 10));
    }

    [Theory]
    [InlineData("Work")]
    [InlineData("Edition")]
    [InlineData("MediaAsset")]
    public void SearchWaitingTitles_ListsTitlesOnAWorkEditionOrMediaAsset(string target)
    {
        var maya = AddProfile("Maya");
        Wait(maya, Title("Dune", null, 2021, "Movies") with { Target = target });

        Assert.Single(_repository.SearchWaitingTitles(GuidSql.FromDb(maya), "Dune", 10));
    }

    [Fact]
    public void SearchWaitingTitles_ListsATitleOnce_AndNoMoreThanTheLimit()
    {
        var maya = AddProfile("Maya");
        Wait(maya, Title("Dune", null, 2021, "Movies"));
        Wait(maya, Title("Dune", null, 2021, "Movies"));
        for (var i = 0; i < 12; i++)
        {
            Wait(maya, Title("Dune " + i.ToString("00"), null, 2000 + i, "Movies"));
        }

        var all = _repository.SearchWaitingTitles(GuidSql.FromDb(maya), "Dune", 50);
        var capped = _repository.SearchWaitingTitles(GuidSql.FromDb(maya), "Dune", 10);

        Assert.Equal(13, all.Count);
        Assert.Equal(1, all.Count(title => title.Name == "Dune"));
        Assert.Equal(10, capped.Count);
    }

    [Fact]
    public void SearchWaitingTitles_TreatsPercentAndUnderscoreAsPlainText()
    {
        var maya = AddProfile("Maya");
        Wait(maya, Title("Dune", null, 2021, "Movies"));
        Wait(maya, Title("100% Wolf", null, 2020, "Movies"));

        Assert.Equal("100% Wolf", Assert.Single(_repository.SearchWaitingTitles(GuidSql.FromDb(maya), "%", 10)).Name);
        Assert.Empty(_repository.SearchWaitingTitles(GuidSql.FromDb(maya), "D_ne", 10));
        Assert.Single(_repository.SearchWaitingTitles(GuidSql.FromDb(maya), "100%", 10));
    }

    [Fact]
    public void SearchWaitingTitles_StopsListingATitle_OnceItsRowsAreGone()
    {
        var maya = AddProfile("Maya");
        Wait(maya, Title("Dune", null, 2021, "Movies"));
        Assert.Single(_repository.SearchWaitingTitles(GuidSql.FromDb(maya), "Dune", 10));

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM waiting_personal_state");
        }

        Assert.Empty(_repository.SearchWaitingTitles(GuidSql.FromDb(maya), "Dune", 10));
    }

    private static BackupTitle Title(string name, string? parent, int? year, string mediaType) =>
        BackupTitle.Empty("t1", "MediaAsset") with
        {
            Ids = new Dictionary<string, string> { ["imdb_id"] = "tt" + Math.Abs(name.GetHashCode()) },
            FileHashes = ["missing-" + name],
            Name = name,
            ParentName = parent,
            Year = year,
            MediaType = mediaType,
        };

    private byte[] AddProfile(string name)
    {
        var id = GuidSql.ToBlob(Guid.NewGuid());
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO profiles (id, display_name, created_at) VALUES (@id, @name, @now)", new { id, name, now = Stamp });
        return id;
    }

    /// <summary>Restores one progress entry for the profile on a title the Library does not have.</summary>
    private void Wait(byte[] profile, BackupTitle title)
    {
        var table = new BackupExportTable(
            "user_states",
            BackupDataKind.PersonalState,
            ["user_id", "asset_id", "progress_pct", "last_accessed"],
            [[BackupValue.FromBlob(profile), BackupValue.FromTitleRef("t1"), BackupValue.FromReal(10), BackupValue.FromText(Stamp)]]);
        var export = new BackupExport(
            [table],
            [],
            [title],
            new BackupExportContents(BackupScope.WholeLibrary, 0, 0, 1, 0, 1, 0, 1, 0),
            []);

        new BackupRestoreRepository(_database).Import(export, "backup.zip", null, null, RestoredAt);
    }

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new SqliteConnection($"Data Source={_path}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        // Best-effort cleanup of the temporary data store; a locked file must not fail the test run.
        foreach (var leftover in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(leftover);
            }
            catch (IOException)
            {
                // A file another process still holds is left for the temp folder clean-up.
            }
        }
    }
}
