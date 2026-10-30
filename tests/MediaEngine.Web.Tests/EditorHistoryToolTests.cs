using Bunit;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The History tool of the universal editor window (v2 D3, D5): what happened to the item, newest first, with a filter by kind of
/// activity, and no old editor shell left anywhere in the Dashboard.
/// </summary>
public sealed class EditorHistoryToolTests : AsyncBunitContext
{
    private static readonly Guid ItemId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly List<Guid> _asked = [];
    private string? _error;

    public EditorHistoryToolTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetItemHistoryWithStatusAsync), args =>
            {
                _asked.Add((Guid)args![0]!);
                return Task.FromResult((new List<LibraryItemHistoryDto>
                {
                    new() { Id = "1", EntityId = ItemId, OccurredAt = DateTimeOffset.Now.AddHours(-3), EventType = "ArtworkUpdated", Label = "Poster changed", Category = "artwork", ActorLabel = "Shy" },
                    new() { Id = "2", EntityId = ItemId, OccurredAt = DateTimeOffset.Now.AddHours(-1), EventType = "MetadataEdited", Label = "Title edited", Detail = "Dune to Dune: Part One", Category = "metadata", ActorLabel = "Shy" },
                }, _error));
            })));
    }

    [Fact]
    public void TheHistory_ListsNewestFirstWithWhoDidIt()
    {
        var cut = Render<EditorHistoryTool>(p => p.Add(x => x.EntityId, ItemId));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".tl-history__row").Count));
        var titles = cut.FindAll(".tl-history__title").Select(t => t.TextContent.Trim()).ToList();
        Assert.Equal(["Title edited", "Poster changed"], titles);
        Assert.Contains("by Shy", cut.Markup);
        Assert.Contains("Dune to Dune: Part One", cut.Markup);
        Assert.Equal([ItemId], _asked);
    }

    [Fact]
    public void ChangingTheItem_ReadsTheHistoryAgainForTheNewOne()
    {
        var cut = Render<EditorHistoryTool>(p => p.Add(x => x.EntityId, ItemId));
        cut.WaitForAssertion(() => Assert.Single(_asked));
        var other = Guid.NewGuid();

        cut.Render(p => p.Add(x => x.EntityId, other));

        cut.WaitForAssertion(() => Assert.Equal([ItemId, other], _asked));
    }

    [Fact]
    public void AnItemThatCouldNotBeRead_SaysSoInsteadOfShowingAnEmptyList()
    {
        _error = "boom";

        var cut = Render<EditorHistoryTool>(p => p.Add(x => x.EntityId, ItemId));

        cut.WaitForAssertion(() => Assert.Contains("could not be read", cut.Find("[role='alert']").TextContent));
    }

    [Fact]
    public void TheOldAllInOneEditorShell_IsGone()
    {
        var root = RepoRoot();
        var editor = Path.Combine(root, "src/MediaEngine.Web/Components/MediaEditor");

        Assert.Empty(Directory.GetFiles(editor, "SharedMediaEditorShell*"));
        Assert.False(Directory.Exists(Path.Combine(editor, "Sections")));
        Assert.False(File.Exists(Path.Combine(root, "src/MediaEngine.Web/Components/Details/EditingSectionSlot.razor")));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }
}
