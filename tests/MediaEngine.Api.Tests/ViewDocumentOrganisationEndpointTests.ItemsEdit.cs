using System.Net;
using System.Net.Http.Json;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

// Photos, videos and documents editor, WP1: one Save that checks everything first and changes all or none.
public sealed partial class ViewDocumentOrganisationEndpointTests
{
    private async Task<Guid> AddPhoto(Person person, char seed, DateTimeOffset? capturedAt = null)
    {
        var id = await AddKindedItem(person, seed, LocalAssetMediaKinds.Image, ViewContentKinds.Photos, $"IMG_{seed}");
        if (capturedAt is { } at)
        {
            await _assets.UpdateCapturedAtAsync(id, at, false);
        }

        return id;
    }

    private async Task<ViewItemsEditResultDto> SaveEdit(ViewItemsEditRequest request)
    {
        using var response = await _client.PostAsJsonAsync("/view/items/edit", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ViewItemsEditResultDto>())!;
    }

    [Fact]
    public async Task OneSaveOfTagsAndAShiftedDateChangesEveryChosenPhotoByExactlyTheSameAmount()
    {
        var person = await CreatePerson("StandardUser");
        _actor.Current = person.Authority;
        // Photos taken across the clocks going forward: a plain 3 hours later is still exactly 3 hours later.
        var first = await AddPhoto(person, 'a', new DateTimeOffset(2026, 3, 8, 1, 30, 0, TimeSpan.FromHours(-8)));
        var second = await AddPhoto(person, 'b', new DateTimeOffset(2026, 3, 8, 23, 30, 0, TimeSpan.FromHours(-7)));
        var third = await AddPhoto(person, 'c', new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

        var result = await SaveEdit(new ViewItemsEditRequest(
            [first, second, third], TagsAdd: ["Lisbon"], CapturedShiftSeconds: 3 * 3600));

        Assert.True(result.Saved);
        Assert.Equal(3, result.ChangedCount);
        foreach (var (id, was) in new[]
                 {
                     (first, new DateTimeOffset(2026, 3, 8, 1, 30, 0, TimeSpan.FromHours(-8))),
                     (second, new DateTimeOffset(2026, 3, 8, 23, 30, 0, TimeSpan.FromHours(-7))),
                     (third, new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)),
                 })
        {
            var item = _assets.Find(id)!;
            Assert.Equal(was.AddHours(3).UtcDateTime, item.CapturedAt!.Value.UtcDateTime);
            Assert.Contains("Lisbon", item.Tags);
        }
    }

