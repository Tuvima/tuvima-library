using System.Net;
using System.Text.RegularExpressions;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace MediaEngine.Web.Tests;

public sealed class DashboardLoginAntiforgeryTests
{
    private const string CookieName = "Tuvima.Test.Antiforgery";

    [Fact]
    public async Task CurrentKeyAndMatchingTokens_AllowTheSubmission()
    {
        using var services = Services();
        var tokens = Tokens(services);
        var request = Request(services, tokens.CookieToken, tokens.RequestToken);

        Assert.Null(await DashboardAuthenticationEndpoints.RefreshInvalidLoginFormAsync(
            request, services.GetRequiredService<IAntiforgery>(), []));
        Assert.Equal(0, request.Response.Headers.SetCookie.Count);
    }

    [Fact]
    public async Task MissingKey_RejectsOldSubmissionAndProvidesUsableFreshForm()
    {
        using var previousLibrary = Services();
        using var currentLibrary = Services();
        var oldTokens = Tokens(previousLibrary);
        var request = Request(currentLibrary, oldTokens.CookieToken, oldTokens.RequestToken);

        var rejection = await DashboardAuthenticationEndpoints.RefreshInvalidLoginFormAsync(
            request, currentLibrary.GetRequiredService<IAntiforgery>(), []);
        Assert.NotNull(rejection);
        await rejection.ExecuteAsync(request);

        Assert.Equal(StatusCodes.Status400BadRequest, request.Response.StatusCode);
        request.Response.Body.Position = 0;
        var html = await new StreamReader(request.Response.Body).ReadToEndAsync();
        Assert.Contains("This sign-in form expired", html);
        Assert.Contains("value=\"/watch\"", html);
        Assert.DoesNotContain("submitted-password", html);
        Assert.DoesNotContain("person@example.test", html);
        Assert.DoesNotContain("CryptographicException", html);

        var cookie = request.Response.Headers.SetCookie.Single(value => value!.StartsWith(CookieName + "="))!;
        var freshCookie = cookie.Split(';')[0][(CookieName.Length + 1)..];
        var freshToken = WebUtility.HtmlDecode(Regex.Match(html,
            "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(freshToken);
        var resubmission = Request(currentLibrary, freshCookie, freshToken);
        Assert.Null(await DashboardAuthenticationEndpoints.RefreshInvalidLoginFormAsync(
            resubmission, currentLibrary.GetRequiredService<IAntiforgery>(), []));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid-token")]
    public async Task MissingOrTamperedToken_IsRejectedAndCannotCarryAnExternalReturnUrl(string? submittedToken)
    {
        using var services = Services();
        var tokens = Tokens(services);
        var request = Request(services, tokens.CookieToken, submittedToken, "https://outside.example/");

        var rejection = await DashboardAuthenticationEndpoints.RefreshInvalidLoginFormAsync(
            request, services.GetRequiredService<IAntiforgery>(), []);
        Assert.NotNull(rejection);
        await rejection.ExecuteAsync(request);
        Assert.Equal(StatusCodes.Status400BadRequest, request.Response.StatusCode);
        request.Response.Body.Position = 0;
        var html = await new StreamReader(request.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain("https://outside.example/", html);
        Assert.Contains("name=\"returnUrl\" value=\"/\"", html);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAntiforgery(options => options.Cookie.Name = CookieName);
        return services.BuildServiceProvider();
    }

    private static AntiforgeryTokenSet Tokens(ServiceProvider services) =>
        services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(
            new DefaultHttpContext { RequestServices = services });

    private static DefaultHttpContext Request(ServiceProvider services, string? cookie, string? token,
        string returnUrl = "/watch")
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"{CookieName}={cookie}";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["__RequestVerificationToken"] = token,
            ["returnUrl"] = returnUrl,
            ["email"] = "person@example.test",
            ["password"] = "submitted-password",
        });
        context.Response.Body = new MemoryStream();
        return context;
    }
}
