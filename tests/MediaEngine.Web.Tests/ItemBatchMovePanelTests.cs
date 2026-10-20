using Bunit;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Moving several ticked rows at once: the mapping table is filled from the Engine's guesses without giving two rows the same entry,
/// Apply stays off while a row is unmatched until "Add unmatched without numbers" is chosen, and each row is moved through its own
/// fresh preview. The Engine is a hand-written stub.
/// </summary>
public sealed class ItemBatchMovePanelTests : AsyncBunitContext
{
    private static readonly Guid Song1 = Guid.Parse("44444444-aaaa-bbbb-cccc-000000000001");
    private static readonly Guid Song2 = Guid.Parse("44444444-aaaa-bbbb-cccc-000000000002");
    private static readonly Guid AlbumId = Guid.Parse("44444444-aaaa-bbbb-cccc-000000000010");
    private static readonly Guid Track1 = Guid.Parse("44444444-aaaa-bbbb-cccc-000000000011");
    private static readonly Guid Track2 = Guid.Parse("44444444-aaaa-bbbb-cccc-000000000012");

    private readonly List<ItemMoveRequest> _moves = [];
    private readonly List<(Guid WorkId, Guid? Entry, string? Mode)> _previews = [];

    public ItemBatchMovePanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IAppDialogService>(new ConfirmingDialogService());
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.FindItemMoveDestinationsAsync), _ => Task.FromResult(
                ItemMoveOutcome<ItemMoveDestinationsResponse>.Ok(new() { Destinations = [new() { Id = AlbumId, Title = "OK Computer", EntryCount = 2 }] })));
            stub.SetHandler(nameof(IEngineApiClient.GetItemMovePreviewAsync), args =>
            {
                var workId = (Guid)args![0]!;
                var entry = (Guid?)args[2];
                var mode = (string?)args[3];
                _previews.Add((workId, entry, mode));
                // Both songs guess the same entry (Track 2), so only the first row can keep it.
                return Task.FromResult(ItemMoveOutcome<ItemMovePreview>.Ok(Preview(workId, entry ?? Track2, mode)));
            });
            stub.SetHandler(nameof(IEngineApiClient.MoveItemAsync), args =>
            {
                var request = (ItemMoveRequest)args![0]!;
                _moves.Add(request);
                return Task.FromResult(ItemMoveOutcome<ItemMoveResultDto>.Ok(new() { WorkId = request.WorkId, LandedWorkId = request.WorkId, DestinationTitle = "OK Computer", Mode = request.Mode }));
            });
        }));
    }

    private static ItemMovePreview Preview(Guid workId, Guid selected, string? mode)
    {
        var addAnyway = mode == ItemMoveModes.AddAnyway;
        return new ItemMovePreview
        {
            WorkId = workId,
            Title = workId == Song1 ? "Paranoid Android" : "Lucky",
            DestinationId = AlbumId,
            DestinationTitle = "OK Computer",
            Entries =
            [
                new() { Id = Track1, Title = "Airbag", Number = 1 },
                new() { Id = Track2, Title = "Paranoid Android", Number = 2 },
            ],
            SelectedEntryId = addAnyway ? null : selected,
            Mode = addAnyway ? ItemMoveModes.AddAnyway : ItemMoveModes.Match,
            Fingerprint = $"fp-{workId:N}-{selected:N}-{mode}",
        };
    }

    private IRenderedComponent<ItemBatchMovePanel> RenderPanel(int moved = 0, Action? onMoved = null) =>
        Render<ItemBatchMovePanel>(p => p
            .Add(x => x.WorkIds, new[] { Song1, Song2 })
            .Add(x => x.MediaType, "Music")
            .Add(x => x.Plural, "tracks")
            .Add(x => x.OnMoved, EventCallback.Factory.Create(this, () => onMoved?.Invoke())));

    private static void ChooseAlbum(IRenderedComponent<ItemBatchMovePanel> panel)
    {
        panel.Find(".item-batch-move button").Click();
        panel.WaitForElement(".item-batch-move__pick").Click();
        panel.WaitForElement(".item-batch-move__table");
    }

    [Fact]
    public void TwoSongsGuessingTheSameEntry_GiveItToTheFirstAndHighlightTheOther()
    {
        var panel = RenderPanel();

        ChooseAlbum(panel);

        Assert.Single(panel.FindAll("tr.is-unmatched"));
        Assert.Contains("1 row has no entry yet", panel.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WhileARowIsUnmatched_ApplyStaysOff_UntilAddUnmatchedIsChosen()
    {
        var panel = RenderPanel();
        ChooseAlbum(panel);

        Assert.True(panel.Find(".item-batch-move__actions button:last-of-type").HasAttribute("disabled"));

        panel.Find(".item-batch-move input[type=checkbox]").Change(true);

        panel.WaitForAssertion(() => Assert.False(panel.Find(".item-batch-move__actions button:last-of-type").HasAttribute("disabled")));
    }

    [Fact]
    public void Applying_MovesEachRowThroughItsOwnFreshPreview_AndAddsTheUnmatchedOneAnyway()
    {
        var moved = 0;
        var panel = RenderPanel(onMoved: () => moved++);
        ChooseAlbum(panel);
        panel.Find(".item-batch-move input[type=checkbox]").Change(true);
        panel.WaitForAssertion(() => Assert.False(panel.Find(".item-batch-move__actions button:last-of-type").HasAttribute("disabled")));

        panel.Find(".item-batch-move__actions button:last-of-type").Click();

        panel.WaitForAssertion(() => Assert.Equal(2, _moves.Count));
        Assert.Equal(ItemMoveModes.Match, _moves.Single(m => m.WorkId == Song1).Mode);
        Assert.Equal(Track2, _moves.Single(m => m.WorkId == Song1).EntryId);
        Assert.Equal(ItemMoveModes.AddAnyway, _moves.Single(m => m.WorkId == Song2).Mode);
        Assert.Null(_moves.Single(m => m.WorkId == Song2).EntryId);
        panel.WaitForAssertion(() => Assert.Contains("2 moved", panel.Markup, StringComparison.Ordinal));
        Assert.Equal(1, moved);
    }

    [Fact]
    public void ResultLine_SaysWhatMovedAndWhatWasLeft()
    {
        Assert.Equal("3 moved", ItemBatchMovePanel.ResultLine(3, 0));
        Assert.Equal("2 moved, 1 could not be moved and was left where it was", ItemBatchMovePanel.ResultLine(2, 1));
        Assert.Equal("0 moved, 2 could not be moved and were left where they were", ItemBatchMovePanel.ResultLine(0, 2));
    }

    /// <summary>Answers every confirmation with its primary button.</summary>
    private sealed class ConfirmingDialogService : IAppDialogService
    {
        public Task<IAppDialogReference> ShowAsync<TComponent>(string? title = null, AppDialogParameters? parameters = null, AppDialogOptions? options = null)
            where TComponent : IComponent => Task.FromResult<IAppDialogReference>(new Reference());

        public Task<IAppDialogReference> ShowAsync<TComponent>(string? title, AppDialogOptions options)
            where TComponent : IComponent => ShowAsync<TComponent>(title, null, options);

        public Task<bool?> ShowMessageBoxAsync(string? title, string message, string yesText = "OK", string? noText = null, string? cancelText = null, AppDialogOptions? options = null) =>
            Task.FromResult<bool?>(true);

        public Task<bool?> ShowMessageBoxAsync(string? title, Microsoft.AspNetCore.Components.MarkupString message, string yesText = "OK", string? noText = null, string? cancelText = null, AppDialogOptions? options = null) =>
            Task.FromResult<bool?>(true);

        private sealed class Reference : IAppDialogReference
        {
            public Guid Id { get; } = Guid.NewGuid();
            public Task<AppDialogResult?> Result => Task.FromResult<AppDialogResult?>(AppDialogResult.Ok(AppConfirmResult.Primary));
            public void Close(AppDialogResult? result = null)
            {
            }
        }
    }
}
