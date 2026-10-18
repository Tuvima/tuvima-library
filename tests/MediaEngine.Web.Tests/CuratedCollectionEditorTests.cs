using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A manually curated collection is never empty: the editor will not save one with no items, it sends the items with
/// the create call so an empty collection never exists on the Engine, and it shows the Engine's reason when the Engine
/// refuses to take out the last item.
/// </summary>
public sealed class CuratedCollectionEditorTests : AsyncBunitContext
{
    private const string EmptyMessage = "A collection needs at least one item.";
    private static readonly Guid FirstWork = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid SecondWork = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private readonly List<IReadOnlyList<Guid>?> _createdWorkIds = [];
    private readonly List<string> _createdTypes = [];
    private readonly List<IReadOnlyList<Guid>?> _updatedWorkIds = [];
    private IReadOnlyList<CollectionPersonalMediaSourceDto> _attachedSources = [];
    private EngineApiClientStub _stub = default!;

    public CuratedCollectionEditorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton<ICollectionPersonalMediaClient>(new EmptyPersonalMediaClient(() => _attachedSources));
    }

    [Fact]
    public void SavingANewCuratedCollectionWithNoItems_IsDisabledAndSaysWhy()
    {
        var editor = RenderCreateEditor();

        var save = SaveButton(editor);

        Assert.True(save.HasAttribute("disabled"));
        Assert.Contains(EmptyMessage, editor.Find(".collection-editor-footer-copy").TextContent);
    }

    [Fact]
    public async Task SavingANewCuratedCollectionWithItems_IsEnabled()
    {
        var editor = RenderCreateEditor();

        await AddItemAsync(editor, FirstWork);

        editor.WaitForAssertion(() => Assert.False(SaveButton(editor).HasAttribute("disabled")));
        Assert.DoesNotContain(EmptyMessage, editor.Find(".collection-editor-footer-copy").TextContent);
    }

    [Fact]
    public async Task CreatingACuratedCollection_SendsTheItemsWithTheCreateCall()
    {
        var closed = new List<bool>();
        var editor = RenderCreateEditor(closed.Add);
        await AddItemAsync(editor, FirstWork);
        await AddItemAsync(editor, SecondWork);

        await editor.InvokeAsync(() => SaveButton(editor).Click());

        editor.WaitForAssertion(() => Assert.Single(_createdWorkIds));
        Assert.Equal([FirstWork, SecondWork], _createdWorkIds[0]);
        Assert.Equal("Custom", Assert.Single(_createdTypes));
        // Nothing is attached after the collection exists, so there is never a moment when it is empty.
        Assert.DoesNotContain(nameof(IEngineApiClient.AddCollectionItemAsync), _stub.Calls);
        Assert.Equal([true], closed);
    }

    [Fact]
    public async Task APlaylistWithNoItems_CanStillBeSaved()
    {
        var editor = RenderEditor(new CollectionEditorLaunchRequest
        {
            Kind = ContainerEditorKind.Playlist,
            InitialTitle = "Road trip",
            InitialOwnerKind = "Library",
            InitialAudience = "Everyone",
        });

        Assert.False(SaveButton(editor).HasAttribute("disabled"));
        await editor.InvokeAsync(() => SaveButton(editor).Click());

        editor.WaitForAssertion(() => Assert.Single(_createdWorkIds));
        Assert.Equal("Playlist", Assert.Single(_createdTypes));
    }

    [Fact]
    public async Task WhenTheEngineRefusesToTakeOutTheLastItem_TheEditorShowsTheEnginesMessage()
    {
        const string engineMessage = "A collection needs at least one item. Delete the collection instead.";
        var collectionId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var client = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetCollectionItemsAsync), _ => Task.FromResult(new List<CollectionItemDto>
            {
                new() { Id = itemId, WorkId = FirstWork, Title = "Arrival", MediaType = "Movies", SortOrder = 1 },
            }));
            stub.SetHandler(nameof(IEngineApiClient.RemoveCollectionItemAsync), _ => Task.FromResult(false));
            stub.SetHandler("get_" + nameof(IEngineApiClient.LastError), _ => engineMessage);
        });
        _stub = (EngineApiClientStub)(object)client;
        Services.AddSingleton(client);
        var editor = Render<CollectionEditorShell>(parameters => parameters
            .Add(shell => shell.Inline, true)
            .Add(shell => shell.Request, new CollectionEditorLaunchRequest
            {
                EditingCollection = new CollectionListItemViewModel
                {
                    Id = collectionId,
                    Name = "Just one",
                    CollectionType = "Custom",
                    Resolution = "materialized",
                    MembershipMode = "Manual",
                    OwnerKind = "Library",
                    Audience = "Everyone",
                    ItemCount = 1,
                },
            }));
        editor.WaitForAssertion(() => Assert.Single(editor.Instance.GetType()
            .GetField("_manualItems", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor.Instance) as System.Collections.IList ?? new List<object>()));

        var remove = editor.Instance.GetType().GetMethod("RemoveManualItemAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var items = (System.Collections.IList)editor.Instance.GetType()
            .GetField("_manualItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor.Instance)!;
        await editor.InvokeAsync(() => (Task)remove.Invoke(editor.Instance, [items[0]])!);

        Assert.Contains(Services.GetRequiredService<MediaEngine.Web.Services.Ui.AppToastService>().Toasts,
            toast => toast.Message == engineMessage);
        // The item is still listed: the Engine kept it.
        Assert.Single(items);
    }

    [Fact]
    public void AnExistingCuratedCollectionWithOnlyAnAttachedGallery_CanStillBeSaved()
    {
        _attachedSources = [new CollectionPersonalMediaSourceDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CollectionPersonalMediaSourceKinds.Gallery, Guid.NewGuid(), null, null, 0)];
        var editor = RenderExisting(Guid.NewGuid(), membershipMode: "Manual", resolution: "materialized");

        editor.WaitForAssertion(() => Assert.False(SaveButton(editor).HasAttribute("disabled")));
        Assert.DoesNotContain(EmptyMessage, editor.Find(".collection-editor-footer-copy").TextContent);
    }

    [Fact]
    public void ACuratedCollectionWithNoItemsAndNoAttachedSource_CannotBeSaved()
    {
        var editor = RenderExisting(Guid.NewGuid(), membershipMode: "Manual", resolution: "materialized");

        editor.WaitForAssertion(() => Assert.True(SaveButton(editor).HasAttribute("disabled")));
    }

    [Fact]
    public async Task SwitchingARuleDrivenCollectionToManual_StagesItemsHereAndSendsThemWithTheSave()
    {
        var collectionId = Guid.NewGuid();
        var editor = RenderExisting(collectionId, membershipMode: "Smart", resolution: "query");
        var setMode = editor.Instance.GetType().GetMethod("SetMembershipModeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await editor.InvokeAsync(() => (Task)setMode.Invoke(editor.Instance, [false])!);

        // Switching to manual with nothing chosen leaves Save off, so the collection is never manual and empty.
        Assert.True(SaveButton(editor).HasAttribute("disabled"));

        await AddItemAsync(editor, FirstWork);
        await AddItemAsync(editor, SecondWork);
        editor.WaitForAssertion(() => Assert.False(SaveButton(editor).HasAttribute("disabled")));
        await editor.InvokeAsync(() => SaveButton(editor).Click());

        editor.WaitForAssertion(() => Assert.Single(_updatedWorkIds));
        Assert.Equal([FirstWork, SecondWork], _updatedWorkIds[0]);
        // The items travel with the save; none is added to the still rule-driven collection one by one.
        Assert.DoesNotContain(nameof(IEngineApiClient.AddCollectionItemAsync), _stub.Calls);
    }

    private IRenderedComponent<CollectionEditorShell> RenderExisting(Guid collectionId, string membershipMode, string resolution)
    {
        var client = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetCollectionItemsAsync), _ => Task.FromResult(new List<CollectionItemDto>()));
            stub.SetHandler(nameof(IEngineApiClient.UpdateCollectionAsync), args =>
            {
                _updatedWorkIds.Add((IReadOnlyList<Guid>?)args![^1]);
                return Task.FromResult(true);
            });
        });
        _stub = (EngineApiClientStub)(object)client;
        Services.AddSingleton(client);
        return Render<CollectionEditorShell>(parameters => parameters
            .Add(shell => shell.Inline, true)
            .Add(shell => shell.Request, new CollectionEditorLaunchRequest
            {
                EditingCollection = new CollectionListItemViewModel
                {
                    Id = collectionId,
                    Name = "Existing",
                    CollectionType = "Custom",
                    Resolution = resolution,
                    MembershipMode = membershipMode,
                    OwnerKind = "Library",
                    Audience = "Everyone",
                },
            }));
    }

    private IRenderedComponent<CollectionEditorShell> RenderCreateEditor(Action<bool>? closed = null) =>
        RenderEditor(new CollectionEditorLaunchRequest
        {
            InitialTitle = "Weekend picks",
            InitialOwnerKind = "Library",
            InitialAudience = "Everyone",
        }, closed);

    private IRenderedComponent<CollectionEditorShell> RenderEditor(CollectionEditorLaunchRequest request, Action<bool>? closed = null)
    {
        var client = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.CreateCollectionAndReturnIdAsync), args =>
            {
                _createdTypes.Add((string)args![3]!);
                _createdWorkIds.Add((IReadOnlyList<Guid>?)args[^1]);
                return Task.FromResult<Guid?>(Guid.NewGuid());
            }));
        _stub = (EngineApiClientStub)(object)client;
        Services.AddSingleton(client);
        return Render<CollectionEditorShell>(parameters =>
        {
            parameters
                .Add(shell => shell.Inline, true)
                .Add(shell => shell.Request, request);
            if (closed is not null)
            {
                parameters.Add(shell => shell.Closed, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<bool>(this, closed));
            }
        });
    }

    private static Task AddItemAsync(IRenderedComponent<CollectionEditorShell> editor, Guid workId)
    {
        var add = editor.Instance.GetType().GetMethod("AddManualItemAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var work = new CollectionMediaLookupDto { WorkId = workId, Title = $"Work {workId:N}", MediaType = "Movies" };
        return editor.InvokeAsync(() => (Task)add.Invoke(editor.Instance, [work])!);
    }

    private static AngleSharp.Dom.IElement SaveButton(IRenderedComponent<CollectionEditorShell> editor) =>
        editor.FindAll(".collection-editor-footer-actions button").Last();

    private sealed class EmptyPersonalMediaClient(Func<IReadOnlyList<CollectionPersonalMediaSourceDto>> sources) : ICollectionPersonalMediaClient
    {
        public string? LastError => null;
        public Task<IReadOnlyList<CollectionGalleryReferenceDto>> GetEligibleGalleriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionGalleryReferenceDto>>([]);
        public Task<IReadOnlyList<CollectionPersonalMediaSourceDto>> GetSourcesAsync(Guid collectionId, CancellationToken ct = default) => Task.FromResult(sources());
        public Task<CollectionPersonalMediaSourceDto?> AddSourceAsync(Guid collectionId, CollectionPersonalMediaSourceWriteRequest request, CancellationToken ct = default) => Task.FromResult<CollectionPersonalMediaSourceDto?>(null);
        public Task<CollectionPersonalMediaSourceDto?> UpdateSourceAsync(Guid collectionId, Guid sourceId, CollectionPersonalMediaSourceWriteRequest request, CancellationToken ct = default) => Task.FromResult<CollectionPersonalMediaSourceDto?>(null);
        public Task<bool> RemoveSourceAsync(Guid collectionId, Guid sourceId, CancellationToken ct = default) => Task.FromResult(false);
    }
}
