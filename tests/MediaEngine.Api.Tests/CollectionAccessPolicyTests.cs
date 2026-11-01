using MediaEngine.Api.Models;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Tests;

public sealed class CollectionAccessPolicyTests
{
    private static readonly Guid OwnerProfileId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherProfileId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void ResolveVisibility_MapsUserAndLibraryScopes()
    {
        var privateCollection = CreateCollection(CollectionScope.User, OwnerProfileId);
        var sharedCollection = CreateCollection(CollectionScope.Library);

        Assert.Equal(CollectionAccessPolicy.PrivateVisibility, CollectionAccessPolicy.ResolveVisibility(privateCollection));
        Assert.Equal(CollectionAccessPolicy.SharedVisibility, CollectionAccessPolicy.ResolveVisibility(sharedCollection));
    }

    [Fact]
    public void CanAccess_AllowsSharedAndOwnedPrivateCollectionsOnly()
    {
        var activeProfile = new Profile
        {
            Id = OwnerProfileId,
        };

        var ownedPrivateCollection = CreateCollection(CollectionScope.User, OwnerProfileId);
        var otherPrivateCollection = CreateCollection(CollectionScope.User, OtherProfileId);
        var sharedCollection = CreateCollection(CollectionScope.Library);

        Assert.True(CollectionAccessPolicy.CanAccess(ownedPrivateCollection, activeProfile));
        Assert.False(CollectionAccessPolicy.CanAccess(otherPrivateCollection, activeProfile));
        Assert.True(CollectionAccessPolicy.CanAccess(sharedCollection, activeProfile));
    }

    [Fact]
    public void CanAccess_AllowsProfilesIncludedInSelectedAudience()
    {
        var collection = CreateCollection(CollectionScope.User, OtherProfileId);
        collection.Audience = ContainerAudience.SelectedProfiles;
        collection.ReplaceAudienceProfiles([OwnerProfileId]);

        Assert.True(CollectionAccessPolicy.CanAccess(collection, new Profile { Id = OwnerProfileId }));
        Assert.False(CollectionAccessPolicy.CanAccess(collection, new Profile { Id = Guid.NewGuid() }));
    }

    [Fact]
    public void CanEdit_SharedCollectionsRequiresExplicitWriteDecision()
    {
        var sharedCollection = CreateCollection(CollectionScope.Library);

        var activeProfile = new Profile { Id = OwnerProfileId };

        Assert.False(CollectionAccessPolicy.CanEdit(sharedCollection, activeProfile, isAdministrator: false));
        Assert.True(CollectionAccessPolicy.CanEdit(sharedCollection, activeProfile, isAdministrator: true));
        Assert.True(CollectionAccessPolicy.CanEdit(sharedCollection, activeProfile: null, isAdministrator: true));
    }

    [Fact]
    public void CanEdit_CuratedCollectionsRequiresExplicitWriteDecision()
    {
        var curatedCollection = CreateCollection(
            CollectionScope.Library,
            collectionType: CollectionType.Custom);
        var activeProfile = new Profile { Id = OwnerProfileId };

        Assert.False(CollectionAccessPolicy.CanManageCuratedCollections(isAdministrator: false));
        Assert.False(CollectionAccessPolicy.CanEdit(curatedCollection, activeProfile, isAdministrator: false));
        Assert.True(CollectionAccessPolicy.CanManageCuratedCollections(isAdministrator: true));
        Assert.True(CollectionAccessPolicy.CanEdit(curatedCollection, activeProfile, isAdministrator: true));
        Assert.True(CollectionAccessPolicy.CanEdit(curatedCollection, activeProfile: null, isAdministrator: true));
    }

    [Fact]
    public void CanEdit_ANonAdministratorEditsOnlyTheCollectionsTheirProfileOwns()
    {
        var me = new Profile { Id = OwnerProfileId };
        var mine = CreateCollection(CollectionScope.User, OwnerProfileId);
        var someoneElses = CreateCollection(CollectionScope.User, OtherProfileId);
        var library = CreateCollection(CollectionScope.Library);

        Assert.True(CollectionAccessPolicy.CanEdit(mine, me, isAdministrator: false));
        Assert.False(CollectionAccessPolicy.CanEdit(someoneElses, me, isAdministrator: false));
        Assert.False(CollectionAccessPolicy.CanEdit(library, me, isAdministrator: false));
        Assert.False(CollectionAccessPolicy.CanEdit(mine, activeProfile: null, isAdministrator: false));
    }

