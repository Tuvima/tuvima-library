using System.Reflection;
using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Contracts.Tests;

public sealed class AccessContractTests
{
    [Fact]
    public void NativeClientScopesAddExplicitEventConsentWithoutChangingDefaultPlaybackConsent()
    {
        var expected = ApplicationPermissionIds.NativeClient.Concat(ApplicationPermissionIds.NativeClientPhotos)
            .Select(id => id.Value).ToArray();

        Assert.Equal(expected, ClientApiScopes.Consumer);
        Assert.Equal(new[]
        {
            "library.read", "artwork.read", "library.changes.read", "progress.read", "progress.write", "queue.read",
            "queue.write", "playback.read", "playback.write", "downloads.read", "downloads.write", "events.subscribe",
            "view.personal.read", "view.originals.read", "view.galleries.read",
        }, ClientApiScopes.Consumer);
        Assert.Equal(new[] { "view.personal.read", "view.originals.read", "view.galleries.read" }, ClientApiScopes.Photos);
        // Photo access is opt-in on the pairing screen: never a default scope.
        Assert.Empty(ClientApiScopes.Default.Intersect(ClientApiScopes.Photos));
        Assert.Equal(new[] { "library.read", "artwork.read", "progress.read", "progress.write",
            "queue.read", "queue.write", "playback.read", "playback.write" }, ClientApiScopes.Default);

        var request = new PairingDecisionRequest
        {
            UserCode = "ABCD-EFGH",
            Approved = true,
            Scopes = [ClientApiScopes.LibraryRead],
        };
        Assert.Equal(
            "{\"user_code\":\"ABCD-EFGH\",\"approved\":true,\"scopes\":[\"library.read\"]}",
            JsonSerializer.Serialize(request));
    }

    [Fact]
    public void ADecisionThatOmitsScopesIsDistinguishableFromOneThatListsNone()
    {
        var omitted = JsonSerializer.Deserialize<PairingDecisionRequest>("{\"user_code\":\"ABCD-EFGH\",\"approved\":true}")!;
        var none = JsonSerializer.Deserialize<PairingDecisionRequest>("{\"user_code\":\"ABCD-EFGH\",\"approved\":true,\"scopes\":[]}")!;

        Assert.Null(omitted.Scopes);
        Assert.NotNull(none.Scopes);
        Assert.Empty(none.Scopes);
    }

    [Fact]
    public void ThePairingReviewSaysWhetherPhotoScopesCanBeOfferedAndDefaultsToNo()
    {
        var absent = JsonSerializer.Deserialize<PairingReviewResponse>("{\"client_id\":\"tuvima-mobile\"}")!;
        Assert.False(absent.OffersPhotoScopes);

        var offered = JsonSerializer.Serialize(new PairingReviewResponse { OffersPhotoScopes = true });
        Assert.Contains("\"offers_photo_scopes\":true", offered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeletionPreviewNamesTheProfilesThatWouldGo_UnderStableWireNames()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var json = JsonSerializer.Serialize(new UserDeletionPreviewResponse(
            Guid.Empty, null, null, true, "Sam's household", [new AccountDeletionProfileDto(id, "Sam")], null));

        using var document = JsonDocument.Parse(json);
        var profiles = document.RootElement.GetProperty("household_profiles");
        Assert.Equal("{\"id\":\"11111111-1111-1111-1111-111111111111\",\"display_name\":\"Sam\"}", profiles[0].GetRawText());
        Assert.Equal("Sam", JsonSerializer.Deserialize<UserDeletionPreviewResponse>(json)!.HouseholdProfiles.Single().DisplayName);
    }

    [Fact]
    public void StartingPasswordRequestsDefaultToATemporaryPassword()
    {
        var set = JsonSerializer.Deserialize<SetTemporaryPasswordRequest>("{\"temporary_password\":\"a long password here\"}")!;
        Assert.True(set.MustChangePassword);

        var explicitOff = JsonSerializer.Deserialize<SetTemporaryPasswordRequest>(
            "{\"temporary_password\":\"a long password here\",\"must_change_password\":false}")!;
        Assert.False(explicitOff.MustChangePassword);
        Assert.Equal("{\"email\":\"new@home.test\"}", JsonSerializer.Serialize(new ChangeAccountEmailRequest("new@home.test")));
    }

    [Fact]
    public void ManagedProfileCarriesItsUserWithoutAnySecret()
    {
        var userId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var profile = new ManagedProfileResponse(Guid.Empty, "Sam", "#112233", null, DateTimeOffset.UnixEpoch,
            User: new ManagedProfileUserDto(userId, "sam@home.test"));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(profile));
        var user = document.RootElement.GetProperty("user");
        Assert.Equal(userId, user.GetProperty("id").GetGuid());
        Assert.Equal("sam@home.test", user.GetProperty("email").GetString());
        Assert.False(document.RootElement.TryGetProperty("own_sign_in", out _));
        Assert.DoesNotContain(typeof(ManagedProfileUserDto).GetProperties(), property =>
            property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) && property.PropertyType == typeof(string));

