using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

/// <summary>
/// What the Dashboard shows outside the player for a Vault video (Videos spec D33): the browser tab, the phone and computer media
/// controls, the lock screen and the device list all say "Video" and nothing else, and playing it keeps Vault mode awake.
/// </summary>
public sealed class VaultPlaybackPrivacyTests
{
    private static readonly Uri BaseUri = new("http://localhost:5016/");

    private static ListenQueueItem Item(bool vault, Guid? workId = null) => new()
    {
        WorkId = workId ?? Guid.NewGuid(),
        MediaType = "Movie",
        Title = "A Very Private Title",
        Year = "2024",
        Album = "A Private Series",
        Manifest = new PlaybackManifestDto { IsVault = vault },
    };

    [Fact]
    public void TheMediaControls_ForAVaultVideo_SayVideoAndCarryNoArtistAlbumOrArtwork()
    {
        var metadata = VideoMediaSessionMetadata.Create(Item(vault: true), "A Private Show", BaseUri);

        Assert.Equal("Video", metadata.Title);
        Assert.Equal(string.Empty, metadata.Artist);
        Assert.Equal(string.Empty, metadata.Album);
        Assert.Empty(metadata.Artwork);
    }

    [Fact]
    public void TheMediaControls_ForAnOrdinaryVideo_StillShowTheRealTitleAndArtwork()
    {
        var metadata = VideoMediaSessionMetadata.Create(Item(vault: false), null, BaseUri);

        Assert.Equal("A Very Private Title", metadata.Title);
        Assert.NotEmpty(metadata.Artwork);
    }

    [Fact]
    public void TheDeviceTitle_IsGenericForAVaultVideo_AndTheRealTitleOtherwise()
    {
        Assert.Equal("Video", Item(vault: true).DeviceTitle);
        Assert.Equal("A Very Private Title", Item(vault: false).DeviceTitle);
        Assert.False(new ListenQueueItem { Title = "No manifest yet" }.IsVault);
    }

    [Fact]
    public void ThePlayerPageTitle_NamesAVaultVideoOnlyAsVideo_AndNeverShowsTheRealTitleBeforeTheFileIsKnown()
    {
        var work = Guid.NewGuid();
        var loadedVault = Item(vault: true, work);
        var loadedOrdinary = Item(vault: false, work);
        var notLoadedYet = loadedOrdinary with { Manifest = null };
        var otherWork = Item(vault: false);

        Assert.Equal("Video", VaultPrivacy.PageTitle(loadedVault, work, "A Very Private Title"));
        Assert.Equal("A Very Private Title", VaultPrivacy.PageTitle(loadedOrdinary, work, "A Very Private Title"));

        // Until the Engine has said whether this file is in the Vault, the real title stays out of the tab, even for a moment.
        Assert.Equal("Video", VaultPrivacy.PageTitle(null, work, "A Very Private Title"));
        Assert.Equal("Video", VaultPrivacy.PageTitle(notLoadedYet, work, "A Very Private Title"));
        Assert.Equal("Video", VaultPrivacy.PageTitle(otherWork, work, "A Very Private Title"));
        Assert.Equal("Player", VaultPrivacy.PageTitle(null, work, null));
    }

    [Fact]
    public void OnlyAPlayingVaultVideo_KeepsNsfwModeAwake()
    {
        Assert.True(VaultPrivacy.ShouldKeepAwake(Item(vault: true), isPlaying: true));
        Assert.False(VaultPrivacy.ShouldKeepAwake(Item(vault: true), isPlaying: false));
        Assert.False(VaultPrivacy.ShouldKeepAwake(Item(vault: false), isPlaying: true));
        Assert.False(VaultPrivacy.ShouldKeepAwake(null, isPlaying: true));
    }
}
