using MediaEngine.Api.Security;
using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Deleting a user removes only the sign-in (the profile and everything in it stay), except the last household administrator's user
/// of a non-default household, which takes the household and every profile in it. Deleting a profile takes its user too and
/// everything personal. Who may do either, what happens to photos, and what is recorded. Real temporary data store; the photo
/// handling is a hand-written spy.
/// </summary>
public sealed class UserDeletionTests
{
    // ---- Deleting a user removes only the sign-in (D5) ----

    [Fact]
    public async Task DeletingAUser_RemovesOnlyTheSignIn_AndTheirProfileAndEverythingInItStay()
    {
        await using var world = await World.CreateAsync();

        await world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Kim.Id);

        Assert.Null(await world.Accounts.GetByIdAsync(world.Kim.Id));
        Assert.NotNull(await world.Profiles.GetByIdAsync(world.KimProfile.Id));
        Assert.NotNull(await world.Profiles.GetByIdAsync(world.Mary.Id));
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Alex.Id));
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Sam.Id));
        // Another person of the household can still open the profile that lost its sign-in.
        Assert.True(await world.Accounts.HasProfileAccessAsync(world.Alex.Id, world.KimProfile.Id));
        Assert.Empty(world.Photos.Prepared);
    }

    [Fact]
    public async Task DeletingAUser_RecordsHowManyProfilesWentButNeverTheirNames()
    {
        await using var world = await World.CreateAsync();

        await world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Kim.Id);

        var deleted = Assert.Single(world.Audit.Events, e => e.EventType == "account.deleted");
        Assert.Equal("0", deleted.Changes["profiles_deleted"]);
        Assert.Equal("false", deleted.Changes["household_removed"]);
        Assert.DoesNotContain(deleted.Changes.Values, value => value is not null && value.Contains("Kim", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(world.Kim.Id.ToString("D"), deleted.SubjectId);
    }

    [Fact]
    public async Task DeletingAHouseholdAdministratorsUser_WhoIsNotTheLast_LeavesTheProfileAnOrdinaryMember()
    {
        await using var world = await World.CreateAsync();

        await world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Sam.Id);

        var kept = (await world.Profiles.GetByIdAsync(world.SamProfile.Id))!;
        Assert.False(kept.IsHouseholdAdministrator);
        Assert.True((await world.Profiles.GetByIdAsync(await DefaultProfileAsync(world, world.Alex)))!.IsHouseholdAdministrator);
        Assert.NotNull(await world.Households.GetByIdAsync(world.Household.Id));
        Assert.Empty(world.Photos.Prepared);
    }

    [Fact]
    public async Task OnlyAServerAdministrator_CanDeleteUsers_NotAHouseholdAdministratorOrAMember()
    {
        await using var world = await World.CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.DeleteUserAsync(world.AlexAuthority, world.Kim.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.DeleteUserAsync(world.KimAuthority, world.Sam.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.PreviewDeleteUserAsync(world.AlexAuthority, world.Kim.Id));

        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Kim.Id));
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Sam.Id));
    }

    [Fact]
    public async Task DeletingAUserThatDoesNotExist_SaysSo()
    {
        await using var world = await World.CreateAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.DeleteUserAsync(world.ServerAdmin, Guid.NewGuid()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.PreviewDeleteUserAsync(world.ServerAdmin, Guid.NewGuid()));
    }

    // ---- The last household administrator's user takes the household (D6) ----

    [Fact]
    public async Task DeletingTheLastHouseholdAdministratorsUser_RemovesTheHouseholdWithEveryProfileInIt_AndMovesPhotosByDefault()
    {
        await using var world = await World.CreateAsync(secondOwner: false);
        var profiles = new[] { await DefaultProfileAsync(world, world.Alex), world.SamProfile.Id, world.Mary.Id, world.KimProfile.Id };

        await world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Alex.Id,
            confirmedProfileIds: await ConfirmedAsync(world, world.Alex.Id));

        Assert.Null(await world.Households.GetByIdAsync(world.Household.Id));
        foreach (var profile in profiles)
        {
            Assert.Null(await world.Profiles.GetByIdAsync(profile));
        }

        Assert.Null(await world.Accounts.GetByIdAsync(world.Alex.Id));
        Assert.Null(await world.Accounts.GetByIdAsync(world.Sam.Id));
        Assert.Null(await world.Accounts.GetByIdAsync(world.Kim.Id));
        Assert.Equal(profiles.Order().ToArray(), world.Photos.Prepared.Select(prepared => prepared.ProfileId).Order().ToArray());
        Assert.All(world.Photos.Prepared, prepared => Assert.Equal(ProfilePhotoDisposition.MoveToShared, prepared.Photos));
        // The Library's own household and the other household are untouched.
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.ServerAdminAccount.Id));
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Bea.Id));
        Assert.NotNull(await world.Households.GetByIdAsync(world.Bea.HouseholdId!.Value));
        var deleted = Assert.Single(world.Audit.Events, e => e.EventType == "account.deleted");
        Assert.Equal("4", deleted.Changes["profiles_deleted"]);
        Assert.Equal("true", deleted.Changes["household_removed"]);
        Assert.Single(world.Audit.Events, e => e.EventType == "household.deleted" && e.SubjectId == world.Household.Id.ToString("D"));
    }

    [Fact]
    public async Task WhenAHouseholdIsRemoved_ThePhotoChoiceDeleteIsAppliedToEveryProfile()
    {
        await using var world = await World.CreateAsync(secondOwner: false);

        await world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Alex.Id, ProfilePhotoDisposition.Delete,
            await ConfirmedAsync(world, world.Alex.Id));

        Assert.Equal(4, world.Photos.Prepared.Count);
        Assert.All(world.Photos.Prepared, prepared => Assert.Equal(ProfilePhotoDisposition.Delete, prepared.Photos));
    }

    [Fact]
    public async Task ThePreview_NamesTheProfilesTheHouseholdRemovalWouldTake_AndChangesNothing()
    {
        await using var world = await World.CreateAsync(secondOwner: false);

        var removal = await world.Mutations.PreviewDeleteUserAsync(world.ServerAdmin, world.Alex.Id);
        var signInOnly = await world.Mutations.PreviewDeleteUserAsync(world.ServerAdmin, world.Kim.Id);

        Assert.True(removal.Plan.RemovesHousehold);
        Assert.Equal(world.Household.Name, removal.Plan.HouseholdName);
        Assert.Equal(["Alex", "Kim", "Mary", "Sam"], removal.Plan.HouseholdProfiles.Select(profile => profile.DisplayName).Order().ToArray());
        Assert.Null(removal.RefusedReason);
        Assert.False(signInOnly.Plan.RemovesHousehold);
        Assert.Empty(signInOnly.Plan.HouseholdProfiles);
        Assert.Equal("Kim", signInOnly.Plan.ProfileName);
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Alex.Id));
        Assert.NotNull(await world.Households.GetByIdAsync(world.Household.Id));
        Assert.Empty(world.Photos.Prepared);
    }

    [Fact]
    public async Task ADeleteConfirmedAgainstAStaleList_IsRefusedBeforeAnythingMoves_AndChangesNothing()
    {
        await using var world = await World.CreateAsync(secondOwner: false);
        var confirmed = (await world.Mutations.PreviewDeleteUserAsync(world.ServerAdmin, world.Alex.Id))
            .Plan.HouseholdProfiles.Select(profile => profile.Id).ToArray();
        // Someone adds a person to the household after the list was shown.
        var zed = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
            new AddHouseholdPersonCommand(world.Household.Id, "Zed", null, false, null));

        var refused = await Assert.ThrowsAsync<AccountRemovalChangedException>(
            () => world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Alex.Id, ProfilePhotoDisposition.MoveToShared, confirmed));
        var empty = await Assert.ThrowsAsync<AccountRemovalChangedException>(
            () => world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Alex.Id, ProfilePhotoDisposition.MoveToShared, []));

        Assert.Equal("Something changed since you looked. Check the list and try again.", refused.Message);
        Assert.Equal(refused.Message, empty.Message);
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Alex.Id));
        Assert.NotNull(await world.Profiles.GetByIdAsync(zed.Id));
        Assert.Empty(world.Photos.Prepared);
        Assert.DoesNotContain(world.Audit.Events, e => e.EventType == "account.deleted");

        // The list as shown now, in any order, goes through; a caller that sends no list is not compared.
        var current = (await world.Mutations.PreviewDeleteUserAsync(world.ServerAdmin, world.Alex.Id))
            .Plan.HouseholdProfiles.Select(profile => profile.Id).Reverse().ToArray();
        await world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Alex.Id, ProfilePhotoDisposition.MoveToShared, current);
        Assert.Null(await world.Households.GetByIdAsync(world.Household.Id));
        Assert.Null(await world.Profiles.GetByIdAsync(zed.Id));
    }

    [Fact]
    public async Task ASignInOnlyDelete_ConfirmedWithAProfileList_IsRefused_AndAnEmptyListGoesThrough()
    {
        await using var world = await World.CreateAsync();

        await Assert.ThrowsAsync<AccountRemovalChangedException>(() => world.Mutations.DeleteUserAsync(
            world.ServerAdmin, world.Kim.Id, ProfilePhotoDisposition.MoveToShared, [world.KimProfile.Id]));
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Kim.Id));

        await world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Kim.Id, ProfilePhotoDisposition.MoveToShared, []);
        Assert.Null(await world.Accounts.GetByIdAsync(world.Kim.Id));
        Assert.NotNull(await world.Profiles.GetByIdAsync(world.KimProfile.Id));
    }

    [Fact]
    public async Task AHouseholdWhoseSharedLibraryStillHoldsPhotos_IsNotRemoved_AndNoPhotoMoves()
    {
        await using var world = await World.CreateAsync(secondOwner: false);
        await world.AddSharedPhotoAsync(world.Household.Id);

        var preview = await world.Mutations.PreviewDeleteUserAsync(world.ServerAdmin, world.Alex.Id);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Alex.Id));

        Assert.Equal("This household still has shared photos in View. Remove them first.", preview.RefusedReason);
        Assert.Equal(preview.RefusedReason, refused.Message);
        Assert.Empty(world.Photos.Prepared);
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Alex.Id));
        Assert.NotNull(await world.Households.GetByIdAsync(world.Household.Id));
    }

    [Fact]
    public async Task PersonalPhotos_CannotMoveIntoTheSharedLibraryOfTheHouseholdBeingRemoved_ButCanBeDeleted()
    {
        await using var world = await World.CreateAsync(secondOwner: false);
        world.Photos.ProfilesWithPhotos.Add(world.Mary.Id);

        var preview = await world.Mutations.PreviewDeleteUserAsync(world.ServerAdmin, world.Alex.Id);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Alex.Id));

        Assert.True(preview.HouseholdHasPersonalPhotos);
        Assert.Null(preview.RefusedReason);
        Assert.Equal(AccountAccessMutationService.PhotosCannotMoveIntoRemovedHouseholdMessage, refused.Message);
        Assert.Empty(world.Photos.Prepared);
        Assert.NotNull(await world.Households.GetByIdAsync(world.Household.Id));

        await world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Alex.Id, ProfilePhotoDisposition.Delete,
            await ConfirmedAsync(world, world.Alex.Id));
        Assert.Null(await world.Households.GetByIdAsync(world.Household.Id));
    }

    [Fact]
    public async Task WhenTheDeleteFailsAfterPhotosMoved_AnErrorNamesEachProfileId_AndNeverAName()
    {
        await using var world = await World.CreateAsync(secondOwner: false);
        world.Photos.ReportsPhotosMoved = true;
        // Shared photos appear after the checks and the photo step, so only the data store's own check stops the delete.
        world.Photos.OnPrepare = () => world.AddSharedPhotoAsync(world.Household.Id).GetAwaiter().GetResult();

        var confirmed = await ConfirmedAsync(world, world.Alex.Id);
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.DeleteUserAsync(
            world.ServerAdmin, world.Alex.Id, ProfilePhotoDisposition.MoveToShared, confirmed));

        Assert.Equal("This household still has shared photos in View. Remove them first.", failed.Message);
        Assert.Equal(4, world.Photos.Prepared.Count);
        Assert.Equal(4, world.Log.Messages.Count);
        Assert.All(world.Log.Levels, level => Assert.Equal(LogLevel.Error, level));
        foreach (var prepared in world.Photos.Prepared)
        {
            Assert.Single(world.Log.Messages, message => message.Contains(prepared.ProfileId.ToString("D"), StringComparison.Ordinal));
        }

        Assert.All(world.Log.Messages, message =>
        {
            Assert.Contains("Shared", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Alex", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Kim", message, StringComparison.Ordinal);
        });
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Alex.Id));
        Assert.NotNull(await world.Households.GetByIdAsync(world.Household.Id));
    }

    // ---- Never ----

    [Fact]
    public async Task ADefaultHousehold_IsNeverRemoved_EvenWhenItsLastHouseholdAdministratorsUserIsDeleted()
    {
        await using var world = await World.CreateAsync(secondOwner: false);
        await world.Access.SetDefaultHouseholdAsync(world.Household.Id);

        var preview = await world.Mutations.PreviewDeleteUserAsync(world.ServerAdmin, world.Alex.Id);
        await world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Alex.Id);

        Assert.False(preview.Plan.RemovesHousehold);
        Assert.NotNull(await world.Households.GetByIdAsync(world.Household.Id));
        Assert.NotNull(await world.Profiles.GetByIdAsync(await DefaultProfileAsync(world, world.Sam)));
        Assert.Null(await world.Accounts.GetByIdAsync(world.Alex.Id));
        Assert.Empty(world.Photos.Prepared);
    }

    [Fact]
    public async Task ADeleteOfTheActorsOwnSignIn_IsRefused()
    {
        await using var world = await World.CreateAsync();

        var preview = await world.Mutations.PreviewDeleteUserAsync(world.ServerAdmin, world.ServerAdminAccount.Id);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Mutations.DeleteUserAsync(world.ServerAdmin, world.ServerAdminAccount.Id));

        Assert.Equal("You can't delete the sign-in you are using.", refused.Message);
        Assert.Equal(refused.Message, preview.RefusedReason);
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.ServerAdminAccount.Id));
    }

    [Fact]
    public async Task TheLastServerAdministratorsUser_CannotBeDeleted_ByAnyone()
    {
        await using var world = await World.CreateAsync();
        var otherAdministrator = World.ServerAuthority(Guid.NewGuid(), Guid.NewGuid());

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Mutations.DeleteUserAsync(otherAdministrator, world.ServerAdminAccount.Id));

        Assert.Contains("final effective administrator", refused.Message, StringComparison.Ordinal);
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.ServerAdminAccount.Id));
        Assert.True((await world.Profiles.GetByIdAsync((await DefaultProfileAsync(world, world.ServerAdminAccount))))!.IsServerAdministrator);
    }

    // ---- Deleting a profile takes its user too and everything personal (D7, D8) ----

    [Fact]
    public async Task AHouseholdAdministrator_CanDeleteAPersonWithNoSignIn_AndNoOtherProfileChanges()
    {
        await using var world = await World.CreateAsync();

        await world.Mutations.DeleteProfileAsync(world.AlexAuthority, world.Mary.Id);

        Assert.Null(await world.Profiles.GetByIdAsync(world.Mary.Id));
        Assert.NotNull(await world.Profiles.GetByIdAsync(world.KimProfile.Id));
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Kim.Id));
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Alex.Id));
        Assert.Equal([(world.Mary.Id, ProfilePhotoDisposition.MoveToShared)], world.Photos.Prepared);
    }

    [Fact]
    public async Task DeletingAProfile_AlsoDeletesItsUser_AndSaysSoInTheAuditEntry()
    {
        await using var world = await World.CreateAsync();

        await world.Mutations.DeleteProfileAsync(world.AlexAuthority, world.KimProfile.Id, ProfilePhotoDisposition.Delete);

        Assert.Null(await world.Profiles.GetByIdAsync(world.KimProfile.Id));
        Assert.Null(await world.Accounts.GetByIdAsync(world.Kim.Id));
        Assert.Equal([(world.KimProfile.Id, ProfilePhotoDisposition.Delete)], world.Photos.Prepared);
        var deleted = Assert.Single(world.Audit.Events, e => e.EventType == "profile.deleted");
        Assert.Equal("true", deleted.Changes["sign_in_removed"]);
        Assert.Equal(world.KimProfile.Id.ToString("D"), deleted.SubjectId);
    }

    [Fact]
    public async Task HouseholdAdministrators_CanDeleteEachOthersProfiles_ButAHouseholdKeepsAtLeastOne()
    {
        await using var world = await World.CreateAsync();

        await world.Mutations.DeleteProfileAsync(world.AlexAuthority, world.SamProfile.Id);

        Assert.Null(await world.Profiles.GetByIdAsync(world.SamProfile.Id));
        Assert.Null(await world.Accounts.GetByIdAsync(world.Sam.Id));
        // Alex is now the household's only administrator: the server administrator cannot delete Alex's profile either.
        var alexProfile = await DefaultProfileAsync(world, world.Alex);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.DeleteProfileAsync(world.ServerAdmin, alexProfile));
        Assert.Equal("A household needs at least one household administrator. Make another profile a household administrator first.", refused.Message);
        Assert.NotNull(await world.Profiles.GetByIdAsync(alexProfile));
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Alex.Id));
    }

    [Fact]
    public async Task ThePersonDeleting_NeverDeletesTheProfileTheyAreUsing_OrTheOneTheirSignInOpens()
    {
        await using var world = await World.CreateAsync();
        var serverProfile = await DefaultProfileAsync(world, world.ServerAdminAccount);
        var alexProfile = await DefaultProfileAsync(world, world.Alex);

        var server = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.DeleteProfileAsync(world.ServerAdmin, serverProfile));
        var owner = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.DeleteProfileAsync(world.AlexAuthority, alexProfile));
        // Acting as someone else's profile does not make the sign-in's own profile deletable.
        var actingAsKim = world.AlexAuthority with { ActiveProfileId = world.KimProfile.Id };
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.DeleteProfileAsync(actingAsKim, alexProfile));

        Assert.Equal("You can't delete the profile you are using. Switch to another profile first.", server.Message);
        Assert.Equal(server.Message, owner.Message);
        Assert.NotNull(await world.Profiles.GetByIdAsync(serverProfile));
        Assert.NotNull(await world.Profiles.GetByIdAsync(alexProfile));
    }

    [Fact]
    public async Task AHouseholdAdministrator_CannotDeleteAServerAdministratorsProfile_EvenInTheirOwnHousehold()
    {
        await using var world = await World.CreateAsync();
        // A server administrator profile lives in the Library's own (default) household, so Alex's household is the one in question.
        await world.Access.SetDefaultHouseholdAsync(world.Household.Id);
        await world.GivePasswordAsync(world.Kim.Id);
        await world.Mutations.SetProfileRolesAsync(world.ServerAdmin, world.KimProfile.Id, false, true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.DeleteProfileAsync(world.AlexAuthority, world.KimProfile.Id));

        Assert.NotNull(await world.Profiles.GetByIdAsync(world.KimProfile.Id));
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Kim.Id));
    }

    [Fact]
    public async Task AnotherHouseholdsProfile_LooksMissingToAHouseholdAdministrator_AndAMemberCannotDeleteAnyone()
    {
        await using var world = await World.CreateAsync();
        var beaProfile = await DefaultProfileAsync(world, world.Bea);

        var outside = await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.DeleteProfileAsync(world.AlexAuthority, beaProfile));
        var missing = await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.DeleteProfileAsync(world.AlexAuthority, Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.DeleteProfileAsync(world.KimAuthority, world.Mary.Id));

        Assert.Equal(missing.Message, outside.Message);
        Assert.NotNull(await world.Profiles.GetByIdAsync(beaProfile));
        Assert.NotNull(await world.Profiles.GetByIdAsync(world.Mary.Id));
    }

    [Fact]
    public async Task AServerAdministrator_CanDeleteAProfileInAnyHousehold()
    {
        await using var world = await World.CreateAsync();

        await world.Mutations.DeleteProfileAsync(world.ServerAdmin, world.KimProfile.Id);

        Assert.Null(await world.Profiles.GetByIdAsync(world.KimProfile.Id));
        Assert.Null(await world.Accounts.GetByIdAsync(world.Kim.Id));
    }

    [Fact]
    public async Task ADeleteThatIsRefused_MovesNoPhotos()
    {
        await using var world = await World.CreateAsync(secondOwner: false);
        var alexProfile = await DefaultProfileAsync(world, world.Alex);

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.DeleteProfileAsync(world.ServerAdmin, alexProfile));

        Assert.Empty(world.Photos.Prepared);
    }

    [Fact]
    public async Task ChangesToWhoCanOpenWhat_WaitForTheSameGateAsDeletingAUser()
    {
        var gate = new AuthenticationPolicyMutationGate();
        await using var world = await World.CreateAsync(gate: gate);
        using var held = await gate.EnterAsync(CancellationToken.None);

        var profile = world.Mutations.CreateProfileAsync(world.ServerAdmin,
            new CreateManagedProfileCommand(world.Sam.Id, "Sam (Kids)", null, false));
        var person = world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
            new AddHouseholdPersonCommand(world.Household.Id, "Zed", null, false, null));
        var removal = world.Mutations.RemoveOwnSignInAsync(world.ServerAdmin, Guid.NewGuid());
        var deletion = world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Kim.Id);
        var profileDeletion = world.Mutations.DeleteProfileAsync(world.ServerAdmin, world.Mary.Id);
        var access = world.Mutations.ReplaceProfileAccessAsync(world.ServerAdmin, world.KimProfile.Id, new HashSet<AccountFeatureId>(), new HashSet<Guid>());
        var roles = world.Mutations.SetProfileRolesAsync(world.ServerAdmin, world.KimProfile.Id, false, false);
        var creation = world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "late@example.com", null, new NewUserProfileCommand("Late", null, world.Household.Id, null),
            NewUserSignInMethod.Invitation));
        await Task.Delay(150);

        // All of them are queued behind the held gate, so a delete's plan and the delete itself see the same grants.
        Assert.All(new Task[] { profile, person, removal, deletion, profileDeletion, access, roles, creation }, task => Assert.False(task.IsCompleted));

        held.Dispose();
        await profile;
        await person;
        await Assert.ThrowsAsync<KeyNotFoundException>(() => removal);
        await deletion;
        await profileDeletion;
        await access;
        await roles;
        await creation;
    }

    // ---- Profile pictures ----

    [Fact]
    public void ADeletedProfilesPicture_IsRemovedFromTheLibrarysOwnAvatarFolder_AndItsEmptyFolderWithIt()
    {
        using var temp = new TempFolder();
        var avatars = Path.Combine(temp.Path, "profiles");
        var profileId = Guid.NewGuid();
        var folder = Path.Combine(avatars, profileId.ToString("D"));
        Directory.CreateDirectory(folder);
        var picture = Path.Combine(folder, "avatar-1.png");
        File.WriteAllBytes(picture, [1, 2, 3]);

        ProfilePhotoDisposer.DeleteAvatar(picture, avatars, profileId, new CapturingLogger());

        Assert.False(File.Exists(picture));
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(avatars));
    }

    [Fact]
    public void ADeletedProfilesPicture_OutsideTheAvatarFolder_IsNeverTouched()
    {
        using var temp = new TempFolder();
        var avatars = Path.Combine(temp.Path, "profiles");
        Directory.CreateDirectory(avatars);
        var outside = Path.Combine(temp.Path, "family-photo.png");
        File.WriteAllBytes(outside, [1, 2, 3]);
        var sibling = Path.Combine(temp.Path, "profiles-backup", "avatar.png");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.WriteAllBytes(sibling, [1]);
        var traversal = Path.Combine(avatars, "..", "family-photo.png");
        var profileId = Guid.NewGuid();

        ProfilePhotoDisposer.DeleteAvatar(outside, avatars, profileId, new CapturingLogger());
        ProfilePhotoDisposer.DeleteAvatar(sibling, avatars, profileId, new CapturingLogger());
        ProfilePhotoDisposer.DeleteAvatar(traversal, avatars, profileId, new CapturingLogger());
        ProfilePhotoDisposer.DeleteAvatar(null, avatars, profileId, new CapturingLogger());

        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(sibling));
    }

    [Fact]
    public void ADeletedProfilesPicture_InAnotherProfilesFolder_IsNeverDeleted_EvenInsideTheAvatarFolder()
    {
        using var temp = new TempFolder();
        var avatars = Path.Combine(temp.Path, "profiles");
        var other = Path.Combine(avatars, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(other);
        var othersPicture = Path.Combine(other, "avatar.png");
        File.WriteAllBytes(othersPicture, [1]);
        var atTheTop = Path.Combine(avatars, "avatar.png");
        File.WriteAllBytes(atTheTop, [1]);
        var profileId = Guid.NewGuid();
        // The same letters in another case are a different folder on a case-sensitive disk and are refused everywhere.
        var upper = Path.Combine(avatars, profileId.ToString("D").ToUpperInvariant());
        var casePicture = Path.Combine(upper, "avatar.png");
        Directory.CreateDirectory(upper);
        File.WriteAllBytes(casePicture, [1]);

        ProfilePhotoDisposer.DeleteAvatar(othersPicture, avatars, profileId, new CapturingLogger());
        ProfilePhotoDisposer.DeleteAvatar(atTheTop, avatars, profileId, new CapturingLogger());
        ProfilePhotoDisposer.DeleteAvatar(casePicture, avatars, profileId, new CapturingLogger());

        Assert.True(File.Exists(othersPicture));
        Assert.True(File.Exists(atTheTop));
        Assert.True(File.Exists(casePicture));
    }

    [Fact]
    public void ADeletedProfilesPicture_ReachedThroughASymbolicLink_IsNeverDeleted_AndIsLoggedAsAWarning()
    {
        using var temp = new TempFolder();
        var avatars = Path.Combine(temp.Path, "profiles");
        var profileId = Guid.NewGuid();
        var folder = Path.Combine(avatars, profileId.ToString("D"));
        Directory.CreateDirectory(folder);
        var precious = Path.Combine(temp.Path, "family-photo.png");
        File.WriteAllBytes(precious, [1, 2, 3]);
        var linkedFile = Path.Combine(folder, "avatar.png");
        File.CreateSymbolicLink(linkedFile, precious);
        var logger = new CapturingLogger();

        ProfilePhotoDisposer.DeleteAvatar(linkedFile, avatars, profileId, logger);

        Assert.True(File.Exists(precious));
        Assert.True(File.Exists(linkedFile));
        Assert.Equal([LogLevel.Warning], logger.Levels);

        // The profile's own folder being a link to somewhere else is refused as well.
        var elsewhere = Path.Combine(temp.Path, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var target = Path.Combine(elsewhere, "avatar.png");
        File.WriteAllBytes(target, [1]);
        var linkedProfile = Guid.NewGuid();
        Directory.CreateSymbolicLink(Path.Combine(avatars, linkedProfile.ToString("D")), elsewhere);

        ProfilePhotoDisposer.DeleteAvatar(Path.Combine(avatars, linkedProfile.ToString("D"), "avatar.png"), avatars, linkedProfile, new CapturingLogger());

        Assert.True(File.Exists(target));
    }

    [Fact]
    public void ADeletedProfilesPicture_ThatCannotBeDeleted_IsLoggedAsAWarning_NotAnError()
    {
        using var temp = new TempFolder();
        var avatars = Path.Combine(temp.Path, "profiles");
        // A directory where the picture should be: File.Delete refuses it.
        var profileId = Guid.NewGuid();
        var stubborn = Path.Combine(avatars, profileId.ToString("D"), "avatar.png");
        Directory.CreateDirectory(stubborn);
        var logger = new CapturingLogger();

        ProfilePhotoDisposer.DeleteAvatar(stubborn, avatars, profileId, logger);

        Assert.Equal([LogLevel.Warning], logger.Levels);
    }

    /// <summary>The list of profiles the preview shows for a user: what a person confirms before a household goes.</summary>
    private static async Task<Guid[]> ConfirmedAsync(World world, Guid userId) =>
        (await world.Mutations.PreviewDeleteUserAsync(world.ServerAdmin, userId))
            .Plan.HouseholdProfiles.Select(profile => profile.Id).ToArray();

    private static async Task<Guid> DefaultProfileAsync(World world, Account account) =>
        (await world.Accounts.GetDefaultProfileIdAsync(account.Id))!.Value;

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tuvima-avatars-{Guid.NewGuid():N}");

        public TempFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    internal class CapturingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
            Messages.Add(formatter(state, exception));
        }
    }

    internal sealed class CapturingLogger<T> : CapturingLogger, ILogger<T>;

    internal sealed class World : IAsyncDisposable
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-user-deletion-{Guid.NewGuid():N}.db");
        private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima-user-deletion-{Guid.NewGuid():N}");
        private DatabaseConnection _database = null!;
        private ConfigurationDirectoryLoader _configuration = null!;

        public AccountRepository Accounts { get; private set; } = null!;
        public ProfileAccessRepository Access { get; private set; } = null!;
        public ProfileRepository Profiles { get; private set; } = null!;
        public AccountAccessMutationService Mutations { get; private set; } = null!;
        public ConfigurationDirectoryLoader Configuration => _configuration;
        public NoProfilePhotos Photos { get; } = new();
        public CapturingAudit Audit { get; } = new();
        public CapturingLogger<AccountAccessMutationService> Log { get; } = new();
        public Household Household { get; private set; } = null!;
        public HouseholdRepository Households { get; private set; } = null!;
        public HouseholdManagementService Management { get; private set; } = null!;
        public IdentityRepository Identities { get; private set; } = null!;

        public RequestAuthority ServerAdmin { get; private set; } = null!;
        public Account ServerAdminAccount { get; private set; } = null!;

        /// <summary>The household's first main sign-in and owner.</summary>
        public Account Alex { get; private set; } = null!;
        public RequestAuthority AlexAuthority { get; private set; } = null!;

        /// <summary>Another main sign-in of the household ("Sam"), an owner when the world is made with a second owner.</summary>
        public Account Sam => Alex2;
        public Account Alex2 { get; private set; } = null!;
        public RequestAuthority Alex2Authority { get; private set; } = null!;
        public Profile SamProfile { get; private set; } = null!;

        /// <summary>A household person with no sign-in of their own, held by both main sign-ins.</summary>
        public Profile Mary { get; private set; } = null!;

        /// <summary>A plain member (not an owner) with a profile of their own.</summary>
        public Account Kim { get; private set; } = null!;
        public Profile KimProfile { get; private set; } = null!;
        public RequestAuthority KimAuthority { get; private set; } = null!;

        /// <summary>The owner of another household.</summary>
        public Account Bea { get; private set; } = null!;
        public RequestAuthority BeaAuthority { get; private set; } = null!;

        /// <summary>
        /// A world with a server administrator ("Server", in the Library's own household) who has an email and a password, and a
        /// Library that already has several households (Alex's with Sam, Mary and Kim; Bea's).
        /// </summary>
        public static async Task<World> CreateAsync(bool secondOwner = true, AuthenticationPolicyMutationGate? gate = null)
        {
            var world = new World();
            world._database = new DatabaseConnection(world._databasePath);
            world._configuration = new ConfigurationDirectoryLoader(world._configPath);
            world._database.InitializeSchema();
            world.Accounts = new AccountRepository(world._database);
            world.Access = new ProfileAccessRepository(world._database);
            var identities = new IdentityRepository(world._database);
            world.Identities = identities;
            world.Households = new HouseholdRepository(world._database);
            world.Profiles = new ProfileRepository(world._database);
            var identity = new FirstPartyIdentityService(
                identities, world.Accounts, world.Profiles,
                new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
                TimeProvider.System, new ConfigurationAuthenticationPolicyProvider(world._configuration));
            world.Mutations = new AccountAccessMutationService(
                world.Accounts,
                        new ProfileAccessRepository(world._database), identities, world.Profiles, world._configuration,
                new RealDecisions(), new AllowEvaluator(), new NoOpInvalidation(), world.Audit,
                identity, world.Photos, TimeProvider.System, new AlwaysUsableAdministrators(), gate ?? new AuthenticationPolicyMutationGate(), world.Log,
                world.Households);
            var households = world.Households;
            world.Management = new HouseholdManagementService(
                world.Households, world.Accounts, new RealDecisions(), new AllowEvaluator(), world.Audit, TimeProvider.System);

            var bootstrap = ServerAuthority(Guid.NewGuid(), Guid.NewGuid());
            world.ServerAdminAccount = await world.Mutations.CreateAsync(bootstrap, new CreateAccountAccessCommand(
                "server@example.com", IsAdministrator: true, ProfileId: null,
                NewProfile: new NewAccountProfileCommand("Server", "#7C4DFF"),
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read }, Libraries: new HashSet<Guid>(), TemporaryPassword: null));
            world.ServerAdmin = ServerAuthority(world.ServerAdminAccount.Id, (await world.Accounts.GetDefaultProfileIdAsync(world.ServerAdminAccount.Id))!.Value);
            await world.SecureServerAdministratorAsync();

            world.Alex = await world.Mutations.CreateAsync(world.ServerAdmin, new CreateAccountAccessCommand(
                "alex@example.com", IsAdministrator: false, ProfileId: null,
                NewProfile: new NewAccountProfileCommand("Alex", "#112233"),
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read }, Libraries: new HashSet<Guid>(), TemporaryPassword: null));
            world.Household = (await households.GetForAccountAsync(world.Alex.Id))!;
            world.Alex = (await world.Accounts.GetByIdAsync(world.Alex.Id))!;

            // Sam is a household person who is then invited to sign in as themselves; Alex keeps a plain grant on Sam.
            world.SamProfile = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
                new AddHouseholdPersonCommand(world.Household.Id, "Sam", null, false, null));
            var invitedSam = await world.Mutations.IssueInvitationAsync(world.ServerAdmin,
                new IssueAccountInvitationCommand("sam@example.com", [world.SamProfile.Id], null));
            if (secondOwner)
            {
                await world.Mutations.SetHouseholdAdminAsync(world.ServerAdmin, invitedSam.AccountId, true);
            }

            world.Alex2 = (await world.Accounts.GetByIdAsync(invitedSam.AccountId))!;

            // Mary and Kim are added once both main sign-ins exist, so both hold Mary.
            world.Mary = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
                new AddHouseholdPersonCommand(world.Household.Id, "Mary", null, false, null));
            world.KimProfile = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
                new AddHouseholdPersonCommand(world.Household.Id, "Kim", null, false, null));
            var invitedKim = await world.Mutations.IssueInvitationAsync(world.ServerAdmin,
                new IssueAccountInvitationCommand("kim@example.com", [world.KimProfile.Id], null));
            world.Kim = (await world.Accounts.GetByIdAsync(invitedKim.AccountId))!;

            world.Bea = await world.Mutations.CreateAsync(world.ServerAdmin, new CreateAccountAccessCommand(
                "bea@example.com", IsAdministrator: false, ProfileId: null,
                NewProfile: new NewAccountProfileCommand("Bea", "#445566"),
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read }, Libraries: new HashSet<Guid>(), TemporaryPassword: null));
            world.Bea = (await world.Accounts.GetByIdAsync(world.Bea.Id))!;

            world.AlexAuthority = await world.OwnerAuthorityAsync(world.Alex, isOwner: true);
            world.Alex2Authority = await world.OwnerAuthorityAsync(world.Alex2, isOwner: secondOwner);
            world.KimAuthority = await world.OwnerAuthorityAsync(world.Kim, isOwner: false);
            world.BeaAuthority = await world.OwnerAuthorityAsync(world.Bea, isOwner: true);
            return world;
        }

        /// <summary>Gives the server administrator a password, as a Library does before it adds a second household (D10).</summary>
        public Task SecureServerAdministratorAsync() => GivePasswordAsync(ServerAdminAccount.Id);

        /// <summary>Takes the server administrator's password away again, as on a first start that skipped it.</summary>
        public void StripServerAdministratorPassword()
        {
            using var connection = _database.CreateConnection();
            Dapper.SqlMapper.Execute(connection, "DELETE FROM account_credentials WHERE account_id = @account;",
                new { account = ServerAdminAccount.Id });
        }

        /// <summary>Runs a statement against the world's data store.</summary>
        public void Run(string sql, object? parameters = null)
        {
            using var connection = _database.CreateConnection();
            Dapper.SqlMapper.Execute(connection, sql, parameters);
        }

        /// <summary>Gives a user a password (the hash is a stand-in; nothing here signs in).</summary>
        public Task GivePasswordAsync(Guid accountId) => Identities.UpsertAccountCredentialAsync(new AccountCredential
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            SecretHash = "work-factored-hash",
            SecurityStamp = "security-stamp",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        /// <summary>Puts one shared photo in the household's Shared Library in View.</summary>
        public async Task AddSharedPhotoAsync(Guid householdId)
        {
            var shared = await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(householdId);
            using var connection = _database.CreateConnection();
            Dapper.SqlMapper.Execute(connection, """
                INSERT INTO local_items (id, scope_kind, library_id, media_kind, primary_file_name, primary_mime_type, created_at, updated_at)
                VALUES (@id, 'shared', @library, 'image', 'beach.jpg', 'image/jpeg', @now, @now);
                """, new { id = Guid.NewGuid(), library = shared.LibraryId, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        private async Task<RequestAuthority> OwnerAuthorityAsync(Account account, bool isOwner) =>
            new(PrincipalKind.Human, true,
                AccountId: account.Id,
                ActiveProfileId: (await Accounts.GetDefaultProfileIdAsync(account.Id))!.Value,
                SessionId: Guid.NewGuid(),
                AccountEnabled: true,
                ActiveProfileInUserHousehold: true,
                ActiveProfileHouseholdId: account.HouseholdId,
                ActiveProfileIsHouseholdAdministrator: isOwner);

        internal static RequestAuthority ServerAuthority(Guid accountId, Guid profileId) =>
            new(PrincipalKind.Human, true,
                AccountId: accountId,
                ActiveProfileId: profileId,
                SessionId: Guid.NewGuid(),
                AccountEnabled: true,
                ActiveProfileInUserHousehold: true,
                ActiveProfileIsServerAdministrator: true);

        public ValueTask DisposeAsync()
        {
            _configuration.Dispose();
            _database.Dispose();
            TestTemp.ReleaseSqlitePool(_databasePath);
            foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            if (Directory.Exists(_configPath))
            {
                Directory.Delete(_configPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    internal sealed class CapturingAudit : IAuthorizationAuditWriter
    {
        public List<AuthorizationAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(AuthorizationAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RealDecisions : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateHouseholdAdministratorAsync(RequestAuthority authority,
            bool requireSurfaceUnlock, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(authority.IsEffectiveHouseholdAdministrator
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));

        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid libraryId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(authority.IsEffectiveAdministrator
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
    }

    private sealed class AllowEvaluator : IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class NoOpInvalidation : IAuthorizationInvalidationService
    {
        public ValueTask InvalidateAccountAsync(Guid accountId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateGrantAsync(Guid accountId, Guid profileId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateApplicationAsync(Guid applicationId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
