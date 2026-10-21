using System.Security.Claims;
using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

// The Vault (Videos spec WP9, D17, D36): an item in a Vault library does not exist for a request that has not switched NSFW mode
// on in its own device session. "Does not exist" means absent from every list, count and projection, and "not found" (never
// "forbidden") from every check on one item. Library grants never open it; an unlocked session does.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private static readonly Guid VaultProfile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId;

    // ── Lists, counts and projections ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Vault_ALockedProfile_SeesNoVaultItemInAnyList_WhateverItsGrantsOrRole(bool administrator, bool grantedTheVaultLibrary)
    {
        var shape = await SeedVaultAsync(administrator, grantedTheVaultLibrary);
        var context = VaultContext(shape.Account, NewVaultSession(shape.Account));
        var service = CreateService(context, RealReaders());

        Assert.Equal([shape.Normal.WorkId], (await service.LoadWorksAsync(default)).Select(row => row.WorkId).ToArray());
        Assert.Equal([shape.Normal.WorkId], (await service.LoadVisibleWorkIdsAsync(default)).ToArray());
        Assert.Equal([shape.Normal.WorkId], (await service.LoadHomeWorksAsync(default)).Select(row => row.WorkId).ToArray());
        Assert.Equal([shape.Normal.AssetId], (await service.LoadAuthorizedAssetsAsync(default)).Select(row => row.AssetId).ToArray());
        Assert.DoesNotContain(shape.Secret.WorkId, (await service.LoadDetailWorksAsync(shape.Secret.WorkId, default)).Select(row => row.WorkId));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Vault_AnUnlockedProfile_KeepsVaultItemsOutOfEverySharedList_ButCanOpenTheFile_EvenWithoutAGrantOnTheVaultLibrary(bool administrator, bool grantedTheVaultLibrary)
    {
        // Videos spec D20, D27, D45 (security review M2): the Vault is its own area with its own routes. An unlocked session does not
        // see Vault items mixed into the shared lists (works, visible ids, Home); it can still open a Vault file it found in the area.
        var shape = await SeedVaultAsync(administrator, grantedTheVaultLibrary);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var service = CreateService(VaultContext(shape.Account, session), RealReaders());

        Assert.Equal([shape.Normal.WorkId], (await service.LoadWorksAsync(default)).Select(row => row.WorkId).ToArray());
        Assert.Equal([shape.Normal.WorkId], (await service.LoadVisibleWorkIdsAsync(default)).ToArray());
        Assert.Equal([shape.Normal.WorkId], (await service.LoadHomeWorksAsync(default)).Select(row => row.WorkId).ToArray());
        Assert.Contains(shape.Secret.AssetId, (await service.LoadAuthorizedAssetsAsync(default)).Select(row => row.AssetId));
    }

    [Fact]
    public async Task Vault_AnUnlockedProfileWithoutTheWatchFeature_StillSeesNothingOfTheVault()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false, features: new HashSet<AccountFeatureId> { AccountFeatureId.Read });
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var service = CreateService(VaultContext(shape.Account, session), RealReaders());

        Assert.DoesNotContain(shape.Secret.WorkId, (await service.LoadWorksAsync(default)).Select(row => row.WorkId));
        Assert.DoesNotContain(shape.Secret.WorkId, await service.LoadVisibleWorkIdsAsync(default));
        Assert.DoesNotContain(shape.Secret.AssetId, (await service.LoadAuthorizedAssetsAsync(default)).Select(row => row.AssetId));
    }

    [Fact]
    public async Task Vault_AWorkWithAnyFileInTheVault_IsHiddenAsAWhole_FromALockedProfile()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        // One Work with a normal file and a Vault file: nothing about it may show while locked.
        var mixedWork = shape.Normal.WorkId;
        await InsertAssetForWorkAsync(mixedWork, Guid.NewGuid(), shape.VaultLibrary, "Mixed copy", createWork: false);
        var service = CreateService(VaultContext(shape.Account, NewVaultSession(shape.Account)), RealReaders());

        Assert.DoesNotContain(mixedWork, (await service.LoadWorksAsync(default)).Select(row => row.WorkId));
        Assert.DoesNotContain(mixedWork, await service.LoadVisibleWorkIdsAsync(default));
    }

    [Fact]
    public async Task Vault_ContinueWatchingAndStates_LeaveOutVaultRows_UntilUnlocked()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var raw = new StubRawProjection(
            [Work(shape.NormalLibrary, "Movie"), Work(shape.VaultLibrary, "Movie")],
            [Journey(shape.NormalLibrary, "Movie", VaultProfile), Journey(shape.VaultLibrary, "Movie", VaultProfile)]);

        var locked = CreateService(VaultContext(shape.Account, NewVaultSession(shape.Account)), raw);
        Assert.Single(await locked.LoadJourneyAsync(VaultProfile, null, default));
        Assert.Single(await locked.LoadStatesAsync(VaultProfile, null, default));
        Assert.Single(await locked.LoadWorksAsync(default));

        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var unlocked = CreateService(VaultContext(shape.Account, session), raw);
        // Unlocking opens the Vault area, not the shared Continue Watching (Videos spec D27, D33, D45).
        Assert.Single(await unlocked.LoadJourneyAsync(VaultProfile, null, default));
        Assert.Single(await unlocked.LoadStatesAsync(VaultProfile, null, default));
    }

    [Fact]
    public async Task Vault_RecentAdditions_LeaveOutVaultRows_UntilUnlocked()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var normalRow = Work(shape.NormalLibrary, "Movie");
        var vaultRow = Work(shape.VaultLibrary, "Movie");
        var candidates = new[]
        {
            new RecentCandidate { WorkId = normalRow.WorkId, RootWorkId = normalRow.WorkId, LibraryId = normalRow.LibraryId, MediaType = "Movie" },
            new RecentCandidate { WorkId = vaultRow.WorkId, RootWorkId = vaultRow.WorkId, LibraryId = vaultRow.LibraryId, MediaType = "Movie" },
        };
        var raw = new StubRawProjection([normalRow, vaultRow], []);

        var locked = CreateService(VaultContext(shape.Account, NewVaultSession(shape.Account)), raw);
        Assert.Equal([normalRow.WorkId], (await locked.FilterRecentWorksAsync([normalRow, vaultRow], VaultProfile, default)).Select(row => row.WorkId).ToArray());
        Assert.Equal([normalRow.WorkId], (await locked.FilterRecentCandidatesAsync(candidates, VaultProfile, default)).Select(row => row.WorkId).ToArray());

        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var unlocked = CreateService(VaultContext(shape.Account, session), raw);
        // Recently Added stays free of the Vault even when it is open (Videos spec D27, D45).
        Assert.Equal([normalRow.WorkId], (await unlocked.FilterRecentWorksAsync([normalRow, vaultRow], VaultProfile, default)).Select(row => row.WorkId).ToArray());
        Assert.Equal([normalRow.WorkId], (await unlocked.FilterRecentCandidatesAsync(candidates, VaultProfile, default)).Select(row => row.WorkId).ToArray());
    }

    [Fact]
    public async Task Vault_Favourites_LeaveOutVaultWorks_UntilUnlocked()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var normalRow = Work(shape.NormalLibrary, "Movie");
        var vaultRow = Work(shape.VaultLibrary, "Movie");
        var raw = new FavoritesProjection([normalRow, vaultRow], new HashSet<Guid> { normalRow.WorkId, vaultRow.WorkId });

        var locked = CreateService(VaultContext(shape.Account, NewVaultSession(shape.Account)), raw);
        Assert.Equal([normalRow.WorkId], (await locked.LoadFavoriteWorkIdsAsync(VaultProfile, default)).ToArray());

        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var unlocked = CreateService(VaultContext(shape.Account, session), raw);
        Assert.Equal([normalRow.WorkId], (await unlocked.LoadFavoriteWorkIdsAsync(VaultProfile, default)).ToArray());
    }

    [Fact]
    public async Task Vault_HomeCollections_AreBuiltOnlyFromWorksTheProfileMaySee()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var lockedRaw = RealReaders();
        var locked = CreateService(VaultContext(shape.Account, NewVaultSession(shape.Account)), lockedRaw);
        await locked.LoadHomeCollectionsAsync(VaultProfile, default);
        Assert.Equal([shape.Normal.WorkId], lockedRaw.AllowedWorkIdsPassedToHomeCollections!.ToArray());

        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var unlockedRaw = RealReaders();
        var unlocked = CreateService(VaultContext(shape.Account, session), unlockedRaw);
        await unlocked.LoadHomeCollectionsAsync(VaultProfile, default);
        Assert.Equal([shape.Normal.WorkId], unlockedRaw.AllowedWorkIdsPassedToHomeCollections!.ToArray());
    }

    [Fact]
    public async Task Vault_ASessionThatIsLocked_LosesTheVaultAtOnce_ForEveryWayItCanBeLocked()
    {
        var other = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@other,'Other','#7C4DFF','StandardUser',@now);",
                new { other, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: false);
        var nsfw = new NsfwAccessRepository(_database);

        async Task<bool> SeesTheVaultAsync(Guid session) =>
            (await CreateService(VaultContext(shape.Account, session), RealReaders()).LoadAuthorizedAssetsAsync(default))
            .Any(row => row.WorkId == shape.Secret.WorkId);

        async Task<Guid> UnlockedSessionAsync()
        {
            var session = NewVaultSession(shape.Account);
            await UnlockVaultAsync(session);
            Assert.True(await SeesTheVaultAsync(session), "A fresh unlock should open the Vault.");
            return session;
        }

        // The user locks it.
        var session = await UnlockedSessionAsync();
        await nsfw.LockSessionAsync(session);
        Assert.False(await SeesTheVaultAsync(session));

        // The idle time runs out.
        session = await UnlockedSessionAsync();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE nsfw_session_unlocks SET expires_at=@past WHERE session_id=@session;",
                new { session, past = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O") });
        }

        Assert.False(await SeesTheVaultAsync(session));

        // The device switches to another profile and back.
        session = await UnlockedSessionAsync();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE auth_sessions SET active_profile_id=@other WHERE id=@session;", new { session, other });
            connection.Execute("UPDATE auth_sessions SET active_profile_id=@profile WHERE id=@session;", new { session, profile = VaultProfile });
        }

        Assert.False(await SeesTheVaultAsync(session));

        // The administrator closes the profile's NSFW access.
        session = await UnlockedSessionAsync();
        await nsfw.SetAsync(VaultProfile, NsfwAccessMode.Off, null, null, DateTimeOffset.UtcNow);
        Assert.False(await SeesTheVaultAsync(session));
        await nsfw.SetAsync(VaultProfile, NsfwAccessMode.Allowed, null, null, DateTimeOffset.UtcNow);
        Assert.False(await SeesTheVaultAsync(session));

        // The sign-in session is revoked.
        session = await UnlockedSessionAsync();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE auth_sessions SET revoked_at=@now WHERE id=@session;", new { session, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        Assert.False(await SeesTheVaultAsync(session));

        // The profile becomes a Kids profile, or gets a content limit.
        session = await UnlockedSessionAsync();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET content_limit='R' WHERE id=@profile;", new { profile = VaultProfile });
        }

        Assert.False(await SeesTheVaultAsync(session));
    }

    [Fact]
    public async Task Vault_AKidsOrContentLimitedProfile_CanNeverUnlock_SoItNeverSeesTheVault()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: true);
        var session = NewVaultSession(shape.Account);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET role='RestrictedProfile' WHERE id=@profile;", new { profile = VaultProfile });
        }

        await Assert.ThrowsAnyAsync<Exception>(() => UnlockVaultAsync(session));

        var service = CreateService(VaultContext(shape.Account, session), RealReaders());
        Assert.DoesNotContain(shape.Secret.WorkId, (await service.LoadWorksAsync(default)).Select(row => row.WorkId));
    }

    [Fact]
    public async Task Vault_TheResolver_NeverUnlocksAnythingButAPersonOnTheirOwnDashboardSession()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var resolver = new VaultAccessResolver(new VaultLibraryRepository(_database), new NsfwAccessRepository(_database), TimeProvider.System);
        RequestAuthority Authority(PrincipalKind kind, bool authenticated = true, Guid? sessionId = null, bool enabled = true) =>
            new(kind, authenticated, shape.Account, VaultProfile, Guid.NewGuid(), sessionId ?? session, Guid.NewGuid(),
                AccountEnabled: enabled, GrantEnabled: enabled, ApplicationEnabled: true);

        Assert.True(await resolver.IsUnlockedAsync(Authority(PrincipalKind.Human)));
        Assert.False(await resolver.IsUnlockedAsync(Authority(PrincipalKind.DelegatedUserClient)));
        Assert.False(await resolver.IsUnlockedAsync(Authority(PrincipalKind.ServiceApplication)));
        Assert.False(await resolver.IsUnlockedAsync(Authority(PrincipalKind.Anonymous)));
        Assert.False(await resolver.IsUnlockedAsync(Authority(PrincipalKind.Human, authenticated: false)));
        Assert.False(await resolver.IsUnlockedAsync(Authority(PrincipalKind.Human, enabled: false)));
        Assert.False(await resolver.IsUnlockedAsync(Authority(PrincipalKind.Human, sessionId: Guid.NewGuid())));
        Assert.False(await resolver.IsUnlockedAsync(Authority(PrincipalKind.Human, sessionId: Guid.Empty)));
        Assert.False(await resolver.IsUnlockedAsync(new RequestAuthority(PrincipalKind.Human, true, shape.Account, null, null, session, null, true, true)));
    }

    [Fact]
    public async Task Vault_AServiceApplicationAndAPairedApp_NeverSeeTheVault_EvenWithFullLibraryPermission()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var resolver = new VaultAccessResolver(new VaultLibraryRepository(_database), new NsfwAccessRepository(_database), TimeProvider.System);

        var visibility = await resolver.ResolveAsync(new RequestAuthority(
            PrincipalKind.ServiceApplication, true, shape.Account, VaultProfile, Guid.NewGuid(), session, null, true, true, true));

        Assert.False(visibility.Unlocked);
        Assert.True(visibility.IsVaultLibrary(shape.VaultLibrary.ToString("D")));
        Assert.True(visibility.HidesLibrary(shape.VaultLibrary.ToString("D")));
        Assert.False(visibility.HidesLibrary(shape.NormalLibrary.ToString("D")));
    }

    // ── One item: stream, playback, artwork, covers, text tracks, progress, cast … (every route behind the catalogue filters) ──

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Vault_ALockedProfile_GetsNotFound_NeverForbidden_ForAVaultFile_ForEveryPermission(bool administrator, bool grantedTheVaultLibrary)
    {
        var shape = await SeedVaultAsync(administrator, grantedTheVaultLibrary);
        var context = VaultContext(shape.Account, NewVaultSession(shape.Account));
        var resources = CreateResourceService(context);

        foreach (var permission in new[]
        {
            ApplicationPermissionIds.PlaybackRead, ApplicationPermissionIds.PlaybackWrite, ApplicationPermissionIds.ArtworkRead,
            ApplicationPermissionIds.LibraryRead, ApplicationPermissionIds.ProgressRead, ApplicationPermissionIds.ProgressWrite,
            ApplicationPermissionIds.DownloadsRead, ApplicationPermissionIds.MetadataRead,
        })
        {
            Assert.Equal(CatalogueResourceAccess.NotFound, await resources.EvaluateAssetAsync(context, shape.Secret.AssetId, permission));
            Assert.Equal(CatalogueResourceAccess.NotFound, await resources.EvaluateEntityAsync(context, "Work", shape.Secret.WorkId, permission));
            Assert.Equal(CatalogueResourceAccess.NotFound, await resources.EvaluateAnyEntityAsync(context, shape.Secret.AssetId, permission));
            Assert.Equal(CatalogueResourceAccess.NotFound, await resources.EvaluateAnyEntityAsync(context, shape.Secret.WorkId, permission));
        }

        Assert.Empty(await resources.GetAuthorizedAssetIdsForWorkAsync(context, shape.Secret.WorkId, VaultProfile, ApplicationPermissionIds.PlaybackRead));
        Assert.Null(await resources.FindAuthorizedAssetForWorkAsync(context, shape.Secret.WorkId, VaultProfile, ApplicationPermissionIds.PlaybackRead));
        // An ordinary file is untouched by all of this.
        Assert.Equal(CatalogueResourceAccess.Allowed, await resources.EvaluateAssetAsync(context, shape.Normal.AssetId, ApplicationPermissionIds.PlaybackRead));
    }

    [Fact]
    public async Task Vault_ALockedProfile_GetsExactlyTheAnswerAMissingFileGets()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var context = VaultContext(shape.Account, NewVaultSession(shape.Account));
        var resources = CreateResourceService(context);
        var missing = Guid.NewGuid();

        Assert.Equal(
            await resources.EvaluateAssetAsync(context, missing, ApplicationPermissionIds.PlaybackRead),
            await resources.EvaluateAssetAsync(context, shape.Secret.AssetId, ApplicationPermissionIds.PlaybackRead));
        Assert.Equal(
            await resources.EvaluateEntityAsync(context, "Work", missing, ApplicationPermissionIds.PlaybackRead),
            await resources.EvaluateEntityAsync(context, "Work", shape.Secret.WorkId, ApplicationPermissionIds.PlaybackRead));

        // Through the real route filter the two are the same empty 404 result.
        var provider = new ServiceCollection().AddSingleton(resources).BuildServiceProvider();
        var filter = new CatalogueAssetAccessFilter(ApplicationPermissionIds.PlaybackRead);
        var vaultResult = await RunAssetFilterAsync(filter, provider, context, shape.Secret.AssetId);
        var missingResult = await RunAssetFilterAsync(filter, provider, context, missing);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(vaultResult);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(missingResult);
        Assert.Equal("next", await RunAssetFilterAsync(filter, provider, context, shape.Normal.AssetId));
    }

    [Fact]
    public async Task Vault_AnArtworkVariantOfAVaultItem_IsNeverServedToALockedProfile()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var variant = await InsertArtworkAsync(shape.Secret.WorkId, "Work");
        var context = VaultContext(shape.Account, NewVaultSession(shape.Account));
        var resources = CreateResourceService(context);

        Assert.NotEqual(CatalogueResourceAccess.Allowed, await resources.EvaluateArtworkVariantAsync(context, variant, ApplicationPermissionIds.ArtworkRead));

        var provider = new ServiceCollection().AddSingleton(resources).BuildServiceProvider();
        var filter = new CatalogueArtworkAccessFilter(ApplicationPermissionIds.ArtworkRead);
        var http = new DefaultHttpContext { User = context.User, RequestServices = provider };
        http.Request.RouteValues["variantId"] = variant.ToString("D");
        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ => ValueTask.FromResult<object?>("next"));
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
    }

    [Fact]
    public async Task Vault_AnUnlockedProfile_CanReachAVaultFile_WithoutAGrantOnTheVaultLibrary_ButOnlyWithTheWatchFeature()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var context = VaultContext(shape.Account, session);
        var resources = CreateResourceService(context);

        Assert.Equal(CatalogueResourceAccess.Allowed, await resources.EvaluateAssetAsync(context, shape.Secret.AssetId, ApplicationPermissionIds.PlaybackRead));
        Assert.Equal(CatalogueResourceAccess.Allowed, await resources.EvaluateEntityAsync(context, "Work", shape.Secret.WorkId, ApplicationPermissionIds.PlaybackRead));
        Assert.Contains(shape.Secret.AssetId, await resources.GetAuthorizedAssetIdsForWorkAsync(context, shape.Secret.WorkId, VaultProfile, ApplicationPermissionIds.PlaybackRead));

        using var connection = _database.CreateConnection();
        connection.Execute("DELETE FROM account_feature_grants WHERE account_id=@account AND feature_id='watch';", new { account = shape.Account });
        Assert.Equal(CatalogueResourceAccess.NotFound, await CreateResourceService(context).EvaluateAssetAsync(context, shape.Secret.AssetId, ApplicationPermissionIds.PlaybackRead));
    }

    [Fact]
    public async Task Vault_AFileOnTheOrdinaryLibraryTheAccountHasNoGrantFor_StillAnswersForbidden()
    {
        // Only the Vault answers "not found" for lack of access; the ordinary rule for a library with no grant is unchanged.
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var other = await InsertOwnedWorkWithIdAsync(Guid.NewGuid(), "Not granted", "Movie");
        var context = VaultContext(shape.Account, NewVaultSession(shape.Account));

        Assert.Equal(CatalogueResourceAccess.Denied, await CreateResourceService(context).EvaluateAssetAsync(context, other.AssetId, ApplicationPermissionIds.PlaybackRead));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private sealed record VaultShape(
        Guid Account,
        Guid NormalLibrary,
        Guid VaultLibrary,
        (Guid WorkId, Guid AssetId) Normal,
        (Guid WorkId, Guid AssetId) Secret);

    private async Task<VaultShape> SeedVaultAsync(bool administrator, bool grantedTheVaultLibrary, IReadOnlySet<AccountFeatureId>? features = null)
    {
        var normalLibrary = Guid.NewGuid();
        var vaultLibrary = Guid.NewGuid();
        var normal = await InsertOwnedWorkWithIdAsync(normalLibrary, "Ordinary film", "Movie");
        var secret = await InsertOwnedWorkWithIdAsync(vaultLibrary, "Secret scene", "Movie");
        var account = Guid.NewGuid();
        var libraries = new HashSet<Guid> { normalLibrary };
        if (grantedTheVaultLibrary)
        {
            libraries.Add(vaultLibrary);
        }

        await CreateHumanAsync(account, features ?? EveryFeature, libraries, administrator);
        using var connection = _database.CreateConnection();
        var now = DateTimeOffset.UtcNow.ToString("O");
        // The profile used by these tests is an adult with no content limit, so it can hold NSFW access.
        connection.Execute("UPDATE profiles SET role='StandardUser', content_limit=NULL WHERE id=@profile;", new { profile = VaultProfile });
        connection.Execute(
            "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@library,'none',@now,@now,@now);",
            new { library = vaultLibrary.ToString("D"), now });
        return new VaultShape(account, normalLibrary, vaultLibrary, normal, secret);
    }

    /// <summary>A device session of the account on the seed profile, as the sign-in would have made it.</summary>
    private Guid NewVaultSession(Guid account)
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO auth_sessions(id,account_id,active_profile_id,token_hash,device_id,device_name,client,authentication_method,security_stamp,created_at,last_seen_at,expires_at)
            VALUES(@sessionId,@account,@profile,@hash,'device','Device','Dashboard','password','stamp',@now,@now,@later);
            """,
            new
            {
                sessionId,
                account,
                profile = VaultProfile,
                hash = Guid.NewGuid().ToString("N"),
                now = now.ToString("O"),
                later = now.AddDays(30).ToString("O"),
            });
        return sessionId;
    }

    /// <summary>Opens the seed profile up (Allowed) and switches NSFW mode on for one device session.</summary>
    private async Task UnlockVaultAsync(Guid session)
    {
        var nsfw = new NsfwAccessRepository(_database);
        await nsfw.SetAsync(VaultProfile, NsfwAccessMode.Allowed, null, null, DateTimeOffset.UtcNow);
        await nsfw.UnlockAsync(session, VaultProfile, NsfwUnlockMethod.Allowed, 15, DateTimeOffset.UtcNow);
    }

    private static DefaultHttpContext VaultContext(Guid account, Guid? session) =>
        new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(TuvimaClaimTypes.PrincipalKind, PrincipalKind.Human.ToString()),
                new Claim(TuvimaClaimTypes.AccountId, account.ToString("D")),
                new Claim(TuvimaClaimTypes.ActiveProfileId, VaultProfile.ToString("D")),
                .. session is { } id ? [new Claim(TuvimaClaimTypes.SessionId, id.ToString("D"))] : Array.Empty<Claim>(),
            ], "test")),
        };

    private ReaderBackedProjection RealReaders() =>
        new(new DisplayWorkProjectionReader(_database), new DisplayVisibleWorkReader(_database));

    private static async Task<object?> RunAssetFilterAsync(
        CatalogueAssetAccessFilter filter, IServiceProvider services, DefaultHttpContext user, Guid assetId)
    {
        var http = new DefaultHttpContext { User = user.User, RequestServices = services };
        http.Request.RouteValues["assetId"] = assetId.ToString("D");
        return await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ => ValueTask.FromResult<object?>("next"));
    }

    private sealed class FavoritesProjection(IReadOnlyList<DisplayWorkRow> works, IReadOnlySet<Guid> favorites) : IRawDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => Task.FromResult(works);
        public Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct) => LoadWorksAsync(ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => LoadJourneyAsync(profileId, lane, ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult(favorites);
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }
}
