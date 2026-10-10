using Dapper;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Api.Security;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>"Other people" (B10c): server administrators read other households' photos, read-only, and it is recorded.</summary>
public sealed class ViewOtherPeopleTests
{
    private static readonly Guid HomeHousehold = Guid.NewGuid();
    private static readonly Guid OtherHousehold = Guid.NewGuid();

    [Fact]
    public async Task ServerAdministratorCanResolveAnotherHouseholdsSpace_AndItIsMarkedOther()
    {
        var admin = State(HomeHousehold);
        var stranger = State(OtherHousehold);
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(admin, stranger));

        var resolution = Assert.IsType<ViewScopeResolution>(await resolver.ResolveAsync(
            ServerAdministrator(admin), ViewScopeRequest.ForProfile(stranger.Policy.ProfileId)));

        Assert.Equal(ViewScopeKind.Profile, resolution.Scope.Kind);
        Assert.Equal(OtherHousehold, resolution.Scope.OtherHouseholdId);
        Assert.True(resolution.Scope.IsOtherHousehold);
    }

    [Fact]
    public async Task ARememberedSelectionNeverOpensAnotherHousehold()
    {
        var admin = State(HomeHousehold);
        var stranger = State(OtherHousehold);
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(admin, stranger));

        var fallback = Assert.IsType<ViewScopeResolution>(await resolver.ResolveAsync(
            ServerAdministrator(admin), ViewScopeRequest.ForProfile(stranger.Policy.ProfileId),
            allowStaleSelectionFallback: true));

        Assert.Equal(ViewScopeKind.Mine, fallback.Scope.Kind);
        Assert.False(fallback.Scope.IsOtherHousehold);
        Assert.True(fallback.Scope.WasFallback);
    }

    [Fact]
    public void SavingPreferencesWhileBrowsingAnotherHouseholdNeverStoresThatScope()
    {
        var endpoint = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "MediaEngine.Api", "Endpoints", "ViewEndpoints.cs"));
        Assert.Contains("remembered.LastScopeKind ?? ViewScopeKind.Mine", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HouseholdAdministratorsMembersAndAppsCannotResolveAnotherHouseholdsSpace()
    {
        var caller = State(HomeHousehold);
        var stranger = State(OtherHousehold);
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(caller, stranger));
        var request = ViewScopeRequest.ForProfile(stranger.Policy.ProfileId);

        var householdAdmin = Human(caller) with { AccountIsHouseholdAdmin = true, AccountHouseholdId = HomeHousehold };
        Assert.Null(await resolver.ResolveAsync(householdAdmin, request));
        Assert.Null(await resolver.ResolveAsync(Human(caller), request));
        Assert.Null(await resolver.ResolveAsync(
            ServerAdministrator(caller) with { PrincipalKind = PrincipalKind.DelegatedUserClient, ApplicationId = Guid.NewGuid() },
            request));
    }

    [Fact]
    public async Task ChildProfileOfAServerAdministratorCannotBrowseOtherHouseholds()
    {
        var caller = State(HomeHousehold);
        var stranger = State(OtherHousehold);
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(caller, stranger));

        Assert.Null(await resolver.ResolveAsync(
            ServerAdministrator(caller) with { ActiveProfileIsRestricted = true },
            ViewScopeRequest.ForProfile(stranger.Policy.ProfileId)));
    }

    [Fact]
    public async Task OtherSharedLibraryResolvesOnlyForServerAdministratorsAndOnlyForOtherHouseholds()
    {
        var admin = State(HomeHousehold);
        var own = State(HomeHousehold);
        var stranger = State(OtherHousehold);
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(admin, own, stranger));

        var resolution = Assert.IsType<ViewScopeResolution>(await resolver.ResolveAsync(
            ServerAdministrator(admin), ViewScopeRequest.ForOtherShared(stranger.Policy.ProfileId)));
        Assert.Equal(ViewScopeKind.Shared, resolution.Scope.Kind);
        Assert.Equal(OtherHousehold, resolution.Scope.OtherHouseholdId);
        Assert.Equal(stranger.Policy.ProfileId, resolution.Scope.OtherViaProfileId);

        Assert.Null(await resolver.ResolveAsync(Human(admin), ViewScopeRequest.ForOtherShared(stranger.Policy.ProfileId)));
        Assert.Null(await resolver.ResolveAsync(
            ServerAdministrator(admin), ViewScopeRequest.ForOtherShared(own.Policy.ProfileId)));
    }

    [Fact]
    public async Task OpeningAnotherHouseholdIsRecordedAndNeverAllowsChanges()
    {
        var admin = State(HomeHousehold);
        var stranger = State(OtherHousehold);
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(admin, stranger));
        var auditor = new CapturingAuditor();
        var authorization = new ViewResourceAuthorizationService(
            resolver, new NoResources(), new TestAllowAuthorizationEvaluator(), auditor);
        var request = ViewScopeRequest.ForProfile(stranger.Policy.ProfileId);

        var opened = await authorization.AuthorizeAsync(ServerAdministrator(admin),
            new ViewResourceRequest(request, ViewResourceKind.Search, null));
        Assert.True(opened.IsAllowed);
        Assert.Single(auditor.Scopes);

        foreach (var kind in new[] { ViewResourceKind.Gallery, ViewResourceKind.Upload, ViewResourceKind.Folder })
        {
            Assert.False((await authorization.AuthorizeAsync(ServerAdministrator(admin),
                new ViewResourceRequest(request, kind, null, ViewResourceAction.Manage))).IsAllowed);
        }

        Assert.False((await authorization.AuthorizeAsync(ServerAdministrator(admin),
            new ViewResourceRequest(request, ViewResourceKind.Gallery, null))).IsAllowed);
        Assert.Single(auditor.Scopes);
    }

    [Fact]
    public async Task LockedAdministratorScreensRefuseOtherPeopleAndNothingIsRecorded()
    {
        var admin = State(HomeHousehold);
        var stranger = State(OtherHousehold);
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(admin, stranger));
        var auditor = new CapturingAuditor();
        var authorization = new ViewResourceAuthorizationService(
            resolver, new NoResources(), new LockedEvaluator(), auditor);

        var decision = await authorization.AuthorizeAsync(ServerAdministrator(admin),
            new ViewResourceRequest(ViewScopeRequest.ForProfile(stranger.Policy.ProfileId), ViewResourceKind.Search, null));

        Assert.Equal(ViewAccessOutcome.Forbidden, decision.Outcome);
        Assert.Empty(auditor.Scopes);
    }

    [Fact]
    public async Task WithoutAWayToRecordItNothingIsShown()
    {
        var admin = State(HomeHousehold);
        var stranger = State(OtherHousehold);
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(admin, stranger));
        var authorization = new ViewResourceAuthorizationService(resolver, new NoResources(), new TestAllowAuthorizationEvaluator());

        var decision = await authorization.AuthorizeAsync(ServerAdministrator(admin),
            new ViewResourceRequest(ViewScopeRequest.ForProfile(stranger.Policy.ProfileId), ViewResourceKind.Search, null));

        Assert.Equal(ViewAccessOutcome.Forbidden, decision.Outcome);
    }

    [Fact]
    public async Task HiddenPhotosStayHiddenFromAServerAdministratorBrowsingAnotherHousehold()
    {
        var admin = State(HomeHousehold);
        var stranger = State(OtherHousehold);
        var resolver = new ViewScopeResolver(new ViewScopeResolverTests.ScopeStore(admin, stranger));
        var libraryId = stranger.PersonalSpace!.LibraryId;
        var hidden = new ViewResourceDescriptor(ViewResourceKind.Asset, Guid.NewGuid(), stranger.Policy.ProfileId, libraryId, Hidden: true);
        var visible = new ViewResourceDescriptor(ViewResourceKind.Asset, Guid.NewGuid(), stranger.Policy.ProfileId, libraryId);
        var authorization = new ViewResourceAuthorizationService(
            resolver, new NoResources(hidden, visible), new TestAllowAuthorizationEvaluator(), new CapturingAuditor());
        var scope = ViewScopeRequest.ForProfile(stranger.Policy.ProfileId);

        Assert.Equal(ViewAccessOutcome.NotFound, (await authorization.AuthorizeAsync(ServerAdministrator(admin),
            new ViewResourceRequest(scope, ViewResourceKind.Asset, hidden.ResourceId))).Outcome);
        Assert.True((await authorization.AuthorizeAsync(ServerAdministrator(admin),
            new ViewResourceRequest(scope, ViewResourceKind.Asset, visible.ResourceId))).IsAllowed);
    }

    [Fact]
    public async Task AnotherHouseholdsSpaceIsRecordedOncePerHourPerViewer_AndOnlyItsOwnersSeeIt()
    {
        using var fixture = new Fixture();
        var scope = fixture.OtherPersonalScope;

        await fixture.Service.RecordOpenAsync(fixture.ServerAdmin, scope);
        await fixture.Service.RecordOpenAsync(fixture.ServerAdmin, scope);
        Assert.Equal(1, fixture.OpenedRows());

        fixture.Clock.Advance(TimeSpan.FromMinutes(61));
        await fixture.Service.RecordOpenAsync(fixture.ServerAdmin, scope);
        Assert.Equal(2, fixture.OpenedRows());

        // The household administrator of the household that was opened sees both, newest first.
        var owner = await fixture.Service.ListViewsAsync(fixture.OtherHouseholdAdmin, PagedRequest.From(0, 10));
        Assert.True(owner.HouseholdWide);
        Assert.Equal(2, owner.Page.Items.Count);
        Assert.All(owner.Page.Items, view =>
        {
            Assert.Equal("Server admin", view.ViewerName);
            Assert.Equal("Casey's photos", view.Opened);
        });
        Assert.True(owner.Page.Items[0].ViewedAt > owner.Page.Items[1].ViewedAt);

        // The person whose photos they were sees it; their housemate and the viewer's own household do not.
        Assert.Equal(2, (await fixture.Service.ListViewsAsync(fixture.OtherMember, PagedRequest.From(0, 10))).Page.Items.Count);
        Assert.Empty((await fixture.Service.ListViewsAsync(fixture.OtherHousemate, PagedRequest.From(0, 10))).Page.Items);
        Assert.Empty((await fixture.Service.ListViewsAsync(fixture.HomeMember, PagedRequest.From(0, 10))).Page.Items);
    }

    [Fact]
    public async Task OtherHouseholdSharedLibraryViewIsRecordedForTheHouseholdAdministrator()
    {
        using var fixture = new Fixture();

        await fixture.Service.RecordOpenAsync(fixture.ServerAdmin, fixture.OtherSharedScope);

        var owner = await fixture.Service.ListViewsAsync(fixture.OtherHouseholdAdmin, PagedRequest.From(0, 10));
        Assert.Equal("Shared Library", Assert.Single(owner.Page.Items).Opened);
        Assert.Empty((await fixture.Service.ListViewsAsync(fixture.OtherMember, PagedRequest.From(0, 10))).Page.Items);
    }

    [Fact]
    public async Task OtherPeopleListsOnlyOtherHouseholds()
    {
        using var fixture = new Fixture();

        var list = await fixture.Service.ListAsync(fixture.ServerAdmin);

        var household = Assert.Single(list.Households);
        Assert.Equal(fixture.OtherHouseholdId, household.HouseholdId);
        Assert.DoesNotContain(list.Households, value => value.HouseholdId == fixture.HomeHouseholdId);
        Assert.Contains(household.People, person => person.DisplayName == "Casey");
    }

    private static ViewScopeStoreEntry State(Guid household)
    {
        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new ViewScopeStoreEntry(
            new ViewProfilePolicy(profileId, true, true, true, true, now),
            new ViewPersonalSpace(Guid.NewGuid(), profileId, Guid.NewGuid(), now, now),
            HouseholdId: household);
    }

    private static RequestAuthority Human(ViewScopeStoreEntry state) =>
        new(PrincipalKind.Human, true, Guid.NewGuid(), state.Policy.ProfileId,
            AccountEnabled: true, GrantEnabled: true, AccountHouseholdId: state.HouseholdId);

    private static RequestAuthority ServerAdministrator(ViewScopeStoreEntry state) =>
        Human(state) with { AccountIsAdministrator = true, GrantAdminEnabled = true };

    private sealed class CapturingAuditor : IViewOtherPeopleAuditor
    {
        public List<ResolvedViewScope> Scopes { get; } = [];

        public Task RecordOpenAsync(RequestAuthority actor, ResolvedViewScope scope, CancellationToken ct = default)
        {
            Scopes.Add(scope);
            return Task.CompletedTask;
        }
    }

    private sealed class LockedEvaluator : IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
                requirement.RequiresAdministratorSurfaceUnlock
                    ? AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired)
                    : AuthorizationDecision.Allow());
    }

    private sealed class NoResources(params ViewResourceDescriptor[] resources) : IViewResourceStore
    {
        public Task<ViewResourceDescriptor?> FindAsync(ViewResourceKind kind, Guid resourceId,
            Guid requestingProfileId, CancellationToken ct = default) =>
            Task.FromResult(resources.FirstOrDefault(value => value.Kind == kind && value.ResourceId == resourceId));
    }

    private sealed class MovableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-other-people-{Guid.NewGuid():N}");
        private readonly DatabaseConnection _database;

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            _database = new DatabaseConnection(Path.Combine(_root, "other-people.db"));
            _database.InitializeSchema();
            Clock = new MovableClock();
            Service = new ViewOtherPeopleService(_database, new AuthorizationAuditWriter(new AccountRepository(_database)), Clock);

            var serverAdminProfile = AddProfile("Server admin", HomeHouseholdId);
            var homeMember = AddProfile("Home member", HomeHouseholdId);
            var otherAdminProfile = AddProfile("Other admin", OtherHouseholdId);
            var casey = AddProfile("Casey", OtherHouseholdId);
            var housemate = AddProfile("Housemate", OtherHouseholdId);
            ServerAdminAccount = AddAccount("admin@example.test", HomeHouseholdId);
            var otherAdminAccount = AddAccount("other@example.test", OtherHouseholdId);

            ServerAdmin = new RequestAuthority(PrincipalKind.Human, true, ServerAdminAccount, serverAdminProfile,
                AccountEnabled: true, GrantEnabled: true, AccountIsAdministrator: true, GrantAdminEnabled: true,
                AccountHouseholdId: HomeHouseholdId);
            HomeMember = Member(homeMember, HomeHouseholdId);
            OtherHouseholdAdmin = new RequestAuthority(PrincipalKind.Human, true, otherAdminAccount, otherAdminProfile,
                AccountEnabled: true, GrantEnabled: true, AccountHouseholdId: OtherHouseholdId, AccountIsHouseholdAdmin: true);
            OtherMember = Member(casey, OtherHouseholdId);
            OtherHousemate = Member(housemate, OtherHouseholdId);
            CaseyId = casey;
        }

        public Guid HomeHouseholdId { get; } = Guid.NewGuid();
        public Guid OtherHouseholdId { get; } = Guid.NewGuid();
        public Guid ServerAdminAccount { get; }
        public Guid CaseyId { get; }
        public MovableClock Clock { get; }
        public ViewOtherPeopleService Service { get; }
        public RequestAuthority ServerAdmin { get; }
        public RequestAuthority HomeMember { get; }
        public RequestAuthority OtherHouseholdAdmin { get; }
        public RequestAuthority OtherMember { get; }
        public RequestAuthority OtherHousemate { get; }

        public ResolvedViewScope OtherPersonalScope => new(ViewScopeKind.Profile, CaseyId,
            new HashSet<Guid> { Guid.NewGuid() }, OtherHouseholdId: OtherHouseholdId, OtherViaProfileId: CaseyId);

        public ResolvedViewScope OtherSharedScope => new(ViewScopeKind.Shared, null,
            new HashSet<Guid> { Guid.NewGuid() }, OtherHouseholdId: OtherHouseholdId, OtherViaProfileId: CaseyId);

        public int OpenedRows()
        {
            using var connection = _database.CreateConnection();
            return connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM authorization_audit_events WHERE event_type = 'view.other_household_opened';");
        }

        private RequestAuthority Member(Guid profile, Guid household) =>
            new(PrincipalKind.Human, true, Guid.NewGuid(), profile, AccountEnabled: true, GrantEnabled: true,
                AccountHouseholdId: household);

        private Guid AddProfile(string name, Guid household)
        {
            var id = Guid.NewGuid();
            ProfileTestData.InsertAsync(_database, new Profile
            {
                Id = id,
                DisplayName = name,
                Role = ProfileRole.StandardUser,
            }, household).GetAwaiter().GetResult();
            return id;
        }

        private Guid AddAccount(string email, Guid household)
        {
            var id = Guid.NewGuid();
            using var connection = _database.CreateConnection();
            connection.Execute("""
                INSERT INTO accounts(id,email,normalized_email,is_enabled,is_administrator,authorization_version,created_at,updated_at,household_id)
                VALUES(@id,@email,@normalized,1,0,1,@now,@now,@household);
                """, new { id, email, normalized = email.ToUpperInvariant(), now = DateTimeOffset.UtcNow.ToString("O"), household });
            return id;
        }

        public void Dispose()
        {
            _database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
