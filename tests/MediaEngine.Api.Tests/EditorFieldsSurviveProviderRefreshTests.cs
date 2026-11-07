using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Locks by default (editor v5 D5, A3). The Details tab keeps a value the person changed as their own display override, stored beside
/// the provider's facts and never competing with them, so a provider refresh cannot replace it. These tests run the real save (the edit
/// session's details handler) and then a real refresh from a hand-written provider (the same persist-and-score step every enrichment
/// uses) on a temp SQLite data store, for every kind of field the Details tab offers: short text, long text, dates, numbers, a year and
/// a list. The person's field keeps their value; a field they never changed takes the provider's new one.
/// </summary>
public sealed class EditorFieldsSurviveProviderRefreshTests : IDisposable
{
    // One entry per kind of single-valued field the Details tab offers on a film: short text, long text, a date, numbers, a year and a rating.
    // Genres and language are lists in the data store and have their own test below.
    private static readonly (string Key, string First, string Refreshed, string Yours)[] Fields =
    [
        ("title", "Dune", "Dune: Part One", "Dune (my cut)"),
        ("original_title", "Dune", "Dune (2021)", "Duna"),
        ("description", "A noble family.", "A noble family becomes embroiled in a war.", "My own synopsis."),
        ("tagline", "Beyond fear.", "Fear is the mind-killer.", "My own tagline."),
        ("release_date", "2021-09-15", "2021-10-22", "2021-12-01"),
        ("year", "2021", "2022", "2020"),
        ("runtime", "150", "155", "140"),
        ("rating", "7.9", "8.1", "9.5"),
        ("content_rating", "PG-13", "PG-15", "R"),
    ];

