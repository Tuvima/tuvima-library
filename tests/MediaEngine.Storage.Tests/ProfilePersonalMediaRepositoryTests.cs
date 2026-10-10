using Dapper;
using MediaEngine.Domain.Enums;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>What removing a person has to do about their personal photos, against a real SQLite data store.</summary>
public sealed class ProfilePersonalMediaRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly ProfilePersonalMediaRepository _media;

    public ProfilePersonalMediaRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_profile_media_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _media = new ProfilePersonalMediaRepository(_database);
    }

    [Fact]
    public async Task APersonWhoSharedPhotos_CanBeRemoved_AndTheShareRecordsGoWithThem()
    {
        var person = Person("Maya");
        var item = Photo(person, "/data/Profiles/maya/a.jpg", managed: true);
        var contributionId = Guid.NewGuid();
        Execute("""
            INSERT INTO view_shared_transfers
                (id,item_id,operation,state,source_manifest_json,created_at,updated_at)
            VALUES (@id,@item,'move','completed','[]',@now,@now);
            INSERT INTO view_shared_contributions
                (id,submitted_by_profile_id,submitted_by_name,status,destination_kind,idempotency_key,submitted_at,updated_at)
            VALUES (@contributionId,@person,'Maya','pending','timeline','k1',@now,@now);
            INSERT INTO view_shared_contribution_items
                (id,contribution_id,item_id,original_profile_name,position,operation,source_manifest_json,execution_state,updated_at)
            VALUES (@itemRow,@contributionId,@item,'Maya',0,'move','[]','waiting',@now);
            """, new { id = Guid.NewGuid(), item, now = Now.ToString("O"), contributionId, person, itemRow = Guid.NewGuid() });

        // The share records point at the person's photo; removing the person lets go of them in the same step.
        await _accounts.DeleteManagedProfileAsync(person);

        Assert.Equal(0, Count("SELECT COUNT(*) FROM profiles WHERE id=@person", new { person }));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM local_items WHERE id=@item", new { item }));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM view_shared_contributions", new { }));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM view_shared_transfers", new { }));
    }

    [Fact]
    public async Task PersonalFiles_SayWhichCopiesTuvimaOwns()
    {
        var person = Person("Maya");
        Photo(person, "/data/Profiles/maya/managed.jpg", managed: true);
        Photo(person, "/photos/linked.jpg", managed: false);
        var someoneElse = Person("Sam");
        Photo(someoneElse, "/data/Profiles/sam/other.jpg", managed: true);

        var files = await _media.GetPersonalFilesAsync(person);

        Assert.Equal(2, files.Count);
        Assert.Contains(files, file => file.FilePath == "/data/Profiles/maya/managed.jpg" && file.IsManaged);
        Assert.Contains(files, file => file.FilePath == "/photos/linked.jpg" && !file.IsManaged);
        Assert.Equal(2, (await _media.GetPersonalItemIdsAsync(person)).Count);
    }

    [Fact]
    public async Task TrashedPhotos_AreNotKept_AndKeptPhotosGetTheTag()
    {
        var person = Person("Maya");
        var kept = Photo(person, "/data/Profiles/maya/kept.jpg", managed: true);
        var trashed = Photo(person, "/data/Profiles/maya/trashed.jpg", managed: true);
        Execute("UPDATE local_items SET trashed_at=@now WHERE id=@trashed;", new { now = Now.ToString("O"), trashed });

        Assert.Equal(new[] { kept }, await _media.GetPersonalItemIdsAsync(person));

        await _media.AddTagAsync(person, "From Maya 2026-10-10", Now);

        Assert.Equal(1, Count("SELECT COUNT(*) FROM local_item_tags WHERE item_id=@kept AND tag='From Maya 2026-10-10'", new { kept }));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM local_item_tags WHERE item_id=@trashed", new { trashed }));
    }

    [Fact]
    public async Task UnusedFiles_AreForgotten_ButAFileAnotherItemStillUsesStays()
    {
        var person = Person("Maya");
        var item = Photo(person, "/data/Profiles/maya/a.jpg", managed: true);
        var fileId = Query<Guid>("SELECT file_id FROM local_item_files WHERE item_id=@item", new { item });
        var other = Photo(Person("Sam"), "/data/Profiles/sam/b.jpg", managed: true);
        var otherFile = Query<Guid>("SELECT file_id FROM local_item_files WHERE item_id=@other", new { other });

        await _accounts.DeleteManagedProfileAsync(person);
        await _media.DeleteUnusedFilesAsync([fileId, otherFile]);

        Assert.Equal(0, Count("SELECT COUNT(*) FROM local_files WHERE id=@fileId", new { fileId }));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM local_files WHERE id=@otherFile", new { otherFile }));
    }

    [Fact]
    public async Task AFailedRemoval_LeavesThePersonsPhotosAndShareRecordsExactlyAsTheyWere()
    {
        var person = Person("Maya");
        var item = Photo(person, "/data/Profiles/maya/a.jpg", managed: true);
        Execute("""
            INSERT INTO view_shared_transfers
                (id,item_id,operation,state,source_manifest_json,created_at,updated_at)
            VALUES (@id,@item,'move','failed','[]',@now,@now);
            CREATE TRIGGER trg_test_block_profile_delete BEFORE DELETE ON profiles
            BEGIN SELECT RAISE(ABORT,'blocked for test'); END;
            """, new { id = Guid.NewGuid(), item, now = Now.ToString("O") });

        await Assert.ThrowsAnyAsync<Exception>(() => _accounts.DeleteManagedProfileAsync(person));

        Assert.Equal(1, Count("SELECT COUNT(*) FROM profiles WHERE id=@person", new { person }));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM view_shared_transfers WHERE item_id=@item", new { item }));
        Assert.Equal(1, Count("""
            SELECT COUNT(*) FROM local_file_sources
             WHERE library_id IN (SELECT library_id FROM view_personal_spaces WHERE owner_profile_id=@person)
            """, new { person }));
    }

    [Fact]
    public async Task APersonWhoChoseAFolderTimelineSetting_CanStillBeRemoved()
    {
        var person = Person("Maya");
        var other = Person("Sam");
        Photo(other, "/data/Profiles/sam/b.jpg", managed: true);
        var source = Query<Guid>("SELECT id FROM view_sources WHERE personal_space_id IN (SELECT id FROM view_personal_spaces WHERE owner_profile_id=@other)", new { other });
        Execute("""
            INSERT INTO view_folder_timeline_policies
                (source_id,relative_path,absolute_path,include_in_timeline,updated_by_profile_id,updated_at)
            VALUES (@source,'Trips','/data/Trips',1,@person,@now);
            """, new { source, person, now = Now.ToString("O") });

        await _accounts.DeleteManagedProfileAsync(person);

        Assert.Equal(1, Count("SELECT COUNT(*) FROM view_folder_timeline_policies WHERE updated_by_profile_id IS NULL", new { }));
    }

    private Guid Person(string name)
    {
        var id = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        Execute("""
            INSERT INTO profiles(id,display_name,avatar_color,role,created_at)
            VALUES(@id,@name,'#7C4DFF',@role,@now);
            INSERT INTO view_personal_spaces(id,owner_profile_id,library_id,created_at,updated_at)
            VALUES(@spaceId,@id,@library,@now,@now);
            """, new { id, name, role = ProfileRole.StandardUser.ToString(), spaceId, library = Guid.NewGuid(), now = Now.ToString("O") });
        return id;
    }

    private Guid Photo(Guid person, string path, bool managed)
    {
        var item = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var space = QueryRow("SELECT id AS Space, library_id AS Library FROM view_personal_spaces WHERE owner_profile_id=@person", new { person });
        Execute("""
            INSERT INTO view_sources
                (id,scope_kind,personal_space_id,library_id,source_type,name,storage_mode,relative_path,external_path,created_at,updated_at)
            VALUES (@sourceId,'personal',@space,@library,@type,'Source',@mode,@relative,@external,@now,@now);
            INSERT INTO local_items
                (id,scope_kind,personal_space_id,owner_profile_id,library_id,media_kind,primary_file_name,primary_mime_type,created_at,updated_at)
            VALUES (@item,'personal',@space,@person,@library,'image','a.jpg','image/jpeg',@now,@now);
            INSERT INTO local_files(id,content_hash,byte_size,mime_type,created_at)
            VALUES (@fileId,@hash,10,'image/jpeg',@now);
            INSERT INTO local_item_files(item_id,file_id,role,added_at)
            VALUES (@item,@fileId,'primary',@now);
            INSERT INTO local_file_sources(id,file_id,library_id,source_id,file_path,modified_at,indexed_at)
            VALUES (@sourceRow,@fileId,@library,@sourceId,@path,@now,@now);
            """, new
        {
            sourceId,
            space = space.Space,
            library = space.Library,
            type = managed ? "browser_upload" : "folder",
            mode = managed ? "managed" : "linked",
            relative = managed ? "Profiles/x" : null,
            external = managed ? null : "/photos",
            item,
            person,
            fileId,
            hash = Guid.NewGuid().ToString("N"),
            sourceRow = Guid.NewGuid(),
            path,
            now = Now.ToString("O"),
        });
        return item;
    }

    private void Execute(string sql, object parameters)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(sql, parameters);
    }

    private int Count(string sql, object parameters)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<int>(sql, parameters);
    }

    private T Query<T>(string sql, object parameters)
    {
        using var connection = _database.CreateConnection();
        return connection.QuerySingle<T>(sql, parameters);
    }

    private SpaceRow QueryRow(string sql, object parameters)
    {
        using var connection = _database.CreateConnection();
        return connection.QuerySingle<SpaceRow>(sql, parameters);
    }

    private sealed class SpaceRow
    {
        public Guid Space { get; init; }
        public Guid Library { get; init; }
    }

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new SqliteConnection($"Data Source={_databasePath}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        // Best-effort cleanup of the temporary data store; a locked file must not fail the test run.
        try { File.Delete(_databasePath); } catch (IOException) { }
    }
}
