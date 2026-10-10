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
        });

        var contract = ProfileContractMapper.ToResponse(policy);

        Assert.Equal(accessSharedLibrary, contract.AccessSharedLibrary);
        Assert.Equal(submitToSharedLibrary, contract.SubmitToSharedLibrary);
        Assert.True(contract.AllowGallerySharing);
    }
}
