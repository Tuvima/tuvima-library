using System.Security.Claims;
using System.Text.Json;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

public sealed class PasswordChangeRequiredMiddlewareTests
{
    [Theory]
    [InlineData(PasswordChangeRequiredMiddleware.ChangeTemporaryPasswordEndpoint)]
    [InlineData(PasswordChangeRequiredMiddleware.ValidateSessionEndpoint)]
    [InlineData(PasswordChangeRequiredMiddleware.RevokeSessionEndpoint)]
    public async Task TemporaryPasswordSession_MayChangePasswordCheckItselfAndSignOut(string endpointName)
    {
        var (status, reached, _) = await RunAsync(temporary: true, endpointName);

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Theory]
    [InlineData("GetAccounts")]
    [InlineData("SetAccountTemporaryPassword")]
    [InlineData(null)]
    public async Task TemporaryPasswordSession_IsRefusedEverywhereElse(string? endpointName)
    {
        var (status, reached, body) = await RunAsync(temporary: true, endpointName);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(TemporaryPasswordPolicy.PasswordChangeRequiredCode, json.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("GetAccounts")]
    [InlineData(null)]
    public async Task NormalSession_IsNotAffected(string? endpointName)
    {
        var (status, reached, _) = await RunAsync(temporary: false, endpointName);

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    private static async Task<(int Status, bool Reached, string Body)> RunAsync(bool temporary, string? endpointName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D")) };
        if (temporary)
        {
            claims.Add(new Claim(TuvimaClaimTypes.PasswordChangeRequired, "true"));
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        if (endpointName is not null)
        {
            context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new EndpointNameMetadata(endpointName)), endpointName));
        }

        var reached = false;
        var middleware = new PasswordChangeRequiredMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, reached, body);
    }
}
