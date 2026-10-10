using System.Net;
using System.Text;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientViewPolicyTests
{
    private static readonly Guid ProfileId = Guid.Parse("b5028d65-b179-4bed-a3b6-2e6011b90d31");

    [Theory]
    [InlineData(HttpStatusCode.NoContent, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public async Task ProfileExperienceUsesCurrentRouteWithoutRoleAndHonorsDenial(HttpStatusCode status, bool expected)
    {
        using var http = CreateHttpClient(async request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal($"/profiles/{ProfileId:D}/experience", request.RequestUri!.AbsolutePath);
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("Profile name", body.RootElement.GetProperty("display_name").GetString());
            Assert.Equal("{}", body.RootElement.GetProperty("navigation_config").GetString());
            Assert.False(body.RootElement.TryGetProperty("role", out _));
            return new HttpResponseMessage(status);
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        Assert.Equal(expected, await client.UpdateProfileAsync(ProfileId, "Profile name", "#123456", "{}"));
    }

    [Fact]
    public async Task UpdateViewProfilePolicyAsync_PreservesIndependentSharedDecisions()
    {
        string? requestJson = null;
        using var http = CreateHttpClient(async request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal($"/profiles/{ProfileId:D}/settings/view", request.RequestUri!.AbsolutePath);
            requestJson = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
            };
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var saved = await client.UpdateViewProfilePolicyAsync(ProfileId, new UpdateViewProfilePolicyRequest
        {
            ViewEnabled = true,
            AccessSharedLibrary = false,
            SubmitToSharedLibrary = true,
            AllowGallerySharing = true,
        });

        Assert.NotNull(saved);
        Assert.Contains("\"access_shared_library\":false", requestJson, StringComparison.Ordinal);
        Assert.Contains("\"submit_to_shared_library\":true", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("review_shared_library_contributions", requestJson, StringComparison.Ordinal);
        Assert.False(saved.AccessSharedLibrary);
        Assert.True(saved.SubmitToSharedLibrary);
    }

    [Fact]
    public async Task GetViewProfilePolicyAsync_UsesProfileAdminRoute()
    {
        using var http = CreateHttpClient(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"/profiles/{ProfileId:D}/settings/view", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {
                      "profile_id": "{{ProfileId:D}}",
                      "view_enabled": true,
                      "access_shared_library": true,
                      "submit_to_shared_library": false,
                      "allow_gallery_sharing": false,
                      "updated_at": null
                    }
                    """, Encoding.UTF8, "application/json"),
            });
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var policy = await client.GetViewProfilePolicyAsync(ProfileId);

        Assert.NotNull(policy);
        Assert.True(policy.ViewEnabled);
        Assert.True(policy.AccessSharedLibrary);
        Assert.False(policy.SubmitToSharedLibrary);
    }

    [Fact]
    public async Task GetViewProfileSourcesAsync_UsesSafeAdministratorReviewRoute()
    {
        using var http = CreateHttpClient(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"/view/admin/profiles/{ProfileId:D}/sources", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {
                      "profile_id": "{{ProfileId:D}}",
                      "personal_space": {
                        "id": "bf270036-a9ad-4d95-a65f-672bf163a074",
                        "created_at": "2026-08-20T08:00:00Z",
                        "updated_at": "2026-08-23T09:00:00Z"
                      },
                      "sources": [{
                        "id": "13fb5fea-eaf6-4f89-8bc5-bb7ec379da94",
                        "source_type": "folder",
                        "name": "Family imports",
                        "last_activity_at": "2026-08-23T08:30:00Z",
                        "created_at": "2026-08-20T08:00:00Z",
                        "updated_at": "2026-08-23T08:30:00Z"
                      }],
                      "devices": []
                    }
                    """, Encoding.UTF8, "application/json"),
            });
        });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var review = await client.GetViewProfileSourcesAsync(ProfileId);

        Assert.NotNull(review);
        Assert.Equal(ProfileId, review.ProfileId);
        Assert.NotNull(review.PersonalSpace);
        Assert.Equal("Family imports", Assert.Single(review.Sources).Name);
        Assert.Equal("folder", review.Sources[0].SourceType);
        Assert.Empty(review.Devices);
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) =>
        new(new StubHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("http://localhost:61495/"),
        };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request);
    }
}
