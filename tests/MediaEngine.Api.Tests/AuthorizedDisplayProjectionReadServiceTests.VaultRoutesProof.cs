using System.Reflection;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

// WP17 part C: the media editor routes that name an item and the library lists for account grants and setup answer for a Vault item
// exactly as they answer for one that does not exist.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private static readonly string[] MediaEditorRouteNames =
    [
        "GetMediaEditorWorkVersions", "GetMediaEditorNavigator", "SearchMediaEditorOwnedChildren",
        "SnapshotMediaEditorOwnedChildSelection", "PreviewMediaEditorMembershipChange",
    ];

    private static IReadOnlyList<RouteEndpoint> EveryMappedRoute()
    {
        var build = typeof(MappedEndpointInventoryTests).GetMethod("BuildEndpointOnlyApplication", BindingFlags.NonPublic | BindingFlags.Static)!;
        var app = (WebApplication)build.Invoke(null, null)!;
        _ingestionApps.Add(app);
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    private static IReadOnlyDictionary<string, RouteEndpoint> MapEverything()
    {
        return EveryMappedRoute()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>() is not null)
            .GroupBy(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName!)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    [Fact]
    public void VaultRoutes_EveryMediaEditorRouteThatNamesAnItem_IsClearedThroughTheCatalogue()
    {
        var routes = MapEverything();

        foreach (var name in MediaEditorRouteNames)
        {
            Assert.True(routes.TryGetValue(name, out var route), name);
            Assert.NotEmpty(route!.Metadata.OfType<CatalogueAnyEntityAccessMetadata>());
        }
    }

    [Fact]
    public async Task VaultRoutes_TheMediaEditorFilter_AnswersAVaultItemExactlyLikeAMissingOne_ForEveryIdKind()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var context = VaultContext(shape.Account, NewVaultSession(shape.Account));
        var provider = new ServiceCollection().AddSingleton(CreateResourceService(context)).BuildServiceProvider();
        var filter = new CatalogueAnyEntityAccessFilter(ApplicationPermissionIds.MetadataRead, "entityId");

        foreach (var id in new[] { Guid.NewGuid(), shape.Secret.AssetId, shape.Secret.WorkId })
        {
            var http = new DefaultHttpContext { User = context.User, RequestServices = provider };
            http.Request.RouteValues["entityId"] = id.ToString("D");
            var nextCalled = false;

            var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ =>
            {
                nextCalled = true;
                return ValueTask.FromResult<object?>("next");
            });

            Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
            Assert.False(nextCalled);
        }
    }

    [Fact]
    public async Task VaultRoutes_TheAccountGrantLibraryList_NeverOffersTheVault()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var all = EveryMappedRoute();
        var route = all.SingleOrDefault(endpoint =>
            endpoint.RoutePattern.RawText!.TrimEnd('/').EndsWith("/access/libraries", StringComparison.OrdinalIgnoreCase)
            && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
        Assert.True(route is not null, string.Join(", ", all.Select(endpoint => endpoint.RoutePattern.RawText).Where(text => text!.Contains("librar", StringComparison.OrdinalIgnoreCase))));
        var loader = new SavingLibrariesLoader(new LibrariesConfiguration
        {
            Libraries =
            [
                new LibraryFolderConfig { Id = shape.NormalLibrary.ToString("D"), Name = "Movies" },
                new LibraryFolderConfig { Id = shape.VaultLibrary.ToString("D"), Name = VaultFolderAttachmentService.LibraryName },
            ],
        });
        var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton<IRequestAuthorityResolver>(new RequestAuthorityResolver(_accounts, _applications))
            .AddSingleton<IAccountRepository>(_accounts)
            .AddSingleton<IProfileAccessRepository>(new ProfileAccessRepository(_database))
            .AddSingleton<IConfigurationLoader>(loader)
            .AddSingleton<IVaultLibraryRepository>(new VaultLibraryRepository(_database))
            .BuildServiceProvider();
        var http = VaultContext(shape.Account, NewVaultSession(shape.Account));
        http.RequestServices = services;
        http.Response.Body = new MemoryStream();

        await route!.RequestDelegate!(http);
        http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync();

        Assert.Contains(shape.NormalLibrary.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(shape.VaultLibrary.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vault", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VaultRoutes_TheLibrarySettingsHelpers_LeaveTheVaultOutOfEveryList_AndKeepItOnSave()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var vaultId = shape.VaultLibrary.ToString("D");
        var stored = new LibrariesConfiguration
        {
            Libraries =
            [
                new LibraryFolderConfig { Id = shape.NormalLibrary.ToString("D"), Name = "Movies" },
                new LibraryFolderConfig { Id = vaultId, Name = VaultFolderAttachmentService.LibraryName },
            ],
        };
        var ids = await new VaultLibraryRepository(_database).GetIdsAsync();

        var shown = VaultLibrarySettings.WithoutVault(stored, ids);
        var asContract = SettingsContractMapper.ToContract(shown);

        Assert.Equal([shape.NormalLibrary.ToString("D")], shown.Libraries.Select(library => library.Id));
        Assert.DoesNotContain(asContract.Libraries, library => string.Equals(library.Id, vaultId, StringComparison.OrdinalIgnoreCase));

        // A save built from what the screen showed keeps the Vault's entry.
        var saved = new LibrariesConfiguration { Libraries = [.. shown.Libraries] };
        VaultLibrarySettings.KeepVault(stored, saved, ids);
        Assert.Contains(saved.Libraries, library => string.Equals(library.Id, vaultId, StringComparison.OrdinalIgnoreCase));
    }
}
