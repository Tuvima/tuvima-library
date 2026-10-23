using System.Text.Json;
using MediaEngine.Contracts.Backups;
using MediaEngine.Domain.Services;

namespace MediaEngine.Contracts.Tests;

public sealed class UserTransferContractTests
{
    private static readonly Guid Operation = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid MayaProfile = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid MayaSignIn = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000001");
    private static readonly Guid Home = Guid.Parse("cccccccc-0000-4000-8000-000000000001");

    [Fact]
    public void UserImportPreview_UsesTheAgreedSnakeCaseNames_AndRoundTrips()
    {
        var preview = new UserImportPreviewDto(
            Operation,
            "household",
            "The Khans",
            [new UserImportProfileDto("Maya", "StandardUser", null, false, MayaProfile, MayaSignIn, ["read", "watch"]), new UserImportProfileDto("Maya (kids)", "RestrictedProfile", "PG", true)],
            [new UserImportSignInDto(MayaSignIn, "maya@example.com", MayaProfile, true, null)],
            [new UserImportHouseholdDto(Home, "Home", 3)],
            false,
            "Set an email and password for your own sign-in first.",
            8,
            12,
            null,
            true);

        var json = JsonSerializer.Serialize(preview, MediaEngineJson.Web);
        var roundTrip = JsonSerializer.Deserialize<UserImportPreviewDto>(json, MediaEngineJson.Web);

        Assert.Equal(
            "{\"operation_id\":\"11111111-2222-3333-4444-555555555555\",\"scope\":\"household\",\"household_name\":\"The Khans\"," +
            "\"profiles\":[{\"name\":\"Maya\",\"role\":\"StandardUser\",\"content_limit\":null,\"content_limit_allow_unrated\":false," +
            "\"id\":\"aaaaaaaa-0000-4000-8000-000000000001\",\"sign_in_id\":\"bbbbbbbb-0000-4000-8000-000000000001\",\"access\":[\"read\",\"watch\"]}," +
            "{\"name\":\"Maya (kids)\",\"role\":\"RestrictedProfile\",\"content_limit\":\"PG\",\"content_limit_allow_unrated\":true," +
            "\"id\":\"00000000-0000-0000-0000-000000000000\",\"sign_in_id\":null,\"access\":null}]," +
            "\"sign_ins\":[{\"id\":\"bbbbbbbb-0000-4000-8000-000000000001\",\"email\":\"maya@example.com\",\"profile_id\":\"aaaaaaaa-0000-4000-8000-000000000001\",\"email_in_use\":true,\"problem\":null}]," +
            "\"households\":[{\"id\":\"cccccccc-0000-4000-8000-000000000001\",\"name\":\"Home\",\"profiles\":3}]," +
            "\"can_add_household\":false,\"add_household_blocked_reason\":\"Set an email and password for your own sign-in first.\"," +
            "\"max_profiles_per_household\":8,\"personal_items\":12,\"blocked_reason\":null,\"two_step_to_set_up\":true}",
            json);
        Assert.Equal("household", roundTrip!.Scope);
        Assert.Equal(preview.SignIns, roundTrip.SignIns);
        Assert.Equal(preview.Households, roundTrip.Households);
        Assert.Equal(["read", "watch"], roundTrip.Profiles[0].Access);
        Assert.Equal("PG", roundTrip.Profiles[1].ContentLimit);
        Assert.True(roundTrip.Profiles[1].ContentLimitAllowUnrated);
        Assert.False(roundTrip.CanAddHousehold);
        Assert.True(roundTrip.TwoStepToSetUp);
    }

    [Fact]
    public void UserImportPreview_WithoutTwoStepToSetUp_ReadsAsFalse()
    {
        var roundTrip = JsonSerializer.Deserialize<UserImportPreviewDto>(
            "{\"operation_id\":\"11111111-2222-3333-4444-555555555555\",\"scope\":\"profile\",\"household_name\":null,\"profiles\":[],\"sign_ins\":[],\"households\":[]," +
            "\"can_add_household\":true,\"add_household_blocked_reason\":null,\"max_profiles_per_household\":8,\"personal_items\":0,\"blocked_reason\":null}",
            MediaEngineJson.Web);

        Assert.False(roundTrip!.TwoStepToSetUp);
    }

