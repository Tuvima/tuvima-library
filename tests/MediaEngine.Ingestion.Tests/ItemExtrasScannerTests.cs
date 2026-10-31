using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Extras;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Tests.Helpers;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

public sealed class ItemExtrasScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_extras_scan_{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_extras_scan_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly ItemExtraRepository _extras;
    private readonly LibraryFolderEntry _library;
    private readonly ItemExtrasScanner _scanner;
    private readonly HashSet<string> _vaultLibraries = [];

    public ItemExtrasScannerTests()
    {
        Directory.CreateDirectory(_root);
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _extras = new ItemExtraRepository(_db);
        _library = new LibraryFolderEntry
        {
            Id = "tv",
            Name = "TV",
            MediaTypes = [MediaType.TV],
            Sources = [new LibrarySourceEntry { Id = "tv-1", Path = _root }],
        };
        var options = new OptionsMonitorStub<IngestionOptions>(new IngestionOptions { LibraryFolders = [_library] });
        _scanner = new ItemExtrasScanner(
            new LibraryFolderResolver(options),
            new MediaTypeExtensionCatalog(),
            _extras,
            new VaultHook(_vaultLibraries),
            options,
            NullLogger<ItemExtrasScanner>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AGuidebookPictureFolderAndClip_AreStoredAsThreeKindsOfExtra_AndNoEpisodeIsClaimed()
    {
        var episode = Touch("Show/Season 1/Show S01E01.mkv");
        Touch("Show/Season 1/Show S01E02.mkv");
        var guide = Touch("Show/Workbook.pdf");
        Touch("Show/Extras/Concept art/a.jpg");
        Touch("Show/Extras/Concept art/b.jpg");
        var clip = Touch("Show/Making of.mkv");

        Assert.True(await _scanner.ClaimsFileAsync(guide, _library));
        Assert.True(await _scanner.ClaimsFileAsync(clip, _library));
        Assert.False(await _scanner.ClaimsFileAsync(episode, _library));

        Assert.True(await _scanner.SyncItemFolderAsync(Path.Combine(_root, "Show")));
        var stored = await _extras.ListByItemFoldersAsync([Path.Combine(_root, "Show")]);

        Assert.Equal(4, stored.Count);
        Assert.Equal(1, stored.Count(extra => extra.Kind == ItemExtraKinds.Document));
        Assert.Equal(2, stored.Count(extra => extra.Kind == ItemExtraKinds.Picture));
        Assert.Equal(1, stored.Count(extra => extra.Kind == ItemExtraKinds.Clip));
    }

    [Fact]
    public async Task AFileTheScanQueued_IsCheckedByTheQueuePass_AndAFileThatLeavesIsRemoved()
    {
        Touch("Show/Season 1/Show S01E01.mkv");
        var guide = Touch("Show/Workbook.pdf");
        await _scanner.ClaimsFileAsync(guide, _library);
        Assert.Equal(1, await _scanner.SyncQueuedAsync());
        Assert.Single(await _extras.ListByItemFoldersAsync([Path.Combine(_root, "Show")]));

        File.Delete(guide);
        _scanner.NoteFile(guide);
        await _scanner.SyncQueuedAsync();

        Assert.Empty(await _extras.ListByItemFoldersAsync([Path.Combine(_root, "Show")]));
    }

    [Fact]
    public async Task AClipMovedToAnEpisode_IsPlacedInTheShowNamedByItsFolder()
    {
        Touch("Show (2024)/Season 1/Show S01E01.mkv");
        var clip = Touch("Show (2024)/Making of.mkv");

        var described = await _scanner.DescribeAsync(clip, ItemExtraOrigins.UserEpisode, 2, 5);
        Assert.NotNull(described);
        Assert.Equal(ItemExtraKinds.Clip, described.Kind);
        Assert.Equal(Path.Combine(_root, "Show (2024)"), described.ItemFolder);
        await _extras.SetDecisionAsync(described);

        var placement = await _scanner.GetEpisodePlacementAsync(clip);

        Assert.Equal(new ExtraEpisodePlacement("Show", "2024", 2, 5), placement);
        Assert.Null(await _scanner.GetEpisodePlacementAsync(Touch("Show (2024)/Other.mkv")));
    }

    [Fact]
    public async Task AFileOutsideAnyShowFolder_CannotBeDescribedAsAnExtra()
    {
        var loose = Touch("Loose clip.mkv");

        Assert.Null(await _scanner.DescribeAsync(loose, ItemExtraOrigins.UserExtra, null, null));
    }

    [Fact]
    public async Task APersonsDecision_DecidesTheClaim()
    {
        var clip = Touch("Show/Making of.mkv");
        Touch("Show/Season 1/Show S01E01.mkv");
        await _extras.SetDecisionAsync(Decision(clip, ItemExtraOrigins.UserEpisode));

        Assert.False(await _scanner.ClaimsFileAsync(clip, _library));

        var lesson = Touch("Show/Season 1/Show S01E02.mkv");
        await _extras.SetDecisionAsync(Decision(lesson, ItemExtraOrigins.UserExtra));

        Assert.True(await _scanner.ClaimsFileAsync(lesson, _library));
    }

    [Fact]
    public async Task AVaultLibrary_HasNoExtras()
    {
        var guide = Touch("Show/Workbook.pdf");
        Touch("Show/Season 1/Show S01E01.mkv");
        _vaultLibraries.Add(_library.Id);

        Assert.False(await _scanner.ClaimsFileAsync(guide, _library));
        Assert.False(await _scanner.SyncItemFolderAsync(Path.Combine(_root, "Show")));
        Assert.Empty(await _extras.ListByItemFoldersAsync([Path.Combine(_root, "Show")]));
    }

    [Fact]
    public async Task SyncAll_RemovesTheExtrasOfAFolderThatWasDeleted()
    {
        Touch("Show/Season 1/Show S01E01.mkv");
        Touch("Show/Workbook.pdf");
        await _scanner.SyncAllAsync();
        Assert.Single(await _extras.ListByItemFoldersAsync([Path.Combine(_root, "Show")]));

        Directory.Delete(Path.Combine(_root, "Show"), recursive: true);
        await _scanner.SyncAllAsync();

        Assert.Empty(await _extras.ListItemFoldersAsync());
    }

    [Fact]
    public async Task WhenTheShowsEpisodesAreDeleted_ItsExtrasRecordsGoWithIt_AndComeBackIfTheEpisodesReturn()
    {
        var episode = Touch("Show/Season 1/Show S01E01.mkv");
        Touch("Show/Workbook.pdf");
        await _scanner.SyncItemFolderAsync(Path.Combine(_root, "Show"));
        Assert.Single(await _extras.ListByItemFoldersAsync([Path.Combine(_root, "Show")]));

        File.Move(episode, episode + ".away");
        await _scanner.SyncItemFolderAsync(Path.Combine(_root, "Show"));
        Assert.Empty(await _extras.ListByItemFoldersAsync([Path.Combine(_root, "Show")]));

        File.Move(episode + ".away", episode);
        await _scanner.SyncItemFolderAsync(Path.Combine(_root, "Show"));
        Assert.Single(await _extras.ListByItemFoldersAsync([Path.Combine(_root, "Show")]));
    }

    [Fact]
    public async Task AnEpisodeCountIsUntouched_BecauseExtrasNeverEnterThePipeline()
    {
        Touch("Show/Season 1/Show S01E01.mkv");
        Touch("Show/Season 1/Show S01E02.mkv");
        var withoutExtras = Directory.GetFiles(Path.Combine(_root, "Show"), "*", SearchOption.AllDirectories)
            .Count(path => ClaimedAsEpisode(path));

        Touch("Show/Workbook.pdf");
        Touch("Show/Making of.mkv");
        var withExtras = Directory.GetFiles(Path.Combine(_root, "Show"), "*", SearchOption.AllDirectories)
            .Count(path => ClaimedAsEpisode(path));

        Assert.Equal(withoutExtras, withExtras);

        bool ClaimedAsEpisode(string path) =>
            Path.GetExtension(path) is ".mkv" && !_scanner.ClaimsFileAsync(path, _library).GetAwaiter().GetResult();
    }

    private ItemExtra Decision(string path, string origin) => new(
        Guid.NewGuid(), _library.Id, Path.GetDirectoryName(Path.GetDirectoryName(path)) is { } parent && parent.EndsWith("Show") ? parent : Path.Combine(_root, "Show"),
        path, ItemExtraKinds.Clip, origin, Path.GetFileNameWithoutExtension(path), null, 0, 4, null, null, DateTimeOffset.UtcNow);

    private string Touch(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[4]);
        return path;
    }

    private sealed class VaultHook(HashSet<string> vaultLibraries) : IVaultIngestionHook
    {
        public Task<bool> IsVaultLibraryAsync(string? libraryId, CancellationToken ct = default) =>
            Task.FromResult(libraryId is not null && vaultLibraries.Contains(libraryId));

        public Task<bool> IsVaultAssetAsync(Guid assetId, CancellationToken ct = default) => Task.FromResult(false);

        public Task OnFileRegisteredAsync(VaultRegisteredFile file, CancellationToken ct = default) => Task.CompletedTask;
    }
}
