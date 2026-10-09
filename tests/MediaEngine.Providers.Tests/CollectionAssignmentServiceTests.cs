using MediaEngine.Domain;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Tests;

public sealed class CollectionAssignmentServiceTests
{
    [Fact]
    public void ReconciliationAdapter_PromotesOnlyStructurallyImmediateP361Series()
    {
        var method = typeof(MediaEngine.Providers.Adapters.ReconciliationAdapter).GetMethod(
            "AddImmediateBridgeSeriesClaims",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = Assert.IsAssignableFrom<IReadOnlyList<ProviderClaim>>(method!.Invoke(null,
        [
            Array.Empty<ProviderClaim>(),
            new List<BridgeSeriesInfo>
            {
                new()
                {
                    SeriesQid = "Q19610143",
                    SeriesLabel = "The Expanse",
                    IsImmediateSeries = true,
                    SourcePropertyId = "P361",
                    Confidence = 0.75,
                },
                new()
                {
                    SeriesQid = "QUniverse",
                    SeriesLabel = "Broad universe",
                    IsImmediateSeries = false,
                    SourcePropertyId = "P361",
                    Confidence = 0.35,
                },
            },
        ]));

        Assert.Contains(result, claim => claim.Key == "series_qid" && claim.Value == "Q19610143::The Expanse");
        Assert.Contains(result, claim => claim.Key == MetadataFieldConstants.SeriesMembershipSource && claim.Value == "P361");
        Assert.DoesNotContain(result, claim => claim.Value.Contains("QUniverse", StringComparison.Ordinal));
    }

    [Fact]
    public void CollectionAssignment_UsesSeriesAsShelfAndDoesNotFallbackToBroadUniverse()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Providers/Services/CollectionAssignmentService.cs"));
        var resolveStart = source.IndexOf("private static ShelfIdentity? ResolveShelfIdentity", StringComparison.Ordinal);
        Assert.True(resolveStart >= 0);

        var resolveEnd = source.IndexOf("private static string? ResolveProviderKey", resolveStart, StringComparison.Ordinal);
        Assert.True(resolveEnd > resolveStart);

        var resolveSource = source[resolveStart..resolveEnd];
        Assert.Contains("TryGetQid(lookup, \"series\"", resolveSource, StringComparison.Ordinal);
        Assert.Contains("mediaType is MediaType.Comics", resolveSource, StringComparison.Ordinal);
        Assert.Contains("TryGetValue(lookup, \"wikidata_qid\"", resolveSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetQid(lookup, \"franchise\"", resolveSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetQid(lookup, \"fictional_universe\"", resolveSource, StringComparison.Ordinal);
        Assert.Contains("multiple shelves share them", source, StringComparison.Ordinal);
        Assert.Contains("FindShelfCollectionAsync(shelf", source, StringComparison.Ordinal);
        Assert.Contains("reassigning", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionAssignment_SupportsProviderBackedShelfIdentityBeforeQidUpgrade()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Providers/Services/CollectionAssignmentService.cs"));

        Assert.Contains("Task<CollectionAssignmentResult> AssignAsync", source, StringComparison.Ordinal);
        Assert.Contains("AssignWorkToCollectionAsync(workId.Value, collection.Id, ct)", source, StringComparison.Ordinal);
        Assert.Contains("internal sealed record ShelfIdentity", File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Providers/Models/ShelfIdentity.cs")), StringComparison.Ordinal);
        Assert.Contains("FindByRuleHashAsync(shelf.ProviderKey", source, StringComparison.Ordinal);
        Assert.Contains("collection.RuleHash, shelf.ProviderKey", source, StringComparison.Ordinal);
        Assert.Contains("tmdb:collection:{tmdbCollectionId}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("tmdb:tv:{tmdbTvId}", source, StringComparison.Ordinal);
        Assert.Contains("tvdb:tv:{tvdbId}", source, StringComparison.Ordinal);
        Assert.Contains("comicvine:volume:{comicVineVolumeId}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("comicvine:series:{comicVineId}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("keeping existing Wikidata movie shelf", source, StringComparison.Ordinal);
        Assert.Contains("UpgradeCollectionIdentityAsync(existingCollection, shelf", source, StringComparison.Ordinal);
        Assert.Contains("WikidataQid = shelf.Qid", source, StringComparison.Ordinal);
        Assert.Contains("RuleHash = shelf.ProviderKey", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionAssignment_UsesArrayBackedBasedOnForCrossMediaRollups()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Providers/Services/CollectionAssignmentService.cs"));

        Assert.Contains("MergeArrayBackedRelationshipHintsAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetAllByEntityAsync(entityId", source, StringComparison.Ordinal);
        Assert.Contains("arrays.TryGetValue(\"based_on\"", source, StringComparison.Ordinal);
        Assert.Contains("MetadataFieldConstants.Characters", source, StringComparison.Ordinal);
        Assert.Contains("AddArrayHint(lookup, \"based_on\"", source, StringComparison.Ordinal);
        Assert.Contains("\"fictional_universe\", \"based_on\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionFinalization_IsSharedByQidAndRetainedRetailPaths()
    {
        var quickHydration = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Providers/Workers/QuickHydrationWorker.cs"));
        var wikidataBridge = ReadWorkerSource("WikidataBridgeWorker");
        var finalizer = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Providers/Services/CollectionFinalizationService.cs"));

        Assert.Contains("CollectionFinalizationService", quickHydration, StringComparison.Ordinal);
        Assert.Contains("CollectionFinalizationReason.QuickHydration", quickHydration, StringComparison.Ordinal);
        Assert.Contains("CollectionFinalizationReason.QidResolved", wikidataBridge, StringComparison.Ordinal);
        Assert.Contains("CollectionFinalizationReason.RetainedRetailIdentity", wikidataBridge, StringComparison.Ordinal);
        Assert.Contains("ResolveParentCollectionAsync", finalizer, StringComparison.Ordinal);
        Assert.Contains("CollectionAssignmentFailed", finalizer, StringComparison.Ordinal);
    }

    [Fact]
    public void TmdbAdapter_AddsMovieCollectionClaimsForWatchShelves()
    {
        var adapter = ReadAdapterSource("ConfigDrivenAdapter");
        var config = File.ReadAllText(GetRepoFilePath(@"config/providers/tmdb.json"));

        Assert.Contains("AddTmdbMovieCollectionClaims", adapter, StringComparison.Ordinal);
        Assert.Contains("belongs_to_collection", adapter, StringComparison.Ordinal);
        Assert.Contains("\"tmdb_collection_id\"", config, StringComparison.Ordinal);
        Assert.Contains("\"tmdb_collection_name\"", config, StringComparison.Ordinal);
        Assert.Contains("MetadataFieldConstants.Series", adapter, StringComparison.Ordinal);
    }

    [Fact]
    public void SeriesManifestHydration_UsesImmediateSeriesBeforeBroaderCollectionFallback()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Providers/Services/WikidataSeriesManifestHydrationService.cs"));
        var resolveStart = source.IndexOf("internal static IReadOnlyList<SeriesManifestCandidate> ResolveClaimedSeriesManifestCandidates", StringComparison.Ordinal);
        Assert.True(resolveStart >= 0);

        var resolveEnd = source.IndexOf("private static IEnumerable<SeriesManifestItem> FilterManifestItems", resolveStart, StringComparison.Ordinal);
        Assert.True(resolveEnd > resolveStart);

        var resolveSource = source[resolveStart..resolveEnd];
        Assert.Contains("\"series_qid\"", resolveSource, StringComparison.Ordinal);
        Assert.DoesNotContain("candidates.Count == 0 && mediaType is MediaType.Movies", resolveSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"franchise_qid\"", resolveSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"fictional_universe_qid\"", resolveSource, StringComparison.Ordinal);
        Assert.Contains("RelType, \"series\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SeriesManifestHydration_CollectsOnlyTrueSeriesQidsAndRejectsListsOrFranchises()
    {
        var spiderCandidates = WikidataSeriesManifestHydrationService.ResolveClaimedSeriesManifestCandidates(
        [
            new ProviderClaim("series_qid", "Q99601314::Spider-Verse", 0.9),
            new ProviderClaim("series_qid", "Q65071834::list of Sony Pictures Animation productions", 0.9),
            new ProviderClaim("franchise_qid", "Q2307877::Spider-Man in film", 0.9),
        ],
        MediaType.Movies);

        var spiderCandidate = Assert.Single(spiderCandidates);
        Assert.Equal("Q99601314", spiderCandidate.Qid);

        var bladeRunnerCandidates = WikidataSeriesManifestHydrationService.ResolveClaimedSeriesManifestCandidates(
        [
            new ProviderClaim("franchise_qid", "Q48724847::Blade Runner", 0.9),
            new ProviderClaim("narrative_root_qid", "Q48724847::Blade Runner", 0.9),
        ],
        MediaType.Movies);

        Assert.Empty(bladeRunnerCandidates);

        var bookCandidates = WikidataSeriesManifestHydrationService.ResolveClaimedSeriesManifestCandidates(
        [
            new ProviderClaim("franchise_qid", "QBroad::Broad Book Franchise", 0.9),
        ],
        MediaType.Books);

        Assert.Empty(bookCandidates);

        var comicSelfCandidate = WikidataSeriesManifestHydrationService.ResolveClaimedSeriesManifestCandidates(
            [],
            MediaType.Comics,
            resolvedWorkQid: "Q2633138",
            resolvedWorkLabel: "Batman");

        var comicCandidate = Assert.Single(comicSelfCandidate);
        Assert.Equal("Q2633138", comicCandidate.Qid);
        Assert.True(comicCandidate.RequireKnownImmediateContainer);

        var movieSelfCandidate = WikidataSeriesManifestHydrationService.ResolveClaimedSeriesManifestCandidates(
            [],
            MediaType.Movies,
            resolvedWorkQid: "Q184843",
            resolvedWorkLabel: "Blade Runner");

        Assert.Empty(movieSelfCandidate);
    }

    [Fact]
    public void SeriesManifestHydration_BuildsDiagnosticsForWikidataOrderWarnings()
    {
        var diagnostics = WikidataSeriesManifestHydrationService.BuildWarningDiagnostics(
        [
            new SeriesManifestWarning
            {
                Code = "BrokenPreviousNextChain",
                Message = "Book 2 does not point back to Book 1.",
                Qid = "Q2",
            },
            new SeriesManifestWarning
            {
                Code = "PreviousNextConflictsWithOrdinal",
                Message = "The ordinal and previous/next data disagree.",
                Qid = "Q3",
            },
        ],
        seriesQid: "QSeries",
        resolvedWorkQid: "QOwned",
        resolvedWorkPresentInManifest: false);

        Assert.Equal(["BrokenPreviousNextChain", "PreviousNextConflictsWithOrdinal", "ResolvedWorkMissingFromManifest"],
            diagnostics.Select(d => d.Code));
        Assert.Equal("QOwned", diagnostics.Last().Qid);
    }

    [Fact]
    public void SeriesManifestHydration_NormalizesDuplicateItemsAndDenseDisplayOrder()
    {
        var normalized = WikidataSeriesManifestHydrationService.NormalizeManifestItems(
        [
            new SeriesManifestItem
            {
                Qid = "Q1",
                Label = "The Matrix",
                ParsedSeriesOrdinal = 1,
                PublicationDate = new DateOnly(1999, 3, 31),
            },
            new SeriesManifestItem
            {
                Qid = "Q1",
                Label = "The Matrix duplicate from expanded collection",
                ParsedSeriesOrdinal = 1,
                PublicationDate = new DateOnly(1999, 3, 31),
                IsExpandedFromCollection = true,
            },
            new SeriesManifestItem
            {
                Qid = "Q2",
                Label = "The Animatrix",
                ParsedSeriesOrdinal = 1,
                PublicationDate = new DateOnly(2003, 6, 3),
            },
            new SeriesManifestItem
            {
                Qid = "Q3",
                Label = "The Matrix Reloaded",
                ParsedSeriesOrdinal = 2,
                PublicationDate = new DateOnly(2003, 5, 15),
            },
        ]);

        Assert.Equal(["Q1", "Q2", "Q3"], normalized.Select(item => item.Item.Qid));
        Assert.Equal([1, 2, 3], normalized.Select(item => item.SortOrder));
    }

    [Fact]
    public void SeriesManifestHydration_ExpandsParentCollectionManifests()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Providers/Services/WikidataSeriesManifestHydrationService.cs"));
        var normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("relinked,\n                        seriesQid", normalizedSource, StringComparison.Ordinal);
        Assert.Contains("itemRecords,\n                manifest.SeriesQid", normalizedSource, StringComparison.Ordinal);
        Assert.Contains(".Where(item => IsCurrentWorkManifestItem(item, context))", source, StringComparison.Ordinal);
        Assert.Contains("CreateManifestRequest(parent.Qid, language)", source, StringComparison.Ordinal);
        Assert.Contains("sourceSeriesQid: childSeriesQid", source, StringComparison.Ordinal);
        Assert.Contains("parentCollectionHydration: true", source, StringComparison.Ordinal);

        var candidates = WikidataSeriesManifestHydrationService.ResolveParentCollectionManifestCandidates(
        [
            new SeriesManifestItemRecord
            {
                Id = Guid.NewGuid(),
                CollectionId = Guid.NewGuid(),
                SeriesQid = "QSpiderVerse",
                ItemQid = "Q1",
                ParentCollectionQid = "QSonyPicturesAnimation",
                ParentCollectionLabel = "List of Sony Pictures Animation productions",
                OrderSource = "None",
            },
            new SeriesManifestItemRecord
            {
                Id = Guid.NewGuid(),
                CollectionId = Guid.NewGuid(),
                SeriesQid = "QSpiderVerse",
                ItemQid = "Q2",
                ParentCollectionQid = "QSonyPicturesAnimation",
                ParentCollectionLabel = null,
                OrderSource = "None",
            },
            new SeriesManifestItemRecord
            {
                Id = Guid.NewGuid(),
                CollectionId = Guid.NewGuid(),
                SeriesQid = "QSpiderVerse",
                ItemQid = "Q3",
                ParentCollectionQid = "QSpiderVerse",
                ParentCollectionLabel = "Spider-Verse",
                OrderSource = "None",
            },
        ],
        "QSpiderVerse");

        var candidate = Assert.Single(candidates);
        Assert.Equal("QSonyPicturesAnimation", candidate.Qid);
        Assert.Equal("List of Sony Pictures Animation productions", candidate.Label);
    }

    [Fact]
    public void SeriesManifestHydration_RetainsSupplementalLiteraryWorksForScopedDisplay()
    {
        var items = new List<SeriesManifestItem>
        {
            new()
            {
                Qid = "QPrequel",
                Label = "Series prequel",
                ParsedSeriesOrdinal = 0.5m,
            },
            new()
            {
                Qid = "QBook1",
                Label = "Book One",
                ParsedSeriesOrdinal = 1,
            },
            new()
            {
                Qid = "QPlay",
                Label = "The Stage Play",
                Description = "A two-part stage play script.",
                ParsedSeriesOrdinal = 8,
            },
            new()
            {
                Qid = "QBook2",
                Label = "Book Two",
                ParsedSeriesOrdinal = 2,
            },
        };

        var context = new SeriesManifestHydrationContext(
            AssetId: Guid.NewGuid(),
            WorkId: Guid.NewGuid(),
            ResolvedWorkQid: "QBook1",
            MediaType: MediaType.Audiobooks,
            Title: "Book One",
            SeriesHint: "Series",
            IngestionRunId: null,
            Lineage: null,
            FullClaims: []);

        var method = typeof(WikidataSeriesManifestHydrationService).GetMethod(
            "FilterManifestItems",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        var filtered = Assert.IsAssignableFrom<IEnumerable<SeriesManifestItem>>(
            method!.Invoke(null, [items, context, "QSeries"]));

        Assert.Equal(["QPrequel", "QBook1", "QPlay", "QBook2"], filtered.Select(item => item.Qid));
    }

    [Fact]
    public void SeriesManifestHydration_DoesNotUseTitleSpecificFilmAllowlists()
    {
        var items = new List<SeriesManifestItem>
        {
            new() { Qid = "Q127367", Label = "The Lord of the Rings: The Fellowship of the Ring", ParsedSeriesOrdinal = 1 },
            new() { Qid = "Q164963", Label = "The Lord of the Rings: The Two Towers", ParsedSeriesOrdinal = 2 },
            new() { Qid = "Q131074", Label = "The Lord of the Rings: The Return of the King", ParsedSeriesOrdinal = 3 },
            new() { Qid = "Q107210110", Label = "The Lord of the Rings: The War of the Rohirrim", ParsedSeriesOrdinal = 4 },
            new() { Qid = "Q125858982", Label = "The Lord of the Rings: The Hunt for Gollum", ParsedSeriesOrdinal = 5 },
        };

        var context = new SeriesManifestHydrationContext(
            AssetId: Guid.NewGuid(),
            WorkId: Guid.NewGuid(),
            ResolvedWorkQid: "Q127367",
            MediaType: MediaType.Movies,
            Title: "The Lord of the Rings: The Fellowship of the Ring",
            SeriesHint: "The Lord of the Rings",
            IngestionRunId: null,
            Lineage: null,
            FullClaims: []);

        var method = typeof(WikidataSeriesManifestHydrationService).GetMethod(
            "FilterManifestItems",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        var filtered = Assert.IsAssignableFrom<IEnumerable<SeriesManifestItem>>(
            method!.Invoke(null, [items, context, "Q190214"]));

        Assert.Equal(
            ["Q127367", "Q164963", "Q131074", "Q107210110", "Q125858982"],
            filtered.Select(item => item.Qid));

        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Providers/Services/WikidataSeriesManifestHydrationService.cs"));
        Assert.DoesNotContain("KnownMovieSeriesItemQids", source, StringComparison.Ordinal);
        Assert.DoesNotContain("tuvima-known-film-trilogy", source, StringComparison.Ordinal);
        Assert.Contains("fact.Scope == SeriesManifestItemScope.MainSequence", source, StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));

    private static string ReadAdapterSource(string adapterName)
    {
        var facade = GetRepoFilePath($@"src/MediaEngine.Providers/Adapters/{adapterName}.cs");
        var internals = GetRepoFilePath(@"src/MediaEngine.Providers/Adapters/Internals");
        return string.Join(
            Environment.NewLine,
            new[] { facade }
                .Concat(Directory
                    .GetFiles(internals, $"{adapterName}.*.cs")
                    .Order(StringComparer.Ordinal))
                .Select(File.ReadAllText));
    }

    private static string ReadWorkerSource(string workerName)
    {
        var facade = GetRepoFilePath($@"src/MediaEngine.Providers/Workers/{workerName}.cs");
        var internals = GetRepoFilePath(@"src/MediaEngine.Providers/Workers/Internals");
        return string.Join(
            Environment.NewLine,
            new[] { facade }
                .Concat(Directory
                    .GetFiles(internals, $"{workerName}.*.cs")
                    .Order(StringComparer.Ordinal))
                .Select(File.ReadAllText));
    }
}
