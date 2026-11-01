using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Everyone makes their own, administrators push to everyone (Collections and lists D19), as the Dashboard offers it: who gets the New
/// collection entry points, which ownership choices the flow shows, and which collections an "Add to collection" list holds.
/// The Engine enforces the same rule again (see CollectionAuthoringByProfileTests in the Engine tests).
/// </summary>
public sealed class CollectionAuthoringWebTests : AsyncBunitContext
{
    private static readonly Guid Profile = TestSessions.Profile;

    public CollectionAuthoringWebTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton<ICollectionPersonalMediaClient>(new NoPersonalMedia());
    }

    // ── Who is offered what ──────────────────────────────────────────────────

    [Fact]
    public void AnAdultProfile_CanCreateButIsNotOfferedSharing()
    {
        var session = SessionFor(kids: false, administrator: false);

        Assert.True(CollectionAuthoringRule.CanCreate(session));
        Assert.False(CollectionAuthoringRule.CanShare(session));
    }

    [Fact]
    public void AnAdministrator_CanCreateAndShare()
    {
        var session = SessionFor(kids: false, administrator: true);

        Assert.True(CollectionAuthoringRule.CanCreate(session));
        Assert.True(CollectionAuthoringRule.CanShare(session));
    }

    [Fact]
    public void AKidsProfile_SeesNoNewCollectionEntry()
    {
        var session = SessionFor(kids: true, administrator: false);

        Assert.True(CollectionAuthoringRule.IsKids(session));
        Assert.False(CollectionAuthoringRule.CanCreate(session));
        Assert.False(CollectionAuthoringRule.CanShare(session));
    }

    [Fact]
    public void AnUnknownProfile_IsTreatedLikeAKidsProfile_SoNothingIsOfferedByMistake()
    {
        var signedOut = new DashboardSessionAccessor();

        Assert.False(CollectionAuthoringRule.CanCreate(signedOut));
    }

    // ── The "Add to collection" list ─────────────────────────────────────────

    [Theory]
    [InlineData("Custom", "materialized", true, true, false, true)]
    [InlineData("Playlist", "materialized", true, true, true, true)]
    [InlineData("Custom", "materialized", false, true, false, false)]
    [InlineData("Custom", "query", true, true, false, false)]
    [InlineData("Custom", "materialized", true, false, false, false)]
    [InlineData("Playlist", "materialized", true, true, false, false)]
    [InlineData("Custom", "materialized", true, true, true, false)]
    public void TheAddList_HoldsOnlyHandPickedCollectionsAndPlaylistsThePersonMayEdit(
        string type, string resolution, bool canEdit, bool enabled, bool forPlaylistMenu, bool expected)
    {
        var collection = new ManagedCollectionDto
        {
            Id = Guid.NewGuid(),
            Name = "Mine",
            CollectionType = type,
            Resolution = resolution,
            CanEdit = canEdit,
            IsEnabled = enabled,
        };

        Assert.Equal(expected, CollectionAuthoringRule.IsAddTarget(collection, forPlaylistMenu));
    }

    // ── The ownership step ───────────────────────────────────────────────────

    [Fact]
    public async Task ANonAdministrator_IsNotOfferedLibraryOwnershipOrSelectedProfiles()
    {
        Services.AddSingleton(SessionFor(kids: false, administrator: false));
        var editor = RenderFlowWithOneTitle();

        await GoToNameAndShowItAsync(editor);

        editor.WaitForAssertion(() => Assert.Contains("Only you can see this collection.", editor.Markup));
        Assert.DoesNotContain("Selected profiles", editor.Markup);
        Assert.DoesNotContain("Library-owned collections are available to everyone.", editor.Markup);
        Assert.DoesNotContain(">Library<", editor.Markup);
    }

    [Fact]
    public async Task AnAdministrator_IsOfferedLibraryOwnershipAndSelectedProfiles()
    {
        Services.AddSingleton(SessionFor(kids: false, administrator: true));
        var editor = RenderFlowWithOneTitle();

        await GoToNameAndShowItAsync(editor);

        editor.WaitForAssertion(() => Assert.Contains("Ownership and visibility", editor.Markup));
        Assert.DoesNotContain("Only you can see this collection.", editor.Markup);
        Assert.Contains("Library", editor.Markup);
    }

    [Fact]
    public void NewCollectionWithThisTitle_OpensOnPickTitlesWithThatTitleAlreadyChosen()
    {
        Services.AddSingleton(SessionFor(kids: false, administrator: false));

        var editor = RenderFlowWithOneTitle();

        Assert.Contains("Dune", editor.Markup);
        Assert.Contains("Pick titles", editor.Markup);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private IRenderedComponent<CollectionEditorShell> RenderFlowWithOneTitle()
    {
        Services.AddSingleton(EngineApiClientStub.Create(_ => { }));
        var request = new CollectionEditorLaunchRequest
        {
            ActiveProfileId = Profile,
            InitialMembershipMode = "Manual",
            InitialTitle = "Dune",
            InitialItems = [new CollectionEditorSeedItem(Guid.NewGuid(), "Dune", "Movies")],
        };
        return Render<CollectionEditorShell>(parameters => parameters
            .Add(shell => shell.Inline, true)
            .Add(shell => shell.Request, request));
    }

    private static async Task GoToNameAndShowItAsync(IRenderedComponent<CollectionEditorShell> editor)
    {
        // The flow opens on Pick titles with the title already chosen; one Next reaches the Name and show-it step.
        await editor.InvokeAsync(() => editor.FindAll(".collection-editor-footer-actions button").Last().Click());
    }

    private static DashboardSessionAccessor SessionFor(bool kids, bool administrator) => TestSessions.For(kids, administrator);

    private sealed class NoPersonalMedia : ICollectionPersonalMediaClient
    {
        public string? LastError => null;
        public Task<IReadOnlyList<CollectionGalleryReferenceDto>> GetEligibleGalleriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionGalleryReferenceDto>>([]);
        public Task<IReadOnlyList<CollectionPersonalMediaSourceDto>> GetSourcesAsync(Guid collectionId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionPersonalMediaSourceDto>>([]);
        public Task<CollectionPersonalMediaSourceDto?> AddSourceAsync(Guid collectionId, CollectionPersonalMediaSourceWriteRequest request, CancellationToken ct = default) => Task.FromResult<CollectionPersonalMediaSourceDto?>(null);
        public Task<CollectionPersonalMediaSourceDto?> UpdateSourceAsync(Guid collectionId, Guid sourceId, CollectionPersonalMediaSourceWriteRequest request, CancellationToken ct = default) => Task.FromResult<CollectionPersonalMediaSourceDto?>(null);
        public Task<bool> RemoveSourceAsync(Guid collectionId, Guid sourceId, CancellationToken ct = default) => Task.FromResult(false);
    }
}
