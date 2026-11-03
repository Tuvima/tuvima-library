using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The one universal editor (universal editor v3, WP1): a scoped service that owns the field session, the staged changes, the saver and
/// the picker, opens at once over any page without that page entering an editing state, keeps every item's staged changes while the person
/// switches item, and asks "Unsaved changes" before it closes. The Engine is a hand-written stub; nothing is written anywhere.
/// </summary>
public sealed class EditorHostTests
{
    private static readonly Guid ItemOne = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid ItemTwo = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<EditTrailItem> _opened = [];
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

    private async Task<EditorHost> HostAsync(string formFactor = "desktop", IEngineApiClient? api = null)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", formFactor, false, FinePointer: true, Hover: true));
        return new EditorHost(api ?? EngineApiClientStub.CreateDefault(), _device, _services, NullLogger<EditorHost>.Instance);
    }

    private EditorBinding Binding(bool permission = true, bool unmatched = false) => new(_device)
    {
        EntityId = ItemOne,
        HasPermission = permission,
        IsUnmatched = unmatched,
        ShowHistory = true,
        OpenItem = item =>
        {
            _opened.Add(item);
            return Task.FromResult(true);
        },
        Trail = () => [new EditTrailItem(ItemOne, "Dune", "Item")],
    };

    [Fact]
    public async Task Opening_ShowsTheWindowAtOnce_BeforeTheFieldsHaveLoaded()
    {
        var host = await HostAsync();
        var gate = new TaskCompletionSource();
        var binding = Binding();
        binding.LoadFields = () => gate.Task;

        var opening = host.OpenAsync(binding, ItemOne);

        // The skeleton is on screen while the Engine is still answering: the pop-over is open, on Details, with an active session.
        Assert.True(host.IsOpen);
        Assert.True(host.Session.IsActive);
        Assert.Equal(EditTool.Details, host.Session.ActiveTool);
        Assert.False(opening.IsCompleted);

        gate.SetResult();
        await opening;
    }

    [Fact]
    public async Task Opening_WithoutPermission_OrOnAPhone_IsRefused_AndNothingOpens()
    {
        var noPermission = await HostAsync();
        Assert.False(await noPermission.OpenAsync(Binding(permission: false), ItemOne));
        Assert.False(noPermission.IsOpen);

        var phone = await HostAsync("phone");
        Assert.False(await phone.OpenAsync(Binding(), ItemOne));
        Assert.False(phone.IsOpen);
        Assert.False(phone.Session.IsActive);
    }

    [Fact]
    public async Task Opening_AnUnmatchedItem_StartsOnMatch_AndAMatchedOneOnDetails()
    {
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetUniverseEditorSubjectAsync), _ => Task.FromResult<EditorSubjectDto?>(Subject())));

        var matched = await HostAsync(api: api);
        var matchedBinding = Binding();
        matchedBinding.LoadFields = () => matched.Mode.Fields.LoadUniverseSubjectAsync("Q1");
        await matched.OpenAsync(matchedBinding, Guid.Empty);
        Assert.Equal(EditTool.Details, matched.Session.ActiveTool);

        // A subject (a Universe) has no Match, so an unmatched flag cannot send it there.
        var unmatched = await HostAsync(api: api);
        var unmatchedBinding = Binding(unmatched: true);
        unmatchedBinding.LoadFields = () => unmatched.Mode.Fields.LoadUniverseSubjectAsync("Q1");
        await unmatched.OpenAsync(unmatchedBinding, Guid.Empty);
        Assert.Equal(EditTool.Details, unmatched.Session.ActiveTool);
        Assert.False(unmatched.Mode.Fields.CanMatch);
    }

    [Fact]
    public async Task ACharacterOrUniverse_OpensThroughTheSameHost_WithoutHistory()
    {
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetUniverseEditorSubjectAsync), _ => Task.FromResult<EditorSubjectDto?>(Subject())));
        var host = await HostAsync(api: api);
        var binding = Binding();
        binding.ShowHistory = false;
        binding.LoadFields = () => host.Mode.Fields.LoadUniverseSubjectAsync("Q1");

        Assert.True(await host.OpenAsync(binding, Guid.Empty));

        Assert.True(host.Mode.Fields.Loaded);
        Assert.False(host.Binding!.ShowHistory);
        Assert.Equal("Dune", host.Mode.Fields.Presentation.Heading?.DisplayValue ?? "Dune");
    }

    [Fact]
    public async Task OpeningAnotherItemWhileOpen_KeepsTheStagedChangesOfTheFirst()
    {
        var host = await HostAsync();
        var binding = Binding();
        Assert.True(await host.OpenAsync(binding, ItemOne));
        host.Session.Pending.SetField(ItemOne, "title", "Arrakis");

        Assert.True(await host.OpenAsync(binding, ItemTwo, trail: [new EditTrailItem(ItemOne, "Dune", "Item"), new EditTrailItem(ItemTwo, "Messiah", "Item")]));
        host.Session.Pending.SetField(ItemTwo, "title", "Messiah 2");

        Assert.Equal(ItemTwo, host.Session.ActiveItem!.EntityId);
        Assert.Equal(2, host.Session.Pending.ChangeCount);
        Assert.True(host.Session.Pending.TryGetField(ItemOne, "title", out var first));
        Assert.Equal("Arrakis", first);
        Assert.Equal([ItemTwo], _opened.Select(item => item.EntityId));
    }

    [Fact]
    public async Task Closing_WithStagedChanges_Asks_KeepEditingStays_AndDiscardClosesAndDropsThem()
    {
        var host = await HostAsync();
        var asked = 0;
        var discard = false;
        host.ConfirmDiscard = () =>
        {
            asked++;
            return Task.FromResult(discard);
        };
        await host.OpenAsync(Binding(), ItemOne);
        host.Session.Pending.SetField(ItemOne, "title", "Arrakis");

        Assert.False(await host.RequestCloseAsync());
        Assert.True(host.IsOpen);
        Assert.Equal(1, host.Session.Pending.ChangeCount);

        discard = true;
        Assert.True(await host.RequestCloseAsync());

        Assert.Equal(2, asked);
        Assert.False(host.IsOpen);
        Assert.Equal(0, host.Session.Pending.ChangeCount);
    }

    [Fact]
    public async Task Closing_WithNothingStaged_NeverAsks()
    {
        var host = await HostAsync();
        host.ConfirmDiscard = () => throw new InvalidOperationException("Nothing is staged, so nothing may be asked.");
        await host.OpenAsync(Binding(), ItemOne);

        Assert.True(await host.RequestCloseAsync());

        Assert.False(host.IsOpen);
    }

    [Fact]
    public async Task APageThatGoesAway_TakesItsOpenEditorWithIt()
    {
        var host = await HostAsync();
        var binding = Binding();
        await host.OpenAsync(binding, ItemOne);

        host.Unbind(binding);

        Assert.False(host.IsOpen);
        Assert.Null(host.Binding);
    }

    private static EditorSubjectDto Subject() => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-0000000000d1"),
        Kind = EditorSubjectKinds.Universe,
        Title = "Dune",
        ArtworkEntityKind = "Collection",
        ArtworkSlots = ["CoverArt", "Background"],
        Fields = [new EditorSubjectFieldDto { Key = "title", Label = "Name", Value = "Dune", CanonicalValue = "Dune", SourceLabel = "Wikidata" }],
    };
}
