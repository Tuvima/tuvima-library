using Dapper;
using MediaEngine.Storage;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Security review leaks 2 and 3 at the data store: the people read that has no viewer never counts a Vault file, the credits behind it
/// carry the file so a viewer's reads can be narrowed to what that viewer may see, and a person known only from Vault files can be told
/// apart from a shared one (Vault sections spec D27, D32).
/// </summary>
public sealed class VaultPersonCreditsStorageTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly string _vaultLibrary = Guid.NewGuid().ToString("D");
    private readonly string _ordinaryLibrary = Guid.NewGuid().ToString("D");

    public VaultPersonCreditsStorageTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_vault_person_credits_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        using var connection = _db.CreateConnection();
        connection.Execute(
            "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@library,'none',@now,@now,@now);",
            new { library = _vaultLibrary, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* a temp file left behind is harmless */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* a temp file left behind is harmless */ }
    }

    [Fact]
    public async Task Presence_NeverCountsAFileInAVaultLibrary_ForAnyoneWhoAsks()
    {
        var shared = SeedPerson("Shared Performer", "QSHARED");
        var vaultOnly = SeedPerson("Vault Only Performer", "QVAULTONLY");
        var ordinary = SeedWork("Movies", _ordinaryLibrary, "Shared Performer");
        var secret = SeedWork("Movies", _vaultLibrary, "Shared Performer", "Vault Only Performer");
        var repository = new PersonRepository(_db);

        var presence = await repository.GetPresenceBatchAsync([shared, vaultOnly]);

        // The shared person counts the ordinary film once, not the Vault one; the person seen only in the Vault has nothing to count.
        Assert.Equal(1, presence[shared]["Movies"]);
        Assert.DoesNotContain(vaultOnly, presence.Keys);
        Assert.NotEqual(Guid.Empty, ordinary.AssetId);
        Assert.NotEqual(Guid.Empty, secret.AssetId);
    }

    [Fact]
    public async Task PresenceCredits_CarryTheFile_AndLeaveOutVaultFiles()
    {
        var shared = SeedPerson("Shared Performer", "QSHARED");
        var ordinary = SeedWork("Movies", _ordinaryLibrary, "Shared Performer");
        var secret = SeedWork("Movies", _vaultLibrary, "Shared Performer");

        var credits = await new PersonRepository(_db).GetPresenceCreditsAsync([shared]);

        var credit = Assert.Single(credits);
        Assert.Equal(shared, credit.PersonId);
        Assert.Equal(ordinary.AssetId, credit.AssetId);
        Assert.DoesNotContain(credits, row => row.AssetId == secret.AssetId);
    }

    [Fact]
    public async Task APersonCreditedOnlyOnVaultFiles_IsVaultOnly()
    {
        var person = SeedPerson("Vault Only Performer", "QVAULTONLY");
        SeedWork("Movies", _vaultLibrary, "Vault Only Performer");

        Assert.True(await new VaultLibraryRepository(_db).IsPersonOnlyInVaultAsync(person));
    }

    [Fact]
    public async Task APersonAlsoCreditedOnAnOrdinaryFile_IsNotVaultOnly()
    {
        var person = SeedPerson("Shared Performer", "QSHARED");
        SeedWork("Movies", _vaultLibrary, "Shared Performer");
        SeedWork("Movies", _ordinaryLibrary, "Shared Performer");

        Assert.False(await new VaultLibraryRepository(_db).IsPersonOnlyInVaultAsync(person));
    }

    [Fact]
    public async Task APersonWithNoCreditedFile_OrOnlyOrdinaryFiles_IsNotVaultOnly()
    {
        var ordinaryOnly = SeedPerson("Ordinary Performer", "QORDINARY");
        SeedWork("Movies", _ordinaryLibrary, "Ordinary Performer");
        var uncredited = SeedPerson("Nobody Yet", null);
        var repository = new VaultLibraryRepository(_db);

        Assert.False(await repository.IsPersonOnlyInVaultAsync(ordinaryOnly));
        Assert.False(await repository.IsPersonOnlyInVaultAsync(uncredited));
        Assert.False(await repository.IsPersonOnlyInVaultAsync(Guid.Empty));
        Assert.False(await repository.IsPersonOnlyInVaultAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task APersonLinkedByEnrichmentToAVaultFileAndNoOrdinaryFile_IsVaultOnly_AndALinkToAnOrdinaryFileMakesThemShared()
    {
        var person = SeedPerson("Linked Performer", null);
        var secret = SeedWork("Movies", _vaultLibrary);
        var ordinary = SeedWork("Movies", _ordinaryLibrary);
        var repository = new VaultLibraryRepository(_db);

        Link(person, secret.AssetId);
        Assert.True(await repository.IsPersonOnlyInVaultAsync(person));

        Link(person, ordinary.AssetId);
        Assert.False(await repository.IsPersonOnlyInVaultAsync(person));
    }

    private Guid SeedPerson(string name, string? qid)
    {
        var id = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute(
            "INSERT INTO persons(id, name, wikidata_qid, created_at) VALUES(@id, @name, @qid, datetime('now'));",
            new { id, name, qid });
        return id;
    }

    private (Guid WorkId, Guid AssetId) SeedWork(string mediaType, string library, params string[] cast)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works(id, media_type, work_kind, ownership, is_catalog_only) VALUES(@workId, @mediaType, 'standalone', 'Owned', 0);
            INSERT INTO editions(id, work_id) VALUES(@editionId, @workId);
            INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id, status)
            VALUES(@assetId, @editionId, @hash, @path, @library, 'Normal');
            """,
            new { workId, editionId, assetId, mediaType, library, hash = $"hash_{assetId:N}", path = $"/library/{assetId:N}.mkv" });
        var ordinal = 0;
        foreach (var name in cast)
        {
            connection.Execute(
                "INSERT INTO canonical_value_arrays(entity_id, key, ordinal, value) VALUES(@workId, 'cast_member', @ordinal, @name);",
                new { workId, ordinal = ordinal++, name });
        }

        return (workId, assetId);
    }

    private void Link(Guid personId, Guid assetId)
    {
        using var connection = _db.CreateConnection();
        connection.Execute(
            "INSERT INTO person_media_links(media_asset_id, person_id, role) VALUES(@assetId, @personId, 'Actor');",
            new { assetId, personId });
    }
}
