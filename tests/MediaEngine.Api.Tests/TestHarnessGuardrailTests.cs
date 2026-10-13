namespace MediaEngine.Api.Tests;

public sealed class TestHarnessGuardrailTests
{
    [Fact]
    public void Generator_KeepsVideoAndMusicFixturesIndependentOfFfmpeg()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GenerateTestEpubs", "Program.cs"));

        Assert.Contains("var movieSeries", source);
        Assert.Contains("var tvSeries", source);
        Assert.Contains("var musicTracks", source);
        Assert.Contains("CreateMp4Fixture", source);
        Assert.Contains("CreateMp3Fixture", source);
        Assert.Contains("CreateMinimalMp4", source);
        Assert.Contains("CreateMinimalMp3", source);
        Assert.DoesNotContain("failed += movieSeries.Length", source);
        Assert.DoesNotContain("failed += tvSeries.Length", source);
        Assert.DoesNotContain("failed += musicTracks.Length", source);
    }

    [Fact]
    public void Generator_DeclaresSeriesAndMusicPeopleForEnrichmentValidation()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GenerateTestEpubs", "Program.cs"));

        Assert.Contains("\"Aaron Paul\"", source);
        Assert.Contains("\"Q302491\"", source);
        Assert.Contains("\"Anna Gunn\"", source);
        Assert.Contains("\"Q271050\"", source);
        Assert.Contains("\"David Bowie\"", source);
        Assert.Contains("\"Q5383\"", source);
    }

    [Fact]
    public void Generator_KeepsExpectedWatchFixturesInFilesystemHarness()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GenerateTestEpubs", "Program.cs"));

        Assert.Contains("Arrival (2016) {imdb-tt2543164}.mp4", source);
        Assert.Contains("The Shawshank Redemption (1994) {imdb-tt0111161}.mp4", source);
        Assert.Contains("Shogun S01E01 Anjin (2024).mp4", source);
        Assert.Contains("large ? \"138\" : \"53\"", source);
    }

    [Fact]
    public void Generator_WritesPerFileIdentityExpectationsToManifest()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GenerateTestEpubs", "Program.cs"));

        Assert.Contains("expected_identity = new", source);
        Assert.Contains("expected_status = expected.ExpectedStatus", source);
        Assert.Contains("expected_work_qid = expected.ExpectedWorkQid", source);
        Assert.Contains("expected_bridge_ids = expected.ExpectedBridgeIds", source);
        Assert.Contains("expected_retail_provider = expected.ExpectedRetailProvider", source);
        Assert.Contains("expected_review_trigger = expected.ExpectedReviewTrigger", source);
        Assert.Contains("Known real-world fixture should resolve to a non-placeholder Wikidata QID.", source);
        Assert.Contains("ExpectedStatus: \"ResolvedQid\"", source);
        Assert.Contains("ExpectedStatus: \"Duplicate\"", source);
        Assert.Contains("ExpectedStatus: \"Corrupt\"", source);
        Assert.Contains("ExpectedStatus: \"Skipped\"", source);
    }

    [Fact]
    public void Generator_HasLargeStressCorpusAcrossAllPrimaryMediaTypes()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GenerateTestEpubs", "Program.cs"));
        var resetScript = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "test-data", "reset-and-generate.ps1"));

        Assert.Contains("--large", source);
        Assert.Contains("var largeBooks", source);
        Assert.Contains("var largeAudiobooks", source);
        Assert.Contains("var largeMovies", source);
        Assert.Contains("var largeTv", source);
        Assert.Contains("var largeMusic", source);
        Assert.Contains("var largeComics", source);
        Assert.Contains("var audiobooksDir = Path.Combine(watchRoot, \"audiobooks\")", source);
        Assert.Contains("var generalDir = Path.Combine(watchRoot, \"general\")", source);
        Assert.Contains("Path.Combine(audiobooksDir, spec.FileName)", source);
        Assert.Contains("Path.Combine(audiobooksDir, \"large-audiobooks\"", source);
        Assert.Contains("audiobooks_directory = audiobooksDir", source);
        Assert.Contains("general_directory = generalDir", source);
        Assert.DoesNotContain("var generalDir = watchRoot", source);
        Assert.DoesNotContain("Path.Combine(booksDir, \"large-audiobooks\"", source);
        Assert.Contains("The Shining (1980) {imdb-tt0081505}.mp4", source);
        Assert.Contains("Blade Runner (1982) {imdb-tt0083658}.mp4", source);
        Assert.Contains("Foundation S01E01 The Emperor's Peace", source);
        Assert.Contains("Breaking Bad S02E01 Seven Thirty-Seven", source);
        Assert.Contains("Game of Thrones S02E01 The North Remembers", source);
        Assert.Contains("Saga 003 (2012).cbz", source);
        Assert.Contains("Batman #405: Year One Part 2 - War Is Declared", source);
        Assert.Contains("The Sandman #2: Imperfect Hosts", source);
        Assert.Contains("Akira 002 (1982).cbz", source);
        Assert.Contains("Hans Zimmer", source);
        Assert.Contains("large ? \"138\" : \"53\"", source);
        Assert.Contains("[switch]$Large", resetScript);
        Assert.Contains(@"""C:\temp\tuvima-watch""", resetScript);
    }

    [Fact]
    public void Generator_MusicCorpusCoversProblemAlbumsAndArtistsWithMultipleAlbums()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GenerateTestEpubs", "Program.cs"));

        Assert.Contains("Path.Combine(\"David Bowie\", \"Heroes\")", source);
        Assert.Contains("Path.Combine(\"David Bowie\", \"Hunky Dory\")", source);
        Assert.Contains("Path.Combine(\"The Beatles\", \"Abbey Road\")", source);
        Assert.Contains("Path.Combine(\"The Beatles\", \"Sgt Peppers\")", source);
        Assert.Contains("Path.Combine(\"Kendrick Lamar\", \"DAMN\")", source);
        Assert.Contains("Path.Combine(\"Kendrick Lamar\", \"good kid maad city\")", source);
        Assert.Contains("Path.Combine(\"Hans Zimmer\", \"Interstellar\")", source);
        Assert.Contains("Path.Combine(\"Hans Zimmer\", \"Inception\")", source);
        Assert.Contains("Artist appears across multiple owned albums", source);
    }

    [Fact]
    public void BookHarnessReadsTheConfiguredBooksLibrarySource()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "Test-BookIngestion.ps1"));

        Assert.Contains("Resolve-BookLibrarySourceFromSettings", source);
        Assert.Contains("Invoke-Api \"/settings/libraries\"", source);
        Assert.Contains("$_.category -eq \"Books\"", source);
        Assert.Contains("$_.id -eq $bookLibrary.primary_destination_source_id", source);
        Assert.DoesNotContain("$_.role", source);
        Assert.DoesNotContain("[\"watch_directories\"]", source);
        Assert.DoesNotContain("[\"watch_directory\"]", source);
    }

    [Fact]
    public void DevSeedHarness_IncludesComicsAndMultiSeasonTvFixtures()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "DevSupport", "DevSeedEndpoints.cs"));

        Assert.Contains("Seven Thirty-Seven", source);
        Assert.Contains("SeasonNumber: 2", source);
        Assert.Contains("No Mas", source);
        Assert.Contains("SeasonNumber: 3", source);
        Assert.Contains("Batman: Year One Part 4", source);
        Assert.Contains("Saga Chapter Three", source);
        Assert.Contains("The Sandman: Imperfect Hosts", source);
        Assert.Contains("Dune Part One (2021) {imdb-tt1160419}.mp4", source);
        Assert.Contains("Dune Part Two (2024) {imdb-tt15239678}.mp4", source);
        Assert.Contains("Batman Begins (2005) {imdb-tt0372784}.mp4", source);
        Assert.Contains("The Dark Knight (2008) {imdb-tt0468569}.mp4", source);
        Assert.Contains("PartCount: 3", source);
        Assert.Contains("IncludeAbsMetadataSidecar: true", source);
        Assert.Contains("WEBRip-1080p v2", source);
        Assert.Contains("AlbumArtist: \"Queen\"", source);
    }

    [Fact]
    public void DevHarness_ExposesDisposableRealWorldEdgeFixtureManifest()
    {
        var endpoint = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "DevSupport", "DevSeedEndpoints.cs"));
        var builder = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "DevSupport", "RealWorldEdgeFixtureBuilder.cs"));

        Assert.Contains("/generate-ingestion-edge-fixtures", endpoint);
        Assert.Contains("MANIFEST.json", builder);
        Assert.Contains("metadata.json", builder);
        Assert.Contains("metadata.opf", builder);
        Assert.Contains(".fuse_hidden", builder);
        Assert.Contains(".azw3", builder);
        Assert.Contains(".iso", builder);
        Assert.Contains("WEBRip-1080p v2", builder);
        Assert.Contains("albumArtist: \"Queen\"", builder);
        Assert.Contains("Live Photo-style pair", builder);
    }

    [Fact]
    public void DevSeedAndIntegrationHarness_ExerciseMusicAlbumManifestsAndCoverArt()
    {
        var seed = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "DevSupport", "DevSeedEndpoints.cs"));
        var integration = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "DevSupport", "IntegrationTestEndpoints.cs"));

        Assert.Contains("must not match a 50-track box set", seed);
        Assert.Contains("Abbey Road manifest and cover regression", seed);
        Assert.Contains("all other album tracks must show missing", seed);
        Assert.Contains("Interstellar MusicBrainz release and Cover Art Archive regression", seed);
        Assert.Contains("Hans Zimmer second album", seed);
        Assert.Contains("MusicAlbumChecks", integration);
        Assert.Contains("new MusicAlbumHarnessExpectation(\"Heroes\", \"David Bowie\", 1, 10)", integration);
        Assert.Contains("new MusicAlbumHarnessExpectation(\"Abbey Road\", \"The Beatles\", 2, 17)", integration);
        Assert.Contains("new MusicAlbumHarnessExpectation(\"DAMN.\", \"Kendrick Lamar\", 1, 14)", integration);
        Assert.Contains("ValidateMusicAlbumHarnessAsync", integration);
    }

    [Fact]
    public void Harnesses_GroupMultipleSourceFoldersIntoOneIngestionBatch()
    {
        var devSeed = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "DevSupport", "DevSeedEndpoints.cs"));
        var integration = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "DevSupport", "IntegrationTestEndpoints.cs"));

        Assert.Contains("await ingestionEngine.ScanDirectories(scanTargets", devSeed, StringComparison.Ordinal);
        Assert.Contains("await ingestionEngine.ScanDirectories(scanTargets", integration, StringComparison.Ordinal);
        Assert.DoesNotContain("await ingestionEngine.ScanDirectory(target.Path", devSeed, StringComparison.Ordinal);
        Assert.DoesNotContain("await ingestionEngine.ScanDirectory(sourcePath", integration, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationHarness_ValidatesCrossMediaSeriesWithMediaScopedCounts()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "DevSupport", "IntegrationTestEndpoints.cs"));

        Assert.Contains("CrossMediaSeriesChecks", source);
        Assert.Contains("CrossMediaSeriesExpectation", source);
        Assert.Contains("Dune audiobook/movie", source);
        Assert.Contains("Batman comic/movie", source);
        Assert.Contains("LOWER(REPLACE(w.media_type, ' ', ''))", source);
        Assert.Contains("includeMovies", source);
        Assert.Contains("owned_ma.file_path_root IS NOT NULL", source);
        Assert.Contains("new SeriesHarnessExpectation(\"Dune Chronicles\", \"Books/Audiobooks\", [\"books\", \"audiobooks\"], 1", source);
        Assert.DoesNotContain("new SeriesHarnessExpectation(\"The Lord of the Rings\", \"Movies\"", source);
    }

    [Fact]
    public void ComicVine_TitleDoesNotOverrideLocalIssueIdentity()
    {
        var config = File.ReadAllText(Path.Combine(FindRepoRoot(), "config", "providers", "comicvine.json"));
        var harness = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "DevSupport", "IntegrationTestEndpoints.cs"));

        Assert.Contains("\"claim_key\": \"title\"", config);
        Assert.Contains("\"confidence\": 0.7", config);
        Assert.Contains("HasComicIssueTitleDrift", harness);
        Assert.Contains("does not preserve its series and issue identity", harness);
    }

    [Fact]
    public void IntegrationHarness_AllowsGeneratedComicIssueDescriptionFallback()
    {
        var harness = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "DevSupport", "IntegrationTestEndpoints.cs"));

        Assert.Contains("AllowsGeneratedFallback", harness);
        Assert.Contains("MetadataFieldConstants.IssueDescription", harness);
        Assert.Contains("(HasAnyDescription || AllowsGeneratedFallback)", harness);
        Assert.Contains("!check.HasAnyDescription && !check.AllowsGeneratedFallback", harness);
    }

    [Fact]
    public void PersonEnrichment_ReadsTvParentWorkLineage()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Providers", "Workers", "PersonEnrichmentWorker.cs"));

        Assert.Contains("GetWorkLineageIdsByMediaAssetAsync", source);
        Assert.Contains("TV cast is", source);
    }

    [Fact]
    public void PersonEnrichment_DoesNotPairIndependentActorAndCharacterArrays()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Providers", "Workers", "PersonEnrichmentWorker.cs"));

        Assert.DoesNotContain("LinkActorCharacterMappingsFromCanonicalArraysAsync", source);
        Assert.Contains("independent cast and character arrays are not pairing evidence", source);
        Assert.Contains("LinkToCharacterAsync(person.Id, fictionalEntity.Id, workQid", source);
        Assert.Contains("ResolveActorCharacterArrayEntityIdsAsync", source);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
