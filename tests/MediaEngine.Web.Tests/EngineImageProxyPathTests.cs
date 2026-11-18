using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

public sealed class EngineImageProxyPathTests
{
    private static readonly Uri EngineBaseAddress = new("http://localhost:61495");

    [Theory]
    [InlineData("/stream/artwork/11111111-1111-1111-1111-111111111111?size=s", "/engine-image/stream/artwork/11111111-1111-1111-1111-111111111111?size=s")]
    [InlineData("/stream/22222222-2222-2222-2222-222222222222/cover", "/engine-image/stream/22222222-2222-2222-2222-222222222222/cover")]
    [InlineData("http://localhost:61495/persons/33333333-3333-3333-3333-333333333333/headshot", "/engine-image/persons/33333333-3333-3333-3333-333333333333/headshot")]
    [InlineData("/api/v1/display/artwork/assets/44444444-4444-4444-4444-444444444444/content?size=s", "/engine-image/api/v1/display/artwork/assets/44444444-4444-4444-4444-444444444444/content?size=s")]
    public void ToBrowserUrl_ProxiesKnownEngineImages(string value, string expected)
    {
        Assert.Equal(expected, EngineImageProxyPath.ToBrowserUrl(value, EngineBaseAddress));
    }

    [Theory]
    [InlineData("/metadata/55555555-5555-5555-5555-555555555555/video-frames/video/3")]
    [InlineData("/metadata/55555555-5555-5555-5555-555555555555/video-frames/episode/0")]
    public void ToBrowserUrl_ProxiesAFrameTakenFromAVideo(string value)
    {
        Assert.Equal($"/engine-image{value}", EngineImageProxyPath.ToBrowserUrl(value, EngineBaseAddress));
    }

    [Theory]
    [InlineData("/metadata/55555555-5555-5555-5555-555555555555/video-frames/video/notanumber")]
    [InlineData("/metadata/55555555-5555-5555-5555-555555555555/video-frames/video/12345")]
    [InlineData("/metadata/not-a-guid/video-frames/video/1")]
    [InlineData("/metadata/55555555-5555-5555-5555-555555555555/video-frames/video")]
    public void IsAllowedEnginePath_RejectsAFrameAddressThatIsNotOne(string value)
    {
        Assert.False(EngineImageProxyPath.IsAllowedEnginePath(value));
    }

    [Theory]
    [InlineData("/stream/11111111-1111-1111-1111-111111111111")]
    [InlineData("/stream/11111111-1111-1111-1111-111111111111/lyrics")]
    [InlineData("/auth/sessions")]
    [InlineData("/stream/artwork/not-a-guid")]
    public void IsAllowedEnginePath_RejectsNonArtworkRoutes(string value)
    {
        Assert.False(EngineImageProxyPath.IsAllowedEnginePath(value));
    }

    [Fact]
    public void ToBrowserUrl_DoesNotProxyAnotherOrigin()
    {
        const string remote = "https://images.example.test/stream/artwork/11111111-1111-1111-1111-111111111111";
        Assert.Equal(remote, EngineImageProxyPath.ToBrowserUrl(remote, EngineBaseAddress));
    }
}
