using MediaEngine.Web.Components.Profiles;

namespace MediaEngine.Web.Tests;

/// <summary>Who is offered Manage profiles and Add profile on the "Who's watching?" picker.</summary>
public sealed class ProfileManageRulesTests
{
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
    public void ManageMode_IsWiredToTheRules_AndTheEngineCalls()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var page = File.ReadAllText(Path.Combine(repoRoot, "src", "MediaEngine.Web", "Components", "Pages", "ProfilePickerPage.razor"));

        Assert.Contains("ProfileManageRules.CanManage(", page, StringComparison.Ordinal);
        Assert.Contains("ProfileManageRules.CanAdd(", page, StringComparison.Ordinal);
        Assert.Contains("ProfileRoleRules.CanDelete(", page, StringComparison.Ordinal);
        Assert.Contains("Identity.AddHouseholdPersonResultAsync(", page, StringComparison.Ordinal);
        Assert.Contains("Identity.UpdateManagedProfileResultAsync(", page, StringComparison.Ordinal);
        Assert.Contains("Identity.DeleteManagedProfileResultAsync(profile.Id, keepPhotos)", page, StringComparison.Ordinal);
        Assert.Contains("Manage profiles", page, StringComparison.Ordinal);
        Assert.DoesNotContain(" style=\"", page, StringComparison.Ordinal);
    }
}