    [Fact]
    public void UserImportRequest_UsesTheAgreedSnakeCaseNames_CarriesNoRoleOrLibraryChoice_AndRoundTrips()
    {
        var request = new UserImportRequestDto(
            null,
            "Our new home",
            MayaProfile,
            true,
            new Dictionary<Guid, string> { [MayaSignIn] = "maya.new@example.com" },
            [Guid.Parse("dddddddd-0000-4000-8000-000000000001")]);

        var json = JsonSerializer.Serialize(request, MediaEngineJson.Web);
        var roundTrip = JsonSerializer.Deserialize<UserImportRequestDto>(json, MediaEngineJson.Web);

        Assert.Equal(
            "{\"target_household_id\":null,\"new_household_name\":\"Our new home\",\"new_household_administrator_profile_id\":\"aaaaaaaa-0000-4000-8000-000000000001\"," +
            "\"keep_access\":true,\"emails\":{\"bbbbbbbb-0000-4000-8000-000000000001\":\"maya.new@example.com\"},\"without_sign_in\":[\"dddddddd-0000-4000-8000-000000000001\"]}",
            json);
        Assert.Equal("maya.new@example.com", roundTrip!.Emails![MayaSignIn]);
        Assert.True(roundTrip.KeepAccess);
        Assert.Equal(MayaProfile, roundTrip.NewHouseholdAdministratorProfileId);
        var names = typeof(UserImportRequestDto).GetProperties().Select(property => property.Name).ToList();
        Assert.DoesNotContain(names, name => name.Contains("Role", StringComparison.Ordinal) || name.Contains("Librar", StringComparison.Ordinal));
    }

    [Fact]
    public void UserImportRequest_WithNothingSet_ReadsAsNoAccessAndNoChoices()
    {
        var roundTrip = JsonSerializer.Deserialize<UserImportRequestDto>("{\"target_household_id\":\"cccccccc-0000-4000-8000-000000000001\"}", MediaEngineJson.Web);

        Assert.Equal(Home, roundTrip!.TargetHouseholdId);
        Assert.False(roundTrip.KeepAccess);
        Assert.Null(roundTrip.Emails);
        Assert.Null(roundTrip.WithoutSignIn);
    }

    [Fact]
    public void UserImportResult_UsesTheAgreedSnakeCaseNames_AndRoundTrips()
    {
        var result = new UserImportResultDto(
            Home,
            "Our new home",
            true,
            [new UserImportedProfileDto(MayaProfile, "Maya", "maya@example.com", true), new UserImportedProfileDto(Guid.Empty, "Kid", null, false)],
            4);

        var json = JsonSerializer.Serialize(result, MediaEngineJson.Web);
        var roundTrip = JsonSerializer.Deserialize<UserImportResultDto>(json, MediaEngineJson.Web);

        Assert.Equal(
            "{\"household_id\":\"cccccccc-0000-4000-8000-000000000001\",\"household_name\":\"Our new home\",\"created_household\":true," +
            "\"profiles\":[{\"id\":\"aaaaaaaa-0000-4000-8000-000000000001\",\"name\":\"Maya\",\"sign_in_email\":\"maya@example.com\",\"is_household_administrator\":true}," +
            "{\"id\":\"00000000-0000-0000-0000-000000000000\",\"name\":\"Kid\",\"sign_in_email\":null,\"is_household_administrator\":false}],\"waiting_items\":4}",
            json);
        Assert.Equal(Home, roundTrip!.HouseholdId);
        Assert.True(roundTrip.Profiles[0].IsHouseholdAdministrator);
        Assert.Null(roundTrip.Profiles[1].SignInEmail);
        Assert.Equal(4, roundTrip.WaitingItems);
    }
}
