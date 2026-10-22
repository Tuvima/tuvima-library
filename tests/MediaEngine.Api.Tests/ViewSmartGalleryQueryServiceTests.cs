using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Tests;

public sealed class ViewSmartGalleryQueryServiceTests
{
    [Fact]
    public async Task ResolvesStoredVersionOneRuleAndLeavesManualGalleryStatic()
    {
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var smart = Gallery(ViewGalleryKind.Smart,
            """{"version":1,"groups":[{"id":"fav","join_with_previous":"or","match_mode":"all","conditions":[{"field":"favorite","op":"eq","value":"true"}]}]}""");
        var manual = Gallery(ViewGalleryKind.Manual, null);
        var repository = new GalleryStore(smart, manual);
        var service = new ViewSmartGalleryQueryService(repository);

        var rule = await service.ResolveRuleAsync(smart.Id);

        Assert.Equal("favorite", Assert.Single(Assert.Single(rule!.Groups).Conditions).Field);
        Assert.Null(await service.ResolveRuleAsync(manual.Id));

        ViewGallery Gallery(ViewGalleryKind kind, string? json) => new(
            Guid.NewGuid(), owner, space, kind.ToString(), null, kind, json, null, 0, 0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task RejectsMalformedStoredRuleWithAValidationError()
    {
        var gallery = new ViewGallery(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Broken", null,
            ViewGalleryKind.Smart, "{not-json", null, 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            new ViewSmartGalleryQueryService(new GalleryStore(gallery)).ResolveRuleAsync(gallery.Id));

        Assert.Contains("malformed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class GalleryStore(params ViewGallery[] galleries) : IViewGalleryRepository
    {
        public Task<ViewGallery?> GetAsync(Guid galleryId, bool includeDocuments = true, CancellationToken ct = default, IReadOnlyCollection<string>? blockedKinds = null) =>
            Task.FromResult(galleries.SingleOrDefault(gallery => gallery.Id == galleryId));

        public Task<IReadOnlyList<ViewGallery>> GetOwnedAsync(Guid ownerProfileId, bool includeDocuments = true, CancellationToken ct = default, IReadOnlyCollection<string>? blockedKinds = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<ViewGallery>> GetSharedWithAsync(Guid profileId, bool includeDocuments = true, CancellationToken ct = default, IReadOnlyCollection<string>? blockedKinds = null) => throw new NotSupportedException();
        public Task<ViewGallery> CreateAsync(CreateViewGalleryCommand command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ViewGallery?> UpdateAsync(UpdateViewGalleryCommand command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid galleryId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ViewGalleryItemPage> GetItemsAsync(Guid galleryId, int? afterPosition = null, Guid? afterItemId = null, int limit = 100, bool includeDocuments = true, CancellationToken ct = default, IReadOnlyCollection<string>? blockedKinds = null) => throw new NotSupportedException();
        public Task<AddViewGalleryItemsResult> AddItemsAsync(Guid galleryId, IReadOnlyCollection<Guid> itemIds, IReadOnlyCollection<string>? blockedKinds = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> RemoveItemsAsync(Guid galleryId, IReadOnlyCollection<Guid> itemIds, bool includeDocuments = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SetItemPositionAsync(Guid galleryId, Guid itemId, int position, bool includeDocuments = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceSharesAsync(Guid galleryId, IReadOnlyCollection<(Guid ProfileId, ViewGallerySharePermission Permission)> shares, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ViewGalleryShare>> GetSharesAsync(Guid galleryId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsItemSharedWithProfileAsync(Guid itemId, Guid profileId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
