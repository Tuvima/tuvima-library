using System.Net;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

public sealed class EngineBusyBackoffHandlerTests
{
    [Fact]
    public async Task Get_AskedAgainAfterBusyAnswer_ReturnsTheLaterSuccess()
    {
        var inner = new Script(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        var state = new EngineBusyState();
        using var client = new HttpClient(new EngineBusyBackoffHandler(state) { InnerHandler = inner });

        using var response = await client.GetAsync("http://engine.test/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
        Assert.True(state.WasBusyRecently);
    }

    [Fact]
    public async Task Post_IsNeverRepeated_ButStillMarksTheEngineBusy()
    {
        var inner = new Script(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        var state = new EngineBusyState();
        using var client = new HttpClient(new EngineBusyBackoffHandler(state) { InnerHandler = inner });

        using var response = await client.PostAsync("http://engine.test/save", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, inner.Calls);
        Assert.True(state.WasBusyRecently);
    }

    [Fact]
    public async Task HealthyEngine_IsNeverMarkedBusy()
    {
        var state = new EngineBusyState();
        using var client = new HttpClient(new EngineBusyBackoffHandler(state) { InnerHandler = new Script(HttpStatusCode.OK) });
        using var response = await client.GetAsync("http://engine.test/ping");
        Assert.False(state.WasBusyRecently);
    }

    private sealed class Script(params HttpStatusCode[] answers) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var status = answers[Math.Min(Calls++, answers.Length - 1)];
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
            }

            return Task.FromResult(response);
        }
    }
}
