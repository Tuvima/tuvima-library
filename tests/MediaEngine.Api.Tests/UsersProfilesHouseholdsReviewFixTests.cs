using System.Security.Claims;
using System.Text;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Security;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using World = MediaEngine.Api.Tests.UserDeletionTests.World;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The security review fixes for the Users, Profiles and Households actions: the server administrator rule on the older routes (D17),
/// a household removal that always needs the confirmed list (D6), no quiet promotion to household administrator (D15), one meaning of
/// "a household administrator who counts" (D7), a household removal that follows each profile's household, the lists a profile's
/// access needs, the recent sign-in check before anything is read, and the users list gated inside the service. Real temporary
/// data store; the same scene as <see cref="UserDeletionTests"/>.
/// </summary>
public sealed class UsersProfilesHouseholdsReviewFixTests
{
    private const string Password = "a long temporary password";

    // ---- D17 on the older routes ----

    [Fact]
    public async Task TheOlderUpdateRoute_RefusesAServerAdministratorWithoutAnEmailAndPassword_AndAllowsOneThatHasThem()
    {
        await using var world = await World.CreateAsync();
        var partner = await PartnerWithoutPasswordAsync(world);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.UpdateAsync(
            world.ServerAdmin, partner.User.Id, new UpdateAccountAccessCommand(partner.User.Email, true, true)));

        Assert.Equal(HouseholdRules.ServerAdministratorNeedsSecuredSignIn, refused.Message);
        Assert.False((await world.Access.GetAdminFlagsAsync(partner.Profile.Id))!.IsServerAdministrator);

        await world.GivePasswordAsync(partner.User.Id);
        await world.Mutations.UpdateAsync(world.ServerAdmin, partner.User.Id, new UpdateAccountAccessCommand(partner.User.Email, true, true));

