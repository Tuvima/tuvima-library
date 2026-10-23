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
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
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
    public async Task AnAcceptedContributionRecordsTheContributorOnEachItemItBringsInAndKeepsItsKind()
    {
        using var fixture = new Fixture();
        await fixture.Policies.SavePolicyAsync(new ViewProfilePolicy(
            fixture.ProfileId, true, true, true, true, null));
        var photo = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, fixture.WriteManaged("tag-photo.jpg", [51, 52, 53]));
        var video = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, fixture.WriteManaged("tag-video.mp4", [61, 62, 63]));
        Assert.Equal(ViewContentKinds.Photos, fixture.Assets.Find(photo!.ItemId)!.ContentKind);
        Assert.Equal(ViewContentKinds.PersonalVideos, fixture.Assets.Find(video!.ItemId)!.ContentKind);
        var preview = await fixture.Contributions.PreviewAsync(fixture.Authority,
            new ViewSharedContributionPreviewRequest([photo.ItemId, video.ItemId]));
        var pending = await fixture.Contributions.SubmitAsync(fixture.Authority,
            new ViewSharedContributionSubmitRequest([photo.ItemId, video.ItemId], "timeline", null, null, preview.PreviewRevision, "tag-request"));

        await fixture.Contributions.DecideAsync(fixture.Authority, pending.Id,
            new ViewSharedContributionDecisionRequest("accepted", pending.Revision));
        await fixture.Contributions.ProcessAsync(pending.Id);

        foreach (var (origin, kind) in new[] { (photo.ItemId, ViewContentKinds.Photos), (video.ItemId, ViewContentKinds.PersonalVideos) })
        {
            var copy = fixture.Assets.Find(fixture.SharedItemId(origin))!;
            Assert.Equal(fixture.ProfileId, copy.AddedByProfileId);
            Assert.Equal("Household administrator", copy.AddedByName);
            Assert.Equal(kind, copy.ContentKind);
        }
    }

    [Fact]
    public async Task PhotosAContributorSendsStayTaggedWithTheContributorNotTheAdministratorWhoAcceptedThem_AndFilterByThemAndByTag()
    {
        using var fixture = new Fixture();
        var bobId = Guid.NewGuid();
        await fixture.InsertProfileAsync(new Profile { Id = bobId, DisplayName = "Bob", Role = ProfileRole.StandardUser });
        var bobSpace = await fixture.Storage.EnsurePersonalSpaceAsync(bobId);
        var first = await fixture.Library.IndexPathAsync(bobSpace.LibraryId, fixture.WriteManagedFor(bobSpace, "bob-one.jpg", [71, 72, 73]));
        var second = await fixture.Library.IndexPathAsync(bobSpace.LibraryId, fixture.WriteManagedFor(bobSpace, "bob-two.jpg", [74, 75, 76]));
        await fixture.Assets.ReplaceTagsAsync(first!.ItemId, ["Holiday"]);
        var bob = fixture.Person(bobId);
        var preview = await fixture.Contributions.PreviewAsync(bob,
            new ViewSharedContributionPreviewRequest([first.ItemId, second!.ItemId]));
        var pending = await fixture.Contributions.SubmitAsync(bob, new ViewSharedContributionSubmitRequest(
            [first.ItemId, second.ItemId], "timeline", null, null, preview.PreviewRevision, "bob-sends-two"));

        var administrator = fixture.HouseholdAdministrator(ProfileTestData.TestHouseholdId);
        await fixture.Contributions.DecideAsync(administrator, pending.Id,
            new ViewSharedContributionDecisionRequest("accepted", pending.Revision));
        await fixture.Contributions.ProcessAsync(pending.Id);

        foreach (var origin in new[] { first.ItemId, second.ItemId })
        {
            var copy = fixture.Assets.Find(fixture.SharedItemId(origin))!;
            Assert.Equal(bobId, copy.AddedByProfileId);
            Assert.Equal("Bob", copy.AddedByName);
        }

        var orchestrator = fixture.NewOrchestrator(new StubProfileContext(administrator));
        var byBob = await orchestrator.QueryAsync(new ViewAssetQueryRequest(
            ViewScopeRequest.Shared, AddedByProfileIds: [bobId]));
        Assert.Equal(2, Assert.IsType<ViewAssetTimelinePageDto>(byBob.Page).Items.Count);
        var byAnyoneElse = await orchestrator.QueryAsync(new ViewAssetQueryRequest(
            ViewScopeRequest.Shared, AddedByProfileIds: [administrator.ActiveProfileId!.Value]));
        Assert.Empty(Assert.IsType<ViewAssetTimelinePageDto>(byAnyoneElse.Page).Items);

        // The tag filter and the added-by filter combine: only Bob's tagged photo remains.
        var tagged = await orchestrator.QueryAsync(new ViewAssetQueryRequest(
            ViewScopeRequest.Shared, AddedByProfileIds: [bobId],
            DocumentFilter: new LocalAssetDocumentFilter([fixture.TagId("Holiday")])));
        Assert.Equal(fixture.SharedItemId(first.ItemId), Assert.Single(Assert.IsType<ViewAssetTimelinePageDto>(tagged.Page).Items).Id);
        var index = await orchestrator.IndexAsync(new ViewAssetQueryRequest(
            ViewScopeRequest.Shared, AddedByProfileIds: [bobId],
            DocumentFilter: new LocalAssetDocumentFilter([fixture.TagId("Holiday")])));
        Assert.Equal(1, Assert.IsType<ViewTimelineIndexDto>(index.Index).Buckets.Sum(bucket => bucket.AssetCount));
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
        var nonAdministrator = fixture.Authority with { ActiveProfileIsServerAdministrator = false };

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
            ActiveProfileInUserHousehold = grantEnabled,
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
    public async Task AMoveIntoTheSharedLibrary_QueuesTheNewSharedRowForTheProfileNameWrite_AfterTheMoveIsDone()
    {
        using var fixture = new Fixture();
        var original = fixture.WriteManaged("moved.jpg", [21, 22, 23]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        fixture.WriteQueue.ViewItems.Clear();

        var result = await fixture.Transfers.ExecuteAsync(indexed!.ItemId, fixture.ProfileId, "timeline", null);

        Assert.Equal("completed", result.State);
        var queued = Assert.Single(fixture.WriteQueue.ViewItems);
        Assert.Equal(fixture.SharedItemId(indexed.ItemId), queued.ItemId);
        Assert.Equal(MediaEngine.Domain.Constants.MediaFileWriteTriggers.ViewAddedBy, queued.Trigger);
        Assert.False(File.Exists(original));
        await fixture.Transfers.ExecuteAsync(indexed.ItemId, fixture.ProfileId, "timeline", null);
        Assert.Single(fixture.WriteQueue.ViewItems);
    }

    [Fact]
    public async Task ACopyIntoTheSharedLibrary_QueuesTheNewSharedRowToo_AndLeavesTheOriginalAlone()
    {
        using var fixture = new Fixture();
        var original = fixture.WriteManaged("copied.jpg", [31, 32, 33]);
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        fixture.WriteQueue.ViewItems.Clear();

        var result = await fixture.Transfers.ExecuteAsync(indexed!.ItemId, fixture.ProfileId, "timeline", null, copyOnly: true);

        Assert.Equal("completed", result.State);
        Assert.Equal(fixture.SharedItemId(indexed.ItemId), Assert.Single(fixture.WriteQueue.ViewItems).ItemId);
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
        await fixture.Storage.AddLinkedSourceAsync(fixture.Space, "Home Movies", linkedRoot.FullName, true, contentKind: ViewContentKinds.PersonalVideos);
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
    public async Task ADocumentCopiedToTheSharedLibraryKeepsItsDetailsNotesAndFieldValuesTogether()
    {
        using var fixture = new Fixture();
        var linkedRoot = Directory.CreateDirectory(Path.Combine(fixture.Root, "linked-docs"));
        await fixture.Storage.AddLinkedSourceAsync(fixture.Space, "Paperwork", linkedRoot.FullName, true, contentKind: ViewContentKinds.Documents);
        var original = Path.Combine(linkedRoot.FullName, "water-bill.txt");
        await File.WriteAllTextAsync(original, "Water bill for the house");
        var indexed = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, original);
        Assert.NotNull(indexed);
        var viewer = new MediaEngine.Storage.Contracts.ViewCatalogueViewer(fixture.ProfileId, [fixture.Space.LibraryId]);
        var status = await fixture.Catalogue.CreateFieldAsync(viewer, "Status", ViewDocumentFieldKinds.Choice, ["Open", "Paid"]);
        var amount = await fixture.Catalogue.CreateFieldAsync(viewer, "Amount", ViewDocumentFieldKinds.Money, null);
        await fixture.Catalogue.UpdateDocumentDetailsAsync(viewer, indexed!.ItemId,
            new MediaEngine.Storage.Contracts.ViewDocumentDetailsWrite(
                "Water bill", new DateOnly(2026, 3, 1), "City Water", "Bill", "A-17", "Pay by April, ask about the meter",
                ViewDocumentFilingStates.Filed, ["Home"], [],
                [
                    new MediaEngine.Storage.Contracts.ViewDocumentFieldWrite(status.Id, Text: "Open"),
                    new MediaEngine.Storage.Contracts.ViewDocumentFieldWrite(amount.Id, Text: "EUR", Number: 120.5),
                ]));

        await fixture.Transfers.ExecuteAsync(indexed.ItemId, fixture.ProfileId, "folder", "Paperwork");

        var original2 = fixture.Assets.Find(indexed.ItemId)!.DocumentInfo!;
        var copy = fixture.Assets.Find(fixture.SharedItemId(indexed.ItemId))!.DocumentInfo!;
        Assert.Equal(new DateOnly(2026, 3, 1), copy.DocumentDate);
        Assert.Equal("City Water", copy.Correspondent!.Name);
        Assert.Equal("Bill", copy.DocumentType!.Name);
        Assert.Equal("A-17", copy.ArchiveNumber);
        Assert.Equal("Pay by April, ask about the meter", copy.Notes);
        Assert.Equal(ViewDocumentFilingStates.Filed, copy.FilingState);
        Assert.Equal(["Home"], copy.Tags!.Select(tag => tag.Name).ToArray());
        Assert.Equal(
            original2.FieldValues!.OrderBy(value => value.Name).Select(value => (value.Name, value.TextValue, value.NumberValue)),
            copy.FieldValues!.OrderBy(value => value.Name).Select(value => (value.Name, value.TextValue, value.NumberValue)));
        Assert.Equal(2, copy.FieldValues!.Count);

        // The copy references the same shared names (one list for the install), not duplicates of them.
        Assert.Equal(original2.Correspondent!.Id, copy.Correspondent.Id);
        Assert.Equal(original2.DocumentType!.Id, copy.DocumentType.Id);
        Assert.Equal(original2.Tags![0].Id, copy.Tags![0].Id);
        // And the original keeps its own.
        Assert.Equal("Pay by April, ask about the meter", original2.Notes);

        // The copy is found in Shared by its text, notes, correspondent, type and tag as soon as it exists.
        var sharedItemId = fixture.SharedItemId(indexed.ItemId);
        foreach (var word in new[] { "house", "meter", "City", "Bill", "Home" })
        {
            var found = fixture.Assets.QueryTimeline(new LocalAssetTimelineQuery([fixture.SharedLibraryId()], Search: word));
            Assert.True(found.Items.Any(item => item.Id == sharedItemId), $"Searching Shared for '{word}' should find the copy.");
        }
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
    public async Task FolderCountsAndPages_LeaveDocumentsOutWhenTheProfileMayNotUseThem()
    {
        using var fixture = new Fixture();
        var photo = fixture.WriteManaged(Path.Combine("Trip", "photo.jpg"), [1, 2, 3, 4]);
        var document = fixture.WriteManaged(Path.Combine("Trip", "passport.pdf"), DocumentFixtures.Pdf("passport words"));
        var deeper = fixture.WriteManaged(Path.Combine("Papers", "bill.pdf"), DocumentFixtures.Pdf("bill words"));
        await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, photo);
        var indexedDocument = await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, document);
        await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, deeper);
        Assert.Equal(LocalAssetMediaKinds.Document, fixture.Assets.Find(indexedDocument!.ItemId)!.MediaKind);
        var source = Assert.Single(await fixture.Sources.GetSourcesAsync(fixture.Space.Id));
        var scope = new ResolvedViewScope(ViewScopeKind.Mine, fixture.ProfileId, new HashSet<Guid> { fixture.Space.LibraryId });

        var withDocuments = await fixture.Folders.QueryAsync(fixture.ProfileId, scope, null, null, false, null, 0, 100);
        var withoutDocuments = await fixture.Folders.QueryAsync(fixture.ProfileId, scope, null, null, false, null, 0, 100, includeDocuments: false);
        Assert.Equal(3, Assert.Single(withDocuments.Sources).ItemCount);
        Assert.Equal(1, Assert.Single(withoutDocuments.Sources).ItemCount);

        var folders = await fixture.Folders.QueryAsync(fixture.ProfileId, scope, source.Id, null, false, null, 0, 100, includeDocuments: false);
        Assert.Equal(["Trip"], folders.Folders.Select(folder => folder.Name).ToArray());
        Assert.Equal(1, Assert.Single(folders.Folders).ItemCount);

        // One photo and one document share the folder: a page of one has no "more" once the document is out of the query.
        var page = await fixture.Folders.QueryAsync(fixture.ProfileId, scope, source.Id, "Trip", false, null, 0, 1, includeDocuments: false);
        Assert.Equal("photo.jpg", Assert.Single(page.Items).FileName);
        Assert.False(page.HasMore);
        var unfiltered = await fixture.Folders.QueryAsync(fixture.ProfileId, scope, source.Id, "Trip", false, null, 0, 1);
        Assert.True(unfiltered.HasMore);
    }

    [Fact]
    public async Task FolderCountsPagesAndListsLeaveOutAKindTheProfileMayNotUse()
    {
        using var fixture = new Fixture();
        var photo = fixture.WriteManaged(Path.Combine("Trip", "photo.jpg"), [1, 2, 3, 4]);
        var video = fixture.WriteManaged(Path.Combine("Trip", "clip.mp4"), [5, 6, 7]);
        var document = fixture.WriteManaged(Path.Combine("Trip", "passport.pdf"), DocumentFixtures.Pdf("passport words"));
        await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, photo);
        await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, video);
        await fixture.Library.IndexPathAsync(fixture.Space.LibraryId, document);
        var linkedRoot = Directory.CreateDirectory(Path.Combine(fixture.Root, "linked-pictures"));
        await fixture.Storage.AddLinkedSourceAsync(fixture.Space, "Pictures", linkedRoot.FullName, true,
            contentKind: ViewContentKinds.Photos);
        var uploads = Assert.Single(await fixture.Sources.GetSourcesAsync(fixture.Space.Id), source => source.SourceType == ViewSourceType.BrowserUpload);
        var scope = new ResolvedViewScope(ViewScopeKind.Mine, fixture.ProfileId, new HashSet<Guid> { fixture.Space.LibraryId });

        var everything = await fixture.Folders.QueryAsync(fixture.ProfileId, scope, null, null, false, null, 0, 100);
        Assert.Equal(2, everything.Sources.Count);
        Assert.Equal(3, everything.Sources.Single(source => source.SourceId == uploads.Id).ItemCount);

        // Without Photos: the photo is gone from the counts and the page, and so is the folder that holds only photos.
        var withoutPhotos = await fixture.Folders.QueryAsync(fixture.ProfileId, scope, null, null, false, null, 0, 100,
            blockedKinds: [ViewContentKinds.Photos]);
        var remaining = Assert.Single(withoutPhotos.Sources);
        Assert.Equal(uploads.Id, remaining.SourceId);
        Assert.Equal(2, remaining.ItemCount);
        var page = await fixture.Folders.QueryAsync(fixture.ProfileId, scope, uploads.Id, "Trip", false, null, 0, 100,
            blockedKinds: [ViewContentKinds.Photos]);
        Assert.DoesNotContain(page.Items, item => item.FileName == "photo.jpg");
        Assert.Equal(2, page.Items.Count);

        var withoutVideos = await fixture.Folders.QueryAsync(fixture.ProfileId, scope, uploads.Id, "Trip", false, null, 0, 100,
            blockedKinds: [ViewContentKinds.PersonalVideos]);
        Assert.DoesNotContain(withoutVideos.Items, item => item.FileName == "clip.mp4");
        Assert.Equal(2, withoutVideos.Items.Count);
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
        var server = fixture.Authority with { ActiveProfileHouseholdId = ProfileTestData.TestHouseholdId };
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

    private static async Task<ViewSharedContributionDto> SubmitManyAsync(
        Fixture fixture, RequestAuthority person, string key, params Guid[] itemIds)
    {
        var preview = await fixture.Contributions.PreviewAsync(person, new ViewSharedContributionPreviewRequest(itemIds));
        return await fixture.Contributions.SubmitAsync(person, new ViewSharedContributionSubmitRequest(
            itemIds, "timeline", null, null, preview.PreviewRevision, key));
    }

    [Fact]
    public async Task ContributionHistory_StaysVisible_WithAPlainPlaceholder_AfterItsItemWasRemoved_EvenForAReviewerWithoutDocuments()
    {
        using var fixture = new Fixture();
        var maryId = Guid.NewGuid();
        await fixture.InsertProfileAsync(new Profile { Id = maryId, DisplayName = "Mary", Role = ProfileRole.StandardUser });
        var marySpace = await fixture.Storage.EnsurePersonalSpaceAsync(maryId);
        async Task<Guid> IndexAsync(string name, byte[] bytes) =>
            (await fixture.Library.IndexPathAsync(marySpace.LibraryId, fixture.WriteManagedFor(marySpace, name, bytes)))!.ItemId;
        var tax = await IndexAsync("Tax return 2025.txt", [1, 2, 3]);
        var letter = await IndexAsync("Lawyer letter.txt", [4, 5, 6]);
        var beach = await IndexAsync("Beach day.jpg", [7, 8, 9]);
        var sunset = await IndexAsync("Sunset walk.jpg", [10, 11, 12]);
        var mary = fixture.Person(maryId);
        var documentsOnly = await SubmitManyAsync(fixture, mary, "documents", tax);
        var removed = await SubmitManyAsync(fixture, mary, "removed", beach);
        var mixed = await SubmitManyAsync(fixture, mary, "mixed", sunset, letter);
        var admin = fixture.HouseholdAdministrator(ProfileTestData.TestHouseholdId);

        // The photo's owner empties their Trash: the item row is gone and the history link clears.
        fixture.RemoveItem(beach);

        var withDocuments = await fixture.Contributions.ListAsync(admin, "review", null, 0, 20);
        var shown = withDocuments.Items.Single(contribution => contribution.Id == removed.Id);
        var line = Assert.Single(shown.Items);
        Assert.Equal("Removed item", line.Title);
        Assert.Equal(Guid.Empty, line.ItemId);
        Assert.DoesNotContain("Beach", System.Text.Json.JsonSerializer.Serialize(shown), StringComparison.OrdinalIgnoreCase);

        await fixture.Policies.SavePolicyAsync(
            (await fixture.Policies.GetPolicyAsync(fixture.ProfileId)) with { DocumentsEnabled = false });
        var withoutDocuments = await fixture.Contributions.ListAsync(admin, "review", null, 0, 20);

        // Still listed and still opens; the documents-only one stays hidden and the mixed one still shows only its photo.
        Assert.Contains(withoutDocuments.Items, contribution => contribution.Id == removed.Id);
        Assert.DoesNotContain(withoutDocuments.Items, contribution => contribution.Id == documentsOnly.Id);
        Assert.Equal(sunset, Assert.Single(withoutDocuments.Items.Single(contribution => contribution.Id == mixed.Id).Items).ItemId);
        var opened = await fixture.Contributions.GetRequiredAsync(admin, removed.Id, true);
        Assert.Equal("Removed item", Assert.Single(opened.Items).Title);
    }

    [Fact]
    public async Task AReviewerWithoutDocumentsNeverSeesADocumentInAContribution()
    {
        using var fixture = new Fixture();
        var maryId = Guid.NewGuid();
        await fixture.InsertProfileAsync(new Profile { Id = maryId, DisplayName = "Mary", Role = ProfileRole.StandardUser });
        var marySpace = await fixture.Storage.EnsurePersonalSpaceAsync(maryId);
        async Task<Guid> IndexAsync(string name, byte[] bytes) =>
            (await fixture.Library.IndexPathAsync(marySpace.LibraryId, fixture.WriteManagedFor(marySpace, name, bytes)))!.ItemId;
        var tax = await IndexAsync("Tax return 2025.txt", [1, 2, 3]);
        var letter = await IndexAsync("Lawyer letter.txt", [4, 5, 6]);
        var beach = await IndexAsync("Beach day.jpg", [7, 8, 9]);
        var sunset = await IndexAsync("Sunset walk.jpg", [10, 11, 12]);
        var mary = fixture.Person(maryId);
        var documentsOnly = await SubmitManyAsync(fixture, mary, "documents", tax);
        var photosOnly = await SubmitManyAsync(fixture, mary, "photos", sunset);
        var mixed = await SubmitManyAsync(fixture, mary, "mixed", letter, beach);

        var admin = fixture.HouseholdAdministrator(ProfileTestData.TestHouseholdId);

        // The transfer log has one line per item per step (and an error can name a file), so for the mixed one it
        // would show how many items were left out.
        fixture.AddEvent(mixed.Id, "transfer_transferring", null);
        fixture.AddEvent(mixed.Id, "transfer_needs_attention", "Could not move Lawyer letter.txt");

        // With Documents the reviewer sees all three, document names included.
        var everything = await fixture.Contributions.ListAsync(admin, "review", null, 0, 20);
        Assert.Equal(3, everything.Items.Count);
        Assert.Equal(2, everything.Items.Single(contribution => contribution.Id == mixed.Id).Events.Count(entry => entry.EventType.StartsWith("transfer_", StringComparison.Ordinal)));
        Assert.All(everything.Items, contribution => Assert.False(contribution.IncludesHiddenItems));
        Assert.Equal(2, everything.Items.Single(contribution => contribution.Id == mixed.Id).Items.Count);

        await fixture.Policies.SavePolicyAsync(
            (await fixture.Policies.GetPolicyAsync(fixture.ProfileId)) with { DocumentsEnabled = false });

        // Without Documents: the documents-only contribution is not listed at all, the mixed one shows its photo
        // only, and nothing in the answer carries a document's name, kind, id or count.
        var visible = await fixture.Contributions.ListAsync(admin, "review", null, 0, 20);
        Assert.Equal(new[] { photosOnly.Id, mixed.Id }.Order(), visible.Items.Select(contribution => contribution.Id).Order());
        var mixedView = visible.Items.Single(contribution => contribution.Id == mixed.Id);
        Assert.Equal(beach, Assert.Single(mixedView.Items).ItemId);
        Assert.True(mixedView.IncludesHiddenItems);
        Assert.False(visible.Items.Single(contribution => contribution.Id == photosOnly.Id).IncludesHiddenItems);
        var wire = System.Text.Json.JsonSerializer.Serialize(visible);
        foreach (var leak in new[] { "Tax return", "Lawyer letter", tax.ToString("D"), letter.ToString("D"), "\"document\"", ".txt" })
        {
            Assert.DoesNotContain(leak, wire, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("1 item(s) submitted.", Assert.Single(mixedView.Events, entry => entry.EventType == "submitted").Detail);
        Assert.DoesNotContain(mixedView.Events, entry => entry.EventType.StartsWith("transfer_", StringComparison.Ordinal));

        // Opening them directly: the documents-only one does not exist, the mixed one is filtered the same way.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Contributions.GetRequiredAsync(admin, documentsOnly.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Contributions.GetRequiredAsync(admin, documentsOnly.Id, true));
        var detail = await fixture.Contributions.GetRequiredAsync(admin, mixed.Id);
        Assert.True(detail.IncludesHiddenItems);
        Assert.DoesNotContain("Lawyer letter", System.Text.Json.JsonSerializer.Serialize(detail), StringComparison.OrdinalIgnoreCase);

        // Approving or declining a contribution that holds a document is refused, nothing changes; one without is fine.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Contributions.DecideAsync(
            admin, mixed.Id, new ViewSharedContributionDecisionRequest("accepted", mixed.Revision)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Contributions.DecideAsync(
            admin, mixed.Id, new ViewSharedContributionDecisionRequest("declined", mixed.Revision)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Contributions.RetryAsync(admin, mixed.Id, mixed.Revision));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Contributions.DecideAsync(
            admin, documentsOnly.Id, new ViewSharedContributionDecisionRequest("declined", documentsOnly.Revision)));
        Assert.Equal("pending", (await fixture.Contributions.GetRequiredAsync(admin, mixed.Id)).Status);
        var decided = await fixture.Contributions.DecideAsync(
            admin, photosOnly.Id, new ViewSharedContributionDecisionRequest("declined", photosOnly.Revision));
        Assert.Equal("declined", decided.Status);

        // The contributor's own "mine" list is held to the same rule once their Documents is off.
        await fixture.Policies.SavePolicyAsync(
            (await fixture.Policies.GetPolicyAsync(maryId)) with { DocumentsEnabled = false });
        var mine = await fixture.Contributions.ListAsync(mary, "mine", null, 0, 20);
        Assert.DoesNotContain(mine.Items, contribution => contribution.Id == documentsOnly.Id);
        Assert.True(mine.Items.Single(contribution => contribution.Id == mixed.Id).IncludesHiddenItems);

        // Only the person who submitted it can cancel it: a reviewer is told it is not there. Without Documents the
        // contributor cannot cancel a documents-only one (it does not exist for them), and cancelling a mixed one
        // answers with the same filtered view.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Contributions.CancelAsync(admin, mixed.Id, mixed.Revision));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Contributions.CancelAsync(mary, documentsOnly.Id, documentsOnly.Revision));
        var cancelledMixed = await fixture.Contributions.CancelAsync(mary, mixed.Id, mixed.Revision);
        Assert.Equal("cancelled", cancelledMixed.Status);
        Assert.Equal(beach, Assert.Single(cancelledMixed.Items).ItemId);
        Assert.True(cancelledMixed.IncludesHiddenItems);

        // The reviewer with Documents back sees it all again.
        await fixture.Policies.SavePolicyAsync(
            (await fixture.Policies.GetPolicyAsync(fixture.ProfileId)) with { DocumentsEnabled = true });
        Assert.Equal(2, (await fixture.Contributions.GetRequiredAsync(admin, mixed.Id)).Items.Count);
        Assert.Single((await fixture.Contributions.GetRequiredAsync(admin, documentsOnly.Id)).Items);

        // Once the contributor has Documents again they can cancel their documents-only contribution.
        await fixture.Policies.SavePolicyAsync(
            (await fixture.Policies.GetPolicyAsync(maryId)) with { DocumentsEnabled = true });
        Assert.Equal("cancelled", (await fixture.Contributions.CancelAsync(mary, documentsOnly.Id, documentsOnly.Revision)).Status);
    }

    [Fact]
    public async Task AProfileWithoutDocumentsCannotPreviewSubmitOrDirectAddADocument()
    {
        using var fixture = new Fixture();
        var maryId = Guid.NewGuid();
        var jimId = Guid.NewGuid();
        await fixture.InsertProfileAsync(new Profile { Id = maryId, DisplayName = "Mary", Role = ProfileRole.StandardUser });
        await fixture.InsertProfileAsync(new Profile { Id = jimId, DisplayName = "Jim", Role = ProfileRole.StandardUser });
        var marySpace = await fixture.Storage.EnsurePersonalSpaceAsync(maryId);
        await fixture.Storage.EnsurePersonalSpaceAsync(jimId);
        var document = (await fixture.Library.IndexPathAsync(marySpace.LibraryId,
            fixture.WriteManagedFor(marySpace, "Passport scan.txt", [1, 2])))!.ItemId;
        var photo = (await fixture.Library.IndexPathAsync(marySpace.LibraryId,
            fixture.WriteManagedFor(marySpace, "Kitchen.jpg", [3, 4])))!.ItemId;
        foreach (var id in new[] { maryId, jimId, fixture.ProfileId })
        {
            await fixture.Policies.SavePolicyAsync((await fixture.Policies.GetPolicyAsync(id)) with { DocumentsEnabled = false });
        }

        // The owner is told plainly; a household member is told what they would be told for something that is not there.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Contributions.PreviewAsync(
            fixture.Person(maryId), new ViewSharedContributionPreviewRequest([photo, document])));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Contributions.PreviewAsync(
            fixture.Person(jimId), new ViewSharedContributionPreviewRequest([document])));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Contributions.SubmitAsync(
            fixture.Person(maryId), new ViewSharedContributionSubmitRequest([document], "timeline", null, null, "x", "key")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Contributions.AddDirectAsync(
            fixture.HouseholdAdministrator(ProfileTestData.TestHouseholdId) with { ActiveProfileId = fixture.ProfileId },
            new ViewSharedDirectAddRequest([document])));

        // Photos are unaffected.
        var preview = await fixture.Contributions.PreviewAsync(
            fixture.Person(maryId), new ViewSharedContributionPreviewRequest([photo]));
        Assert.Single(preview.Items);
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
                AccountEnabled: true, ActiveProfileInUserHousehold: true,
                ActiveProfileIsServerAdministrator: true);
            Storage = new ViewStorageService(_configuration, _spaces, new ViewSharedLibraryRepository(_database));
            Space = Storage.EnsurePersonalSpaceAsync(ProfileId).GetAwaiter().GetResult();
            Library = new ViewLibraryService(_assets, _configuration, _spaces, Storage,
                NullLogger<ViewLibraryService>.Instance, fileWrites: WriteQueue);
            Transfers = new ViewSharedTransferService(_database, _assets, Storage, WriteQueue);
            Policies = new ViewProfileRepository(_database);
            Authorization = new TestAllowAuthorizationEvaluator();
            Decisions = new AdministratorDecisions();
            Audit = new RecordingAuditWriter();
            Contributions = new ViewSharedContributionService(_database, _assets, Policies, Transfers,
                Authorization, new ViewSharedContributionQueue(), Decisions, Audit);
            Folders = new ViewFolderService(_database, _spaces, Profiles, _assets, Storage);
        }

        public string Root { get; }
        public RecordingFileWriteQueue WriteQueue { get; } = new();
        public Guid ProfileId { get; }
        public RequestAuthority Authority { get; }
        public ViewPersonalSpace Space { get; }
        public ViewPersonalSpaceRepository Sources => _spaces;
        public ProfileRepository Profiles { get; }

        public Task InsertProfileAsync(Profile profile, Guid? householdId = null) =>
            ProfileTestData.InsertAsync(_database, profile, householdId);

        /// <summary>An ordinary signed-in person acting as the given profile.</summary>
        public RequestAuthority Person(Guid profileId) => new(PrincipalKind.Human, true, Guid.NewGuid(), profileId,
            AccountEnabled: true, ActiveProfileInUserHousehold: true);

        /// <summary>A household administrator (not a server administrator) of the given household.</summary>
        public RequestAuthority HouseholdAdministrator(Guid householdId) => Person(ProfileId) with
        {
            ActiveProfileHouseholdId = householdId,
            ActiveProfileIsHouseholdAdministrator = true,
        };

        public ViewResourceAuthorizationService NewAuthorization() => new(
            new ViewScopeResolver(new ViewScopePersistenceService(
                Profiles, Policies, _spaces, new ViewSharedLibraryRepository(_database), _database)),
            new ViewResourcePersistenceService(_assets, new ViewGalleryRepository(_database), _spaces, _database),
            Authorization);

        public ViewQueryOrchestrator NewOrchestrator(IViewRequestProfileContext context) =>
            new(context, NewAuthorization(), new ViewAssetQueryService(_assets), Policies);

        public ViewDiscoveryService NewDiscovery(IViewRequestProfileContext context) =>
            new(context, NewAuthorization(), new ViewDiscoveryRepository(_database), Policies, _assets);

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

        public MediaEngine.Storage.ViewCatalogueRepository Catalogue => new(_database);
        public LocalAssetRepository Assets => _assets;

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

        public Guid TagId(string name)
        {
            using var connection = _database.CreateConnection();
            return connection.QuerySingle<Guid>("SELECT id FROM tags WHERE name = @name;", new { name });
        }

        public Guid SharedItemId(Guid originItemId)
        {
            using var connection = _database.CreateConnection();
            return connection.QuerySingle<Guid>(
                "SELECT item_id FROM view_shared_assets WHERE origin_item_id=@originItemId;",
                new { originItemId });
        }

        public void AddEvent(Guid contributionId, string eventType, string? detail)
        {
            using var connection = _database.CreateConnection();
            connection.Execute("""
                INSERT INTO view_shared_contribution_events (id, contribution_id, actor_name, event_type, detail, occurred_at)
                VALUES (@id, @contributionId, 'Tuvima Library', @eventType, @detail, @now);
                """, new { id = Guid.NewGuid(), contributionId, eventType, detail, now = DateTimeOffset.UtcNow });
        }

        /// <summary>Removes an item's row the way Empty Trash does; its history links clear instead of blocking it.</summary>
        public void RemoveItem(Guid itemId)
        {
            using var connection = _database.CreateConnection();
            connection.Execute("DELETE FROM local_items WHERE id = @itemId;", new { itemId });
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
            TestTemp.ReleaseSqlitePoolsIn(Root);
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
