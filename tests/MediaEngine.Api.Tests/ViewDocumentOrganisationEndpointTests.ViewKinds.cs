using System.Net;
using System.Net.Http.Json;
using Dapper;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

// Library setup WP8a: the three View kinds (Photos, Personal videos, Documents) each have their own permission, and every
// item records which profile added it. These tests use the same real routes and data store as the Documents tests.
public sealed partial class ViewDocumentOrganisationEndpointTests
{
    private const string AddedByCounts = "AddedByCountsNeverIncludeAProfileWhoseOnlyItemsTheCallerCannotSee";

    private async Task<Person> CreatePersonWithout(Guid? household, params string[] blockedKinds)
    {
        var person = await CreatePerson("StandardUser", household);
        await _policies.SavePolicyAsync(ViewProfilePolicy.Default(person.ProfileId) with
        {
            PhotosEnabled = !blockedKinds.Contains(ViewContentKinds.Photos),
            PersonalVideosEnabled = !blockedKinds.Contains(ViewContentKinds.PersonalVideos),
            DocumentsEnabled = !blockedKinds.Contains(ViewContentKinds.Documents),
        });
        return person;
    }

    /// <summary>A personal item of the given file type and View kind, in a source the timeline includes.</summary>
    private async Task<Guid> AddKindedItem(Person person, char seed, string mediaKind, string contentKind, string title, Guid? addedBy = null)
    {
        var space = await _spaces.GetByOwnerAsync(person.ProfileId);
        var sourceId = InsertPersonalSource(person, space!.Id, "managed");
        var extension = mediaKind switch { LocalAssetMediaKinds.Video => "mp4", LocalAssetMediaKinds.Document => "pdf", _ => "jpg" };
        return (await _assets.UpsertAsync(new LocalAssetRegistration(
            person.LibraryId, space.Id, person.ProfileId, mediaKind, title, null,
            [new LocalAssetFileRegistration($@"C:\kinds\{seed}.{extension}", HashOf(seed), $"{seed}.{extension}",
                "application/octet-stream", 1024, DateTimeOffset.UtcNow) with { SourceId = sourceId }],
            ContentKind: contentKind, AddedByProfileId: addedBy ?? person.ProfileId))).ItemId;
    }

