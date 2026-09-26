using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Search;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class AudiobookSeriesDetailTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public AudiobookSeriesDetailTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_audiobook_series_detail_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    [Fact]
    public async Task BuildAsync_ResolvesDynamicAudiobookSeriesOnSharedCollectionDetail()
    {
        var rootWorkId = Guid.NewGuid();
        var firstWorkId = Guid.NewGuid();
        var secondWorkId = Guid.NewGuid();
        var firstAssetId = Guid.NewGuid();
        var secondAssetId = Guid.NewGuid();
        var group = new ContentGroupDto
        {
            CollectionId = Guid.NewGuid(),
            RootWorkId = rootWorkId,
            DisplayName = "The Expanse",
            PrimaryMediaType = "Audiobooks",
            Creator = "James S. A. Corey",
            WorkCount = 2,
            PreviewItems =
            [
                new(firstWorkId, "Leviathan Wakes", $"/stream/{firstAssetId:D}/cover", "portrait", "1"),
                new(secondWorkId, "Caliban's War", $"/stream/{secondAssetId:D}/cover", "portrait", "2"),
            ],
        };
        var browse = new FakeCollectionBrowseReadService(
            group,
            [
                new CollectionSystemViewDetailWorkReadModel
                {
                    WorkId = firstWorkId,
                    AssetId = firstAssetId,
                    RootWorkId = rootWorkId,
                    Title = "Leviathan Wakes",
                    Series = "The Expanse",
                    SeriesIndex = "1",
                    Author = "James S. A. Corey",
                    YearValue = "2011",
                    DurationSecondsValue = "72000",
                },
                new CollectionSystemViewDetailWorkReadModel
                {
                    WorkId = secondWorkId,
                    AssetId = secondAssetId,
                    RootWorkId = rootWorkId,
                    Title = "Caliban's War",
                    Series = "The Expanse",
                    SeriesIndex = "2",
                    Author = "James S. A. Corey",
                    YearValue = "2012",
                    DurationSecondsValue = "75600",
                },
            ]);
        var routeId = SystemViewGroupIdentity.CreateId(group, "Audiobooks", "series");
        var composer = new DetailComposerService(
            _db,
            new LibraryItemRepository(_db),
            new PersonRepository(_db),
            new EntityAssetRepository(_db),
            new CanonicalValueArrayRepository(_db),
            new SeriesManifestRepository(_db),
            null!,
            new DetailRecommendationService(_db),
            collectionBrowse: browse);

        var detail = await composer.BuildAsync(
            DetailEntityType.Collection,
            routeId,
            DetailPresentationContext.Listen);

        Assert.NotNull(detail);
        Assert.Equal(routeId.ToString("D"), detail.Id);
        Assert.Equal(DetailEntityType.Collection, detail.EntityType);
        Assert.Equal("The Expanse", detail.Title);
        Assert.Empty(detail.Facts?.Authors ?? []);
        Assert.Empty(detail.ContributorGroups);
        Assert.NotNull(detail.EditorTarget);
        Assert.Equal(rootWorkId.ToString("D"), detail.EditorTarget.EntityId);
        Assert.Equal("StructuralCollection", detail.EditorTarget.EntityKind);

        var items = Assert.Single(detail.MediaGroups).Items;
        Assert.Equal(["Leviathan Wakes", "Caliban's War"], items.Select(item => item.Title));
        Assert.All(items, item => Assert.Equal(DetailEntityType.Audiobook, item.EntityType));
        Assert.All(items, item => Assert.Contains("/details/work/", item.Actions.Single().Route));
        Assert.Equal(("series", "The Expanse", "Audiobooks", "James S. A. Corey"), browse.DetailRequest);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    private sealed class FakeCollectionBrowseReadService(
        ContentGroupDto group,
        IReadOnlyList<CollectionSystemViewDetailWorkReadModel> works) : ICollectionBrowseReadService
    {
        public (string GroupField, string GroupValue, string? MediaType, string? Creator)? DetailRequest { get; private set; }

        public Task<List<CollectionDto>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult(new List<CollectionDto>());

        public Task<Guid?> GetRootWorkIdAsync(Guid workId, CancellationToken ct) =>
            Task.FromResult<Guid?>(null);

        public Task<Guid?> GetRepresentativeAssetIdAsync(Guid workId, CancellationToken ct) =>
            Task.FromResult<Guid?>(null);

        public Task<Dictionary<Guid, Guid?>> GetPrimaryAssetIdsAsync(
            IEnumerable<Guid> workIds,
            CancellationToken ct) =>
            Task.FromResult(new Dictionary<Guid, Guid?>());

        public Task<CollectionPaletteReadModel?> GetAssetPaletteAsync(Guid entityId, CancellationToken ct) =>
            Task.FromResult<CollectionPaletteReadModel?>(null);

        public Task<IReadOnlyList<CollectionArtistWorkReadModel>> GetArtistWorksAsync(
            string artistName,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CollectionArtistWorkReadModel>>([]);

        public Task<IReadOnlyList<CollectionSystemViewDetailWorkReadModel>> GetSystemViewDetailWorksAsync(
            string groupField,
            string groupValue,
            string? mediaType,
            string? artistName,
            CancellationToken ct, Guid? rootWorkId = null)
        {
            DetailRequest = (groupField, groupValue, mediaType, artistName);
            return Task.FromResult(works);
        }

        public IReadOnlyList<Guid> EvaluateRules(
            CollectionRuleDefinition definition,
            string? sortField = null,
            string sortDirection = "desc",
            int limit = 0,
            string? query = null,
            string? secondarySortField = null,
            string? secondarySortDirection = null) =>
            [];

        public int CountRuleMatches(CollectionRuleDefinition definition, string? query = null) => 0;

        public Task<IReadOnlyList<string>> GetFieldValuesAsync(
            string field,
            string? query,
            int limit,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<List<ContentGroupDto>> GetSystemViewGroupsAsync(
            string? mediaType,
            string? groupField,
            CancellationToken ct,
            IReadOnlySet<Guid>? allowedWorkIds = null) =>
            Task.FromResult(
                string.Equals(mediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase)
                && string.Equals(groupField, "series", StringComparison.OrdinalIgnoreCase)
                    ? new List<ContentGroupDto> { group }
                    : []);
    }
}
