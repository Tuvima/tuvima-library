using Bunit;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Batch editing (details overlay spec D8): ticks exist only in Editing Mode, the bar counts them, fields say "No change" or "Mixed",
/// tags carry Add, Replace or Remove, one Apply sends the whole batch, and the result line says what was updated and what went to
/// Needs Review. The Engine is a hand-written stub.
/// </summary>
public sealed class EditingBatchSectionTests : AsyncBunitContext
{
    private static readonly Guid Ep1 = Guid.Parse("33333333-aaaa-bbbb-cccc-000000000001");
    private static readonly Guid Ep2 = Guid.Parse("33333333-aaaa-bbbb-cccc-000000000002");
    private static readonly Guid Ep3 = Guid.Parse("33333333-aaaa-bbbb-cccc-000000000003");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<LibraryBatchEditRequest> _applied = [];
    private LibraryBatchEditResult _result = new() { UpdatedCount = 3 };

    public EditingBatchSectionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.PreviewBatchEditAsync), args =>
            {
                var request = (LibraryBatchEditRequest)args![0]!;
                return Task.FromResult<LibraryBatchEditPreview?>(new LibraryBatchEditPreview
                {
                    AffectedCount = request.EntityIds.Count,
                    Changes = [.. request.FieldChanges.Select(change => new LibraryFieldChangePreview
                    {
                        Key = change.Key,
                        NewValue = change.Value,
                        OldValueCounts = change.Key == "language"
                            ? new Dictionary<string, int> { ["en"] = 2, ["fr"] = 1 }
                            : new Dictionary<string, int> { ["(empty)"] = request.EntityIds.Count },
                    })],
                });
            }
            );
            stub.SetHandler(nameof(IEngineApiClient.ApplyBatchEditAsync), args =>
            {
                _applied.Add((LibraryBatchEditRequest)args![0]!);
                return Task.FromResult<LibraryBatchEditResult?>(_result);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
        }));
    }

    private async Task<EditingModeContext> OnAsync()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var session = new ItemFieldEditSession(Services.GetRequiredService<IEngineApiClient>(), _device);
        var mode = new EditingModeContext(_device, session) { HasPermission = true };
        mode.Loader = () => session.LoadAsync(EditingModeTestData.MovieId, "Movies", "movie", null);
        Assert.True(await mode.EnterAsync());
        return mode;
    }

    private IRenderedComponent<EditingBatchSection> Section(EditingModeContext mode) =>
        Render<EditingBatchSection>(p => p.Add(x => x.Mode, mode).Add(x => x.Singular, "episode").Add(x => x.Plural, "episodes"));

    [Fact]
    public async Task WithNothingTicked_DrawsNothing()
    {
        var mode = await OnAsync();

        var section = Section(mode);

        Assert.Empty(section.FindAll(".tl-batch"));
    }

    [Fact]
    public async Task Ticks_AreRefusedOutsideEditingMode()
    {
        var mode = await OnAsync();
        mode.Leave();

        Assert.False(mode.ToggleBatchSelection(Ep1));
        Assert.Empty(mode.BatchSelection);
    }

    [Fact]
    public async Task TickingRows_ShowsTheBarWithTheCount_AndOpeningShowsNoChangeAndMixed()
    {
        var mode = await OnAsync();
        var section = Section(mode);
        mode.ToggleBatchSelection(Ep1);
        mode.ToggleBatchSelection(Ep2);
        mode.ToggleBatchSelection(Ep3);

        section.WaitForAssertion(() => Assert.Contains("3 episodes selected", section.Find(".tl-batch__bar").TextContent, StringComparison.Ordinal));
        section.Find(".tl-batch__bar-actions button").Click();

        section.WaitForAssertion(() =>
        {
            var inputs = section.FindAll(".tl-batch__panel input");
            Assert.Contains(inputs, i => i.GetAttribute("placeholder") == "No change");
            Assert.Contains(inputs, i => i.GetAttribute("placeholder") == "Mixed");
        });
        Assert.Contains("Apply to 3 episodes", section.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_SendsOneBatchWithTheTagModeAndShowsTheResultLine()
    {
        var mode = await OnAsync();
        _result = new LibraryBatchEditResult { UpdatedCount = 2, FailedIds = [Ep3], SentToReviewCount = 1 };
        var section = Section(mode);
        mode.ToggleBatchSelection(Ep1);
        mode.ToggleBatchSelection(Ep2);
        mode.ToggleBatchSelection(Ep3);
        section.WaitForElement(".tl-batch__bar-actions button").Click();
        var tags = section.WaitForElements(".tl-batch__panel input[type=text], .tl-batch__panel input:not([type])").First();
        tags.Input("Favourite");

        section.WaitForAssertion(() => Assert.True(mode.BatchDirty));
        section.FindAll(".tl-batch__actions button").Single(b => b.TextContent.Contains("Apply to 3 episodes")).Click();

        section.WaitForAssertion(() => Assert.Contains("2 updated, 1 sent to Needs Review", section.Find(".tl-batch__result").TextContent, StringComparison.Ordinal));
        var request = Assert.Single(_applied);
        Assert.Equal(3, request.EntityIds.Count);
        var change = Assert.Single(request.FieldChanges);
        Assert.Equal("custom_tags", change.Key);
        Assert.Equal("Favourite", change.Value);
        Assert.Equal(LibraryFieldChangeModes.Add, change.Mode);
        Assert.False(mode.BatchSectionOpen);
    }

    [Fact]
    public void ResultLine_SaysUpdatedAndSentToNeedsReview()
    {
        Assert.Equal("3 updated", EditingBatchSection.ResultLine(new LibraryBatchEditResult { UpdatedCount = 3 }));
        Assert.Equal("2 updated, 1 sent to Needs Review",
            EditingBatchSection.ResultLine(new LibraryBatchEditResult { UpdatedCount = 2, FailedIds = [Ep1], SentToReviewCount = 1 }));
    }

    [Fact]
    public async Task LeavingEditingMode_ClearsTheTicks()
    {
        var mode = await OnAsync();
        mode.ToggleBatchSelection(Ep1);

        mode.Leave();

        Assert.Empty(mode.BatchSelection);
        Assert.False(mode.BatchSectionOpen);
    }
}
