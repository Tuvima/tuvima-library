using System.Security.Cryptography;
using Dapper;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Contracts.Vault;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Configuration;
using MediaEngine.Storage.Services;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

// Move to Vault and Move out (Vault sections WP3, D30, D36 to D38, D40): real temp data store, real temp folders, the real deletion service,
// safety gate and hasher. A "scan" of the destination folder is played by the test, which registers the new file the way ingestion would.
public sealed class VaultMoveTests : IDisposable
{
    private readonly VaultMoveWorld _world = new();

    public void Dispose() => _world.Dispose();

    private static StartVaultMoveRequest Start(string direction, string sourceId, string fingerprint, bool removeOriginals = false) =>
        new() { Direction = direction, SourceId = sourceId, Fingerprint = fingerprint, RemoveReadOnlyOriginals = removeOriginals };

    private async Task<Guid> QueueAsync(string direction, Guid sourceId, bool removeOriginals = false)
    {
        var preview = await _world.Service.PreviewAsync(direction, sourceId.ToString("D"), default);
        Assert.Equal(VaultMovePreviewStates.Ready, preview.State);
        var outcome = await _world.Service.QueueAsync(_world.Admin, Start(direction, sourceId.ToString("D"), preview.Fingerprint, removeOriginals), default);
        Assert.Equal(VaultMoveStartStatus.Started, outcome.Status);
        return outcome.JobId!.Value;
    }

