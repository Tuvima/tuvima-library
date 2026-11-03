using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>"Put in a series" for several ticked videos (universal editor v3.1, D9): offered only for videos, stages one change each, and moves nothing.</summary>
public sealed class BulkVideoSeriesTests : AsyncBunitContext
{
    private static readonly Guid One = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly Guid Two = Guid.Parse("eeeeeeee-0000-0000-0000-000000000002");
    private static readonly Guid Course = Guid.Parse("eeeeeeee-0000-0000-0000-0000000000c1");

    private static IReadOnlyList<BulkEditRow> Videos() =>
        [new BulkEditRow(One, "Kitchen tour", DetailEntityType.Work, null, "Videos"), new BulkEditRow(Two, "Bread day", DetailEntityType.Work, null, "Videos")];

    public BulkVideoSeriesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetVideoSeriesAsync), _ => Task.FromResult<VideoSeriesStateResponse?>(new VideoSeriesStateResponse
            {
                Supported = true,
                Targets = [new VideoSeriesTargetDto { Id = Course, Title = "Cooking with Sam", VideoCount = 4 }],
            }))));
    }

    [Fact]
    public void PutInASeries_IsOfferedOnlyWhenEveryTickedRowIsAVideo()
    {
        Assert.True(BulkEditRules.CanPutInSeries(Videos()));
        Assert.False(BulkEditRules.CanPutInSeries([.. Videos(), new BulkEditRow(Guid.NewGuid(), "Song", DetailEntityType.Work, null, "Music")]));
        Assert.False(BulkEditRules.CanPutInSeries([]));
        Assert.Equal("Editing 2 videos", BulkEditRules.EditingTitle(Videos()));
    }

    [Fact]
    public void Staging_GivesEveryVideoItsOwnSeriesChange()
    {
        var pending = new PendingEditList();

        var staged = BulkEditStager.PutInSeries(pending, Videos(), Course, null, "Cooking with Sam");

        Assert.Equal(2, staged);
        Assert.All(pending.Changes, change =>
        {
            Assert.Equal(EditSessionChangeKinds.VideoSeries, change.Kind);
            Assert.Equal(Course.ToString(), change.TargetId);
        });
        Assert.Equal([One, Two], pending.Changes.Select(change => change.EntityId));
    }

    [Fact]
    public void Staging_ANewSeries_NeedsAName()
    {
        var pending = new PendingEditList();

        Assert.Equal(0, BulkEditStager.PutInSeries(pending, Videos(), null, "  ", "x"));
        Assert.Equal(2, BulkEditStager.PutInSeries(pending, Videos(), null, "Garden talks", "Garden talks"));
        Assert.All(pending.Changes, change => Assert.Equal("new:Garden talks", change.Token));
    }

    [Fact]
    public void ThePicker_OffersTheSeriesAndStagesOneChangePerVideo()
    {
        var pending = new PendingEditList();
        var done = false;
        var cut = Render<BulkVideoSeriesPicker>(p => p
            .Add(c => c.Rows, Videos())
            .Add(c => c.Pending, pending)
            .Add(c => c.CanChange, true)
            .Add(c => c.OnDone, () => done = true));

        cut.WaitForAssertion(() => Assert.Contains("Cooking with Sam", cut.Markup));
        cut.Find("button:contains('Stage this series')").Click();

        Assert.Equal(2, pending.Changes.Count);
        Assert.True(done);
    }
}
