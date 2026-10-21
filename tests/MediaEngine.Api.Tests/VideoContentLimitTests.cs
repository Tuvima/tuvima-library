using MediaEngine.Api.Security;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Tests;

/// <summary>Saved videos carry no ratings, so limited profiles see them only from a library marked OK for them.</summary>
public sealed class VideoContentLimitTests
{
    private const string Library = "77777777-7777-4777-8777-777777777777";

    [Fact]
    public void LimitedProfile_DoesNotSeeVideos_UnlessTheLibraryIsMarkedOk()
    {
        var hidden = new ContentLimitFilter(ProfileContentLimit.Strictest);
        var allowed = new ContentLimitFilter(ProfileContentLimit.Strictest, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Library });

        Assert.False(hidden.AllowsVideo(Library, "Videos"));
        Assert.True(allowed.AllowsVideo(Library, "Videos"));
        Assert.False(allowed.AllowsVideo("11111111-1111-4111-8111-111111111111", "Videos"));
    }

    [Fact]
    public void OtherMediaAndUnrestrictedProfiles_AreNotAffected()
    {
        var limited = new ContentLimitFilter(ProfileContentLimit.Strictest);

        Assert.True(limited.AllowsVideo(Library, "Movies"));
        Assert.True(limited.AllowsVideo(null, "TV"));
        Assert.True(ContentLimitFilter.Unrestricted.AllowsVideo(Library, "Videos"));
    }
}
