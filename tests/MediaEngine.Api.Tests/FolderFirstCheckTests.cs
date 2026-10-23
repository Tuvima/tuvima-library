using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The first check of a newly added folder (Library setup spec D35, D61): its first files are identified as normal, but nothing in the folder
/// is renamed, moved or tagged; a folder where most files do not match is paused and claims no more identification work; Keep going and a
/// pass release the held moves and tags to the next run; removing the folder drops the check. Real temp folders, a real data store, the
/// real organiser, identity-job repository and file queue; only the tag writer is a spy.
/// </summary>
public sealed class FolderFirstCheckTests : IDisposable
{
    private const string LibraryId = "music";
    private const string ManagedSource = "managed";
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private readonly LibraryMoveTestWorld _world = new(withFiles: true);
    private readonly FolderFirstCheckSignal _signal = new();
    private readonly FolderFirstCheckRepository _checks;
    private readonly IdentityJobRepository _jobs;
    private readonly UnmatchedAssetRepository _unmatched;
    private readonly AutoOrganizeService _organizer;
    private readonly FolderFirstCheckService _service;
    private Guid? _album;
    private int _songs;

    public FolderFirstCheckTests()
    {
        _checks = new FolderFirstCheckRepository(Files.Database, _signal);
        _jobs = new IdentityJobRepository(Files.Database, _signal);
        _unmatched = new UnmatchedAssetRepository(Files.Database);
        _organizer = Files.CreateFilingOrganizer(new FolderFirstCheckFilingHold(_checks));
        _service = new FolderFirstCheckService(_checks, Files.Configuration, NullLogger<FolderFirstCheckService>.Instance);
        Files.WriteBack.PathOf = Files.StoredPath;
    }

    private MoveFileWorld Files => _world.Files!;

    public void Dispose()
    {
        _signal.Dispose();
        _world.Dispose();
    }

