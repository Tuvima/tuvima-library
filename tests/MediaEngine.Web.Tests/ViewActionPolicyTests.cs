using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

public sealed class ViewActionPolicyTests
{
    private static ViewActionContext Desktop(ViewItemOwnership ownership) =>
        new(ownership, CanEdit: true, FileMissing: false, CanTrash: true, IsArchived: false, IsTrashed: false, IsHidden: false);

    [Fact]
    public void OwnPhoto_OnDesktop_OffersEverything()
    {
        var actions = ViewActionPolicy.For(Desktop(ViewItemOwnership.Own));

        Assert.Equal(
            ViewActions.Favorite | ViewActions.AddToGallery | ViewActions.Download | ViewActions.Edit | ViewActions.Archive | ViewActions.Hide | ViewActions.Trash,
            actions);
    }

    [Fact]
    public void SharedPhotoAddedBySomeoneElse_OffersOnlyTheSafeActions()
    {
        var actions = ViewActionPolicy.For(Desktop(ViewItemOwnership.SharedByOther));

        Assert.Equal(ViewActions.Favorite | ViewActions.AddToGallery | ViewActions.Download, actions);
    }

    [Fact]
    public void AnotherPersonsPhoto_IsLookAndDownloadOnly_AndNoDownloadWhenNotAllowed()
    {
        var context = Desktop(ViewItemOwnership.OtherPerson);

        Assert.Equal(ViewActions.Download, ViewActionPolicy.For(context));
        Assert.Equal(ViewActions.None, ViewActionPolicy.For(context with { DownloadAllowed = false }));
    }

    [Fact]
    public void Phone_OffersEditAndMoveToTrashLikeADesktop_WhenTheDeviceMayEditAndTheFolderIsWritable()
    {
        // The phone and the desktop ask the same rule: a phone is just a context with the same flags.
        var actions = ViewActionPolicy.For(Desktop(ViewItemOwnership.Own));

        Assert.True(actions.HasFlag(ViewActions.Edit));
        Assert.True(actions.HasFlag(ViewActions.Trash));
        Assert.True(actions.HasFlag(ViewActions.Archive));
    }

    [Fact]
    public void Phone_NeverOffersEditWhenTheDeviceMayNotEdit_ButStillOffersTrash()
    {
        var actions = ViewActionPolicy.For(Desktop(ViewItemOwnership.Own) with { CanEdit = false });

        Assert.False(actions.HasFlag(ViewActions.Edit));
        Assert.True(actions.HasFlag(ViewActions.Trash));
    }

    [Fact]
    public void OnADeviceThatMayNotEditItems_ATv_NeverOffersTrash_ButKeepsTheOtherActions()
    {
        var actions = ViewActionPolicy.For(Desktop(ViewItemOwnership.Own) with { CanEdit = false, DeviceMayEditItems = false });

        Assert.False(actions.HasFlag(ViewActions.Trash));
        Assert.True(actions.HasFlag(ViewActions.Archive));
        Assert.True(actions.HasFlag(ViewActions.Favorite));
    }

    [Fact]
    public void TrashedPhoto_OnATv_StillOffersRestore()
    {
        var actions = ViewActionPolicy.For(Desktop(ViewItemOwnership.Own) with { IsTrashed = true, DeviceMayEditItems = false });

        Assert.True(actions.HasFlag(ViewActions.Restore));
    }

    [Fact]
    public void PhotoInAReadOnlyFolder_NeverOffersTrash()
    {
        var actions = ViewActionPolicy.For(Desktop(ViewItemOwnership.Own) with { CanTrash = false });

        Assert.False(actions.HasFlag(ViewActions.Trash));
        Assert.True(actions.HasFlag(ViewActions.Archive));
    }

    [Fact]
    public void TrashedPhoto_OffersOnlyRestore()
    {
        var actions = ViewActionPolicy.For(Desktop(ViewItemOwnership.Own) with { IsTrashed = true });

        Assert.True(actions.HasFlag(ViewActions.Restore));
        Assert.False(actions.HasFlag(ViewActions.Archive));
        Assert.False(actions.HasFlag(ViewActions.Trash));
        Assert.False(actions.HasFlag(ViewActions.Edit));
    }

    [Fact]
    public void ArchivedPhoto_OffersRestoreAndEditButNotArchiveOrTrash()
    {
        var actions = ViewActionPolicy.For(Desktop(ViewItemOwnership.Own) with { IsArchived = true });

        Assert.True(actions.HasFlag(ViewActions.Restore));
        Assert.True(actions.HasFlag(ViewActions.Edit));
        Assert.False(actions.HasFlag(ViewActions.Archive));
        Assert.False(actions.HasFlag(ViewActions.Trash));
    }

    [Fact]
    public void MissingFile_OffersNothingToDoWithTheFile()
    {
        var actions = ViewActionPolicy.For(Desktop(ViewItemOwnership.Own) with { FileMissing = true });

        Assert.Equal(ViewActions.None, actions);
    }

    [Fact]
    public void AlreadyHiddenPhoto_DoesNotOfferHideAgain()
    {
        var actions = ViewActionPolicy.For(Desktop(ViewItemOwnership.Own) with { IsHidden = true });

        Assert.False(actions.HasFlag(ViewActions.Hide));
    }

    [Fact]
    public void OwnershipFollowsTheScopeAndWhoAddedTheSharedPhoto()
    {
        var me = Guid.NewGuid();

        Assert.Equal(ViewItemOwnership.Own, ViewActionPolicy.OwnershipOf(ViewScopeKind.Mine, null, me));
        Assert.Equal(ViewItemOwnership.Own, ViewActionPolicy.OwnershipOf(ViewScopeKind.Shared, me, me));
        Assert.Equal(ViewItemOwnership.SharedByOther, ViewActionPolicy.OwnershipOf(ViewScopeKind.Shared, Guid.NewGuid(), me));
        Assert.Equal(ViewItemOwnership.OtherPerson, ViewActionPolicy.OwnershipOf(ViewScopeKind.Profile, null, me));
    }
}
