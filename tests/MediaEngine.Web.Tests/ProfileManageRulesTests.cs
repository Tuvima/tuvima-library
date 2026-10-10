using MediaEngine.Web.Components.Profiles;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

/// <summary>Who is offered Manage profiles, Add profile and Delete on the "Who's watching?" picker.</summary>
public sealed class ProfileManageRulesTests
{
    private static ProfileViewModel Person(Guid? id = null, bool kids = false) => new(
        id ?? Guid.NewGuid(), "Maya", "#3B82F6", kids ? "RestrictedProfile" : "StandardUser", DateTimeOffset.UtcNow);

    private static readonly ProfileViewModel Owner = new(
        new Guid("00000000-0000-0000-0000-000000000001"), "Owner", "#8852FC", "Administrator", DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public void Manage_IsForAdministratorsOutsideKidsProfiles(bool kidsActive, bool administrator, bool expected) =>
        Assert.Equal(expected, ProfileManageRules.CanManage(kidsActive, administrator));

    [Theory]
    [InlineData(true, 7, true)]
    [InlineData(true, 8, false)]
    [InlineData(false, 3, false)]
    public void Add_StopsAtEightPeople(bool canManage, int count, bool expected) =>
        Assert.Equal(expected, ProfileManageRules.CanAdd(canManage, count));

    [Fact]
    public void Delete_IsNeverOfferedForTheLastPerson_TheOwner_OrTheOpenProfile()
    {
        var other = Person();
        var open = Person();

        Assert.True(ProfileManageRules.CanDelete(other, open.Id, 3));
        Assert.False(ProfileManageRules.CanDelete(other, open.Id, 1));
        Assert.False(ProfileManageRules.CanDelete(Owner, open.Id, 3));
        Assert.False(ProfileManageRules.CanDelete(open, open.Id, 3));
    }

    [Fact]
    public void ManageMode_IsWiredToTheRules_AndTheEngineCalls()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var page = File.ReadAllText(Path.Combine(repoRoot, "src", "MediaEngine.Web", "Components", "Pages", "ProfilePickerPage.razor"));

        Assert.Contains("ProfileManageRules.CanManage(", page, StringComparison.Ordinal);
        Assert.Contains("ProfileManageRules.CanAdd(", page, StringComparison.Ordinal);
        Assert.Contains("ProfileManageRules.CanDelete(", page, StringComparison.Ordinal);
        Assert.Contains("Identity.AddHouseholdPersonResultAsync(", page, StringComparison.Ordinal);
        Assert.Contains("Identity.UpdateManagedProfileResultAsync(", page, StringComparison.Ordinal);
        Assert.Contains("Identity.DeleteManagedProfileResultAsync(profile.Id, keepPhotos)", page, StringComparison.Ordinal);
        Assert.Contains("Manage profiles", page, StringComparison.Ordinal);
        Assert.DoesNotContain(" style=\"", page, StringComparison.Ordinal);
    }
}
