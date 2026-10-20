using Bunit;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Match step panel (details overlay spec D12 to D17): it draws nothing for a title the Engine will not move, lists only the
/// places the Engine offers, shows the Engine's pre-selected guess, keeps Move off while a taken entry is open, and sends the
/// preview's fingerprint when confirmed. The Engine is a hand-written stub.
/// </summary>
public sealed class ItemMovePanelTests : AsyncBunitContext
{
    private static readonly Guid WorkId = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000001");
    private static readonly Guid AlbumId = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000002");
    private static readonly Guid Track2 = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000003");
    private static readonly Guid Track1 = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000004");

    private bool _movable = true;
    private readonly List<ItemMoveRequest> _moves = [];
    private readonly List<(Guid? Entry, string? Mode)> _previews = [];

    public ItemMovePanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.FindItemMoveDestinationsAsync), _ => Task.FromResult(_movable
                ? ItemMoveOutcome<ItemMoveDestinationsResponse>.Ok(new() { Destinations = [new() { Id = AlbumId, Title = "OK Computer", EntryCount = 2 }] })
                : ItemMoveOutcome<ItemMoveDestinationsResponse>.Failed(ItemMoveOutcomeStatus.Refused, "Only a song can be moved.")));
            stub.SetHandler(nameof(IEngineApiClient.GetItemMovePreviewAsync), args =>
            {
                var entry = (Guid?)args![2];
                var mode = (string?)args[3];
                _previews.Add((entry, mode));
                return Task.FromResult(ItemMoveOutcome<ItemMovePreview>.Ok(Preview(entry ?? Track2, mode)));
            });
            stub.SetHandler(nameof(IEngineApiClient.MoveItemAsync), args =>
            {
                var request = (ItemMoveRequest)args![0]!;
                _moves.Add(request);
                return Task.FromResult(ItemMoveOutcome<ItemMoveResultDto>.Ok(new() { WorkId = WorkId, LandedWorkId = Track2, DestinationTitle = "OK Computer", Mode = request.Mode }));
            });
        }));
    }

    private static ItemMovePreview Preview(Guid selected, string? mode)
    {
        var taken = selected == Track1;
        var addAnyway = mode == ItemMoveModes.AddAnyway;
        var chosenMode = addAnyway ? ItemMoveModes.AddAnyway : taken ? ItemMoveModes.AnotherVersion : ItemMoveModes.Match;
        return new ItemMovePreview
        {
            WorkId = WorkId,
            Title = "Paranoid Android",
            FromTitle = "The Best Of",
            DestinationId = AlbumId,
            DestinationTitle = "OK Computer",
            Entries =
            [
                new() { Id = Track1, Title = "Karma Police", Number = 1, Taken = true },
                new() { Id = Track2, Title = "Paranoid Android", Number = 2, Taken = false },
            ],
            SelectedEntryId = addAnyway ? null : selected,
            GuessReason = "Same title",
            Mode = chosenMode,
            SelectedEntryTaken = !addAnyway && taken,
            Message = addAnyway ? "This won't get a number or details from OK Computer." : "Move Paranoid Android to OK Computer as 2. Paranoid Android?",
            BlockedReason = !addAnyway && taken && mode != ItemMoveModes.AnotherVersion ? "1. Karma Police already has a file." : null,
            Fingerprint = $"fp-{selected:N}-{chosenMode}",
        };
    }

    private IRenderedComponent<ItemMovePanel> RenderPanel() =>
        Render<ItemMovePanel>(p => p.Add(x => x.WorkId, WorkId).Add(x => x.MediaType, "Music"));

    [Fact]
    public void ATitleTheEngineWillNotMove_DrawsNothing()
    {
        _movable = false;

        var panel = RenderPanel();

        panel.WaitForAssertion(() => Assert.Empty(panel.FindAll(".item-move")));
    }

    [Fact]
    public void ChoosingWhere_ListsTheOfferedPlaces_ThenShowsTheEnginesGuessAsSelected()
    {
        var panel = RenderPanel();
        panel.WaitForElement(".item-move__intro").QuerySelector("button")!.Click();
        panel.WaitForElement(".item-move__row").Click();

        panel.WaitForElement(".item-move__entry.is-selected");
        Assert.Contains("Best guess: Same title", panel.Markup, StringComparison.Ordinal);
        var selected = panel.Find(".item-move__entry.is-selected");
        Assert.Contains("Paranoid Android", selected.TextContent, StringComparison.Ordinal);
        Assert.Contains("Already has a file", panel.Find(".item-move__entry.is-taken").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void PickingATakenEntry_KeepsMoveHereOff_AndOffersAnotherVersionInstead()
    {
        var panel = RenderPanel();
        panel.WaitForElement(".item-move__intro").QuerySelector("button")!.Click();
        panel.WaitForElement(".item-move__row").Click();
        panel.WaitForElement(".item-move__entry.is-selected");

        panel.Find(".item-move__entry.is-taken").Click();

        panel.WaitForAssertion(() =>
        {
            Assert.Contains("already has a file", panel.Find(".item-move__note").TextContent, StringComparison.Ordinal);
            var labels = panel.FindAll(".item-move__actions button").Select(b => b.TextContent.Trim()).ToList();
            Assert.Contains("Add as another version", labels);
            Assert.DoesNotContain("Move here", labels);
        });
        Assert.Empty(_moves);
    }
}
