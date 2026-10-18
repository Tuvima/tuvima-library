using MediaEngine.Contracts.Review;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

/// <summary>The "from merge" flag on a review item, for files sitting in a managed folder's From merge holding folder.</summary>
public sealed class ReviewFromMergeBadgeTests
{
    [Fact]
    public void TheDashboardMapper_CarriesTheFlagAcross_AndTheWireNameIsFromMerge()
    {
        var dto = new ReviewItemDto { Id = Guid.NewGuid(), FromMerge = true };

        Assert.True(dto.ToViewModel().FromMerge);
        Assert.False(new ReviewItemDto { Id = Guid.NewGuid() }.ToViewModel().FromMerge);
        Assert.Contains("\"from_merge\":true", System.Text.Json.JsonSerializer.Serialize(dto), StringComparison.Ordinal);
    }
}
