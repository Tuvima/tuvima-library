using System.Net;
using System.Text;
using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The "Also appears in" client: a 404 from the Engine means nothing is visible for that subject, which the panel shows as
/// "Nothing else in your Library", while a real failure stays a failure (null) so the panel offers Retry.
/// </summary>
public sealed class EngineApiClientUniverseAppearancesTests
{
    private static readonly Guid Person = Guid.Parse("40000000-0000-0000-0000-000000000004");

    [Fact]
    public async Task ANotFoundAnswer_ForAnEntity_IsAnEmptyResult_NotAFailure()
    {
        var (client, handler) = Create(HttpStatusCode.NotFound, "{}");
        using var _ = client;

        var result = await client.GetEntityAppearancesAsync("Q4242", "Q937618", null);

        Assert.NotNull(result);
        Assert.Empty(result.Groups);
        Assert.Equal(0, result.TotalWorks);
        Assert.Equal("Q937618", result.SubjectQid);
        Assert.Equal("/universe/Q4242/also-appears-in?entity=Q937618", handler.LastPathAndQuery);
    }

    [Fact]
    public async Task ANotFoundAnswer_ForAPerson_IsAnEmptyResult_NotAFailure()
    {
        var (client, handler) = Create(HttpStatusCode.NotFound, "{}");
        using var _ = client;

        var result = await client.GetEntityAppearancesAsync("Q4242", null, Person);

        Assert.NotNull(result);
        Assert.Empty(result.Groups);
        Assert.Equal(Person, result.PersonId);
        Assert.Equal($"/universe/Q4242/also-appears-in?person={Person:D}", handler.LastPathAndQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task ARealFailure_StaysAFailure(HttpStatusCode status)
    {
        var (client, _) = Create(status, "{}");
        using var _ = client;

        Assert.Null(await client.GetEntityAppearancesAsync("Q4242", "Q937618", null));
    }

    [Fact]
    public async Task ASuccessfulAnswer_IsReadAsTheAppearances()
    {
        var (client, _) = Create(
            HttpStatusCode.OK,
            """{"universe_qid":"Q4242","subject_kind":"character","subject_label":"Hal Jordan","subject_qid":"Q937618","groups":[],"total_works":0,"works_without_character_data":2,"works_not_yet_checked":1}""");
        using var _ = client;

        var result = await client.GetEntityAppearancesAsync("Q4242", "Q937618", null);

        Assert.Equal("Hal Jordan", result!.SubjectLabel);
        Assert.Equal(2, result.WorksWithoutCharacterData);
        Assert.Equal(1, result.WorksNotYetChecked);
    }

    [Fact]
    public async Task BothOrNeitherSubject_IsRefusedWithoutAskingTheEngine()
    {
        var (client, handler) = Create(HttpStatusCode.OK, "{}");
        using var _ = client;

        Assert.Null(await client.GetEntityAppearancesAsync("Q4242", null, null));
        Assert.Null(await client.GetEntityAppearancesAsync("Q4242", "Q1", Person));
        Assert.Null(handler.LastPathAndQuery);
    }

    private static (EngineApiClient Client, StaticHandler Handler) Create(HttpStatusCode status, string body)
    {
        var handler = new StaticHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        return (new EngineApiClient(http, NullLogger<EngineApiClient>.Instance), handler);
    }

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastPathAndQuery { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPathAndQuery = request.RequestUri!.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
