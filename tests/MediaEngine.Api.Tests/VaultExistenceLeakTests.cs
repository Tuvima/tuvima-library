using System.Reflection;
using System.Text.Json;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Routes outside the Vault area that could still show, to someone whose NSFW mode is off, that a Vault exists (Videos spec D17, D45).
/// Hand-written stand-ins for the data store; each route is called through its real handler.
/// </summary>
public sealed class VaultExistenceLeakTests : IAsyncLifetime
{
    private static readonly Guid Account = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Profile = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Session = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private const string VaultLibraryId = "aaaaaaaa-0000-4000-8000-000000000001";

    private WebApplication? _app;

    public Task InitializeAsync()
    {
        var build = typeof(MappedEndpointInventoryTests).GetMethod("BuildEndpointOnlyApplication", BindingFlags.NonPublic | BindingFlags.Static)!;
        _app = (WebApplication)build.Invoke(null, null)!;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private static RequestAuthority Person() => new(
        PrincipalKind.Human, true, Account, Profile, null, Session, null, AccountEnabled: true, GrantEnabled: true);

    private RouteEndpoint Route(string name) =>
        ((IEndpointRouteBuilder)_app!).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);

    private static VaultAccessResolver Access(bool unlocked) => new(
        HandlerProxy.Create<IVaultLibraryRepository>(new()
        {
            ["GetIdsAsync"] = _ => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { VaultLibraryId }),
        }),
        HandlerProxy.Create<INsfwAccessRepository>(new()
        {
            ["IsUnlockedAsync"] = _ => Task.FromResult(unlocked),
        }),
        TimeProvider.System);

    private static ServiceCollection BaseServices(bool unlocked, LibrariesConfiguration libraries)
    {
        var authority = Person();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(HandlerProxy.Create<IConfigurationLoader>(new() { ["LoadLibraries"] = _ => libraries }));
        services.AddSingleton(HandlerProxy.Create<IVaultLibraryRepository>(new()
        {
            ["GetIdsAsync"] = _ => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { VaultLibraryId }),
        }));
        services.AddSingleton(Access(unlocked));
        services.AddSingleton(HandlerProxy.Create<IRequestAuthorityResolver>(new()
        {
            ["ResolveAsync"] = _ => ValueTask.FromResult(authority),
        }));
        return services;
    }

    private static async Task<(int Status, string Body)> CallAsync(RouteEndpoint route, IServiceProvider services, string query = "")
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "GET";
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();
        await route.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static LibrariesConfiguration TwoLibraries() => new()
    {
        Libraries =
        [
            new LibraryFolderConfig { Id = "bbbbbbbb-0000-4000-8000-000000000001", Name = "Movies", Area = "watch", Sources = [new LibrarySourceConfig { Id = "m", Path = Path.GetTempPath() }] },
            new LibraryFolderConfig { Id = VaultLibraryId, Name = "Secret shelf", Area = "watch", Sources = [new LibrarySourceConfig { Id = "v", Path = Path.GetTempPath() }] },
        ],
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task LibraryCapacity_NamesTheVaultLibrary_OnlyInsideNsfwMode(bool unlocked, bool vaultShown)
    {
        var authority = Person();
        var services = BaseServices(unlocked, TwoLibraries());
        services.AddSingleton(HandlerProxy.Create<IViewRequestProfileContext>(new()
        {
            ["ResolveAuthorityAsync"] = _ => ValueTask.FromResult(authority),
        }));
        services.AddSingleton(HandlerProxy.Create<IAccountAccessDecisionService>(new()
        {
            ["EvaluateFeatureAsync"] = _ => ValueTask.FromResult(AuthorizationDecision.Allow()),
            ["EvaluateLibraryAsync"] = _ => ValueTask.FromResult(AuthorizationDecision.Allow()),
        }));

        var (status, body) = await CallAsync(Route("GetLibraryCapacity"), services.BuildServiceProvider(), "?area=watch");

        Assert.Equal(StatusCodes.Status200OK, status);
        var labels = JsonDocument.Parse(body).RootElement.GetProperty("locations").EnumerateArray()
            .Select(location => location.GetProperty("label").GetString()).ToList();
        Assert.Equal(vaultShown, labels.Any(label => label!.Contains("Secret shelf", StringComparison.Ordinal)));
        Assert.Contains(labels, label => label!.Contains("Movies", StringComparison.Ordinal));
    }
}
