using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class CollectionPersonalMediaSectionTests : AsyncBunitContext
{
    private readonly FakeCollectionPersonalMediaClient _client = new();

    public CollectionPersonalMediaSectionTests()
    {
        Services.AddNativeUiServices();
        Services.AddSingleton<ICollectionPersonalMediaClient>(_client);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task PickerOffersWholeGalleryReferencesAndNeverIndividualAssets()
    {
        Render<AppPopoverHost>();
        _client.Galleries.AddRange(
        [
            new(Guid.NewGuid(), "Family Trip", "manual", DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), "Recent Favorites", "smart", DateTimeOffset.UtcNow),
        ]);

        var cut = Render<CollectionPersonalMediaSection>();
        cut.WaitForState(() => cut.Markup.Contains("Family Trip", StringComparison.Ordinal));

        Assert.Contains("Gallery references", cut.Markup);
        Assert.Contains("Family Trip", cut.Markup);
        Assert.Contains("Recent Favorites", cut.Markup);
        Assert.DoesNotContain("Search library", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thumbnail", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("img"));

        cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Family Trip", StringComparison.Ordinal))
            .Click();

        Assert.True(await cut.Instance.SaveAsync(Guid.NewGuid()));
        var request = Assert.Single(_client.AddRequests);
        Assert.Equal(CollectionPersonalMediaSourceKinds.Gallery, request.Kind);
        Assert.Equal(_client.Galleries[0].GalleryId, request.GalleryId);
        Assert.Null(request.RuleDefinition);
        Assert.Null(request.AdditionalMembers);
    }

    [Fact]
    public async Task TakingOutTheLastSourceOfACuratedCollection_ShowsTheEnginesRefusalAndKeepsTheSource()
    {
        Render<AppPopoverHost>();
        var collectionId = Guid.NewGuid();
        var gallery = new CollectionGalleryReferenceDto(Guid.NewGuid(), "Family Trip", "manual", DateTimeOffset.UtcNow);
        _client.Galleries.Add(gallery);
        _client.Sources.Add(new CollectionPersonalMediaSourceDto(
            Guid.NewGuid(), collectionId, Guid.NewGuid(), CollectionPersonalMediaSourceKinds.Gallery, gallery.GalleryId, null, null, 0));
        _client.RefuseRemovalWith = "A collection needs at least one item. Delete the collection instead.";

        var cut = Render<CollectionPersonalMediaSection>(parameters => parameters.Add(c => c.CollectionId, collectionId));
        cut.WaitForState(() => cut.Markup.Contains("Family Trip", StringComparison.Ordinal));
        cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Family Trip", StringComparison.Ordinal))
            .Click();

        Assert.False(await cut.InvokeAsync(() => cut.Instance.SaveAsync(collectionId)));

        Assert.Equal("A collection needs at least one item. Delete the collection instead.", cut.Instance.LastError);
        cut.WaitForAssertion(() => Assert.Contains(
            "A collection needs at least one item. Delete the collection instead.",
            cut.Find(".collection-personal-media__notice").TextContent));
        Assert.Single(_client.Sources);
        Assert.Contains("is-selected", cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Family Trip", StringComparison.Ordinal)).ClassName);
    }

    [Fact]
    public async Task WhenOneOfSeveralRemovedSourcesIsRefused_TheSectionShowsWhatIsReallyAttachedAndDoesNotRemoveTheGoneOneAgain()
    {
        Render<AppPopoverHost>();
        var collectionId = Guid.NewGuid();
        var galleryA = new CollectionGalleryReferenceDto(Guid.NewGuid(), "Family Trip", "manual", DateTimeOffset.UtcNow);
        var galleryB = new CollectionGalleryReferenceDto(Guid.NewGuid(), "Recent Favorites", "manual", DateTimeOffset.UtcNow);
        _client.Galleries.AddRange([galleryA, galleryB]);
        var sourceA = new CollectionPersonalMediaSourceDto(
            Guid.NewGuid(), collectionId, Guid.NewGuid(), CollectionPersonalMediaSourceKinds.Gallery, galleryA.GalleryId, null, null, 0);
        var sourceB = new CollectionPersonalMediaSourceDto(
            Guid.NewGuid(), collectionId, Guid.NewGuid(), CollectionPersonalMediaSourceKinds.Gallery, galleryB.GalleryId, null, null, 1);
        _client.Sources.AddRange([sourceA, sourceB]);
        // The collection has no other items, so the Engine keeps the last source.
        _client.RefuseRemovalWith = "A collection needs at least one item. Delete the collection instead.";
        _client.RefuseRemovalOnlyFor = sourceB.SourceId;

        var cut = Render<CollectionPersonalMediaSection>(parameters => parameters.Add(c => c.CollectionId, collectionId));
        cut.WaitForState(() => cut.Markup.Contains("Family Trip", StringComparison.Ordinal));
        ToggleButton(cut, "Family Trip").Click();
        ToggleButton(cut, "Recent Favorites").Click();

        Assert.False(await cut.InvokeAsync(() => cut.Instance.SaveAsync(collectionId)));

        cut.WaitForAssertion(() => Assert.Contains(
            "A collection needs at least one item.", cut.Find(".collection-personal-media__notice").TextContent));
        Assert.DoesNotContain("is-selected", ToggleButton(cut, "Family Trip").ClassName);
        Assert.Contains("is-selected", ToggleButton(cut, "Recent Favorites").ClassName);
        Assert.Equal(1, cut.Instance.AttachedSourceCount);
        Assert.Equal([sourceA.SourceId, sourceB.SourceId], _client.RemoveCalls);

        _client.RemoveCalls.Clear();
        ToggleButton(cut, "Recent Favorites").Click();
        Assert.False(await cut.InvokeAsync(() => cut.Instance.SaveAsync(collectionId)));

        Assert.Equal([sourceB.SourceId], _client.RemoveCalls);
    }

    private static AngleSharp.Dom.IElement ToggleButton(IRenderedComponent<CollectionPersonalMediaSection> cut, string label) =>
        cut.FindAll("button").Single(button => button.TextContent.Contains(label, StringComparison.Ordinal));

    [Fact]
    public void SmartSourceUsesViewRuleBuilderBackedBySharedInteractiveCore()
    {
        Render<AppPopoverHost>();
        var cut = Render<CollectionPersonalMediaSection>();
        cut.WaitForState(() => cut.Markup.Contains("Add rule", StringComparison.Ordinal));

        cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Add rule", StringComparison.Ordinal))
            .Click();

        cut.WaitForElement("[data-rule-domain='view']");
        Assert.Equal("view", cut.Find("[data-rule-domain]").GetAttribute("data-rule-domain"));

        var sectionSource = File.ReadAllText(GetRepoFile(
            "src", "MediaEngine.Web", "Components", "Collections", "CollectionPersonalMediaSection.razor"));
        var viewWrapper = File.ReadAllText(GetRepoFile(
            "src", "MediaEngine.Web", "Components", "Rules", "ViewRuleBuilder.razor"));
        Assert.Contains("<ViewRuleBuilder", sectionSource, StringComparison.Ordinal);
        Assert.Contains("<SharedRuleBuilder", viewWrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("collection-rule-builder__group", sectionSource, StringComparison.Ordinal);
    }

    private sealed class FakeCollectionPersonalMediaClient : ICollectionPersonalMediaClient
    {
        public List<CollectionGalleryReferenceDto> Galleries { get; } = [];
        public List<CollectionPersonalMediaSourceDto> Sources { get; } = [];
        public List<CollectionPersonalMediaSourceWriteRequest> AddRequests { get; } = [];
        public string? LastError { get; private set; }
        public string? RefuseRemovalWith { get; set; }
        public Guid? RefuseRemovalOnlyFor { get; set; }
        public List<Guid> RemoveCalls { get; } = [];

        public Task<IReadOnlyList<CollectionGalleryReferenceDto>> GetEligibleGalleriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CollectionGalleryReferenceDto>>(Galleries);

        public Task<IReadOnlyList<CollectionPersonalMediaSourceDto>> GetSourcesAsync(Guid collectionId, CancellationToken ct = default)
        {
            // Like the real client, a successful read clears the last failure.
            LastError = null;
            return Task.FromResult<IReadOnlyList<CollectionPersonalMediaSourceDto>>(Sources.ToList());
        }

        public Task<CollectionPersonalMediaSourceDto?> AddSourceAsync(
            Guid collectionId,
            CollectionPersonalMediaSourceWriteRequest request,
            CancellationToken ct = default)
        {
            AddRequests.Add(request);
            var source = new CollectionPersonalMediaSourceDto(
                Guid.NewGuid(),
                collectionId,
                Guid.NewGuid(),
                request.Kind,
                request.GalleryId,
                request.RuleVersion,
                request.RuleDefinition,
                request.Position);
            Sources.Add(source);
            return Task.FromResult<CollectionPersonalMediaSourceDto?>(source);
        }

        public Task<CollectionPersonalMediaSourceDto?> UpdateSourceAsync(
            Guid collectionId,
            Guid sourceId,
            CollectionPersonalMediaSourceWriteRequest request,
            CancellationToken ct = default) =>
            Task.FromResult<CollectionPersonalMediaSourceDto?>(Sources.FirstOrDefault(source => source.SourceId == sourceId));

        public Task<bool> RemoveSourceAsync(Guid collectionId, Guid sourceId, CancellationToken ct = default)
        {
            RemoveCalls.Add(sourceId);
            if (RefuseRemovalWith is not null && (RefuseRemovalOnlyFor is null || RefuseRemovalOnlyFor == sourceId))
            {
                LastError = RefuseRemovalWith;
                return Task.FromResult(false);
            }

            Sources.RemoveAll(source => source.SourceId == sourceId);
            LastError = null;
            return Task.FromResult(true);
        }
    }

    private static string GetRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. segments]);
    }
}
