using System.Net;
using System.Net.Http.Json;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Vault;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Vault mode and Vault library calls tell a refusal apart (not found, PIN needed or wrong, too many tries, a plain reason) through the
/// shared envelope helpers, send PINs only in request bodies and never write one to a log.
/// </summary>
public sealed class EngineApiClientVaultTests
{
    private const string Pin = "2468135";

    private sealed record Seen(HttpMethod Method, string Path, string Body);

    private static (EngineApiClient Client, List<Seen> Requests, CapturingLoggerProvider Logs) Client(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var requests = new List<Seen>();
        var logs = new CapturingLoggerProvider();
        var http = new HttpClient(new FuncHandler(request =>
        {
            requests.Add(new Seen(request.Method, request.RequestUri!.AbsolutePath, request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty));
            return answer(request);
        }))
        { BaseAddress = new Uri("http://engine.test") };
        var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        return (new EngineApiClient(http, factory.CreateLogger<EngineApiClient>(), loggerFactory: factory), requests, logs);
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string title, string detail) =>
        new(status) { Content = JsonContent.Create(new { title, detail, status = (int)status }) };

    [Fact]
    public async Task Status_IsNotAvailable_WhenTheEngineAnswersNotFound_OrAnswersSomethingThatIsNotAMode()
    {
        var (notFound, _, _) = Client(_ => Problem(HttpStatusCode.NotFound, "Not found.", "Not found."));
        Assert.Equal(VaultOutcome.NotAvailable, (await notFound.GetNsfwStatusAsync()).Outcome);

        var (empty, _, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) });
        Assert.Equal(VaultOutcome.NotAvailable, (await empty.GetNsfwStatusAsync()).Outcome);

        var (ok, requests, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new NsfwStatusDto { Mode = "pin", Unlocked = false, HasPin = true, IdleLockMinutes = 15 }),
        });
        var status = await ok.GetNsfwStatusAsync();
        Assert.True(status.Succeeded);
        Assert.Equal("pin", status.Value!.Mode);
        Assert.Equal("/api/v1/nsfw/status", Assert.Single(requests).Path);
    }

    [Fact]
    public async Task Unlock_SendsThePinInTheBody_TellsAWrongPinFromTooManyTries_AndNeverLogsThePin()
    {
        var answer = HttpStatusCode.PreconditionRequired;
        var (client, requests, logs) = Client(_ => answer == HttpStatusCode.PreconditionRequired
            ? Problem(answer, "PIN required", "Enter the PIN to continue.")
            : Problem(answer, "Too many attempts", "Too many attempts. Try again later."));

        var wrong = await client.UnlockNsfwAsync(Pin);
        Assert.Equal(VaultOutcome.PinRequired, wrong.Outcome);
        Assert.Equal(VaultMessages.WrongPin, VaultMessages.For(wrong));

        answer = HttpStatusCode.TooManyRequests;
        var paused = await client.UnlockNsfwAsync(Pin);
        Assert.Equal(VaultOutcome.TooManyAttempts, paused.Outcome);

        Assert.All(requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v1/nsfw/unlock", request.Path);
            Assert.Contains(Pin, request.Body);
        });
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(Pin, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task AdministratorCalls_TellAWrongPinWhenTheEngineAsksForOne()
    {
        var (wrong, _, _) = Client(_ => Problem(HttpStatusCode.PreconditionRequired, "PIN required", "Enter the PIN to continue."));
        Assert.Equal(VaultOutcome.PinRequired, (await wrong.SetProfileNsfwAccessAsync(Guid.NewGuid(), new SetNsfwAccessRequest { Mode = "pin", AdminPin = Pin })).Outcome);
    }

    [Fact]
    public async Task SetNavigation_PutsTheChoiceInTheBody_AndAnUnavailableProfileIsNotAvailable()
    {
        var (client, requests, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"mode\":\"allowed\",\"unlocked\":false,\"has_pin\":false,\"idle_lock_minutes\":30,\"show_in_navigation\":true}", System.Text.Encoding.UTF8, "application/json"),
        });

        var result = await client.SetNsfwNavigationAsync(true);

        Assert.True(result.Succeeded);
        Assert.True(result.Value!.ShowInNavigation);
        var sent = Assert.Single(requests);
        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal("/api/v1/nsfw/navigation", sent.Path);
        Assert.Contains("\"show_in_navigation\":true", sent.Body);

        var (refused, _, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Equal(VaultOutcome.NotAvailable, (await refused.SetNsfwNavigationAsync(true)).Outcome);
    }

    [Fact]
    public async Task SetIdle_SendsTheChosenMinutesInTheBody_AndPassesOnThePlainReason()
    {
        var (client, requests, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"mode\":\"allowed\",\"unlocked\":true,\"has_pin\":false,\"idle_lock_minutes\":30}", System.Text.Encoding.UTF8, "application/json"),
        });

        var result = await client.SetNsfwIdleAsync(30);

        Assert.True(result.Succeeded);
        Assert.Equal(30, result.Value!.IdleLockMinutes);
        var sent = Assert.Single(requests);
        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal("/api/v1/nsfw/idle", sent.Path);
        Assert.Contains("\"idle_lock_minutes\":30", sent.Body);

        var (refused, _, _) = Client(_ => Problem(HttpStatusCode.BadRequest, "Invalid request.", "Choose 5, 15, 30 or 60 minutes, or Never."));
        var rejected = await refused.SetNsfwIdleAsync(45);
        Assert.Equal(VaultOutcome.Rejected, rejected.Outcome);
        Assert.Equal("Choose 5, 15, 30 or 60 minutes, or Never.", VaultMessages.For(rejected));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, VaultOutcome.NotAvailable)]
    [InlineData(HttpStatusCode.PreconditionRequired, VaultOutcome.PinRequired)]
    [InlineData(HttpStatusCode.TooManyRequests, VaultOutcome.TooManyAttempts)]
    [InlineData(HttpStatusCode.BadRequest, VaultOutcome.Rejected)]
    [InlineData(HttpStatusCode.Conflict, VaultOutcome.Rejected)]
    public async Task AttachFolder_SendsTheFolderAndPinOnlyInTheBody_AndTellsEachRefusalApart(HttpStatusCode status, VaultOutcome expected)
    {
        const string folder = "/media/secret-folder";
        var (client, requests, logs) = Client(_ => Problem(status, status == HttpStatusCode.Conflict ? "Conflict." : "Invalid request.", "That folder wasn't found on the server, so nothing was added."));

        var call = await client.AttachVaultFolderAsync("v1", new AttachVaultFolderRequest { FolderPath = folder, AdminPin = Pin });

        Assert.Equal(expected, call.Outcome);
        var sent = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/api/v1/vault/libraries/v1/folders", sent.Path);
        Assert.DoesNotContain("secret-folder", sent.Path, StringComparison.Ordinal);
        Assert.Contains(folder, sent.Body, StringComparison.Ordinal);
        Assert.Contains($"\"admin_pin\":\"{Pin}\"", sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(Pin, StringComparison.Ordinal) || entry.Message.Contains("secret-folder", StringComparison.Ordinal));
        if (expected == VaultOutcome.Rejected)
        {
            Assert.Equal("That folder wasn't found on the server, so nothing was added.", VaultMessages.For(call));
        }
    }

    [Fact]
    public async Task AttachFolder_Succeeds_WhenTheEngineReturnsTheLibrary()
    {
        var (client, _, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new VaultLibraryDto { Id = "v1", LookupSource = "none" }) });

        var call = await client.AttachVaultFolderAsync("v1", new AttachVaultFolderRequest { FolderPath = "/media/adult", AdminPin = Pin });

        Assert.True(call.Succeeded);
        Assert.Equal("v1", call.Value!.Id);
    }

    [Fact]
    public async Task TheLibraryCalls_UseTheEnginesRoutes()
    {
        var (client, requests, _) = Client(request => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new VaultLibraryDto { Id = "v1", LookupSource = "none" } }) }
            : request.RequestUri!.AbsolutePath.EndsWith("/remove", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new VaultLibraryDto { Id = "v1", LookupSource = "stash_box" }) });

        var list = await client.GetVaultLibrariesAsync();
        var update = await client.UpdateVaultLibraryAsync("v1", new UpdateVaultLibraryRequest { LookupSource = "stash_box", AdminPin = Pin });
        var removed = await client.RemoveVaultLibraryAsync("v1", new RemoveVaultLibraryRequest { AdminPin = Pin });

        Assert.Equal("v1", Assert.Single(list.Value!).Id);
        Assert.Equal("stash_box", update.Value!.LookupSource);
        Assert.True(removed.Succeeded);
        Assert.Equal(
            ["GET /api/v1/vault/libraries", "PUT /api/v1/vault/libraries/v1", "POST /api/v1/vault/libraries/v1/remove"],
            requests.Select(request => $"{request.Method} {request.Path}"));
    }

    [Fact]
    public async Task AnUnreachableEngine_IsAFailure_NotASuccess()
    {
        var http = new HttpClient(new FuncHandler(_ => throw new HttpRequestException("down"))) { BaseAddress = new Uri("http://engine.test") };
        var client = new EngineApiClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<EngineApiClient>.Instance);

        Assert.Equal(VaultOutcome.Failed, (await client.UnlockNsfwAsync(Pin)).Outcome);
        Assert.Equal(VaultOutcome.Failed, (await client.LockNsfwAsync()).Outcome);
        Assert.Equal(VaultOutcome.Failed, default(VaultCall<bool>).Outcome);
    }

    private sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