    /// <summary>A Shared Library item added by <paramref name="addedBy"/>, in a source the timeline includes.</summary>
    private async Task<Guid> AddSharedKindedItem(Guid libraryId, char seed, string mediaKind, string contentKind, string title, Guid addedBy, bool hidden = false)
    {
        var sourceId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO view_sources
                    (id, scope_kind, personal_space_id, library_id, source_type, name, source_key, storage_mode,
                     relative_path, external_path, include_subdirectories, enabled, created_at, updated_at)
                VALUES (@sourceId, 'shared', NULL, @libraryId, 'folder', 'Shared things', @sourceKey, 'managed',
                        'Shared/Things/test', NULL, 1, 1, @now, @now);
                INSERT INTO view_source_policies (source_id, include_in_timeline, updated_at) VALUES (@sourceId, 1, @now);
                """, new { sourceId, libraryId, sourceKey = $"shared:{sourceId:N}", now = DateTimeOffset.UtcNow });
        }

        var extension = mediaKind == LocalAssetMediaKinds.Video ? "mp4" : "jpg";
        var itemId = (await _assets.UpsertAsync(new LocalAssetRegistration(
            libraryId, null, null, mediaKind, title, null,
            [new LocalAssetFileRegistration($@"C:\shared-kinds\{seed}.{extension}", HashOf(seed), $"{seed}.{extension}",
                "application/octet-stream", 1024, DateTimeOffset.UtcNow) with { SourceId = sourceId }],
            ScopeKind: LocalAssetScopeKinds.Shared, ContentKind: contentKind, AddedByProfileId: addedBy))).ItemId;
        if (hidden)
        {
            await _assets.SetFlagsAsync(itemId, null, true);
        }

        return itemId;
    }

    /// <summary>A 64-digit hexadecimal content hash made from one letter, so each seed gives a different file.</summary>
    private static string HashOf(char seed) => string.Concat(Enumerable.Repeat(((int)seed).ToString("x2"), 32));

    private async Task<IReadOnlyList<LocalAssetDto>> ListAsync(string route) =>
        (await _client.GetFromJsonAsync<ViewAssetTimelinePageDto>(route))!.Items;

    [Fact]
    public async Task AProfileWithoutPhotosGetsNoPhotoFromListingsCountsSearchOrItemReads_AndKeepsItsVideosAndDocuments()
    {
        var blocked = await CreatePersonWithout(null, ViewContentKinds.Photos);
        var photo = await AddKindedItem(blocked, 'p', LocalAssetMediaKinds.Image, ViewContentKinds.Photos, "Sunset beach");
        var video = await AddKindedItem(blocked, 'v', LocalAssetMediaKinds.Video, ViewContentKinds.PersonalVideos, "Sunset clip");
        var document = await AddKindedItem(blocked, 'd', LocalAssetMediaKinds.Document, ViewContentKinds.Documents, "Sunset lease");
        _actor.Current = blocked.Authority;

        var listed = await ListAsync("/view/assets?scope=mine");
        Assert.DoesNotContain(listed, item => item.Id == photo);
        Assert.Contains(listed, item => item.Id == video);
        Assert.Contains(listed, item => item.Id == document);

        // Searching never finds it, and the month counts leave it out.
        var found = await ListAsync("/view/assets?scope=mine&q=Sunset");
        Assert.Equal(2, found.Count);
        Assert.Contains(found, item => item.Id == video);
        Assert.Contains(found, item => item.Id == document);
        Assert.DoesNotContain(found, item => item.Id == photo);
        var index = await _client.GetFromJsonAsync<ViewTimelineIndexDto>("/view/assets/timeline-index?scope=mine");
        Assert.Equal(2, index!.Buckets.Sum(bucket => bucket.AssetCount));
        Assert.DoesNotContain(await ListAsync("/view/assets?scope=mine&kind=image"), item => item.Id == photo);

        // A photo's id is an id nobody has: not found, never forbidden.
        foreach (var route in new[] { "", "/content", "/thumbnail", "/preview", "/text" })
        {
            using var response = await _client.GetAsync($"/view/items/{photo:D}{route}?scope=mine");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var videoRead = await _client.GetAsync($"/view/items/{video:D}?scope=mine");
        Assert.Equal(HttpStatusCode.OK, videoRead.StatusCode);
    }

    [Fact]
    public async Task AProfileWithoutPersonalVideosGetsNoVideoFromListingsCountsSearchOrItemReads_AndKeepsItsPhotos()
    {
        var blocked = await CreatePersonWithout(null, ViewContentKinds.PersonalVideos);
        var photo = await AddKindedItem(blocked, 'p', LocalAssetMediaKinds.Image, ViewContentKinds.Photos, "Harbour photo");
        var video = await AddKindedItem(blocked, 'v', LocalAssetMediaKinds.Video, ViewContentKinds.PersonalVideos, "Harbour clip");
        _actor.Current = blocked.Authority;

        var found = await ListAsync("/view/assets?scope=mine&q=Harbour");
        Assert.Equal(photo, Assert.Single(found).Id);
        Assert.Equal(1, (await _client.GetFromJsonAsync<ViewTimelineIndexDto>("/view/assets/timeline-index?scope=mine"))!
            .Buckets.Sum(bucket => bucket.AssetCount));
        foreach (var route in new[] { "", "/content", "/thumbnail", "/preview" })
        {
            using var response = await _client.GetAsync($"/view/items/{video:D}{route}?scope=mine");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var photoRead = await _client.GetAsync($"/view/items/{photo:D}?scope=mine");
        Assert.Equal(HttpStatusCode.OK, photoRead.StatusCode);
    }

    [Fact]
    public async Task AJpegKeptInADocumentsFolderIsHiddenFromAProfileWithoutDocuments()
    {
        var blocked = await CreatePersonWithout(null, ViewContentKinds.Documents);
        var receipt = await AddKindedItem(blocked, 'r', LocalAssetMediaKinds.Image, ViewContentKinds.Documents, "Receipt scan");
        var photo = await AddKindedItem(blocked, 'p', LocalAssetMediaKinds.Image, ViewContentKinds.Photos, "Receipt lookalike photo");
        _actor.Current = blocked.Authority;

        var listed = await ListAsync("/view/assets?scope=mine&q=Receipt");
        Assert.Equal(photo, Assert.Single(listed).Id);
        using var read = await _client.GetAsync($"/view/items/{receipt:D}?scope=mine");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        using var content = await _client.GetAsync($"/view/items/{receipt:D}/content?scope=mine");
        Assert.Equal(HttpStatusCode.NotFound, content.StatusCode);
    }

    [Fact]
    public async Task EveryItemCarriesTheNameOfTheProfileThatAddedIt_AndTheNameFollowsARename()
    {
        var household = Guid.NewGuid();
        var alice = await CreatePerson("StandardUser", household);
        var bob = await CreatePerson("StandardUser", household);
        var shared = await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(household);
        var fromAlice = await AddSharedKindedItem(shared.LibraryId, 'a', LocalAssetMediaKinds.Image, ViewContentKinds.Photos, "Picnic a", alice.ProfileId);
        var fromBob = await AddSharedKindedItem(shared.LibraryId, 'b', LocalAssetMediaKinds.Image, ViewContentKinds.Photos, "Picnic b", bob.ProfileId);
        _actor.Current = alice.Authority;

        var all = await ListAsync("/view/assets?scope=shared");
        var aliceItem = Assert.Single(all, item => item.Id == fromAlice);
        Assert.Equal(alice.ProfileId, aliceItem.AddedByProfileId);
        Assert.Equal($"Profile {alice.ProfileId:N}", aliceItem.AddedByName);

        // One or several profile ids filter the list (and the month counts).
        var onlyBob = await ListAsync($"/view/assets?scope=shared&added_by={bob.ProfileId:D}");
        Assert.Equal(fromBob, Assert.Single(onlyBob).Id);
        var both = await ListAsync($"/view/assets?scope=shared&added_by={bob.ProfileId:D}&added_by={alice.ProfileId:D}");
        Assert.Equal(2, both.Count);
        Assert.Equal(1, (await _client.GetFromJsonAsync<ViewTimelineIndexDto>(
            $"/view/assets/timeline-index?scope=shared&added_by={alice.ProfileId:D}"))!.Buckets.Sum(bucket => bucket.AssetCount));
        Assert.Empty(await ListAsync($"/view/assets?scope=shared&added_by={Guid.NewGuid():D}"));

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET display_name = 'Alice Renamed' WHERE id = @id;", new { id = alice.ProfileId });
        }

        var renamed = Assert.Single(await ListAsync("/view/assets?scope=shared"), item => item.Id == fromAlice);
        Assert.Equal("Alice Renamed", renamed.AddedByName);
        var single = await _client.GetFromJsonAsync<LocalAssetDto>($"/view/items/{fromAlice:D}?scope=shared");
        Assert.Equal("Alice Renamed", single!.AddedByName);
    }

    [Fact]
    public async Task AddedByCountsNeverIncludeAProfileWhoseOnlyItemsTheCallerCannotSee()
    {
        var household = Guid.NewGuid();
        var viewer = await CreatePersonWithout(household, ViewContentKinds.PersonalVideos);
        var alice = await CreatePerson("StandardUser", household);
        var bob = await CreatePerson("StandardUser", household);
        var carol = await CreatePerson("StandardUser", household);
        var dave = await CreatePerson("StandardUser", household);
        var shared = await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(household);
        await AddSharedKindedItem(shared.LibraryId, 'a', LocalAssetMediaKinds.Image, ViewContentKinds.Photos, "A1", alice.ProfileId);
        await AddSharedKindedItem(shared.LibraryId, 'b', LocalAssetMediaKinds.Image, ViewContentKinds.Photos, "A2", alice.ProfileId);
        await AddSharedKindedItem(shared.LibraryId, 'c', LocalAssetMediaKinds.Image, ViewContentKinds.Photos, "B1", bob.ProfileId);
        // Carol only added a video, which the viewer may not use; Dave only a hidden photo.
        await AddSharedKindedItem(shared.LibraryId, 'd', LocalAssetMediaKinds.Video, ViewContentKinds.PersonalVideos, "C1", carol.ProfileId);
        await AddSharedKindedItem(shared.LibraryId, 'e', LocalAssetMediaKinds.Image, ViewContentKinds.Photos, "D1", dave.ProfileId, hidden: true);

        _actor.Current = viewer.Authority;
        var counts = (await _client.GetFromJsonAsync<List<ViewAddedByDto>>("/view/assets/added-by?scope=shared"))!;
        Assert.Equal([alice.ProfileId, bob.ProfileId], counts.Select(entry => entry.ProfileId).ToArray());
        Assert.Equal([2, 1], counts.Select(entry => entry.ItemCount).ToArray());
        Assert.Equal($"Profile {alice.ProfileId:N}", counts[0].Name);

        // A person who may use videos sees Carol too (the hidden photo still hides Dave).
        _actor.Current = alice.Authority;
        var everyone = (await _client.GetFromJsonAsync<List<ViewAddedByDto>>("/view/assets/added-by?scope=shared"))!;
        Assert.Equal(new[] { alice.ProfileId, bob.ProfileId, carol.ProfileId }.Order(), everyone.Select(entry => entry.ProfileId).Order());

        // Asking only for documents is refused for a profile without Documents, as for the list itself.
        var noDocuments = await CreatePersonWithout(household, ViewContentKinds.Documents);
        _actor.Current = noDocuments.Authority;
        using var refused = await _client.GetAsync("/view/assets/added-by?scope=shared&kind=document");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task AGalleryNeverShowsOrCountsAKindTheProfileMayNotUse()
    {
        var household = Guid.NewGuid();
        var owner = await CreatePerson("StandardUser", household);
        var guest = await CreatePersonWithout(household, ViewContentKinds.Photos);
        var photo = await AddKindedItem(owner, 'p', LocalAssetMediaKinds.Image, ViewContentKinds.Photos, "Gallery photo");
        var video = await AddKindedItem(owner, 'v', LocalAssetMediaKinds.Video, ViewContentKinds.PersonalVideos, "Gallery video");
        var galleries = new ViewGalleryRepository(_database);
        var gallery = await CreateManualGallery(owner, "Trip");
        await galleries.AddItemsAsync(gallery.Id, [photo, video]);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO view_gallery_shares (gallery_id, profile_id, permission, shared_at) VALUES (@galleryId, @profileId, 'view', @now);",
                new { galleryId = gallery.Id, profileId = guest.ProfileId, now = DateTimeOffset.UtcNow });
        }

        _actor.Current = owner.Authority;
        Assert.Equal(2, (await _client.GetFromJsonAsync<ViewGalleryDto>($"/view/galleries/{gallery.Id:D}"))!.ItemCount);

        _actor.Current = guest.Authority;
        var list = await _client.GetFromJsonAsync<ViewGalleryListResponse>("/view/galleries");
        Assert.Equal(1, list!.SharedWithYou.Single(entry => entry.Id == gallery.Id).ItemCount);
        Assert.Equal(1, (await _client.GetFromJsonAsync<ViewGalleryDto>($"/view/galleries/{gallery.Id:D}"))!.ItemCount);
        var page = await _client.GetFromJsonAsync<ViewGalleryItemPageDto>($"/view/galleries/{gallery.Id:D}/items");
        Assert.Equal(video, Assert.Single(page!.Items).ItemId);
        Assert.DoesNotContain(await ListAsync($"/view/assets?scope=shared&galleryId={gallery.Id:D}"), item => item.Id == photo);
    }
}
