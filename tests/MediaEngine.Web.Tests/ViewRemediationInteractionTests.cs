using Bunit;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.View;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

public sealed class ViewRemediationInteractionTests : AsyncBunitContext
{
    [Fact]
    public async Task RapidTagEditsCancelOldSearchAndDisposalSuppressesLateSuggestions()
    {
        var oldStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResult=new TaskCompletionSource<IEnumerable<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeResult=new TaskCompletionSource<IEnumerable<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken=default,disposeToken=default;
        var cut=Render<AppTagInput>(p=>p.Add(x=>x.Commit,_=>Task.FromResult(true)).Add(x=>x.Search,(draft,ct)=>{
            if(draft=="old") {oldToken=ct;oldStarted.TrySetResult();return oldResult.Task;}
            if(draft=="dispose") {disposeToken=ct;disposeStarted.TrySetResult();return disposeResult.Task;}
            return Task.FromResult<IEnumerable<string>>(["Current suggestion"]);
        }));
        var input=cut.FindComponent<AppNativeInput>().Instance;
        Task? old=null;
        await cut.InvokeAsync(()=>{old=input.ValueChanged.InvokeAsync("old");});await oldStarted.Task;
        await cut.InvokeAsync(()=>input.ValueChanged.InvokeAsync("current"));
        Assert.True(oldToken.IsCancellationRequested);
        oldResult.SetResult(["Obsolete suggestion"]);await old!;
        cut.WaitForAssertion(()=>Assert.Contains("Current suggestion",cut.Markup));
        Assert.DoesNotContain("Obsolete suggestion",cut.Markup);
        Task? disposing=null;
        await cut.InvokeAsync(()=>{disposing=input.ValueChanged.InvokeAsync("dispose");});await disposeStarted.Task;
        await cut.InvokeAsync(cut.Instance.Dispose);Assert.True(disposeToken.IsCancellationRequested);
        disposeResult.SetResult(["Disposed suggestion"]);await disposing!;
        cut.Dispose();
    }
    [Fact]
    public void TypingTagDoesNotCommit_ExplicitSubmitCommitsWholePhrase()
    {
        var saved = new List<string>();
        var cut = Render<AppTagInput>(p => p
            .Add(x => x.Search, (_, _) => Task.FromResult<IEnumerable<string>>(["Summer vacation"]))
            .Add(x => x.Commit, value => { saved.Add(value); return Task.FromResult(true); }));
        foreach (var draft in new[] { "S", "Su", "Summer vacation" })
        {
            cut.Find("input").Input(draft);
        }
        Assert.Empty(saved);
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal(["Summer vacation"], saved));
        Assert.Equal("", cut.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void FailedTagCommitRetainsDraftForRetry()
    {
        var cut = Render<AppTagInput>(p => p
            .Add(x => x.Search, (_, _) => Task.FromResult<IEnumerable<string>>([]))
            .Add(x => x.Commit, _ => Task.FromResult(false)));
        cut.Find("input").Input("Tokyo trip");
        cut.Find("form").Submit();
        Assert.Equal("Tokyo trip", cut.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void ScaleUsesSamePositionForEveryRepresentationOfDate()
    {
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var scale = new PlacesTimeScale(start, start.AddYears(2));
        foreach (var tick in scale.Ticks())
        {
            Assert.Equal(scale.Percent(tick), scale.Percent(scale.Index(scale.DateAt(tick))));
        }
        Assert.Equal(0, scale.Percent(0));
        Assert.Equal(100, scale.Percent(scale.Days));
    }

    [Fact]
    public void SingleDateHasNonzeroDomainAndUniqueTicks()
    {
        var date = new DateTimeOffset(2024, 2, 29, 0, 0, 0, TimeSpan.Zero);
        var scale = new PlacesTimeScale(date, date);
        Assert.True(scale.Days > 0);
        Assert.Equal(scale.Ticks().Count(), scale.Ticks().Distinct().Count());
        Assert.Equal(date, scale.DateAt(scale.Index(date)));
    }
}
