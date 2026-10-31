using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Moving a file between a show's Extras and its episodes in the editor (Extras spec WP4): the check says why a move cannot happen, an
/// Extra moved to an episode stops being an Extra and is queued for ingestion as that episode, and an episode moved to Extras keeps its
/// file and loses only its library records.
/// </summary>
public sealed class ExtraMoveEditSessionChangeHandlerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_extra_move_{Guid.NewGuid():N}");
    private readonly string _show;
    private readonly string _clip;
    private readonly string _guide;
    private readonly string _episodeFile;
    private readonly DatabaseConnection _database;
    private readonly ItemExtraRepository _extras;
    private readonly DeletionSpy _titles;
    private readonly ScannerSpy _scanner = new();
    private readonly IngestionSpy _ingestion = new();
    private readonly ExtraMoveEditSessionChangeHandler _handler;
    private readonly Guid _showId = Guid.NewGuid();
    private readonly Guid _episodeId = Guid.NewGuid();

    public ExtraMoveEditSessionChangeHandlerTests()
    {
        DapperConfiguration.Configure();
        _show = Path.Combine(_root, "Masterclass");
        Directory.CreateDirectory(Path.Combine(_show, "Season 01"));
        _clip = Path.Combine(_show, "Behind the scenes.mp4");
        _guide = Path.Combine(_show, "Workbook.pdf");
        _episodeFile = Path.Combine(_show, "Season 01", "Lesson 1.mkv");
        foreach (var file in new[] { _clip, _guide, _episodeFile })
        {
            File.WriteAllText(file, "x");
        }

        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _extras = new ItemExtraRepository(_database);
        _titles = new DeletionSpy(_showId, _episodeId, _episodeFile);
        _scanner.Described = Extra(_episodeFile, ItemExtraKinds.Clip, ItemExtraOrigins.UserExtra);
        _handler = new ExtraMoveEditSessionChangeHandler(
            _extras, _scanner, _titles, _ingestion, NullLogger<ExtraMoveEditSessionChangeHandler>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task AClipMovedToAnEpisode_StopsBeingAnExtra_AndIsQueuedForIngestion()
    {
        var clip = await SeedAsync(_clip, ItemExtraKinds.Clip);

        var change = ToEpisode(clip.Id, season: 1, episode: 2);
        Assert.Null(await _handler.CheckAsync(change));
        var undo = await _handler.ApplyAsync(change);

        Assert.Null(undo);
        Assert.Empty(await _extras.ListByItemFoldersAsync([Path.GetFullPath(_show)]));
        var decided = await _extras.GetByPathAsync(Path.GetFullPath(_clip));
        Assert.Equal(ItemExtraOrigins.UserEpisode, decided!.Origin);
        Assert.Equal((1, 2), (decided.TargetSeason, decided.TargetEpisode));
        Assert.Equal([Path.GetDirectoryName(Path.GetFullPath(_clip))!], _ingestion.Scanned);
    }

    [Fact]
    public async Task AGuidebook_CannotBecomeAnEpisode()
    {
        var guide = await SeedAsync(_guide, ItemExtraKinds.Document);

        Assert.Equal(ExtraMoveEditSessionChangeHandler.OnlyClips, await _handler.CheckAsync(ToEpisode(guide.Id, 1, 1)));
    }

    [Fact]
    public async Task AMoveIsRefused_WhenTheNumbersAreWrong_TheFileIsGone_OrTheExtraBelongsToAnotherShow()
    {
        var clip = await SeedAsync(_clip, ItemExtraKinds.Clip);

        Assert.Equal(ExtraMoveEditSessionChangeHandler.BadNumbers, await _handler.CheckAsync(ToEpisode(clip.Id, 1, 0)));
        Assert.Equal(ExtraMoveEditSessionChangeHandler.ExtraGone, await _handler.CheckAsync(ToEpisode(Guid.NewGuid(), 1, 1)));
        Assert.Equal(ExtraMoveEditSessionChangeHandler.NotThisShow, await _handler.CheckAsync(ToEpisode(clip.Id, 1, 1, Guid.NewGuid())));

        File.Delete(_clip);
        Assert.Equal(ExtraMoveEditSessionChangeHandler.FileMissing, await _handler.CheckAsync(ToEpisode(clip.Id, 1, 1)));
    }

    [Fact]
    public async Task AnEpisodeMovedToExtras_KeepsItsFile_AndLosesItsLibraryRecords()
    {
        var change = ToExtras(_episodeId);

        Assert.Null(await _handler.CheckAsync(change));
        await _handler.ApplyAsync(change);

        Assert.True(File.Exists(_episodeFile));
        Assert.Equal([_episodeId], _titles.Deleted);
        var decided = await _extras.GetByPathAsync(Path.GetFullPath(_episodeFile));
        Assert.Equal(ItemExtraOrigins.UserExtra, decided!.Origin);
        Assert.Equal([Path.GetFullPath(_show)], _scanner.Synced);
    }

    [Fact]
    public async Task AnEpisodeWithSeveralFiles_CannotMoveToExtras()
    {
        _titles.AssetCount = 2;

        Assert.Equal(ExtraMoveEditSessionChangeHandler.OneFileOnly, await _handler.CheckAsync(ToExtras(_episodeId)));
    }

    private EditSessionChange ToEpisode(Guid extraId, int season, int episode, Guid? showId = null) => new()
    {
        Id = "c1",
        Kind = EditSessionChangeKinds.ExtraMove,
        EntityId = showId ?? _showId,
        TargetId = extraId.ToString("D"),
        Token = $"episode:{season}:{episode}",
    };

    private static EditSessionChange ToExtras(Guid episodeId) => new()
    {
        Id = "c2",
        Kind = EditSessionChangeKinds.ExtraMove,
        EntityId = episodeId,
        Token = EditSessionExtraMove.ToExtrasToken,
    };

    private async Task<ItemExtra> SeedAsync(string path, string kind)
    {
        var extra = Extra(path, kind, ItemExtraOrigins.Rule) with { Origin = ItemExtraOrigins.UserExtra };
        await _extras.SetDecisionAsync(extra);
        return extra;
    }

    private ItemExtra Extra(string path, string kind, string origin) => new(
        Guid.NewGuid(), "lib", Path.GetFullPath(_show), Path.GetFullPath(path), kind, origin,
        Path.GetFileNameWithoutExtension(path), null, 0, 1, null, null, DateTimeOffset.UtcNow);

    private sealed class ScannerSpy : IItemExtrasScanner
    {
        public ItemExtra? Described { get; set; }
        public List<string> Synced { get; } = [];

        public Task<ItemExtra?> DescribeAsync(string filePath, string origin, int? targetSeason, int? targetEpisode, CancellationToken ct = default) =>
            Task.FromResult(Described is null ? null : Described with { Origin = origin });

        public Task<bool> SyncItemFolderAsync(string itemFolder, CancellationToken ct = default)
        {
            Synced.Add(itemFolder);
            return Task.FromResult(true);
        }

        public Task<ExtraEpisodePlacement?> GetEpisodePlacementAsync(string filePath, CancellationToken ct = default) => Task.FromResult<ExtraEpisodePlacement?>(null);
        public Task<bool> ClaimsFileAsync(string filePath, LibraryFolderEntry? library, CancellationToken ct = default) => Task.FromResult(false);
        public void NoteFile(string filePath) { }
        public Task<int> SyncQueuedAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> SyncAllAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class DeletionSpy(Guid showId, Guid episodeId, string episodeFile) : ILibraryItemDeletionRepository
    {
        public int AssetCount { get; set; } = 1;
        public List<Guid> Deleted { get; } = [];

        public Task<IReadOnlyList<LibraryItemDeletionWork>> GetWorksAsync(IReadOnlyList<Guid> workIds, CancellationToken ct = default, bool vaultItems = false)
        {
            var works = workIds.Where(id => id == episodeId).Select(id => new LibraryItemDeletionWork(
                id, "episode", false, "TV", "Lesson 1",
                [.. Enumerable.Range(0, AssetCount).Select(_ => new LibraryItemDeletionAsset(Guid.NewGuid(), episodeFile, "hash"))])).ToList();
            return Task.FromResult<IReadOnlyList<LibraryItemDeletionWork>>(works);
        }

        public Task<LibraryItemDeletionGroup?> ResolveGroupAsync(Guid groupId, CancellationToken ct = default) =>
            Task.FromResult(groupId == showId
                ? new LibraryItemDeletionGroup(showId, "show", "Masterclass", "TV", [episodeId], 0, [], null, 0)
                : null);

        public Task CancelProcessingAsync(IReadOnlyList<Guid> workIds, CancellationToken ct = default) => Task.CompletedTask;

        public Task<LibraryItemDeletionDataResult> DeleteWorksAsync(IReadOnlyList<Guid> workIds, bool keepPersonalState = false, CancellationToken ct = default)
        {
            Deleted.AddRange(workIds);
            return Task.FromResult(new LibraryItemDeletionDataResult([], [], [], [], new HashSet<string>()));
        }

        public Task<LibraryItemDeletionRelatedFacts> GetRelatedFactsAsync(IReadOnlyList<Guid> workIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> FindPathsUsedOutsideRequestAsync(IReadOnlyList<Guid> workIds, IReadOnlyCollection<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DiscardKeptProgressAsync(DateTimeOffset? olderThan, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class IngestionSpy : IIngestionEngine
    {
        public List<string> Scanned { get; } = [];

        public Task ScanDirectory(string directory, bool includeSubdirectories = true, CancellationToken ct = default)
        {
            Scanned.Add(directory);
            return Task.CompletedTask;
        }

        public Task EnqueueIntakeAsync(IntakeFileRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FileMetadataRereadResult> RereadAssetMetadataAsync(Guid assetId, CancellationToken ct = default) => throw new NotSupportedException();
        public void Start() => throw new NotSupportedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PendingOperation>> DryRunAsync(string rootPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ScanDirectories(IReadOnlyList<IngestionScanTarget> targets, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RefreshImportFolderAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task PauseWatcherAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ResumeWatcherAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
