using System.Security.Claims;
using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class AuthorizedDisplayProjectionReadServiceTests : IDisposable
{
    [Fact]
    public async Task JourneyAndAllStateReadsScopeSqlAndReflectCommittedWrites()
    {
        var owned = await InsertOwnedWorkWithIdAsync(Guid.NewGuid(), "Saved movie", "Movie");
        var profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId;
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO user_states(user_id, asset_id, progress_pct, last_accessed, extended_properties)
            VALUES (@profile, @asset, 42, CURRENT_TIMESTAMP, '{"position_seconds":300,"duration_seconds":900}');
            """, new { profile, asset = owned.AssetId });
        var reader = new DisplayJourneyProjectionReader(_database);
        Assert.Empty(await reader.LoadAsync(null, null, default));
        Assert.Empty(await reader.LoadAsync(Guid.NewGuid(), null, default, includeCompleted: true));
        var partial = Assert.Single(await reader.LoadAsync(profile, null, default));
        Assert.Equal(300, partial.PositionSeconds);
        Assert.Equal(900, partial.DurationSeconds);
        connection.Execute("UPDATE user_states SET progress_pct=100 WHERE user_id=@profile AND asset_id=@asset", new { profile, asset = owned.AssetId });
        Assert.Empty(await reader.LoadAsync(profile, null, default));
        Assert.Equal(100, Assert.Single(await reader.LoadAsync(profile, null, default, includeCompleted: true)).ProgressPct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinueEpisodeUsesEpisodeTitleInsteadOfGenericShowTitle(bool assetTitle)
    {
        var owned = await InsertOwnedWorkWithIdAsync(Guid.NewGuid(), "Solo Leveling", "TV");
        var profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId;
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO user_states(user_id, asset_id, progress_pct, last_accessed)
            VALUES (@profile, @asset, 42, CURRENT_TIMESTAMP);
            INSERT INTO canonical_values(entity_id, key, value, last_scored_at)
            VALUES (@owner, 'episode_title', 'I am Used to It', CURRENT_TIMESTAMP),
                   (@work, 'show_name', 'Solo Leveling', CURRENT_TIMESTAMP),
                   (@asset, 'season_number', '1', CURRENT_TIMESTAMP),
                   (@asset, 'episode_number', '1', CURRENT_TIMESTAMP);
            """, new { profile, asset = owned.AssetId, work = owned.WorkId, owner = assetTitle ? owned.AssetId : owned.WorkId });
        var row = Assert.Single(await new DisplayJourneyProjectionReader(_database).LoadAsync(profile, null, default));
        var card = new DisplayCardBuilder().FromJourney(row, "home");
        Assert.Equal("I am Used to It", card.EpisodeContext!.EpisodeTitle);
        Assert.Equal("Solo Leveling", card.EpisodeContext.ShowTitle);
        Assert.Equal(1, card.EpisodeContext.SeasonNumber);
        Assert.Equal(1, card.EpisodeContext.EpisodeNumber);
    }

    [Fact]
    public async Task RequestedNullOrDifferentProfileFailsClosedForJourneyAndStates()
    {
        var account = Guid.NewGuid(); var library = Guid.NewGuid();
        await CreateHumanAsync(account, new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid> { library });
        var profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId;
        var service = CreateService(HumanContext(account, profile), new StubRawProjection([], [Journey(library, "Book", profile)]));
        Assert.Empty(await service.LoadJourneyAsync(null, null, default));
        Assert.Empty(await service.LoadStatesAsync(Guid.NewGuid(), null, default));
        Assert.Single(await service.LoadStatesAsync(profile, null, default));
    }

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"tuvima_catalogue_scope_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly ApplicationRepository _applications;

    public AuthorizedDisplayProjectionReadServiceTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _applications = new ApplicationRepository(_database);
    }

    [Fact]
    public async Task FiltersLibraryFeatureAndProfileBeforeComposerReadsRows()
    {
        var accountId = Guid.NewGuid();
        var allowedLibrary = Guid.NewGuid();
        var otherLibrary = Guid.NewGuid();
        await CreateHumanAsync(accountId,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            new HashSet<Guid> { allowedLibrary });
        var context = HumanContext(accountId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
        var service = CreateService(context, new StubRawProjection(
            [
                Work(allowedLibrary, "Book"),
                Work(otherLibrary, "Book"),
                Work(allowedLibrary, "Movie"),
            ],
            [
                Journey(allowedLibrary, "Book", MediaEngine.Domain.Aggregates.Profile.SeedProfileId),
                Journey(otherLibrary, "Book", MediaEngine.Domain.Aggregates.Profile.SeedProfileId),
                Journey(allowedLibrary, "Book", Guid.NewGuid()),
            ]));

        var works = await service.LoadWorksAsync(CancellationToken.None);
        var journey = await service.LoadJourneyAsync(MediaEngine.Domain.Aggregates.Profile.SeedProfileId, null, CancellationToken.None);

        Assert.Single(works);
        Assert.Equal(allowedLibrary.ToString("D"), works[0].LibraryId);
        Assert.Single(journey);
        Assert.Equal(MediaEngine.Domain.Aggregates.Profile.SeedProfileId, journey[0].ProfileId);
    }

    [Fact]
    public async Task RechecksLiveGrantsAfterProjectionCacheHit()
    {
        var accountId = Guid.NewGuid();
        var allowedLibrary = Guid.NewGuid();
        var newlyAllowedLibrary = Guid.NewGuid();
        await CreateHumanAsync(accountId,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            new HashSet<Guid> { allowedLibrary });
        await InsertOwnedWorkAsync(allowedLibrary, "Allowed book");
        await InsertOwnedWorkAsync(newlyAllowedLibrary, "Other book");
        var raw = new CachedReaderProjection(new DisplayWorkProjectionReader(_database));
        var service = CreateService(HumanContext(
            accountId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId), raw);

        var first = Assert.Single(await service.LoadWorksAsync(CancellationToken.None));
        Assert.Equal("Allowed book", first.Title);
        await _accounts.ReplaceAccountAccessAsync(
            accountId,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            new HashSet<Guid> { newlyAllowedLibrary },
            DateTimeOffset.UtcNow);

        var second = Assert.Single(await service.LoadWorksAsync(CancellationToken.None));
        Assert.Equal("Other book", second.Title);
        Assert.Equal(2, raw.WorkReadCount);
        Assert.Equal(1, raw.RepositoryReadCount);
    }

    [Fact]
    public async Task EffectiveAdministratorOverridesFeatureAndLibraryButMalformedProvenanceDenies()
    {
        var accountId = Guid.NewGuid();
        await CreateHumanAsync(accountId,
            new HashSet<AccountFeatureId>(),
            new HashSet<Guid>(),
            administrator: true);
        var libraryId = Guid.NewGuid();
        var service = CreateService(HumanContext(
            accountId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId),
            new StubRawProjection(
                [Work(libraryId, "Movie"), new DisplayWorkRow { WorkId = Guid.NewGuid(), MediaType = "Movie" }],
                []));

        var rows = await service.LoadWorksAsync(CancellationToken.None);

        Assert.Single(rows);
        Assert.Equal(libraryId.ToString("D"), rows[0].LibraryId);
    }

    [Fact]
    public async Task SelectsRepresentativeAssetOnlyAfterFilteringSameWorkLibraries()
    {
        var accountId = Guid.NewGuid();
        var deniedLibrary = Guid.NewGuid();
        var allowedLibrary = Guid.NewGuid();
        await CreateHumanAsync(accountId,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            new HashSet<Guid> { allowedLibrary });
        var workId = Guid.NewGuid();
        var deniedAsset = Guid.Parse("00000000-0000-4000-8000-000000000101");
        var allowedAsset = Guid.Parse("ffffffff-ffff-4fff-8fff-fffffffff102");
        var secondAllowedAsset = Guid.Parse("ffffffff-ffff-4fff-8fff-fffffffff103");
        await InsertAssetForWorkAsync(workId, deniedAsset, deniedLibrary, "Denied edition", createWork: true);
        await InsertAssetForWorkAsync(workId, allowedAsset, allowedLibrary, "Allowed edition", createWork: false);
        await InsertAssetForWorkAsync(workId, secondAllowedAsset, allowedLibrary, "Second allowed edition", createWork: false);
        var context = HumanContext(accountId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
        var service = CreateService(
            context,
            new CachedReaderProjection(new DisplayWorkProjectionReader(_database)));

        var selected = Assert.Single(await service.LoadWorksAsync(CancellationToken.None));

        Assert.Equal(allowedAsset, selected.AssetId);
        Assert.Equal(allowedLibrary.ToString("D"), selected.LibraryId);
        Assert.Equal("Allowed edition", selected.Title);
        Assert.Equal(
            [allowedAsset, secondAllowedAsset],
            (await service.LoadAuthorizedAssetsAsync(CancellationToken.None))
                .Select(row => row.AssetId)
                .Order()
                .ToArray());
        Assert.Equal(allowedAsset, await CreateResourceService(context).FindAuthorizedAssetForWorkAsync(
            context,
            workId,
            MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            ApplicationPermissionIds.LibraryRead));
        Assert.Equal([allowedAsset, secondAllowedAsset], await CreateResourceService(context).GetAuthorizedAssetIdsForWorkAsync(
            context,
            workId,
            MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            ApplicationPermissionIds.LibraryRead));
    }

    [Fact]
    public async Task BookFormatsPreferAuthorizedEpubOverAzw3EvenAfterExternalFormatProgress()
    {
        var accountId = Guid.NewGuid(); var allowedLibrary = Guid.NewGuid(); var deniedLibrary = Guid.NewGuid();
        await CreateHumanAsync(accountId, new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid> { allowedLibrary });
        var workId = Guid.NewGuid(); var epub = Guid.NewGuid(); var azw = Guid.NewGuid(); var denied = Guid.NewGuid();
        await InsertAssetForWorkAsync(workId, azw, allowedLibrary, "Book", createWork: true);
        await InsertAssetForWorkAsync(workId, epub, allowedLibrary, "Book", createWork: false);
        await InsertAssetForWorkAsync(workId, denied, deniedLibrary, "Book", createWork: false);
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync("""
                    UPDATE media_assets SET file_path_root='C:/library/Book.azw3' WHERE id=@azw;
                    UPDATE media_assets SET file_path_root='C:/library/Book.epub' WHERE id IN (@epub,@denied);
                    INSERT INTO user_states(user_id,asset_id,progress_pct,last_accessed) VALUES(@profile,@azw,20,CURRENT_TIMESTAMP);
                    """, new { azw, epub, denied, profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId });
        }
        var context = HumanContext(accountId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
        var assets = await CreateResourceService(context).GetAuthorizedAssetIdsForWorkAsync(
            context, workId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId, ApplicationPermissionIds.LibraryRead);
        Assert.Equal([epub, azw], assets);
    }

    [Fact]
    public async Task DirectAssetAuthorizationIntersectsFeatureAndLibrary()
    {
        var accountId = Guid.NewGuid();
        var allowedLibrary = Guid.NewGuid();
        var deniedLibrary = Guid.NewGuid();
        await CreateHumanAsync(accountId,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            new HashSet<Guid> { allowedLibrary });
        var allowedBook = await InsertOwnedWorkAsync(allowedLibrary, "Allowed book");
        var deniedBook = await InsertOwnedWorkAsync(deniedLibrary, "Denied book");
        var deniedMovie = await InsertOwnedWorkAsync(allowedLibrary, "Denied movie", "Movie");
        var context = HumanContext(accountId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
        var service = CreateResourceService(context);

        Assert.Equal(CatalogueResourceAccess.Allowed,
            await service.EvaluateAssetAsync(context, allowedBook, ApplicationPermissionIds.PlaybackRead));
        Assert.Equal(CatalogueResourceAccess.Denied,
            await service.EvaluateAssetAsync(context, deniedBook, ApplicationPermissionIds.PlaybackRead));
        Assert.Equal(CatalogueResourceAccess.Denied,
            await service.EvaluateAssetAsync(context, deniedMovie, ApplicationPermissionIds.PlaybackRead));
        Assert.Equal(CatalogueResourceAccess.NotFound,
            await service.EvaluateAssetAsync(context, Guid.NewGuid(), ApplicationPermissionIds.PlaybackRead));
    }

    [Fact]
    public async Task ArtworkVariantUsesOwningWorkLibraryInsteadOfVariantIdAlone()
    {
        var accountId = Guid.NewGuid();
        var allowedLibrary = Guid.NewGuid();
        var deniedLibrary = Guid.NewGuid();
        await CreateHumanAsync(accountId,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            new HashSet<Guid> { allowedLibrary });
        var allowed = await InsertOwnedWorkWithIdAsync(allowedLibrary, "Allowed book");
        var denied = await InsertOwnedWorkWithIdAsync(deniedLibrary, "Denied book");
        var allowedVariant = await InsertArtworkAsync(allowed.WorkId, "Work");
        var deniedVariant = await InsertArtworkAsync(denied.WorkId, "Work");
        var context = HumanContext(accountId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
        var service = CreateResourceService(context);

        Assert.Equal(CatalogueResourceAccess.Allowed,
            await service.EvaluateArtworkVariantAsync(context, allowedVariant, ApplicationPermissionIds.ArtworkRead));
        Assert.Equal(CatalogueResourceAccess.Denied,
            await service.EvaluateArtworkVariantAsync(context, deniedVariant, ApplicationPermissionIds.ArtworkRead));
        Assert.Equal(CatalogueResourceAccess.NotFound,
            await service.EvaluateArtworkVariantAsync(context, Guid.NewGuid(), ApplicationPermissionIds.ArtworkRead));

        using var connection = _database.CreateConnection();
        foreach (var (linkId, ownerId) in new[] { (allowedVariant, allowed.WorkId), (deniedVariant, denied.WorkId) })
        {
            await connection.ExecuteAsync("""
                INSERT INTO artwork_assets (id, content_hash, original_path, created_at)
                VALUES (@linkId, @hash, 'test.jpg', CURRENT_TIMESTAMP);
                INSERT INTO entity_artwork_links (id, artwork_asset_id, entity_id, entity_type, role, created_at)
                VALUES (@linkId, @linkId, @ownerId, 'Work', 'Primary', CURRENT_TIMESTAMP);
                """, new { linkId, ownerId, hash = linkId.ToString() });
        }
        Assert.Equal(CatalogueResourceAccess.Allowed,
            await service.EvaluateArtworkLinkAsync(context, allowedVariant, ApplicationPermissionIds.ArtworkRead));
        Assert.Equal(CatalogueResourceAccess.Denied,
            await service.EvaluateArtworkLinkAsync(context, deniedVariant, ApplicationPermissionIds.ArtworkRead));
        Assert.Equal(CatalogueResourceAccess.NotFound,
            await service.EvaluateArtworkLinkAsync(context, Guid.NewGuid(), ApplicationPermissionIds.ArtworkRead));
    }

    [Fact]
    public async Task QueryCollectionAuthorizationResolvesRulesAndRetainsAssetGrantChecks()
    {
        var accountId = Guid.NewGuid();
        var allowedLibrary = Guid.NewGuid();
        await CreateHumanAsync(accountId,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            new HashSet<Guid> { allowedLibrary });
        await InsertOwnedWorkAsync(allowedLibrary, "Allowed book", "Book");
        await InsertOwnedWorkAsync(allowedLibrary, "Feature-denied movie", "Movies");

        var bookCollectionId = Guid.NewGuid();
        var movieCollectionId = Guid.NewGuid();
        var bookRules = System.Text.Json.JsonSerializer.Serialize(CollectionRuleDefinition.SingleGroup(
            [new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "Book" }]));
        var movieRules = System.Text.Json.JsonSerializer.Serialize(CollectionRuleDefinition.SingleGroup(
            [new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "Movies" }]));
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO collections
                    (id, display_name, collection_type, scope, resolution, is_enabled, rule_json)
                VALUES
                    (@bookCollectionId, 'Dynamic books', 'Custom', 'library', 'query', 1, @bookRules),
                    (@movieCollectionId, 'Dynamic movies', 'Custom', 'library', 'query', 1, @movieRules);
                """,
                new { bookCollectionId, movieCollectionId, bookRules, movieRules });
        }

        var context = HumanContext(accountId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
        var service = CreateResourceService(context);

        Assert.Equal(CatalogueResourceAccess.Allowed,
            await service.EvaluateEntityAsync(
                context, "Collection", bookCollectionId, ApplicationPermissionIds.LibraryRead));
        Assert.Equal(CatalogueResourceAccess.Denied,
            await service.EvaluateEntityAsync(
                context, "Collection", movieCollectionId, ApplicationPermissionIds.LibraryRead));

        using var verify = _database.CreateConnection();
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM collection_items WHERE collection_id IN (@bookCollectionId, @movieCollectionId);",
            new { bookCollectionId, movieCollectionId }));
    }

    [Fact]
    public async Task StructuralTvArtworkUsesOwnedEpisodeLibraryAccess()
    {
        var accountId = Guid.NewGuid();
        var allowedLibrary = Guid.NewGuid();
        await CreateHumanAsync(accountId,
            new HashSet<AccountFeatureId> { AccountFeatureId.Watch },
            new HashSet<Guid> { allowedLibrary });
        var episode = await InsertOwnedWorkWithIdAsync(allowedLibrary, "Owned episode", "TV");
        var showId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind, curator_state)
                VALUES (@showId, 'TV', 'parent', 'accepted');
                INSERT INTO works (id, media_type, work_kind, parent_work_id, curator_state)
                VALUES (@seasonId, 'TV', 'parent', @showId, 'accepted');
                UPDATE works
                SET parent_work_id=@seasonId, work_kind='child'
                WHERE id=@episodeId;
                """,
                new { showId, seasonId, episodeId = episode.WorkId });
        }

        var showArtwork = await InsertArtworkAsync(showId, "Work");
        var seasonArtwork = await InsertArtworkAsync(seasonId, "Work");
        var context = HumanContext(accountId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
        var service = CreateResourceService(context);

        Assert.Equal(CatalogueResourceAccess.Allowed,
            await service.EvaluateArtworkVariantAsync(context, showArtwork, ApplicationPermissionIds.ArtworkRead));
        Assert.Equal(CatalogueResourceAccess.Allowed,
            await service.EvaluateArtworkVariantAsync(context, seasonArtwork, ApplicationPermissionIds.ArtworkRead));
    }

    [Fact]
    public async Task PersonArtworkRequiresAnAuthorizedCreditedAsset()
    {
        var accountId = Guid.NewGuid();
        var allowedLibrary = Guid.NewGuid();
        var deniedLibrary = Guid.NewGuid();
        await CreateHumanAsync(accountId,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            new HashSet<Guid> { allowedLibrary });
        var allowed = await InsertOwnedWorkWithIdAsync(allowedLibrary, "Allowed book");
        var denied = await InsertOwnedWorkWithIdAsync(deniedLibrary, "Denied book");
        var visiblePerson = Guid.NewGuid();
        var hiddenPerson = Guid.NewGuid();
        var supplementaryPerson = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO persons (id, name, created_at) VALUES (@visiblePerson, 'Visible', CURRENT_TIMESTAMP);
                INSERT INTO persons (id, name, created_at) VALUES (@hiddenPerson, 'Hidden', CURRENT_TIMESTAMP);
                INSERT INTO persons (id, name, created_at) VALUES (@supplementaryPerson, 'Supplementary', CURRENT_TIMESTAMP);
                INSERT INTO person_media_links (media_asset_id, person_id, role)
                VALUES (@allowedAsset, @visiblePerson, 'Author'),
                       (@deniedAsset, @hiddenPerson, 'Author'),
                       (@allowedAsset, @supplementaryPerson, 'Assistant');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@allowedAsset, 'author', 0, 'Visible'),
                       (@deniedAsset, 'author', 0, 'Hidden');
                """,
                new { visiblePerson, hiddenPerson, supplementaryPerson, allowedAsset = allowed.AssetId, deniedAsset = denied.AssetId });
        }
        var context = HumanContext(accountId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
        var service = CreateResourceService(context);

        Assert.Equal(CatalogueResourceAccess.Allowed,
            await service.EvaluateEntityAsync(context, "Person", visiblePerson, ApplicationPermissionIds.ArtworkRead));
        Assert.Equal(CatalogueResourceAccess.Denied,
            await service.EvaluateEntityAsync(context, "Person", hiddenPerson, ApplicationPermissionIds.ArtworkRead));
        Assert.Equal(CatalogueResourceAccess.NotFound,
            await service.EvaluateEntityAsync(context, "Person", supplementaryPerson, ApplicationPermissionIds.ArtworkRead));
    }

    [Fact]
    public async Task IndirectCharacterAndQidResourcesRetainOwningLibraryScope()
    {
        var accountId = Guid.NewGuid();
        var allowedLibrary = Guid.NewGuid();
        var deniedLibrary = Guid.NewGuid();
        await CreateHumanAsync(accountId,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            new HashSet<Guid> { allowedLibrary });
        var allowed = await InsertOwnedWorkWithIdAsync(allowedLibrary, "Allowed character work");
        var denied = await InsertOwnedWorkWithIdAsync(deniedLibrary, "Denied character work");
        var actorId = Guid.NewGuid();
        var allowedCharacter = Guid.NewGuid();
        var deniedCharacter = Guid.NewGuid();
        var allowedPortrait = Guid.NewGuid();
        var deniedPortrait = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                UPDATE works SET wikidata_qid='Q3101' WHERE id=@allowedWorkId;
                UPDATE works SET wikidata_qid='Q3102' WHERE id=@deniedWorkId;
                INSERT INTO persons (id, name, created_at) VALUES (@actorId, 'Actor', CURRENT_TIMESTAMP);
                INSERT INTO fictional_entities
                    (id, wikidata_qid, label, entity_sub_type, fictional_universe_qid, created_at)
                VALUES (@allowedCharacter, 'Q3201', 'Allowed Character', 'Character', 'Q3001', CURRENT_TIMESTAMP),
                       (@deniedCharacter, 'Q3202', 'Denied Character', 'Character', 'Q3002', CURRENT_TIMESTAMP);
                INSERT INTO fictional_entity_work_links (id, appearance_key, entity_id, work_qid)
                VALUES (randomblob(16), 'allowed-character-q3101', @allowedCharacter, 'Q3101'),
                       (randomblob(16), 'denied-character-q3102', @deniedCharacter, 'Q3102');
                INSERT INTO character_portraits
                    (id, person_id, fictional_entity_id, image_url, created_at)
                VALUES (@allowedPortrait, @actorId, @allowedCharacter, '/allowed.jpg', CURRENT_TIMESTAMP),
                       (@deniedPortrait, @actorId, @deniedCharacter, '/denied.jpg', CURRENT_TIMESTAMP);
                INSERT INTO entity_assets
                    (id, entity_id, entity_type, asset_type, aspect_class, created_at)
                VALUES (@entityAssetId, @allowedWorkId, 'Work', 'CoverArt', 'Portrait', CURRENT_TIMESTAMP);
                """,
                new
                {
                    allowedWorkId = allowed.WorkId,
                    deniedWorkId = denied.WorkId,
                    actorId,
                    allowedCharacter,
                    deniedCharacter,
                    allowedPortrait,
                    deniedPortrait,
                    entityAssetId = Guid.NewGuid(),
                });
        }
        var context = HumanContext(accountId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
        var service = CreateResourceService(context);

        Assert.Equal(CatalogueResourceAccess.Allowed,
            await service.EvaluateQidAsync(context, "Q3001", ApplicationPermissionIds.LibraryRead));
        Assert.Equal(CatalogueResourceAccess.Denied,
            await service.EvaluateQidAsync(context, "Q3002", ApplicationPermissionIds.LibraryRead));
        Assert.Equal(CatalogueResourceAccess.Allowed,
            await service.EvaluateCharacterPortraitAsync(context, allowedPortrait, ApplicationPermissionIds.ArtworkRead));
        Assert.Equal(CatalogueResourceAccess.Denied,
            await service.EvaluateCharacterPortraitAsync(context, deniedPortrait, ApplicationPermissionIds.ArtworkRead));
        Assert.Equal(CatalogueResourceAccess.Allowed,
            await service.EvaluateAnyEntityAsync(context, allowed.WorkId, ApplicationPermissionIds.MetadataEnrichmentRead));
        Assert.Equal(CatalogueResourceAccess.Allowed,
            await service.EvaluateEntityAssetContainerAsync(context, allowed.WorkId, ApplicationPermissionIds.MetadataRead));
    }

    [Fact]
    public async Task AuthorizedWorkDetailExcludesDeniedEditionsAndAssetMetadata()
    {
        var accountId = Guid.NewGuid();
        var deniedLibrary = Guid.NewGuid();
        var allowedLibrary = Guid.NewGuid();
        await CreateHumanAsync(accountId,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            new HashSet<Guid> { allowedLibrary });
        var work = await InsertOwnedWorkWithIdAsync(deniedLibrary, "Denied edition", "Books");
        var allowedEditionId = Guid.NewGuid();
        var allowedAssetId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO editions (id, work_id, format_label)
                VALUES (@allowedEditionId, @workId, 'EPUB');
                INSERT INTO media_assets
                    (id, edition_id, content_hash, file_path_root, presented_at, library_id)
                VALUES
                    (@allowedAssetId, @allowedEditionId, @hash, @path, CURRENT_TIMESTAMP, @allowedLibrary);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@allowedAssetId, 'title', 'Allowed edition', CURRENT_TIMESTAMP),
                       (@allowedAssetId, 'cover_url', '/allowed-cover.jpg', CURRENT_TIMESTAMP);
                """,
                new
                {
                    workId = work.WorkId,
                    allowedEditionId,
                    allowedAssetId,
                    hash = Guid.NewGuid().ToString("N"),
                    path = $"C:/library/{allowedAssetId:N}.epub",
                    allowedLibrary = allowedLibrary.ToString("D"),
                });
        }
        var context = HumanContext(accountId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
        var resources = CreateResourceService(context);
        var allowedAssets = await resources.GetAuthorizedAssetIdsForWorkAsync(
            context,
            work.WorkId,
            MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            ApplicationPermissionIds.LibraryRead);
        var composer = new DetailComposerService(
            _database,
            new LibraryItemRepository(_database),
            new PersonRepository(_database),
            new EntityAssetRepository(_database),
            new CanonicalValueArrayRepository(_database),
            new SeriesManifestRepository(_database),
            new PersonCreditReadService(
                new CanonicalValueArrayRepository(_database),
                new PersonRepository(_database),
                _database),
            new DetailRecommendationService(_database));

        var detail = await composer.BuildAuthorizedAsync(
            MediaEngine.Contracts.Details.DetailEntityType.Book,
            work.WorkId,
            MediaEngine.Contracts.Details.DetailPresentationContext.Default,
            CancellationToken.None,
            selectedContainerId: null,
            MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            default(DetailActionAuthorizationContext),
            allowedAssets);

        Assert.NotNull(detail);
        Assert.Equal("Allowed edition", detail!.Title);
        var format = Assert.Single(detail.OwnedFormats);
        Assert.Equal(allowedEditionId.ToString("D"), format.Id);
        Assert.Equal("/allowed-cover.jpg", format.CoverUrl);
        Assert.DoesNotContain(detail.OwnedFormats, item => item.Id == work.AssetId.ToString("D"));
        Assert.Equal(1, detail.PersonalStatus?.OwnedCount);
        Assert.DoesNotContain(detail.OverflowActions, action => action.Key == "add-collection");

        var managedDetail = await composer.BuildAuthorizedAsync(
            MediaEngine.Contracts.Details.DetailEntityType.Book,
            work.WorkId,
            MediaEngine.Contracts.Details.DetailPresentationContext.Default,
            CancellationToken.None,
            selectedContainerId: null,
            MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            new DetailActionAuthorizationContext(CanManageMetadata: true),
            allowedAssets);
        Assert.DoesNotContain(managedDetail!.OverflowActions, action => action.Key == "add-collection");
        Assert.DoesNotContain(managedDetail.OverflowActions, action => action.Key == "file-information");

        Assert.Null(await composer.BuildAuthorizedAsync(
            MediaEngine.Contracts.Details.DetailEntityType.Book,
            work.WorkId,
            MediaEngine.Contracts.Details.DetailPresentationContext.Default,
            CancellationToken.None,
            selectedContainerId: null,
            MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            default(DetailActionAuthorizationContext),
            []));
    }

    [Fact]
    public async Task CollectionDetailFiltersMembersArtworkAndCountsBeforeComposition()
    {
        var allowedLibrary = Guid.NewGuid();
        var deniedLibrary = Guid.NewGuid();
        var allowed = await InsertOwnedWorkWithIdAsync(allowedLibrary, "Allowed collection member");
        var denied = await InsertOwnedWorkWithIdAsync(deniedLibrary, "Denied collection member");
        var collectionId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO collections (id, display_name, collection_type, created_at)
                VALUES (@collectionId, 'Scoped collection', 'Custom', CURRENT_TIMESTAMP);
                UPDATE works SET collection_id = @collectionId WHERE id IN (@allowedWorkId, @deniedWorkId);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@allowedAssetId, 'cover_url', '/allowed.jpg', CURRENT_TIMESTAMP),
                       (@deniedAssetId, 'cover_url', '/denied.jpg', CURRENT_TIMESTAMP);
                """,
                new
                {
                    collectionId,
                    allowedWorkId = allowed.WorkId,
                    deniedWorkId = denied.WorkId,
                    allowedAssetId = allowed.AssetId,
                    deniedAssetId = denied.AssetId,
                });
        }

        var detail = await CreateComposer().BuildAuthorizedAsync(
            MediaEngine.Contracts.Details.DetailEntityType.Collection,
            collectionId,
            MediaEngine.Contracts.Details.DetailPresentationContext.Default,
            CancellationToken.None,
            selectedContainerId: null,
            MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            default(DetailActionAuthorizationContext),
            authorizedAssetIds: null,
            authorizedWorks:
            [
                new DisplayWorkRow
                {
                    WorkId = allowed.WorkId,
                    AssetId = allowed.AssetId,
                    LibraryId = allowedLibrary.ToString("D"),
                    MediaType = "Book",
                    CoverUrl = "/allowed.jpg",
                },
            ]);

        Assert.NotNull(detail);
        var items = detail!.MediaGroups.SelectMany(group => group.Items).ToList();
        var item = Assert.Single(items);
        Assert.Equal(allowed.WorkId.ToString("D"), item.Id);
        Assert.Equal("/allowed.jpg", item.ArtworkUrl);
        Assert.DoesNotContain(detail.Metadata, pill => pill.Label.Contains("2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueryCollectionDetailRetainsAuthorizedCollapsedTvShow()
    {
        var collectionId = Guid.NewGuid();
        var showId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var episodeAssetId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var ruleJson = System.Text.Json.JsonSerializer.Serialize(CollectionRuleDefinition.SingleGroup(
            [new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "TV" }]));
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO collections
                    (id, display_name, collection_type, scope, resolution, is_enabled, rule_json, created_at)
                VALUES
                    (@collectionId, 'Smart TV', 'Custom', 'library', 'query', 1, @ruleJson, CURRENT_TIMESTAMP);
                INSERT INTO works (id, media_type, work_kind, curator_state)
                VALUES (@showId, 'TV', 'parent', 'accepted');
                INSERT INTO works (id, media_type, work_kind, parent_work_id, curator_state)
                VALUES (@seasonId, 'TV', 'parent', @showId, 'accepted'),
                       (@episodeId, 'TV', 'child', @seasonId, 'accepted');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @episodeId);
                INSERT INTO media_assets
                    (id, edition_id, content_hash, file_path_root, presented_at, library_id)
                VALUES
                    (@episodeAssetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @libraryId);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@showId, 'title', 'Authorized Smart Show', CURRENT_TIMESTAMP),
                       (@episodeAssetId, 'title', 'Pilot', CURRENT_TIMESTAMP);
                """,
                new
                {
                    collectionId,
                    showId,
                    seasonId,
                    episodeId,
                    editionId,
                    episodeAssetId,
                    libraryId = libraryId.ToString("D"),
                    ruleJson,
                    hash = Guid.NewGuid().ToString("N"),
                    path = $"C:/library/{episodeAssetId:N}.mkv",
                });
        }

        var detail = await CreateCollectionComposer().BuildAuthorizedAsync(
            MediaEngine.Contracts.Details.DetailEntityType.Collection,
            collectionId,
            MediaEngine.Contracts.Details.DetailPresentationContext.Default,
            CancellationToken.None,
            selectedContainerId: null,
            MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            default(DetailActionAuthorizationContext),
            authorizedAssetIds: null,
            authorizedWorks:
            [
                new DisplayWorkRow
                {
                    WorkId = episodeId,
                    AssetId = episodeAssetId,
                    LibraryId = libraryId.ToString("D"),
                    MediaType = "TV",
                },
            ]);

        Assert.NotNull(detail);
        Assert.Equal("Smart TV", detail!.Title);
        var item = Assert.Single(detail.MediaGroups.SelectMany(group => group.Items));
        Assert.Equal(showId.ToString("D"), item.Id);
    }

    [Fact]
    public async Task PersonDetailFiltersCreditsBeforeGroupsAndOwnedCount()
    {
        var personId = Guid.NewGuid();
        var allowed = await InsertOwnedWorkWithIdAsync(Guid.NewGuid(), "Allowed credit");
        var denied = await InsertOwnedWorkWithIdAsync(Guid.NewGuid(), "Denied credit");
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO persons (id, name, created_at) VALUES (@personId, 'Scoped Person', CURRENT_TIMESTAMP);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@allowedAssetId, 'author', 0, 'Scoped Person'),
                       (@deniedAssetId, 'author', 0, 'Scoped Person');
                """,
                new { personId, allowedAssetId = allowed.AssetId, deniedAssetId = denied.AssetId });
        }

        var detail = await CreateComposer().BuildAuthorizedAsync(
            MediaEngine.Contracts.Details.DetailEntityType.Person,
            personId,
            MediaEngine.Contracts.Details.DetailPresentationContext.Default,
            CancellationToken.None,
            selectedContainerId: null,
            MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            default(DetailActionAuthorizationContext),
            authorizedAssetIds: null,
            authorizedWorks:
            [
                new DisplayWorkRow { WorkId = allowed.WorkId, AssetId = allowed.AssetId, MediaType = "Book" },
            ]);

        Assert.NotNull(detail);
        var item = Assert.Single(detail!.MediaGroups.SelectMany(group => group.Items));
        Assert.Equal(allowed.WorkId.ToString("D"), item.Id);
        Assert.Contains(detail.Metadata, pill => pill.Label == "1 title in library");
    }

    [Fact]
    public async Task PersonDetailAuthorizesMusicAlbumThroughTheExactVisibleCreditedTrack()
    {
        var personId = Guid.NewGuid();
        var albumId = Guid.NewGuid();
        var allowedTrackId = Guid.NewGuid();
        var deniedTrackId = Guid.NewGuid();
        var allowedEditionId = Guid.NewGuid();
        var deniedEditionId = Guid.NewGuid();
        var allowedAssetId = Guid.NewGuid();
        var deniedAssetId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO persons (id, name, wikidata_qid, created_at)
                VALUES (@personId, 'Canonical Artist', 'Q169119', CURRENT_TIMESTAMP);
                INSERT INTO works (id, media_type, work_kind, curator_state)
                VALUES (@albumId, 'Music', 'parent', 'accepted');
                INSERT INTO works (id, parent_work_id, media_type, work_kind, curator_state)
                VALUES (@allowedTrackId, @albumId, 'Music', 'child', 'accepted'),
                       (@deniedTrackId, @albumId, 'Music', 'child', 'accepted');
                INSERT INTO editions (id, work_id)
                VALUES (@allowedEditionId, @allowedTrackId), (@deniedEditionId, @deniedTrackId);
                INSERT INTO media_assets
                    (id, edition_id, content_hash, file_path_root, presented_at, library_id)
                VALUES (@allowedAssetId, @allowedEditionId, @allowedHash, @allowedPath, CURRENT_TIMESTAMP, @libraryId),
                       (@deniedAssetId, @deniedEditionId, @deniedHash, @deniedPath, CURRENT_TIMESTAMP, @libraryId);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@albumId, 'title', 'Authorized Album', CURRENT_TIMESTAMP),
                       (@allowedTrackId, 'title', 'Allowed Track', CURRENT_TIMESTAMP),
                       (@deniedTrackId, 'title', 'Denied Track', CURRENT_TIMESTAMP);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid)
                VALUES (@allowedAssetId, 'artist', 0, 'Credited Alias', 'Q169119'),
                       (@deniedAssetId, 'artist', 0, 'Credited Alias', 'Q169119');
                """,
                new
                {
                    personId,
                    albumId,
                    allowedTrackId,
                    deniedTrackId,
                    allowedEditionId,
                    deniedEditionId,
                    allowedAssetId,
                    deniedAssetId,
                    libraryId = libraryId.ToString("D"),
                    allowedHash = Guid.NewGuid().ToString("N"),
                    deniedHash = Guid.NewGuid().ToString("N"),
                    allowedPath = $"C:/library/{allowedAssetId:N}.flac",
                    deniedPath = $"D:/private/{deniedAssetId:N}.flac",
                });
        }

        var visibleWorks = new[]
        {
            new DisplayWorkRow
            {
                WorkId = allowedTrackId,
                RootWorkId = albumId,
                AssetId = allowedAssetId,
                LibraryId = libraryId.ToString("D"),
                MediaType = "Music",
                Title = "Allowed Track",
                CoverUrl = "/authorized-track-art.jpg",
            },
        };
        var detail = await CreateComposer().BuildAuthorizedAsync(
            MediaEngine.Contracts.Details.DetailEntityType.Person,
            personId,
            MediaEngine.Contracts.Details.DetailPresentationContext.Default,
            CancellationToken.None,
            selectedContainerId: null,
            MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            default(DetailActionAuthorizationContext),
            authorizedAssetIds: null,
            authorizedWorks: visibleWorks);

        Assert.NotNull(detail);
        var album = Assert.Single(detail!.MediaGroups.SelectMany(group => group.Items));
        Assert.Equal(albumId.ToString("D"), album.Id);
        Assert.Equal("Authorized Album", album.Title);
        Assert.Equal("/authorized-track-art.jpg", album.ArtworkUrl);
    }

    [Fact]
    public async Task PersonDetailUsesShowPosterInsteadOfAuthorizedEpisodeStill()
    {
        var personId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var showWorkId = Guid.NewGuid();
        var episodeWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var episodeAssetId = Guid.NewGuid();
        var showCoverId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO persons (id, name, created_at)
                VALUES (@personId, 'Series Actor', CURRENT_TIMESTAMP);
                INSERT INTO collections (id, display_name, collection_type, created_at)
                VALUES (@collectionId, 'Root Artwork Show', 'Series', CURRENT_TIMESTAMP);
                INSERT INTO works (id, collection_id, media_type, work_kind, curator_state)
                VALUES (@showWorkId, @collectionId, 'TV', 'parent', 'accepted');
                INSERT INTO works (id, parent_work_id, collection_id, media_type, work_kind, ordinal, curator_state)
                VALUES (@episodeWorkId, @showWorkId, @collectionId, 'TV', 'child', 1, 'accepted');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @episodeWorkId);
                INSERT INTO media_assets
                    (id, edition_id, content_hash, file_path_root, presented_at, library_id)
                VALUES
                    (@episodeAssetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @libraryId);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@episodeAssetId, 'title', 'Pilot', CURRENT_TIMESTAMP);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@episodeAssetId, 'cast_member', 0, 'Series Actor');
                INSERT INTO entity_assets
                    (id, entity_id, entity_type, asset_type, aspect_class, is_preferred, created_at)
                VALUES
                    (@showCoverId, @showWorkId, 'Work', 'CoverArt', 'Portrait', 1, CURRENT_TIMESTAMP);
                """,
                new
                {
                    personId,
                    libraryId = libraryId.ToString("D"),
                    collectionId,
                    showWorkId,
                    episodeWorkId,
                    editionId,
                    episodeAssetId,
                    showCoverId,
                    hash = Guid.NewGuid().ToString("N"),
                    path = $"C:/library/{episodeAssetId:N}.mkv",
                });
        }

        var detail = await CreateComposer().BuildAuthorizedAsync(
            MediaEngine.Contracts.Details.DetailEntityType.Person,
            personId,
            MediaEngine.Contracts.Details.DetailPresentationContext.Default,
            CancellationToken.None,
            selectedContainerId: null,
            MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            default(DetailActionAuthorizationContext),
            authorizedAssetIds: null,
            authorizedWorks:
            [
                new DisplayWorkRow
                {
                    WorkId = episodeWorkId,
                    AssetId = episodeAssetId,
                    MediaType = "TV",
                    CoverUrl = "/episode-still.jpg",
                },
            ]);

        Assert.NotNull(detail);
        var item = Assert.Single(detail!.MediaGroups.SelectMany(group => group.Items));
        Assert.Equal(collectionId.ToString("D"), item.Id);
        Assert.Equal("Root Artwork Show", item.Title);
        Assert.Equal($"/stream/artwork/{showCoverId:D}", item.ArtworkUrl);
        Assert.DoesNotContain("episode-still", item.ArtworkUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UniverseDetailFiltersCharactersByVisibleWorkProvenance()
    {
        var collectionId = Guid.NewGuid();
        var visibleCharacter = Guid.NewGuid();
        var hiddenCharacter = Guid.NewGuid();
        var visible = await InsertOwnedWorkWithIdAsync(Guid.NewGuid(), "Visible universe work");
        var hidden = await InsertOwnedWorkWithIdAsync(Guid.NewGuid(), "Hidden universe work");
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO collections (id, display_name, collection_type, wikidata_qid, created_at)
                VALUES (@collectionId, 'Scoped Universe', 'Universe', 'Q1000', CURRENT_TIMESTAMP);
                UPDATE works SET collection_id=@collectionId, wikidata_qid='Q1001' WHERE id=@visibleWorkId;
                UPDATE works SET collection_id=@collectionId, wikidata_qid='Q1002' WHERE id=@hiddenWorkId;
                INSERT INTO fictional_entities
                    (id, wikidata_qid, label, entity_sub_type, fictional_universe_qid, created_at)
                VALUES (@visibleCharacter, 'Q2001', 'Visible Character', 'Character', 'Q1000', CURRENT_TIMESTAMP),
                       (@hiddenCharacter, 'Q2002', 'Hidden Character', 'Character', 'Q1000', CURRENT_TIMESTAMP);
                INSERT INTO fictional_entity_work_links (id, appearance_key, entity_id, work_qid)
                VALUES (randomblob(16), 'visible-character-q1001', @visibleCharacter, 'Q1001'),
                       (randomblob(16), 'hidden-character-q1002', @hiddenCharacter, 'Q1002');
                """,
                new
                {
                    collectionId,
                    visibleCharacter,
                    hiddenCharacter,
                    visibleWorkId = visible.WorkId,
                    hiddenWorkId = hidden.WorkId,
                });
        }

        var detail = await CreateComposer().BuildAuthorizedAsync(
            MediaEngine.Contracts.Details.DetailEntityType.Universe,
            collectionId,
            MediaEngine.Contracts.Details.DetailPresentationContext.Default,
            CancellationToken.None,
            selectedContainerId: null,
            MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            default(DetailActionAuthorizationContext),
            authorizedAssetIds: null,
            authorizedWorks:
            [
                new DisplayWorkRow
                {
                    WorkId = visible.WorkId,
                    AssetId = visible.AssetId,
                    MediaType = "Book",
                    IdentityQid = "Q1001",
                },
            ]);

        Assert.NotNull(detail);
        var character = Assert.Single(detail!.CharacterGroups.SelectMany(group => group.Characters));
        Assert.Equal(visibleCharacter.ToString("D"), character.EntityId);
        Assert.DoesNotContain(detail.PreviewCharacters, item => item.EntityId == hiddenCharacter.ToString("D"));
    }

    private DetailComposerService CreateComposer() =>
        new(
            _database,
            new LibraryItemRepository(_database),
            new PersonRepository(_database),
            new EntityAssetRepository(_database),
            new CanonicalValueArrayRepository(_database),
            new SeriesManifestRepository(_database),
            new PersonCreditReadService(
                new CanonicalValueArrayRepository(_database),
                new PersonRepository(_database),
                _database),
            new DetailRecommendationService(_database));

    private DetailComposerService CreateCollectionComposer()
    {
        var collectionRepository = new CollectionRepository(_database);
        var collectionLookup = new CollectionMediaLookupReadService(_database);
        var collectionCatalog = new CollectionCatalogReadService(
            collectionRepository,
            new SeriesManifestRepository(_database),
            new PersonRepository(_database),
            new ArtworkPaletteService(),
            collectionLookup,
            _database,
            NullLogger<CollectionCatalogReadService>.Instance);
        return new DetailComposerService(
            _database,
            new LibraryItemRepository(_database),
            new PersonRepository(_database),
            new EntityAssetRepository(_database),
            new CanonicalValueArrayRepository(_database),
            new SeriesManifestRepository(_database),
            new PersonCreditReadService(
                new CanonicalValueArrayRepository(_database),
                new PersonRepository(_database),
                _database),
            new DetailRecommendationService(_database),
            collectionCatalog: collectionCatalog,
            profiles: new ProfileRepository(_database));
    }

    [Fact]
    public async Task PlayerScopeRejectsOtherProfilesAndAssetsAndRemovesRevokedQueueEntries()
    {
        var account = Guid.NewGuid();
        var library = Guid.NewGuid();
        await CreateHumanAsync(account, new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid> { library });
        var allowed = Work(library, "Book");
        var denied = Work(Guid.NewGuid(), "Book");
        await InsertAssetForWorkAsync(allowed.WorkId, allowed.AssetId, library, "Allowed", createWork: true);
        await InsertAssetForWorkAsync(denied.WorkId, denied.AssetId, Guid.Parse(denied.LibraryId!), "Denied", createWork: true);
        var context = HumanContext(account, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
        using var services = new ServiceCollection()
            .AddSingleton<IRequestAuthorityResolver>(new RequestAuthorityResolver(_accounts, _applications, new ProfileRepository(_database)))
            .AddSingleton(CreateService(context, new StubRawProjection([allowed, denied], [])))
            .BuildServiceProvider();
        context.RequestServices = services;
        var scope = new PlayerCatalogueScope(new HttpContextAccessor { HttpContext = context });
        Assert.Equal(MediaEngine.Domain.Aggregates.Profile.SeedProfileId, await scope.RequireProfileAsync(null, default));
        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() => scope.RequireProfileAsync(Guid.NewGuid(), default));
        await scope.RequireAssetAsync(allowed.AssetId, default, allowed.WorkId);
        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() => scope.RequireAssetAsync(denied.AssetId, default));
        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() => scope.RequireAssetAsync(allowed.AssetId, default, Guid.NewGuid()));
        var hidden = new PlayerQueueItemDto { QueueItemId = Guid.NewGuid(), WorkId = denied.WorkId, AssetId = denied.AssetId };
        var filtered = await scope.FilterStateAsync(new PlayerStateDto
        {
            Queue = [new PlayerQueueItemDto { AssetId = allowed.AssetId, WorkId = allowed.WorkId }, hidden],
            CurrentItem = hidden,
            CurrentQueueItemId = hidden.QueueItemId,
            PlaybackState = PlayerPlaybackStates.Playing,
            PositionSeconds = 42,
            ProgressPct = 10,
        }, default);
        Assert.Single(filtered.Queue);
        Assert.Null(filtered.CurrentItem);
        Assert.Null(filtered.CurrentQueueItemId);
        Assert.Equal(PlayerPlaybackStates.Stopped, filtered.PlaybackState);
        Assert.Equal(0, filtered.PositionSeconds);
    }

    [Fact]
    public async Task ContinueEpisodeUsesRootPosterAndEpisodePeekWithoutChangingPlaybackIdentity()
    {
        var episode = await InsertOwnedWorkWithIdAsync(Guid.NewGuid(), "Episode Three", "TV");
        var showId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync("""
            INSERT INTO works (id, media_type, work_kind, curator_state)
            VALUES (@showId, 'TV', 'parent', 'accepted');
            UPDATE works SET parent_work_id = @showId, work_kind = 'child' WHERE id = @workId;
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                (@showId, 'cover_url', '/shows/poster.jpg', CURRENT_TIMESTAMP),
                (@showId, 'cover_url_s', '/shows/poster-s.jpg', CURRENT_TIMESTAMP),
                (@showId, 'background_url', '/shows/backdrop.jpg', CURRENT_TIMESTAMP),
                (@workId, 'episode_still_url', '/episodes/3.jpg', CURRENT_TIMESTAMP),
                (@workId, 'episode_still_url_m', '/episodes/3-m.jpg', CURRENT_TIMESTAMP),
                (@assetId, 'cover_url', '/episodes/legacy-cover.jpg', CURRENT_TIMESTAMP),
                (@assetId, 'background_url', '/shows/inherited-backdrop.jpg', CURRENT_TIMESTAMP),
                (@assetId, 'background_url_m', '/shows/inherited-backdrop-m.jpg', CURRENT_TIMESTAMP),
                (@assetId, 'season_number', '1', CURRENT_TIMESTAMP),
                (@assetId, 'episode_number', '3', CURRENT_TIMESTAMP);
            INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed)
            VALUES (@profileId, @assetId, 42, CURRENT_TIMESTAMP);
            """, new
        {
            showId,
            workId = episode.WorkId,
            assetId = episode.AssetId,
            profileId = MediaEngine.Domain.Aggregates.Profile.SeedProfileId
        });

        var row = Assert.Single(await new DisplayJourneyProjectionReader(_database).LoadAsync(MediaEngine.Domain.Aggregates.Profile.SeedProfileId, "watch", default));
        Assert.Equal("/shows/poster.jpg", row.CoverUrl);
        Assert.Equal("/shows/poster-s.jpg", row.CoverSmallUrl);
        Assert.Equal("/episodes/3.jpg", row.BackgroundUrl);
        Assert.Equal("/episodes/3-m.jpg", row.BackgroundMediumUrl);
        Assert.Equal(episode.AssetId, row.AssetId);
        Assert.Equal(episode.WorkId, row.WorkId);
        Assert.Equal(42, row.ProgressPct);
    }

    [Fact]
    public async Task FreshRecentCatalogueUsesSavedPartialStateAndAlbumHasNoCompletionBar()
    {
        var account = Guid.NewGuid(); var library = Guid.NewGuid();
        await CreateHumanAsync(account, new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Listen }, new HashSet<Guid> { library });
        var book = await InsertOwnedWorkWithIdAsync(library, "Recent book", "Book");
        var music = await InsertOwnedWorkWithIdAsync(library, "Track", "Music");
        var profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId;
        using var connection = _database.CreateConnection();
        connection.Execute("""INSERT INTO user_states(user_id,asset_id,progress_pct,last_accessed,extended_properties) VALUES (@profile,@asset,42,CURRENT_TIMESTAMP,'{"position_seconds":300,"duration_seconds":900}');""", new { profile, asset = book.AssetId });
        var states = await new DisplayJourneyProjectionReader(_database).LoadAsync(profile, null, default, includeCompleted: true);
        var authorization = CreateService(HumanContext(account, profile), new StubRawProjection([], states));
        var recent = new RecentCatalogueReadService(new DisplayWorkProjectionReader(_database), authorization, new DisplayCardBuilder(), _database);
        var items = await recent.LoadAsync("all", profile, null, 100, default);
        var partial = items.Single(i => i.Catalogue!.Id == book.WorkId).Catalogue!;
        Assert.Equal(42, partial.Progress!.Percent);
        Assert.Equal(600, partial.Progress.RemainingSeconds);
        Assert.Equal(MediaEngine.Contracts.Display.DisplayContinuationState.InProgress, partial.ContinuationState);
        Assert.Null(items.Single(i => i.Catalogue!.MediaType == "Music").Catalogue!.Progress);
        Assert.Empty(await recent.LoadAsync("all", Guid.NewGuid(), null, 100, default));
    }

    [Fact]
    public async Task RecentUsesLatestAuthorizedLibraryAdditionAndIgnoresNewerDeniedVariant()
    {
        var account = Guid.NewGuid(); var firstLibrary = Guid.NewGuid(); var secondLibrary = Guid.NewGuid(); var deniedLibrary = Guid.NewGuid();
        await CreateHumanAsync(account, new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid> { firstLibrary, secondLibrary });
        var shared = await InsertOwnedWorkWithIdAsync(firstLibrary, "Shared work");
        var before = await InsertOwnedWorkWithIdAsync(firstLibrary, "Before"); var after = await InsertOwnedWorkWithIdAsync(firstLibrary, "After");
        using var connection = _database.CreateConnection();
        foreach (var (library, date) in new[] { (secondLibrary, "2024-01-01T00:00:00Z"), (deniedLibrary, "2026-01-01T00:00:00Z") })
        {
            var asset = Guid.NewGuid(); var edition = Guid.NewGuid();
            connection.Execute("""
                INSERT INTO editions(id,work_id) VALUES(@edition,@work);
                INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,presented_at,library_id)
                VALUES(@asset,@edition,@hash,@path,@date,@library);
                INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES(@asset,'title','Shared work',CURRENT_TIMESTAMP);
                """, new { edition, work = shared.WorkId, asset, hash = asset.ToString("N"), path = $"C:/library/{asset:N}.epub", date, library = library.ToString("D") });
        }
        connection.Execute("UPDATE media_assets SET presented_at=@date WHERE id=@asset", new { date = "2020-01-01T00:00:00Z", asset = shared.AssetId });
        connection.Execute("UPDATE media_assets SET presented_at=@date WHERE id=@asset", new { date = "2023-01-01T00:00:00Z", asset = before.AssetId });
        connection.Execute("UPDATE media_assets SET presented_at=@date WHERE id=@asset", new { date = "2025-01-01T00:00:00Z", asset = after.AssetId });
        var profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId;
        var authorization = CreateService(HumanContext(account, profile), new StubRawProjection([], []));
        var recent = new RecentCatalogueReadService(new DisplayWorkProjectionReader(_database), authorization, new DisplayCardBuilder(), _database);
        var items = await recent.LoadAsync("read", profile, null, 100, default);
        Assert.Equal(new[] { after.WorkId, shared.WorkId, before.WorkId }, items.Select(i => i.Catalogue!.Id));
        Assert.Equal(DateTimeOffset.Parse("2024-01-01T00:00:00Z"), items[1].AddedAt);
        Assert.Single(items, i => i.Catalogue!.Id == shared.WorkId);
    }

    [Theory]
    [InlineData(0, MediaEngine.Contracts.Display.DisplayContinuationState.Unstarted, 0, "saved")]
    [InlineData(42, MediaEngine.Contracts.Display.DisplayContinuationState.InProgress, 0, "saved")]
    [InlineData(100, MediaEngine.Contracts.Display.DisplayContinuationState.Completed, 2, "saved")]
    [InlineData(42, MediaEngine.Contracts.Display.DisplayContinuationState.InProgress, 0, "missing")]
    [InlineData(42, MediaEngine.Contracts.Display.DisplayContinuationState.InProgress, 0, "invalid")]
    public async Task FinalEpisodeDetailPreservesOwnedSequenceIdentityTimingAndProfileState(double percent, MediaEngine.Contracts.Display.DisplayContinuationState expected, int completed, string timingMode)
    {
        var library = Guid.NewGuid(); var first = await InsertOwnedWorkWithIdAsync(library, "Episode five", "TV"); var second = await InsertOwnedWorkWithIdAsync(library, "Episode seven", "TV");
        var show = Guid.NewGuid(); var providerOnly = Guid.NewGuid(); var profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId; var position = percent * 30;
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works(id,media_type,work_kind,curator_state) VALUES(@show,'TV','parent','accepted');
            INSERT INTO works(id,media_type,work_kind,parent_work_id,curator_state) VALUES(@providerOnly,'TV','child',@show,'accepted');
            UPDATE works SET parent_work_id=@show,work_kind='child' WHERE id IN(@firstWork,@secondWork);
            INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES
            (@show,'title','Owned show',CURRENT_TIMESTAMP),(@show,'cover_url','/shows/cover.jpg',CURRENT_TIMESTAMP),
            (@firstAsset,'season_number','1',CURRENT_TIMESTAMP),(@firstAsset,'episode_number','5',CURRENT_TIMESTAMP),
            (@secondAsset,'season_number','1',CURRENT_TIMESTAMP),(@secondAsset,'episode_number','7',CURRENT_TIMESTAMP),
            (@firstWork,'episode_number','5',CURRENT_TIMESTAMP),(@secondWork,'episode_number','7',CURRENT_TIMESTAMP),
            (@firstWork,'runtime','45',CURRENT_TIMESTAMP),(@secondWork,'runtime','45',CURRENT_TIMESTAMP),
            (@providerOnly,'episode_number','6',CURRENT_TIMESTAMP),(@providerOnly,'season_number','1',CURRENT_TIMESTAMP),
            (@firstWork,'episode_still_url','/episodes/five.jpg',CURRENT_TIMESTAMP),(@secondWork,'episode_still_url','/episodes/seven.jpg',CURRENT_TIMESTAMP);
            """, new { show, providerOnly, firstWork = first.WorkId, secondWork = second.WorkId, firstAsset = first.AssetId, secondAsset = second.AssetId });
        var actualStillId = Guid.NewGuid();
        connection.Execute("""
            INSERT INTO entity_assets(id,entity_id,entity_type,asset_type,local_image_path,local_image_path_s,width_px,height_px)
            VALUES(@actualStillId,@firstWork,'Work','EpisodeStill','fixture/episode.jpg','fixture/episode-s.jpg',1920,1080);
            """, new { actualStillId, firstWork = first.WorkId });
        foreach (var asset in new[] { first.AssetId, second.AssetId })
        {
            connection.Execute("INSERT INTO user_states(user_id,asset_id,progress_pct,last_accessed,extended_properties) VALUES(@profile,@asset,@percent,CURRENT_TIMESTAMP,@timing)", new
            {
                profile,
                asset,
                percent,
                timing = System.Text.Json.JsonSerializer.Serialize(timingMode == "missing" ? new Dictionary<string, string>() : new Dictionary<string, string>
                {
                    ["position_seconds"] = position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["duration_seconds"] = timingMode == "invalid" ? "-1" : "3000"
                })
            });
        }
        var composer = CreateComposer();
        var detail = await composer.BuildAuthorizedAsync(MediaEngine.Contracts.Details.DetailEntityType.TvEpisode, first.WorkId, MediaEngine.Contracts.Details.DetailPresentationContext.Watch, default, show.ToString("D"), profile, default, [first.AssetId, second.AssetId]);
        Assert.NotNull(detail); var placement = Assert.IsType<MediaEngine.Contracts.Details.SequencePlacementViewModel>(detail.SequencePlacement);
        Assert.Equal(2, placement.OrderedItems.Count); Assert.DoesNotContain(placement.OrderedItems, i => i.Id == providerOnly.ToString("D"));
        var group = Assert.Single(placement.Groups); Assert.Equal(2, group.OwnedCount); Assert.Equal(completed, group.CompletedCount);
        var actualStill = placement.OrderedItems.Single(item => item.Id == first.WorkId.ToString("D"));
        Assert.Equal($"/stream/artwork/{actualStillId:D}", actualStill.EpisodeStillUrl);
        Assert.Equal(1920, actualStill.EpisodeStillWidthPx); Assert.Equal(1080, actualStill.EpisodeStillHeightPx);
        var noStoredStill = placement.OrderedItems.Single(item => item.Id == second.WorkId.ToString("D"));
        Assert.Null(noStoredStill.EpisodeStillUrl);
        Assert.NotNull(noStoredStill.ArtworkUrl); // Existing generic artwork remains available to other surfaces.
        foreach (var item in placement.OrderedItems)
        {
            Assert.NotNull(item.EpisodeContext); Assert.Equal(show, item.EpisodeContext.ShowWorkId); Assert.Equal(expected, item.EpisodeContext.State);
            if (timingMode == "saved") { Assert.Equal(position, item.PositionSeconds); Assert.Equal(3000, item.DurationSeconds); Assert.Equal(3000 - position, item.RemainingSeconds); }
            Assert.Equal(percent, item.ProgressPercent); Assert.Equal(MediaEngine.Contracts.Details.TvEpisodeDetailRoute.Build(show, Guid.Parse(item.Id)), item.Route);
        }
        var showDetail = await composer.BuildAuthorizedAsync(MediaEngine.Contracts.Details.DetailEntityType.TvShow, show, MediaEngine.Contracts.Details.DetailPresentationContext.Watch, default, null, profile, default, [first.AssetId, second.AssetId]);
        Assert.NotNull(showDetail);
        if (percent == 42)
        {
            Assert.NotNull(detail.Progress); Assert.NotNull(showDetail.Progress);
            foreach (var progress in new[] { detail.Progress, showDetail.Progress })
            {
                Assert.Contains("42% watched", progress.Label);
                Assert.DoesNotContain("27m left", progress.Label);
                if (timingMode == "saved")
                {
                    Assert.Contains("29m left", progress.Label);
                }
                else
                {
                    Assert.DoesNotContain(" left", progress.Label);
                }
            }
        }
        var other = await composer.BuildAuthorizedAsync(MediaEngine.Contracts.Details.DetailEntityType.TvEpisode, first.WorkId, MediaEngine.Contracts.Details.DetailPresentationContext.Watch, default, show.ToString("D"), Guid.NewGuid(), default, [first.AssetId, second.AssetId]);
        Assert.NotNull(other); Assert.All(other.SequencePlacement!.OrderedItems, i => { Assert.Equal(MediaEngine.Contracts.Display.DisplayContinuationState.Unstarted, i.EpisodeContext!.State); Assert.Null(i.ProgressPercent); });
        Assert.Equal(0, Assert.Single(other.SequencePlacement.Groups).CompletedCount);
    }

    [Fact]
    public async Task CombinedFileEpisodesResolveToTheHostFileForPlayback()
    {
        var account = Guid.NewGuid();
        var library = Guid.NewGuid();
        await CreateHumanAsync(account, new HashSet<AccountFeatureId> { AccountFeatureId.Watch }, new HashSet<Guid> { library });
        var combined = await InsertOwnedWorkWithIdAsync(library, "Pilot", "TV");
        var covered = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync("""
                INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@covered, 'TV', 'standalone', 'accepted');
                INSERT INTO media_asset_coverage (asset_id, work_id, position, source) VALUES
                    (@asset, @first, 1, 'filename'), (@asset, @covered, 2, 'filename');
                """, new { covered, first = combined.WorkId, asset = combined.AssetId });
        }
        var context = HumanContext(account, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
        var resources = CreateResourceService(context);
        Assert.Equal([combined.AssetId], await resources.GetAuthorizedAssetIdsForWorkAsync(
            context, covered, MediaEngine.Domain.Aggregates.Profile.SeedProfileId, ApplicationPermissionIds.LibraryRead));
        Assert.Equal(combined.AssetId, await resources.FindAuthorizedAssetForWorkAsync(
            context, combined.WorkId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId, ApplicationPermissionIds.LibraryRead));
        Assert.Empty(await resources.GetAuthorizedAssetIdsForWorkAsync(
            context, Guid.NewGuid(), MediaEngine.Domain.Aggregates.Profile.SeedProfileId, ApplicationPermissionIds.LibraryRead));
    }

    [Fact]
    public async Task CombinedFileQueuesOnceAndStartsAtTheRequestedEpisodeOnlyWhenNothingToResume()
    {
        var account = Guid.NewGuid();
        var library = Guid.NewGuid();
        var profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId;
        await CreateHumanAsync(account, new HashSet<AccountFeatureId> { AccountFeatureId.Watch }, new HashSet<Guid> { library });
        var combined = await InsertOwnedWorkWithIdAsync(library, "Pilot", "TV");
        var next = await InsertOwnedWorkWithIdAsync(library, "Third", "TV");
        var covered = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync("""
                INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@covered, 'TV', 'standalone', 'accepted');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                    (@covered, 'title', 'Part Two', CURRENT_TIMESTAMP),
                    (@covered, 'episode_number', '2', CURRENT_TIMESTAMP),
                    (@asset, 'episode_number', '1', CURRENT_TIMESTAMP);
                INSERT INTO media_asset_coverage (asset_id, work_id, position, start_seconds, source) VALUES
                    (@asset, @first, 1, 0, 'filename'), (@asset, @covered, 2, 1500, 'manual');
                """, new { covered, first = combined.WorkId, asset = combined.AssetId });
        }
        var context = HumanContext(account, profile);
        DisplayWorkRow Row(Guid work, Guid asset) =>
            new() { WorkId = work, AssetId = asset, LibraryId = library.ToString("D"), MediaType = "TV" };
        using var services = new ServiceCollection()
            .AddSingleton<IRequestAuthorityResolver>(new RequestAuthorityResolver(_accounts, _applications, new ProfileRepository(_database)))
            .AddSingleton(CreateService(context, new StubRawProjection(
                [Row(combined.WorkId, combined.AssetId), Row(covered, combined.AssetId), Row(next.WorkId, next.AssetId)], [])))
            .BuildServiceProvider();
        context.RequestServices = services;
        var player = new PlayerService(
            null!, null!, _database, null!, null!, null!, null!, null!, null!,
            new PlayerCatalogueScope(new HttpContextAccessor { HttpContext = context }), null!);
        var request = new PlayerQueueMutationDto
        {
            WorkIds = [combined.WorkId, covered, next.WorkId],
            StartWorkId = covered,
        };

        var queue = await player.ResolveQueueItemsAsync(request, profile, default);

        Assert.Equal([covered, next.WorkId], queue.Select(item => item.WorkId).ToArray());
        var episodeTwo = queue[0];
        Assert.Equal(combined.AssetId, episodeTwo.AssetId);
        Assert.Equal("Part Two", episodeTwo.Title);
        Assert.Equal("2", episodeTwo.EpisodeNumber);
        Assert.Equal(1500, episodeTwo.PositionSeconds);
        Assert.Null(queue[1].PositionSeconds);

        var fromStart = await player.ResolveQueueItemsAsync(request with { StartWorkId = combined.WorkId }, profile, default);
        Assert.Equal([combined.WorkId, next.WorkId], fromStart.Select(item => item.WorkId).ToArray());
        Assert.Null(fromStart[0].PositionSeconds);

        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO user_states(user_id, asset_id, progress_pct, last_accessed) VALUES (@profile, @asset, 40, CURRENT_TIMESTAMP);",
                new { profile, asset = combined.AssetId });
        }
        Assert.Null((await player.ResolveQueueItemsAsync(request, profile, default))[0].PositionSeconds);

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE user_states SET progress_pct = 100 WHERE asset_id = @asset;", new { asset = combined.AssetId });
        }
        Assert.Equal(1500, (await player.ResolveQueueItemsAsync(request, profile, default))[0].PositionSeconds);
    }

    private AuthorizedDisplayProjectionReadService CreateService(
        DefaultHttpContext context,
        IRawDisplayProjectionReadService raw)
    {
        var accessor = new HttpContextAccessor { HttpContext = context };
        var unlocks = new GrantAdminUnlockService(
            _accounts,
            new PasswordHasher<GrantAdminProtection>(),
            TimeProvider.System);
        var decisions = new AccountAccessDecisionService(_accounts, unlocks);
        var evaluator = new AuthorizationEvaluator(
            decisions,
            _applications,
            new PermissionRegistry(),
            new AuthorizationAuditWriter(_accounts),
            accessor);
        return new AuthorizedDisplayProjectionReadService(
            raw,
            accessor,
            new RequestAuthorityResolver(_accounts, _applications, new ProfileRepository(_database)),
            _accounts,
            evaluator,
            _database);
    }

    private CatalogueResourceAuthorizationService CreateResourceService(DefaultHttpContext context)
    {
        var accessor = new HttpContextAccessor { HttpContext = context };
        var decisions = new AccountAccessDecisionService(
            _accounts,
            new GrantAdminUnlockService(
                _accounts,
                new PasswordHasher<GrantAdminProtection>(),
                TimeProvider.System));
        return new CatalogueResourceAuthorizationService(
            _database,
            new RequestAuthorityResolver(_accounts, _applications, new ProfileRepository(_database)),
            decisions,
            new AuthorizationEvaluator(
                decisions,
                _applications,
                new PermissionRegistry(),
                new AuthorizationAuditWriter(_accounts),
                accessor));
    }

    private Task CreateHumanAsync(
        Guid accountId,
        IReadOnlySet<AccountFeatureId> features,
        IReadOnlySet<Guid> libraries,
        bool administrator = false) =>
        _accounts.CreateAccountAsync(
            new Account
            {
                Id = accountId,
                Email = $"{accountId:N}@example.com",
                NormalizedEmail = $"{accountId:N}@EXAMPLE.COM",
                IsEnabled = true,
                IsAdministrator = administrator,
                AuthorizationVersion = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            new AccountProfileGrant
            {
                AccountId = accountId,
                ProfileId = MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
                IsDefault = true,
                IsEnabled = true,
                AdminEnabled = administrator,
                AuthorizationVersion = 1,
                GrantedAt = DateTimeOffset.UtcNow,
            },
            features,
            libraries);

    private static DefaultHttpContext HumanContext(Guid accountId, Guid profileId) =>
        new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(TuvimaClaimTypes.PrincipalKind, PrincipalKind.Human.ToString()),
                new Claim(TuvimaClaimTypes.AccountId, accountId.ToString("D")),
                new Claim(TuvimaClaimTypes.ActiveProfileId, profileId.ToString("D")),
            ], "test")),
        };

    private static DisplayWorkRow Work(Guid libraryId, string mediaType) =>
        new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), LibraryId = libraryId.ToString("D"), MediaType = mediaType };

    private static DisplayJourneyRow Journey(Guid libraryId, string mediaType, Guid profileId) =>
        new()
        {
            WorkId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            LibraryId = libraryId.ToString("D"),
            MediaType = mediaType,
            ProfileId = profileId,
        };

    private async Task<Guid> InsertOwnedWorkAsync(Guid libraryId, string title, string mediaType = "Book")
        => (await InsertOwnedWorkWithIdAsync(libraryId, title, mediaType)).AssetId;

    private async Task<(Guid WorkId, Guid AssetId)> InsertOwnedWorkWithIdAsync(
        Guid libraryId,
        string title,
        string mediaType = "Book")
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state)
            VALUES (@workId, @mediaType, 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets
                (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES
                (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @libraryId);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@assetId, 'title', @title, CURRENT_TIMESTAMP);
            """,
            new
            {
                workId,
                editionId,
                assetId,
                hash = Guid.NewGuid().ToString("N"),
                path = $"C:/library/{assetId:N}.epub",
                libraryId = libraryId.ToString("D"),
                title,
                mediaType,
            });
        return (workId, assetId);
    }

    private async Task<Guid> InsertArtworkAsync(Guid ownerId, string entityType)
    {
        var variantId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO entity_assets
                (id, entity_id, entity_type, asset_type, aspect_class, created_at)
            VALUES
                (@variantId, @ownerId, @entityType, 'CoverArt', 'Portrait', CURRENT_TIMESTAMP);
            """,
            new { variantId, ownerId, entityType });
        return variantId;
    }

    private async Task InsertAssetForWorkAsync(
        Guid workId,
        Guid assetId,
        Guid libraryId,
        string title,
        bool createWork)
    {
        var editionId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        if (createWork)
        {
            await connection.ExecuteAsync(
                "INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@workId, 'Book', 'standalone', 'accepted');",
                new { workId });
        }
        await connection.ExecuteAsync(
            """
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets
                (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES
                (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @libraryId);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@assetId, 'title', @title, CURRENT_TIMESTAMP);
            """,
            new
            {
                workId,
                editionId,
                assetId,
                hash = Guid.NewGuid().ToString("N"),
                path = $"C:/library/{assetId:N}.epub",
                libraryId = libraryId.ToString("D"),
                title,
            });
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_databasePath); } catch { }
    }

    private sealed class StubRawProjection(
        IReadOnlyList<DisplayWorkRow> works,
        IReadOnlyList<DisplayJourneyRow> journey) : IRawDisplayProjectionReadService
    {
        public int WorkReadCount { get; private set; }

        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct)
        {
            WorkReadCount++;
            return Task.FromResult(works);
        }

        public Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct) => LoadWorksAsync(ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => LoadJourneyAsync(profileId, lane, ct);

        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) => Task.FromResult(journey);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }

    private sealed class CachedReaderProjection(DisplayWorkProjectionReader reader) : IRawDisplayProjectionReadService
    {
        private IReadOnlyList<DisplayWorkRow>? _cached;
        public int WorkReadCount { get; private set; }
        public int RepositoryReadCount { get; private set; }

        public async Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct)
        {
            WorkReadCount++;
            if (_cached is not null)
            {
                return _cached;
            }

            RepositoryReadCount++;
            return _cached = await reader.LoadAsync(ct);
        }

        public Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct) => LoadWorksAsync(ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => LoadJourneyAsync(profileId, lane, ct);

        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }
}
