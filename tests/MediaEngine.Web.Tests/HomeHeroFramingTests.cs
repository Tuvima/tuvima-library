using MediaEngine.Web.Services.MediaTiles;
namespace MediaEngine.Web.Tests;
public sealed class HomeHeroFramingTests
{
    [Theory]
    [InlineData(1920,1080,true,false,"center 22%")]
    [InlineData(1600,1100,true,false,"center 30%")]
    [InlineData(1000,700,false,true,"center center")]
    [InlineData(1000,700,true,false,"center 30%")]
    [InlineData(800,1200,true,true,"center center")]
    [InlineData(0,0,true,true,"center center")]
    public void OnlyAdequateMeasuredArtworkUsesFullBleed(int width,int height,bool large,bool cover,string position)
    {
        var framing=HomeHeroFraming.Resolve(width,height,large);
        Assert.Equal(cover,framing.CoverLed); Assert.Equal(position,framing.Position);
    }
}