    [Fact]
    public async Task APreviewChangesNothingButCountsPhotosThatMoveToAnotherDay()
    {
        var person = await CreatePerson("StandardUser");
        _actor.Current = person.Authority;
        var late = await AddPhoto(person, 'a', new DateTimeOffset(2026, 3, 8, 22, 0, 0, TimeSpan.Zero));
        var early = await AddPhoto(person, 'b', new DateTimeOffset(2026, 3, 8, 9, 0, 0, TimeSpan.Zero));

        var preview = await SaveEdit(new ViewItemsEditRequest([late, early], PreviewOnly: true, CapturedShiftSeconds: 3 * 3600, TagsAdd: ["x"]));

        Assert.True(preview.Valid);
        Assert.False(preview.Saved);
        Assert.Equal(1, preview.DayMoves);
        Assert.Empty(_assets.Find(late)!.Tags);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 22, 0, 0, TimeSpan.Zero), _assets.Find(late)!.CapturedAt);
    }

    [Fact]
    public async Task AProfileWithoutDocumentsCannotEditADocumentWithTheOneSave()
    {
        var blocked = await CreatePersonWithoutDocuments();
        var document = await AddDocument(blocked, 'a');
        _actor.Current = blocked.Authority;

        using var response = await _client.PostAsJsonAsync("/view/items/edit",
            new ViewItemsEditRequest([document], TagsAdd: ["Sneaky"], Document: new ViewItemsDocumentEdit(SetCorrespondent: true, Correspondent: "Someone")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_assets.Find(document)!.Tags);
    }

    [Fact]
    public async Task OneItemThePersonMayNotChangeStopsTheWholeSaveAndNothingIsWritten()
    {
        var mine = await CreatePerson("StandardUser");
        var theirs = await CreatePerson("StandardUser");
        var yours = await AddPhoto(mine, 'a');
        var other = await AddPhoto(theirs, 'b');
        _actor.Current = mine.Authority;

        var result = await SaveEdit(new ViewItemsEditRequest([yours, other], TagsAdd: ["Sneaky"]));

        Assert.False(result.Saved);
        Assert.Contains(result.Refusals, refusal => refusal.ItemId == other);
        Assert.Empty(_assets.Find(yours)!.Tags);
        Assert.Empty(_assets.Find(other)!.Tags);
    }

    [Fact]
    public async Task ADescriptionIsRefusedForAGroup_ButTakenForOnePhoto()
    {
        var person = await CreatePerson("StandardUser");
        _actor.Current = person.Authority;
        var one = await AddPhoto(person, 'a');
        var two = await AddPhoto(person, 'b');

        var group = await SaveEdit(new ViewItemsEditRequest([one, two], SetDescription: true, Description: "Same caption"));
        Assert.False(group.Saved);
        Assert.All(group.Refusals, refusal => Assert.Equal(ViewItemsEditService.GroupIdentifyingReason, refusal.Reason));

        var single = await SaveEdit(new ViewItemsEditRequest([one], SetDescription: true, Description: "Sunset"));
        Assert.True(single.Saved);
        Assert.Equal("Sunset", _assets.Find(one)!.Description);
        Assert.Null(_assets.Find(two)!.Description);
    }

    [Fact]
    public async Task NewGalleryNamedInTheSaveIsMadeWithAllTheChosenPhotos()
    {
        var person = await CreatePerson("StandardUser");
        _actor.Current = person.Authority;
        var ids = new[] { await AddPhoto(person, 'a'), await AddPhoto(person, 'b'), await AddPhoto(person, 'c') };

        var result = await SaveEdit(new ViewItemsEditRequest(ids, CreateGalleryName: "Lisbon 2026"));

        Assert.True(result.Saved);
        var gallery = await new ViewGalleryRepository(_database).GetAsync(result.CreatedGalleryId!.Value);
        Assert.Equal("Lisbon 2026", gallery!.Name);
        Assert.Equal(3, gallery.ItemCount);
    }

    [Fact]
    public async Task AFailureAfterTheEditsPutsEveryPhotoBackAndRemovesTheGalleryThatWasMade()
    {
        var person = await CreatePerson("StandardUser");
        var one = await AddPhoto(person, 'a', new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero));
        var two = await AddPhoto(person, 'b', new DateTimeOffset(2026, 5, 2, 10, 0, 0, TimeSpan.Zero));
        var space = (await _spaces.GetByOwnerAsync(person.ProfileId))!;
        var real = new ViewGalleryRepository(_database);
        var service = new ViewItemsEditService(_assets, new ViewCatalogueRepository(_database), new FailsOnAdd(real), NullLogger<ViewItemsEditService>.Instance);
        var viewer = new ViewCatalogueViewer(person.ProfileId, [person.LibraryId]);
        var context = new ViewItemsEditService.Context(viewer, person.ProfileId, space.Id, [], _ => Task.FromResult<string?>(null), _ => Task.FromResult(true));

        var result = await service.RunAsync(context,
            new ViewItemsEditRequest([one, two], TagsAdd: ["Temp"], CapturedShiftSeconds: 3600, CreateGalleryName: "Doomed"), [], CancellationToken.None);

        Assert.False(result.Saved);
        Assert.Empty(_assets.Find(one)!.Tags);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero), _assets.Find(one)!.CapturedAt);
        Assert.Equal(new DateTimeOffset(2026, 5, 2, 10, 0, 0, TimeSpan.Zero), _assets.Find(two)!.CapturedAt);
        Assert.Empty(await real.GetOwnedAsync(person.ProfileId));
    }

    // A gallery store whose "add items" step fails after the gallery itself was made, so the Save has to be undone.
    private sealed class FailsOnAdd(IViewGalleryRepository inner) : IViewGalleryRepository
    {
        public Task<ViewGallery?> GetAsync(Guid galleryId, bool includeDocuments = true, CancellationToken ct = default, IReadOnlyCollection<string>? blockedKinds = null) => inner.GetAsync(galleryId, includeDocuments, ct, blockedKinds);
        public Task<IReadOnlyList<ViewGallery>> GetOwnedAsync(Guid ownerProfileId, bool includeDocuments = true, CancellationToken ct = default, IReadOnlyCollection<string>? blockedKinds = null) => inner.GetOwnedAsync(ownerProfileId, includeDocuments, ct, blockedKinds);
        public Task<IReadOnlyList<ViewGallery>> GetSharedWithAsync(Guid profileId, bool includeDocuments = true, CancellationToken ct = default, IReadOnlyCollection<string>? blockedKinds = null) => inner.GetSharedWithAsync(profileId, includeDocuments, ct, blockedKinds);
        public Task<ViewGallery> CreateAsync(CreateViewGalleryCommand command, CancellationToken ct = default) => inner.CreateAsync(command, ct);
        public Task<ViewGallery?> UpdateAsync(UpdateViewGalleryCommand command, CancellationToken ct = default) => inner.UpdateAsync(command, ct);
        public Task<bool> DeleteAsync(Guid galleryId, CancellationToken ct = default) => inner.DeleteAsync(galleryId, ct);
        public Task<ViewGalleryItemPage> GetItemsAsync(Guid galleryId, int? afterPosition = null, Guid? afterItemId = null, int limit = 100, bool includeDocuments = true, CancellationToken ct = default, IReadOnlyCollection<string>? blockedKinds = null) => inner.GetItemsAsync(galleryId, afterPosition, afterItemId, limit, includeDocuments, ct, blockedKinds);
        public Task<AddViewGalleryItemsResult> AddItemsAsync(Guid galleryId, IReadOnlyCollection<Guid> itemIds, IReadOnlyCollection<string>? blockedKinds = null, CancellationToken ct = default) => throw new InvalidOperationException("The gallery store went away.");
        public Task<int> RemoveItemsAsync(Guid galleryId, IReadOnlyCollection<Guid> itemIds, bool includeDocuments = true, CancellationToken ct = default) => inner.RemoveItemsAsync(galleryId, itemIds, includeDocuments, ct);
        public Task<bool> SetItemPositionAsync(Guid galleryId, Guid itemId, int position, bool includeDocuments = true, CancellationToken ct = default) => inner.SetItemPositionAsync(galleryId, itemId, position, includeDocuments, ct);
        public Task ReplaceSharesAsync(Guid galleryId, IReadOnlyCollection<(Guid ProfileId, ViewGallerySharePermission Permission)> shares, CancellationToken ct = default) => inner.ReplaceSharesAsync(galleryId, shares, ct);
        public Task<IReadOnlyList<ViewGalleryShare>> GetSharesAsync(Guid galleryId, CancellationToken ct = default) => inner.GetSharesAsync(galleryId, ct);
        public Task<bool> IsItemSharedWithProfileAsync(Guid itemId, Guid profileId, CancellationToken ct = default) => inner.IsItemSharedWithProfileAsync(itemId, profileId, ct);
    }
}
