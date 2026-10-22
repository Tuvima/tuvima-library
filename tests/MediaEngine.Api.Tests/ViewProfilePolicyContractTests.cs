using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Profiles;

namespace MediaEngine.Api.Tests;

public sealed class ViewProfilePolicyContractTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SharedLibraryCapabilitiesRemainIndependent(bool accessSharedLibrary, bool submitToSharedLibrary)
    {
        var profileId = Guid.NewGuid();
        var policy = ProfileContractMapper.ToDomain(profileId, new UpdateViewProfilePolicyRequest
        {
            ViewEnabled = true,
            AccessSharedLibrary = accessSharedLibrary,
            SubmitToSharedLibrary = submitToSharedLibrary,
            AllowGallerySharing = true,
        }, currentDocumentsEnabled: true);

        var contract = ProfileContractMapper.ToResponse(policy);

        Assert.Equal(accessSharedLibrary, contract.AccessSharedLibrary);
        Assert.Equal(submitToSharedLibrary, contract.SubmitToSharedLibrary);
        Assert.True(contract.AllowGallerySharing);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DocumentsSwitchRoundTripsWhenTheRequestSetsIt(bool allowDocuments, bool saved)
    {
        var policy = ProfileContractMapper.ToDomain(Guid.NewGuid(), new UpdateViewProfilePolicyRequest
        {
            ViewEnabled = true,
            AllowDocuments = allowDocuments,
        }, currentDocumentsEnabled: saved);

        Assert.Equal(allowDocuments, policy.DocumentsEnabled);
        Assert.Equal(allowDocuments, ProfileContractMapper.ToResponse(policy).AllowDocuments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARequestThatOmitsTheDocumentsSwitchKeepsTheSavedValue(bool saved)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<UpdateViewProfilePolicyRequest>(
            """{"view_enabled":true,"access_shared_library":true}""")!;

        Assert.Null(request.AllowDocuments);
        Assert.Equal(saved, ProfileContractMapper.ToDomain(Guid.NewGuid(), request, saved).DocumentsEnabled);
        Assert.Null(new UpdateViewProfilePolicyRequest().AllowDocuments);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PhotosAndPersonalVideosSwitchesRoundTripWhenTheRequestSetsThem(bool photos, bool videos)
    {
        var policy = ProfileContractMapper.ToDomain(Guid.NewGuid(), new UpdateViewProfilePolicyRequest
        {
            ViewEnabled = true,
            AllowPhotos = photos,
            AllowPersonalVideos = videos,
        }, currentDocumentsEnabled: true, currentPhotosEnabled: !photos, currentPersonalVideosEnabled: !videos);

        var contract = ProfileContractMapper.ToResponse(policy);

        Assert.Equal(photos, policy.PhotosEnabled);
        Assert.Equal(videos, policy.PersonalVideosEnabled);
        Assert.Equal(photos, contract.AllowPhotos);
        Assert.Equal(videos, contract.AllowPersonalVideos);
    }

    [Fact]
    public void ARequestThatOmitsAKindSwitchKeepsTheSavedValue()
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<UpdateViewProfilePolicyRequest>(
            """{"view_enabled":true}""")!;

        var policy = ProfileContractMapper.ToDomain(Guid.NewGuid(), request, true, currentPhotosEnabled: false, currentPersonalVideosEnabled: false);

        Assert.False(policy.PhotosEnabled);
        Assert.False(policy.PersonalVideosEnabled);
        Assert.Equal(["photos", "personal_videos"], policy.BlockedContentKinds);
    }
}
