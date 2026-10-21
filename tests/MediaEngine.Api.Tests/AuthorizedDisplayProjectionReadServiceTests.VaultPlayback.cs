using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

// Watching the Vault keeps NSFW mode awake from the Engine's side and belongs to one device session (Videos spec D33, D36).
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
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
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
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
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
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
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
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
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
}
