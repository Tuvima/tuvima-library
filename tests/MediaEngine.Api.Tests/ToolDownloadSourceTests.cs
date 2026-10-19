using System.Net;
using MediaEngine.Api.Services.Tools;

namespace MediaEngine.Api.Tests;

/// <summary>
/// GitHub hands release and raw files on to its own storage hosts. The Library follows those redirects by hand, only
/// to a short list of hosts, only over https and for a few hops, so a redirect can never lead a download elsewhere.
/// </summary>
public sealed class ToolDownloadSourceTests
{
    private static readonly Uri Start = new("https://github.com/test/ffmpeg.zip");

    [Theory]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/ffmpeg.zip")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/ffmpeg.zip")]
    [InlineData("https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/abc/eng.traineddata")]
    [InlineData("https://github.com/test/other-place.zip")]
    public async Task ARedirectToAnAllowedGithubHost_IsFollowed(string target)
    {
        var handler = new Redirects(new Dictionary<string, string> { [Start.ToString()] = target });
        var source = new HttpToolDownloadSource(new Factory(handler));

        await using var download = await source.OpenAsync(Start, CancellationToken.None);

        Assert.Equal("payload", await new StreamReader(download.Content).ReadToEndAsync());
        Assert.Equal([Start.ToString(), target], handler.Requested);
    }

    [Theory]
    [InlineData("https://evil.example.com/ffmpeg.zip")]
    [InlineData("https://github.com.evil.example.com/ffmpeg.zip")]
    [InlineData("https://githubusercontent.com/ffmpeg.zip")]
    [InlineData("https://user:secret@objects.githubusercontent.com/ffmpeg.zip")]
    [InlineData("https://objects.githubusercontent.com:8443/ffmpeg.zip")]
    [InlineData("http://objects.githubusercontent.com/ffmpeg.zip")]
    public async Task ARedirectToAnyOtherHost_OrOverPlainHttp_IsRefused_BeforeItIsRequested(string target)
    {
        var handler = new Redirects(new Dictionary<string, string> { [Start.ToString()] = target });
        var source = new HttpToolDownloadSource(new Factory(handler));

        await Assert.ThrowsAsync<ToolDownloadRefusedException>(() => source.OpenAsync(Start, CancellationToken.None));

        Assert.Equal([Start.ToString()], handler.Requested);
    }

    [Fact]
    public async Task ARelativeRedirect_IsResolvedAgainstTheCurrentAddress_AndChecked()
    {
        var handler = new Redirects(new Dictionary<string, string> { [Start.ToString()] = "/test/moved.zip" });
        var source = new HttpToolDownloadSource(new Factory(handler));

        await using var download = await source.OpenAsync(Start, CancellationToken.None);

        Assert.Equal("https://github.com/test/moved.zip", handler.Requested[^1]);
    }

    [Fact]
    public async Task MoreThanFiveRedirects_AreRefused()
    {
        var chain = new Dictionary<string, string>();
        for (var hop = 0; hop < 10; hop++)
        {
            var from = hop == 0 ? Start.ToString() : $"https://github.com/test/hop{hop}.zip";
            chain[from] = $"https://github.com/test/hop{hop + 1}.zip";
        }

        var handler = new Redirects(chain);
        var source = new HttpToolDownloadSource(new Factory(handler));

        await Assert.ThrowsAsync<ToolDownloadRefusedException>(() => source.OpenAsync(Start, CancellationToken.None));

        Assert.Equal(HttpToolDownloadSource.MaxRedirects + 1, handler.Requested.Count);
    }

    [Fact]
    public async Task ARedirectWithNoLocation_IsRefused()
    {
        var source = new HttpToolDownloadSource(new Factory(new NoLocation()));

        await Assert.ThrowsAsync<ToolDownloadRefusedException>(() => source.OpenAsync(Start, CancellationToken.None));
    }

    [Fact]
    public async Task AnErrorResponse_IsAFailedDownload_NotARedirect()
    {
        var source = new HttpToolDownloadSource(new Factory(new Redirects(new Dictionary<string, string>(), HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<HttpRequestException>(() => source.OpenAsync(Start, CancellationToken.None));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Redirects(Dictionary<string, string> redirects, HttpStatusCode fallback = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var address = request.RequestUri!.ToString();
            Requested.Add(address);
            if (redirects.TryGetValue(address, out var target))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
                return Task.FromResult(redirect);
            }

            return Task.FromResult(new HttpResponseMessage(fallback) { Content = new StringContent("payload") });
        }
    }

    private sealed class NoLocation : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found));
    }
}
