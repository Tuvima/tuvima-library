using System.Text.RegularExpressions;
using MediaEngine.Api.DevSupport;
using MediaEngine.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Development builds expose actions that reset, wipe, seed or list the whole library. Each one requires an
/// administrator, not just any signed-in user, and every development group is created through one helper.
/// </summary>
public sealed class DevelopmentRouteAccessGuardrailTests
{
    [Fact]
    public void DevelopmentGroup_RequiresAnUnlockedAdministrator_ForEveryAction()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        var app = builder.Build();

        var group = app.MapDevelopmentGroup();
        group.MapPost("/factory-reset", () => "ok");
        group.MapGet("/library-items", () => "ok");

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        Assert.Equal(2, endpoints.Count);
        Assert.All(endpoints, endpoint =>
        {
            Assert.StartsWith("/dev/", endpoint.RoutePattern.RawText, StringComparison.Ordinal);
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.Equal(AuthPolicies.Administrator, Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
        });
    }

    [Fact]
    public void DevelopmentActions_AreGroupedOnlyThroughTheAdministratorHelper()
    {
        var root = FindRepoRoot();
        var devSupport = Path.Combine(root, "src", "MediaEngine.Api", "DevSupport");
        var helper = Path.Combine(devSupport, "DevelopmentRouteGroup.cs");
        var rawDevGroup = new Regex("""MapGroup\(\s*"/dev(/|")""", RegexOptions.CultureInvariant);

        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "MediaEngine.Api"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !string.Equals(file, helper, StringComparison.OrdinalIgnoreCase))
            .Where(file => rawDevGroup.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(root, file))
            .ToList();

        Assert.Empty(offenders);
        Assert.Contains("MapDevelopmentGroup()", File.ReadAllText(Path.Combine(devSupport, "DevSeedEndpoints.cs")), StringComparison.Ordinal);
        Assert.Contains("MapDevelopmentGroup()", File.ReadAllText(Path.Combine(devSupport, "IntegrationTestEndpoints.cs")), StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