    [Fact]
    public async Task AMovieInAManagedFolder_MovesIntoTheVault_CarriesProgressAcross_AndNamesNoTitleAnywhere()
    {
        var path = _world.WriteFile(_world.NormalFolder, "Dune (2021).mkv", 4096);
        var old = _world.AddTitle(_world.NormalLibraryId, path, "Dune");
        var profile = _world.AddProfile();
        _world.SetProgress(profile, old.AssetId, 42);
        _world.React(profile, old.WorkId, "Like");

        var job = await QueueAsync(VaultMoveDirections.In, _world.NormalSourceId);
        await _world.Runner.RunPassAsync(default);

        // The checked copy is in the Vault folder under its real name, the old title is gone, and no leftover copy sits under a hidden name.
        var moved = Path.Combine(_world.VaultFolder, "Dune (2021).mkv");
        Assert.True(File.Exists(moved));
        Assert.Empty(Directory.GetFiles(_world.VaultFolder, "*" + VaultMoveService.StagingSuffix));
        Assert.False(File.Exists(path));
        Assert.Equal(0, _world.Count("works", old.WorkId));
        Assert.Equal(VaultMoveStates.Running, (await _world.Moves.GetAsync(job))!.State);

        // The destination folder's scan adds the file; the next pass carries what the profile had done.
        var added = _world.AddTitle(_world.VaultLibraryId, moved, "Dune (2021)");
        await _world.Runner.RunPassAsync(default);

        Assert.Equal(VaultMoveStates.Done, (await _world.Moves.GetAsync(job))!.State);
        Assert.Equal(42d, _world.Progress(profile, added.AssetId));
        Assert.Equal("Like", _world.Reaction(profile, added.WorkId));
        Assert.All(await _world.Moves.GetItemsAsync(job), item => Assert.Equal(VaultMoveItemStates.Done, item.State));

        // The activity entry and the events say only that an item was moved (D40).
        var activity = _world.ActivityText();
        Assert.Contains("An item was moved to the Vault.", activity, StringComparison.Ordinal);
        Assert.DoesNotContain("Dune", activity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dune", _world.ExclusionText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFolderLibraryReadsOnly_KeepsItsOriginal_AndTheJobSaysSo()
    {
        var world = new VaultMoveWorld(normalReadOnly: true);
        try
        {
            var path = world.WriteFile(world.NormalFolder, "Heat.mkv", 2048);
            world.AddTitle(world.NormalLibraryId, path, "Heat");

            var preview = await world.Service.PreviewAsync(VaultMoveDirections.In, world.NormalSourceId.ToString("D"), default);
            Assert.True(preview.OriginalsStay);
            var outcome = await world.Service.QueueAsync(world.Admin, Start(VaultMoveDirections.In, world.NormalSourceId.ToString("D"), preview.Fingerprint), default);
            await world.Runner.RunPassAsync(default);
            world.AddTitle(world.VaultLibraryId, Path.Combine(world.VaultFolder, "Heat.mkv"), "Heat");
            await world.Runner.RunPassAsync(default);

            Assert.True(File.Exists(path));
            var job = (await world.Moves.GetAsync(outcome.JobId!.Value))!;
            Assert.Equal(VaultMoveStates.Attention, job.State);
            Assert.Contains("still in place", job.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Heat", job.Message, StringComparison.Ordinal);
            Assert.True((await world.Moves.GetItemsAsync(job.Id)).Single().OriginalLeft);
        }
        finally
        {
            world.Dispose();
        }
    }

    [Fact]
    public async Task TheSeparateChoice_RemovesAReadOnlyOriginal_OnlyAfterTheCopyIsChecked()
    {
        var world = new VaultMoveWorld(normalReadOnly: true);
        try
        {
            var path = world.WriteFile(world.NormalFolder, "Heat.mkv", 2048);
            world.AddTitle(world.NormalLibraryId, path, "Heat");

            var job = await Queue(world, removeOriginals: true);
            await world.Runner.RunPassAsync(default);
            world.AddTitle(world.VaultLibraryId, Path.Combine(world.VaultFolder, "Heat.mkv"), "Heat");
            await world.Runner.RunPassAsync(default);

            Assert.False(File.Exists(path));
            Assert.Equal(VaultMoveStates.Done, (await world.Moves.GetAsync(job))!.State);
            Assert.False((await world.Moves.GetItemsAsync(job)).Single().OriginalLeft);
        }
        finally
        {
            world.Dispose();
        }

        static async Task<Guid> Queue(VaultMoveWorld w, bool removeOriginals)
        {
            var preview = await w.Service.PreviewAsync(VaultMoveDirections.In, w.NormalSourceId.ToString("D"), default);
            var outcome = await w.Service.QueueAsync(w.Admin, Start(VaultMoveDirections.In, w.NormalSourceId.ToString("D"), preview.Fingerprint, removeOriginals), default);
            return outcome.JobId!.Value;
        }
    }

    [Fact]
    public async Task ARemovalChoice_IsIgnoredForAFolderLibraryManages()
    {
        var path = _world.WriteFile(_world.NormalFolder, "Alien.mkv", 1024);
        _world.AddTitle(_world.NormalLibraryId, path, "Alien");
        var job = await QueueAsync(VaultMoveDirections.In, _world.NormalSourceId, removeOriginals: true);

        Assert.False((await _world.Moves.GetAsync(job))!.RemoveReadOnlyOriginals);
    }

    [Fact]
    public async Task SubtitlesAndNotesTravelWithTheFile_UnderTheCleanedName()
    {
        if (OperatingSystem.IsWindows())
        {
            // The name under test has a colon, which Windows cannot give a file; the cleaning is the same code and runs elsewhere.
            return;
        }

        var path = _world.WriteFile(_world.NormalFolder, "Dune: Part One.mkv", 2000);
        File.WriteAllText(Path.Combine(_world.NormalFolder, "Dune: Part One.en.srt"), "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        _world.AddTitle(_world.NormalLibraryId, path, "Dune");

        await QueueAsync(VaultMoveDirections.In, _world.NormalSourceId);
        await _world.Runner.RunPassAsync(default);

        Assert.True(File.Exists(Path.Combine(_world.VaultFolder, "Dune_ Part One.mkv")));
        Assert.True(File.Exists(Path.Combine(_world.VaultFolder, "Dune_ Part One.en.srt")));
        Assert.Empty(Directory.GetFiles(_world.VaultFolder, "*" + VaultMoveService.StagingSuffix));
        Assert.False(File.Exists(Path.Combine(_world.NormalFolder, "Dune: Part One.en.srt")));
    }

    [Fact]
    public async Task AMoveThatStopsAfterTheCopy_LeavesTheOriginalAndItsTitle_AndCarriesOnFromTheSavedStep()
    {
        var path = _world.WriteFile(_world.NormalFolder, "Alien.mkv", 1024);
        var old = _world.AddTitle(_world.NormalLibraryId, path, "Alien");
        var job = await QueueAsync(VaultMoveDirections.In, _world.NormalSourceId);

        // The Engine stops right after the checked copy was saved, before the old title is touched.
        _world.Runner.BeforeStep = state => { if (state == VaultMoveItemStates.Copied) { throw new InvalidOperationException("stop"); } };
        await _world.Runner.RunPassAsync(default);

        Assert.Equal(VaultMoveStates.Attention, (await _world.Moves.GetAsync(job))!.State);
        Assert.Equal(VaultMoveItemStates.Copied, (await _world.Moves.GetItemsAsync(job)).Single().State);
        Assert.True(File.Exists(path));
        Assert.Equal(1, _world.Count("works", old.WorkId));
        Assert.Single(Directory.GetFiles(_world.VaultFolder, "*" + VaultMoveService.StagingSuffix));

        _world.Runner.BeforeStep = null;
        await _world.Runner.RunPassAsync(default);

        Assert.Equal(VaultMoveItemStates.Published, (await _world.Moves.GetItemsAsync(job)).Single().State);
        Assert.True(File.Exists(Path.Combine(_world.VaultFolder, "Alien.mkv")));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task IfTheCheckedCopyGoesMissingBeforeTheOldTitleIsRemoved_TheOriginalIsNeverTouched_AndTheCopyIsMadeAgain()
    {
        var path = _world.WriteFile(_world.NormalFolder, "Brazil.mkv", 2048);
        var old = _world.AddTitle(_world.NormalLibraryId, path, "Brazil");
        var job = await QueueAsync(VaultMoveDirections.In, _world.NormalSourceId);

        _world.Runner.BeforeStep = state => { if (state == VaultMoveItemStates.Copied) { throw new InvalidOperationException("stop"); } };
        await _world.Runner.RunPassAsync(default);
        foreach (var staged in Directory.GetFiles(_world.VaultFolder, "*" + VaultMoveService.StagingSuffix))
        {
            File.Delete(staged);
        }

        _world.Runner.BeforeStep = null;
        await _world.Runner.RunPassAsync(default);

        // The original stayed until a fresh checked copy existed; then the move finished normally.
        Assert.Equal(VaultMoveItemStates.Published, (await _world.Moves.GetItemsAsync(job)).Single().State);
        Assert.True(File.Exists(Path.Combine(_world.VaultFolder, "Brazil.mkv")));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AVaultTitle_MovesOutToAFolderLibraryManages_WithProgress_AndNoEveryoneEvent()
    {
        var path = _world.WriteFile(_world.VaultFolder, "Private.mkv", 3000);
        var old = _world.AddTitle(_world.VaultLibraryId, path, "Private");
        var profile = _world.AddProfile();
        _world.SetProgress(profile, old.AssetId, 70);

        var job = await QueueAsync(VaultMoveDirections.Out, _world.VaultSourceId);
        await _world.Runner.RunPassAsync(default);

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(_world.NormalFolder, "Private.mkv")));
        Assert.Equal(0, _world.Count("works", old.WorkId));
        // Vault titles are never announced to every open screen (D37).
        Assert.Empty(_world.Events.Published);

        var added = _world.AddTitle(_world.NormalLibraryId, Path.Combine(_world.NormalFolder, "Private.mkv"), "Private");
        await _world.Runner.RunPassAsync(default);

        Assert.Equal(VaultMoveStates.Done, (await _world.Moves.GetAsync(job))!.State);
        Assert.Equal(70d, _world.Progress(profile, added.AssetId));
        Assert.DoesNotContain("Private", _world.ActivityText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePreview_SaysWhyNothingCanMove()
    {
        var path = _world.WriteFile(_world.NormalFolder, "Two.mkv", 100);
        var other = _world.WriteFile(_world.NormalFolder, "Two part 2.mkv", 101);
        var work = _world.AddTitle(_world.NormalLibraryId, path, "Two");
        _world.AddFileToTitle(work.WorkId, other, _world.NormalLibraryId);

        // A title with two files stays where it is.
        var preview = await _world.Service.PreviewAsync(VaultMoveDirections.In, _world.NormalSourceId.ToString("D"), default);
        Assert.Equal(VaultMovePreviewStates.NothingToMove, preview.State);
        Assert.Equal(2, preview.Skipped);

        // Out of the ordinary library is not a Move out, and an unknown folder is refused.
        Assert.Equal(VaultMovePreviewStates.Refused, (await _world.Service.PreviewAsync(VaultMoveDirections.Out, _world.NormalSourceId.ToString("D"), default)).State);
        Assert.Equal(VaultMovePreviewStates.Refused, (await _world.Service.PreviewAsync(VaultMoveDirections.In, Guid.NewGuid().ToString("D"), default)).State);
    }

    [Fact]
    public async Task WithoutAManagedVaultFolder_ThePreviewSaysSo_AndNothingStarts()
    {
        var world = new VaultMoveWorld(vaultManaged: false);
        try
        {
            world.AddTitle(world.NormalLibraryId, world.WriteFile(world.NormalFolder, "Alien.mkv", 100), "Alien");

            var preview = await world.Service.PreviewAsync(VaultMoveDirections.In, world.NormalSourceId.ToString("D"), default);
            Assert.Equal(VaultMovePreviewStates.NeedsManagedFolder, preview.State);

            var outcome = await world.Service.QueueAsync(world.Admin, Start(VaultMoveDirections.In, world.NormalSourceId.ToString("D"), preview.Fingerprint), default);
            Assert.Equal(VaultMoveStartStatus.NotAllowed, outcome.Status);
            Assert.Empty(await world.Moves.ListRecentAsync(10));
        }
        finally
        {
            world.Dispose();
        }
    }

    [Fact]
    public async Task AFolderThatChangedSinceThePreview_StartsNothing_AndSaysWhatIsTrueNow()
    {
        var path = _world.WriteFile(_world.NormalFolder, "Alien.mkv", 100);
        _world.AddTitle(_world.NormalLibraryId, path, "Alien");
        var preview = await _world.Service.PreviewAsync(VaultMoveDirections.In, _world.NormalSourceId.ToString("D"), default);

        File.WriteAllBytes(path, new byte[200]);
        var outcome = await _world.Service.QueueAsync(_world.Admin, Start(VaultMoveDirections.In, _world.NormalSourceId.ToString("D"), preview.Fingerprint), default);

        Assert.Equal(VaultMoveStartStatus.Changed, outcome.Status);
        Assert.Equal(VaultMoveService.NoLongerMessage, outcome.Preview!.Message);
        Assert.Empty(await _world.Moves.ListRecentAsync(10));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void FileNamesAreCleanedLikeTheOrganiserDoesIt()
    {
        Assert.Equal("Dune_ Part One.mkv", VaultMoveService.CleanFileName("Dune: Part One.mkv"));
        Assert.Equal("_CON.mkv", VaultMoveService.CleanFileName("CON.mkv"));
        Assert.Equal("Video.mkv", VaultMoveService.CleanFileName("...mkv"));
        Assert.Equal("A B.mkv", VaultMoveService.CleanFileName("A   B .mkv"));
    }
}

/// <summary>A real temp data store with a normal Movies library and a Vault Movies library, wired to the real deletion service and safety gate.</summary>
internal sealed class VaultMoveWorld : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_vault_move_{Guid.NewGuid():N}.db");
    private readonly IngestionOptions _options;
    private readonly ConfigurationDirectoryLoader _configuration;

    public VaultMoveWorld(bool normalReadOnly = false, bool vaultManaged = true)
    {
        DapperConfiguration.Configure();
        Root = Path.Combine(Path.GetTempPath(), $"tuvima_vault_move_{Guid.NewGuid():N}");
        NormalFolder = Directory.CreateDirectory(Path.Combine(Root, "movies")).FullName;
        VaultFolder = Directory.CreateDirectory(Path.Combine(Root, "private-movies")).FullName;
        Directory.CreateDirectory(Path.Combine(Root, "config"));

        Database = new DatabaseConnection(_databasePath);
        Database.InitializeSchema();
        Database.RunStartupChecks();

        var normalManagement = normalReadOnly ? LibrarySourceManagementModes.ExistingLibrary : LibrarySourceManagementModes.ManagedByTuvima;
        var normalAccess = normalReadOnly ? LibrarySourceAccessModes.ReadOnly : LibrarySourceAccessModes.Writable;
        var vaultManagement = vaultManaged ? LibrarySourceManagementModes.ManagedByTuvima : LibrarySourceManagementModes.ExistingLibrary;
        var vaultAccess = vaultManaged ? LibrarySourceAccessModes.Writable : LibrarySourceAccessModes.ReadOnly;

        _configuration = new ConfigurationDirectoryLoader(Path.Combine(Root, "config"));
        _configuration.SaveLibraries(new LibrariesConfiguration
        {
            StorageLocations = [new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = Root, AllowWrite = true }],
            Libraries =
            [
                new LibraryFolderConfig
                {
                    Id = NormalLibraryId, Name = "Movies", Category = "Movies", Kind = LibraryKinds.Catalogued, KindKey = "movies", Area = "watch",
                    MediaTypes = ["Movies"], Access = LibraryAccessLevels.Standard,
                    Sources = [new LibrarySourceConfig { Id = NormalSourceId.ToString("D"), Path = NormalFolder, ManagementMode = normalManagement, AccessMode = normalAccess }],
                },
                new LibraryFolderConfig
                {
                    Id = VaultLibraryId, Name = "Private Movies", Category = "Movies", Kind = LibraryKinds.Catalogued, KindKey = "movies", Area = "watch",
                    MediaTypes = ["Movies"], Access = LibraryAccessLevels.Vault,
                    Sources = [new LibrarySourceConfig { Id = VaultSourceId.ToString("D"), Path = VaultFolder, ManagementMode = vaultManagement, AccessMode = vaultAccess }],
                },
            ],
        });

        _options = new IngestionOptions
        {
            LibraryFolders =
            [
                new LibraryFolderEntry
                {
                    Id = NormalLibraryId, Name = "Movies", KindKey = "movies",
                    Sources = [new LibrarySourceEntry { Id = NormalSourceId.ToString("D"), Path = NormalFolder, ManagementMode = normalManagement, AccessMode = normalAccess }],
                },
                new LibraryFolderEntry
                {
                    Id = VaultLibraryId, Name = "Private Movies", KindKey = "movies",
                    Sources = [new LibrarySourceEntry { Id = VaultSourceId.ToString("D"), Path = VaultFolder, ManagementMode = vaultManagement, AccessMode = vaultAccess }],
                },
            ],
        };

        using (var connection = Database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO profiles(id,display_name,avatar_color,role,content_limit,created_at) VALUES(@id,'Administrator','#7C4DFF','Administrator',NULL,@now);",
                new { id = Admin, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        VaultLibraries = new VaultLibraryRepository(Database);
        var now = DateTimeOffset.UtcNow;
        VaultLibraries.AddAsync(new VaultLibrary(VaultLibraryId, VaultLookupSources.None, null, now, Admin, now, now)).GetAwaiter().GetResult();

        Moves = new VaultMoveRepository(Database);
        Events = new LibraryItemDeletionTestWorld.RecordingEvents();
        var resolver = new LibraryFolderResolver(new StaticMonitor(_options));
        var excluded = new ExcludedSourceFileRepository(Database);
        Excluded = excluded;
        Activity = new SystemActivityRepository(Database);
        var deletion = new LibraryItemDeletionService(
            new LibraryItemDeletionRepository(Database, NullLogger<LibraryItemDeletionRepository>.Instance),
            resolver,
            new SourceMutationPolicyGate(new FolderOrganiseLogRepository(Database)),
            new Extensions(),
            excluded,
            new ExcludedSourceFileFilter(resolver, excluded, new AssetHasher(), NullLogger<ExcludedSourceFileFilter>.Instance),
            new WorkHierarchyMaintenanceService(Database),
            Activity,
            Events,
            new AssetPathService(Path.Combine(Root, "library"), dataRoot: Path.Combine(Root, "data")),
            NullLogger<LibraryItemDeletionService>.Instance);

        Service = new VaultMoveService(
            _configuration, VaultLibraries, Moves, new Extensions(), null!, new VaultMoveSignal(), TimeProvider.System, NullLogger<VaultMoveService>.Instance);
        Runner = new VaultMoveRunner(
            Moves, new MovedItemStateCarry(Database), deletion, new AssetHasher(), TimeProvider.System, NullLogger<VaultMoveRunner>.Instance);
    }

    public Guid Admin { get; } = Guid.NewGuid();
    public string NormalLibraryId { get; } = Guid.NewGuid().ToString("D");
    public string VaultLibraryId { get; } = Guid.NewGuid().ToString("D");
    public Guid NormalSourceId { get; } = Guid.NewGuid();
    public Guid VaultSourceId { get; } = Guid.NewGuid();
    public string Root { get; }
    public string NormalFolder { get; }
    public string VaultFolder { get; }
    public DatabaseConnection Database { get; }
    public VaultLibraryRepository VaultLibraries { get; }
    public VaultMoveRepository Moves { get; }
    public ExcludedSourceFileRepository Excluded { get; }
    public SystemActivityRepository Activity { get; }
    public LibraryItemDeletionTestWorld.RecordingEvents Events { get; }
    public VaultMoveService Service { get; }
    public VaultMoveRunner Runner { get; }

    public string WriteFile(string folder, string name, int bytes)
    {
        var path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // The same bytes in every file of the same size, so the same size is the same file (the duplicate test relies on it).
        File.WriteAllBytes(path, Enumerable.Range(0, bytes).Select(i => (byte)(i % 251)).ToArray());
        return path;
    }

    /// <summary>Registers a title with one file in a library, with the file's real hash, as the ingestion scan would.</summary>
    public (Guid WorkId, Guid AssetId) AddTitle(string libraryId, string path, string title)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = Database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, media_type, work_kind, is_catalog_only, ownership) VALUES (@workId, 'Movies', 'standalone', 0, 'Owned');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'title', @title, @now);
            """,
            new { workId, editionId, title, now = DateTimeOffset.UtcNow.ToString("O") });
        InsertAsset(connection, editionId, assetId, libraryId, path);
        return (workId, assetId);
    }

    public void AddFileToTitle(Guid workId, string path, string libraryId)
    {
        using var connection = Database.CreateConnection();
        var editionId = connection.ExecuteScalar<Guid>("SELECT id FROM editions WHERE work_id = @workId LIMIT 1;", new { workId });
        InsertAsset(connection, editionId, Guid.NewGuid(), libraryId, path);
    }

    private static void InsertAsset(System.Data.IDbConnection connection, Guid editionId, Guid assetId, string libraryId, string path) =>
        connection.Execute("""
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id, status)
            VALUES (@assetId, @editionId, @hash, @path, @libraryId, 'Normal');
            """,
            new { assetId, editionId, hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), path, libraryId });

    public Guid AddProfile()
    {
        var id = Guid.NewGuid();
        using var connection = Database.CreateConnection();
        connection.Execute("INSERT INTO profiles (id, display_name, created_at) VALUES (@id, 'Someone', @now);", new { id, now = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    public void SetProgress(Guid profile, Guid assetId, double percent)
    {
        using var connection = Database.CreateConnection();
        connection.Execute(
            "INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed) VALUES (@profile, @assetId, @percent, @now);",
            new { profile, assetId, percent, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    public void React(Guid profile, Guid workId, string reaction)
    {
        using var connection = Database.CreateConnection();
        connection.Execute(
            "INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@profile, 'Movie', @workId, @reaction, @now);",
            new { profile, workId, reaction, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    public double? Progress(Guid profile, Guid assetId)
    {
        using var connection = Database.CreateConnection();
        return connection.ExecuteScalar<double?>("SELECT progress_pct FROM user_states WHERE user_id = @profile AND asset_id = @assetId;", new { profile, assetId });
    }

    public string? Reaction(Guid profile, Guid workId)
    {
        using var connection = Database.CreateConnection();
        return connection.ExecuteScalar<string?>("SELECT reaction FROM profile_reactions WHERE profile_id = @profile AND entity_id = @workId;", new { profile, workId });
    }

    public int Count(string table, Guid id)
    {
        using var connection = Database.CreateConnection();
        return connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table} WHERE id = @id;", new { id });
    }

    public string ActivityText()
    {
        using var connection = Database.CreateConnection();
        return string.Join("\n", connection.Query<string>("SELECT COALESCE(detail,'') || ' ' || COALESCE(collection_name,'') || ' ' || COALESCE(changes_json,'') FROM system_activity;"));
    }

    public string ExclusionText()
    {
        using var connection = Database.CreateConnection();
        return string.Join("\n", connection.Query<string>("SELECT COALESCE(title,'') || ' ' || relative_path FROM excluded_source_files;").Select(text => text.Split(' ')[0]));
    }

    public void Dispose()
    {
        _configuration.Dispose();
        Database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
        TestTemp.DeleteDirectory(Root);
    }

    private sealed class StaticMonitor(IngestionOptions value) : IOptionsMonitor<IngestionOptions>
    {
        public IngestionOptions CurrentValue => value;

        public IngestionOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<IngestionOptions, string?> listener) => null;
    }

    private sealed class Extensions : IMediaTypeExtensionCatalog
    {
        private static readonly IReadOnlySet<string> Media = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4" };

        public IReadOnlySet<string> GetAllMediaExtensions() => Media;

        public IReadOnlySet<string> GetExtensionsFor(MediaType mediaType) => throw new NotSupportedException();

        public bool IsKnownMediaExtension(string? extension) => extension is not null && Media.Contains(extension);

        public bool IsUnambiguousExtension(string? extension) => throw new NotSupportedException();

        public bool IsStrongFormatExtension(string? extension) => throw new NotSupportedException();

        public bool IsVideoExtension(string? extension) => throw new NotSupportedException();

        public MediaType ResolveMediaTypeFromExtension(string? extension) => throw new NotSupportedException();
    }
}
