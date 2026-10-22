using MediaEngine.Api.Services.Libraries;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A file excluded from a Vault library is not listed and cannot be added back from the library screens: the list names files and
/// folders, so the Vault is absent from it exactly as it would be if nothing had ever been excluded there (Videos spec D17, WP17 part C).
/// The test world's "old-rips" library stands in for the Vault.
/// </summary>
public sealed class ExcludedFileVaultTests : IDisposable
{
    private readonly ExcludedFileTestWorld _world = new();

    public void Dispose() => _world.Dispose();

    private ExcludedFileService VaultAware() => new(
        _world.Excluded, _world.Resolver, _world.Filter, _world.Intake, NullLogger<ExcludedFileService>.Instance, new FixedVaultLibraries("old-rips"));

    [Fact]
    public async Task TheList_LeavesOutEveryFileExcludedFromAVaultLibrary_ForAllLibrariesAndForThatOne()
    {
        _world.WriteFile(_world.AsIs, "secret/one.mkv");
        _world.WriteFile(_world.Managed, "Dune.mkv");
        var vault = await _world.ExcludeAsync("old-rips", "secret/one.mkv", DateTimeOffset.UtcNow, title: "Secret Title");
        var ordinary = await _world.ExcludeAsync("movies", "Dune.mkv", DateTimeOffset.UtcNow.AddMinutes(-1));
        var service = VaultAware();

        var all = await service.ListAsync(null);
        var one = await service.ListAsync("old-rips");

        Assert.Equal([ordinary.Id], all.Select(row => row.Id));
        Assert.Empty(one);
        Assert.DoesNotContain(all, row => row.Id == vault.Id);
    }

    [Fact]
    public async Task AddingBackAVaultFile_AnswersLikeAnUnknownId_AndChangesNothing()
    {
        _world.WriteFile(_world.AsIs, "secret/two.mkv");
        var vault = await _world.ExcludeAsync("old-rips", "secret/two.mkv", DateTimeOffset.UtcNow);
        var service = VaultAware();

        var vaultAnswer = await service.AddBackAsync(vault.Id);
        var missingAnswer = await service.AddBackAsync(Guid.NewGuid());

        Assert.Null(vaultAnswer);
        Assert.Null(missingAnswer);
        Assert.NotNull(await _world.Excluded.GetByIdAsync(vault.Id));
        Assert.Empty(_world.Intake.Requests);
        Assert.Equal(0, _world.Filter.Invalidations);
    }

    [Fact]
    public async Task AnOrdinaryFile_StillAddsBack_WhileAVaultLibraryExists()
    {
        _world.WriteFile(_world.Managed, "Dune.mkv");
        var ordinary = await _world.ExcludeAsync("movies", "Dune.mkv", DateTimeOffset.UtcNow);

        var result = await VaultAware().AddBackAsync(ordinary.Id);

        Assert.NotNull(result);
        Assert.Single(_world.Intake.Requests);
    }
}
