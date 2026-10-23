using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

// Watching the Vault keeps Vault mode awake from the Engine's side and belongs to one device session (Videos spec D33, D36).
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private static readonly TimeSpan SoonToLock = TimeSpan.FromMinutes(14);

    /// <summary>Unlocks a session as if it had been unlocked 14 minutes ago with a 15 minute idle limit: one minute from locking.</summary>
    private async Task UnlockNearlyIdleAsync(Guid session)
    {
        var nsfw = new NsfwAccessRepository(_database);
        await nsfw.SetAsync(VaultProfile, NsfwAccessMode.Allowed, 15, null, DateTimeOffset.UtcNow - SoonToLock);
        await nsfw.UnlockAsync(session, VaultProfile, NsfwUnlockMethod.Allowed, 15, DateTimeOffset.UtcNow - SoonToLock);
    }

    private Task<bool> StillUnlockedInAsync(Guid session, TimeSpan from) =>
        new NsfwAccessRepository(_database).IsUnlockedAsync(session, VaultProfile, DateTimeOffset.UtcNow + from);

    [Fact]
    public async Task Vault_PlayingAVaultFile_KeepsTheUnlockedSessionAwake_SoTheIdleLockNeverInterruptsIt()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockNearlyIdleAsync(session);
        Assert.False(await StillUnlockedInAsync(session, TimeSpan.FromMinutes(10)));

        // A stream range, a heartbeat or a cover for the Vault file is real use.
        var context = VaultContext(shape.Account, session);
        Assert.Equal(CatalogueResourceAccess.Allowed, await CreateResourceService(context).EvaluateAssetAsync(
            context, shape.Secret.AssetId, ApplicationPermissionIds.PlaybackRead));

        Assert.True(await StillUnlockedInAsync(session, TimeSpan.FromMinutes(10)));
        Assert.False(await StillUnlockedInAsync(session, TimeSpan.FromMinutes(20)));
    }

    [Fact]
    public async Task Vault_UsingAnOrdinaryFile_DoesNotKeepNsfwModeAwake()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockNearlyIdleAsync(session);

        var context = VaultContext(shape.Account, session);
        Assert.Equal(CatalogueResourceAccess.Allowed, await CreateResourceService(context).EvaluateAssetAsync(
            context, shape.Normal.AssetId, ApplicationPermissionIds.PlaybackRead));

        Assert.False(await StillUnlockedInAsync(session, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public async Task Vault_ASessionThatHasAlreadyLocked_IsNotWokenByAskingForAVaultFile_AndGetsNotFound()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        var nsfw = new NsfwAccessRepository(_database);
        await nsfw.SetAsync(VaultProfile, NsfwAccessMode.Allowed, 15, null, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30));
        await nsfw.UnlockAsync(session, VaultProfile, NsfwUnlockMethod.Allowed, 15, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30));

        var context = VaultContext(shape.Account, session);
        Assert.Equal(CatalogueResourceAccess.NotFound, await CreateResourceService(context).EvaluateAssetAsync(
            context, shape.Secret.AssetId, ApplicationPermissionIds.PlaybackRead));

        Assert.False(await StillUnlockedInAsync(session, TimeSpan.Zero));
    }

    [Fact]
    public async Task Vault_NsfwModeBelongsToOneDeviceSession_AnotherSessionOfTheSameProfileStaysLocked()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var phone = NewVaultSession(shape.Account);
        var television = NewVaultSession(shape.Account);
        await UnlockVaultAsync(phone);

        var phoneContext = VaultContext(shape.Account, phone);
        var televisionContext = VaultContext(shape.Account, television);

        Assert.Equal(CatalogueResourceAccess.Allowed, await CreateResourceService(phoneContext).EvaluateAssetAsync(
            phoneContext, shape.Secret.AssetId, ApplicationPermissionIds.PlaybackRead));
        Assert.Equal(CatalogueResourceAccess.NotFound, await CreateResourceService(televisionContext).EvaluateAssetAsync(
            televisionContext, shape.Secret.AssetId, ApplicationPermissionIds.PlaybackRead));

        // Locking the phone locks only the phone; nothing the television does wakes it.
        await new NsfwAccessRepository(_database).LockSessionAsync(phone);
        var phoneAgain = VaultContext(shape.Account, phone);
        Assert.Equal(CatalogueResourceAccess.NotFound, await CreateResourceService(phoneAgain).EvaluateAssetAsync(
            phoneAgain, shape.Secret.AssetId, ApplicationPermissionIds.PlaybackRead));
    }

    // ── Adaptive-stream segments (the "keep awake on use" gap, Videos spec D33) ─────────────────

    private static RequestAuthority HlsGrantAuthority(Guid account, Guid session) =>
        new(PrincipalKind.Human, true, account, VaultProfile, SessionId: session, AccountEnabled: true, GrantEnabled: true);

    [Fact]
    public async Task Vault_FetchingASegmentOfAVaultVideo_KeepsTheUnlockedSessionAwake()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockNearlyIdleAsync(session);
        Assert.False(await StillUnlockedInAsync(session, TimeSpan.FromMinutes(10)));

        // The segment route authorizes with the authority stored in its grant, not with a request of its own.
        await CreateResourceService(VaultContext(shape.Account, session))
            .KeepVaultSessionAwakeAsync(HlsGrantAuthority(shape.Account, session), shape.Secret.AssetId);

        Assert.True(await StillUnlockedInAsync(session, TimeSpan.FromMinutes(10)));
        Assert.False(await StillUnlockedInAsync(session, TimeSpan.FromMinutes(20)));
    }

    [Fact]
    public async Task Vault_FetchingASegmentOfAnOrdinaryVideo_DoesNotKeepNsfwModeAwake()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockNearlyIdleAsync(session);

        await CreateResourceService(VaultContext(shape.Account, session))
            .KeepVaultSessionAwakeAsync(HlsGrantAuthority(shape.Account, session), shape.Normal.AssetId);

        Assert.False(await StillUnlockedInAsync(session, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public async Task Vault_ASegmentGrantForAPairedAppOrAnAnonymousCaller_NeverWakesNsfwMode()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockNearlyIdleAsync(session);
        var resources = CreateResourceService(VaultContext(shape.Account, session));

        // A paired app is never the person's own Dashboard session, even carrying the same session id.
        await resources.KeepVaultSessionAwakeAsync(
            new RequestAuthority(PrincipalKind.DelegatedUserClient, true, shape.Account, VaultProfile, Guid.NewGuid(), session, Guid.NewGuid(),
                AccountEnabled: true, GrantEnabled: true, ApplicationEnabled: true),
            shape.Secret.AssetId);
        await resources.KeepVaultSessionAwakeAsync(new RequestAuthority(PrincipalKind.Anonymous, false), shape.Secret.AssetId);

        Assert.False(await StillUnlockedInAsync(session, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void TheSegmentRoute_AsksTheCatalogueToKeepAVaultSessionAwake_BeforeItServesTheResource()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src/MediaEngine.Api/Endpoints/HlsStreamEndpoints.cs"));

        var keepAwake = source.IndexOf("KeepVaultSessionAwakeAsync", StringComparison.Ordinal);
        Assert.True(keepAwake > 0, "The segment route must restart the idle timer of a Vault video's session.");
        Assert.True(keepAwake < source.IndexOf("OpenResourceAsync", StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
