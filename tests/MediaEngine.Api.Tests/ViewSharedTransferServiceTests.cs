using Dapper;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class ViewSharedTransferServiceTests
{
    [Fact]
    public async Task Contribution_DoesNotMoveUntilHouseholdAdministratorAccepts_AndIsIdempotent()
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, true, true, null));
        var original = fixture.WriteManaged("keeper.jpg", [11, 12, 13]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        Assert.NotNull(indexed);
        var preview = await fixture.Contributions.PreviewAsync(fixture.Authority,
            new ViewSharedContributionPreviewRequest([indexed!.ItemId]));
        var request = new ViewSharedContributionSubmitRequest([indexed.ItemId], "timeline", null,
            "Worth keeping", preview.PreviewRevision, "stable-request");

        var pending = await fixture.Contributions.SubmitAsync(fixture.Authority, request);
        var repeated = await fixture.Contributions.SubmitAsync(fixture.Authority, request);

        Assert.Equal(pending.Id, repeated.Id);
        Assert.Equal("pending", pending.Status);
        Assert.True(File.Exists(original));

        await fixture.Contributions.DecideAsync(fixture.Authority, pending.Id,
            new ViewSharedContributionDecisionRequest("accepted", pending.Revision));
        await fixture.Contributions.ProcessAsync(pending.Id);
        var accepted = await fixture.Contributions.GetRequiredAsync(fixture.Authority, pending.Id, true);

        Assert.Equal("accepted", accepted.Status);
        Assert.Equal("completed", Assert.Single(accepted.Items).ExecutionState);
        Assert.Contains(accepted.Events, value => value.EventType == "submitted");
        Assert.Contains(accepted.Events, value => value.EventType == "accepted");
        Assert.Contains(accepted.Events, value => value.EventType == "transfer_completed");
        Assert.False(File.Exists(original));
    }

    [Fact]
    public async Task HouseholdAdministratorCanAddOwnedItemDirectlyWithoutSubmitGrant()
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, false, true, null));
        var original = fixture.WriteManaged("direct.jpg", [31, 32]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);

        var accepted = await fixture.Contributions.AddDirectAsync(fixture.Authority,
            new ViewSharedDirectAddRequest([indexed!.ItemId], IdempotencyKey: "direct-request"));

        Assert.Equal("accepted", accepted.Status);
        Assert.True(File.Exists(original));
        await fixture.Contributions.ProcessAsync(accepted.Id);
        Assert.False(File.Exists(original));
    }

    [Fact]
    public async Task ReviewingRequiresEffectiveAdministratorAuthority()
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, false, true, null));
        var original = fixture.WriteManaged("denied-direct.jpg", [41, 42]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        var nonAdministrator = fixture.Authority with { AccountIsAdministrator = false };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Contributions.AddDirectAsync(
            nonAdministrator, new ViewSharedDirectAddRequest([indexed!.ItemId])));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public async Task ReviewingRequiresAdministratorSurfaceUnlock()
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, false, true, null));
        var original = fixture.WriteManaged("unlock-direct.jpg", [61, 62]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);

        await fixture.Contributions.AddDirectAsync(fixture.Authority,
            new ViewSharedDirectAddRequest([indexed!.ItemId]));

        Assert.True(fixture.Decisions.LastRequiredSurfaceUnlock);
    }

    [Fact]
    public async Task ContributionCannotProbeAnotherHouseholdsItem()
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, true, true, null));
        var original = fixture.WriteManaged("private-item.jpg", [71, 72]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        var otherProfileId = Guid.NewGuid();
        await fixture.InsertProfileAsync(new Profile
        {
            Id = otherProfileId,
            DisplayName = "Other profile",
            Role = ProfileRole.StandardUser,
        }, householdId: Guid.NewGuid());
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            otherProfileId, true, true, true, false, null));
        var otherAuthority = fixture.Authority with { ActiveProfileId = otherProfileId };

        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Contributions.PreviewAsync(
            otherAuthority, new ViewSharedContributionPreviewRequest([indexed!.ItemId])));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public async Task DisabledAccountCannotSubmitDespiteProfilePolicy()
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, true, true, null));
        var original = fixture.WriteManaged("disabled-submit.jpg", [51, 52]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Contributions.PreviewAsync(
            fixture.Authority with { AccountEnabled = false },
            new ViewSharedContributionPreviewRequest([indexed!.ItemId])));
        Assert.True(File.Exists(original));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisabledGrantOrDelegatedApplicationCannotSubmit(
        bool grantEnabled,
        bool applicationEnabled)
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, true, true, null));
        var original = fixture.WriteManaged("disabled-binding.jpg", [53, 54]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        var authority = fixture.Authority with
        {
            PrincipalKind = applicationEnabled ? PrincipalKind.Human : PrincipalKind.DelegatedUserClient,
            GrantEnabled = grantEnabled,
            ApplicationId = applicationEnabled ? null : Guid.NewGuid(),
            ApplicationEnabled = applicationEnabled,
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Contributions.PreviewAsync(
            authority, new ViewSharedContributionPreviewRequest([indexed!.ItemId])));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public async Task DeclinedContribution_LeavesOriginalUntouched()
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, true, true, null));
        var original = fixture.WriteManaged("declined.jpg", [21, 22]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        var preview = await fixture.Contributions.PreviewAsync(fixture.Authority,
            new ViewSharedContributionPreviewRequest([indexed!.ItemId]));
        var pending = await fixture.Contributions.SubmitAsync(fixture.Authority,
            new([indexed.ItemId], "timeline", null, null, preview.PreviewRevision, "decline-request"));

        var declined = await fixture.Contributions.DecideAsync(fixture.Authority, pending.Id,
            new("declined", pending.Revision, "Not for the shared collection"));

        Assert.Equal("declined", declined.Status);
        Assert.Equal("waiting", Assert.Single(declined.Items).ExecutionState);
        Assert.True(File.Exists(original));
    }

    [Fact]
    public async Task ManagedItem_IsVerifiedMovedAndBrowsableUnderSharedRoot()
    {
        using var fixture = new Fixture();
        var original = fixture.WriteManaged("shared-moment.jpg", [1, 2, 3, 4, 5]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        Assert.NotNull(indexed);

        var preview = fixture.Transfers.Preview(indexed!.ItemId, "timeline", null);
        var result = await fixture.Transfers.ExecuteAsync(indexed.ItemId, fixture.ProfileId, "timeline", null);
        var repeated = await fixture.Transfers.ExecuteAsync(indexed.ItemId, fixture.ProfileId, "timeline", null);

        Assert.Equal("move", preview.Operation);
        Assert.Equal("completed", result.State);
        Assert.False(File.Exists(original));
        var destination = Assert.Single(result.DestinationPaths);
        Assert.StartsWith(fixture.Storage.GetSharedRoot(), destination, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([1, 2, 3, 4, 5], await File.ReadAllBytesAsync(destination));
        Assert.Equal(result.DestinationPaths, repeated.DestinationPaths);

        var sharedLibraryId = fixture.SharedLibraryId();
        var sharedSourceId = fixture.SharedSourceId("shared:timeline");
        var root = await fixture.Folders.QueryAsync(
            fixture.ProfileId,
            new ResolvedViewScope(ViewScopeKind.Shared, null, new HashSet<Guid> { sharedLibraryId }),
            null, null, false, null, 0, 100);
        Assert.Contains(root.Sources, source => source.SourceId == sharedSourceId);
        var shared = await fixture.Folders.QueryAsync(
            fixture.ProfileId,
            new ResolvedViewScope(ViewScopeKind.Shared, null, new HashSet<Guid> { sharedLibraryId }),
            sharedSourceId, null, true, null, 0, 100);
        Assert.Equal("Timeline", Assert.Single(shared.Breadcrumbs).Label);
        var sharedItem = Assert.Single(shared.Items);
        Assert.Equal("shared", sharedItem.ScopeKind);
        Assert.Null(sharedItem.OwnerProfileId);
        Assert.Null(sharedItem.PersonalSpaceId);
        Assert.NotEqual(indexed.ItemId, sharedItem.Id);
        Assert.Equal(indexed.ItemId, fixture.OriginItemId(sharedItem.Id));
    }

    [Fact]
    public async Task LinkedItem_IsCopiedAndExternalOriginalRemains()
    {
        using var fixture = new Fixture();
        var linkedRoot = Directory.CreateDirectory(Path.Combine(fixture.Root, "linked"));
        await fixture.Storage.AddLinkedSourceAsync(fixture.Space, "Home Movies", linkedRoot.FullName, true);
        var original = Path.Combine(linkedRoot.FullName, "home-movie.mp4");
        await File.WriteAllBytesAsync(original, [8, 9, 10]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        Assert.NotNull(indexed);

        var preview = fixture.Transfers.Preview(indexed!.ItemId, "folder", "Home Movies");
        var result = await fixture.Transfers.ExecuteAsync(
            indexed.ItemId, fixture.ProfileId, "folder", "Home Movies");

        Assert.Equal("copy", preview.Operation);
        Assert.Equal("completed", result.State);
        Assert.True(File.Exists(original));
        var destination = Assert.Single(result.DestinationPaths);
        Assert.Equal([8, 9, 10], await File.ReadAllBytesAsync(destination));
        var sharedId = fixture.SharedItemId(indexed.ItemId);
        var resolved = await fixture.ResolveSharedContentAsync(sharedId);
        Assert.NotNull(resolved);
        Assert.Equal(destination, resolved!.FilePath);
        Assert.NotEqual(original, resolved.FilePath);
        Assert.Null(resolved.OwnerProfileId);
        Assert.Equal(fixture.FileIdForPath(original), fixture.FileIdForPath(destination));
    }

    [Fact]
    public async Task FolderPinsAndTimelinePolicies_AreScopedAndInherited()
    {
        using var fixture = new Fixture();
        var source = Assert.Single(await fixture.Sources.GetSourcesAsync(fixture.Space.Id));
        var scope = new ResolvedViewScope(ViewScopeKind.Mine, fixture.ProfileId,
            new HashSet<Guid> { fixture.Space.LibraryId });

        await fixture.Folders.SetPinAsync(fixture.ProfileId, scope, source.Id, "Timeline", true);
        await fixture.Folders.SetTimelinePolicyAsync(fixture.ProfileId, true, scope,
            source.Id, string.Empty, false);
        await fixture.Folders.SetTimelinePolicyAsync(fixture.ProfileId, true, scope,
            source.Id, "Timeline", true);

        var root = await fixture.Folders.QueryAsync(fixture.ProfileId, scope,
            null, null, false, null, 0, 100);
        var nested = await fixture.Folders.QueryAsync(fixture.ProfileId, scope,
            source.Id, Path.Combine("Timeline", "2024"), false, null, 0, 100);

        Assert.Contains(root.PinnedFolders, pin => pin.SourceId == source.Id && pin.RelativePath == "Timeline");
        Assert.True(nested.EffectiveIncludeInTimeline);
        Assert.Null(nested.IncludeInTimelineOverride);
    }

    [Fact]
    public async Task FolderCounts_CountLogicalItemsRatherThanDuplicateFilePaths()
    {
        using var fixture = new Fixture();
        var first = fixture.WriteManaged(Path.Combine("Trip", "photo.jpg"), [1, 2, 3, 4]);
        var duplicate = fixture.WriteManaged(Path.Combine("Trip", "copy.jpg"), [1, 2, 3, 4]);
        await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, first);
        await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, duplicate);
        var source = Assert.Single(await fixture.Sources.GetSourcesAsync(fixture.Space.Id));
        var scope = new ResolvedViewScope(ViewScopeKind.Mine, fixture.ProfileId, new HashSet<Guid> { fixture.Space.LibraryId });
        var root = await fixture.Folders.QueryAsync(fixture.ProfileId, scope, null, null, false, null, 0, 100);
        var folders = await fixture.Folders.QueryAsync(fixture.ProfileId, scope, source.Id, null, false, null, 0, 100);
        var items = await fixture.Folders.QueryAsync(fixture.ProfileId, scope, source.Id, "Trip", false, null, 0, 100);
        Assert.Equal(1, Assert.Single(root.Sources).ItemCount);
        Assert.Equal(1, Assert.Single(folders.Folders).ItemCount);
        Assert.Single(items.Items);
    }

    [Fact]
    public async Task JimCanOpenMarysPhotoAndShareACopy_AHouseholdAdministratorApproves_AndJimCannotEditIt()
    {
        using var fixture = new Fixture();
        var maryId = Guid.NewGuid();
        var jimId = Guid.NewGuid();
        await fixture.InsertProfileAsync(new Profile { Id = maryId, DisplayName = "Mary", Role = ProfileRole.StandardUser });
        await fixture.InsertProfileAsync(new Profile { Id = jimId, DisplayName = "Jim", Role = ProfileRole.StandardUser });
        var marySpace = await fixture.Storage.EnsurePersonalSpaceAsync(maryId);
        await fixture.Storage.EnsurePersonalSpaceAsync(jimId);
        var original = fixture.WriteManagedFor(marySpace, "mary-lake.jpg", [5, 6, 7]);
        var indexed = await fixture.Library.IndexPathAsync(marySpace.LibraryId, original);
        Assert.NotNull(indexed);
        var jim = fixture.Person(jimId);
        var context = new StubProfileContext(jim);
        var authorization = fixture.NewAuthorization();

        // Jim opens Mary's photo (read only) and finds it in her space's timeline.
        var open = await authorization.AuthorizeAsync(jim, new ViewResourceRequest(
            ViewScopeRequest.ForProfile(maryId), ViewResourceKind.Asset, indexed!.ItemId));
        Assert.True(open.IsAllowed);
        var timeline = await fixture.NewOrchestrator(context).QueryAsync(new ViewAssetQueryRequest(
            ViewScopeRequest.ForProfile(maryId)));
        Assert.Contains(Assert.IsType<ViewAssetTimelinePageDto>(timeline.Page).Items, item => item.Id == indexed.ItemId);

        // He cannot change or remove it.
        foreach (var action in new[] { ViewResourceAction.Manage, ViewResourceAction.Contribute })
        {
            var edit = await authorization.AuthorizeAsync(jim, new ViewResourceRequest(
                ViewScopeRequest.ForProfile(maryId), ViewResourceKind.Asset, indexed.ItemId, action));
            Assert.False(edit.IsAllowed);
            var viaMine = await authorization.AuthorizeAsync(jim, new ViewResourceRequest(
                ViewScopeRequest.Mine, ViewResourceKind.Asset, indexed.ItemId, action));
            Assert.False(viaMine.IsAllowed);
        }

        // He sends a copy to the household Shared Library.
        var preview = await fixture.Contributions.PreviewAsync(jim,
            new ViewSharedContributionPreviewRequest([indexed.ItemId]));
        Assert.Equal("copy", Assert.Single(preview.Items).Operation);
        var pending = await fixture.Contributions.SubmitAsync(jim, new ViewSharedContributionSubmitRequest(
            [indexed.ItemId], "timeline", null, "Mary's lake", preview.PreviewRevision, "jim-sends-marys-lake"));
        Assert.Equal("pending", pending.Status);

        // Only a household administrator of that household (or a server administrator) can review it.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Contributions.DecideAsync(
            jim, pending.Id, new ViewSharedContributionDecisionRequest("accepted", pending.Revision)));
        var otherHouseholdAdministrator = fixture.HouseholdAdministrator(Guid.NewGuid());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Contributions.DecideAsync(
            otherHouseholdAdministrator, pending.Id,
            new ViewSharedContributionDecisionRequest("accepted", pending.Revision)));

        var householdAdministrator = fixture.HouseholdAdministrator(ProfileTestData.TestHouseholdId);
        var reviewList = await fixture.Contributions.ListAsync(householdAdministrator, "review", "pending", 0, 20);
        Assert.True(reviewList.CanReview);
        Assert.Equal(pending.Id, Assert.Single(reviewList.Items).Id);
        await fixture.Contributions.DecideAsync(householdAdministrator, pending.Id,
            new ViewSharedContributionDecisionRequest("accepted", pending.Revision));
        await fixture.Contributions.ProcessAsync(pending.Id);

        // Mary's original stays exactly where it was; the Shared Library has its own copy.
        Assert.True(File.Exists(original));
        Assert.Equal(indexed.ItemId, fixture.OriginItemId(fixture.SharedItemId(indexed.ItemId)));
    }

    [Fact]
    public async Task AChildProfileCannotSendToTheSharedLibraryUntilAHouseholdAdministratorAllowsIt()
    {
        using var fixture = new Fixture();
        var childId = Guid.NewGuid();
        await fixture.InsertProfileAsync(new Profile { Id = childId, DisplayName = "Child", Role = ProfileRole.RestrictedProfile });
        var childSpace = await fixture.Storage.EnsurePersonalSpaceAsync(childId);
        var original = fixture.WriteManagedFor(childSpace, "drawing.jpg", [8, 9]);
        var indexed = await fixture.Library.IndexPathAsync(childSpace.LibraryId, original);
        var child = fixture.Person(childId) with { ActiveProfileIsRestricted = true };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Contributions.PreviewAsync(
            child, new ViewSharedContributionPreviewRequest([indexed!.ItemId])));

        await fixture.Policies.SavePolicyAsync((await fixture.Policies.GetPolicyAsync(childId)) with { SubmitToSharedLibrary = true });

        var preview = await fixture.Contributions.PreviewAsync(
            child, new ViewSharedContributionPreviewRequest([indexed!.ItemId]));
        Assert.Single(preview.Items);
    }

    [Fact]
    public async Task AnotherHouseholdSeesNothingOfThisOne_AcrossEverySurface()
    {
        using var fixture = new Fixture();
        var maryId = Guid.NewGuid();
        var eveId = Guid.NewGuid();
        var otherHousehold = Guid.NewGuid();
        await fixture.InsertProfileAsync(new Profile { Id = maryId, DisplayName = "Mary", Role = ProfileRole.StandardUser });
        await fixture.InsertProfileAsync(new Profile { Id = eveId, DisplayName = "Eve", Role = ProfileRole.StandardUser }, otherHousehold);
        var marySpace = await fixture.Storage.EnsurePersonalSpaceAsync(maryId);
        var eveSpace = await fixture.Storage.EnsurePersonalSpaceAsync(eveId);
        var maryFile = fixture.WriteManagedFor(marySpace, "mary-secret.jpg", [1, 1, 1]);
        var eveFile = fixture.WriteManagedFor(eveSpace, "eve-own.jpg", [2, 2, 2]);
        var maryItem = (await fixture.Library.IndexPathAsync(marySpace.LibraryId, maryFile))!.ItemId;
        var eveItem = (await fixture.Library.IndexPathAsync(eveSpace.LibraryId, eveFile))!.ItemId;

        // Mary shares a photo into her household's Shared Library, approved by her household's administrator.
        var mary = fixture.Person(maryId);
        var maryPreview = await fixture.Contributions.PreviewAsync(mary, new ViewSharedContributionPreviewRequest([maryItem]));
        var contribution = await fixture.Contributions.SubmitAsync(mary, new ViewSharedContributionSubmitRequest(
            [maryItem], "timeline", null, null, maryPreview.PreviewRevision, "mary-shares"));
        await fixture.Contributions.DecideAsync(fixture.HouseholdAdministrator(ProfileTestData.TestHouseholdId),
            contribution.Id, new ViewSharedContributionDecisionRequest("accepted", contribution.Revision));
        await fixture.Contributions.ProcessAsync(contribution.Id);
        var maryShared = fixture.SharedItemId(maryItem);

        var eve = fixture.Person(eveId);
        var eveAdministrator = fixture.HouseholdAdministrator(otherHousehold) with { ActiveProfileId = eveId };
        var context = new StubProfileContext(eve);
        var authorization = fixture.NewAuthorization();
        var orchestrator = fixture.NewOrchestrator(context);
        var discovery = fixture.NewDiscovery(context);

        async Task<ViewAccessOutcome> Authorize(ViewScopeRequest scope, ViewResourceKind kind, Guid? id) =>
            (await authorization.AuthorizeAsync(eve, new ViewResourceRequest(scope, kind, id))).Outcome;

        var probes = new (string Surface, Func<Task<bool>> Hidden)[]
        {
            ("another household's profile scope", async () =>
                await Authorize(ViewScopeRequest.ForProfile(maryId), ViewResourceKind.Search, null) == ViewAccessOutcome.NotFound),
            ("asset by id", async () => await Authorize(ViewScopeRequest.Mine, ViewResourceKind.Asset, maryItem) == ViewAccessOutcome.NotFound),
            ("asset by id in their profile scope", async () =>
                await Authorize(ViewScopeRequest.ForProfile(maryId), ViewResourceKind.Asset, maryItem) == ViewAccessOutcome.NotFound),
            ("thumbnail", async () => await Authorize(ViewScopeRequest.Mine, ViewResourceKind.Thumbnail, maryItem) == ViewAccessOutcome.NotFound),
            ("original", async () =>
                await Authorize(ViewScopeRequest.ForProfile(maryId), ViewResourceKind.Original, maryItem) == ViewAccessOutcome.NotFound),
            ("their Shared Library photo", async () => await Authorize(ViewScopeRequest.Shared, ViewResourceKind.Asset, maryShared) == ViewAccessOutcome.NotFound),
            ("their Shared Library thumbnail", async () => await Authorize(ViewScopeRequest.Shared, ViewResourceKind.Thumbnail, maryShared) == ViewAccessOutcome.NotFound),
            ("timeline in their space", async () =>
                (await orchestrator.QueryAsync(new ViewAssetQueryRequest(ViewScopeRequest.ForProfile(maryId)))).Outcome == ViewAccessOutcome.NotFound),
            ("own timeline lists only own photos", async () =>
            {
                var own = await orchestrator.QueryAsync(new ViewAssetQueryRequest(ViewScopeRequest.Mine));
                return own.Page!.Items.Select(item => item.Id).SequenceEqual([eveItem]);
            }),
            ("Shared Library listing", async () =>
            {
                var shared = await orchestrator.QueryAsync(new ViewAssetQueryRequest(ViewScopeRequest.Shared));
                return shared.Page!.Items.All(item => item.Id != maryShared && item.Id != maryItem);
            }),
            ("search", async () =>
            {
                var found = await orchestrator.QueryAsync(new ViewAssetQueryRequest(ViewScopeRequest.Mine, Search: "mary"));
                return found.Page!.Items.Count == 0;
            }),
            ("counts and timeline buckets in their space", async () =>
                (await orchestrator.IndexAsync(new ViewAssetQueryRequest(ViewScopeRequest.ForProfile(maryId)))).Outcome == ViewAccessOutcome.NotFound),
            ("own counts", async () =>
                (await orchestrator.IndexAsync(new ViewAssetQueryRequest(ViewScopeRequest.Mine))).Index!.Buckets.Sum(bucket => bucket.AssetCount) == 1),
            ("map pins in their space", async () =>
                (await discovery.GetPlacesAsync(new ViewDiscoveryRequest(ViewScopeRequest.ForProfile(maryId), 50))).Outcome == ViewAccessOutcome.NotFound),
            ("people in their space", async () =>
                (await discovery.GetPeopleAsync(new ViewDiscoveryRequest(ViewScopeRequest.ForProfile(maryId), 50))).Outcome == ViewAccessOutcome.NotFound),
            ("contributions list (own household's reviewer view)", async () =>
                (await fixture.Contributions.ListAsync(eveAdministrator, "review", null, 0, 20)).Items.Count == 0),
            ("contribution by id", async () =>
            {
                try
                {
                    await fixture.Contributions.GetRequiredAsync(eveAdministrator, contribution.Id, true);
                    return false;
                }
                catch (KeyNotFoundException)
                {
                    return true;
                }
            }),
        };

        foreach (var probe in probes)
        {
            Assert.True(await probe.Hidden(), $"Leaked through: {probe.Surface}");
        }
    }

    [Fact]
    public async Task ServerAdministratorReviewsAnyHousehold_AndOtherHouseholdAccessIsAudited()
    {
        using var fixture = new Fixture();
        var maryId = Guid.NewGuid();
        var eveId = Guid.NewGuid();
        var otherHousehold = Guid.NewGuid();
        await fixture.InsertProfileAsync(new Profile { Id = maryId, DisplayName = "Mary", Role = ProfileRole.StandardUser });
        await fixture.InsertProfileAsync(new Profile { Id = eveId, DisplayName = "Eve", Role = ProfileRole.StandardUser }, otherHousehold);
        var marySpace = await fixture.Storage.EnsurePersonalSpaceAsync(maryId);
        var eveSpace = await fixture.Storage.EnsurePersonalSpaceAsync(eveId);
        var maryItem = (await fixture.Library.IndexPathAsync(marySpace.LibraryId,
            fixture.WriteManagedFor(marySpace, "mary.jpg", [1, 2])))!.ItemId;
        var eveItem = (await fixture.Library.IndexPathAsync(eveSpace.LibraryId,
            fixture.WriteManagedFor(eveSpace, "eve.jpg", [3, 4])))!.ItemId;
        var maryContribution = await SubmitAsync(fixture, fixture.Person(maryId), maryItem, "mary-key");
        var eveContribution = await SubmitAsync(fixture, fixture.Person(eveId), eveItem, "eve-key");

        // A server administrator whose own household is Mary's.
        var server = fixture.Authority with { AccountHouseholdId = ProfileTestData.TestHouseholdId };
        var list = await fixture.Contributions.ListAsync(server, "review", "pending", 0, 20);
        Assert.Equal(2, list.Items.Count);

        // Same household: nothing to audit.
        await fixture.Contributions.GetRequiredAsync(server, maryContribution.Id, true);
        Assert.Empty(fixture.Audit.Events);

        // Another household: open and decide each leave a record naming that household.
        await fixture.Contributions.GetRequiredAsync(server, eveContribution.Id, true);
        await fixture.Contributions.DecideAsync(server, eveContribution.Id,
            new ViewSharedContributionDecisionRequest("declined", eveContribution.Revision));

        Assert.Equal(["open", "decide"],
            fixture.Audit.Events.Select(value => value.Changes["action"]));
        Assert.All(fixture.Audit.Events, value =>
        {
            Assert.Equal("view.admin_other_household_contribution", value.EventType);
            Assert.Equal(otherHousehold.ToString("D"), value.Changes["household_id"]);
        });

        // A household administrator only lists their own household's contributions.
        var householdAdministrator = fixture.HouseholdAdministrator(ProfileTestData.TestHouseholdId);
        var own = await fixture.Contributions.ListAsync(householdAdministrator, "review", null, 0, 20);
        Assert.Equal(maryContribution.Id, Assert.Single(own.Items).Id);
    }

    [Fact]
    public async Task AHiddenPhotoStaysHiddenFromTheRestOfTheHousehold_EvenByDirectLink()
    {
        using var fixture = new Fixture();
        var maryId = Guid.NewGuid();
        var jimId = Guid.NewGuid();
        await fixture.InsertProfileAsync(new Profile { Id = maryId, DisplayName = "Mary", Role = ProfileRole.StandardUser });
        await fixture.InsertProfileAsync(new Profile { Id = jimId, DisplayName = "Jim", Role = ProfileRole.StandardUser });
        var marySpace = await fixture.Storage.EnsurePersonalSpaceAsync(maryId);
        await fixture.Storage.EnsurePersonalSpaceAsync(jimId);
        var item = (await fixture.Library.IndexPathAsync(marySpace.LibraryId,
            fixture.WriteManagedFor(marySpace, "private.jpg", [9, 9])))!.ItemId;
        await fixture.SetHiddenAsync(item);
        var authorization = fixture.NewAuthorization();

        foreach (var kind in new[] { ViewResourceKind.Asset, ViewResourceKind.Thumbnail, ViewResourceKind.Original })
        {
            var jimsView = await authorization.AuthorizeAsync(fixture.Person(jimId),
                new ViewResourceRequest(ViewScopeRequest.ForProfile(maryId), kind, item));
            Assert.Equal(ViewAccessOutcome.NotFound, jimsView.Outcome);
            var marysView = await authorization.AuthorizeAsync(fixture.Person(maryId),
                new ViewResourceRequest(ViewScopeRequest.Mine, kind, item));
            Assert.True(marysView.IsAllowed);
        }

        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Contributions.PreviewAsync(
            fixture.Person(jimId), new ViewSharedContributionPreviewRequest([item])));
    }

    private static async Task<ViewSharedContributionDto> SubmitAsync(
        Fixture fixture, RequestAuthority person, Guid itemId, string key)
    {
        var preview = await fixture.Contributions.PreviewAsync(person, new ViewSharedContributionPreviewRequest([itemId]));
        return await fixture.Contributions.SubmitAsync(person, new ViewSharedContributionSubmitRequest(
            [itemId], "timeline", null, null, preview.PreviewRevision, key));
    }

    private sealed class StubProfileContext(RequestAuthority authority) : IViewRequestProfileContext
    {
        public ValueTask<RequestAuthority> ResolveAuthorityAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(authority);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ConfigurationDirectoryLoader _configuration;
        private readonly DatabaseConnection _database;
        private readonly ViewPersonalSpaceRepository _spaces;
        private readonly LocalAssetRepository _assets;

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"tuvima-shared-transfer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            _configuration = new ConfigurationDirectoryLoader(Path.Combine(Root, "config"));
            _configuration.SaveLibraries(new LibrariesConfiguration
            {
                SchemaVersion = "6.0",
                StorageLocations =
                [
                    new ServerStorageLocationConfig
                    {
                        Id = "view",
                        Label = "View",
                        Path = Path.Combine(Root, "storage"),
                        AllowWrite = true,
                    },
                ],
                ViewStorage = new ViewStorageConfig
                {
                    StorageLocationId = "view",
                    RelativeRoot = "View",
                },
            });
            _database = new DatabaseConnection(Path.Combine(Root, "view.db"));
            _database.InitializeSchema();
            _spaces = new ViewPersonalSpaceRepository(_database);
            _assets = new LocalAssetRepository(_database);
            Profiles = new ProfileRepository(_database);
            ProfileId = Guid.NewGuid();
            ProfileTestData.InsertAsync(_database, new Profile
            {
                Id = ProfileId,
                DisplayName = "Household administrator",
                Role = ProfileRole.Administrator,
            }).GetAwaiter().GetResult();
            Authority = new RequestAuthority(PrincipalKind.Human, true, Guid.NewGuid(), ProfileId,
                AccountEnabled: true, GrantEnabled: true,
                AccountIsAdministrator: true, GrantAdminEnabled: true);
            Storage = new ViewStorageService(_configuration, _spaces, new ViewSharedLibraryRepository(_database));
            Space = Storage.EnsurePersonalSpaceAsync(ProfileId).GetAwaiter().GetResult();
            Library = new ViewLibraryService(_assets, _configuration, _spaces, Storage,
                NullLogger<ViewLibraryService>.Instance);
            Transfers = new ViewSharedTransferService(_database, _assets, Storage);
            Policies = new ViewProfileRepository(_database);
            Authorization = new TestAllowAuthorizationEvaluator();
            Decisions = new AdministratorDecisions();
            Audit = new RecordingAuditWriter();
            Contributions = new ViewSharedContributionService(_database, _assets, Policies, Transfers,
                Authorization, new ViewSharedContributionQueue(), Decisions, Audit);
            Folders = new ViewFolderService(_database, _spaces, Profiles, _assets, Storage);
        }

        public string Root { get; }
        public Guid ProfileId { get; }
        public RequestAuthority Authority { get; }
        public ViewPersonalSpace Space { get; }
        public ViewPersonalSpaceRepository Sources => _spaces;
        public ProfileRepository Profiles { get; }

        public Task InsertProfileAsync(Profile profile, Guid? householdId = null) =>
            ProfileTestData.InsertAsync(_database, profile, householdId);

        /// <summary>An ordinary signed-in person acting as the given profile.</summary>
        public RequestAuthority Person(Guid profileId) => new(PrincipalKind.Human, true, Guid.NewGuid(), profileId,
            AccountEnabled: true, GrantEnabled: true);

        /// <summary>A household administrator (not a server administrator) of the given household.</summary>
        public RequestAuthority HouseholdAdministrator(Guid householdId) => Person(ProfileId) with
        {
            AccountHouseholdId = householdId,
            AccountIsHouseholdAdmin = true,
        };

        public ViewResourceAuthorizationService NewAuthorization() => new(
            new ViewScopeResolver(new ViewScopePersistenceService(
                Profiles, Policies, _spaces, new ViewSharedLibraryRepository(_database), _database)),
            new ViewResourcePersistenceService(_assets, new ViewGalleryRepository(_database), _spaces, _database),
            Authorization);

        public ViewQueryOrchestrator NewOrchestrator(IViewRequestProfileContext context) =>
            new(context, NewAuthorization(), new ViewAssetQueryService(_assets));

        public ViewDiscoveryService NewDiscovery(IViewRequestProfileContext context) =>
            new(context, NewAuthorization(), new ViewDiscoveryRepository(_database), _assets);

        public string WriteManagedFor(ViewPersonalSpace space, string name, byte[] bytes)
        {
            var source = _spaces.GetSourcesAsync(space.Id).GetAwaiter().GetResult()
                .Single(value => value.SourceType == ViewSourceType.BrowserUpload);
            var path = Path.Combine(Storage.GetSourcePath(space, source), name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        public ViewStorageService Storage { get; }
        public ViewLibraryService Library { get; }
        public ViewSharedTransferService Transfers { get; }
        public TestAllowAuthorizationEvaluator Authorization { get; }
        public AdministratorDecisions Decisions { get; }
        public RecordingAuditWriter Audit { get; }

        public Task SetHiddenAsync(Guid itemId) => _assets.SetFlagsAsync(itemId, null, true);
        public ViewProfileRepository Policies { get; }
        public ViewSharedContributionService Contributions { get; }
        public ViewFolderService Folders { get; }

        public Guid SharedLibraryId()
        {
            return new ViewSharedLibraryRepository(_database).GetAsync().GetAwaiter().GetResult().LibraryId;
        }

        public Guid SharedSourceId(string sourceKey)
        {
            using var connection = _database.CreateConnection();
            return connection.QuerySingle<Guid>(
                "SELECT id FROM view_sources WHERE scope_kind='shared' AND source_key=@sourceKey;",
                new { sourceKey });
        }

        public Guid? OriginItemId(Guid sharedItemId)
        {
            using var connection = _database.CreateConnection();
            return connection.QuerySingle<Guid?>(
                "SELECT origin_item_id FROM view_shared_assets WHERE item_id=@sharedItemId;",
                new { sharedItemId });
        }

        public Guid SharedItemId(Guid originItemId)
        {
            using var connection = _database.CreateConnection();
            return connection.QuerySingle<Guid>(
                "SELECT item_id FROM view_shared_assets WHERE origin_item_id=@originItemId;",
                new { originItemId });
        }

        public Guid FileIdForPath(string path)
        {
            using var connection = _database.CreateConnection();
            return connection.QuerySingle<Guid>(
                "SELECT file_id FROM local_file_sources WHERE file_path=@path;",
                new { path = Path.GetFullPath(path) });
        }

        public Task<MediaEngine.Storage.Contracts.LocalAssetContentLocation?> ResolveSharedContentAsync(Guid itemId)
        {
            var resources = new ViewResourcePersistenceService(
                _assets, new ViewGalleryRepository(_database), _spaces, _database);
            return resources.ResolveContentAsync(itemId, MediaEngine.Storage.Contracts.LocalAssetFileRoles.Primary,
                new ResolvedViewScope(ViewScopeKind.Shared, null,
                    new HashSet<Guid> { SharedLibraryId() }));
        }

        public string WriteManaged(string name, byte[] bytes)
        {
            var source = _spaces.GetSourcesAsync(Space.Id).GetAwaiter().GetResult()
                .Single(value => value.SourceType == ViewSourceType.BrowserUpload);
            var path = Path.Combine(Storage.GetSourcePath(Space, source), name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            _configuration.Dispose();
            _database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
