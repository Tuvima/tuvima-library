using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// "Move to episode" and "Move to Extras" in the editor's Match tool (Extras spec WP4): a show lists its video clips, an episode offers
/// one button, and both only stage a change that Review changes shows.
/// </summary>
public sealed class EditorExtrasMovePanelTests : AsyncBunitContext
{
    private static readonly Guid ShowId = Guid.Parse("33333333-0000-0000-0000-000000000001");
    private static readonly string ClipId = Guid.NewGuid().ToString("D");

    private readonly EditSessionState _session = new(new PendingEditList());

    public EditorExtrasMovePanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _session.Begin([new EditTrailItem(ShowId, "Masterclass", "TvShow")]);
    }

    [Fact]
    public void AShow_ListsOnlyItsVideoClips()
    {
        var cut = RenderPanel("series", [Card(ClipId, DetailExtraCardKinds.Clip, "Trailer"), Card(Guid.NewGuid().ToString("D"), DetailExtraCardKinds.Document, "Workbook")]);

        Assert.Contains("Trailer", cut.Markup);
        Assert.DoesNotContain("Workbook", cut.Markup);
        Assert.Contains("Move to episode", cut.Markup);
    }

    [Fact]
    public void ChoosingASeasonAndEpisode_OnlyStagesTheMove()
    {
        var cut = RenderPanel("series", [Card(ClipId, DetailExtraCardKinds.Clip, "Trailer")]);

        cut.Find("button:contains('Move to episode')").Click();
        cut.FindAll("input")[0].Change("2");
        cut.FindAll("input")[1].Change("5");
        cut.Find("button:contains('Move')").Click();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.ExtraMove, staged.Kind);
        Assert.Equal(ShowId, staged.EntityId);
        Assert.Equal(ClipId, staged.TargetId);
        Assert.Equal("episode:2:5", staged.Token);
        Assert.Contains("season 2, episode 5", cut.Markup);
    }

    [Fact]
    public void AStagedMove_CanBeUndone()
    {
        _session.Pending.SetExtraMove(ShowId, ClipId, EditSessionExtraMove.ToEpisodeToken(1, 2), "Trailer: Season 1, episode 2");
        var cut = RenderPanel("series", [Card(ClipId, DetailExtraCardKinds.Clip, "Trailer")]);

        cut.Find("button:contains('Undo')").Click();

        Assert.Empty(_session.Pending.Changes);
    }

    [Fact]
    public void AnEpisode_OffersMoveToExtras()
    {
        var cut = RenderPanel("episode", []);

        cut.Find("button:contains('Move to Extras')").Click();

        var staged = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionExtraMove.ToExtrasToken, staged.Token);
        Assert.Null(staged.TargetId);
    }

    [Fact]
    public void TwoClipsOfOneShow_AreStagedSeparately()
    {
        var other = Guid.NewGuid().ToString("D");
        _session.Pending.SetExtraMove(ShowId, ClipId, "episode:1:1");
        _session.Pending.SetExtraMove(ShowId, other, "episode:1:2");
        _session.Pending.SetExtraMove(ShowId, ClipId, "episode:1:3");

        Assert.Equal(2, _session.Pending.Changes.Count);
        Assert.Equal("episode:1:3", _session.Pending.FindExtraMove(ShowId, ClipId)!.Token);
    }

    [Fact]
    public void ReviewChanges_NamesWhatMovesAndWhatItCosts()
    {
        Assert.False(EditSessionExtraMove.TryParseEpisode("episode:1:0", out _, out _));
        Assert.False(EditSessionExtraMove.TryParseEpisode("episode:100:1", out _, out _));
        Assert.True(EditSessionExtraMove.TryParseEpisode("episode:0:3", out var season, out var episode));
        Assert.Equal((0, 3), (season, episode));
        Assert.Contains("progress is lost", EditReviewRows.ExtraToExtrasNote);
    }

    private IRenderedComponent<EditorExtrasMovePanel> RenderPanel(string scope, IReadOnlyList<DetailExtraCardViewModel> extras) =>
        Render<EditorExtrasMovePanel>(p => p
            .Add(c => c.Subject, new EditorMatchSubject(ShowId, "TV", scope, "series", "Masterclass", null, null, null,
                new Dictionary<string, string>(), []))
            .Add(c => c.Extras, extras)
            .Add(c => c.Session, _session)
            .Add(c => c.CanEdit, true));

    private static DetailExtraCardViewModel Card(string id, string kind, string title) =>
        new() { Id = id, Kind = kind, Title = title };
}
