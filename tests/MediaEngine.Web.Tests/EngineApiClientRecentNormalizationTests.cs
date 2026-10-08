using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientRecentNormalizationTests
{
    [Fact]
    public async Task RecentCatalogueNormalizesEveryRenditionAndPreviewWithoutChangingViewMetadata()
    {
        var asset=Guid.NewGuid();var added=DateTimeOffset.UtcNow;
        var fields=typeof(DisplayArtworkDto).GetProperties().Where(p=>p.Name.EndsWith("Url",StringComparison.Ordinal)).ToDictionary(p=>p.Name,p=>$"/stream/artwork/{asset:D}?variant={p.Name}");
        var artwork=JsonSerializer.Deserialize<DisplayArtworkDto>(JsonSerializer.Serialize(fields))!;
        var card=new DisplayCardDto(asset,asset,asset,null,"Book","work","Book",null,[],artwork,"portrait","default","caption","bottom",null,[],new(false,true,false,false),added)
        {PreviewItems=[new(asset,asset,"Preview",$"/stream/artwork/{asset:D}?size=s","portrait",null)]};
        var view=new DisplayRecentViewAssetDto(Guid.NewGuid(),Guid.NewGuid(),"Photo","photo.jpg","image",640,400,null,added);
        var page=new DisplayRecentPageDto("all",[new("catalogue:"+asset.ToString("N"),added,card,null),new("view:"+view.AssetId.ToString("N"),added,null,view)],null,false);
        using var http=new HttpClient(new Handler(page)){BaseAddress=new Uri("http://localhost:61495")};
        using var client=new EngineApiClient(http,NullLogger<EngineApiClient>.Instance);
        var result=await client.GetDisplayRecentAsync();Assert.NotNull(result);
        var normalized=result.Items[0].Catalogue!;
        foreach(var property in typeof(DisplayArtworkDto).GetProperties().Where(p=>p.Name.EndsWith("Url",StringComparison.Ordinal)))
        {
            Assert.Equal($"/engine-image/stream/artwork/{asset:D}?variant={property.Name}",property.GetValue(normalized.Artwork));
        }
        Assert.Equal($"/engine-image/stream/artwork/{asset:D}?size=s",Assert.Single(normalized.PreviewItems).ImageUrl);
        Assert.Equal(view,result.Items[1].ViewAsset);Assert.Null(result.Items[1].Catalogue);
    }
    private sealed class Handler(DisplayRecentPageDto page):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
            =>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(page)});
    }
}
