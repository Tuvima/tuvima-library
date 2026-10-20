using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// A Series a person picked is a lock the Engine always respects, and the "which Series?" question has one stable
/// answer path. These run the real assignment service, reviewer and repositories against a temp data store.
/// </summary>
public sealed class SeriesPlacementLockAssignmentTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly CollectionRepository _collections;
    private readonly SeriesManifestRepository _manifests;
    private readonly SeriesPlacementRepository _placements;
    private readonly ReviewQueueRepository _reviews;
    private readonly CollectionAssignmentService _assignment;
    private readonly CollectionFinalizationService _finalization;
    private readonly WikidataSeriesManifestHydrationService _hydration;
    private readonly SeriesAmbiguityReviewer _reviewer;

    public SeriesPlacementLockAssignmentTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_series_lock_assign_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();

        _collections = new CollectionRepository(_db);
        _manifests = new SeriesManifestRepository(_db);
        _placements = new SeriesPlacementRepository(_db);
        _reviews = new ReviewQueueRepository(_db);
        var canonicals = new CanonicalValueRepository(_db);
        var activity = new SystemActivityRepository(_db);
        var outcomes = new StageOutcomeFactory(
            _reviews,
            activity,
            new SilentEventPublisher(),
            canonicals,
            NullLogger<StageOutcomeFactory>.Instance);
        _reviewer = new SeriesAmbiguityReviewer(
            outcomes,
            _collections,
            NullLogger<SeriesAmbiguityReviewer>.Instance,
            new MediaAssetRepository(_db),
            _reviews,
            _placements);
        _assignment = new CollectionAssignmentService(
            _collections,
            canonicals,
            new WorkRepository(_db),
            NullLogger<CollectionAssignmentService>.Instance,
            new CanonicalValueArrayRepository(_db),
            _manifests,
            _reviewer,
            _placements);
        _finalization = new CollectionFinalizationService(
            _assignment,
            new SilentParentResolver(),
            activity,
            NullLogger<CollectionFinalizationService>.Instance);
        _hydration = new WikidataSeriesManifestHydrationService(
            null!,
            _manifests,
            _collections,
            null!,
            NullLogger<WikidataSeriesManifestHydrationService>.Instance,
            _reviewer);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Fact]
    public async Task AssigningAgain_KeepsAWorkWhereAPersonPutIt_WhateverTheClaimsAndManifestsSay()
    {
        var chosen = await CreateShelfAsync("QChosen", "Chosen");
        var claimed = await CreateShelfAsync("QClaimed", "Claimed");
        var work = AddWork("Work", seriesQid: "QClaimed", seriesLabel: "Claimed");
        await StoreManifestAsync(claimed, "QClaimed", work);
        await _placements.SetChoiceAsync(work.WorkId, chosen);

        var first = await _assignment.AssignAsync(work.AssetId);
        var second = await _assignment.AssignAsync(work.AssetId);

        Assert.Equal(CollectionAssignmentOutcome.AlreadyAssigned, first.Outcome);
        Assert.Equal(chosen, first.CollectionId);
        Assert.Equal(CollectionAssignmentOutcome.AlreadyAssigned, second.Outcome);
        Assert.Equal(chosen, await ShelfOfAsync(work));
    }

    [Fact]
    public async Task AssigningAgain_KeepsANoSeriesChoice_EvenWhenTheWorkClaimsASeries()
    {
        var claimed = await CreateShelfAsync("QClaimed", "Claimed");
        var work = AddWork("Work", seriesQid: "QClaimed", seriesLabel: "Claimed");
        await StoreManifestAsync(claimed, "QClaimed", work);
        await _placements.SetChoiceAsync(work.WorkId, collectionId: null);

        var result = await _assignment.AssignAsync(work.AssetId);

        Assert.Equal(CollectionAssignmentOutcome.SkippedNoShelfIdentity, result.Outcome);
        Assert.Null(await ShelfOfAsync(work));
    }

    [Fact]
    public async Task BackfillingThroughFinalization_KeepsALockedWorkWhereThePersonPutIt()
    {
        var chosen = await CreateShelfAsync("QChosen", "Chosen");
        var locked = AddWork("Locked", seriesQid: "QClaimed", seriesLabel: "Claimed");
        var nothing = AddWork("No Series", seriesQid: "QClaimed", seriesLabel: "Claimed");
        await _placements.SetChoiceAsync(locked.WorkId, chosen);
        await _placements.SetChoiceAsync(nothing.WorkId, collectionId: null);

        await _finalization.FinalizeAsync(locked.AssetId, CollectionFinalizationReason.Backfill);
        await _finalization.FinalizeAsync(nothing.AssetId, CollectionFinalizationReason.Backfill);

        Assert.Equal(chosen, await ShelfOfAsync(locked));
        Assert.Null(await ShelfOfAsync(nothing));
    }

    [Fact]
    public async Task RefreshingAManifest_KeepsALockedWorkWhereThePersonPutIt_IncludingNoSeries()
    {
        var chosen = await CreateShelfAsync("QChosen", "Chosen");
        var listing = await CreateShelfAsync("QListing", "Listing");
        var other = await CreateShelfAsync("QOther", "Other");
        var locked = AddWork("Locked", seriesQid: "QListing", seriesLabel: "Listing");
        var nothing = AddWork("No Series", seriesQid: "QListing", seriesLabel: "Listing");
        await _placements.SetChoiceAsync(locked.WorkId, chosen);
        await _placements.SetChoiceAsync(nothing.WorkId, collectionId: null);

        await StoreManifestAsync(listing, "QListing", locked, nothing);
        await StoreManifestAsync(other, "QOther", locked, nothing);
        await StoreManifestAsync(listing, "QListing", locked, nothing);

        Assert.Equal(chosen, await ShelfOfAsync(locked));
        Assert.Null(await ShelfOfAsync(nothing));
    }

    [Fact]
    public async Task LettingTheLibraryDecideAgain_PlacesTheWorkWhereTheEvidenceSays()
    {
        var chosen = await CreateShelfAsync("QChosen", "Chosen");
        var claimed = await CreateShelfAsync("QClaimed", "Claimed");
        var work = AddWork("Work", seriesQid: "QClaimed", seriesLabel: "Claimed");
        await StoreManifestAsync(claimed, "QClaimed", work);
        await _placements.SetChoiceAsync(work.WorkId, chosen);

        await _placements.ReleaseChoiceAsync(work.WorkId);
        await _finalization.FinalizeAsync(work.AssetId, CollectionFinalizationReason.PersonReleasedSeriesChoice);

        Assert.Equal(claimed, await ShelfOfAsync(work));
        Assert.Null(await _placements.GetLockAsync(work.WorkId));
    }

    [Fact]
    public async Task ASeriesTieBrokenByAClaimStoredOnlyOnTheFile_GivesTheSameShelfFromAssigningAndFromLinking()
    {
        var first = await CreateShelfAsync("QA1", "Series A");
        var second = await CreateShelfAsync("QB1", "Series B");
        var viaAssign = AddWork("Via assign", seriesQid: null, seriesLabel: null, claimOnFile: "QB1");
        var viaLink = AddWork("Via link", seriesQid: null, seriesLabel: null, claimOnFile: "QB1");

        // Both Series list both works as supplementary: a tie that is not at main sequence, so the claim decides.
        await StoreManifestAsync(first, "QA1", viaLink, scope: SeriesMembershipScopeNames.Supplementary);
        await StoreManifestAsync(second, "QB1", viaLink, scope: SeriesMembershipScopeNames.Supplementary);
        await StoreManifestOnlyAsync(first, "QA1", viaAssign, SeriesMembershipScopeNames.Supplementary);
        await StoreManifestOnlyAsync(second, "QB1", viaAssign, SeriesMembershipScopeNames.Supplementary);
        await _assignment.AssignAsync(viaAssign.AssetId);

        Assert.Equal(second, await ShelfOfAsync(viaLink));
        Assert.Equal(second, await ShelfOfAsync(viaAssign));

        // And running either path again, in the other order, changes nothing.
        await _assignment.AssignAsync(viaLink.AssetId);
        await StoreManifestAsync(first, "QA1", viaAssign, scope: SeriesMembershipScopeNames.Supplementary);
        Assert.Equal(second, await ShelfOfAsync(viaLink));
        Assert.Equal(second, await ShelfOfAsync(viaAssign));
    }

    [Fact]
    public async Task AskingAboutTheSameAmbiguousWorkAgain_FromAnyFileOrRun_NeverCreatesASecondPendingItem()
    {
        var one = await CreateShelfAsync("QOne1", "Series One");
        var two = await CreateShelfAsync("QTwo1", "Series Two");
        var work = AddWork("Shared", seriesQid: "QOne1", seriesLabel: "Series One");
        var secondFile = AddFile(work);
        await StoreManifestOnlyAsync(one, "QOne1", work);
        await StoreManifestOnlyAsync(two, "QTwo1", work);

        await _assignment.AssignAsync(work.AssetId);
        await _assignment.AssignAsync(secondFile);
        await _assignment.AssignAsync(work.AssetId);
        await StoreManifestAsync(two, "QTwo1", work);
        await StoreManifestAsync(one, "QOne1", work);

        var pending = (await _reviews.GetPendingAsync(50))
            .Where(r => r.Trigger == ReviewTrigger.AmbiguousSeriesMembership)
            .ToList();
        var item = Assert.Single(pending);
        // New items hang on the work's earliest-stored Normal file.
        Assert.Equal(work.AssetId, item.EntityId);
    }

    [Fact]
    public async Task AnAmbiguousQuestionRaisedOnAnotherFile_IsStillTheOnlyOne_WhenTheStableFileAsksAgain()
    {
        var one = await CreateShelfAsync("QOne1", "Series One");
        var two = await CreateShelfAsync("QTwo1", "Series Two");
        var work = AddWork("Shared", seriesQid: "QOne1", seriesLabel: "Series One");
        var secondFile = AddFile(work);
        await StoreManifestOnlyAsync(one, "QOne1", work);
        await StoreManifestOnlyAsync(two, "QTwo1", work);
        // An older run keyed its item on the second file.
        await _reviews.InsertAsync(new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = secondFile,
            EntityType = "MediaAsset",
            Trigger = ReviewTrigger.AmbiguousSeriesMembership,
            Detail = "older",
            ReviewReadyAt = DateTimeOffset.UtcNow,
        });

        await _assignment.AssignAsync(work.AssetId);

        Assert.Single(
            await _reviews.GetPendingAsync(50),
            r => r.Trigger == ReviewTrigger.AmbiguousSeriesMembership);
    }

    [Fact]
    public async Task WhenAPersonChoosesASeries_TheAmbiguousQuestionIsClosed_OnTheNextPlacement()
    {
        var one = await CreateShelfAsync("QOne1", "Series One");
        var two = await CreateShelfAsync("QTwo1", "Series Two");
        var work = AddWork("Shared", seriesQid: "QOne1", seriesLabel: "Series One");
        await StoreManifestOnlyAsync(one, "QOne1", work);
        await StoreManifestOnlyAsync(two, "QTwo1", work);
        await _assignment.AssignAsync(work.AssetId);
        Assert.Single(await _reviews.GetPendingAsync(50), r => r.Trigger == ReviewTrigger.AmbiguousSeriesMembership);

        await _placements.SetChoiceAsync(work.WorkId, two);
        await _assignment.AssignAsync(work.AssetId);

        Assert.DoesNotContain(await _reviews.GetPendingAsync(50), r => r.Trigger == ReviewTrigger.AmbiguousSeriesMembership);
        Assert.Equal(two, await ShelfOfAsync(work));
    }

    [Fact]
    public async Task WhenALaterManifestSettlesTheTie_AssigningOrLinking_ClosesTheAmbiguousQuestion()
    {
        var one = await CreateShelfAsync("QOne1", "Series One");
        var two = await CreateShelfAsync("QTwo1", "Series Two");
        var viaAssign = AddWork("Assign", seriesQid: "QOne1", seriesLabel: "Series One");
        var viaLink = AddWork("Link", seriesQid: "QOne1", seriesLabel: "Series One");
        foreach (var work in new[] { viaAssign, viaLink })
        {
            await StoreManifestOnlyAsync(one, "QOne1", work);
            await StoreManifestOnlyAsync(two, "QTwo1", work);
            await _assignment.AssignAsync(work.AssetId);
        }

        Assert.Equal(2, (await _reviews.GetPendingAsync(50)).Count(r => r.Trigger == ReviewTrigger.AmbiguousSeriesMembership));

        // Series Two stops claiming both works as main sequence (its manifest now lists them as supplementary).
        await StoreManifestOnlyAsync(two, "QTwo1", viaAssign, SeriesMembershipScopeNames.Supplementary);
        await StoreManifestOnlyAsync(two, "QTwo1", viaLink, SeriesMembershipScopeNames.Supplementary);
        await _assignment.AssignAsync(viaAssign.AssetId);
        await StoreManifestAsync(one, "QOne1", viaLink);

        Assert.DoesNotContain(await _reviews.GetPendingAsync(50), r => r.Trigger == ReviewTrigger.AmbiguousSeriesMembership);
        Assert.Equal(one, await ShelfOfAsync(viaAssign));
        Assert.Equal(one, await ShelfOfAsync(viaLink));
    }

    // ----------------------------------------------------------------------------------------------------------------

    private sealed record Library(Guid WorkId, Guid AssetId);

    private Library AddWork(string title, string? seriesQid, string? seriesLabel, string? claimOnFile = null)
    {
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO works (id, collection_id, media_type, work_kind) VALUES (@workId, NULL, 'Books', 'standalone');",
            new { workId });
        AddFileRows(conn, workId, assetId, title);

        var values = new Dictionary<string, string> { ["title"] = title, ["media_type"] = "Books" };
        if (seriesQid is not null)
        {
            values["series_qid"] = seriesQid;
            values["series"] = seriesLabel ?? seriesQid;
        }

        foreach (var (key, value) in values)
        {
            conn.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, @key, @value, @now);",
                new { workId, key, value, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        if (claimOnFile is not null)
        {
            conn.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@assetId, 'series_qid', @claim, @now);",
                new { assetId, claim = claimOnFile, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        return new Library(workId, assetId);
    }

    private Guid AddFile(Library library)
    {
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        AddFileRows(conn, library.WorkId, assetId, "second");
        return assetId;
    }

    private static void AddFileRows(System.Data.IDbConnection conn, Guid workId, Guid assetId, string name)
        => conn.Execute(
            """
            INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Edition');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES (@assetId, @editionId, @contentHash, @filePath);
            """,
            new
            {
                editionId = Guid.NewGuid(),
                workId,
                assetId,
                contentHash = Guid.NewGuid().ToString("N"),
                filePath = $"{name}-{assetId:N}.bin",
            });

    private async Task<Guid> CreateShelfAsync(string qid, string name)
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            WikidataQid = qid,
            MediaType = "Books",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        collection.RestoreDefinition(
            CollectionType.ContentGroup,
            CollectionScope.Library,
            CollectionResolution.Materialized,
            CollectionMatchMode.All,
            CollectionSortDirection.Desc,
            CollectionUniverseStatus.Unknown);
        return await _collections.UpsertAsync(collection);
    }

    /// <summary>Stores the manifest rows and links the owned works through the hydration path (which also reports ambiguity).</summary>
    private async Task StoreManifestAsync(Guid collectionId, string seriesQid, params Library[] works)
        => await StoreManifestCoreAsync(collectionId, seriesQid, SeriesMembershipScopeNames.MainSequence, works);

    private async Task StoreManifestAsync(Guid collectionId, string seriesQid, Library work, string scope)
        => await StoreManifestCoreAsync(collectionId, seriesQid, scope, [work]);

    private async Task StoreManifestCoreAsync(Guid collectionId, string seriesQid, string scope, Library[] works)
    {
        var items = Items(collectionId, seriesQid, scope, works);
        await _manifests.UpsertManifestAsync(Hydrated(collectionId, seriesQid), items);
        await _hydration.LinkAndReportAsync(collectionId, items, default);
    }

    /// <summary>Stores the manifest rows without linking, so a later assignment run is what places the work.</summary>
    private async Task StoreManifestOnlyAsync(Guid collectionId, string seriesQid, Library work, string scope = SeriesMembershipScopeNames.MainSequence)
        => await _manifests.UpsertManifestAsync(Hydrated(collectionId, seriesQid), Items(collectionId, seriesQid, scope, [work]));

    private static SeriesManifestItemRecord[] Items(Guid collectionId, string seriesQid, string scope, Library[] works)
    {
        var now = DateTimeOffset.UtcNow;
        return works.Select((work, index) => new SeriesManifestItemRecord
        {
            Id = Guid.NewGuid(),
            CollectionId = collectionId,
            SeriesQid = seriesQid,
            ItemQid = $"Q-{work.WorkId:N}",
            ItemLabel = $"Item {index + 1}",
            MediaType = "Books",
            RawOrdinal = (index + 1).ToString(),
            ParsedOrdinal = index + 1,
            OrdinalScopeQid = seriesQid,
            SortOrder = index + 1,
            OrderSource = "SeriesOrdinal",
            OwnershipState = "Owned",
            LinkedWorkId = work.WorkId,
            MembershipScope = scope,
            SourcePropertiesJson = """["P179"]""",
            RelationshipsJson = "[]",
            LastHydratedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        }).ToArray();
    }

    private static SeriesManifestHydration Hydrated(Guid collectionId, string seriesQid)
    {
        var now = DateTimeOffset.UtcNow;
        return new SeriesManifestHydration
        {
            SeriesQid = seriesQid,
            CollectionId = collectionId,
            SeriesLabel = seriesQid,
            WarningsJson = "[]",
            ApiMetadataJson = "{}",
            LastHydratedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private async Task<Guid?> ShelfOfAsync(Library work)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<Guid?>(
            "-- readiness: test lookup of one work by id\nSELECT collection_id FROM works WHERE id = @id;",
            new { id = work.WorkId });
    }

    private sealed class SilentEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
            => Task.CompletedTask;
    }

    private sealed class SilentParentResolver : IParentCollectionResolver
    {
        public Task ResolveParentCollectionAsync(Guid collectionId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
