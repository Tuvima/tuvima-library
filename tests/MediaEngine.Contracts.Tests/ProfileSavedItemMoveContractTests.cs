using System.Text.Json;
using MediaEngine.Contracts.ProfileState;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shape of moving a hearted song within Favorites (Listen music spec D14).</summary>
public sealed class ProfileSavedItemMoveContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TheMoveRequest_StillCarriesAPlace_AsToIndex()
    {
        var json = JsonSerializer.Serialize(new ProfileSavedItemMoveRequest(3), Web);

        Assert.Equal("{\"toIndex\":3,\"beforeEntityId\":null}", json);
        Assert.Equal(0, JsonSerializer.Deserialize<ProfileSavedItemMoveRequest>("{\"toIndex\":0}", Web)!.ToIndex);
    }

    [Fact]
    public void TheMoveRequest_CanNameTheFavoriteTheSongShouldSitBefore_AsBeforeEntityId()
    {
        var neighbour = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var json = JsonSerializer.Serialize(new ProfileSavedItemMoveRequest(BeforeEntityId: neighbour), Web);

        Assert.Equal("{\"toIndex\":null,\"beforeEntityId\":\"22222222-2222-2222-2222-222222222222\"}", json);
        var read = JsonSerializer.Deserialize<ProfileSavedItemMoveRequest>("{\"beforeEntityId\":\"22222222-2222-2222-2222-222222222222\"}", Web)!;
        Assert.Null(read.ToIndex);
        Assert.Equal(neighbour, read.BeforeEntityId);
    }

    [Fact]
    public void AMoveRequestWithNeitherField_MeansMoveToTheEnd()
    {
        var read = JsonSerializer.Deserialize<ProfileSavedItemMoveRequest>("{}", Web)!;

        Assert.Null(read.ToIndex);
        Assert.Null(read.BeforeEntityId);
    }

    [Fact]
    public void ASavedItem_StillCarriesItsSavedPosition_SoTheDashboardCanReadTheOrder()
    {
        var item = new ProfileSavedItemDto(MediaEngine.Domain.Enums.ProfileEntityKind.Song, Guid.Parse("11111111-1111-1111-1111-111111111111"), DateTimeOffset.Parse("2026-11-12T00:00:00+00:00"), -2);

        var json = JsonSerializer.Serialize(item, Web);

        Assert.Contains("\"position\":-2", json, StringComparison.Ordinal);
        Assert.Equal(-2, JsonSerializer.Deserialize<ProfileSavedItemDto>(json, Web)!.Position);
    }
}
