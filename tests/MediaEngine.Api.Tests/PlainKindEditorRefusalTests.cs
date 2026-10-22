using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The places a person can ask for outside help for one item: album and series suggestions, applying a retail match, and the
/// enrichment refresh. For an item in a library that never looks anything up (a plain kind) each refuses before any provider is
/// contacted or any provider claim is written (Library setup D38).
/// </summary>
public sealed class PlainKindEditorRefusalTests : IDisposable
{
    private const string PlainLibrary = "abababab-0000-4000-8000-000000000001";
    private const string MusicLibrary = "abababab-0000-4000-8000-000000000002";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-editor-plain-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly (Guid Work, Guid Asset) _plain;
    private readonly (Guid Work, Guid Asset) _music;
    private readonly LibraryGuard _guard = new(PlainLibrary);

    public PlainKindEditorRefusalTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _plain = Seed(PlainLibrary);
        _music = Seed(MusicLibrary);
        _guard.Map(_plain.Asset, PlainLibrary);
        _guard.Map(_plain.Work, PlainLibrary);
        _guard.Map(_music.Asset, MusicLibrary);
        _guard.Map(_music.Work, MusicLibrary);
    }

    public void Dispose()
    {
        _database.Dispose();
        DatabaseConnection.ReleasePooledConnections(_databasePath);
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task AlbumSuggestionsFromRetail_ForAnItemInAPlainKindLibrary_NeverAskAProvider_AndStayLocal()
    {
        var search = new RecordingSearch();
        var service = new MediaEditorNavigationReadService(_database, search, null!, _guard);

        var suggestions = await service.GetSuggestionsAsync(_plain.Asset, "album", "Abbey", "retail", null, null, CancellationToken.None);

        Assert.Equal(0, search.RetailSearches);
        Assert.All(suggestions, suggestion => Assert.NotEqual("retail", suggestion.Source));
    }

    [Fact]
    public async Task AlbumSuggestionsFromRetail_ForAnItemInAMusicLibrary_StillAskTheProvider()
    {
        var search = new RecordingSearch();
        var service = new MediaEditorNavigationReadService(_database, search, null!, _guard);

        await service.GetSuggestionsAsync(_music.Asset, "album", "Abbey", "retail", null, null, CancellationToken.None);

        Assert.Equal(1, search.RetailSearches);
    }

    [Fact]
    public async Task ApplyingARetailMatch_ToAnItemInAPlainKindLibrary_IsRefusedWithAPlainSentence_AndAMusicItemIsNot()
    {
        var refused = await ItemCanonicalEndpoints.RefuseInLibraryThatNeverLooksAnythingUpAsync(_guard, _plain.Asset, CancellationToken.None);
        var allowed = await ItemCanonicalEndpoints.RefuseInLibraryThatNeverLooksAnythingUpAsync(_guard, _music.Asset, CancellationToken.None);

        Assert.NotNull(refused);
        Assert.Null(allowed);
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        await refused.ExecuteAsync(http);
        Assert.Equal(StatusCodes.Status409Conflict, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync();
        Assert.Contains("never looks anything up", body, StringComparison.Ordinal);
        Assert.Contains("Change what this folder is", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRetailMatchRoute_RefusesBeforeItValidatesOrWritesAnything()
    {
        var source = File.ReadAllText(RepoFile("src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs"));
        var route = source[source.IndexOf("/{entityId:guid}/retail-match\"", StringComparison.Ordinal)..];
        route = route[..route.IndexOf("/{entityId:guid}/wikidata-match", StringComparison.Ordinal)];

        var refusal = route.IndexOf("RefuseInLibraryThatNeverLooksAnythingUpAsync", StringComparison.Ordinal);
        Assert.True(refusal >= 0);
        Assert.True(refusal < route.IndexOf("claimRepo.InsertBatchAsync", StringComparison.Ordinal));
        Assert.True(refusal < route.IndexOf("pipeline.EnqueueAsync", StringComparison.Ordinal));
        Assert.True(refusal < route.IndexOf("tvdb.GetAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSearchAndPreviewRoutes_RefuseBeforeTheyAskAnyProvider()
    {
        var source = File.ReadAllText(RepoFile("src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs"));

        var search = source[source.IndexOf("/{entityId:guid}/canonical-search\"", StringComparison.Ordinal)..];
        search = search[..search.IndexOf("/{entityId:guid}/canonical-apply", StringComparison.Ordinal)];
        var searchRefusal = search.IndexOf("RefuseInLibraryThatNeverLooksAnythingUpAsync", StringComparison.Ordinal);
        Assert.True(searchRefusal >= 0);
        Assert.True(searchRefusal < search.IndexOf("retailMatchPreview.SearchAsync", StringComparison.Ordinal));
        Assert.True(searchRefusal < search.IndexOf("wikidataMatchPreview.", StringComparison.Ordinal));

        var preview = source[source.IndexOf("/{entityId:guid}/retail-match-preview\"", StringComparison.Ordinal)..];
        preview = preview[..preview.IndexOf("/{entityId:guid}/retail-match\"", StringComparison.Ordinal)];
        var previewRefusal = preview.IndexOf("RefuseInLibraryThatNeverLooksAnythingUpAsync", StringComparison.Ordinal);
        Assert.True(previewRefusal >= 0);
        Assert.True(previewRefusal < preview.IndexOf("VerifyTvdbEpisodeMoveAsync(", StringComparison.Ordinal));
        Assert.True(previewRefusal < preview.IndexOf("VerifyProviderParentChildRelationAsync(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheScheduledRefresh_SkipsAWorkInAPlainKindLibrary_InsteadOfQueuingItEveryCycle()
    {
        var hydration = new RecordingHydration();
        var service = new EnrichmentRefreshScheduleService(
            _database,
            new ConfigurationDirectoryLoader(Path.Combine(Path.GetTempPath(), $"tuvima-editor-plain-config-{Guid.NewGuid():N}")),
            null!,
            new CanonicalValueRepository(_database),
            null!,
            hydration,
            new SystemActivityRepository(_database),
            _guard);

        var skipped = await service.QueueNowAsync("Work", _plain.Work, "Scheduled");
        var skippedByFile = await service.QueueNowAsync("MediaAsset", _plain.Asset, "Scheduled");
        var queued = await service.QueueNowAsync("Work", _music.Work, "Scheduled");

        Assert.Null(skipped);
        Assert.Null(skippedByFile);
        Assert.NotNull(queued);
        Assert.Equal([_music.Work], hydration.Enqueued);
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM enrichment_refresh_schedule WHERE entity_id = @id", new { id = _plain.Work }));
    }

    private (Guid Work, Guid Asset) Seed(string libraryId)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow.ToString("O");
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind) VALUES (@work, 'Music', 'standalone');
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id, presented_at)
            VALUES (@asset, @edition, @hash, @path, 'Normal', @libraryId, @stamp);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@asset, 'title', 'Come Together', @stamp);
            """,
            new { work, edition, asset, libraryId, stamp, hash = asset.ToString("N"), path = $"music/{asset:N}.flac" });
        return (work, asset);
    }

    private static string RepoFile(string relative, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, relative);
    }

    private sealed class LibraryGuard(params string[] blockedLibraries) : IExternalLookupGuard
    {
        private readonly Dictionary<Guid, string> _libraryOf = [];

        public void Map(Guid id, string libraryId) => _libraryOf[id] = libraryId;

        public bool BlocksLibrary(string? libraryId) => libraryId is not null && blockedLibraries.Contains(libraryId);

        public IReadOnlyCollection<string> GetBlockedLibraryIds() => blockedLibraries;

        public Task<bool> BlocksEntityAsync(Guid entityId, CancellationToken ct = default) =>
            Task.FromResult(_libraryOf.TryGetValue(entityId, out var library) && BlocksLibrary(library));
    }

    private sealed class RecordingSearch : ISearchService
    {
        public int RetailSearches { get; private set; }

        public Task<SearchUniverseResult> SearchUniverseAsync(SearchUniverseRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("A Wikidata search is not part of these suggestions.");

        public Task<SearchRetailResult> SearchRetailAsync(SearchRetailRequest request, CancellationToken ct = default)
        {
            RetailSearches++;
            return Task.FromResult(new SearchRetailResult([], request.Query, request.MediaType));
        }
    }

    private sealed class RecordingHydration : IHydrationPipelineService
    {
        public List<Guid> Enqueued { get; } = [];

        public ValueTask<Guid> EnqueueAsync(HarvestRequest request, CancellationToken ct = default)
        {
            Enqueued.Add(request.EntityId);
            return ValueTask.FromResult(Guid.NewGuid());
        }

        public Task<HydrationResult> RunSynchronousAsync(HarvestRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("The refresh only queues work.");
    }
}