        var without = JsonSerializer.Deserialize<ManagedProfileResponse>(
            "{\"id\":\"00000000-0000-0000-0000-000000000000\",\"display_name\":\"Sam\",\"avatar_color\":\"#112233\",\"avatar_path\":null,\"created_at\":\"1970-01-01T00:00:00+00:00\"}")!;
        Assert.Null(without.User);
    }

    [Fact]
    public void AccessResponsesContainNoSecretOrHashProperties()
    {
        var responseTypes = new[]
        {
            typeof(UserSummaryResponse), typeof(AccountProfileGrantDto), typeof(ProfileAdminProtectionResponse),
            typeof(AccountSelfServiceResponse), typeof(ApplicationResponse),
            typeof(ApplicationCredentialResponse), typeof(ApplicationPermissionDefinitionDto),
            typeof(AuthorizationAuditEntryResponse),
        };

        foreach (var type in responseTypes)
        {
            Assert.DoesNotContain(type.GetProperties(BindingFlags.Instance | BindingFlags.Public), property =>
                property.Name.Contains("Hash", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Plaintext", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void PlaintextCredentialExistsOnlyOnTheDedicatedIssueResponse()
    {
        var accessTypes = typeof(ApplicationResponse).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == typeof(ApplicationResponse).Namespace)
            .ToArray();
        var plaintextProperties = accessTypes
            .SelectMany(type => type.GetProperties().Select(property => (Type: type, Property: property)))
            .Where(entry => entry.Property.Name.Contains("PlaintextCredential", StringComparison.Ordinal))
            .ToArray();

        var entry = Assert.Single(plaintextProperties);
        Assert.Equal(typeof(ApplicationCredentialIssuedResponse), entry.Type);
        Assert.Equal(typeof(string), entry.Property.PropertyType);
    }

    [Fact]
    public void ApplicationTypeAndEventEnvelopeUseStableJsonNames()
    {
        Assert.Equal("\"ServerIntegration\"", JsonSerializer.Serialize(ApplicationTypeDto.ServerIntegration));

        using var payload = JsonDocument.Parse("{\"state\":\"healthy\"}");
        var envelope = new ApplicationEventEnvelope(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "system.health_changed",
            1,
            DateTimeOffset.Parse("2026-09-08T12:00:00Z"),
            "server-1",
            new ApplicationEventSubjectDto("server", "server-1", null, null),
            payload.RootElement.Clone());

        var json = JsonSerializer.Serialize(envelope);
        Assert.Contains("\"event_id\":", json, StringComparison.Ordinal);
        Assert.Contains("\"event_type\":\"system.health_changed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"occurred_at\":", json, StringComparison.Ordinal);
        Assert.Contains("\"server_id\":\"server-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"payload\":{\"state\":\"healthy\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileAdminProtection_KeepsItsWireNames_AndNeverCarriesAPin()
    {
        var json = JsonSerializer.Serialize(new ProfileAdminProtectionResponse(true, "FixedDuration", 30, 4));

        Assert.Equal("{\"enabled\":true,\"unlock_mode\":\"FixedDuration\",\"unlock_minutes\":30,\"protection_version\":4}", json);
        var request = JsonSerializer.Deserialize<SetProfileAdminProtectionRequest>(
            "{\"enabled\":false,\"unlock_mode\":\"LockOnLeave\",\"unlock_minutes\":null}")!;
        Assert.False(request.Enabled);
        Assert.Equal("LockOnLeave", request.UnlockMode);
        Assert.Null(request.UnlockMinutes);
    }

    [Fact]
    public void TemporaryPasswordResponse_CarriesTheUserAndWhenThePasswordStopsWorking()
    {
        var userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var expires = DateTimeOffset.Parse("2026-10-30T12:00:00Z");

        var json = JsonSerializer.Serialize(new UserTemporaryPasswordResponse(
            new UserSummaryResponse(userId, "mom@home.test", null, null, null, null, true, false, true, false), expires));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(userId, document.RootElement.GetProperty("user").GetProperty("id").GetGuid());
        Assert.Equal(expires, document.RootElement.GetProperty("temporary_password_expires_at").GetDateTimeOffset());
        Assert.DoesNotContain("password\":\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void UserSummary_KeepsItsWireNames_AndNeverCarriesASecret()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var profile = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var household = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var json = JsonSerializer.Serialize(new UserSummaryResponse(
            id, "mom@home.test", profile, "Mom", household, "Household 1", true, false, true, false));

        Assert.Equal(
            "{\"id\":\"11111111-1111-1111-1111-111111111111\",\"email\":\"mom@home.test\"," +
            "\"profile_id\":\"22222222-2222-2222-2222-222222222222\",\"profile_name\":\"Mom\"," +
            "\"household_id\":\"33333333-3333-3333-3333-333333333333\",\"household_name\":\"Household 1\"," +
            "\"has_password\":true,\"has_two_step\":false,\"is_enabled\":true,\"this_computer_only\":false}",
            json);
        Assert.DoesNotContain("password\":\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateUserRequest_ReadsTheNewProfileChoices_AndKidsIsOffByDefault()
    {
        var request = JsonSerializer.Deserialize<CreateUserRequest>(
            "{\"email\":\"pat@home.test\",\"method\":\"invitation\",\"new_profile\":{\"name\":\"Pat\",\"new_household_name\":\"Household 2\"}}")!;

        Assert.Equal("invitation", request.Method);
        Assert.Null(request.ProfileId);
        Assert.Null(request.TemporaryPassword);
        Assert.Equal("Pat", request.NewProfile!.Name);
        Assert.Equal("Household 2", request.NewProfile.NewHouseholdName);
        Assert.Null(request.NewProfile.HouseholdId);
        Assert.False(request.NewProfile.IsKids);
    }

    [Fact]
    public void CreateUserResponse_CarriesTheUserAndItsInvitation_UnderStableWireNames()
    {
        var user = new UserSummaryResponse(Guid.Empty, "pat@home.test", null, null, null, null, false, false, true, false);

        var json = JsonSerializer.Serialize(new CreateUserResponse(
            user, new AccountInvitationResponse(Guid.Empty, "ABCDE-FGHIJ", DateTimeOffset.UnixEpoch), null));

        Assert.Contains("\"user\":{", json, StringComparison.Ordinal);
        Assert.Contains("\"invitation\":{\"account_id\":", json, StringComparison.Ordinal);
        Assert.Contains("\"temporary_password_expires_at\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUserDeletionPreview_NamesTheHouseholdProfilesAndAnyRefusal_UnderStableWireNames()
    {
        var user = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var pat = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var json = JsonSerializer.Serialize(new UserDeletionPreviewResponse(
            user, "pat@home.test", "Pat", true, "Household 2", [new AccountDeletionProfileDto(pat, "Pat")], null, true));

        Assert.Equal(
            "{\"user_id\":\"11111111-1111-1111-1111-111111111111\",\"email\":\"pat@home.test\",\"profile_name\":\"Pat\"," +
            "\"removes_household\":true,\"household_name\":\"Household 2\"," +
            "\"household_profiles\":[{\"id\":\"22222222-2222-2222-2222-222222222222\",\"display_name\":\"Pat\"}]," +
            "\"refused_reason\":null,\"has_personal_photos\":true}",
            json);
        Assert.Equal("Pat", JsonSerializer.Deserialize<UserDeletionPreviewResponse>(json)!.HouseholdProfiles.Single().DisplayName);
    }

    [Fact]
    public void TheProfileList_AddsRolesAccessAndUserWithoutChangingItsOlderMembers()
    {
        var older = JsonSerializer.Deserialize<ManagedProfileResponse>(
            "{\"id\":\"11111111-1111-1111-1111-111111111111\",\"display_name\":\"Sam\",\"avatar_color\":\"#7C4DFF\",\"avatar_path\":null,\"created_at\":\"2026-01-01T00:00:00+00:00\"}")!;
        Assert.False(older.IsServerAdmin);
        Assert.False(older.IsHouseholdAdmin);
        Assert.Null(older.FeatureIds);
        Assert.Null(older.LibraryIds);
        Assert.Null(older.User);

        var userId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var library = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var json = JsonSerializer.Serialize(older with
        {
            IsServerAdmin = true,
            IsHouseholdAdmin = true,
            FeatureIds = ["read", "watch"],
            LibraryIds = [library],
            User = new ManagedProfileUserDto(userId, "sam@home.test"),
        });

        Assert.Contains("\"is_server_admin\":true,\"is_household_admin\":true,\"feature_ids\":[\"read\",\"watch\"]," +
            "\"library_ids\":[\"33333333-3333-3333-3333-333333333333\"]," +
            "\"user\":{\"id\":\"22222222-2222-2222-2222-222222222222\",\"email\":\"sam@home.test\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileAccessAndRoleRequests_KeepTheirWireNames()
    {
        var library = Guid.Parse("33333333-3333-3333-3333-333333333333");

        Assert.Equal(
            "{\"feature_ids\":[\"read\"],\"library_ids\":[\"33333333-3333-3333-3333-333333333333\"]}",
            JsonSerializer.Serialize(new ReplaceProfileAccessRequest(["read"], [library])));
        Assert.Equal(
            "{\"is_household_admin\":true,\"is_server_admin\":false}",
            JsonSerializer.Serialize(new SetProfileRolesRequest(true, false)));
        Assert.True(JsonSerializer.Deserialize<SetProfileRolesRequest>("{\"is_household_admin\":true,\"is_server_admin\":false}")!.IsHouseholdAdmin);
    }
}
