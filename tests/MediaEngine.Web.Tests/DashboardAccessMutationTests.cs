using System.Net;
using System.Net.Http.Json;
using System.Text;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

public sealed class DashboardAccessMutationTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, DashboardAccessMutationFailure.Validation)]
    [InlineData(HttpStatusCode.Unauthorized, DashboardAccessMutationFailure.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, DashboardAccessMutationFailure.Forbidden)]
    [InlineData(HttpStatusCode.Conflict, DashboardAccessMutationFailure.Conflict)]
    [InlineData(HttpStatusCode.NotFound, DashboardAccessMutationFailure.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable, DashboardAccessMutationFailure.Transient)]
    public async Task AccountMutation_PreservesExpectedFailureClass(HttpStatusCode status, DashboardAccessMutationFailure expected)
    {
        var client = Client(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("""{"errors":{"pin":["secret-pin"],"email":["invalid"]}}""", Encoding.UTF8, "application/problem+json"),
        });

        var result = await client.CreateManagedAccountResultAsync(CreateAccountRequest());

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Failure);
        Assert.Equal(status, result.StatusCode);
        if (expected == DashboardAccessMutationFailure.Validation)
        {
            Assert.Equal(["pin", "email"], result.ValidationFields);
            Assert.DoesNotContain("secret-pin", string.Join(' ', result.ValidationFields!));
        }
        else
        {
            Assert.Empty(result.ValidationFields!);
        }
    }

    [Fact]
    public async Task ApplicationCredentialMutation_KeepsOneTimeSecretOnlyInSuccessValue()
    {
        var applicationId = Guid.NewGuid();
        var credential = new ApplicationCredentialResponse(Guid.NewGuid(), applicationId, "Dashboard", DateTimeOffset.UtcNow, null, null, null);
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ApplicationCredentialIssuedResponse(credential, "once-only-secret")),
        });

        var result = await client.IssueApplicationCredentialResultAsync(applicationId, new CreateApplicationCredentialRequest("Dashboard", null));

        Assert.True(result.Succeeded);
        Assert.Equal("once-only-secret", result.Value!.PlaintextCredential);
        Assert.Equal(DashboardAccessMutationFailure.None, result.Failure);
    }

    [Fact]
    public async Task Mutation_MalformedSuccessIsDistinctFromFailureResponse()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{broken", Encoding.UTF8, "application/json"),
        });

        var result = await client.CreateApplicationResultAsync(new CreateApplicationRequest("Test", null, ApplicationTypeDto.Automation, false, []));

        Assert.False(result.Succeeded);
        Assert.Equal(DashboardAccessMutationFailure.InvalidResponse, result.Failure);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task Mutation_TransportFailureIsTransient()
    {
        var client = Client(_ => throw new HttpRequestException("unavailable"));

        var result = await client.DeleteApplicationResultAsync(Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(DashboardAccessMutationFailure.Transient, result.Failure);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public async Task ApplicationBindingMutation_UsesCanonicalRouteAndTypedFailure()
    {
        var applicationId = Guid.NewGuid();
        var handler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new DashboardIdentityClient(new TestClientFactory(handler));

        var result = await client.SetApplicationClientBindingsResultAsync(applicationId, new SetApplicationClientBindingsRequest(["native-client"]));

        Assert.Equal(DashboardAccessMutationFailure.NotFound, result.Failure);
        Assert.Equal($"/access/applications/{applicationId:D}/client-bindings", handler.Path);
    }

    [Fact]
    public async Task AccountGrantMutations_AcceptTheActualNoContentSuccessContract()
    {
        var accountId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var handler = new RecordingNoContentHandler();
        var client = new DashboardIdentityClient(new TestClientFactory(handler));

        var access = await client.ReplaceManagedAccountAccessResultAsync(accountId, new ReplaceAccountAccessRequest(["read"], []));
        var grant = await client.SetManagedProfileGrantResultAsync(accountId, profileId, new SetAccountProfileGrantAccessRequest(true, true));
        var protection = await client.SetGrantProtectionResultAsync(accountId, profileId, new SetGrantAdminProtectionRequest(true, "pin", "FixedDuration", 30));

        Assert.True(access.Succeeded);
        Assert.True(grant.Succeeded);
        Assert.True(protection.Succeeded);
        Assert.All(handler.Requests, request => Assert.Equal("application/json", request.ContentType));
        Assert.Equal([
            $"/access/accounts/{accountId:D}/access",
            $"/access/accounts/{accountId:D}/grants/{profileId:D}",
            $"/access/accounts/{accountId:D}/grants/{profileId:D}/admin-protection",
        ], handler.Paths);
    }

    [Fact]
    public async Task ValidationMutation_WithNonObjectProblemBodyStillReturnsValidation()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/problem+json"),
        });

        var result = await client.CreateManagedAccountResultAsync(CreateAccountRequest());

        Assert.Equal(DashboardAccessMutationFailure.Validation, result.Failure);
        Assert.Empty(result.ValidationFields!);
    }

    private static CreateManagedAccountRequest CreateAccountRequest() =>
        new("person@example.test", false, Guid.NewGuid(), null, [], []);

    private static DashboardIdentityClient Client(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new TestClientFactory(new DelegateHandler(response)));

    private sealed class TestClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Path = request.RequestUri!.AbsolutePath;
            return response(request);
        }
    }

    private sealed class RecordingNoContentHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<(string? ContentType, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Requests.Add((request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
