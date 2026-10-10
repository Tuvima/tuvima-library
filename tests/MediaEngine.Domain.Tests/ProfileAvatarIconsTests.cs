using MediaEngine.Domain.Aggregates;

namespace MediaEngine.Domain.Tests;

public sealed class ProfileAvatarIconsTests
{
    [Fact]
    public void ThereAreTwelveDistinctIcons()
    {
        Assert.Equal(12, ProfileAvatarIcons.All.Count);
        Assert.Equal(12, ProfileAvatarIcons.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_TreatsBlankAsNoIcon(string? key) => Assert.Null(ProfileAvatarIcons.Normalize(key));

    [Fact]
    public void Normalize_AcceptsAKnownKeyInAnyCase() => Assert.Equal("rocket", ProfileAvatarIcons.Normalize("  Rocket "));

    [Fact]
    public void Normalize_RejectsAnUnknownKey() => Assert.Throws<ArgumentException>(() => ProfileAvatarIcons.Normalize("<script>"));
}
