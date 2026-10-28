using System.Text.Json.Nodes;
using MediaEngine.Api.Endpoints;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Tests;

public sealed class TvdbShowIdentityInheritanceTests
{
    private const string Series = """{"id":10,"defaultSeasonType":1,"seasons":[{"id":101,"number":1,"type":{"id":1}},{"id":201,"number":1,"type":{"id":2}},{"id":102,"number":2,"type":{"id":1}}]}""";
    private const string Episodes = """[{"id":1001,"seasonNumber":1,"number":1},{"id":1002,"seasonNumber":1,"number":2},{"id":2001,"seasonNumber":2,"number":1}]""";

    private readonly Guid _showId = Guid.NewGuid();
    private readonly Guid _seasonId = Guid.NewGuid();
    private readonly Guid _episodeId = Guid.NewGuid();
    private readonly StubCanonicals _canonicals = new();

    [Fact]
    public async Task BareSeasonOfAMatchedShow_UsesTheShowsTvdbIdentityToFindItsSeason()
    {
        MatchShow();
        var season = Scope("season", _seasonId);
        var own = Values(("season_number", "2"));

        var id = await Resolve(season, own);

        Assert.Equal("102", id);
    }

    [Fact]
    public async Task BareEpisodeOfAMatchedShow_FindsItsEpisodeInTvdbsDefaultOrder()
    {
        MatchShow();
        var episode = Scope("episode", _episodeId);
        var own = Values(("season_number", "1"), ("episode_number", "2"));

        var id = await Resolve(episode, own);

        Assert.Equal("1002", id);
    }

    [Fact]
    public async Task SeasonOfAnUnmatchedShow_GetsNoIdentityAndTheNewPlainWording()
    {
        var season = Scope("season", _seasonId);
        var own = Values(("season_number", "1"));

        Assert.Null(await Resolve(season, own));
        var message = await MetadataEndpoints.DescribeMissingProviderIdentityAsync(
            season, Scope("series", _showId), _canonicals, CancellationToken.None);

        Assert.Equal("This show is not matched to TheTVDB yet. Open Match and Identity to choose the show.", message);
    }

    [Fact]
    public async Task EpisodeThatCannotBePairedOnAMatchedShow_SaysSoInsteadOfAskingToMatchTheShow()
    {
        MatchShow();
        var episode = Scope("episode", _episodeId);
        var own = Values(("season_number", "9"), ("episode_number", "9"));

        Assert.Null(await Resolve(episode, own));
        var message = await MetadataEndpoints.DescribeMissingProviderIdentityAsync(
            episode, Scope("series", _showId), _canonicals, CancellationToken.None);

        Assert.Contains("no matching episode", message);
        Assert.DoesNotContain("not matched to TheTVDB", message);
    }

    [Fact]
    public async Task EpisodeWithoutItsOwnSeasonNumber_UsesTheSeasonsNumber()
    {
        MatchShow();
        var id = await Resolve(Scope("episode", _episodeId), Values(("episode_number", "1")), Values(("season_number", "2")));

        Assert.Equal("2001", id);
    }

    [Fact]
    public async Task SeasonAlreadyMatchedToAnotherProvider_DoesNotInheritTheShowsTvdbIdentity()
    {
        MatchShow();
        var season = Scope("season", _seasonId);
        var own = Values(("season_number", "1"), ("identity_provider", "tmdb"));

        Assert.Null(await Resolve(season, own));
    }

    [Fact]
    public void SeasonLookupIgnoresDvdAndOtherOrdersWithTheSameNumber()
    {
        Assert.Equal("101", MetadataEndpoints.FindTvdbSeasonId(JsonNode.Parse(Series), 1));
        Assert.Null(MetadataEndpoints.FindTvdbSeasonId(JsonNode.Parse(Series), 7));
    }

    private void MatchShow()
    {
        _canonicals.Add(_showId, "identity_provider", "tvdb");
        _canonicals.Add(_showId, "tvdb_id", "10");
    }

    private Task<string?> Resolve(MetadataEndpoints.EditorScopeResolution scope, IReadOnlyDictionary<string, string> own,
        IReadOnlyDictionary<string, string>? seasonValues = null) =>
        MetadataEndpoints.ResolveInheritedTvdbIdCoreAsync(scope, own, seasonValues ?? own, Scope("series", _showId), _canonicals,
            (_, _) => Task.FromResult<JsonNode?>(JsonNode.Parse(Series)),
            (_, _) => Task.FromResult<IReadOnlyList<JsonNode>>(JsonNode.Parse(Episodes)!.AsArray().Select(node => node!).ToList()),
            CancellationToken.None);

    private static IReadOnlyDictionary<string, string> Values(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static MetadataEndpoints.EditorScopeResolution Scope(string scopeId, Guid entityId) =>
        new(scopeId, scopeId, 0, entityId, "Work", null, null, scopeId, null, scopeId, scopeId, null, null,
            true, true, "TV", null, null);

    private sealed class StubCanonicals : ICanonicalValueRepository
    {
        private readonly List<CanonicalValue> _values = [];

        public void Add(Guid entityId, string key, string value) =>
            _values.Add(new CanonicalValue { EntityId = entityId, Key = key, Value = value, LastScoredAt = DateTimeOffset.UtcNow });

        public Task<IReadOnlyList<CanonicalValue>> GetByEntityAsync(Guid entityId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CanonicalValue>>(_values.Where(value => value.EntityId == entityId).ToList());

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>> GetByEntitiesAsync(
            IReadOnlyList<Guid> entityIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>>(entityIds.ToDictionary(
                id => id, id => (IReadOnlyList<CanonicalValue>)_values.Where(value => value.EntityId == id).ToList()));

        public Task UpsertBatchAsync(IReadOnlyList<CanonicalValue> values, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CanonicalValue>> GetConflictedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CanonicalValue>>([]);
        public Task DeleteByKeyAsync(Guid entityId, string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Guid>> FindByValueAsync(string key, string value, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<IReadOnlyList<CanonicalValue>> FindByKeyAndPrefixAsync(string key, string prefix, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CanonicalValue>>([]);
        public Task<IReadOnlyList<Guid>> GetEntitiesNeedingEnrichmentAsync(string hasField, string missingField, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }
}
