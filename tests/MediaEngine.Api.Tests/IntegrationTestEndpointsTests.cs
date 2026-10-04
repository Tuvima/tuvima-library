using System.Collections;
using System.Reflection;
using MediaEngine.Api.DevSupport;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

public sealed class IntegrationTestEndpointsTests : IDisposable
{
    private readonly string _tempRoot;

    public IntegrationTestEndpointsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_integration_validator_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void Program_MapsIntegrationTestEndpointsOnlyInDevelopment()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DependencyInjection\ApiEndpointRouteBuilderExtensions.cs"));
        var developmentGuard = "if (app.Environment.IsDevelopment())";
        var guardIndex = source.IndexOf(developmentGuard, StringComparison.Ordinal);
        var mapIndex = source.IndexOf("app.MapIntegrationTestEndpoints();", StringComparison.Ordinal);

        Assert.True(guardIndex >= 0);
        Assert.True(mapIndex > guardIndex);
        Assert.DoesNotContain(
            "app.MapIntegrationTestEndpoints();",
            source[..guardIndex],
            StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationTestEndpoints_RegisterOnlyUnderDevRouteGroup()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\IntegrationTestEndpoints.cs"));

        Assert.Contains("app.MapGroup(\"/dev\")", source, StringComparison.Ordinal);
        Assert.Contains("group.MapPost(\"/integration-test\", RunIntegrationTestAsync)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("app.MapPost(\"/integration-test\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DevHarnessReset_DefaultsToGeneratedStateScope()
    {
        Assert.Equal(DevHarnessWipeScope.GeneratedState, DevHarnessResetService.ParseScope(null));
        Assert.Equal(DevHarnessWipeScope.GeneratedState, DevHarnessResetService.ParseScope("generated-state"));
        Assert.Equal(DevHarnessWipeScope.Full, DevHarnessResetService.ParseScope("full"));
    }

    [Fact]
    public void DevelopmentTools_UseIntentEndpointsAndPreserveConfigurationByDefault()
    {
        var endpoints = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\DevSeedEndpoints.cs"));
        var reset = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\DevHarnessResetService.cs"));
        var service = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\DevelopmentTestService.cs"));

        Assert.Contains("group.MapPost(\"/reset-and-seed\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("group.MapPost(\"/reset-library-data\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("group.MapPost(\"/factory-reset\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("ResetAndSeedAsync", service, StringComparison.Ordinal);
        Assert.Contains("DevelopmentFixtureSet fixtureSet", service, StringComparison.Ordinal);
        Assert.Contains("ScanDirectories", service, StringComparison.Ordinal);
        Assert.Contains("ResumeWatcherAsync", service, StringComparison.Ordinal);
        Assert.Contains("PreservedConfigurationTables", reset, StringComparison.Ordinal);
        Assert.Contains("ResetLibraryDatabaseAsync", reset, StringComparison.Ordinal);
        Assert.Contains("\"accounts\"", reset, StringComparison.Ordinal);
        Assert.Contains("\"profiles\"", reset, StringComparison.Ordinal);
        Assert.Contains("\"metadata_providers\"", reset, StringComparison.Ordinal);
        Assert.Contains("\"provider_config\"", reset, StringComparison.Ordinal);
        Assert.Contains("DevFixtureManifestStore.Read", reset, StringComparison.Ordinal);
        Assert.DoesNotContain("DevSeedEndpoints.GetSeedFilePaths", reset, StringComparison.Ordinal);
        Assert.Contains("Configured source media was preserved", reset, StringComparison.Ordinal);
    }

    [Fact]
    public void StandardFixtureSet_IsSmallDeterministicAndStructurallyRepresentative()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\DevSeedEndpoints.cs"));

        Assert.Contains("StandardBookTitles", source, StringComparison.Ordinal);
        Assert.Contains("\"Leviathan Wakes\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Caliban's War\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Blade Runner 2049\"", source, StringComparison.Ordinal);
        Assert.Contains("fixture.SeasonNumber == 1", source, StringComparison.Ordinal);
        Assert.Contains("StandardMusicTitles", source, StringComparison.Ordinal);
        Assert.Contains("StandardComicTitles", source, StringComparison.Ordinal);
        Assert.Contains("fixtureSet == DevelopmentFixtureSet.Stress", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentFixtureManifest_RecordsOnlyExplicitlyGeneratedFiles()
    {
        var libraryRoot = Path.Combine(_tempRoot, "library");
        var sourceRoot = Path.Combine(_tempRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        var generated = Path.Combine(sourceRoot, "generated.epub");
        var unrelated = Path.Combine(sourceRoot, "Dune.epub");
        File.WriteAllText(generated, "fixture");
        File.WriteAllText(unrelated, "user media");
        var options = Options.Create(new IngestionOptions { LibraryRoot = libraryRoot });

        DevFixtureManifestStore.Record(options, [generated], NullLogger.Instance);

        var entries = DevFixtureManifestStore.Read(options, NullLogger.Instance);
        Assert.Equal([Path.GetFullPath(generated)], entries);
        Assert.DoesNotContain(Path.GetFullPath(unrelated), entries, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("accounts", true)]
    [InlineData("profiles", true)]
    [InlineData("profile_sequence_preferences", true)]
    [InlineData("metadata_providers", true)]
    [InlineData("provider_config", true)]
    [InlineData("application_permission_grants", true)]
    [InlineData("view_sources", true)]
    [InlineData("local_items", true)]
    [InlineData("works", false)]
    [InlineData("media_assets", false)]
    [InlineData("ingestion_batches", false)]
    [InlineData("metadata_claims", false)]
    public void LibraryReset_PreservesConfigurationAndClearsGeneratedLibraryTables(string table, bool expected)
    {
        var method = typeof(DevHarnessResetService).GetMethod(
            "ShouldPreserveConfigurationTable",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.Equal(expected, Assert.IsType<bool>(method!.Invoke(null, [table])));
    }

    [Theory]
    [InlineData("search_index_data", true)]
    [InlineData("search_index_idx", true)]
    [InlineData("search_index", false)]
    [InlineData("search_results_cache", false)]
    public void LibraryReset_DoesNotDeleteFtsShadowTablesDirectly(string table, bool expected)
    {
        var method = typeof(DevHarnessResetService).GetMethod(
            "IsSearchIndexShadowTable",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.Equal(expected, Assert.IsType<bool>(method!.Invoke(null, [table])));
    }

    [Fact]
    public void RunFullIntegration_DefaultsToGeneratedStateAndSupportsExplicitFullWipe()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"tools\Run-FullIntegration.ps1"));

        Assert.Contains("[ValidateSet(\"generated-state\", \"full\")]", source, StringComparison.Ordinal);
        Assert.Contains("[string]$WipeScope = \"generated-state\"", source, StringComparison.Ordinal);
        Assert.Contains("[int]$TimeoutSec = 10800", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$MusicOnly", source, StringComparison.Ordinal);
        Assert.Contains("$Types = @(\"music\")", source, StringComparison.Ordinal);
        Assert.Contains("wipeScope=$([uri]::EscapeDataString($WipeScope))", source, StringComparison.Ordinal);
        Assert.Contains("$WipeScope -eq \"full\"", source, StringComparison.Ordinal);
        Assert.Contains("stages=$Stages", source, StringComparison.Ordinal);
        Assert.Contains("-TimeoutSec $TimeoutSec", source, StringComparison.Ordinal);
        Assert.Contains("-TimeoutSec 30", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewPhotoHarness_IsDevelopmentOnlyAndKeepsLicenseAndPeopleProvenance()
    {
        var endpoints = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\DevSeedEndpoints.cs"));
        var harness = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\ViewPhotoHarnessService.cs"));

        Assert.Contains("group.MapPost(\"/view-photo-harness\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("Wikimedia Commons", harness, StringComparison.Ordinal);
        Assert.Contains("SourcePage", harness, StringComparison.Ordinal);
        Assert.Contains("License", harness, StringComparison.Ordinal);
        Assert.Contains("Author", harness, StringComparison.Ordinal);
        Assert.Contains("UpdateCapturedAtAsync", harness, StringComparison.Ordinal);
        Assert.Contains("resetToEmbedded: false", harness, StringComparison.Ordinal);
        Assert.Contains("manual test fixture; not face recognition", harness, StringComparison.Ordinal);
        Assert.Contains("Sydney, Australia", harness, StringComparison.Ordinal);
        Assert.Contains("Machu Picchu, Peru", harness, StringComparison.Ordinal);
        Assert.Contains("Cape Town, South Africa", harness, StringComparison.Ordinal);
        Assert.Contains("New York, United States", harness, StringComparison.Ordinal);
        Assert.Contains("new DateTimeOffset(2004", harness, StringComparison.Ordinal);
        Assert.Contains("new DateTimeOffset(2024", harness, StringComparison.Ordinal);
        Assert.Contains("LocalAssetLifecycleState.Trashed", harness, StringComparison.Ordinal);
        Assert.Contains("LocalAssetLifecycleState.Active", harness, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupPreflight_UsesCanonicalStorageResolversForEmptyDataRoot()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\Services\SetupPreflightService.cs"));

        Assert.Contains("new AssetPathService(", source, StringComparison.Ordinal);
        Assert.Contains("core.DataRoot).DataRoot", source, StringComparison.Ordinal);
        Assert.Contains("TuvimaDataPathResolver.ResolveDatabasePath(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Probe(\"artwork\", \"Artwork and generated data\", core.DataRoot",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DevHarnessReset_RecreatesConfiguredSourceFoldersAfterWipe()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\DevHarnessResetService.cs"));

        Assert.Contains("EnsureConfiguredSourcePathsExist(details)", source, StringComparison.Ordinal);
        Assert.Contains("EnsureConfiguredSourcePathsExist(details ?? [])", source, StringComparison.Ordinal);
        Assert.Contains("Directory.CreateDirectory(srcPath)", source, StringComparison.Ordinal);
        Assert.Contains("EnumerateConfiguredSourcePaths().Distinct", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationHarness_TracksStageAwareWaitBudgetsAndOwnedCounts()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\IntegrationTestEndpoints.cs"));

        Assert.Contains("ResolveIngestionWaitPlan", source, StringComparison.Ordinal);
        Assert.Contains("IngestionWaitStages", source, StringComparison.Ordinal);
        Assert.Contains("CompletedAfterTimeout", source, StringComparison.Ordinal);
        Assert.Contains("Ingestion Wait Budgets", source, StringComparison.Ordinal);
        Assert.Contains("owned, {mt.CatalogOnlyCount} catalog-only", source, StringComparison.Ordinal);
        Assert.Contains("allItems.Items.Where(IsOwnedValidationItem)", source, StringComparison.Ordinal);
        Assert.Contains("ProviderHealthTimeoutSeconds", source, StringComparison.Ordinal);
        Assert.Contains("elapsed {sw.Elapsed.TotalSeconds:F1}s", source, StringComparison.Ordinal);
        Assert.Contains("ProviderHealthDetails", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationHarness_UsesMusicBrainzAsMusicIdentityAndAppleAsEnrichment()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\IntegrationTestEndpoints.cs"));
        var seedSource = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\DevSeedEndpoints.cs"));

        Assert.Contains("[\"music\"] = [\"musicbrainz\", \"apple_api\"]", source, StringComparison.Ordinal);
        Assert.Contains("(\"Bohemian Rhapsody\", \"Queen\", \"musicbrainz\", \"Music identity\"", source, StringComparison.Ordinal);
        Assert.Contains("(\"Bohemian Rhapsody\", \"Queen\", \"apple_api\", \"Music enrichment\"", source, StringComparison.Ordinal);
        Assert.Contains("Artist = enumType == MediaType.Music ? creator : null", source, StringComparison.Ordinal);
        Assert.Contains("\"music\" => \"musicbrainz\"", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedQid: \"Q11986\"", seedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationHarness_SeedsAndValidatesRepeatCreatorsAndCrossMediaPeople()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\IntegrationTestEndpoints.cs"));
        var seedSource = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\DevSeedEndpoints.cs"));

        Assert.Contains("\"The Expanse books/audiobooks/TV\"", source, StringComparison.Ordinal);
        Assert.Contains("[\"books\", \"audiobooks\", \"tv\"]", source, StringComparison.Ordinal);
        Assert.Contains("ValidatePersonCoverageAsync", source, StringComparison.Ordinal);
        Assert.Contains("new PersonCreditRequirement(\"books\", \"Author\", 2)", source, StringComparison.Ordinal);
        Assert.Contains("new PersonCreditRequirement(\"music\", \"Artist\", 2)", source, StringComparison.Ordinal);
        Assert.Contains("MatchesHarnessContributorRole(credit.Role, requirement.Role)", source, StringComparison.Ordinal);
        Assert.Contains("SELECT value FROM canonical_value_arrays WHERE entity_id = parent.id", source, StringComparison.Ordinal);
        Assert.Contains("SELECT value FROM canonical_value_arrays WHERE entity_id IN (child.id, asset.id)", source, StringComparison.Ordinal);
        Assert.Contains("\"Andy Serkis\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Bryan Cranston\"", source, StringComparison.Ordinal);
        Assert.Contains("ValidateOutwardDetailRoutesAsync", source, StringComparison.Ordinal);
        Assert.Contains("\"Audiobook series\"", source, StringComparison.Ordinal);
        Assert.Contains("enforceSharedCollectionContract: true", source, StringComparison.Ordinal);
        Assert.Contains("SystemViewGroupIdentity.CreateId(group, \"Audiobooks\", \"series\")", source, StringComparison.Ordinal);
        Assert.Contains("SystemViewGroupIdentity.CreateId(group, \"Books\", \"series\")", source, StringComparison.Ordinal);
        Assert.Contains("detail.ContributorGroups.Count == 0", source, StringComparison.Ordinal);
        Assert.Contains("Rise of the Planet of the Apes (2011) {imdb-tt1318514}.mp4", seedSource, StringComparison.Ordinal);
        Assert.Contains("Drive (2011) {imdb-tt0780504}.mp4", seedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("dune-films/", seedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("batman-nolan/", seedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("cross-media-people/", seedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("middle-earth/", seedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationHarness_ReportShowsUtf8IssueCategoriesAndProviderProvenance()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\IntegrationTestEndpoints.cs"));
        var script = File.ReadAllText(GetRepoFilePath(@"tools\Run-FullIntegration.ps1"));

        Assert.Contains("text/html; charset=utf-8", source, StringComparison.Ordinal);
        Assert.Contains("File.WriteAllTextAsync(filePath, html, Encoding.UTF8, ct)", source, StringComparison.Ordinal);
        Assert.Contains("Results.Content(html, \"text/html; charset=utf-8\", Encoding.UTF8)", source, StringComparison.Ordinal);
        Assert.Contains("CategorizeIssue", source, StringComparison.Ordinal);
        Assert.Contains("Product data failure", source, StringComparison.Ordinal);
        Assert.Contains("Harness expectation drift", source, StringComparison.Ordinal);
        Assert.Contains("Provider/runtime transient", source, StringComparison.Ordinal);
        Assert.Contains("Dashboard/API tracking", source, StringComparison.Ordinal);
        Assert.Contains("Identity Provider", source, StringComparison.Ordinal);
        Assert.Contains("Enrichment Providers", source, StringComparison.Ordinal);
        Assert.Contains("Bridge Provider", source, StringComparison.Ordinal);
        Assert.Contains("Bridge ID", source, StringComparison.Ordinal);
        Assert.Contains("ArtworkCompleteness", source, StringComparison.Ordinal);
        Assert.Contains("ResolveIdentityProvider", source, StringComparison.Ordinal);
        Assert.Contains("MetadataFieldConstants.IdentityProvider", source, StringComparison.Ordinal);
        Assert.Contains("ResolveEnrichmentProviders", source, StringComparison.Ordinal);
        Assert.Contains("ProviderMatchesExpectation", source, StringComparison.Ordinal);
        Assert.Contains("ProviderForExpectationComparison", source, StringComparison.Ordinal);
        Assert.Contains("UsesIdentityProviderExpectation", source, StringComparison.Ordinal);
        Assert.Contains("identity provider", source, StringComparison.Ordinal);
        Assert.Contains("musicbrainz_recording_id", source, StringComparison.Ordinal);
        Assert.Contains("apple_music_id", source, StringComparison.Ordinal);
        Assert.Contains("BuildHarnessTitleMediaKeys", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeHarnessTitleKey", source, StringComparison.Ordinal);
        Assert.Contains("IdentityJobState", source, StringComparison.Ordinal);
        Assert.Contains("TextEncodingRepair.RepairMojibake", source, StringComparison.Ordinal);
        Assert.Contains("CharUnicodeInfo.GetUnicodeCategory", source, StringComparison.Ordinal);
        Assert.Contains("-OutFile $OutputPath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationHarness_ReadsCurrentSchemaGuidBlobs()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\IntegrationTestEndpoints.cs"));

        Assert.Contains("TryReadCurrentGuid", source, StringComparison.Ordinal);
        Assert.Contains("GuidSql.FromDb(value)", source, StringComparison.Ordinal);
        Assert.Contains("GuidSql.ToBlob(collectionId)", source, StringComparison.Ordinal);
        Assert.Contains("entityIds = ids.Select(GuidSql.ToBlob).ToArray()", source, StringComparison.Ordinal);
        Assert.Contains("QueryAsync<WorkAssetIdRow>", source, StringComparison.Ordinal);
        Assert.Contains("QueryAsync<AssetPathRow>", source, StringComparison.Ordinal);
        Assert.Contains("QueryAsync<OptionalArtworkRow>", source, StringComparison.Ordinal);
        Assert.Contains("QueryAsync<WorkHierarchyRow>", source, StringComparison.Ordinal);
        Assert.Contains("QueryAsync<CanonicalValueRow>", source, StringComparison.Ordinal);
        Assert.Contains("QueryAsync<PreferredArtworkRow>", source, StringComparison.Ordinal);
        Assert.Contains("provider_id AS ProviderIdBlob", source, StringComparison.Ordinal);
        Assert.Contains("winning_provider_id AS WinningProviderIdBlob", source, StringComparison.Ordinal);
        Assert.Contains("GuidSql.FromDb(ProviderIdBlob)", source, StringComparison.Ordinal);
        Assert.Contains("GuidSql.FromDb(WinningProviderIdBlob)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QueryAsync<(string WorkId, string AssetId)>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QueryAsync<(string EntityId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Guid.Parse(row.EntityId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Guid.TryParse(row.EntityId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationHarness_PreflightsManifestDrivenIdentityExpectations()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\IntegrationTestEndpoints.cs"));
        var seedSource = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\DevSeedEndpoints.cs"));

        Assert.Contains("BuildExpectationPreflight", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedIdentityStatus", seedSource, StringComparison.Ordinal);
        Assert.Contains("ExpectedResolved", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedExactQid", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedIdentityOnly", source, StringComparison.Ordinal);
        Assert.Contains("Preflight Expected Outcomes", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationHarness_DoesNotAcceptQidNoMatchForExpectedFixtures()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\DevSupport\IntegrationTestEndpoints.cs"));

        Assert.Contains("expectsIdentification", source, StringComparison.Ordinal);
        Assert.Contains("? hasQid && exactQidMatches", source, StringComparison.Ordinal);
        Assert.Contains(": hasQid || hasIdentifiedState", source, StringComparison.Ordinal);
        Assert.Contains("expected a Wikidata QID", source, StringComparison.Ordinal);
        Assert.Contains("QID optional", source, StringComparison.Ordinal);
        Assert.DoesNotContain("accepted re-check state", source, StringComparison.Ordinal);
        Assert.DoesNotContain("hasQid || retainedRetailWithoutQid", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconciliationReport_EmitsPerFixtureQidAndProviderAlignment()
    {
        var report = new ReconciliationReport
        {
            Total = 1,
            Matched = 1,
            Items =
            [
                new ReconciliationReportItem(
                    FileName: "Dune",
                    MediaType: "Books",
                    ExpectedStatus: "Identified as Q190192",
                    ActualStatus: "Identified",
                    ExpectedQid: "Q190192",
                    ActualQid: "Q190192",
                    ExpectedProvider: "apple_api",
                    ActualProvider: "apple_api",
                    ExpectedTrigger: null,
                    ActualTrigger: null,
                    Classification: "Match",
                    Matched: true,
                    Reason: null)
            ],
        };

        var json = report.ToJson();

        Assert.Contains("\"file_name\": \"Dune\"", json, StringComparison.Ordinal);
        Assert.Contains("\"media_type\": \"Books\"", json, StringComparison.Ordinal);
        Assert.Contains("\"expected_qid\": \"Q190192\"", json, StringComparison.Ordinal);
        Assert.Contains("\"actual_qid\": \"Q190192\"", json, StringComparison.Ordinal);
        Assert.Contains("\"expected_provider\": \"apple_api\"", json, StringComparison.Ordinal);
        Assert.Contains("\"actual_provider\": \"apple_api\"", json, StringComparison.Ordinal);
        Assert.Contains("\"classification\": \"Match\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void OverallPass_FailsWhenReconciliationHasMismatches()
    {
        var report = CreateTestReport();
        var reconciliation = CreateReconciliationSummary();
        AddToListProperty(reconciliation, "Mismatches", CreateReconciliationItemResult());
        SetProperty(report, "Reconciliation", reconciliation);

        Assert.False(GetOverallPass(report));
    }

    [Fact]
    public void OverallPass_FailsWhenExpectedReconciliationCountsDrift()
    {
        var report = CreateTestReport();
        AddToListProperty(report, "IssuesFound", "Reconciliation count mismatch: expected 97 exact QID, actual 96.");

        Assert.False(GetOverallPass(report));
    }

    [Fact]
    public void OverallPass_FailsWhenARequestedMediaTypeIsSkipped()
    {
        var report = CreateTestReport();
        AddToSetProperty(report, "RequestedTypes", "music");
        AddToDictionaryProperty(report, "SkippedTypes", "music", "Provider 'apple_api' unavailable");

        Assert.False(GetOverallPass(report));
    }

    [Fact]
    public void OverallPass_IgnoresMediaTypesSkippedOnlyBecauseTheyWereNotRequested()
    {
        var report = CreateTestReport();
        AddToSetProperty(report, "RequestedTypes", "books");
        AddToDictionaryProperty(report, "SkippedTypes", "music", "Not requested");

        Assert.True(GetOverallPass(report));
    }

    [Fact]
    public void OverallPass_FailsWhenSummarySectionHasFailures()
    {
        var report = CreateTestReport();
        AddToListProperty(report, "MediaTypeResults", CreateMediaTypeResult(count: 0, failed: 0));

        Assert.False(GetOverallPass(report));
    }

    [Fact]
    public void OverallPass_FailsWhenIngestionProgressSnapshotHasImpossibleCounts()
    {
        var report = CreateTestReport();
        AddToListProperty(report, "IngestionProgressSnapshots", CreateIngestionProgressSnapshot(progressPercent: 125));

        Assert.False(GetOverallPass(report));
    }

    [Fact]
    public void OverallPass_AllowsIngestionCompletedAfterSoftWaitBudgetTimeout()
    {
        var report = CreateTestReport();
        AddToListProperty(report, "IngestionWaitStages", CreateIngestionWaitStage(completed: true, timedOut: true));

        Assert.True(GetOverallPass(report));
    }

    [Fact]
    public void OverallPass_FailsWhenIngestionWaitStageHardTimesOut()
    {
        var report = CreateTestReport();
        AddToListProperty(report, "IngestionWaitStages", CreateIngestionWaitStage(completed: false, timedOut: true));

        Assert.False(GetOverallPass(report));
    }

    [Fact]
    public void OverallPass_FailsWhenCrossMediaSeriesValidationFails()
    {
        var report = CreateTestReport();
        AddToListProperty(report, "CrossMediaSeriesChecks", CreateCrossMediaSeriesCheckResult());

        Assert.False(GetOverallPass(report));
    }

    [Fact]
    public void OverallPass_FailsWhenPersonCoverageValidationFails()
    {
        var report = CreateTestReport();
        AddToListProperty(report, "PersonCoverageChecks", CreatePersonCoverageCheckResult());

        Assert.False(GetOverallPass(report));
    }

    [Fact]
    public void OverallPass_FailsWhenAnOutwardDetailRouteDoesNotResolve()
    {
        var report = CreateTestReport();
        AddToListProperty(report, "DetailRouteChecks", CreateDetailRouteCheckResult());

        Assert.False(GetOverallPass(report));
    }

    [Fact]
    public void GenerateHtmlReport_DoesNotShowAllPassWhenReconciliationMismatches()
    {
        var report = CreateTestReport();
        var reconciliation = CreateReconciliationSummary();
        AddToListProperty(reconciliation, "Mismatches", CreateReconciliationItemResult());
        SetProperty(reconciliation, "ExpectedTotal", 1);
        SetProperty(report, "Reconciliation", reconciliation);

        var html = InvokeGenerateHtmlReport(report);

        Assert.Contains("VALIDATION FAILED", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ALL PASS", html, StringComparison.Ordinal);
    }

    [Fact]
    public void HasCentralPreferredArtwork_AcceptsCentralAssetStorePaths()
    {
        var ownerEntityId = Guid.NewGuid();
        var assetPaths = new AssetPathService(_tempRoot);
        var coverPath = assetPaths.GetCentralAssetPath("Work", ownerEntityId, "CoverArt", Guid.NewGuid(), ".jpg");
        AssetPathService.EnsureDirectory(coverPath);
        File.WriteAllBytes(coverPath, [1, 2, 3]);

        var preferredRecord = CreatePreferredArtworkRecord(coverPath);

        var result = InvokeHasCentralPreferredArtwork(ownerEntityId, preferredRecord, assetPaths, "Work", "CoverArt");

        Assert.True(result);
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, ".data", "images")));
    }

    [Fact]
    public void HasCentralPreferredArtwork_RejectsLegacyPathLocations()
    {
        var ownerEntityId = Guid.NewGuid();
        var assetPaths = new AssetPathService(_tempRoot);
        var legacyPath = Path.Combine(_tempRoot, ".data", "images", "works", "legacy", "cover.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllBytes(legacyPath, [4, 5, 6]);

        var preferredRecord = CreatePreferredArtworkRecord(legacyPath);

        var result = InvokeHasCentralPreferredArtwork(ownerEntityId, preferredRecord, assetPaths, "Work", "CoverArt");

        Assert.False(result);
    }

    [Fact]
    public void ShouldRequireSidecarArtwork_ReturnsFalseWhenArtworkExportIsDisabled()
    {
        var policy = new LibraryStoragePolicy
        {
            ArtworkExport = false,
            ExportProfile = new SidecarExportProfile { Artwork = false },
        };

        var result = InvokeShouldRequireSidecarArtwork(policy);

        Assert.False(result);
    }

    [Fact]
    public void ShouldRequireSidecarArtwork_ReturnsTrueWhenArtworkExportIsEnabled()
    {
        var policy = new LibraryStoragePolicy
        {
            ArtworkExport = true,
            ExportProfile = new SidecarExportProfile { Artwork = false },
        };

        var result = InvokeShouldRequireSidecarArtwork(policy);

        Assert.True(result);
    }

    [Fact]
    public void DescribeFileSystemCheck_ReportsCentralAssetStoreWhenStoredArtworkIsMissing()
    {
        var check = CreateFileSystemCheckResult();
        SetProperty(check, "FileExists", true);
        SetProperty(check, "LocationMatchesExpectation", true);
        SetProperty(check, "RequiresStoredArtwork", true);
        SetProperty(check, "HasStoredCover", false);
        SetProperty(check, "HasStoredCoverSmall", true);
        SetProperty(check, "HasStoredCoverMedium", true);
        SetProperty(check, "HasStoredCoverLarge", true);
        SetProperty(check, "HasStoredPalette", true);

        var result = InvokeDescribeFileSystemCheck(check);

        Assert.Equal("Stored artwork renditions or palette metadata are incomplete in the central asset store", result);
    }

    private static bool InvokeHasCentralPreferredArtwork(
        Guid ownerEntityId,
        object preferredRecord,
        AssetPathService assetPaths,
        string ownerKind,
        string assetType)
    {
        var method = typeof(IntegrationTestEndpoints).GetMethod(
            "HasCentralPreferredArtwork",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(Guid), preferredRecord.GetType());
        var dictionary = (IDictionary)Activator.CreateInstance(dictionaryType)!;
        dictionary.Add(ownerEntityId, preferredRecord);

        var result = method!.Invoke(null, [ownerEntityId, dictionary, assetPaths, ownerKind, assetType]);
        return Assert.IsType<bool>(result);
    }

    private static object CreatePreferredArtworkRecord(
        string localImagePath,
        string? localImagePathSmall = null,
        string? localImagePathMedium = null,
        string? localImagePathLarge = null,
        string? primaryHex = "#112233",
        string? secondaryHex = "#223344",
        string? accentHex = "#334455")
    {
        var type = typeof(IntegrationTestEndpoints).GetNestedType(
            "PreferredArtworkRecord",
            BindingFlags.NonPublic);

        Assert.NotNull(type);
        return Activator.CreateInstance(type!, [localImagePath, localImagePathSmall, localImagePathMedium, localImagePathLarge, primaryHex, secondaryHex, accentHex])!;
    }

    private static bool InvokeShouldRequireSidecarArtwork(LibraryStoragePolicy policy)
    {
        var method = typeof(IntegrationTestEndpoints).GetMethod(
            "ShouldRequireSidecarArtwork",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = method!.Invoke(null, [policy]);
        return Assert.IsType<bool>(result);
    }

    private static object CreateFileSystemCheckResult()
    {
        var type = typeof(IntegrationTestEndpoints).GetNestedType(
            "FileSystemCheckResult",
            BindingFlags.NonPublic);

        Assert.NotNull(type);
        return Activator.CreateInstance(type!, nonPublic: true)!;
    }

    private static object CreateTestReport()
    {
        var type = typeof(IntegrationTestEndpoints).GetNestedType(
            "TestReport",
            BindingFlags.NonPublic);

        Assert.NotNull(type);
        return Activator.CreateInstance(type!, nonPublic: true)!;
    }

    private static object CreateReconciliationSummary()
    {
        var type = typeof(IntegrationTestEndpoints).GetNestedType(
            "ReconciliationSummary",
            BindingFlags.NonPublic);

        Assert.NotNull(type);
        return Activator.CreateInstance(type!, nonPublic: true)!;
    }

    private static object CreateReconciliationItemResult()
    {
        var type = typeof(IntegrationTestEndpoints).GetNestedType(
            "ReconciliationItemResult",
            BindingFlags.NonPublic);

        Assert.NotNull(type);
        var item = Activator.CreateInstance(type!, nonPublic: true)!;
        SetProperty(item, "Title", "Fixture");
        SetProperty(item, "MediaType", "Books");
        SetProperty(item, "Expected", "Identified");
        SetProperty(item, "Actual", "InReview");
        SetProperty(item, "Classification", "UnexpectedReview");
        return item;
    }

    private static object CreateMediaTypeResult(int count, int failed)
    {
        var type = typeof(IntegrationTestEndpoints).GetNestedType(
            "MediaTypeResult",
            BindingFlags.NonPublic);

        Assert.NotNull(type);
        var result = Activator.CreateInstance(type!, nonPublic: true)!;
        SetProperty(result, "MediaType", "Books");
        SetProperty(result, "Count", count);
        SetProperty(result, "Failed", failed);
        return result;
    }

    private static object CreateIngestionProgressSnapshot(double progressPercent)
    {
        var type = typeof(IntegrationTestEndpoints).GetNestedType(
            "IngestionProgressSnapshot",
            BindingFlags.NonPublic);

        Assert.NotNull(type);
        var snapshot = Activator.CreateInstance(type!, nonPublic: true)!;
        SetProperty(snapshot, "AssetCount", 10);
        SetProperty(snapshot, "ExpectedCount", 10);
        SetProperty(snapshot, "ResolvedCount", 10);
        SetProperty(snapshot, "WorkCount", 10);
        SetProperty(snapshot, "PendingCount", 0);
        SetProperty(snapshot, "ClaimCount", 20);
        SetProperty(snapshot, "ActiveJobCount", 0);
        SetProperty(snapshot, "ProgressPercent", progressPercent);
        return snapshot;
    }

    private static object CreateIngestionWaitStage(bool completed, bool timedOut)
    {
        var type = typeof(IntegrationTestEndpoints).GetNestedType(
            "IngestionWaitStageResult",
            BindingFlags.NonPublic);

        Assert.NotNull(type);
        var stage = Activator.CreateInstance(type!, nonPublic: true)!;
        SetProperty(stage, "StageKey", "file_registration");
        SetProperty(stage, "Label", "File registration");
        SetProperty(stage, "BudgetSeconds", 1d);
        SetProperty(stage, "ElapsedSeconds", 2d);
        SetProperty(stage, "Completed", completed);
        SetProperty(stage, "TimedOut", timedOut);
        SetProperty(stage, "CompletedAfterTimeout", completed && timedOut);
        return stage;
    }

    private static object CreateCrossMediaSeriesCheckResult()
    {
        var type = typeof(IntegrationTestEndpoints).GetNestedType(
            "CrossMediaSeriesCheckResult",
            BindingFlags.NonPublic);

        Assert.NotNull(type);
        var result = Activator.CreateInstance(type!, nonPublic: true)!;
        SetProperty(result, "Name", "Fixture");
        SetProperty(result, "OwnedCount", 1);
        return result;
    }

    private static object CreatePersonCoverageCheckResult()
    {
        var type = typeof(IntegrationTestEndpoints).GetNestedType(
            "PersonCoverageCheckResult",
            BindingFlags.NonPublic);

        Assert.NotNull(type);
        var result = Activator.CreateInstance(type!, nonPublic: true)!;
        SetProperty(result, "Name", "Fixture Person");
        SetProperty(result, "PersonFound", false);
        return result;
    }

    private static object CreateDetailRouteCheckResult()
    {
        var type = typeof(IntegrationTestEndpoints).GetNestedType(
            "DetailRouteCheckResult",
            BindingFlags.NonPublic);

        Assert.NotNull(type);
        var result = Activator.CreateInstance(type!, nonPublic: true)!;
        SetProperty(result, "Surface", "Audiobook series");
        SetProperty(result, "Title", "Fixture Series");
        SetProperty(result, "Route", "/details/collection/00000000-0000-0000-0000-000000000000?context=listen");
        SetProperty(result, "Resolved", false);
        return result;
    }

    private static string InvokeDescribeFileSystemCheck(object check)
    {
        var method = typeof(IntegrationTestEndpoints).GetMethod(
            "DescribeFileSystemCheck",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = method!.Invoke(null, [check]);
        return Assert.IsType<string>(result);
    }

    private static string InvokeGenerateHtmlReport(object report)
    {
        var method = typeof(IntegrationTestEndpoints).GetMethod(
            "GenerateHtmlReport",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = method!.Invoke(null, [report]);
        return Assert.IsType<string>(result);
    }

    private static bool GetOverallPass(object instance)
    {
        var property = instance.GetType().GetProperty("OverallPass", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return Assert.IsType<bool>(property!.GetValue(instance));
    }

    private static void SetProperty(object instance, string name, object value)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        property!.SetValue(instance, value);
    }

    private static void AddToListProperty(object instance, string name, object value)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        var list = Assert.IsAssignableFrom<IList>(property!.GetValue(instance));
        list.Add(value);
    }

    private static void AddToSetProperty(object instance, string name, string value)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        var set = Assert.IsAssignableFrom<ISet<string>>(property!.GetValue(instance));
        set.Add(value);
    }

    private static void AddToDictionaryProperty(object instance, string name, string key, string value)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        var dictionary = Assert.IsAssignableFrom<IDictionary<string, string>>(property!.GetValue(instance));
        dictionary[key] = value;
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