        Assert.True((await world.Access.GetAdminFlagsAsync(partner.Profile.Id))!.IsServerAdministrator);
    }

    [Fact]
    public async Task TheOlderUpdateRoute_StillRefusesAKidsProfile_AndAProfileOutsideTheLibrarysHousehold()
    {
        await using var world = await World.CreateAsync();
        var home = (await world.Households.GetForAccountAsync(world.ServerAdminAccount.Id))!;
        var little = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
            new AddHouseholdPersonCommand(home.Id, "Little", null, true, null));
        var kidsUser = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "little@example.com", little.Id, null, NewUserSignInMethod.TemporaryPassword, Password));

        var kids = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.UpdateAsync(
            world.ServerAdmin, kidsUser.User.Id, new UpdateAccountAccessCommand(kidsUser.User.Email, true, true)));
        var elsewhere = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.UpdateAsync(
            world.ServerAdmin, world.Kim.Id, new UpdateAccountAccessCommand(world.Kim.Email, true, true)));

        Assert.Equal("Child profiles can't be administrators.", kids.Message);
        Assert.Equal("A server administrator must belong to the Library's own household.", elsewhere.Message);
    }

    [Fact]
    public async Task TheOlderCreateRoute_RefusesAServerAdministratorWithoutAPassword_AndAllowsOneWithAStartingPassword()
    {
        await using var world = await World.CreateAsync();
        var profilesBefore = (await world.Profiles.GetAllAsync()).Count;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.CreateAsync(world.ServerAdmin,
            new CreateAccountAccessCommand("second@example.com", IsAdministrator: true, ProfileId: null,
                NewProfile: new NewAccountProfileCommand("Second", null),
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read }, Libraries: new HashSet<Guid>())));

        Assert.Equal(HouseholdRules.ServerAdministratorNeedsSecuredSignIn, refused.Message);
        Assert.Null(await world.Accounts.GetByNormalizedEmailAsync("SECOND@EXAMPLE.COM"));
        Assert.Equal(profilesBefore, (await world.Profiles.GetAllAsync()).Count);

        var allowed = await world.Mutations.CreateAsync(world.ServerAdmin,
            new CreateAccountAccessCommand("second@example.com", IsAdministrator: true, ProfileId: null,
                NewProfile: new NewAccountProfileCommand("Second", null),
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read }, Libraries: new HashSet<Guid>(),
                TemporaryPassword: Password));
        Assert.True((await world.Access.GetAdminFlagsAsync(allowed.ProfileId!.Value))!.IsServerAdministrator);
    }

    [Fact]
    public async Task TheOlderCreateRoute_RefusesAKidsProfileAsAServerAdministrator()
    {
        await using var world = await World.CreateAsync();
        var home = (await world.Households.GetForAccountAsync(world.ServerAdminAccount.Id))!;
        var little = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
            new AddHouseholdPersonCommand(home.Id, "Little", null, true, null));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.CreateAsync(world.ServerAdmin,
            new CreateAccountAccessCommand("little@example.com", IsAdministrator: true, ProfileId: little.Id, NewProfile: null,
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read }, Libraries: new HashSet<Guid>(),
                TemporaryPassword: Password)));

        Assert.Equal("Child profiles can't be administrators.", refused.Message);
        Assert.Null(await world.Accounts.GetByNormalizedEmailAsync("LITTLE@EXAMPLE.COM"));
    }

    // ---- A household removal always needs the confirmed list ----

    [Fact]
    public async Task ADeleteSentWithoutTheList_ThatTurnedIntoAHouseholdRemoval_IsRefusedAndRemovesNothing()
    {
        await using var world = await World.CreateAsync();
        // The preview showed a sign-in-only delete: Sam also looks after the household.
        var preview = await world.Mutations.PreviewDeleteUserAsync(world.ServerAdmin, world.Alex.Id);
        Assert.False(preview.Plan.RemovesHousehold);
        // Meanwhile another administrator deletes Sam's user, so Alex is now the last one.
        await world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Sam.Id);

        var refused = await Assert.ThrowsAsync<AccountRemovalChangedException>(
            () => world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Alex.Id));
        var emptyList = await Assert.ThrowsAsync<AccountRemovalChangedException>(
            () => world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Alex.Id, ProfilePhotoDisposition.MoveToShared, []));

        Assert.Equal("Something changed since you looked. Check the list and try again.", refused.Message);
        Assert.Equal(refused.Message, emptyList.Message);
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Alex.Id));
        Assert.NotNull(await world.Households.GetByIdAsync(world.Household.Id));
        Assert.Empty(world.Photos.Prepared);
    }

    [Fact]
    public async Task TheDataStore_RefusesAHouseholdRemovalThatWasNeverConfirmed()
    {
        await using var world = await World.CreateAsync(secondOwner: false);

        await Assert.ThrowsAsync<AccountRemovalChangedException>(() => world.Accounts.DeleteUserAsync(world.Alex.Id, null));

        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Alex.Id));
        Assert.NotNull(await world.Households.GetByIdAsync(world.Household.Id));
    }

    [Theory]
    [InlineData("/access/users/{userId:guid}", "userId")]
    [InlineData("/access/accounts/{accountId:guid}", "accountId")]
    public async Task TheDeleteRoutes_AnswerConflict_ForAHouseholdRemovalSentWithoutTheList(string route, string parameter)
    {
        await using var world = await World.CreateAsync(secondOwner: false);
        await using var host = EndpointHost.Create(world);

        var refused = await host.InvokeAsync(route, "DELETE", new Dictionary<string, object> { [parameter] = world.Alex.Id });
        var confirmed = (await world.Mutations.PreviewDeleteUserAsync(world.ServerAdmin, world.Alex.Id))
            .Plan.HouseholdProfiles.Select(profile => profile.Id.ToString("D"));
        var allowed = await host.InvokeAsync(route, "DELETE", new Dictionary<string, object> { [parameter] = world.Alex.Id },
            query: "?profiles=" + string.Join(',', confirmed));

        Assert.Equal(StatusCodes.Status409Conflict, refused.Status);
        Assert.Contains("Something changed since you looked", refused.Body, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status204NoContent, allowed.Status);
        Assert.Null(await world.Households.GetByIdAsync(world.Household.Id));
    }

    [Fact]
    public async Task DeletingAUser_ChecksTheRecentSignInBeforeReadingAnythingElse()
    {
        await using var world = await World.CreateAsync();
        await using var host = EndpointHost.Create(world);

        // A person whose sign-in is not recent learns nothing from how the rest of the request reads.
        var stale = await host.InvokeAsync("/access/users/{userId:guid}", "DELETE",
            new Dictionary<string, object> { ["userId"] = world.Kim.Id },
            query: "?photos=nonsense&profiles=not-a-list", human: true);

        Assert.Equal(StatusCodes.Status403Forbidden, stale.Status);
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Kim.Id));
    }

    // ---- No quiet promotion ----

    [Fact]
    public async Task APersonJoiningAHouseholdThatHasNoAdministrator_IsNeverQuietlyPromoted()
    {
        await using var world = await World.CreateAsync();
        world.Run("""
            UPDATE profiles SET is_household_admin = 0 WHERE household_id = @household;
            UPDATE households SET primary_account_id = NULL WHERE id = @household;
            """, new { household = world.Household.Id });

        var made = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "new.person@example.com", null, new NewUserProfileCommand("New Person", null, world.Household.Id, null),
            NewUserSignInMethod.Invitation));
        var person = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
            new AddHouseholdPersonCommand(world.Household.Id, "Older Person", null, false, null));
        var attached = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "older.person@example.com", person.Id, null, NewUserSignInMethod.TemporaryPassword, Password));
        var invited = await world.Mutations.IssueInvitationAsync(world.ServerAdmin, new IssueAccountInvitationCommand(
            "invited.person@example.com", [(await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
                new AddHouseholdPersonCommand(world.Household.Id, "Invited", null, false, null))).Id], null));

        foreach (var profileId in new[] { made.Profile.Id, attached.Profile.Id,
                     (await world.Accounts.GetDefaultProfileIdAsync(invited.AccountId))!.Value })
        {
            Assert.False((await world.Access.GetAdminFlagsAsync(profileId))!.IsHouseholdAdministrator);
        }

        Assert.False((await world.Accounts.GetByIdAsync(made.User.Id))!.HouseholdAdmin);
        Assert.DoesNotContain(await world.Profiles.GetAllAsync(), profile => profile.HouseholdId == world.Household.Id && profile.IsHouseholdAdministrator);
    }

    [Fact]
    public async Task ThePersonWhoStartsAHousehold_ByInvitationOrByUser_IsItsFirstHouseholdAdministrator()
    {
        await using var world = await World.CreateAsync();

        var byUser = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "first.one@example.com", null, new NewUserProfileCommand("First One", null, null, "Household 2"), NewUserSignInMethod.Invitation));
        var byInvitation = await world.Mutations.IssueInvitationAsync(world.ServerAdmin, new IssueAccountInvitationCommand(
            "first.two@example.com", [], null, "First Two"));

        Assert.True((await world.Access.GetAdminFlagsAsync(byUser.Profile.Id))!.IsHouseholdAdministrator);
        Assert.True((await world.Access.GetAdminFlagsAsync(
            (await world.Accounts.GetDefaultProfileIdAsync(byInvitation.AccountId))!.Value))!.IsHouseholdAdministrator);
        // The next person who joins that household is an ordinary member.
        var joined = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "joined@example.com", null, new NewUserProfileCommand("Joined", null, byUser.Profile.HouseholdId, null), NewUserSignInMethod.Invitation));
        Assert.False((await world.Access.GetAdminFlagsAsync(joined.Profile.Id))!.IsHouseholdAdministrator);
    }

    // ---- One meaning of "a household administrator who counts" ----

    [Fact]
    public async Task WithTheOtherHouseholdAdministratorsUserDisabled_TheLastOneCannotBeTurnedOffDeletedOrDisabled()
    {
        await using var world = await World.CreateAsync();
        var alexProfile = (await world.Accounts.GetDefaultProfileIdAsync(world.Alex.Id))!.Value;
        var samProfile = (await world.Accounts.GetDefaultProfileIdAsync(world.Sam.Id))!.Value;
        // Alice (Alex) is enabled; Carol (Sam) is switched off. Both are household administrators.
        await world.Mutations.UpdateAsync(world.ServerAdmin, world.Sam.Id, new UpdateAccountAccessCommand(world.Sam.Email, false, false));
        Assert.True((await world.Access.GetAdminFlagsAsync(samProfile))!.IsHouseholdAdministrator);

        var turnOff = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Mutations.SetProfileRolesAsync(world.ServerAdmin, alexProfile, false, false));
        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Mutations.DeleteProfileAsync(world.ServerAdmin, alexProfile));
        var disable = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Mutations.UpdateAsync(world.ServerAdmin, world.Alex.Id, new UpdateAccountAccessCommand(world.Alex.Email, false, false)));
        var store = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Access.SetHouseholdAdministratorAsync(alexProfile, false, DateTimeOffset.UtcNow));

        Assert.All(new[] { turnOff, delete, disable, store }, refusal =>
            Assert.Equal(UserRemovalMessages.LastHouseholdAdministrator, refusal.Message));
        Assert.True((await world.Access.GetAdminFlagsAsync(alexProfile))!.IsHouseholdAdministrator);
        Assert.NotNull(await world.Profiles.GetByIdAsync(alexProfile));
        Assert.True((await world.Accounts.GetByIdAsync(world.Alex.Id))!.IsEnabled);
    }

    [Fact]
    public async Task WithBothHouseholdAdministratorsEnabled_OneCanBeTurnedOff_ButNotTheOtherAfterIt()
    {
        await using var world = await World.CreateAsync();
        var alexProfile = (await world.Accounts.GetDefaultProfileIdAsync(world.Alex.Id))!.Value;
        var samProfile = (await world.Accounts.GetDefaultProfileIdAsync(world.Sam.Id))!.Value;

        await world.Mutations.SetProfileRolesAsync(world.ServerAdmin, samProfile, false, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Mutations.SetProfileRolesAsync(world.ServerAdmin, alexProfile, false, false));
    }

    [Fact]
    public async Task TheHouseholdRemovalPlan_DoesNotCountAHouseholdAdministratorWhoseUserIsDisabled()
    {
        await using var world = await World.CreateAsync();
        await world.Mutations.UpdateAsync(world.ServerAdmin, world.Sam.Id, new UpdateAccountAccessCommand(world.Sam.Email, false, false));

        var plan = (await world.Mutations.PreviewDeleteUserAsync(world.ServerAdmin, world.Alex.Id)).Plan;

        Assert.True(plan.RemovesHousehold);
    }

    // ---- A household removal follows each profile's household ----

    [Fact]
    public async Task AHouseholdRemoval_TakesTheUsersOfItsProfiles_AndNeverAUserOnlyBecauseOfAnOutOfDateColumn()
    {
        await using var world = await World.CreateAsync(secondOwner: false);
        // Bea's own column wrongly names Alex's household; Kim's profile is still in it. Kim's user has lost its profile link and
        // is matched by the column alone.
        world.Run("UPDATE accounts SET household_id = @household WHERE id = @bea;",
            new { household = world.Household.Id, bea = world.Bea.Id });
        var beaHousehold = world.Bea.HouseholdId!.Value;
        var confirmed = (await world.Mutations.PreviewDeleteUserAsync(world.ServerAdmin, world.Alex.Id))
            .Plan.HouseholdProfiles.Select(profile => profile.Id).ToArray();

        await world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Alex.Id, ProfilePhotoDisposition.MoveToShared, confirmed);

        Assert.Null(await world.Households.GetByIdAsync(world.Household.Id));
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Bea.Id));
        Assert.NotNull(await world.Households.GetByIdAsync(beaHousehold));
        Assert.Null(await world.Accounts.GetByIdAsync(world.Kim.Id));
    }

    [Fact]
    public async Task AHouseholdRemoval_AlsoTakesAUserWithNoProfile_WhoseColumnNamesTheHousehold()
    {
        await using var world = await World.CreateAsync(secondOwner: false);
        world.Run("UPDATE accounts SET profile_id = NULL, household_id = @household WHERE id = @kim;",
            new { household = world.Household.Id, kim = world.Kim.Id });
        var confirmed = (await world.Mutations.PreviewDeleteUserAsync(world.ServerAdmin, world.Alex.Id))
            .Plan.HouseholdProfiles.Select(profile => profile.Id).ToArray();

        await world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Alex.Id, ProfilePhotoDisposition.MoveToShared, confirmed);

        Assert.Null(await world.Accounts.GetByIdAsync(world.Kim.Id));
        Assert.Null(await world.Households.GetByIdAsync(world.Household.Id));
    }

    // ---- A household made with its first user ----

    [Fact]
    public async Task ANewHousehold_IsMadeInTheSameStepAsItsUser_SoAFailureLeavesNoEmptyHouseholdBehind()
    {
        await using var world = await World.CreateAsync();
        var householdsBefore = (await world.Households.ListSummariesAsync()).Count;
        var taken = world.Kim.Email!;
        var now = DateTimeOffset.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = taken,
            NormalizedEmail = taken.ToUpperInvariant(),
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var profile = new Profile { Id = Guid.NewGuid(), DisplayName = "Dup", AvatarColor = "#7C4DFF", CreatedAt = now };
        var grant = new AccountProfileGrant
        {
            AccountId = account.Id,
            ProfileId = profile.Id,
            IsDefault = true,
            IsEnabled = true,
            AuthorizationVersion = 1,
            GrantedAt = now,
        };

        // The same email is already a user, so the step fails after the household would have been made.
        await Assert.ThrowsAnyAsync<Exception>(() => world.Accounts.CreateAccountAsync(
            account, grant, new HashSet<AccountFeatureId>(), new HashSet<Guid>(), default, profile, newHouseholdName: "Left Behind"));

        Assert.Equal(householdsBefore, (await world.Households.ListSummariesAsync()).Count);
        Assert.Null(await world.Profiles.GetByIdAsync(profile.Id));
    }

    [Fact]
    public async Task ANewHouseholdsName_IsKeptExactlyAsChosen()
    {
        await using var world = await World.CreateAsync();

        var created = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "named@example.com", null, new NewUserProfileCommand("Named", null, null, "  The Smiths  "), NewUserSignInMethod.Invitation));

        Assert.Equal("The Smiths", (await world.Households.GetByIdAsync(created.Profile.HouseholdId!.Value))!.Name);
    }

    // ---- The users list is gated inside the service ----

    [Fact]
    public async Task TheUsersList_IsForAnUnlockedServerAdministratorOrATrustedApplication_AndNobodyElse()
    {
        await using var world = await World.CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.ListUsersAsync(world.AlexAuthority));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.ListUsersAsync(world.KimAuthority));
        var all = await world.Mutations.ListUsersAsync(world.ServerAdmin);
        var application = world.ServerAdmin with
        {
            PrincipalKind = PrincipalKind.ServiceApplication,
            AccountId = null,
            ActiveProfileId = null,
            ActiveProfileIsServerAdministrator = false,
        };

        Assert.Contains(all, user => user.Id == world.Kim.Id);
        Assert.Equal(all.Count, (await world.Mutations.ListUsersAsync(application)).Count);
    }

    // ---- What a profile's access needs ----

    [Fact]
    public async Task ReplacingAProfilesAccess_WithoutBothLists_AnswersABadRequest_NotAServerError()
    {
        await using var world = await World.CreateAsync();
        await using var host = EndpointHost.Create(world);
        var route = new Dictionary<string, object> { ["profileId"] = world.KimProfile.Id };

        var noFeatures = await host.InvokeAsync("/access/profiles/{profileId:guid}/access", "PUT", route, body: """{"library_ids":[]}""");
        var noLibraries = await host.InvokeAsync("/access/profiles/{profileId:guid}/access", "PUT", route, body: """{"feature_ids":["read"]}""");
        var neither = await host.InvokeAsync("/access/profiles/{profileId:guid}/access", "PUT", route, body: "{}");
        var both = await host.InvokeAsync("/access/profiles/{profileId:guid}/access", "PUT", route,
            body: """{"feature_ids":["read","watch"],"library_ids":[]}""");

        Assert.All(new[] { noFeatures, noLibraries, neither }, answer =>
        {
            Assert.Equal(StatusCodes.Status400BadRequest, answer.Status);
            Assert.Contains("feature_ids", answer.Body, StringComparison.Ordinal);
        });
        Assert.Equal(StatusCodes.Status204NoContent, both.Status);
        Assert.Equal(["read", "watch"], (await world.Access.GetFeatureGrantsAsync(world.KimProfile.Id)).Select(feature => feature.Value).Order().ToArray());
    }

    // ---- Helpers ----

    /// <summary>A person in the Library's own household whose user has an email and no password yet.</summary>
    private static async Task<CreatedUser> PartnerWithoutPasswordAsync(World world)
    {
        var home = (await world.Households.GetForAccountAsync(world.ServerAdminAccount.Id))!;
        var partner = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
            new AddHouseholdPersonCommand(home.Id, "Partner", null, false, null));
        return await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "partner@example.com", partner.Id, null, NewUserSignInMethod.Invitation));
    }

    /// <summary>Calls a mapped Engine action directly, as the framework would, with the world's service as the one that answers.</summary>
    private sealed class EndpointHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly RouteEndpoint[] _endpoints;

        private EndpointHost(WebApplication app)
        {
            _app = app;
            _endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        }

        public static EndpointHost Create(World world)
        {
            var builder = WebApplication.CreateBuilder();
            AccountEndpointContractTests.AddEndpointServices(builder.Services);
            builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedResolver(world.ServerAdmin));
            builder.Services.AddSingleton(world.Mutations);
            builder.Services.AddSingleton<IAccountAccessMutationService>(world.Mutations);
            builder.Services.AddSingleton(new RecentSignInGuard(null!));

            var app = builder.Build();
            app.MapAccountEndpoints();
            return new EndpointHost(app);
        }

        public async Task<(int Status, string Body)> InvokeAsync(
            string route, string method, IDictionary<string, object> routeValues, string query = "", string? body = null, bool human = false)
        {
            var endpoint = Assert.Single(_endpoints, candidate => candidate.RoutePattern.RawText == route &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));
            using var scope = _app.Services.CreateScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Method = method;
            context.Request.QueryString = new QueryString(query);
            foreach (var (name, value) in routeValues)
            {
                context.Request.RouteValues[name] = value.ToString();
            }

            if (body is not null)
            {
                context.Request.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Request.Body = new MemoryStream(bytes);
                context.Request.ContentLength = bytes.Length;
                context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyPresent());
            }

            context.User = human
                ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(TuvimaClaimTypes.PrincipalKind, nameof(PrincipalKind.Human))], "test"))
                : new ClaimsPrincipal(new ClaimsIdentity("test"));
            using var response = new MemoryStream();
            context.Response.Body = response;
            var handler = endpoint.RequestDelegate!;
            await handler(context);
            return (context.Response.StatusCode, Encoding.UTF8.GetString(response.ToArray()));
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    private sealed class BodyPresent : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class FixedResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) => ValueTask.FromResult(authority);
    }
}
