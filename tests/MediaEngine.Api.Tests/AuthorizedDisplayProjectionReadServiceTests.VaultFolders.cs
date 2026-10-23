using MediaEngine.Api.Endpoints;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

// Adding a folder to the Vault (Videos spec D45): the route answers "not found" to anyone who is not an unlocked administrator,
// before the service (and so the PIN check and the folder) is reached. The service itself is covered by VaultFolderAttachmentServiceTests.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private const string AttachFolderBody = "{\"folder_path\":\"/media/adult\",\"admin_pin\":\"2468\"}";

    [Fact]
    public async Task VaultFolders_AnAdministratorWhoHasNotUnlocked_GetsNotFound_AndTheServiceIsNeverReached()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());

        var result = await InvokeAsync(routes["AttachVaultFolder"], shape.Account, NewVaultSession(shape.Account), ("libraryId", shape.VaultLibrary.ToString("D")), AttachFolderBody);

        Assert.Equal(StatusCodes.Status404NotFound, result.Status);
        Assert.DoesNotContain("adult", result.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VaultFolders_AnUnlockedPersonWhoIsNotAnAdministrator_GetsNotFound_AndTheServiceIsNeverReached()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints());

        var result = await InvokeAsync(routes["AttachVaultFolder"], shape.Account, session, ("libraryId", shape.VaultLibrary.ToString("D")), AttachFolderBody);

        Assert.Equal(StatusCodes.Status404NotFound, result.Status);
    }
}