    // The fields the person changes; every other field in the list above is left to the provider.
    private static readonly HashSet<string> Changed = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "description", "release_date", "runtime", "rating",
    };

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-editor-refresh-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly ItemCanonicalRepository _items;
    private readonly MetadataClaimRepository _claims;
    private readonly CanonicalValueRepository _canonicals;
    private readonly CanonicalValueArrayRepository _arrays;
    private readonly PriorityCascadeEngine _scoring;
    private readonly DetailsEditSessionChangeHandler _handler;
    private readonly Guid _provider = WellKnownProviders.Tmdb;

    public EditorFieldsSurviveProviderRefreshTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _items = new ItemCanonicalRepository(_db, NullLogger<ItemCanonicalRepository>.Instance);
        _claims = new MetadataClaimRepository(_db);
        _canonicals = new CanonicalValueRepository(_db);
        _arrays = new CanonicalValueArrayRepository(_db);
        _scoring = new PriorityCascadeEngine(new NoConfigurationLoader(), NullLogger<PriorityCascadeEngine>.Instance);
        _handler = new DetailsEditSessionChangeHandler(_items, new SystemActivityRepository(_db));
        EnsureProviderRow(_provider);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* test cleanup: the temp file goes next */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }

    [Fact]
    public void EveryFieldTheTestsChange_IsOneTheEngineAcceptsAsADisplayOverride()
    {
        foreach (var field in Fields)
        {
            Assert.True(DetailDisplayOverrideCatalog.TryValidateValue(field.Key, field.Yours, out _, out var error), $"{field.Key}: {error}");
        }

        Assert.True(DetailDisplayOverrideCatalog.TryValidateValue("genre", "Folk; Nordic", out _, out _));
        Assert.True(DetailDisplayOverrideCatalog.TryValidateValue("custom_tags", "epic; sandworms", out _, out _));
    }

    [Fact]
    public async Task AChangedField_KeepsThePersonsValue_AfterAProviderRefresh_WhileAnUnchangedFieldTakesTheNewProviderValue()
    {
        var work = SeedWork();
        await RefreshFromProviderAsync(work, Fields.Select(field => (field.Key, field.First)), genres: ["Science Fiction", "Adventure"]);
        await SaveTheEditAsync(work);

        await RefreshFromProviderAsync(work, Fields.Select(field => (field.Key, field.Refreshed)), genres: ["Drama"]);

        var overrides = new Dictionary<string, string>((await _items.LoadDisplayOverridesAsync(work)).Values, StringComparer.OrdinalIgnoreCase);
        var canonical = (await _canonicals.GetByEntityAsync(work)).ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            // The provider's fact is refreshed either way; it is only what the person sees that differs.
            Assert.Equal(field.Refreshed, canonical[field.Key]);
            var shown = overrides.TryGetValue(field.Key, out var mine) ? mine : canonical[field.Key];
            if (Changed.Contains(field.Key))
            {
                Assert.Equal(field.Yours, overrides[field.Key]);
                Assert.Equal(field.Yours, shown);
            }
            else
            {
                Assert.False(overrides.ContainsKey(field.Key), $"{field.Key} was never changed, so it holds no override of its own.");
                Assert.Equal(field.Refreshed, shown);
            }
        }
    }

    [Fact]
    public async Task AChangedList_AndTheLibraryTags_KeepTheirValue_AfterAProviderRefresh()
    {
        var work = SeedWork();
        await RefreshFromProviderAsync(work, [("title", "Dune"), ("language", "en")], genres: ["Science Fiction", "Adventure"]);
        await SaveTheEditAsync(work);

        await RefreshFromProviderAsync(work, [("title", "Dune: Part One"), ("language", "fr")], genres: ["Drama"]);

        var overrides = (await _items.LoadDisplayOverridesAsync(work)).Values;
        Assert.Equal("Folk; Nordic", overrides["genre"]);
        Assert.Equal("de", overrides["language"]);
        Assert.Equal("epic; sandworms", overrides["custom_tags"]);
        // The provider's own lists are refreshed all the same; the person's lists sit beside them.
        Assert.Equal(["Drama"], (await _arrays.GetValuesAsync(work, "genre")).Select(entry => entry.Value));
        Assert.Equal(["fr"], (await _arrays.GetValuesAsync(work, "language")).Select(entry => entry.Value));
    }

    [Fact]
    public async Task RevertingAChangedField_ByClearingItsOverride_ShowsTheProvidersCurrentValueAgain()
    {
        var work = SeedWork();
        await RefreshFromProviderAsync(work, Fields.Select(field => (field.Key, field.First)), genres: []);
        await SaveTheEditAsync(work);
        await RefreshFromProviderAsync(work, Fields.Select(field => (field.Key, field.Refreshed)), genres: []);

        // Revert stages an empty value for the field, which the details save reads as "back to the provider's value".
        var revert = await _handler.ApplyAsync(Change(work, ("description", string.Empty)));

        Assert.NotNull(revert);
        var overrides = (await _items.LoadDisplayOverridesAsync(work)).Values;
        Assert.False(overrides.ContainsKey("description"));
        var canonical = (await _canonicals.GetByEntityAsync(work)).Single(value => value.Key == "description");
        Assert.Equal("A noble family becomes embroiled in a war.", canonical.Value);
        Assert.Equal("Dune (my cut)", overrides["title"]);
    }

    // -- Helpers --------------------------------------------------------------------------------

    // What Save changes sends for the fields the person unlocked and changed (and the tags and genres they set).
    private async Task SaveTheEditAsync(Guid work)
    {
        var fields = Fields.Where(field => Changed.Contains(field.Key)).Select(field => (field.Key, field.Yours))
            .Append(("genre", "Folk; Nordic"))
            .Append(("language", "de"))
            .Append(("custom_tags", "epic; sandworms"))
            .ToArray();
        var change = Change(work, fields);
        Assert.Null(await _handler.CheckAsync(change));
        Assert.NotNull(await _handler.ApplyAsync(change));
    }

    private static EditSessionChange Change(Guid work, params (string Key, string Value)[] fields) => new()
    {
        Id = "details",
        Kind = EditSessionChangeKinds.Details,
        EntityId = work,
        Fields = fields.Select(field => new EditSessionFieldChange { Key = field.Key, Value = field.Value }).ToList(),
    };

    // A provider refresh: the hand-written provider's claims replace its current ones for the same fields, then the entity is scored again.
    private async Task RefreshFromProviderAsync(Guid work, IEnumerable<(string Key, string Value)> scalars, IReadOnlyList<string> genres)
    {
        var claims = scalars.Select(pair => new ProviderClaim(pair.Key, pair.Value, 0.9)).ToList();
        claims.AddRange(genres.Select(genre => new ProviderClaim("genre", genre, 0.9)));
        var keys = claims.Select(claim => claim.Key).Append("genre").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await ScoringHelper.PersistClaimsAndScoreAsync(
            work, claims, _provider, _claims, _canonicals, _scoring, new NoConfigurationLoader(), [], CancellationToken.None,
            _arrays, NullLogger.Instance, snapshotFieldKeys: keys);
    }

    private Guid SeedWork()
    {
        var workId = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute(
            "INSERT INTO works (id, media_type, work_kind) VALUES (@workId, 'Movies', 'standalone');",
            new { workId = GuidSql.ToBlob(workId) });
        return workId;
    }

    private void EnsureProviderRow(Guid id)
    {
        using var connection = _db.CreateConnection();
        var exists = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM metadata_providers WHERE id = @id;", new { id = GuidSql.ToBlob(id) });
        if (exists == 0)
        {
            connection.Execute(
                "INSERT INTO metadata_providers (id, name, version, is_enabled) VALUES (@id, 'fake-provider', '1', 1);",
                new { id = GuidSql.ToBlob(id) });
        }
    }

    /// <summary>A configuration loader with the built-in defaults, so the Priority Cascade scores with no provider files.</summary>
    private sealed class NoConfigurationLoader : IConfigurationLoader
    {
        public event EventHandler<ConfigurationFileChangedEventArgs>? ConfigurationChanged { add { } remove { } }

        public string ConfigDirectoryPath => string.Empty;
        public CoreConfiguration LoadCore() => new();
        public void SaveCore(CoreConfiguration config) { }
        public ScoringSettings LoadScoring() => new();
        public void SaveScoring(ScoringSettings settings) { }
        public MaintenanceSettings LoadMaintenance() => new();
        public void SaveMaintenance(MaintenanceSettings settings) { }
        public HydrationSettings LoadHydration() => new();
        public void SaveHydration(HydrationSettings settings) { }
        public PipelineConfiguration LoadPipelines() => new();
        public void SavePipelines(PipelineConfiguration config) { }
        public DisambiguationSettings LoadDisambiguation() => new();
        public void SaveDisambiguation(DisambiguationSettings settings) { }
        public MediaTypeConfiguration LoadMediaTypes() => new();
        public void SaveMediaTypes(MediaTypeConfiguration config) { }
        public TranscodingSettings LoadTranscoding() => new();
        public void SaveTranscoding(TranscodingSettings settings) { }
        public FieldPriorityConfiguration LoadFieldPriorities() => new();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
        public LibrariesConfiguration LoadLibraries() => new();
        public ProviderConfiguration? LoadProvider(string name) => null;
        public void DeleteProvider(string name) { }
        public void SaveProvider(ProviderConfiguration config) { }
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
        public T? LoadAi<T>() where T : class => default;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
    }
}