    // ----- the safety break ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANewManagedFolder_WhoseFirst50FilesAreSixtyPercentUnmatched_PausesAfter50_AndNothingInItIsRenamedMovedOrTagged()
    {
        var songs = await AddSongsAsync(80);
        await _checks.StartAsync(LibraryId, ManagedSource, Files.Managed, Now);

        await FinishAsync(songs.Take(50), matched: index => index % 5 >= 3);
        var ended = await _service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(1, ended);
        var listed = Assert.Single(await _service.ListAsync(CancellationToken.None));
        Assert.Equal((FolderFirstCheckStates.Paused, 50, 30, 80), (listed.Check.State, listed.Check.Tried, listed.Check.Unmatched, listed.TotalFiles));
        Assert.Equal((LibraryId, ManagedSource, Files.Managed), (listed.Check.LibraryId, listed.Check.SourceId, listed.Check.Path));

        // Run everything the Engine would run: the scheduled batch, Write now, the move lane.
        await Files.Scheduler.RunBatchAsync();
        await Files.Scheduler.RunMoveLaneAsync(CancellationToken.None);
        Assert.True(Files.Scheduler.RequestRun());
        await Files.Scheduler.TickAsync();
        foreach (var song in songs)
        {
            Assert.True(File.Exists(song.OldPath), song.OldPath);
            Assert.Equal(song.OldPath, Files.StoredPath(song.Asset));
        }

        Assert.Empty(Files.WriteBack.Calls);
        Assert.Equal(20, Files.Scalar<int>("SELECT COUNT(*) FROM media_file_write_intents WHERE move_step = 'held' AND move_origin = 'filing';"));

        // The 30 files that were not tried are not claimed either; nothing of this folder is identified until an administrator chooses.
        Assert.Empty(await _jobs.LeaseNextAsync("worker", [IdentityJobState.Queued], 100, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task KeepGoing_ResumesIdentification_AndReleasesTheMatchedFilesToTheNextRun_WhileUnmatchedFilesStayPut()
    {
        var songs = await AddSongsAsync(80);
        await _checks.StartAsync(LibraryId, ManagedSource, Files.Managed, Now);
        await FinishAsync(songs.Take(50), matched: index => index % 5 >= 3);
        await _service.EvaluateAsync(CancellationToken.None);
        var matched = songs.Take(50).Where((_, index) => index % 5 >= 3).ToList();
        var unmatched = songs.Take(50).Where((_, index) => index % 5 < 3).ToList();
        var targets = new Dictionary<Guid, string>();
        foreach (var song in matched)
        {
            targets[song.Asset] = await ExpectedPathAsync(song.Asset);
        }

        Assert.True(await _service.KeepGoingAsync(ManagedSource, CancellationToken.None));

        // Identification resumes for the rest of the folder.
        var resumed = await _jobs.LeaseNextAsync("worker", [IdentityJobState.Queued], 100, TimeSpan.FromMinutes(1));
        Assert.Equal(30, resumed.Count);
        Assert.Empty(await _service.ListAsync(CancellationToken.None));
        Assert.Equal(FolderFirstCheckStates.KeptGoing, (await _checks.GetAsync(ManagedSource))!.State);

        // The matched files are filed in the next run, with their tags written at the final place.
        Assert.All(matched, song => Assert.Equal("waiting", MoveStep(song.Asset)));
        await Files.Scheduler.RunBatchAsync();
        foreach (var song in matched)
        {
            Assert.True(File.Exists(targets[song.Asset]));
            Assert.False(File.Exists(song.OldPath));
            Assert.Equal(targets[song.Asset], Files.StoredPath(song.Asset));
            Assert.Contains(Files.WriteBack.Calls, call => call.AssetId == song.Asset && call.PathAtTheTime == targets[song.Asset]);
        }

        // Unmatched files are not moved and stay Unmatched, to be retried later.
        foreach (var song in unmatched)
        {
            Assert.True(File.Exists(song.OldPath));
            Assert.Equal(song.OldPath, Files.StoredPath(song.Asset));
            Assert.NotNull(await _unmatched.GetAsync(song.Asset));
        }
    }

    [Fact]
    public async Task ANewManagedFolderThatPasses_HasItsHeldMatchedFilesFiledInTheNextRun_WithTheirTags()
    {
        var songs = await AddSongsAsync(10);
        await _checks.StartAsync(LibraryId, ManagedSource, Files.Managed, Now);
        var targets = new Dictionary<Guid, string>();
        foreach (var song in songs.Skip(2))
        {
            targets[song.Asset] = await ExpectedPathAsync(song.Asset);
        }

        await FinishAsync(songs.Take(9), matched: index => index >= 2);
        // One file has not been looked at yet: a folder with fewer files than the check tries waits for all of them.
        Assert.Equal(0, await _service.EvaluateAsync(CancellationToken.None));
        await FinishAsync(songs.Skip(9), matched: _ => true);
        Assert.Equal(1, await _service.EvaluateAsync(CancellationToken.None));

        var check = (await _checks.GetAsync(ManagedSource))!;
        Assert.Equal((FolderFirstCheckStates.Passed, 10, 2), (check.State, check.Tried, check.Unmatched));
        Assert.Empty(await _service.ListAsync(CancellationToken.None));
        Assert.All(songs.Skip(2), song => Assert.Equal("waiting", MoveStep(song.Asset)));
        // Nothing moved at the moment the check passed: the files wait for the next run.
        Assert.All(songs, song => Assert.True(File.Exists(song.OldPath)));

        await Files.Scheduler.RunBatchAsync();

        foreach (var song in songs.Skip(2))
        {
            Assert.True(File.Exists(targets[song.Asset]));
            Assert.False(File.Exists(song.OldPath));
            Assert.Contains(Files.WriteBack.Calls, call => call.AssetId == song.Asset && call.PathAtTheTime == targets[song.Asset]);
        }

        Assert.All(songs.Take(2), song => Assert.True(File.Exists(song.OldPath)));
    }

    [Fact]
    public async Task AFolderWith8Files_6OfThemUnmatched_PausesOnceAll8AreDone_AndItsIngestionHasSettled()
    {
        var songs = await AddSongsAsync(8);
        await _checks.StartAsync(LibraryId, ManagedSource, Files.Managed, Now);

        await FinishAsync(songs.Take(7), matched: index => index >= 6);
        Assert.Equal(0, await _service.EvaluateAsync(CancellationToken.None));

        await FinishAsync(songs.Skip(7), matched: _ => false);
        // The last file is done, but another file of the folder is still being taken in.
        Files.Execute("""
            INSERT INTO media_operations(id,operation_type,operation_kind,source_path,status,position_key,created_at,updated_at,idempotency_key)
            VALUES(@id,'ingestion.file','ingestion',@path,'running',1,'2026-10-22T09:00:00Z','2026-10-22T09:00:00Z',@key);
            """, new { id = Guid.NewGuid(), path = Path.Combine(Files.Managed, "inbox", "coming.flac"), key = Guid.NewGuid().ToString("N") });
        Assert.Equal(0, await _service.EvaluateAsync(CancellationToken.None));

        Files.Execute("UPDATE media_operations SET status = 'completed';");
        Assert.Equal(1, await _service.EvaluateAsync(CancellationToken.None));

        var listed = Assert.Single(await _service.ListAsync(CancellationToken.None));
        Assert.Equal((FolderFirstCheckStates.Paused, 8, 7), (listed.Check.State, listed.Check.Tried, listed.Check.Unmatched));
    }

    [Fact]
    public async Task AFolderWith8FilesAndOnlyHalfUnmatched_Passes_BecauseItTakesMoreThanTheShare()
    {
        var songs = await AddSongsAsync(8);
        await _checks.StartAsync(LibraryId, ManagedSource, Files.Managed, Now);

        await FinishAsync(songs, matched: index => index % 2 == 0);
        await _service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(FolderFirstCheckStates.Passed, (await _checks.GetAsync(ManagedSource))!.State);
    }

    [Fact]
    public async Task TheThresholdsComeFromTheMaintenanceSettings()
    {
        Files.Configuration.SaveMaintenance(new MaintenanceSettings { FirstCheckFiles = 4, FirstCheckUnmatchedShare = 0.2 });
        var songs = await AddSongsAsync(10);
        await _checks.StartAsync(LibraryId, ManagedSource, Files.Managed, Now);

        // One of the first four is Unmatched: a quarter, over the 0.2 share. The other files have not been looked at.
        await FinishAsync(songs.Take(4), matched: index => index != 0);
        await _service.EvaluateAsync(CancellationToken.None);

        var check = (await _checks.GetAsync(ManagedSource))!;
        Assert.Equal((FolderFirstCheckStates.Paused, 4, 1), (check.State, check.Tried, check.Unmatched));
    }

    [Fact]
    public async Task AReadOnlyFolder_GetsTheCheckAndThePauseToo_WithNothingToHold()
    {
        var songs = await AddSongsAsync(8, Files.AsIs);
        await _checks.StartAsync(LibraryId, "as-is", Files.AsIs, Now);

        await FinishAsync(songs, matched: index => index >= 6);
        await _service.EvaluateAsync(CancellationToken.None);

        var listed = Assert.Single(await _service.ListAsync(CancellationToken.None));
        Assert.Equal(("as-is", FolderFirstCheckStates.Paused, 8, 6), (listed.Check.SourceId, listed.Check.State, listed.Check.Tried, listed.Check.Unmatched));
        // A read-only folder is never filed, so there is no filing row to hold; the files are where they were.
        Assert.Equal(0, Files.Scalar<int>("SELECT COUNT(*) FROM media_file_write_intents WHERE move_step IS NOT NULL;"));
        Assert.All(songs, song => Assert.True(File.Exists(song.OldPath)));
        Assert.Empty(await _jobs.LeaseNextAsync("worker", [IdentityJobState.Queued], 100, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task AYoutubeFileWithNoVideoId_CountsAsUnmatchedInTheCheck()
    {
        var songs = await AddSongsAsync(8);
        await _checks.StartAsync(LibraryId, ManagedSource, Files.Managed, Now);
        var marker = new MediaEngine.Ingestion.Services.UnmatchedFileMarker(
            _unmatched, Files.Configuration, NullLogger<MediaEngine.Ingestion.Services.UnmatchedFileMarker>.Instance, _checks);

        // Six files have no video ID and so are never looked up; the other two are matched.
        foreach (var song in songs.Take(6))
        {
            await marker.MarkNoYoutubeIdAsync(song.Asset, LibraryId, song.OldPath, CancellationToken.None);
        }

        await FinishAsync(songs.Skip(6), matched: _ => true);
        await _service.EvaluateAsync(CancellationToken.None);

        var check = (await _checks.GetAsync(ManagedSource))!;
        Assert.Equal((FolderFirstCheckStates.Paused, 8, 6), (check.State, check.Tried, check.Unmatched));
        Assert.Equal(UnmatchedReasons.NoYoutubeId, (await _unmatched.GetAsync(songs[0].Asset))!.Reason);
    }

    // ----- nothing is written into an Unmatched file, and held subtitles are exported when the hold ends ------------------------

    [Fact]
    public async Task AnUnmatchedFilesTags_StayPending_InAnyManagedFolder_AndAreWrittenOnTheNextRunOnceItIsIdentified()
    {
        var songs = await AddSongsAsync(2);

        // No first check runs here: the folder is an ordinary Managed folder.
        await FinishAsync(songs, matched: index => index == 1);
        await Files.Scheduler.RunBatchAsync();

        Assert.Equal([songs[1].Asset], Files.WriteBack.Calls.Select(call => call.AssetId).ToArray());
        Assert.Equal("pending", Files.Intent(songs[0].Asset).Status);

        // A lookup identifies it: the Unmatched row clears and the tags that were waiting are written in the next run.
        await _jobs.UpdateStateAsync(songs[0].Job, IdentityJobState.RetailMatched);
        await Files.Scheduler.RunBatchAsync();

        Assert.Contains(Files.WriteBack.Calls, call => call.AssetId == songs[0].Asset);
        Assert.Equal("verified", Files.Intent(songs[0].Asset).Status);
    }

    [Fact]
    public async Task AFileKeptAsItIs_GetsNoTagsEither_UntilALookIsAskedFor()
    {
        var songs = await AddSongsAsync(1);
        await FinishAsync(songs, matched: _ => false);
        await _unmatched.KeepAsIsAsync(songs[0].Asset, Now);

        await Files.Scheduler.RunBatchAsync();
        Assert.Empty(Files.WriteBack.Calls);

        await _unmatched.LookUpAgainAsync(songs[0].Asset, Now);
        await _jobs.UpdateStateAsync(songs[0].Job, IdentityJobState.RetailMatched);
        await Files.Scheduler.RunBatchAsync();

        Assert.Equal(songs[0].Asset, Assert.Single(Files.WriteBack.Calls).AssetId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASubtitleIsNotExportedBesideAFileInAHeldFolder_AndIsExportedWhenTheCheckPassesOrKeepGoingIsChosen(bool pausedThenKeepGoing)
    {
        var songs = await AddSongsAsync(pausedThenKeepGoing ? 8 : 1);
        Files.Configuration.SaveConfig(string.Empty, "writeback", new WriteBackConfiguration { Enabled = true });
        var tracks = new TextTrackRepository(Files.Database);
        var assets = new MediaAssetRepository(Files.Database);
        var resolver = new LibraryFolderResolver(new FixedMonitor(Files.Options));
        var export = new TextTrackExportService(
            resolver, new SourceMutationPolicyGate(Files.MergeLedger), Files.Configuration, NullLogger<TextTrackExportService>.Instance,
            new FolderFirstCheckFilingHold(_checks));
        var paths = new AssetPathService(Path.Combine(Files.Managed, ".library"), new LibraryStoragePolicy { SubtitleExport = true }, null);
        var exporter = new HeldSubtitleExporter(tracks, assets, export, paths, NullLogger<HeldSubtitleExporter>.Instance);
        var service = new FolderFirstCheckService(_checks, Files.Configuration, NullLogger<FolderFirstCheckService>.Instance, heldSubtitles: exporter);
        await _checks.StartAsync(LibraryId, ManagedSource, Files.Managed, Now);
        var song = songs[^1];
        var download = MoveFileWorld.WriteFile(Path.Combine(Files.Managed, ".library"), "downloads/subtitle.vtt", "WEBVTT");
        var track = new TextTrack
        {
            AssetId = song.Asset, Kind = TextTrackKind.Subtitles, Language = "en", Provider = "test", SourceFormat = "vtt",
            NormalizedFormat = "vtt", LocalPath = download, IsPreferred = true,
        };
        await tracks.UpsertAsync(track);
        var asset = (await assets.FindByIdAsync(song.Asset))!;
        var sidecar = AssetPathService.BuildSubtitleSidecarPath(song.OldPath, "en", ".vtt");

        // While the folder is held nothing is written beside the file.
        Assert.Null(await export.ExportPreferredSubtitleAsync(asset, track));
        Assert.False(File.Exists(sidecar));

        if (pausedThenKeepGoing)
        {
            await FinishAsync(songs, matched: index => index >= 6);
            await service.EvaluateAsync(CancellationToken.None);
            Assert.Equal(FolderFirstCheckStates.Paused, (await _checks.GetAsync(ManagedSource))!.State);
            Assert.False(File.Exists(sidecar));
            Assert.True(await service.KeepGoingAsync(ManagedSource, CancellationToken.None));
        }
        else
        {
            await FinishAsync(songs, matched: _ => true);
            Assert.Equal(1, await service.EvaluateAsync(CancellationToken.None));
        }

        // The hold ended: the subtitle is exported and remembered.
        Assert.True(File.Exists(sidecar));
        Assert.Equal(sidecar, (await tracks.FindByIdAsync(track.Id))!.SidecarPath);
    }

    // ----- which folders get a check -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("movies", true)]
    [InlineData("tv", true)]
    [InlineData("music", true)]
    [InlineData("books", true)]
    [InlineData("youtube", true)]
    [InlineData("", true)]
    [InlineData("other_videos", false)]
    [InlineData("other_audio", false)]
    [InlineData("a_kind_no_list_knows", false)]
    public void OnlyAKindThatLooksDetailsUp_GetsAFirstCheck(string kindKey, bool expected)
    {
        var library = new LibraryFolderConfig { Id = "lib", KindKey = kindKey };

        Assert.Equal(expected, FolderFirstCheckService.NeedsFirstCheck(library, LibraryKindsConfiguration.BuiltIn(), new HashSet<string>()));
    }

    [Fact]
    public void ALibraryThatKeepsIdentityLocal_OrIsInTheVault_NeverGetsAFirstCheck()
    {
        var local = new LibraryFolderConfig { Id = "lib", KindKey = "movies", MetadataPolicy = LibraryMetadataPolicies.LocalOnly };
        var vault = new LibraryFolderConfig { Id = "vault-lib", KindKey = "movies" };

        Assert.True(FolderFirstCheckService.NeedsFirstCheck(new LibraryFolderConfig { Id = "lib", KindKey = "movies" }, null, new HashSet<string>()));
        Assert.False(FolderFirstCheckService.NeedsFirstCheck(local, null, new HashSet<string>()));
        Assert.False(FolderFirstCheckService.NeedsFirstCheck(vault, null, new HashSet<string> { "vault-lib" }));
    }

    [Fact]
    public async Task OnlyAFolderAddedNow_StartsACheck_AFolderThatExistedBeforeNever_AndAPlainKindOrVaultFolderNever()
    {
        var existing = Source("existing", "D:/Media/Old");
        var before = Config(Library("lib", "movies", existing), Library("plain", "other_videos"), Library("vault-lib", "movies"));
        var after = Config(
            Library("lib", "movies", existing, Source("added", "D:/Media/New")),
            Library("plain", "other_videos", Source("plain-added", "D:/Media/Clips")),
            Library("vault-lib", "movies", Source("vault-added", "D:/Media/Hidden")));

        var started = await _service.StartForAddedFoldersAsync(before, after, new HashSet<string> { "vault-lib" }, CancellationToken.None);

        Assert.Equal(1, started);
        Assert.NotNull(await _checks.GetAsync("added"));
        Assert.Null(await _checks.GetAsync("existing"));
        Assert.Null(await _checks.GetAsync("plain-added"));
        Assert.Null(await _checks.GetAsync("vault-added"));
        Assert.Equal(0, await _service.StartForAddedFoldersAsync(after, after, new HashSet<string> { "vault-lib" }, CancellationToken.None));
    }

    [Fact]
    public async Task SavingTheLibraries_StartsTheCheckBeforeTheFileIsSaved_AndDropsItWhenTheSaveFails()
    {
        var before = Config(Library("lib", "movies", Source("existing", Files.Managed)));
        var after = Config(Library("lib", "movies", Source("existing", Files.Managed), Source("added", Files.AsIs)));
        var checkedBeforeSave = false;

        await Assert.ThrowsAsync<IOException>(() => _service.SaveLibrariesWithFirstChecksAsync(
            _ =>
            {
                checkedBeforeSave = _checks.IsHeld("added");
                throw new IOException("The disk is full.");
            },
            before, after, new HashSet<string>(), CancellationToken.None));

        Assert.True(checkedBeforeSave);
        Assert.Null(await _checks.GetAsync("added"));
    }

    // ----- removing the folder -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RemovingAFolder_DropsItsCheckAndHeldRows_AndMovesNoFile()
    {
        var songs = await AddSongsAsync(80);
        await _checks.StartAsync(LibraryId, ManagedSource, Files.Managed, Now);
        await FinishAsync(songs.Take(50), matched: index => index % 5 >= 3);
        await _service.EvaluateAsync(CancellationToken.None);
        Assert.Equal(20, Files.Scalar<int>("SELECT COUNT(*) FROM media_file_write_intents WHERE move_step = 'held';"));
        var before = Config(Library("lib", "music", Source(ManagedSource, Files.Managed)));
        var after = Config(Library("lib", "music"));

        var dropped = await _service.DropRemovedFoldersAsync(before, after, CancellationToken.None);

        Assert.Equal(1, dropped);
        Assert.Null(await _checks.GetAsync(ManagedSource));
        Assert.Empty(await _service.ListAsync(CancellationToken.None));
        Assert.Equal(0, Files.Scalar<int>("SELECT COUNT(*) FROM media_file_write_intents WHERE move_step IS NOT NULL;"));
        await Files.Scheduler.RunBatchAsync();
        Assert.All(songs, song => Assert.True(File.Exists(song.OldPath)));
        Assert.All(songs, song => Assert.Equal(song.OldPath, Files.StoredPath(song.Asset)));
    }

    [Fact]
    public async Task EndingACheckBecauseTheKindChanged_DropsItWithoutMovingAnything_SoANewOneCanStart()
    {
        var songs = await AddSongsAsync(8);
        await _checks.StartAsync(LibraryId, ManagedSource, Files.Managed, Now);
        await FinishAsync(songs, matched: index => index >= 6);
        await _service.EvaluateAsync(CancellationToken.None);
        Assert.Equal(FolderFirstCheckStates.Paused, (await _checks.GetAsync(ManagedSource))!.State);

        Assert.True(await _service.EndBecauseKindChangedAsync(ManagedSource, CancellationToken.None));

        Assert.Null(await _checks.GetAsync(ManagedSource));
        Assert.All(songs, song => Assert.True(File.Exists(song.OldPath)));
        Assert.True(await _checks.StartAsync(LibraryId, ManagedSource, Files.Managed, Now));
    }

    // ----- helpers -------------------------------------------------------------------------------------------------------------

    private sealed record Song(Guid Asset, Guid Job, string OldPath);

    private sealed class FixedMonitor(IngestionOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<IngestionOptions>
    {
        public IngestionOptions CurrentValue => value;

        public IngestionOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<IngestionOptions, string?> listener) => null;
    }

    private async Task<List<Song>> AddSongsAsync(int count, string? folder = null)
    {
        var songs = new List<Song>();
        for (var i = 0; i < count; i++)
        {
            songs.Add(await AddSongAsync(folder ?? Files.Managed));
        }

        return songs;
    }

    /// <summary>A song that sits in a folder but not where the library's pattern would put it, with its identity job queued and its tags asked for.</summary>
    private async Task<Song> AddSongAsync(string folder)
    {
        var number = ++_songs;
        var title = $"Song {number:000}";
        _album ??= _world.AddParent("Music", "The Best Of");
        if (_songs == 1)
        {
            _world.Put(_album.Value, "year", "2000");
        }

        var path = MoveFileWorld.WriteFile(folder, $"inbox/{number:000} {title}.flac", $"{title} audio bytes");
        var (_, _, assetId) = _world.AddChild(_album.Value, "Music", title, number, filePath: path);
        var asset = assetId!.Value;
        _world.Put(asset, "title", title);
        _world.Put(asset, "media_type", "Music");
        _world.Put(asset, "artist", "Radiohead");
        _world.Put(asset, "album", "The Best Of");
        _world.Put(asset, "track_number", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var job = new IdentityJob { EntityId = asset, EntityType = EntityType.MediaAsset.ToString(), MediaType = "Music", Pass = "Quick" };
        await _jobs.CreateAsync(job);
        // The pipeline asks for a file's tags as it takes the file in, before it is identified.
        await Files.Intents.EnqueueAssetsAsync([asset], "auto_match");
        return new Song(asset, job.Id, path);
    }

    /// <summary>Finishes the retail lookup of the songs, in order: a matched file asks to be filed, an unmatched one is marked Unmatched.</summary>
    private async Task FinishAsync(IEnumerable<Song> songs, Func<int, bool> matched)
    {
        var index = 0;
        foreach (var song in songs)
        {
            if (matched(index++))
            {
                await _jobs.UpdateStateAsync(song.Job, IdentityJobState.RetailMatched);
                await _organizer.TryAutoOrganizeAsync(song.Asset);
            }
            else
            {
                await _unmatched.MarkUnmatchedAsync(song.Asset, LibraryId, UnmatchedReasons.NoMatch, Now, Now.AddDays(7));
                await _jobs.UpdateStateAsync(song.Job, IdentityJobState.RetailNoMatch);
            }
        }
    }

    private async Task<string> ExpectedPathAsync(Guid asset)
    {
        var plan = await Files.Organizer.PlanRefileAsync(asset, null);
        Assert.Equal(AssetRefileDisposition.Moves, plan.Disposition);
        return plan.TargetPath!;
    }

    private string? MoveStep(Guid asset) =>
        Files.Scalar<string?>("SELECT move_step FROM media_file_write_intents WHERE asset_id = @asset;", new { asset });

    private static LibrarySourceConfig Source(string id, string path) =>
        new() { Id = id, Path = path, ManagementMode = LibrarySourceManagementModes.ManagedByTuvima, AccessMode = LibrarySourceAccessModes.Writable };

    private static LibraryFolderConfig Library(string id, string kindKey, params LibrarySourceConfig[] sources) =>
        new() { Id = id, KindKey = kindKey, Sources = [.. sources] };

    private static LibrariesConfiguration Config(params LibraryFolderConfig[] libraries) =>
        new() { Libraries = [.. libraries] };
}