    [Fact]
    public void CanEdit_AnAdministratorEditsLibraryCollectionsAndTheirOwnButNotAnotherProfilesPrivateOnes()
    {
        var admin = new Profile { Id = OwnerProfileId };

        Assert.True(CollectionAccessPolicy.CanEdit(CreateCollection(CollectionScope.Library), admin, isAdministrator: true));
        Assert.True(CollectionAccessPolicy.CanEdit(CreateCollection(CollectionScope.User, OwnerProfileId), admin, isAdministrator: true));
        Assert.False(CollectionAccessPolicy.CanEdit(CreateCollection(CollectionScope.User, OtherProfileId), admin, isAdministrator: true));
    }

    [Fact]
    public void CanFill_ListsOnlyEnabledHandPickedCollectionsAndPlaylistsTheCallerMayEdit()
    {
        var me = new Profile { Id = OwnerProfileId };
        var myPlaylist = HandPicked(CreateCollection(CollectionScope.User, OwnerProfileId, CollectionType.Playlist));
        var library = HandPicked(CreateCollection(CollectionScope.Library, collectionType: CollectionType.Custom));
        var mine = HandPicked(CreateCollection(CollectionScope.User, OwnerProfileId, CollectionType.Custom));
        var disabled = HandPicked(CreateCollection(CollectionScope.User, OwnerProfileId, CollectionType.Custom));
        disabled.IsEnabled = false;
        var ruleDriven = HandPicked(CreateCollection(CollectionScope.User, OwnerProfileId, CollectionType.Custom));
        ruleDriven.ChangeResolution(CollectionResolution.Query);

        Assert.True(CollectionAccessPolicy.CanFill(mine, me, isAdministrator: false));
        Assert.True(CollectionAccessPolicy.CanFill(myPlaylist, me, isAdministrator: false));
        Assert.False(CollectionAccessPolicy.CanFill(library, me, isAdministrator: false));
        Assert.True(CollectionAccessPolicy.CanFill(library, me, isAdministrator: true));
        Assert.False(CollectionAccessPolicy.CanFill(disabled, me, isAdministrator: true));
        Assert.False(CollectionAccessPolicy.CanFill(ruleDriven, me, isAdministrator: true));
    }

    [Theory]
    [InlineData(ContainerOwnerKind.Profile, ContainerAudience.Private, null, false)]
    [InlineData(ContainerOwnerKind.Library, ContainerAudience.Private, null, true)]
    [InlineData(ContainerOwnerKind.Profile, ContainerAudience.SelectedProfiles, null, true)]
    [InlineData(ContainerOwnerKind.Profile, ContainerAudience.Everyone, null, true)]
    [InlineData(ContainerOwnerKind.Profile, ContainerAudience.Private, "shared", true)]
    [InlineData(ContainerOwnerKind.Profile, ContainerAudience.Private, "private", false)]
    public void RequestsSharing_IsTrueForAnythingBeyondACollectionOnlyTheOwnerSees(
        ContainerOwnerKind owner, ContainerAudience audience, string? visibility, bool expected) =>
        Assert.Equal(expected, CollectionAccessPolicy.RequestsSharing(owner, audience, visibility));

    [Fact]
    public void ApplyVisibility_MapsPrivateAndSharedToExistingStorageFields()
    {
        var collection = new Collection();

        CollectionAccessPolicy.ApplyVisibility(collection, CollectionAccessPolicy.PrivateVisibility, OwnerProfileId);

        Assert.Equal(CollectionScope.User, collection.Scope);
        Assert.Equal(OwnerProfileId, collection.ProfileId);

        CollectionAccessPolicy.ApplyVisibility(collection, CollectionAccessPolicy.SharedVisibility, OwnerProfileId);

        Assert.Equal(CollectionScope.Library, collection.Scope);
        Assert.Null(collection.ProfileId);
    }

    private static Collection HandPicked(Collection collection)
    {
        collection.IsEnabled = true;
        collection.ChangeResolution(CollectionResolution.Materialized);
        return collection;
    }

    private static Collection CreateCollection(
        CollectionScope scope,
        Guid? profileId = null,
        CollectionType collectionType = CollectionType.Collection)
    {
        var collection = new Collection();
        collection.ClassifyAs(collectionType);
        collection.SetVisibility(scope, profileId);
        collection.OwnerKind = scope == CollectionScope.Library
            ? ContainerOwnerKind.Library
            : ContainerOwnerKind.Profile;
        collection.Audience = scope == CollectionScope.Library
            ? ContainerAudience.Everyone
            : ContainerAudience.Private;
        return collection;
    }
}
