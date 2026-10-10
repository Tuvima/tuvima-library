using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Jobs;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Services;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Repository tests using a real SQLite database per test class instance.
/// </summary>
public sealed class RepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public RepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_repo_test_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  MediaAssetRepository
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AccountExternalLogin_InsertFindTouchAndDelete()
    {
        var repo = new AccountExternalLoginRepository(_db);
        var accountRepo = new AccountRepository(_db);
        await accountRepo.InsertAsync(new Account
        {
            Id = Account.SeedAccountId,
            Email = "owner@example.com",
            NormalizedEmail = "OWNER@EXAMPLE.COM",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var login = new AccountExternalLogin
        {
            Id = Guid.NewGuid(),
            AccountId = Account.SeedAccountId,
            Provider = "oidc",
            Issuer = "https://identity.example",
            Subject = $"subject-{Guid.NewGuid():N}",
            Email = "owner@example.com",
            DisplayName = "Owner",
            LinkedAt = DateTimeOffset.UtcNow,
        };

        await repo.InsertAsync(login);

        var byAccount = await repo.GetByAccountAsync(Account.SeedAccountId);
        var bySubject = await repo.GetByProviderSubjectAsync(login.Provider, login.Issuer, login.Subject);

        Assert.Contains(byAccount, item => item.Id == login.Id);
        Assert.NotNull(bySubject);
        Assert.Equal(login.Email, bySubject.Email);

        var otherIssuer = new AccountExternalLogin
        {
            Id = Guid.NewGuid(),
            AccountId = Account.SeedAccountId,
            Provider = login.Provider,
            Issuer = "https://other-identity.example",
            Subject = login.Subject,
            DisplayName = "Other issuer",
            LinkedAt = DateTimeOffset.UtcNow,
        };
        await repo.InsertAsync(otherIssuer);
        Assert.Equal(
            otherIssuer.Id,
            (await repo.GetByProviderSubjectAsync(otherIssuer.Provider, otherIssuer.Issuer, otherIssuer.Subject))?.Id);

        var secondAccount = new Account
        {
            Id = Guid.NewGuid(),
            Email = "other@example.com",
            NormalizedEmail = "OTHER@EXAMPLE.COM",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await accountRepo.InsertAsync(secondAccount);
        var stolenIdentity = new AccountExternalLogin
        {
            Id = Guid.NewGuid(),
            AccountId = secondAccount.Id,
            Provider = login.Provider,
            Issuer = login.Issuer,
            Subject = login.Subject,
            LinkedAt = DateTimeOffset.UtcNow,
        };
        await Assert.ThrowsAsync<SqliteException>(() => repo.InsertAsync(stolenIdentity));

        var lastLoginAt = DateTimeOffset.UtcNow.AddMinutes(1);
        Assert.True(await repo.TouchLastLoginAsync(login.Id, lastLoginAt));

        var touched = await repo.GetByProviderSubjectAsync(login.Provider, login.Issuer, login.Subject);
        Assert.NotNull(touched?.LastLoginAt);

        Assert.True(await repo.DeleteAsync(login.Id));
        Assert.Null(await repo.GetByProviderSubjectAsync(login.Provider, login.Issuer, login.Subject));
        Assert.NotNull(await repo.GetByProviderSubjectAsync(otherIssuer.Provider, otherIssuer.Issuer, otherIssuer.Subject));
    }

    [Fact]
    public async Task MediaAsset_InsertAndFindByHash()
    {
        var repo = new MediaAssetRepository(_db);
        var hash = $"hash_{Guid.NewGuid():N}";
        var editionId = await CreateTestEditionAsync();

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            EditionId = editionId,
            ContentHash = hash,
            FilePathRoot = "/library/Books/test.epub",
            Status = AssetStatus.Normal,
        };

        await repo.InsertAsync(asset);
        var found = await repo.FindByHashAsync(hash);

        Assert.NotNull(found);
        Assert.Equal(asset.Id, found.Id);
        Assert.Equal(hash, found.ContentHash);
        Assert.Equal(AssetStatus.Normal, found.Status);
    }

    [Fact]
    public async Task UserState_Save_UsesGuidBlobAssetForeignKey()
    {
        var assetRepo = new MediaAssetRepository(_db);
        var stateRepo = new UserStateRepository(_db);
        var editionId = await CreateTestEditionAsync();
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            EditionId = editionId,
            ContentHash = $"state_{Guid.NewGuid():N}",
            FilePathRoot = "/library/Audiobooks/test.m4b",
            Status = AssetStatus.Normal,
        };
        await assetRepo.InsertAsync(asset);

        var userId = Guid.NewGuid();
        await stateRepo.SaveAsync(new UserState
        {
            UserId = userId,
            AssetId = asset.Id,
            ContentHash = asset.ContentHash,
            ProgressPct = 42.5,
            LastAccessed = DateTimeOffset.UtcNow,
            ExtendedProperties =
            {
                ["position_seconds"] = "123.5",
            },
        });

        using var conn = _db.CreateConnection();
        var storageType = await conn.ExecuteScalarAsync<string>(
            "SELECT typeof(asset_id) FROM user_states WHERE user_id = @userId AND asset_id = @assetId",
            new { userId, assetId = asset.Id });
        var saved = await stateRepo.GetAsync(userId, asset.Id);

        Assert.Equal("blob", storageType);
        Assert.NotNull(saved);
        Assert.Equal(asset.Id, saved.AssetId);
        Assert.Equal(42.5, saved.ProgressPct);
        Assert.Equal("123.5", saved.ExtendedProperties["position_seconds"]);
    }

    [Fact]
    public async Task MediaAsset_FindByHash_ReturnsNull_WhenNotFound()
    {
        var repo = new MediaAssetRepository(_db);
        var found = await repo.FindByHashAsync("nonexistent_hash_999");

        Assert.Null(found);
    }

    [Fact]
    public async Task MediaAsset_DuplicateHash_Ignored()
    {
        var repo = new MediaAssetRepository(_db);
        var hash = $"dup_{Guid.NewGuid():N}";
        var editionId = await CreateTestEditionAsync();

        var asset1 = new MediaAsset
        {
            Id = Guid.NewGuid(),
            EditionId = editionId,
            ContentHash = hash,
            FilePathRoot = "/first.epub",
            Status = AssetStatus.Normal,
        };
        var asset2 = new MediaAsset
        {
            Id = Guid.NewGuid(),
            EditionId = editionId,
            ContentHash = hash,
            FilePathRoot = "/second.epub",
            Status = AssetStatus.Normal,
        };

        await repo.InsertAsync(asset1);
        await repo.InsertAsync(asset2); // INSERT OR IGNORE

        var found = await repo.FindByHashAsync(hash);
        Assert.Equal(asset1.Id, found!.Id);
    }

    [Fact]
    public async Task MediaAsset_UpdateContentHash_RefreshesKnownPathAsset()
    {
        var repo = new MediaAssetRepository(_db);
        var editionId = await CreateTestEditionAsync();
        var oldHash = $"old_{Guid.NewGuid():N}";
        var newHash = $"new_{Guid.NewGuid():N}";
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            EditionId = editionId,
            ContentHash = oldHash,
            FilePathRoot = "/library/Books/changed-after-writeback.epub",
            Status = AssetStatus.Normal,
        };

        await repo.InsertAsync(asset);

        Assert.True(await repo.UpdateContentHashAsync(asset.Id, newHash));

        Assert.Null(await repo.FindByHashAsync(oldHash));
        var found = await repo.FindByHashAsync(newHash);
        Assert.NotNull(found);
        Assert.Equal(asset.Id, found.Id);
        Assert.Equal(asset.FilePathRoot, found.FilePathRoot);
    }

    [Fact]
    public async Task MediaAsset_Renditions_ShareEditionAndRoundTripDeliveryMetadata()
    {
        var repo = new MediaAssetRepository(_db);
        var editionId = await CreateTestEditionAsync();
        var original = new MediaAsset
        {
            Id = Guid.NewGuid(),
            EditionId = editionId,
            ContentHash = $"original_{Guid.NewGuid():N}",
            FilePathRoot = "/movies/title-4k.mkv",
        };
        var mobile = new MediaAsset
        {
            Id = Guid.NewGuid(),
            EditionId = editionId,
            ContentHash = $"mobile_{Guid.NewGuid():N}",
            FilePathRoot = "/movies/title-mobile.mp4",
            RenditionPurpose = RenditionPurpose.Mobile,
            DerivedFromAssetId = original.Id,
            EncoderProfileVersion = "mobile-h264-v3",
            Width = 1920,
            Height = 1080,
            BitrateBitsPerSecond = 4_500_000,
            VideoCodec = "h264",
            AudioCodec = "aac",
            DynamicRange = "SDR",
            AudioLayout = "stereo",
            RenditionGeneratedAt = DateTimeOffset.UtcNow,
            SourceFingerprint = original.ContentHash,
        };

        Assert.True(await repo.InsertAsync(original));
        Assert.True(await repo.InsertAsync(mobile));

        var renditions = await repo.ListByEditionAsync(editionId);
        Assert.Equal(2, renditions.Count);
        var saved = Assert.Single(renditions, item => item.Id == mobile.Id);
        Assert.Equal(RenditionPurpose.Mobile, saved.RenditionPurpose);
        Assert.Equal(original.Id, saved.DerivedFromAssetId);
        Assert.Equal("mobile-h264-v3", saved.EncoderProfileVersion);
        Assert.Equal(1920, saved.Width);
        Assert.Equal(4_500_000, saved.BitrateBitsPerSecond);
        Assert.Equal(original.ContentHash, saved.SourceFingerprint);
    }

    [Fact]
    public async Task MediaAsset_UpdateRendition_CoLocatesExplicitSourceAndRejectsCycle()
    {
        var repo = new MediaAssetRepository(_db);
        var editionId = await CreateTestEditionAsync();
        var otherEditionId = await CreateTestEditionAsync();
        var source = new MediaAsset
        {
            Id = Guid.NewGuid(),
            EditionId = editionId,
            ContentHash = $"source_{Guid.NewGuid():N}",
            FilePathRoot = "/source.mkv"
        };
        var derived = new MediaAsset
        {
            Id = Guid.NewGuid(),
            EditionId = editionId,
            ContentHash = $"derived_{Guid.NewGuid():N}",
            FilePathRoot = "/derived.mp4"
        };
        var other = new MediaAsset
        {
            Id = Guid.NewGuid(),
            EditionId = otherEditionId,
            ContentHash = $"other_{Guid.NewGuid():N}",
            FilePathRoot = "/other.mkv"
        };
        await repo.InsertAsync(source);
        await repo.InsertAsync(derived);
        await repo.InsertAsync(other);

        derived.RenditionPurpose = RenditionPurpose.Compatibility;
        derived.DerivedFromAssetId = other.Id;
        Assert.True(await repo.UpdateRenditionAsync(derived));
        Assert.Equal(otherEditionId, derived.EditionId);
        Assert.Equal(otherEditionId, (await repo.FindByIdAsync(derived.Id))!.EditionId);

        other.RenditionPurpose = RenditionPurpose.Other;
        other.DerivedFromAssetId = derived.Id;
        Assert.False(await repo.UpdateRenditionAsync(other));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  MetadataClaimRepository
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MetadataClaim_InsertAndRetrieve()
    {
        var repo = new MetadataClaimRepository(_db);
        var entityId = Guid.NewGuid();
        var providerId = await CreateTestProviderAsync();
        var claim = MakeClaim(entityId, "title", "Dune", 0.95, providerId);

        await repo.InsertBatchAsync([claim]);
        var claims = await repo.GetByEntityAsync(entityId);

        Assert.Single(claims);
        Assert.Equal("Dune", claims[0].ClaimValue);
        Assert.Equal(0.95, claims[0].Confidence);
    }

    [Fact]
    public async Task MetadataClaim_MultipleClaims_AllReturned()
    {
        var repo = new MetadataClaimRepository(_db);
        var entityId = Guid.NewGuid();
        var providerId = await CreateTestProviderAsync();

        await repo.InsertBatchAsync([MakeClaim(entityId, "title", "Dune", 0.9, providerId)]);
        await repo.InsertBatchAsync([MakeClaim(entityId, "author", "Frank Herbert", 0.9, providerId)]);
        await repo.InsertBatchAsync([MakeClaim(entityId, "year", "1965", 0.9, providerId)]);

        var claims = await repo.GetByEntityAsync(entityId);
        Assert.Equal(3, claims.Count);
    }

    [Fact]
    public async Task MetadataClaim_ProviderSnapshotSupersedesMissingCurrentFactsButKeepsHistory()
    {
        var repo = new MetadataClaimRepository(_db);
        var entityId = Guid.NewGuid();
        var providerId = await CreateTestProviderAsync();

        await repo.ReplaceCurrentProviderClaimsAsync(
            entityId,
            providerId,
            ["award_received", "award_received_qid"],
            [MakeClaim(entityId, "award_received", "Best Picture", 0.92, providerId)],
            CancellationToken.None);
        await repo.ReplaceCurrentProviderClaimsAsync(
            entityId,
            providerId,
            ["award_received", "award_received_qid"],
            [],
            CancellationToken.None);

        Assert.Empty(await repo.GetByEntityAsync(entityId));
        using var conn = _db.CreateConnection();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM metadata_claims WHERE entity_id = @entityId AND is_current = 0 AND superseded_at IS NOT NULL;",
            new { entityId }));
    }

    [Fact]
    public async Task MetadataClaim_BatchLookup_ReturnsEveryRequestedEntityAndOmitsUnrequestedRows()
    {
        var repo = new MetadataClaimRepository(_db);
        var providerId = await CreateTestProviderAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var unrequested = Guid.NewGuid();

        await repo.InsertBatchAsync([
            MakeClaim(first, "title", "First", 0.9, providerId),
            MakeClaim(second, "title", "Second", 0.9, providerId),
            MakeClaim(unrequested, "title", "Hidden", 0.9, providerId),
        ]);

        var result = await repo.GetByEntitiesAsync([first, second, first, Guid.Empty]);

        Assert.Equal(2, result.Count);
        Assert.Equal("First", Assert.Single(result[first]).ClaimValue);
        Assert.Equal("Second", Assert.Single(result[second]).ClaimValue);
        Assert.DoesNotContain(unrequested, result.Keys);
    }

    [Fact]
    public async Task MetadataClaim_UserLockedFlag_Preserved()
    {
        var repo = new MetadataClaimRepository(_db);
        var entityId = Guid.NewGuid();
        var providerId = await CreateTestProviderAsync();
        var claim = MakeClaim(entityId, "title", "My Title", 0.9, providerId);
        claim.IsUserLocked = true;

        await repo.InsertBatchAsync([claim]);
        var claims = await repo.GetByEntityAsync(entityId);

        Assert.Single(claims);
        Assert.True(claims[0].IsUserLocked);
    }

    [Fact]
    public async Task MetadataClaim_UserManualProvider_IsSelfHealingWhenSeedMissing()
    {
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                "DELETE FROM metadata_providers WHERE id = @id;",
                new { id = WellKnownProviders.UserManual });
        }

        var repo = new MetadataClaimRepository(_db);
        var entityId = Guid.NewGuid();
        var claim = MakeClaim(entityId, "wikidata_qid", "Q155653", 1.0, WellKnownProviders.UserManual);
        claim.IsUserLocked = true;

        await repo.InsertBatchAsync([claim]);

        var claims = await repo.GetByEntityAsync(entityId);
        Assert.Single(claims);
        Assert.Equal(WellKnownProviders.UserManual, claims[0].ProviderId);

        using var verifyConn = _db.CreateConnection();
        var providerCount = await verifyConn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM metadata_providers WHERE id = @id;",
            new { id = WellKnownProviders.UserManual });
        Assert.Equal(1, providerCount);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  CanonicalValueRepository
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CanonicalValue_UpsertAndRetrieve()
    {
        var repo = new CanonicalValueRepository(_db);
        var entityId = Guid.NewGuid();

        await repo.UpsertBatchAsync([new CanonicalValue
        {
            EntityId = entityId, Key = "title", Value = "Dune",
            LastScoredAt = DateTimeOffset.UtcNow,
        }]);

        var values = await repo.GetByEntityAsync(entityId);
        Assert.Single(values);
        Assert.Equal("Dune", values[0].Value);
    }

    [Fact]
    public async Task CanonicalValue_BatchLookup_UsesGuidBlobEntityIds()
    {
        var repo = new CanonicalValueRepository(_db);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await repo.UpsertBatchAsync([
            new CanonicalValue
            {
                EntityId = first,
                Key = "title",
                Value = "Le Petit Prince",
                LastScoredAt = DateTimeOffset.UtcNow,
            },
            new CanonicalValue
            {
                EntityId = second,
                Key = "title",
                Value = "La Vie en rose",
                LastScoredAt = DateTimeOffset.UtcNow,
            },
        ]);

        var requestedIds = Enumerable.Range(0, 1_100)
            .Select(_ => Guid.NewGuid())
            .Append(first)
            .Append(second)
            .ToList();
        var values = await repo.GetByEntitiesAsync(requestedIds);

        using var conn = _db.CreateConnection();
        var storageTypes = conn.Query<string>("""
            SELECT typeof(entity_id) AS EntityIdType
            FROM canonical_values
            WHERE entity_id IN (@first, @second)
            ORDER BY value;
            """, new { first, second }).ToList();

        Assert.True(values.ContainsKey(first));
        Assert.True(values.ContainsKey(second));
        Assert.Equal("Le Petit Prince", Assert.Single(values[first]).Value);
        Assert.Equal("La Vie en rose", Assert.Single(values[second]).Value);
        Assert.Equal(2, storageTypes.Count);
        Assert.All(storageTypes, storageType => Assert.Equal("blob", storageType));
    }

    [Fact]
    public async Task CanonicalValue_Upsert_UpdatesExisting()
    {
        var repo = new CanonicalValueRepository(_db);
        var entityId = Guid.NewGuid();

        await repo.UpsertBatchAsync([new CanonicalValue
        {
            EntityId = entityId, Key = "title", Value = "Old",
            LastScoredAt = DateTimeOffset.UtcNow,
        }]);
        await repo.UpsertBatchAsync([new CanonicalValue
        {
            EntityId = entityId, Key = "title", Value = "New",
            LastScoredAt = DateTimeOffset.UtcNow,
        }]);

        var values = await repo.GetByEntityAsync(entityId);
        Assert.Single(values);
        Assert.Equal("New", values[0].Value);
    }

    [Fact]
    public async Task CanonicalValue_FindByKeyAndPrefix_EmptyPrefixReturnsAllValuesForKey()
    {
        var repo = new CanonicalValueRepository(_db);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var otherKey = Guid.NewGuid();

        await repo.UpsertBatchAsync([
            new CanonicalValue
            {
                EntityId = first,
            Key = "title",
            Value = "Dune",
                LastScoredAt = DateTimeOffset.UtcNow,
            },
            new CanonicalValue
            {
                EntityId = second,
            Key = "title",
            Value = "Foundation",
                LastScoredAt = DateTimeOffset.UtcNow,
            },
            new CanonicalValue
            {
                EntityId = otherKey,
                Key = "media_type",
                Value = "Book",
                LastScoredAt = DateTimeOffset.UtcNow,
            },
        ]);

        var values = await repo.FindByKeyAndPrefixAsync("title", "");

        Assert.Equal(2, values.Count);
        Assert.Contains(values, item => item.EntityId == first && item.Value == "Dune");
        Assert.Contains(values, item => item.EntityId == second && item.Value == "Foundation");
        Assert.DoesNotContain(values, item => item.EntityId == otherKey);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ReviewQueueRepository
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EntityAsset_Upsert_RejectsRetiredBannerTypeAndAllowsLogo()
    {
        var repo = new EntityAssetRepository(_db);
        var entityId = Guid.NewGuid().ToString();
        var createdAt = DateTimeOffset.UtcNow;

        var logoId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repo.UpsertAsync(new EntityAsset
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = "Work",
            AssetTypeValue = "Banner",
            LocalImagePath = "/library/Movies/Arrival/banner.png",
            SourceProvider = "fanart_tv",
            AssetClassValue = "Artwork",
            StorageLocationValue = "Central",
            OwnerScope = "Work",
            IsPreferred = true,
            CreatedAt = createdAt,
        }));
        Assert.Equal("asset", exception.ParamName);

        await repo.UpsertAsync(new EntityAsset
        {
            Id = logoId,
            EntityId = entityId,
            EntityType = "Work",
            AssetTypeValue = "Logo",
            LocalImagePath = "/library/Movies/Arrival/logo.png",
            SourceProvider = "fanart_tv",
            AssetClassValue = "Artwork",
            StorageLocationValue = "Central",
            OwnerScope = "Work",
            IsPreferred = true,
            CreatedAt = createdAt,
        });

        var assets = await repo.GetByEntityAsync(entityId);

        Assert.DoesNotContain(assets, asset => asset.AssetTypeValue == "Banner");
        Assert.Contains(assets, asset => asset.AssetTypeValue == "Logo");

        using var conn = _db.CreateConnection();
        var storageTypes = conn.Query<string>(
            """
            SELECT typeof(entity_id)
            FROM entity_assets
            WHERE id = @logoId
            ORDER BY asset_type;
            """,
            new { logoId }).ToList();
        Assert.Single(storageTypes);
        Assert.All(storageTypes, type => Assert.Equal("blob", type));
    }

    [Fact]
    public async Task EntityAsset_Upsert_WhenFileReplacedInPlaceWithDifferentBytes_RelinksWithoutForeignKeyFailure()
    {
        // Two tracks of one album resolve to the same owner and variant id but carry different embedded
        // art, so the second write replaces the variant's file in place with a new content hash.
        var repo = new EntityAssetRepository(_db);
        var albumId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var directory = Path.Combine(Path.GetTempPath(), $"tuvima_entity_asset_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var coverPath = Path.Combine(directory, $"{variantId:N}.jpg");
            EntityAsset Build() => new()
            {
                Id = variantId,
                EntityId = albumId.ToString(),
                EntityType = "Work",
                AssetTypeValue = "CoverArt",
                ImageUrl = $"/stream/artwork/{variantId}",
                LocalImagePath = coverPath,
                SourceProvider = "embedded",
                AssetClassValue = "Artwork",
                StorageLocationValue = "Central",
                OwnerScope = "Work",
                IsPreferred = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            await File.WriteAllBytesAsync(coverPath, [1, 2, 3, 4]);
            await repo.UpsertAsync(Build());
            await File.WriteAllBytesAsync(coverPath, [9, 8, 7, 6, 5]);
            await repo.UpsertAsync(Build());

            using var conn = _db.CreateConnection();
            Assert.Equal(1, conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM entity_artwork_links WHERE id = @variantId;", new { variantId }));
            var linkedHash = conn.ExecuteScalar<string>(
                """
                SELECT a.content_hash
                FROM entity_artwork_links l
                JOIN artwork_assets a ON a.id = l.artwork_asset_id
                WHERE l.id = @variantId;
                """,
                new { variantId });
            Assert.NotNull(linkedHash);
            Assert.DoesNotContain("source:", linkedHash);
            Assert.Equal(2, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM artwork_assets;"));
            Assert.Single(await repo.GetByEntityAsync(albumId.ToString(), "CoverArt"));
        }
        finally
        {
            TestTemp.DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CanonicalValueArray_BatchLookup_PreservesEntityKeyAndOrdinalGrouping()
    {
        var arrays = new CanonicalValueArrayRepository(_db);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await arrays.SetValuesAsync(first, MetadataFieldConstants.Genre, [
            new CanonicalArrayEntry { Ordinal = 0, Value = "Hip hop" },
            new CanonicalArrayEntry { Ordinal = 1, Value = "Rap" },
        ]);
        await arrays.SetValuesAsync(second, MetadataFieldConstants.Artist, [
            new CanonicalArrayEntry { Ordinal = 0, Value = "Eminem" },
        ]);

        var result = await arrays.GetAllByEntitiesAsync([first, second, first, Guid.Empty]);

        Assert.Equal(2, result.Count);
        Assert.Equal(["Hip hop", "Rap"], result[first][MetadataFieldConstants.Genre].Select(value => value.Value));
        Assert.Equal("Eminem", Assert.Single(result[second][MetadataFieldConstants.Artist]).Value);
    }

    [Fact]
    public async Task EntityAsset_GetByEntities_LoadsAllRequestedOwnersInOneBatchApi()
    {
        var repo = new EntityAssetRepository(_db);
        var firstEntityId = Guid.NewGuid().ToString();
        var secondEntityId = Guid.NewGuid().ToString();
        foreach (var entityId in new[] { firstEntityId, secondEntityId })
        {
            await repo.UpsertAsync(new EntityAsset
            {
                Id = Guid.NewGuid(),
                EntityId = entityId,
                EntityType = "Work",
                AssetTypeValue = "CoverArt",
                SourceProvider = "test",
                AssetClassValue = "Artwork",
                StorageLocationValue = "Central",
                OwnerScope = "Work",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        var assets = await repo.GetByEntitiesAsync([firstEntityId, secondEntityId]);

        Assert.Equal(2, assets.Count);
        Assert.Equal(
            new[] { firstEntityId, secondEntityId }.Order(StringComparer.OrdinalIgnoreCase),
            assets.Select(asset => asset.EntityId).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReviewQueue_InsertAndGetPending()
    {
        var repo = new ReviewQueueRepository(_db);
        var entry = new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = Guid.NewGuid(),
            EntityType = nameof(EntityType.MediaAsset),
            Trigger = ReviewTrigger.LowConfidence,
            Status = ReviewStatus.Pending,
            ConfidenceScore = 0.45,
            Detail = "Low confidence file",
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewReadyAt = DateTimeOffset.UtcNow,
            AutomationCompletedAt = DateTimeOffset.UtcNow,
        };

        await repo.InsertAsync(entry);
        var pending = await repo.GetPendingAsync();

        Assert.Single(pending);
        Assert.Equal(entry.Id, pending[0].Id);
    }

    [Fact]
    public async Task ReviewQueue_Insert_IsIdempotentForPendingEntityAndTrigger()
    {
        var repo = new ReviewQueueRepository(_db);
        var entityId = Guid.NewGuid();
        var first = new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = nameof(EntityType.MediaAsset),
            Trigger = ReviewTrigger.RetailMatchAmbiguous,
            Status = ReviewStatus.Pending,
            ConfidenceScore = 0.42,
            Detail = "First ambiguous match",
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewReadyAt = DateTimeOffset.UtcNow,
            AutomationCompletedAt = DateTimeOffset.UtcNow,
        };
        var second = new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = nameof(EntityType.MediaAsset),
            Trigger = ReviewTrigger.RetailMatchAmbiguous,
            Status = ReviewStatus.Pending,
            ConfidenceScore = 0.61,
            Detail = "Refreshed ambiguous match",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            ReviewReadyAt = DateTimeOffset.UtcNow.AddMinutes(1),
            AutomationCompletedAt = DateTimeOffset.UtcNow.AddMinutes(1),
        };

        var firstId = await repo.InsertAsync(first);
        var secondId = await repo.InsertAsync(second);

        Assert.Equal(firstId, secondId);
        var pending = await repo.GetPendingByEntityAsync(entityId);
        var row = Assert.Single(pending);
        Assert.Equal(first.Id, row.Id);
        Assert.Equal("Refreshed ambiguous match", row.Detail);
        Assert.Equal(0.61, row.ConfidenceScore);
    }

    [Fact]
    public async Task ReviewQueue_Resolve_RemovesFromPending()
    {
        var repo = new ReviewQueueRepository(_db);
        var entry = new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = Guid.NewGuid(),
            EntityType = nameof(EntityType.MediaAsset),
            Trigger = ReviewTrigger.LowConfidence,
            Status = ReviewStatus.Pending,
            ConfidenceScore = 0.45,
            Detail = "Test",
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewReadyAt = DateTimeOffset.UtcNow,
            AutomationCompletedAt = DateTimeOffset.UtcNow,
        };

        await repo.InsertAsync(entry);
        await repo.UpdateStatusAsync(entry.Id, ReviewStatus.Resolved);

        var pending = await repo.GetPendingAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task ReviewQueue_GetPendingAndCount_ExcludeRowsNotReadyForReview()
    {
        var repo = new ReviewQueueRepository(_db);
        var assetRepo = new MediaAssetRepository(_db);
        var editionId = await CreateTestEditionAsync();
        var readyAssetId = Guid.NewGuid();
        var queuedAssetId = Guid.NewGuid();

        await assetRepo.InsertAsync(new MediaAsset
        {
            Id = readyAssetId,
            EditionId = editionId,
            ContentHash = $"ready_{Guid.NewGuid():N}",
            FilePathRoot = "/library/ready.epub",
            Status = AssetStatus.Normal,
        });
        await assetRepo.InsertAsync(new MediaAsset
        {
            Id = queuedAssetId,
            EditionId = editionId,
            ContentHash = $"queued_{Guid.NewGuid():N}",
            FilePathRoot = "/library/queued.epub",
            Status = AssetStatus.Normal,
        });

        await repo.InsertAsync(new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = readyAssetId,
            EntityType = nameof(EntityType.MediaAsset),
            Trigger = ReviewTrigger.LowConfidence,
            Status = ReviewStatus.Pending,
            Detail = "Automation complete",
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewReadyAt = DateTimeOffset.UtcNow,
            AutomationCompletedAt = DateTimeOffset.UtcNow,
        });
        await repo.InsertAsync(new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = queuedAssetId,
            EntityType = nameof(EntityType.MediaAsset),
            Trigger = ReviewTrigger.LowConfidence,
            Status = ReviewStatus.Pending,
            Detail = "Still queued",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var pending = await repo.GetPendingAsync();

        Assert.Single(pending);
        Assert.Equal(readyAssetId, pending[0].EntityId);
        Assert.Single(await repo.GetPendingByEntityAsync(queuedAssetId));
        Assert.Equal(1, await repo.GetPendingCountAsync());
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ApiKeyRepository
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReviewQueue_MarkPendingReadyByEntity_PromotesHiddenPendingRows()
    {
        var repo = new ReviewQueueRepository(_db);
        var assetRepo = new MediaAssetRepository(_db);
        var editionId = await CreateTestEditionAsync();
        var assetId = Guid.NewGuid();

        await assetRepo.InsertAsync(new MediaAsset
        {
            Id = assetId,
            EditionId = editionId,
            ContentHash = $"mark_ready_{Guid.NewGuid():N}",
            FilePathRoot = "/library/mark-ready.epub",
            Status = AssetStatus.Normal,
        });

        await repo.InsertAsync(new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = assetId,
            EntityType = nameof(EntityType.MediaAsset),
            Trigger = ReviewTrigger.LowConfidence,
            Status = ReviewStatus.Pending,
            Detail = "Still in automation",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        Assert.Empty(await repo.GetPendingAsync());

        var promoted = await repo.MarkPendingReadyByEntityAsync(assetId);

        Assert.Equal(1, promoted);
        var pendingAfterPromotion = await repo.GetPendingAsync();
        Assert.Single(pendingAfterPromotion);
        Assert.Equal(assetId, pendingAfterPromotion[0].EntityId);
        Assert.NotNull(pendingAfterPromotion[0].ReviewReadyAt);
        Assert.NotNull(pendingAfterPromotion[0].AutomationCompletedAt);
        Assert.Equal(0, await repo.MarkPendingReadyByEntityAsync(assetId));
    }

    [Fact]
    public async Task ReviewQueue_PromotePendingReadyByEntity_ReturnsOnlyNewlyPromotedRows()
    {
        var repo = new ReviewQueueRepository(_db);
        var assetRepo = new MediaAssetRepository(_db);
        var editionId = await CreateTestEditionAsync();
        var assetId = Guid.NewGuid();

        await assetRepo.InsertAsync(new MediaAsset
        {
            Id = assetId,
            EditionId = editionId,
            ContentHash = $"promote_ready_{Guid.NewGuid():N}",
            FilePathRoot = "/library/promote-ready.epub",
            Status = AssetStatus.Normal,
        });

        var entryId = Guid.NewGuid();
        await repo.InsertAsync(new ReviewQueueEntry
        {
            Id = entryId,
            EntityId = assetId,
            EntityType = nameof(EntityType.MediaAsset),
            Trigger = ReviewTrigger.LowConfidence,
            Status = ReviewStatus.Pending,
            Detail = "Still in automation",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var promoted = await repo.PromotePendingReadyByEntityAsync(assetId);

        var promotedEntry = Assert.Single(promoted);
        Assert.Equal(entryId, promotedEntry.Id);
        Assert.Equal(assetId, promotedEntry.EntityId);
        Assert.NotNull(promotedEntry.ReviewReadyAt);
        Assert.NotNull(promotedEntry.AutomationCompletedAt);

        Assert.Empty(await repo.PromotePendingReadyByEntityAsync(assetId));
    }

    [Fact]
    public async Task WorkIdentityReconciliation_KeepsReadFormatsSeparateAndAlignsAudiobookAuthorIdentityByQid()
    {
        var service = new WorkIdentityReconciliationService(_db);
        var qid = "Q43361";
        var bookWorkId = Guid.NewGuid();
        var audiobookWorkId = Guid.NewGuid();
        var bookEditionId = Guid.NewGuid();
        var audiobookEditionId = Guid.NewGuid();
        var bookAssetId = Guid.NewGuid();
        var audiobookAssetId = Guid.NewGuid();
        var authorPersonId = Guid.NewGuid();

        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind, wikidata_qid)
                VALUES (@bookWorkId, 'Books', 'standalone', @qid),
                       (@audiobookWorkId, 'Audiobooks', 'standalone', @qid);

                INSERT INTO editions (id, work_id)
                VALUES (@bookEditionId, @bookWorkId),
                       (@audiobookEditionId, @audiobookWorkId);

                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@bookAssetId, @bookEditionId, @bookHash, '/library/Books/Harry Potter.epub', 'Normal'),
                       (@audiobookAssetId, @audiobookEditionId, @audioHash, '/library/Audiobooks/Harry Potter.m4b', 'Normal');

                INSERT INTO persons (id, name, wikidata_qid, created_at)
                VALUES (@authorPersonId, 'J. K. Rowling', 'Q34660', @createdAt);
                """,
                new
                {
                    qid,
                    bookWorkId,
                    audiobookWorkId,
                    bookEditionId,
                    audiobookEditionId,
                    bookAssetId,
                    audiobookAssetId,
                    authorPersonId,
                    createdAt = DateTimeOffset.UtcNow.ToString("O"),
                    bookHash = $"book_{Guid.NewGuid():N}",
                    audioHash = $"audio_{Guid.NewGuid():N}",
                });
        }

        var arrays = new CanonicalValueArrayRepository(_db);
        await arrays.SetValuesAsync(
            bookAssetId,
            "author",
            [new CanonicalArrayEntry { Ordinal = 0, Value = "J. K. Rowling", ValueQid = "Q34660" }]);
        await arrays.SetValuesAsync(
            audiobookWorkId,
            "author",
            [new CanonicalArrayEntry { Ordinal = 0, Value = "J.K. Rowling" }]);

        var merged = await service.MergeDuplicateReadWorksByQidAsync();
        var aligned = await service.AlignAudiobookAuthorsWithBooksByQidAsync();

        Assert.Equal(0, merged);
        Assert.Equal(1, aligned);
        using var verify = _db.CreateConnection();
        var workCount = await verify.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM works WHERE wikidata_qid = @qid;",
            new { qid });
        var bookEditionWorkId = await verify.ExecuteScalarAsync<Guid>(
            "SELECT work_id FROM editions WHERE id = @bookEditionId;",
            new { bookEditionId });
        var audiobookEditionWorkId = await verify.ExecuteScalarAsync<Guid>(
            "SELECT work_id FROM editions WHERE id = @audiobookEditionId;",
            new { audiobookEditionId });

        Assert.Equal(2, workCount);
        Assert.Equal(bookWorkId, bookEditionWorkId);
        Assert.Equal(audiobookWorkId, audiobookEditionWorkId);

        var audiobookAuthors = await arrays.GetValuesAsync(audiobookWorkId, "author");
        var audiobookAuthor = Assert.Single(audiobookAuthors);
        Assert.Equal("J. K. Rowling", audiobookAuthor.Value);
        Assert.Equal("Q34660", audiobookAuthor.ValueQid);
        var alignedPersonCreditCount = await verify.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(1)
            FROM primary_person_media_credits
            WHERE media_asset_id = @audiobookAssetId
              AND person_id = @authorPersonId
              AND credit_key = 'author';
            """,
            new { audiobookAssetId, authorPersonId });
        Assert.Equal(1, alignedPersonCreditCount);
        Assert.Equal(0, await service.AlignAudiobookAuthorsWithBooksByQidAsync());
    }

    [Fact]
    public async Task PrimaryPersonCredits_QidBackedCreditNeverFallsBackToConflictingSameNamePerson()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var novelistId = Guid.NewGuid();
        var footballerId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind) VALUES (@workId, 'Audiobooks', 'standalone');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @hash, '/library/Audiobooks/Project Hail Mary/Part 01.m4b', 'Normal');
                INSERT INTO persons (id, name, wikidata_qid, created_at)
                VALUES (@novelistId, 'Andy Weir', 'Q18590295', @createdAt),
                       (@footballerId, 'Andy Weir', 'Q4761465', @createdAt);
                """,
                new
                {
                    workId,
                    editionId,
                    assetId,
                    novelistId,
                    footballerId,
                    hash = $"asset_{assetId:N}",
                    createdAt = DateTimeOffset.UtcNow.ToString("O"),
                });
        }

        var arrays = new CanonicalValueArrayRepository(_db);
        await arrays.SetValuesAsync(workId, "author",
        [
            new CanonicalArrayEntry { Ordinal = 0, Value = "Andy Weir", ValueQid = "Q18590295" },
        ]);

        using var verify = _db.CreateConnection();
        var credits = (await verify.QueryAsync<(Guid PersonId, string PersonQid)>(
            """
            SELECT person_id AS PersonId, person_qid AS PersonQid
            FROM primary_person_media_credits
            WHERE media_asset_id = @assetId AND credit_key = 'author';
            """, new { assetId })).ToList();

        var credit = Assert.Single(credits);
        Assert.Equal(novelistId, credit.PersonId);
        Assert.Equal("Q18590295", credit.PersonQid);
        Assert.DoesNotContain(credits, item => item.PersonId == footballerId);
    }

    [Fact]
    public async Task WorkIdentityReconciliation_AlignsAudiobookChildAndSeriesRootAuthors()
    {
        var service = new WorkIdentityReconciliationService(_db);
        var arrays = new CanonicalValueArrayRepository(_db);
        var qid = "Q190192";
        var bookRootId = Guid.NewGuid();
        var audiobookRootId = Guid.NewGuid();
        var bookWorkId = Guid.NewGuid();
        var audiobookWorkId = Guid.NewGuid();
        var bookEditionId = Guid.NewGuid();
        var audiobookEditionId = Guid.NewGuid();
        var bookAssetId = Guid.NewGuid();
        var audiobookAssetId = Guid.NewGuid();

        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind, parent_key)
                VALUES (@bookRootId, 'Books', 'parent', 'frank-herbert|dune'),
                       (@audiobookRootId, 'Audiobooks', 'parent', 'frank-herbert|dune');

                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, wikidata_qid)
                VALUES (@bookWorkId, 'Books', 'child', @bookRootId, 1, @qid),
                       (@audiobookWorkId, 'Audiobooks', 'child', @audiobookRootId, 1, @qid);

                INSERT INTO editions (id, work_id)
                VALUES (@bookEditionId, @bookWorkId),
                       (@audiobookEditionId, @audiobookWorkId);

                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@bookAssetId, @bookEditionId, @bookHash, '/library/Books/Dune.epub', 'Normal'),
                       (@audiobookAssetId, @audiobookEditionId, @audioHash, '/library/Audiobooks/Dune.m4b', 'Normal');
                """,
                new
                {
                    qid,
                    bookRootId,
                    audiobookRootId,
                    bookWorkId,
                    audiobookWorkId,
                    bookEditionId,
                    audiobookEditionId,
                    bookAssetId,
                    audiobookAssetId,
                    bookHash = $"book_{Guid.NewGuid():N}",
                    audioHash = $"audio_{Guid.NewGuid():N}",
                });
        }

        await arrays.SetValuesAsync(
            bookAssetId,
            "author",
            [new CanonicalArrayEntry { Ordinal = 0, Value = "Frank Herbert", ValueQid = "Q1005" }]);
        await arrays.SetValuesAsync(
            audiobookWorkId,
            "author",
            [new CanonicalArrayEntry { Ordinal = 0, Value = "Frank P. Herbert" }]);
        await arrays.SetValuesAsync(
            audiobookRootId,
            "author",
            [new CanonicalArrayEntry { Ordinal = 0, Value = "Frank P. Herbert" }]);

        Assert.Equal(2, await service.AlignAudiobookAuthorsWithBooksByQidAsync());

        foreach (var targetId in new[] { audiobookWorkId, audiobookRootId })
        {
            var author = Assert.Single(await arrays.GetValuesAsync(targetId, "author"));
            Assert.Equal("Frank Herbert", author.Value);
            Assert.Equal("Q1005", author.ValueQid);
        }
    }

    [Theory]
    [InlineData("Q85815891", null, "4a40febf-b65d-4ea7-810d-8317f0706a88", true)]
    [InlineData(null, null, "4a40febf-b65d-4ea7-810d-8317f0706a88", true)]
    [InlineData("Q85815891", "Q42", "4a40febf-b65d-4ea7-810d-8317f0706a88", false)]
    [InlineData(null, null, "81", false)]
    public async Task WorkIdentityReconciliation_CombinesBookFormatsOnlyWithCompatibleTrustedIdentity(
        string? firstQid, string? secondQid, string uuid, bool shouldMerge)
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var epub = Guid.NewGuid(); var azw = Guid.NewGuid();
        var e1 = Guid.NewGuid(); var e2 = Guid.NewGuid(); var profile = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        var args = new { first, second, epub, azw, e1, e2, profile, firstQid, secondQid, uuid };
        await conn.ExecuteAsync("""
            INSERT INTO profiles(id,display_name,created_at) VALUES(@profile,'Reader','2026-01-01');
            INSERT INTO works(id,media_type,work_kind,wikidata_qid) VALUES(@first,'Books','standalone',@firstQid),(@second,'Books','standalone',@secondQid);
            INSERT INTO editions(id,work_id,format_label) VALUES(@e1,@first,'EPUB'),(@e2,@second,'AZW3');
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,status)
            VALUES(@epub,@e1,'epub-test','/library/Book.epub','Normal'),(@azw,@e2,'azw-test','/library/Book.azw3','Normal');
            INSERT INTO canonical_values(entity_id,key,value,last_scored_at)
            VALUES(@epub,'calibre_uuid',@uuid,'2026-01-01'),(@azw,'calibre_uuid',@uuid,'2026-01-01'),
                  (@first,'title','Book','2026-01-01'),(@second,'title','Book: A Subtitle','2026-01-01');
            INSERT INTO user_states(user_id,asset_id,progress_pct) VALUES(@profile,@epub,25),(@profile,@azw,60);
            INSERT INTO profile_saved_items(profile_id,entity_kind,entity_id,saved_at) VALUES(@profile,'Book',@second,'2026-01-01');
            INSERT INTO profile_reactions(profile_id,entity_kind,entity_id,reaction,updated_at) VALUES(@profile,'Book',@second,'Love','2026-01-01');
            """, args);
        var service = new WorkIdentityReconciliationService(_db);
        Assert.Equal(shouldMerge ? 1 : 0, await service.MergeDuplicateReadWorksByQidAsync());
        Assert.Equal(shouldMerge ? 1 : 2, await conn.ExecuteScalarAsync<int>("SELECT COUNT(DISTINCT work_id) FROM editions WHERE id IN (@e1,@e2)", args));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM media_assets WHERE id IN (@epub,@azw)", args));
        Assert.Equal(85, await conn.ExecuteScalarAsync<int>("SELECT SUM(progress_pct) FROM user_states WHERE user_id=@profile", args));
        if (shouldMerge)
        {
            var target = await conn.ExecuteScalarAsync<Guid>("SELECT work_id FROM editions WHERE id=@e1", args);
            Assert.Equal(target, await conn.ExecuteScalarAsync<Guid>("SELECT entity_id FROM profile_saved_items WHERE profile_id=@profile", args));
            Assert.Equal(target, await conn.ExecuteScalarAsync<Guid>("SELECT entity_id FROM profile_reactions WHERE profile_id=@profile", args));
            Assert.Equal(target, WorkRedirects.Resolve(conn, second));
        }
        Assert.Equal(0, await service.MergeDuplicateReadWorksByQidAsync());
    }

    [Fact]
    public async Task WorkIdentityReconciliation_MergesDuplicateReadWorksWithinMediaTypeByQid()
    {
        var service = new WorkIdentityReconciliationService(_db);
        var qid = "Q190192";
        var firstWorkId = Guid.NewGuid();
        var secondWorkId = Guid.NewGuid();
        var firstEditionId = Guid.NewGuid();
        var secondEditionId = Guid.NewGuid();
        var firstAssetId = Guid.NewGuid();
        var secondAssetId = Guid.NewGuid();

        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind, wikidata_qid)
                VALUES (@firstWorkId, 'Books', 'standalone', @qid),
                       (@secondWorkId, 'Books', 'standalone', @qid);

                INSERT INTO editions (id, work_id)
                VALUES (@firstEditionId, @firstWorkId),
                       (@secondEditionId, @secondWorkId);

                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@firstAssetId, @firstEditionId, @firstHash, '/library/Books/Dune.epub', 'Normal'),
                       (@secondAssetId, @secondEditionId, @secondHash, '/library/Books/Dune-copy.epub', 'Normal');
                """,
                new
                {
                    qid,
                    firstWorkId,
                    secondWorkId,
                    firstEditionId,
                    secondEditionId,
                    firstAssetId,
                    secondAssetId,
                    firstHash = $"book_{Guid.NewGuid():N}",
                    secondHash = $"book_{Guid.NewGuid():N}",
                });
        }

        var merged = await service.MergeDuplicateReadWorksByQidAsync();

        Assert.Equal(1, merged);
        using var verify = _db.CreateConnection();
        var workCount = await verify.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM works WHERE wikidata_qid = @qid;",
            new { qid });
        var editionWorkIds = (await verify.QueryAsync<Guid>(
            "SELECT DISTINCT work_id FROM editions WHERE id IN (@firstEditionId, @secondEditionId);",
            new { firstEditionId, secondEditionId })).ToList();

        Assert.Equal(1, workCount);
        Assert.Single(editionWorkIds);
    }

    [Fact]
    public async Task WorkIdentityReconciliation_RepeatedMergeCollapsesDuplicateArtworkIdempotently()
    {
        var service = new WorkIdentityReconciliationService(_db);
        var qid = "Q_ARTWORK_DUPLICATE";
        var firstWorkId = Guid.NewGuid();
        var secondWorkId = Guid.NewGuid();
        var firstEditionId = Guid.NewGuid();
        var secondEditionId = Guid.NewGuid();
        var firstAssetId = Guid.NewGuid();
        var secondAssetId = Guid.NewGuid();
        var firstArtworkId = Guid.NewGuid();
        var secondArtworkId = Guid.NewGuid();

        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind, wikidata_qid)
                VALUES (@firstWorkId, 'Books', 'standalone', @qid),
                       (@secondWorkId, 'Books', 'standalone', @qid);

                INSERT INTO editions (id, work_id)
                VALUES (@firstEditionId, @firstWorkId),
                       (@secondEditionId, @secondWorkId);

                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@firstAssetId, @firstEditionId, @firstHash, '/library/Books/Artwork One.epub', 'Normal'),
                       (@secondAssetId, @secondEditionId, @secondHash, '/library/Books/Artwork Two.epub', 'Normal');

                INSERT INTO entity_assets
                    (id, entity_id, entity_type, asset_type, image_url,
                     local_image_path_s, source_provider, width_px, height_px,
                     aspect_class, is_user_override, created_at)
                VALUES
                    (@firstArtworkId, @firstWorkId, 'Work', 'CoverArt', 'https://images.example/Dune.jpg',
                     '/assets/dune-small.webp', 'user_upload', 1200, 1800,
                     'Portrait', 1, '2025-01-01T00:00:00Z');

                INSERT INTO entity_assets
                    (id, entity_id, entity_type, asset_type, image_url,
                     local_image_path_l, source_provider, width_px, height_px,
                     primary_hex, is_preferred, is_locally_exported, created_at)
                VALUES
                    (@secondArtworkId, @secondWorkId, 'Work', 'CoverArt', 'https://images.example/dune.jpg',
                     '/assets/dune-large.webp', 'wikidata', 2000, 3000,
                     '#123456', 1, 1, '2025-02-01T00:00:00Z');
                """,
                new
                {
                    qid,
                    firstWorkId,
                    secondWorkId,
                    firstEditionId,
                    secondEditionId,
                    firstAssetId,
                    secondAssetId,
                    firstArtworkId,
                    secondArtworkId,
                    firstHash = $"book_{Guid.NewGuid():N}",
                    secondHash = $"book_{Guid.NewGuid():N}",
                });
        }

        Assert.Equal(1, await service.MergeDuplicateReadWorksByQidAsync());
        for (var pass = 0; pass < 5; pass++)
        {
            Assert.Equal(0, await service.MergeDuplicateReadWorksByQidAsync());
        }

        using var verify = _db.CreateConnection();
        Assert.Equal(1, await verify.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM works WHERE wikidata_qid = @qid;",
            new { qid }));
        Assert.Equal(1, await verify.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(1)
            FROM entity_assets
            WHERE entity_type = 'Work'
              AND asset_type = 'CoverArt'
              AND image_url = 'https://images.example/dune.jpg' COLLATE NOCASE;
            """));

        var artwork = await verify.QuerySingleAsync<dynamic>(
            """
            SELECT local_image_path_s, local_image_path_l, width_px, height_px,
                   primary_hex, is_preferred, is_user_override, is_locally_exported
            FROM entity_assets
            WHERE entity_type = 'Work' AND asset_type = 'CoverArt';
            """);
        Assert.Equal("/assets/dune-small.webp", (string)artwork.local_image_path_s);
        Assert.Equal("/assets/dune-large.webp", (string)artwork.local_image_path_l);
        Assert.Equal(2000L, (long)artwork.width_px);
        Assert.Equal(3000L, (long)artwork.height_px);
        Assert.Equal("#123456", (string)artwork.primary_hex);
        Assert.Equal(1L, (long)artwork.is_preferred);
        Assert.Equal(1L, (long)artwork.is_user_override);
        Assert.Equal(1L, (long)artwork.is_locally_exported);
        Assert.Equal("ok", await verify.ExecuteScalarAsync<string>("PRAGMA integrity_check;"));
        Assert.Empty(await verify.QueryAsync<string>("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task WorkIdentityReconciliation_DoesNotMergeComicIssuesBySeriesQid()
    {
        var service = new WorkIdentityReconciliationService(_db);
        var seriesQid = "Q2633138";
        var firstWorkId = Guid.NewGuid();
        var secondWorkId = Guid.NewGuid();
        var firstEditionId = Guid.NewGuid();
        var secondEditionId = Guid.NewGuid();
        var firstAssetId = Guid.NewGuid();
        var secondAssetId = Guid.NewGuid();

        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind, wikidata_qid)
                VALUES (@firstWorkId, 'Comics', 'child', @seriesQid),
                       (@secondWorkId, 'Comics', 'child', @seriesQid);

                INSERT INTO editions (id, work_id)
                VALUES (@firstEditionId, @firstWorkId),
                       (@secondEditionId, @secondWorkId);

                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@firstAssetId, @firstEditionId, @firstHash, '/library/Comics/Batman 404.cbz', 'Normal'),
                       (@secondAssetId, @secondEditionId, @secondHash, '/library/Comics/Batman 405.cbz', 'Normal');
                """,
                new
                {
                    seriesQid,
                    firstWorkId,
                    secondWorkId,
                    firstEditionId,
                    secondEditionId,
                    firstAssetId,
                    secondAssetId,
                    firstHash = $"comic_{Guid.NewGuid():N}",
                    secondHash = $"comic_{Guid.NewGuid():N}",
                });
        }

        var merged = await service.MergeDuplicateReadWorksByQidAsync();

        Assert.Equal(0, merged);
        using var verify = _db.CreateConnection();
        var workCount = await verify.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM works WHERE wikidata_qid = @seriesQid;",
            new { seriesQid });
        var editionWorkIds = (await verify.QueryAsync<Guid>(
            "SELECT DISTINCT work_id FROM editions WHERE id IN (@firstEditionId, @secondEditionId);",
            new { firstEditionId, secondEditionId })).ToList();

        Assert.Equal(2, workCount);
        Assert.Equal(2, editionWorkIds.Count);
    }

    [Fact]
    public async Task IdentityJob_CreateAsync_IgnoresDuplicateActiveJobForSameEntityAndPass()
    {
        var repo = new IdentityJobRepository(_db);
        var entityId = Guid.NewGuid();

        await repo.CreateAsync(new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Books),
            Pass = "Quick",
            State = IdentityJobState.Queued.ToString(),
        });

        await repo.CreateAsync(new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Books),
            Pass = "Quick",
            State = IdentityJobState.Queued.ToString(),
        });

        var queued = await repo.GetByStateAsync(IdentityJobState.Queued, 10);
        Assert.Single(queued, j => j.EntityId == entityId && j.Pass == "Quick");
    }

    [Fact]
    public async Task IdentityJob_CreateAsync_AllowsRetryAfterTerminalReviewState()
    {
        var repo = new IdentityJobRepository(_db);
        var entityId = Guid.NewGuid();

        await repo.CreateAsync(new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Movies),
            Pass = "Quick",
            State = IdentityJobState.RetailNoMatch.ToString(),
        });

        await repo.CreateAsync(new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Movies),
            Pass = "Quick",
            State = IdentityJobState.Queued.ToString(),
        });

        var noMatch = await repo.GetByStateAsync(IdentityJobState.RetailNoMatch, 10);
        var queued = await repo.GetByStateAsync(IdentityJobState.Queued, 10);

        Assert.Single(noMatch, j => j.EntityId == entityId && j.Pass == "Quick");
        Assert.Single(queued, j => j.EntityId == entityId && j.Pass == "Quick");
    }

    [Fact]
    public async Task IdentityJob_UserResolution_ResumesActiveReviewJobForEnrichment()
    {
        var repo = new IdentityJobRepository(_db);
        var entityId = Guid.NewGuid();
        var existing = new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Music),
            Pass = "Quick",
            State = IdentityJobState.RetailMatchedNeedsReview.ToString(),
            LastError = "Manual confirmation required",
            NextRetryAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        await repo.CreateAsync(existing);

        var resumedId = await repo.CreateOrResumeUserResolutionAsync(new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Music),
            Pass = "Quick",
            State = IdentityJobState.RetailMatched.ToString(),
        });

        var resumed = await repo.GetByIdAsync(resumedId);
        Assert.Equal(existing.Id, resumedId);
        Assert.NotNull(resumed);
        Assert.Equal(IdentityJobState.RetailMatched.ToString(), resumed!.State);
        Assert.Null(resumed.LastError);
        Assert.Null(resumed.NextRetryAt);
        Assert.Null(resumed.LeaseOwner);
        Assert.Single(await repo.GetByStateAsync(IdentityJobState.RetailMatched, 10), j => j.EntityId == entityId);
    }

    [Fact]
    public async Task IdentityJob_UpdateStateAsync_PreservesLeaseOnlyForProcessingStates()
    {
        var repo = new IdentityJobRepository(_db);
        var job = new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = Guid.NewGuid(),
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Books),
            Pass = "Quick",
            State = IdentityJobState.Queued.ToString(),
        };

        await repo.CreateAsync(job);

        var leased = await repo.LeaseNextAsync(
            "test-worker",
            [IdentityJobState.Queued],
            1,
            TimeSpan.FromMinutes(10));

        var leasedJob = Assert.Single(leased);
        Assert.Equal("test-worker", leasedJob.LeaseOwner);

        await repo.UpdateStateAsync(job.Id, IdentityJobState.RetailSearching);
        var searching = await repo.GetByIdAsync(job.Id);

        Assert.NotNull(searching);
        Assert.Equal("test-worker", searching!.LeaseOwner);
        Assert.NotNull(searching.LeaseExpiresAt);
        Assert.Equal(1, searching.AttemptCount);

        await repo.UpdateStateAsync(job.Id, IdentityJobState.RetailMatched);
        var matched = await repo.GetByIdAsync(job.Id);

        Assert.NotNull(matched);
        Assert.Null(matched!.LeaseOwner);
        Assert.Null(matched.LeaseExpiresAt);
        Assert.Equal(1, matched.AttemptCount);
    }

    [Fact]
    public async Task IdentityJob_ScheduleRetryAsync_DelaysLeaseAndIncrementsAttempts()
    {
        var repo = new IdentityJobRepository(_db);
        var job = new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = Guid.NewGuid(),
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Books),
            Pass = "Quick",
            State = IdentityJobState.Queued.ToString(),
        };
        await repo.CreateAsync(job);

        var leased = await repo.LeaseNextAsync("test-worker", [IdentityJobState.Queued], 1, TimeSpan.FromMinutes(10));
        Assert.Single(leased);

        await repo.ScheduleRetryAsync(job.Id, IdentityJobState.Queued, DateTimeOffset.UtcNow.AddMinutes(10), "locked", CancellationToken.None);

        var retrying = await repo.GetByIdAsync(job.Id);
        Assert.NotNull(retrying);
        Assert.Equal(1, retrying!.AttemptCount);
        Assert.Equal(1, retrying.PoisonAttemptCount);
        Assert.Equal("locked", retrying.LastError);
        Assert.NotNull(retrying.NextRetryAt);
        Assert.Null(retrying.LeaseOwner);

        var notEligible = await repo.LeaseNextAsync("test-worker-2", [IdentityJobState.Queued], 1, TimeSpan.FromMinutes(10));
        Assert.Empty(notEligible);
    }

    [Fact]
    public async Task IdentityJob_UnavailableCapabilityRetriesDoNotConsumePoisonBudget()
    {
        var repo = new IdentityJobRepository(_db);
        var job = new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = Guid.NewGuid(),
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Books),
            Pass = "Quick",
            State = IdentityJobState.Queued.ToString(),
        };
        await repo.CreateAsync(job);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await repo.ScheduleRetryForOutcomeAsync(
                job.Id,
                IdentityJobState.Queued,
                DateTimeOffset.UtcNow,
                "No enabled provider is configured.",
                BackgroundJobOutcomeCategory.UnavailableCapability);
        }

        var retrying = await repo.GetByIdAsync(job.Id);
        Assert.NotNull(retrying);
        Assert.Equal(10, retrying!.AttemptCount);
        Assert.Equal(0, retrying.PoisonAttemptCount);
        Assert.Equal(nameof(BackgroundJobOutcomeCategory.UnavailableCapability), retrying.LastOutcomeCategory);
        Assert.NotEqual(IdentityJobState.Failed.ToString(), retrying.State);
    }

    [Fact]
    public async Task IdentityJob_MarkDeadLetteredAsync_UsesTerminalFailedState()
    {
        var repo = new IdentityJobRepository(_db);
        var job = new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = Guid.NewGuid(),
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Books),
            Pass = "Quick",
            State = IdentityJobState.Queued.ToString(),
        };
        await repo.CreateAsync(job);

        await repo.MarkDeadLetteredAsync(job.Id, "poison data", CancellationToken.None);

        var failed = await repo.GetByIdAsync(job.Id);
        Assert.NotNull(failed);
        Assert.Equal(IdentityJobState.Failed.ToString(), failed!.State);
        Assert.Equal("poison data", failed.LastError);

        var leased = await repo.LeaseNextAsync("test-worker", [IdentityJobState.Queued], 1, TimeSpan.FromMinutes(10));
        Assert.Empty(leased);
    }

    [Fact]
    public async Task IdentityJob_ReclaimStuckJobsAsync_FailsRetryExhaustedIntermediateJobs()
    {
        var repo = new IdentityJobRepository(_db);
        var job = new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = Guid.NewGuid(),
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Books),
            Pass = "Quick",
            State = IdentityJobState.UniverseEnriching.ToString(),
        };
        await repo.CreateAsync(job);

        using (var conn = _db.CreateConnection())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE identity_jobs
                    SET attempt_count = 5,
                        updated_at = @updatedAt
                    WHERE id = @id;
                """;
                cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"));
                cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(job.Id);
                cmd.ExecuteNonQuery();
            }
        }

        var reclaimed = await repo.ReclaimStuckJobsAsync(
            IdentityJobState.UniverseEnriching,
            TimeSpan.FromMinutes(5));
        var failed = await repo.GetByIdAsync(job.Id);

        Assert.Equal(1, reclaimed);
        Assert.NotNull(failed);
        Assert.Equal(IdentityJobState.Failed.ToString(), failed!.State);
        Assert.Equal("Stuck intermediate state exceeded retry limit", failed.LastError);
    }

    [Fact]
    public async Task IdentityJob_ReclaimStuckJobsAsync_RecoversFailedStage3EnhancerCandidates()
    {
        var repo = new IdentityJobRepository(_db);
        var canonicalRepo = new CanonicalValueRepository(_db);
        var entityId = Guid.NewGuid();
        var job = new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Movies),
            Pass = "Quick",
            State = IdentityJobState.Failed.ToString(),
            LastError = "Stuck intermediate state exceeded retry limit",
        };
        await repo.CreateAsync(job);
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = "wikidata_qid", Value = "Q123", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = "tmdb_movie_id", Value = "123", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var reclaimed = await repo.ReclaimStuckJobsAsync(
            IdentityJobState.UniverseEnriching,
            TimeSpan.FromMinutes(5));
        var recovered = await repo.GetByIdAsync(job.Id);

        Assert.Equal(1, reclaimed);
        Assert.NotNull(recovered);
        Assert.Equal(IdentityJobState.QidResolved.ToString(), recovered!.State);
        Assert.Equal(0, recovered.AttemptCount);
        Assert.Equal("Recovered for Stage 3 artwork/enhancer retry", recovered.LastError);
    }

    [Fact]
    public async Task IdentityJob_ReclaimStuckJobsAsync_OnlyReclaimsTheRequestedPipelineStage()
    {
        var repo = new IdentityJobRepository(_db);
        var hydratingJob = new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = Guid.NewGuid(),
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Books),
            Pass = "Quick",
            State = IdentityJobState.Hydrating.ToString(),
        };
        var universeJob = new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = Guid.NewGuid(),
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Books),
            Pass = "Universe",
            State = IdentityJobState.UniverseEnriching.ToString(),
        };
        await repo.CreateAsync(hydratingJob);
        await repo.CreateAsync(universeJob);

        using (var conn = _db.CreateConnection())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE identity_jobs
                    SET updated_at = @updatedAt
                    WHERE id = @id;
                    """;
                cmd.Parameters.AddWithValue(
                    "@updatedAt",
                    DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"));
                var idParameter = cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Blob);
                foreach (var id in new[] { hydratingJob.Id, universeJob.Id })
                {
                    idParameter.Value = GuidSql.ToBlob(id);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        var reclaimed = await repo.ReclaimStuckJobsAsync(
            IdentityJobState.Hydrating,
            TimeSpan.FromMinutes(5));
        var recoveredHydration = await repo.GetByIdAsync(hydratingJob.Id);
        var untouchedUniverse = await repo.GetByIdAsync(universeJob.Id);

        Assert.Equal(1, reclaimed);
        Assert.Equal(IdentityJobState.QidResolved.ToString(), recoveredHydration!.State);
        Assert.Equal(IdentityJobState.UniverseEnriching.ToString(), untouchedUniverse!.State);
    }

    [Fact]
    public async Task IdentityJob_RecoverInterruptedJobsAsync_ReleasesStartupLeasesImmediately()
    {
        var repo = new IdentityJobRepository(_db);
        var job = new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = Guid.NewGuid(),
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Movies),
            Pass = "Quick",
            State = IdentityJobState.RetailMatched.ToString(),
        };
        await repo.CreateAsync(job);

        var leased = await repo.LeaseNextAsync(
            "previous-engine",
            [IdentityJobState.RetailMatched],
            1,
            TimeSpan.FromMinutes(10));
        Assert.Single(leased);

        await repo.UpdateStateAsync(job.Id, IdentityJobState.BridgeSearching);

        var recoveredCount = await repo.RecoverInterruptedJobsAsync();
        var recovered = await repo.GetByIdAsync(job.Id);

        Assert.Equal(1, recoveredCount);
        Assert.NotNull(recovered);
        Assert.Equal(IdentityJobState.RetailMatched.ToString(), recovered!.State);
        Assert.Null(recovered.LeaseOwner);
        Assert.Null(recovered.LeaseExpiresAt);
        Assert.Equal("Recovered after engine restart", recovered.LastError);
    }

    [Theory]
    [InlineData("RetailSearching", "Queued")]
    [InlineData("BridgeSearching", "RetailMatched")]
    [InlineData("Hydrating", "QidResolved")]
    [InlineData("UniverseEnriching", "QidResolved")]
    public async Task IdentityJob_StartupRecoversUnleasedWorkWithoutChangingBatch(string state, string resumedState)
    {
        var repo = new IdentityJobRepository(_db);
        var runId = Guid.NewGuid();
        var job = new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = Guid.NewGuid(),
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Books),
            Pass = "Quick",
            State = state,
            IngestionRunId = runId,
        };
        await repo.CreateAsync(job);
        Assert.Equal(1, await repo.RecoverInterruptedJobsAsync());
        var recovered = await repo.GetByIdAsync(job.Id);
        Assert.Equal(resumedState, recovered!.State);
        Assert.Equal(runId, recovered.IngestionRunId);
        Assert.Null(recovered.LeaseOwner);
        Assert.Equal(0, await repo.RecoverInterruptedJobsAsync());
    }

    [Fact]
    public async Task ReviewQueue_PendingCountAndPurge_UseGuidBlobEntityIds()
    {
        var assetRepo = new MediaAssetRepository(_db);
        var repo = new ReviewQueueRepository(_db);
        var editionId = await CreateTestEditionAsync();
        var assetId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        await assetRepo.InsertAsync(new MediaAsset
        {
            Id = assetId,
            EditionId = editionId,
            ContentHash = $"review_{Guid.NewGuid():N}",
            FilePathRoot = "/library/review.epub",
            Status = AssetStatus.Normal,
        });

        await repo.InsertAsync(new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = assetId,
            EntityType = nameof(EntityType.MediaAsset),
            Trigger = ReviewTrigger.LowConfidence,
            Status = ReviewStatus.Pending,
            SourceOperationId = operationId,
            ReviewReadyAt = DateTimeOffset.UtcNow,
            AutomationCompletedAt = DateTimeOffset.UtcNow,
        });

        using var conn = _db.CreateConnection();
        var storageTypes = conn.QuerySingle<(string IdType, string EntityIdType, string SourceOperationIdType)>("""
            SELECT typeof(id) AS IdType,
                   typeof(entity_id) AS EntityIdType,
                   typeof(source_operation_id) AS SourceOperationIdType
            FROM review_queue
            WHERE entity_id = @assetId;
            """, new { assetId });

        Assert.Equal("blob", storageTypes.IdType);
        Assert.Equal("blob", storageTypes.EntityIdType);
        Assert.Equal("blob", storageTypes.SourceOperationIdType);
        Assert.Equal(1, await repo.GetPendingCountAsync());
        Assert.Equal(0, await repo.PurgeOrphanedAsync());
        Assert.Single(await repo.GetPendingByEntityAsync(assetId));
    }

    [Fact]
    public async Task IngestionBatchLogAndActivity_UseGuidBlobRunIds()
    {
        var batchRepo = new IngestionBatchRepository(_db);
        var logRepo = new IngestionLogRepository(_db);
        var activityRepo = new SystemActivityRepository(_db);
        var batchId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        await batchRepo.CreateAsync(new IngestionBatch
        {
            Id = batchId,
            Status = "running",
            SourcePath = "/library/import",
            FilesTotal = 1,
        });

        await logRepo.InsertAsync(new IngestionLogEntry
        {
            Id = Guid.NewGuid(),
            FilePath = "/library/import/book.epub",
            MediaAssetId = assetId,
            Status = "queued_identity",
            IngestionRunId = batchId,
        });

        await activityRepo.LogAsync(new SystemActivityEntry
        {
            ActionType = SystemActionType.FileIngested,
            EntityId = assetId,
            ProfileId = Profile.SeedProfileId,
            IngestionRunId = batchId,
        });

        using var conn = _db.CreateConnection();
        var batchType = conn.ExecuteScalar<string>("SELECT typeof(id) FROM ingestion_batches WHERE id = @batchId;", new { batchId });
        var logTypes = conn.QuerySingle<(string IdType, string AssetType, string RunType)>("""
            SELECT typeof(id) AS IdType,
                   typeof(media_asset_id) AS AssetType,
                   typeof(ingestion_run_id) AS RunType
            FROM ingestion_log
            WHERE ingestion_run_id = @batchId;
            """, new { batchId });
        var activityTypes = conn.QuerySingle<(string EntityType, string ProfileType, string RunType)>("""
            SELECT typeof(entity_id) AS EntityType,
                   typeof(profile_id) AS ProfileType,
                   typeof(ingestion_run_id) AS RunType
            FROM system_activity
            WHERE ingestion_run_id = @batchId;
            """, new { batchId });

        Assert.Equal("blob", batchType);
        Assert.Equal("blob", logTypes.IdType);
        Assert.Equal("blob", logTypes.AssetType);
        Assert.Equal("blob", logTypes.RunType);
        Assert.Equal("blob", activityTypes.EntityType);
        Assert.Equal("blob", activityTypes.ProfileType);
        Assert.Equal("blob", activityTypes.RunType);
        Assert.NotNull(await batchRepo.GetByIdAsync(batchId));
        Assert.Single(await logRepo.GetByRunIdAsync(batchId));
        Assert.Single(await activityRepo.GetByRunIdAsync(batchId));
    }

    [Fact]
    public async Task IngestionBatchProgress_CountsIdentityWorkAfterFileOperationsFinish()
    {
        var batchRepo = new IngestionBatchRepository(_db);
        var jobRepo = new IdentityJobRepository(_db);
        var batchId = Guid.NewGuid();

        await batchRepo.CreateAsync(new IngestionBatch
        {
            Id = batchId,
            Status = "running",
            SourcePath = "/library/books",
            FilesTotal = 2,
            FilesProcessed = 2,
        });

        await jobRepo.CreateAsync(new IdentityJob
        {
            EntityId = Guid.NewGuid(),
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Books),
            IngestionRunId = batchId,
            Pass = "Quick",
            State = IdentityJobState.Hydrating.ToString(),
        });
        await jobRepo.CreateAsync(new IdentityJob
        {
            EntityId = Guid.NewGuid(),
            EntityType = nameof(EntityType.MediaAsset),
            MediaType = nameof(MediaType.Books),
            IngestionRunId = batchId,
            Pass = "Quick",
            State = IdentityJobState.QidResolved.ToString(),
        });

        var snapshot = await batchRepo.GetProgressSnapshotAsync(batchId);

        Assert.Equal(2, snapshot.OutstandingOperations);
        Assert.Equal(1, snapshot.ActiveOperations);
        Assert.Equal(1, snapshot.QueuedOperations);
        Assert.Equal(0, snapshot.RetryWaitingOperations);
    }

    [Fact]
    public async Task IngestionBatch_GetRecent_IncludesAbandonedTerminalBatches()
    {
        var batchRepo = new IngestionBatchRepository(_db);
        var batchId = Guid.NewGuid();

        await batchRepo.CreateAsync(new IngestionBatch
        {
            Id = batchId,
            Status = "abandoned",
            SourcePath = "/library/interrupted",
            FilesTotal = 5,
            FilesProcessed = 5,
            FilesFailed = 5,
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var recent = await batchRepo.GetRecentAsync(10);

        var batch = Assert.Single(recent);
        Assert.Equal(batchId, batch.Id);
        Assert.Equal("abandoned", batch.Status);
        Assert.Equal(5, batch.FilesFailed);
    }

    [Fact]
    public async Task BridgeId_UpsertAndBatchLookup_UseGuidBlobEntityIds()
    {
        var repo = new BridgeIdRepository(_db);
        var entityId = Guid.NewGuid();

        await repo.UpsertAsync(new BridgeIdEntry
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            IdType = BridgeIdKeys.WikidataQid,
            IdValue = "Q123",
            ProviderId = "wikidata",
        });

        var byEntity = await repo.GetByEntityAsync(entityId);
        var byEntities = await repo.GetByEntitiesAsync([entityId]);

        using var conn = _db.CreateConnection();
        var storageTypes = conn.QuerySingle<(string IdType, string EntityIdType, string ProviderType)>("""
            SELECT typeof(id) AS IdType,
                   typeof(entity_id) AS EntityIdType,
                   typeof(provider_id) AS ProviderType
            FROM bridge_ids
            WHERE entity_id = @entityId;
            """, new { entityId });

        Assert.Single(byEntity);
        Assert.True(byEntities.ContainsKey(entityId));
        Assert.Equal("blob", storageTypes.IdType);
        Assert.Equal("blob", storageTypes.EntityIdType);
        Assert.Equal("text", storageTypes.ProviderType);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SystemActivityRepository
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SystemActivity_LogAndRetrieve()
    {
        var repo = new SystemActivityRepository(_db);

        await repo.LogAsync(new SystemActivityEntry
        {
            ActionType = SystemActionType.FileIngested,
            Detail = "Ingested test.epub",
            OccurredAt = DateTimeOffset.UtcNow,
        });

        var recent = await repo.GetRecentAsync(10);
        Assert.Single(recent);
        Assert.Equal(SystemActionType.FileIngested, recent[0].ActionType);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  DatabaseConnection — schema + migrations
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DatabaseConnection_SchemaCreation_Succeeds()
    {
        // If constructor + InitializeSchema + RunStartupChecks succeeded, schema is valid.
        Assert.True(true);
    }

    [Fact]
    public void DatabaseConnection_SecondInit_IsIdempotent()
    {
        _db.InitializeSchema();
        _db.RunStartupChecks();
        Assert.True(true);
    }

    [Fact]
    public void EnrichmentRefreshSchedule_PersistsFutureIntentSeparatelyFromOperations()
    {
        var entityId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO enrichment_refresh_schedule
                (entity_type, entity_id, stage, provider_id, policy_key, interval_days,
                 next_due_at, status, updated_at)
            VALUES
                ('Person', @entityId, 'people', 'wikidata', 'people.default', 30,
                 @nextDueAt, 'Scheduled', @updatedAt);
            """,
            new
            {
                entityId,
                nextDueAt = now.AddDays(30).ToString("O"),
                updatedAt = now.ToString("O"),
            });

        var interval = conn.ExecuteScalar<int>(
            "SELECT interval_days FROM enrichment_refresh_schedule WHERE entity_id = @entityId;",
            new { entityId });
        var operationCount = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM media_operations WHERE entity_id = @entityId;",
            new { entityId });

        Assert.Equal(30, interval);
        Assert.Equal(0, operationCount);
    }

    [Fact]
    public async Task PersonGroupMembers_ConcurrentLinksRemainCompleteAndPendingNameCanBeReplaced()
    {
        var repo = new PersonRepository(_db);
        var group = await repo.CreateAsync(new Person
        {
            Name = "Test Band",
            WikidataQid = "Q9000001",
            Roles = ["Artist"],
        });
        var members = new List<Person>();
        for (var i = 0; i < 12; i++)
        {
            members.Add(await repo.CreateAsync(new Person
            {
                Name = $"Name pending (Q90001{i:00})",
                WikidataQid = $"Q90001{i:00}",
                Roles = ["Performer"],
            }));
        }

        await Task.WhenAll(members.Select(member => repo.LinkGroupMemberAsync(group.Id, member.Id)));
        await repo.UpdateNameAsync(members[0].Id, "Resolved Member");

        using var conn = _db.CreateConnection();
        var linkedCount = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM person_group_members WHERE group_id = @groupId;",
            new { groupId = group.Id });
        var resolvedName = conn.ExecuteScalar<string>(
            "SELECT name FROM persons WHERE id = @memberId;",
            new { memberId = members[0].Id });

        Assert.Equal(members.Count, linkedCount);
        Assert.Equal("Resolved Member", resolvedName);
    }

    [Fact]
    public async Task PersonGraphReferences_RetainRelationshipOnlyGroupMembers()
    {
        var repo = new PersonRepository(_db);
        var group = await repo.CreateAsync(new Person
        {
            Name = "Reference Test Band",
            WikidataQid = "Q9200001",
            IsGroup = true,
            Roles = ["Artist"],
        });
        var member = await repo.CreateAsync(new Person
        {
            Name = "Relationship Only Member",
            WikidataQid = "Q9200002",
            Roles = ["Performer"],
        });

        await repo.LinkGroupMemberAsync(group.Id, member.Id);

        Assert.Equal(0, await repo.CountMediaLinksAsync(member.Id));
        Assert.Equal(1, await repo.CountGraphReferencesAsync(member.Id));
        Assert.Equal(1, await repo.CountGraphReferencesAsync(group.Id));
    }

    [Fact]
    public async Task ReassignAllLinks_PreservesMembershipWhenMemberOrGroupIdentityIsMerged()
    {
        var repo = new PersonRepository(_db);
        var sourceGroup = await repo.CreateAsync(new Person
        {
            Name = "Duplicate Band",
            WikidataQid = "Q9100001",
            IsGroup = true,
            Roles = ["Artist"],
        });
        var canonicalGroup = await repo.CreateAsync(new Person
        {
            Name = "Canonical Band",
            WikidataQid = "Q9100002",
            IsGroup = true,
            Roles = ["Artist"],
        });
        var sourceMember = await repo.CreateAsync(new Person
        {
            Name = "Temporary Member",
            WikidataQid = "Q9100003",
            Roles = ["Performer"],
        });
        var canonicalMember = await repo.CreateAsync(new Person
        {
            Name = "Canonical Member",
            WikidataQid = "Q9100004",
            Roles = ["Performer"],
        });

        await repo.LinkGroupMemberAsync(sourceGroup.Id, sourceMember.Id);

        await repo.ReassignAllLinksAsync(sourceMember.Id, canonicalMember.Id);
        await repo.DeleteAsync(sourceMember.Id);
        await repo.ReassignAllLinksAsync(sourceGroup.Id, canonicalGroup.Id);
        await repo.DeleteAsync(sourceGroup.Id);

        using var conn = _db.CreateConnection();
        var linkedCount = conn.ExecuteScalar<int>(
            """
            SELECT COUNT(*)
            FROM person_group_members
            WHERE group_id = @groupId AND member_id = @memberId;
            """,
            new { groupId = canonicalGroup.Id, memberId = canonicalMember.Id });
        var staleCount = conn.ExecuteScalar<int>(
            """
            SELECT COUNT(*)
            FROM person_group_members
            WHERE group_id IN (@sourceGroupId, @sourceMemberId)
               OR member_id IN (@sourceGroupId, @sourceMemberId);
            """,
            new { sourceGroupId = sourceGroup.Id, sourceMemberId = sourceMember.Id });

        Assert.Equal(1, linkedCount);
        Assert.Equal(0, staleCount);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  TransactionJournal
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TransactionJournal_LogAndPrune()
    {
        var journal = new TransactionJournal(_db);
        journal.Log("HUB_CREATED", "Collection", Guid.NewGuid());
        journal.Log("WORK_AUTO_LINKED", "Work", Guid.NewGuid());
        journal.Prune(1);
        Assert.True(true); // no exception = pass
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MetadataClaim MakeClaim(
        Guid entityId, string key, string value, double confidence = 0.9, Guid? providerId = null) => new()
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            ProviderId = providerId ?? Guid.NewGuid(),
            ClaimKey = key,
            ClaimValue = value,
            Confidence = confidence,
            ClaimedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Creates a Collection → Work → Edition chain in the database and returns the Edition ID.
    /// Required to satisfy the FK constraint on media_assets.edition_id.
    /// </summary>
    private async Task<Guid> CreateTestEditionAsync()
    {
        using var conn = _db.CreateConnection();
        var collectionId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();

        await conn.ExecuteAsync(
            """
            INSERT INTO collections (id, created_at) VALUES (@collectionId, datetime('now'));
            INSERT INTO works (id, collection_id, media_type) VALUES (@workId, @collectionId, 'Epub');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            """,
            new { collectionId, workId, editionId });
        return editionId;
    }

    /// <summary>
    /// Inserts a test provider into metadata_providers and returns its ID.
    /// Required to satisfy the FK constraint on metadata_claims.provider_id.
    /// </summary>
    private async Task<Guid> CreateTestProviderAsync()
    {
        using var conn = _db.CreateConnection();
        var providerId = Guid.NewGuid();
        using var cmd = conn.CreateCommand();
        await conn.ExecuteAsync(
            "INSERT INTO metadata_providers (id, name, version) VALUES (@id, @name, '1.0')",
            new { id = providerId, name = $"test-provider-{providerId:N}" });
        return providerId;
    }
}

/// <summary>
/// Tests for <see cref="CollectionRuleEvaluator.ComputeRuleHash"/> — verifies that
/// the discriminator predicate technique produces distinct hashes for collections
/// with identical rules but different group_by_field values.
/// </summary>
public sealed class CollectionRuleEvaluatorHashTests
{
    [Fact]
    public void GroupRelationship_ChangesMembershipHash()
    {
        var first = new CollectionRuleGroup
        {
            Conditions = [new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "Movies" }],
        };
        var second = new CollectionRuleGroup
        {
            JoinWithPrevious = "or",
            Conditions = [new CollectionRulePredicate { Field = "genre", Op = "eq", Value = "Drama" }],
        };
        var orDefinition = new CollectionRuleDefinition { Groups = [first, second] };
        var andDefinition = new CollectionRuleDefinition
        {
            Groups =
            [
                first,
                new CollectionRuleGroup
                {
                    JoinWithPrevious = "and",
                    Conditions = second.Conditions,
                },
            ],
        };

        Assert.NotEqual(
            CollectionRuleEvaluator.ComputeRuleHash(orDefinition),
            CollectionRuleEvaluator.ComputeRuleHash(andDefinition));
    }

    [Fact]
    public void SameRules_SameHash()
    {
        var rules1 = new CollectionRulePredicate[] { new() { Field = "media_type", Op = "eq", Value = "Books" } };
        var rules2 = new CollectionRulePredicate[] { new() { Field = "media_type", Op = "eq", Value = "Books" } };

        var hash1 = CollectionRuleEvaluator.ComputeRuleHash(rules1);
        var hash2 = CollectionRuleEvaluator.ComputeRuleHash(rules2);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void DisplayLabels_DoNotChangeMembershipHash()
    {
        var first = new CollectionRulePredicate
        {
            Field = "award_received",
            Op = "in",
            Values = ["Q102427", "Q103916"],
            DisplayValues = ["Best Picture", "Best Actor"],
        };
        var second = new CollectionRulePredicate
        {
            Field = "award_received",
            Op = "in",
            Values = ["Q102427", "Q103916"],
            DisplayValues = ["Academy Award for Best Picture", "Academy Award for Best Actor"],
        };

        Assert.Equal(
            CollectionRuleEvaluator.ComputeRuleHash([first]),
            CollectionRuleEvaluator.ComputeRuleHash([second]));
    }

    [Fact]
    public void SameRules_DifferentGroupBy_DifferentHash()
    {
        // "All Books" (no group_by) and "Books by Series" (group_by=series)
        // should produce different hashes when a _group_by discriminator is added.
        var baseRules = new CollectionRulePredicate[] { new() { Field = "media_type", Op = "eq", Value = "Books" } };

        var hashFlat = CollectionRuleEvaluator.ComputeRuleHash(baseRules);
        var hashGrouped = CollectionRuleEvaluator.ComputeRuleHash(
            [.. baseRules, new CollectionRulePredicate { Field = "_group_by", Op = "eq", Value = "series" }]);

        Assert.NotEqual(hashFlat, hashGrouped);
    }

    [Fact]
    public void DifferentGroupByFields_DifferentHashes()
    {
        // "Music by Artist" and "Music by Album" should produce different hashes.
        var baseRules = new CollectionRulePredicate[] { new() { Field = "media_type", Op = "eq", Value = "Music" } };

        var hashArtist = CollectionRuleEvaluator.ComputeRuleHash(
            [.. baseRules, new CollectionRulePredicate { Field = "_group_by", Op = "eq", Value = "artist" }]);
        var hashAlbum = CollectionRuleEvaluator.ComputeRuleHash(
            [.. baseRules, new CollectionRulePredicate { Field = "_group_by", Op = "eq", Value = "album" }]);

        Assert.NotEqual(hashArtist, hashAlbum);
    }

    [Fact]
    public void SameGroupBy_SameHash_Idempotent()
    {
        var rules = new CollectionRulePredicate[]
        {
            new() { Field = "media_type", Op = "eq", Value = "Books" },
            new() { Field = "_group_by", Op = "eq", Value = "series" },
        };

        var hash1 = CollectionRuleEvaluator.ComputeRuleHash(rules);
        var hash2 = CollectionRuleEvaluator.ComputeRuleHash(rules);

        Assert.Equal(hash1, hash2);
    }
}
