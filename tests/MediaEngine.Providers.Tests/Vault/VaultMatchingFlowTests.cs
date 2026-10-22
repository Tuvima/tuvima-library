using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Services.StashBox;
using MediaEngine.Providers.Services.Vault;
using MediaEngine.Providers.Tests.StashBox;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests.Vault;

/// <summary>
/// The whole Vault lookup path against recorded stash-box answers, with hand-written spies for the storage on either side
/// (Videos spec WP12): what is applied, where each field is recorded as coming from, and what is deliberately left alone.
/// </summary>
public sealed class VaultMatchingFlowTests : IDisposable
{
    private const string VaultLibraryId = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string Key = "test-account-key-1234";
    private static readonly Guid Asset = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly AdultFingerprint RecordedOshash = new(AdultFingerprintAlgorithms.Oshash, "8e245d9679d31e12");

    private readonly StashBoxConfigFolder _config = new();
    private readonly RecordedStashBox _server = new();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "tuvima-vault-flow-" + Guid.NewGuid().ToString("N"));

    public VaultMatchingFlowTests() => Directory.CreateDirectory(_data);

    public void Dispose()
    {
        _config.Dispose();
        Directory.Delete(_data, recursive: true);
    }

    private sealed class Flow
    {
        public required VaultVideoDetailsService Details { get; init; }
        public required VaultIdentifyService Identify { get; init; }
        public required VaultIngestionHook Hook { get; init; }
        public required List<MetadataClaim> Claims { get; init; }
        public required List<CanonicalValue> Upserted { get; init; }
        public required List<(VaultMatch Match, IReadOnlyList<VaultFieldSource> Fields)> Saved { get; init; }
        public required List<(string Kind, string Detail)> Activity { get; init; }
        public required List<string> Covers { get; init; }
        public required List<string> Stills { get; init; }
        public required List<string> Performers { get; init; }
        public required List<string> Unlinked { get; init; }
        public required List<string> Calls { get; init; }
    }

    private async Task<Flow> BuildAsync(
        string fileName,
        double? duration = 1800,
        bool fingerprintPermission = false,
        IReadOnlyList<CanonicalValue>? existing = null,
        string lookupSource = VaultLookupSources.StashBox)
    {
        await _config.Settings.SetKeyAsync(StashBoxSettings.DefaultServerId, Key);
        await _config.Settings.SetFingerprintPermissionAsync(fingerprintPermission);

        var filePath = Path.Combine(_data, fileName);
        File.WriteAllBytes(filePath, new byte[4096]);

        var calls = new List<string>();
        var (assets, assetsSpy) = Spy<IMediaAssetRepository>.Create();
        assetsSpy.Handlers[nameof(IMediaAssetRepository.FindByIdAsync)] = _ => new MediaAsset { LibraryId = VaultLibraryId };

        var claims = new List<MetadataClaim>();
        var upserted = new List<CanonicalValue>();
        var (canonical, canonicalSpy) = Spy<ICanonicalValueRepository>.Create();
        canonicalSpy.Handlers[nameof(ICanonicalValueRepository.GetByEntityAsync)] = _ => existing ?? (IReadOnlyList<CanonicalValue>)[];
        canonicalSpy.Handlers[nameof(ICanonicalValueRepository.UpsertBatchAsync)] = args => { upserted.AddRange((IReadOnlyList<CanonicalValue>)args[0]!); return null; };
        var (claimRepo, claimSpy) = Spy<IMetadataClaimRepository>.Create();
        claimSpy.Handlers[nameof(IMetadataClaimRepository.InsertBatchAsync)] = args => { claims.AddRange((IReadOnlyList<MetadataClaim>)args[0]!); return null; };

        var performers = new List<string>();
        var unlinked = new List<string>();
        var (names, namesSpy) = Spy<IVaultNameRepository>.Create();
        namesSpy.Handlers[nameof(IVaultNameRepository.EnsurePerformerAsync)] = args => { performers.Add((string)args[1]!); return Guid.NewGuid(); };
        namesSpy.Handlers[nameof(IVaultNameRepository.EnsureStudioAsync)] = _ => Guid.NewGuid();
        namesSpy.Handlers[nameof(IVaultNameRepository.EnsureTagAsync)] = _ => Guid.NewGuid();
        namesSpy.Handlers[nameof(IVaultNameRepository.LinkPerformerAsync)] = _ => null;
        namesSpy.Handlers[nameof(IVaultNameRepository.LinkStudioAsync)] = _ => null;
        namesSpy.Handlers[nameof(IVaultNameRepository.LinkTagAsync)] = _ => null;
        namesSpy.Handlers[nameof(IVaultNameRepository.UnlinkPerformersAsync)] = _ => { unlinked.Add("performers"); return null; };
        namesSpy.Handlers[nameof(IVaultNameRepository.UnlinkStudiosAsync)] = _ => { unlinked.Add("studio"); return null; };
        namesSpy.Handlers[nameof(IVaultNameRepository.UnlinkTagsAsync)] = _ => { unlinked.Add("tags"); return null; };
        namesSpy.Handlers[nameof(IVaultNameRepository.GetPerformersOfAsync)] = _ => (IReadOnlyList<VaultPerformer>)[];
        namesSpy.Handlers[nameof(IVaultNameRepository.GetStudiosOfAsync)] = _ => (IReadOnlyList<VaultStudio>)[];
        namesSpy.Handlers[nameof(IVaultNameRepository.GetTagsOfAsync)] = _ => (IReadOnlyList<VaultTag>)[];

        var covers = new List<string>();
        var stills = new List<string>();
        var (artwork, artworkSpy) = Spy<IVaultArtworkRepository>.Create();
        artworkSpy.Handlers[nameof(IVaultArtworkRepository.GetForOwnerAsync)] = _ => (IReadOnlyList<VaultArtworkRecord>)[];
        artworkSpy.Handlers[nameof(IVaultArtworkRepository.AddAsync)] = args =>
        {
            (string.Equals((string)args[2]!, VaultArtworkKinds.Still, StringComparison.Ordinal) ? stills : covers).Add((string)args[3]!);
            return Guid.NewGuid();
        };

        var saved = new List<(VaultMatch, IReadOnlyList<VaultFieldSource>)>();
        var (matches, matchesSpy) = Spy<IVaultMatchRepository>.Create();
        matchesSpy.Handlers[nameof(IVaultMatchRepository.GetFileFactsAsync)] = _ => new VaultFileFacts(Asset, VaultLibraryId, filePath, duration, 4096);
        matchesSpy.Handlers[nameof(IVaultMatchRepository.GetFingerprintsAsync)] = _ =>
            (IReadOnlyList<VaultStoredFingerprint>)[new(RecordedOshash.Algorithm, RecordedOshash.Hash, 4096, DateTimeOffset.UtcNow)];
        matchesSpy.Handlers[nameof(IVaultMatchRepository.SaveMatchAsync)] = args => { saved.Add(((VaultMatch)args[0]!, (IReadOnlyList<VaultFieldSource>)args[1]!)); return null; };
        matchesSpy.Handlers[nameof(IVaultMatchRepository.GetMatchAsync)] = _ => saved.Count == 0 ? null : saved[^1].Item1;

        var activity = new List<(string, string)>();
        var (activityRepo, activitySpy) = Spy<IVaultActivityRepository>.Create();
        activitySpy.Handlers[nameof(IVaultActivityRepository.AddAsync)] = args => { activity.Add(((string)args[1]!, (string)args[3]!)); return null; };

        var (vault, vaultSpy) = Spy<IVaultLibraryRepository>.Create();
        vaultSpy.Handlers[nameof(IVaultLibraryRepository.IsInVaultAsync)] = _ => true;
        vaultSpy.Handlers[nameof(IVaultLibraryRepository.GetIdsAsync)] = _ => (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase) { VaultLibraryId };
        vaultSpy.Handlers[nameof(IVaultLibraryRepository.GetAsync)] = _ =>
            new VaultLibrary(VaultLibraryId, lookupSource, StashBoxSettings.DefaultServerId, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var naming = new VaultNamingConfigurationProvider(() => null);
        var reader = new VaultFileNameReader(naming);
        var lookup = new StashBoxVideoLookup(_config.Loader, _config.Settings, _config.Client(_server), naming, NullLogger<StashBoxVideoLookup>.Instance);
        var applier = new VaultRecordApplier(
            canonical, claimRepo, names, artwork, matches, new TuvimaDataPaths(_data), TimeProvider.System, NullLogger<VaultRecordApplier>.Instance);
        var gate = new ProviderAudienceGate(_config.Loader, vault);
        var details = new VaultVideoDetailsService(
            gate, vault, assets, canonical, matches, [lookup], _config.Settings, _config.Loader, reader, applier, TimeProvider.System,
            NullLogger<VaultVideoDetailsService>.Instance);
        var identify = new VaultIdentifyService(
            gate, vault, assets, canonical, matches, names, artwork, activityRepo, [lookup], _config.Settings, reader, naming, applier, TimeProvider.System);
        var hook = new VaultIngestionHook(
            vault, artwork, activityRepo, matches, details, new TuvimaDataPaths(_data), TimeProvider.System, NullLogger<VaultIngestionHook>.Instance);

        return new Flow
        {
            Details = details,
            Identify = identify,
            Hook = hook,
            Claims = claims,
            Upserted = upserted,
            Saved = saved,
            Activity = activity,
            Covers = covers,
            Stills = stills,
            Performers = performers,
            Unlinked = unlinked,
            Calls = calls,
        };
    }

    // ------------------------------------------------------------------ automatic matching

    [Fact]
    public async Task AnExactFingerprintMatch_FillsFieldsAndRecordsWhereEachCameFrom()
    {
        _server.Respond = RecordedStashBox.Script(fingerprint: RecordedStashBox.Fixture("fingerprint-match.json"));
        var flow = await BuildAsync("a plain name.mp4", fingerprintPermission: true);

        var outcome = await flow.Details.LookupFileAsync(Asset);

        Assert.Equal(VaultVideoDetailsOutcome.Filled, outcome.Status);
        Assert.Equal("Example Scene One (Fingerprint Record)", flow.Upserted.Single(v => v.Key == MetadataFieldConstants.Title).Value);
        Assert.Equal(["Performer One"], flow.Performers);
        Assert.Single(flow.Covers);
        var (match, fields) = Assert.Single(flow.Saved);
        Assert.Equal(AdultMatchSignals.Fingerprint, match.MatchedBy);
        Assert.Equal(VaultAppliedVia.Automatic, match.AppliedVia);
        Assert.Equal("7d1c2c0e-4f0b-4a3e-9a63-0f2b1f9a1111", match.RecordId);
        Assert.Equal(StashBoxSettings.DefaultServerId, match.ServerId);
        Assert.Equal(
            [VaultFieldNames.Title, VaultFieldNames.Description, VaultFieldNames.ReleaseDate, VaultFieldNames.Performers, VaultFieldNames.Studio, VaultFieldNames.Tags, VaultFieldNames.Cover],
            fields.Select(f => f.Field));
        Assert.All(fields, f =>
        {
            Assert.Equal(match.RecordId, f.RecordId);
            Assert.Equal(AdultMatchSignals.Fingerprint, f.MatchedBy);
            Assert.Equal(VaultAppliedVia.Automatic, f.AppliedVia);
        });
        Assert.All(flow.Claims, c => Assert.Equal(WellKnownProviders.StashBox, c.ProviderId));
        Assert.Contains(_server.Requests, r => r.Body.Contains("findScenesByFullFingerprints", StringComparison.Ordinal) && r.Body.Contains(RecordedOshash.Hash, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutThePermission_NoFingerprintIsReadOrSent_AndTheNameAndLengthStillMatch()
    {
        _server.Respond = RecordedStashBox.Script(search: RecordedStashBox.Fixture("search-scene-match.json"));
        var flow = await BuildAsync("Example Studio - 2021-03-04 - Example Scene One.mp4", fingerprintPermission: false);

        var outcome = await flow.Details.LookupFileAsync(Asset);

        Assert.Equal(VaultVideoDetailsOutcome.Filled, outcome.Status);
        Assert.DoesNotContain(_server.Requests, r => r.Body.Contains("findScenesByFullFingerprints", StringComparison.Ordinal));
        Assert.DoesNotContain(_server.Requests, r => r.Body.Contains(RecordedOshash.Hash, StringComparison.Ordinal));
        Assert.Equal(AdultMatchSignals.Title, flow.Saved.Single().Match.MatchedBy);
    }

    [Fact]
    public async Task AProductCodeWithAnAgreeingLength_MatchesAndFillsFields()
    {
        _server.Respond = RecordedStashBox.Script(search: RecordedStashBox.Fixture("search-scene-match.json"));
        var flow = await BuildAsync("ABC-123 - something else entirely.mp4", duration: 1805);

        var outcome = await flow.Details.LookupFileAsync(Asset);

        Assert.Equal(VaultVideoDetailsOutcome.Filled, outcome.Status);
        Assert.Equal(AdultMatchSignals.ProductCode, flow.Saved.Single().Match.MatchedBy);
        Assert.Equal("Example Scene One", flow.Upserted.Single(v => v.Key == MetadataFieldConstants.Title).Value);
        Assert.All(flow.Saved.Single().Fields, f => Assert.Equal(AdultMatchSignals.ProductCode, f.MatchedBy));
    }

    [Fact]
    public async Task AProductCodeWhoseRecordHasAnotherLength_StaysUnmatchedAndNothingIsWritten()
    {
        _server.Respond = RecordedStashBox.Script(search: RecordedStashBox.Fixture("search-scene-match.json"));
        var flow = await BuildAsync("ABC-123 - something else entirely.mp4", duration: 600);

        var outcome = await flow.Details.LookupFileAsync(Asset);

        Assert.Equal(VaultVideoDetailsOutcome.NotFound, outcome.Status);
        AssertNothingApplied(flow);
    }

    [Fact]
    public async Task ACodeThatDisagreesWithTheFingerprint_IsNotApplied()
    {
        _server.Respond = RecordedStashBox.Script(
            fingerprint: RecordedStashBox.Fixture("fingerprint-other-record.json"),
            search: RecordedStashBox.Fixture("search-scene-match.json"));
        var flow = await BuildAsync("ABC-123 - Example Scene One.mp4", fingerprintPermission: true);

        var outcome = await flow.Details.LookupFileAsync(Asset);

        Assert.Equal(VaultVideoDetailsOutcome.NotFound, outcome.Status);
        AssertNothingApplied(flow);
    }

    [Fact]
    public async Task ATitleAloneWithTheWrongLength_IsWeakAndStaysUnmatched()
    {
        _server.Respond = RecordedStashBox.Script(search: RecordedStashBox.Fixture("search-scene-match.json"));
        var flow = await BuildAsync("Example Scene One.mp4", duration: 120);

        Assert.Equal(VaultVideoDetailsOutcome.NotFound, (await flow.Details.LookupFileAsync(Asset)).Status);
        AssertNothingApplied(flow);
    }

    [Fact]
    public async Task TwoRecordsWithTheSameCodeAndLength_AreNotGuessedBetween()
    {
        _server.Respond = RecordedStashBox.Script(search: RecordedStashBox.Fixture("search-two-same-code.json"));
        var flow = await BuildAsync("ABC-123.mp4");

        Assert.Equal(VaultVideoDetailsOutcome.NotFound, (await flow.Details.LookupFileAsync(Asset)).Status);
        AssertNothingApplied(flow);
    }

    [Fact]
    public async Task AWrongKey_ShowsNotConnected_AndTheVideoStaysUnmatchedLikeNone()
    {
        _server.Respond = _ => RecordedStashBox.Json("{}", System.Net.HttpStatusCode.Unauthorized);
        var flow = await BuildAsync("Example Scene One.mp4");

        Assert.Equal(VaultVideoDetailsOutcome.NotFound, (await flow.Details.LookupFileAsync(Asset)).Status);
        AssertNothingApplied(flow);
        Assert.Equal(StashBoxConnectionStates.NotConnected, Assert.Single((await _config.Settings.GetAsync()).Servers).Connection);
    }

    [Fact]
    public async Task APersonsOwnEdit_IsNeverReplacedByAMatch()
    {
        _server.Respond = RecordedStashBox.Script(fingerprint: RecordedStashBox.Fixture("fingerprint-match.json"));
        var mine = new CanonicalValue { EntityId = Asset, Key = MetadataFieldConstants.Title, Value = "My own title", WinningProviderId = WellKnownProviders.UserManual };
        var flow = await BuildAsync("a plain name.mp4", fingerprintPermission: true, existing: [mine]);

        await flow.Details.LookupFileAsync(Asset);

        Assert.DoesNotContain(flow.Claims, c => c.ClaimKey == MetadataFieldConstants.Title);
        Assert.DoesNotContain(flow.Saved.Single().Fields, f => f.Field == VaultFieldNames.Title);
    }

    private static void AssertNothingApplied(Flow flow)
    {
        Assert.Empty(flow.Claims);
        Assert.Empty(flow.Upserted);
        Assert.Empty(flow.Saved);
        Assert.Empty(flow.Performers);
        Assert.Empty(flow.Covers);
    }

    // ---------------------------------------------------------------- bad pictures

    [Fact]
    public async Task ACoverThatIsNotAnImage_IsRefused_ButTheTextStillFillsAndTheStillStays()
    {
        _server.Respond = seen => seen.Uri.AbsolutePath.EndsWith("/cover.jpg", StringComparison.Ordinal)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[5000]) }
            : RecordedStashBox.Script(fingerprint: RecordedStashBox.Fixture("fingerprint-match.json"))(seen);
        var flow = await BuildAsync("a plain name.mp4", fingerprintPermission: true);

        await flow.Hook.OnFileRegisteredAsync(new VaultRegisteredFile(Asset, VaultLibraryId, Path.Combine(_data, "a plain name.mp4"), TestImages.Jpeg(320, 180), "image/jpeg"));

        Assert.Empty(flow.Covers);
        Assert.Single(flow.Stills);
        Assert.NotEmpty(flow.Upserted);
        Assert.Contains(flow.Saved.Single().Fields, f => f.Field == VaultFieldNames.Title);
        Assert.DoesNotContain(flow.Saved.Single().Fields, f => f.Field == VaultFieldNames.Cover);
    }

    [Fact]
    public async Task ACoverBytesTheApplierIsGivenThatAreNotAnImage_IsRefusedWithoutStoringAnything()
    {
        var (canonical, canonicalSpy) = Spy<ICanonicalValueRepository>.Create();
        canonicalSpy.Handlers[nameof(ICanonicalValueRepository.GetByEntityAsync)] = _ => (IReadOnlyList<CanonicalValue>)[];
        var (artwork, artworkSpy) = Spy<IVaultArtworkRepository>.Create();
        artworkSpy.Handlers[nameof(IVaultArtworkRepository.GetForOwnerAsync)] = _ => (IReadOnlyList<VaultArtworkRecord>)[];
        var (matches, matchesSpy) = Spy<IVaultMatchRepository>.Create();
        matchesSpy.Handlers[nameof(IVaultMatchRepository.SaveMatchAsync)] = _ => null;
        var applier = new VaultRecordApplier(
            canonical, Spy<IMetadataClaimRepository>.Create().Proxy, Spy<IVaultNameRepository>.Create().Proxy, artwork, matches,
            new TuvimaDataPaths(_data), TimeProvider.System, NullLogger<VaultRecordApplier>.Instance);

        var info = new AdultVideoInfo("stashdb", "rec", AdultMatchSignals.Fingerprint, null, null, null, null, null, [], [], new byte[5000], "image/jpeg");
        var result = await applier.ApplyAsync(new VaultApplyRequest(Asset, VaultLibraryId, info, VaultAppliedVia.Automatic, null));

        Assert.Empty(result.Applied);
        Assert.Equal([VaultFieldNames.Cover], result.Refused);
        Assert.Equal(VaultImageRefusal.NotAnImage, result.CoverRefusal);
        Assert.Empty(Directory.GetFiles(_data, "cover-*", SearchOption.AllDirectories));
    }

    // ----------------------------------------------------------- the ingestion hook

    [Fact]
    public async Task TheHook_KeepsTheStillFrameInTheVaultsOwnArtwork_AndNotesAnAddedAndAMatchedLine()
    {
        _server.Respond = RecordedStashBox.Script(fingerprint: RecordedStashBox.Fixture("fingerprint-match.json"));
        var flow = await BuildAsync("a plain name.mp4", fingerprintPermission: true);

        await flow.Hook.OnFileRegisteredAsync(new VaultRegisteredFile(Asset, VaultLibraryId, Path.Combine(_data, "a plain name.mp4"), TestImages.Jpeg(320, 180), "image/jpeg"));

        Assert.Single(flow.Stills);
        Assert.True(File.Exists(flow.Stills[0]));
        Assert.Equal([VaultActivityKinds.Added, VaultActivityKinds.Matched], flow.Activity.Select(a => a.Kind));
        Assert.All(flow.Activity, a => Assert.DoesNotContain("Example Scene", a.Detail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheHook_AWeakMatchIsOnlyAQuietUnmatchedLine()
    {
        _server.Respond = RecordedStashBox.Script(search: RecordedStashBox.Fixture("search-scene-match.json"));
        var flow = await BuildAsync("Example Scene One.mp4", duration: 90);

        await flow.Hook.OnFileRegisteredAsync(new VaultRegisteredFile(Asset, VaultLibraryId, Path.Combine(_data, "Example Scene One.mp4"), null, null));

        Assert.Equal([VaultActivityKinds.Added, VaultActivityKinds.Unmatched], flow.Activity.Select(a => a.Kind));
        AssertNothingApplied(flow);
    }

    [Fact]
    public async Task TheHook_ALookupThatFails_NeverFailsIngestion()
    {
        _server.Respond = _ => throw new InvalidOperationException("the server fell over");
        var flow = await BuildAsync("Example Scene One.mp4");

        await flow.Hook.OnFileRegisteredAsync(new VaultRegisteredFile(Asset, VaultLibraryId, Path.Combine(_data, "Example Scene One.mp4"), null, null));

        Assert.Contains(flow.Activity, a => a.Kind is VaultActivityKinds.Failed or VaultActivityKinds.Unmatched);
        AssertNothingApplied(flow);
    }

    // ---------------------------------------------------------------------- Identify

    [Fact]
    public async Task Identify_OnAnUnmatchedVideo_FindsPreviewsAndAppliesOnlyTheTickedFields()
    {
        _server.Respond = RecordedStashBox.Script(
            search: RecordedStashBox.Fixture("search-scene-match.json"), scene: RecordedStashBox.Fixture("scene-by-id.json"));
        var flow = await BuildAsync("Example Scene One.mp4", duration: 90);
        Assert.Equal(VaultVideoDetailsOutcome.NotFound, (await flow.Details.LookupFileAsync(Asset)).Status);

        var found = await flow.Identify.SearchAsync(Asset, "Example Scene");
        Assert.Equal(VaultIdentifyStatus.Ok, found.Status);
        var candidate = found.Candidates.First(c => c.RecordId.EndsWith("1111", StringComparison.Ordinal));
        Assert.False(candidate.LengthAgrees);

        var preview = await flow.Identify.PreviewAsync(Asset, candidate.ServerId, candidate.RecordId);
        Assert.Equal(VaultIdentifyStatus.Ok, preview.Status);
        Assert.Equal("Example Scene One", preview.RecordTitle);
        Assert.Equal(7, preview.Fields.Count);
        Assert.Contains(preview.Fields, f => f.Field == VaultFieldNames.Title && f.Proposed == "Example Scene One" && f.Changes && f.Ticked);
        Assert.Empty(flow.Claims);

        var result = await flow.Identify.ApplyAsync(Asset, candidate.ServerId, candidate.RecordId, [VaultFieldNames.Title, VaultFieldNames.Studio, "not_a_field"]);

        Assert.Equal(VaultIdentifyStatus.Ok, result.Status);
        Assert.Equal([VaultFieldNames.Title, VaultFieldNames.Studio], result.Applied);
        Assert.Single(flow.Claims, c => c.ClaimKey == MetadataFieldConstants.Title);
        Assert.DoesNotContain(flow.Claims, c => c.ClaimKey == MetadataFieldConstants.Description);
        Assert.Empty(flow.Performers);
        Assert.Empty(flow.Covers);
        Assert.Equal(["studio"], flow.Unlinked);
        var (match, fields) = Assert.Single(flow.Saved);
        Assert.Equal(AdultMatchSignals.Identify, match.MatchedBy);
        Assert.Equal(VaultAppliedVia.Identify, match.AppliedVia);
        Assert.Equal([VaultFieldNames.Title, VaultFieldNames.Studio], fields.Select(f => f.Field));
        Assert.Contains(flow.Activity, a => a.Kind == VaultActivityKinds.Identified);
        // What was applied is the cleaned, server-read record, not text a browser sent.
        Assert.Equal("Example Scene One", flow.Upserted.Single(v => v.Key == MetadataFieldConstants.Title).Value);
    }

    [Fact]
    public async Task Identify_NeverReplacesAPersonsOwnEdit_EvenWhenTheFieldIsTicked()
    {
        _server.Respond = RecordedStashBox.Script(scene: RecordedStashBox.Fixture("scene-by-id.json"));
        var mine = new CanonicalValue { EntityId = Asset, Key = MetadataFieldConstants.Title, Value = "My own title", WinningProviderId = WellKnownProviders.UserManual };
        var flow = await BuildAsync("Example Scene One.mp4", existing: [mine]);

        var preview = await flow.Identify.PreviewAsync(Asset, StashBoxSettings.DefaultServerId, "7d1c2c0e-4f0b-4a3e-9a63-0f2b1f9a1111");
        var title = preview.Fields.Single(f => f.Field == VaultFieldNames.Title);
        Assert.True(title.PersonsOwn);
        Assert.False(title.Ticked);

        var result = await flow.Identify.ApplyAsync(Asset, StashBoxSettings.DefaultServerId, "7d1c2c0e-4f0b-4a3e-9a63-0f2b1f9a1111", [VaultFieldNames.Title]);

        Assert.Empty(result.Applied);
        Assert.Empty(flow.Claims);
    }

    [Fact]
    public async Task Identify_RecordTextFromTheServer_IsCleanedBeforeItIsPreviewedOrStored()
    {
        _server.Respond = RecordedStashBox.Script(scene: RecordedStashBox.Fixture("scene-by-id.json"));
        var flow = await BuildAsync("Example Scene One.mp4");

        await flow.Identify.ApplyAsync(Asset, StashBoxSettings.DefaultServerId, "7d1c2c0e-4f0b-4a3e-9a63-0f2b1f9a1111", [VaultFieldNames.Description]);

        var description = flow.Upserted.Single(v => v.Key == MetadataFieldConstants.Description).Value;
        Assert.DoesNotContain('<', description);
        Assert.DoesNotContain("https", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Identify_WithNoWorkingKey_SaysNotConnectedAndAsksNobody()
    {
        _server.Respond = _ => RecordedStashBox.Json("{}", System.Net.HttpStatusCode.Unauthorized);
        var flow = await BuildAsync("Example Scene One.mp4");
        await _config.Settings.RemoveKeyAsync(StashBoxSettings.DefaultServerId);
        _server.Requests.Clear();

        var found = await flow.Identify.SearchAsync(Asset, "anything");

        Assert.Equal(VaultIdentifyStatus.NotConnected, found.Status);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Identify_ForALibrarySetToNone_AsksNobody()
    {
        var flow = await BuildAsync("Example Scene One.mp4", lookupSource: VaultLookupSources.None);

        Assert.Equal(VaultIdentifyStatus.LookupOff, (await flow.Identify.SearchAsync(Asset, "anything")).Status);
        Assert.Empty(_server.Requests);
    }
}
