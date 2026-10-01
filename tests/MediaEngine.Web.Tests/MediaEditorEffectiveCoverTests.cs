using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class MediaEditorEffectiveCoverTests
{
    [Fact]
    public async Task FocusedOwnedFileReadsEffectiveCoverWithEditionOwner()
    {
        var assetId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var variant = new ArtworkEntityVariantDto(Guid.NewGuid(), Guid.NewGuid(), "Primary", null,
            "CoverArt", true, false, "/artwork/full", "/artwork/thumb", 600, 900, "Portrait", "Library", null);
        var payload = JsonSerializer.Serialize(new EffectiveArtworkSelection(variant, "Edition", editionId, false),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var handler = new StaticHandler(payload);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var cover = await api.GetEffectiveOwnedAssetCoverAsync(assetId);

        Assert.Equal($"/api/v1/display/artwork/media-assets/{assetId:D}/effective-cover", handler.Path);
        var actual = Assert.IsType<EffectiveArtworkSelection>(cover);
        Assert.Equal(editionId, actual.SourceEntityId);
        Assert.Equal("Edition cover for this file", MediaEditorEffectiveCoverPresentation.OwnerLabel(actual));
        Assert.Contains("thumb", actual.Variant?.ThumbnailUrl);
    }

    [Fact]
    public void InheritedOwnerIsExplicitAndOtherRolesAreOutsideThisRead()
    {
        var inherited = new EffectiveArtworkSelection(null, "Work", Guid.NewGuid(), true);
        Assert.Equal("Inherited from Work", MediaEditorEffectiveCoverPresentation.OwnerLabel(inherited));
        Assert.True(MediaEditorEffectiveCoverPresentation.SupportsOwnedFileCover("Music"));
        Assert.True(MediaEditorEffectiveCoverPresentation.SupportsOwnedFileCover("Audiobooks"));
        Assert.False(MediaEditorEffectiveCoverPresentation.SupportsOwnedFileCover("TV"));
        Assert.True(MediaEditorEffectiveCoverPresentation.SupportsOwnedFileCover("Comics"));
        Assert.True(MediaEditorEffectiveCoverPresentation.SupportsOwnedFileCover("Comic"));
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
