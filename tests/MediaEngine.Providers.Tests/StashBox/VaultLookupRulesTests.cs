using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Services.StashBox;
using MediaEngine.Providers.Services.Vault;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests.StashBox;

/// <summary>
/// The rules of the Vault lookup (Videos spec D21, D26): adult-only providers are asked about Vault items and nothing else, no
/// other provider (YouTube included) is asked about a Vault item, and only blank fields are filled.
/// </summary>
public sealed class VaultLookupRulesTests : IDisposable
{
    private const string VaultLibraryId = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string NormalLibraryId = "bbbbbbbb-0000-4000-8000-000000000002";
    private static readonly Guid VaultAsset = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid NormalAsset = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private readonly StashBoxConfigFolder _config = new();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "tuvima-vault-data-" + Guid.NewGuid().ToString("N"));

    public VaultLookupRulesTests()
    {
        _config.AddProvider("fake_adult", adultOnly: true);
        _config.AddProvider("fake_normal", adultOnly: false);
    }

    public void Dispose()
    {
        _config.Dispose();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private static T VaultRepository<T>(string lookupSource = VaultLookupSources.StashBox, string? serverId = "stashdb") where T : class
    {
        var (proxy, spy) = Spy<IVaultLibraryRepository>.Create();
        spy.Handlers[nameof(IVaultLibraryRepository.IsInVaultAsync)] = args => (Guid)args[0]! == VaultAsset;
        spy.Handlers[nameof(IVaultLibraryRepository.GetIdsAsync)] = _ => (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase) { VaultLibraryId };
        spy.Handlers[nameof(IVaultLibraryRepository.GetAsync)] = args => string.Equals((string)args[0]!, VaultLibraryId, StringComparison.OrdinalIgnoreCase)
            ? new VaultLibrary(VaultLibraryId, lookupSource, serverId, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            : null;
        return (T)(object)proxy;
    }

    // ---------------------------------------------------------------- the gate

    [Fact]
    public async Task Gate_AnAdultOnlyProviderIsAskedAboutVaultItemsAndNeverAboutNormalOnes()
    {
        var gate = new ProviderAudienceGate(_config.Loader, VaultRepository<IVaultLibraryRepository>());

        Assert.True(await gate.MayAskAboutItemAsync("fake_adult", VaultAsset));
        Assert.False(await gate.MayAskAboutItemAsync("fake_adult", NormalAsset));
        Assert.True(await gate.MayAskAboutItemAsync("stashbox", VaultAsset));
        Assert.False(await gate.MayAskAboutItemAsync("stashbox", NormalAsset));
    }

    [Theory]
    [InlineData("youtube")]
    [InlineData("fake_normal")]
    public async Task Gate_NoOtherProviderIsEverAskedAboutAVaultItem(string provider)
    {
        var gate = new ProviderAudienceGate(_config.Loader, VaultRepository<IVaultLibraryRepository>());

        Assert.False(await gate.MayAskAboutItemAsync(provider, VaultAsset));
        Assert.True(await gate.MayAskAboutItemAsync(provider, NormalAsset));
    }

    [Fact]
    public async Task Gate_ByLibrary_KeepsTheSameSeparationBeforeAnItemExists()
    {
        var gate = new ProviderAudienceGate(_config.Loader, VaultRepository<IVaultLibraryRepository>());

        Assert.False(await gate.MayAskAboutLibraryAsync("youtube", VaultLibraryId));
        Assert.False(await gate.MayAskAboutLibraryAsync("youtube", VaultLibraryId.ToUpperInvariant()));
        Assert.True(await gate.MayAskAboutLibraryAsync("youtube", NormalLibraryId));
        Assert.True(await gate.MayAskAboutLibraryAsync("stashbox", VaultLibraryId));
        Assert.False(await gate.MayAskAboutLibraryAsync("stashbox", NormalLibraryId));
        Assert.False(await gate.MayAskAboutLibraryAsync("stashbox", null));
    }

    [Fact]
    public async Task Gate_AnUnknownProviderIsNeverAsked()
    {
        var gate = new ProviderAudienceGate(_config.Loader, VaultRepository<IVaultLibraryRepository>());

        Assert.False(await gate.MayAskAboutItemAsync("no_such_provider", NormalAsset));
        Assert.False(await gate.MayAskAboutItemAsync("", NormalAsset));
    }

    [Fact]
    public void TheShippedManifests_DeclareStashBoxAdultOnlyAndEnabled_AndYoutubeNot()
    {
        var stash = _config.Loader.LoadProvider("stashbox")!;
        Assert.True(stash.AdultOnly);
        Assert.True(stash.FileFingerprint);
        Assert.True(stash.Enabled);
        Assert.Contains("api_key", stash.Onboarding!.Credentials.Select(c => c.Key));
        Assert.False(_config.Loader.LoadProvider("youtube")!.AdultOnly);
        Assert.False(_config.Loader.LoadProvider("youtube")!.FileFingerprint);
    }

    // ------------------------------------------------------------ the filler

    private sealed class Harness
    {
        public required VaultVideoDetailsService Service { get; init; }
        public required Spy<ICanonicalValueRepository> Canonical { get; init; }
        public required List<MetadataClaim> Claims { get; init; }
        public required List<CanonicalValue> Upserted { get; init; }
        public required List<string> Performers { get; init; }
        public required List<string> Tags { get; init; }
        public required List<string> Studios { get; init; }
        public required List<string> Artwork { get; init; }
        public required Spy<IVaultNameRepository> Names { get; init; }
    }

    private Harness Build(
        IReadOnlyList<IAdultVideoLookup> lookups,
        List<CanonicalValue>? existing = null,
        string? assetLibraryId = VaultLibraryId,
        string lookupSource = VaultLookupSources.StashBox,
        Guid? assetId = null)
    {
        var id = assetId ?? VaultAsset;
        var (assets, assetsSpy) = Spy<IMediaAssetRepository>.Create();
        assetsSpy.Handlers[nameof(IMediaAssetRepository.FindByIdAsync)] = _ => new MediaAsset { LibraryId = assetLibraryId };

        var (canonical, canonicalSpy) = Spy<ICanonicalValueRepository>.Create();
        var upserted = new List<CanonicalValue>();
        canonicalSpy.Handlers[nameof(ICanonicalValueRepository.GetByEntityAsync)] = _ => (IReadOnlyList<CanonicalValue>)(existing ?? []);
        canonicalSpy.Handlers[nameof(ICanonicalValueRepository.UpsertBatchAsync)] = args => { upserted.AddRange((IReadOnlyList<CanonicalValue>)args[0]!); return null; };

        var (claimRepo, claimSpy) = Spy<IMetadataClaimRepository>.Create();
        var claims = new List<MetadataClaim>();
        claimSpy.Handlers[nameof(IMetadataClaimRepository.InsertBatchAsync)] = args => { claims.AddRange((IReadOnlyList<MetadataClaim>)args[0]!); return null; };

        var (names, namesSpy) = Spy<IVaultNameRepository>.Create();
        var performers = new List<string>();
        var tags = new List<string>();
        var studios = new List<string>();
        namesSpy.Handlers[nameof(IVaultNameRepository.EnsurePerformerAsync)] = args => { performers.Add((string)args[1]!); return Guid.NewGuid(); };
        namesSpy.Handlers[nameof(IVaultNameRepository.EnsureTagAsync)] = args => { tags.Add((string)args[1]!); return Guid.NewGuid(); };
        namesSpy.Handlers[nameof(IVaultNameRepository.EnsureStudioAsync)] = args => { studios.Add((string)args[1]!); return Guid.NewGuid(); };
        namesSpy.Handlers[nameof(IVaultNameRepository.LinkPerformerAsync)] = _ => null;
        namesSpy.Handlers[nameof(IVaultNameRepository.LinkStudioAsync)] = _ => null;
        namesSpy.Handlers[nameof(IVaultNameRepository.LinkTagAsync)] = _ => null;

        var (artwork, artworkSpy) = Spy<IVaultArtworkRepository>.Create();
        var stored = new List<string>();
        artworkSpy.Handlers[nameof(IVaultArtworkRepository.GetForOwnerAsync)] = _ => (IReadOnlyList<VaultArtworkRecord>)Array.Empty<VaultArtworkRecord>();
        artworkSpy.Handlers[nameof(IVaultArtworkRepository.AddAsync)] = args => { stored.Add((string)args[3]!); return Guid.NewGuid(); };

        var (matches, matchesSpy) = Spy<IVaultMatchRepository>.Create();
        matchesSpy.Handlers[nameof(IVaultMatchRepository.GetFileFactsAsync)] = _ =>
            new VaultFileFacts(id, VaultLibraryId, Path.Combine(_data, "Example Scene One.mp4"), 1800, null);
        matchesSpy.Handlers[nameof(IVaultMatchRepository.SaveMatchAsync)] = _ => null;

        var vault = VaultRepository<IVaultLibraryRepository>(lookupSource);
        var applier = new VaultRecordApplier(
            canonical, claimRepo, names, artwork, matches, new TuvimaDataPaths(_data), TimeProvider.System,
            NullLogger<VaultRecordApplier>.Instance);
        var service = new VaultVideoDetailsService(
            new ProviderAudienceGate(_config.Loader, vault), vault, assets, canonical, matches, lookups, _config.Settings,
            _config.Loader, new VaultFileNameReader(new VaultNamingConfigurationProvider(_config.Loader)), applier,
            TimeProvider.System, NullLogger<VaultVideoDetailsService>.Instance);
        return new Harness
        {
            Service = service,
            Canonical = canonicalSpy,
            Claims = claims,
            Upserted = upserted,
            Performers = performers,
            Tags = tags,
            Studios = studios,
            Artwork = stored,
            Names = namesSpy,
        };
    }

    private sealed class FakeLookup(string name, AdultVideoInfo? answer) : IAdultVideoLookup
    {
        public int Calls { get; private set; }

        public AdultLookupQuery? LastQuery { get; private set; }

        public string ProviderName => name;

        public Task<AdultVideoInfo?> LookupAsync(AdultLookupQuery query, CancellationToken ct = default)
        {
            Calls++;
            LastQuery = query;
            return Task.FromResult(answer);
        }
    }

    private static AdultVideoInfo SomeInfo() => new(
        "stashdb", "rec-1", AdultMatchSignals.Fingerprint, "Remote Title", "Remote description", new DateOnly(2021, 3, 4), "ABC-123",
        "Remote Studio", ["Performer One", "Performer Two"], ["Tag One"], RecordedStashBox.FakeJpeg(), "image/jpeg");

    private static CanonicalValue Value(string key, string value, Guid? provider = null) =>
        new() { EntityId = VaultAsset, Key = key, Value = value, WinningProviderId = provider ?? WellKnownProviders.LocalProcessor };

    [Fact]
    public async Task LookupAndFillAsync_AnAdultOnlyTestProviderIsCalledForAVaultItem_AndANormalProviderNeverIs()
    {
        var adult = new FakeLookup("fake_adult", SomeInfo());
        var normal = new FakeLookup("fake_normal", SomeInfo());
        var harness = Build([normal, adult]);

        var outcome = await harness.Service.LookupAndFillAsync(VaultAsset, []);

        Assert.Equal(VaultVideoDetailsOutcome.Filled, outcome.Status);
        Assert.Equal(1, adult.Calls);
        Assert.Equal(0, normal.Calls);
        Assert.Equal("stashdb", adult.LastQuery!.PreferredServerId);
    }

    [Fact]
    public async Task LookupAndFillAsync_ForANormalItem_NoAdultProviderIsCalled()
    {
        var adult = new FakeLookup("fake_adult", SomeInfo());
        var harness = Build([adult], assetLibraryId: NormalLibraryId, assetId: NormalAsset);

        var outcome = await harness.Service.LookupAndFillAsync(NormalAsset, []);

        Assert.Equal(VaultVideoDetailsOutcome.Skipped, outcome.Status);
        Assert.Equal(0, adult.Calls);
        Assert.Empty(harness.Claims);
    }

    [Fact]
    public async Task LookupAndFillAsync_AVaultLibrarySetToNone_AsksNobody()
    {
        var adult = new FakeLookup("fake_adult", SomeInfo());
        var harness = Build([adult], lookupSource: VaultLookupSources.None);

        Assert.Equal(VaultVideoDetailsOutcome.Skipped, (await harness.Service.LookupAndFillAsync(VaultAsset, [])).Status);
        Assert.Equal(0, adult.Calls);
    }

    [Fact]
    public async Task LookupAndFillAsync_FillsOnlyBlankFields_AndAUserEditAlwaysWins()
    {
        var existing = new List<CanonicalValue>
        {
            Value(MetadataFieldConstants.Title, "My Own Title", WellKnownProviders.UserManual),
            Value(MetadataFieldConstants.Description, "   "),
        };
        var harness = Build([new FakeLookup("fake_adult", SomeInfo())], existing);

        var outcome = await harness.Service.LookupAndFillAsync(VaultAsset, []);

        Assert.DoesNotContain(MetadataFieldConstants.Title, outcome.FilledFields);
        Assert.DoesNotContain(harness.Claims, c => c.ClaimKey == MetadataFieldConstants.Title);
        Assert.Contains(MetadataFieldConstants.Description, outcome.FilledFields);
        Assert.Equal("Remote description", harness.Upserted.Single(v => v.Key == MetadataFieldConstants.Description).Value);
        Assert.Equal("2021-03-04", harness.Upserted.Single(v => v.Key == MetadataFieldConstants.OriginalReleaseDate).Value);
        Assert.All(harness.Claims, c => Assert.Equal(WellKnownProviders.StashBox, c.ProviderId));
        Assert.All(harness.Claims, c => Assert.False(c.IsUserLocked));
    }

    [Fact]
    public async Task LookupAndFillAsync_ABlankTitleIsFilled_AndTheVaultOnlyNamesAreRecorded()
    {
        var harness = Build([new FakeLookup("fake_adult", SomeInfo())]);

        var outcome = await harness.Service.LookupAndFillAsync(VaultAsset, []);

        Assert.Equal("Remote Title", harness.Upserted.Single(v => v.Key == MetadataFieldConstants.Title).Value);
        Assert.Equal(["Performer One", "Performer Two"], harness.Performers);
        Assert.Equal(["Remote Studio"], harness.Studios);
        Assert.Equal(["Tag One"], harness.Tags);
        Assert.Contains("cover", outcome.FilledFields);
        var cover = Assert.Single(harness.Artwork);
        Assert.True(File.Exists(cover));
    }

    [Fact]
    public async Task LookupAndFillAsync_NothingFound_ChangesNothing()
    {
        var harness = Build([new FakeLookup("fake_adult", null)]);

        var outcome = await harness.Service.LookupAndFillAsync(VaultAsset, []);

        Assert.Equal(VaultVideoDetailsOutcome.NotFound, outcome.Status);
        Assert.Empty(harness.Claims);
        Assert.Empty(harness.Performers);
    }

    [Fact]
    public async Task LookupAndFillAsync_WithTheRealPluginAndARecordedResponse_FillsBlankFieldsOfAVaultItem()
    {
        await _config.Settings.SetKeyAsync(StashBoxSettings.DefaultServerId, "test-account-key-1234");
        var server = new RecordedStashBox { Respond = RecordedStashBox.Script(search: RecordedStashBox.Fixture("search-scene-match.json")) };
        var harness = Build(
            [_config.Lookup(server)],
            [Value(MetadataFieldConstants.Title, "Example Scene One")]);

        var outcome = await harness.Service.LookupAndFillAsync(VaultAsset, []);

        Assert.Equal(VaultVideoDetailsOutcome.Filled, outcome.Status);
        Assert.Equal("A short description of the example scene.\nSecond line.", harness.Upserted.Single(v => v.Key == MetadataFieldConstants.Description).Value);
        Assert.Equal("2021-03-04", harness.Upserted.Single(v => v.Key == MetadataFieldConstants.OriginalReleaseDate).Value);
        Assert.Equal(VaultLookupSources.StashBox, harness.Upserted.Single(v => v.Key == MetadataFieldConstants.VideoLookupSource).Value);
        Assert.Equal(["Performer One", "Performer Two"], harness.Performers);
        Assert.Single(harness.Artwork);
        // Only the title leaves the machine: no fingerprint was given and the permission is off.
        Assert.All(server.Requests.Where(r => r.Uri.AbsolutePath == "/graphql"), r => Assert.DoesNotContain("findScenesByFullFingerprints", r.Body, StringComparison.OrdinalIgnoreCase));
    }
}
