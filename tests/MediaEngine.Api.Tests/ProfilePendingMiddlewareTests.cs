using System.Security.Claims;
using System.Text.Json;
using MediaEngine.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

/// <summary>
/// From sign-in until a profile of the household is chosen (users, profiles and households spec D3) the session can do only what the
/// profile choice needs: check itself, list the household's profiles and their pictures, switch, and sign out. Everything else is
/// refused with 403 <c>profile_required</c>.
/// </summary>
public sealed class ProfilePendingMiddlewareTests
{
    [Theory]
    [InlineData(PasswordChangeRequiredMiddleware.ValidateSessionEndpoint)]
    [InlineData(PasswordChangeRequiredMiddleware.RevokeSessionEndpoint)]
    [InlineData(ProfilePendingMiddleware.SwitchProfileEndpoint)]
    [InlineData(ProfilePendingMiddleware.ListProfilesEndpoint)]
    [InlineData(ProfilePendingMiddleware.ProfileAvatarEndpoint)]
    public async Task APendingSession_MayOnlyCheckItself_ListTheHouseholdsProfiles_SwitchAndSignOut(string endpointName)
    {
        var (status, reached, _) = await RunAsync(pending: true, endpointName);

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Theory]
    [InlineData("GetAccounts")]
    [InlineData("ListLibraries")]
    [InlineData("GetProfile")]
    [InlineData("UpdateProfileExperience")]
    [InlineData("SetProfilePin")]
    [InlineData("ExportMyData")]
    [InlineData("ChangeTemporaryPassword")]
    [InlineData(null)]
    public async Task APendingSession_IsRefusedEverywhereElse_AndTheEndpointNeverRuns(string? endpointName)
    {
        var (status, reached, body) = await RunAsync(pending: true, endpointName);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(ProfilePendingMiddleware.ProfileRequiredCode, json.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("GetAccounts")]
    [InlineData(null)]
    public async Task ASessionWithAChosenProfile_IsNotAffected(string? endpointName)
    {
        var (status, reached, _) = await RunAsync(pending: false, endpointName);

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    private static async Task<(int Status, bool Reached, string Body)> RunAsync(bool pending, string? endpointName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D")) };
        if (pending)
        {
            claims.Add(new Claim(TuvimaClaimTypes.ProfilePending, "true"));
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        if (endpointName is not null)
        {
            context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new EndpointNameMetadata(endpointName)), endpointName));
        }

        var reached = false;
        var middleware = new ProfilePendingMiddleware(_ =>
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
