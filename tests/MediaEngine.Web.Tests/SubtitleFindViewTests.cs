using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Video player follow-ups D9 to D12 (mockup section 3): Find subtitles is a list you pick from, searched once as the view opens in the profile's
/// language, with a plain sentence for every outcome written by the Dashboard. The Engine is a hand-written stub that records every call.
/// </summary>
public sealed class SubtitleFindViewTests : AsyncBunitContext
{
    private static readonly Guid Asset = Guid.NewGuid();
    private readonly List<(string Kind, string? Language)> _searches = [];
    private readonly List<ChooseTextTrackAlternativeRequest> _chosen = [];
    private readonly List<Guid> _markedWrong = [];
    private readonly List<Guid?> _tracksChanged = [];

    public SubtitleFindViewTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton(new PlaybackTransientToolCoordinator());
        Render<AppPopoverHost>();
    }

    private static TextTrackAlternativeDto Item(string id, bool current = false, double confidence = 0.93, bool hearingImpaired = false, string release = "The.Long.Way.Home.2019.1080p.BluRay.x264") =>
        new()
        {
            provider = "subdl",
            provider_name = "SubDL",
            source_id = id,
            language = "en",
            is_timed = true,
            confidence = confidence,
            is_hearing_impaired = hearingImpaired,
            release_name = release,
            is_current = current,
        };

    private static TextTrackAlternativesResponse Found(params TextTrackAlternativeDto[] items) =>
        new() { asset_id = Asset, kind = "Subtitles", status = "Found", source_name = "SubDL", language = "en", items = items };

    private static TextTrackAlternativesResponse Status(string status, string? source = "SubDL") =>
        new() { asset_id = Asset, kind = "Subtitles", status = status, source_name = source, message = "an internal sentence the view must never show" };

    private IEngineApiClient Api(Func<TextTrackAlternativesResponse?> answer) => EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetTextTrackAlternativesAsync), args =>
        {
            _searches.Add(((string)args![1]!, (string?)args[3]));
            return Task.FromResult(answer());
        });
        stub.SetHandler(nameof(IEngineApiClient.ChooseTextTrackAlternativeAsync), args =>
        {
            _chosen.Add((ChooseTextTrackAlternativeRequest)args![1]!);
            return Task.FromResult<TextTrackChoiceResponse?>(new() { asset_id = Asset, status = "Updated", track_id = Guid.Parse("11111111-1111-1111-1111-111111111111") });
        });
        stub.SetHandler(nameof(IEngineApiClient.MarkTextTrackWrongAsync), args =>
        {
            _markedWrong.Add((Guid)args![1]!);
            return Task.FromResult<TextTrackChoiceResponse?>(new() { asset_id = Asset, status = "Updated" });
        });
    });

    private SubtitleFindPresenter Presenter(IEngineApiClient api, TimeSpan? wait = null)
    {
        var presenter = new SubtitleFindPresenter(api, wait) { TracksChanged = id => { _tracksChanged.Add(id); return Task.CompletedTask; } };
        presenter.Bind(Asset);
        return presenter;
    }

    private async Task<IRenderedComponent<AudioSubtitlesPanel>> OpenFindAsync(
        SubtitleFindPresenter presenter,
        bool administrator = false,
        bool episode = false,
        bool noSource = false,
        SubtitleLanguageSettingsDto? look = null,
        bool phone = false)
    {
        var cut = Render<AudioSubtitlesPanel>(parameters => parameters
            .Add(p => p.Find, presenter)
            .Add(p => p.IsAdministrator, administrator)
            .Add(p => p.IsEpisode, episode)
            .Add(p => p.NoSubtitleSource, noSource)
            .Add(p => p.Look, look));
        var panel = cut.FindComponent<PlaybackPanel>();
        if (phone)
        {
            await cut.InvokeAsync(() => panel.Instance.SetViewport(true));
        }
        await cut.InvokeAsync(() => panel.Instance.OpenAsync(true));
        cut.WaitForElement("[data-audio-subtitles]");
        await cut.Find("[data-asa-action='find']").ClickAsync();
        return cut;
    }

    [Fact]
    public async Task TheViewListsTheResults_WithTheirTags_AndTheFirstOtherOneIsThePrimaryAction()
    {
        var api = Api(() => Found(Item("a", current: true, hearingImpaired: true), Item("b", release: "The.Long.Way.Home.2019.2160p.UHD.BluRay.x265"), Item("c", confidence: 0.84, release: "WEB-DL.720p")));
        var cut = await OpenFindAsync(Presenter(api));

        cut.WaitForElement("[data-sfv-results]");

        Assert.Equal("3 results from SubDL", cut.Find("[data-sfv-head]").TextContent);
        var rows = cut.FindAll("[data-sfv-result]");
        Assert.Equal(["a", "b", "c"], rows.Select(row => row.GetAttribute("data-sfv-result")));
        Assert.Equal(["current", "hearing-impaired", "same-release"], rows[0].QuerySelectorAll("[data-sfv-tag]").Select(tag => tag.GetAttribute("data-sfv-tag")));
        Assert.Equal("This one is wrong", rows[0].QuerySelector("[data-sfv-action='wrong']")!.TextContent.Trim());
        Assert.Equal("Use this", rows[1].QuerySelector("[data-sfv-action='use']")!.TextContent.Trim());
        Assert.Contains("sfv__button--primary", rows[1].QuerySelector("[data-sfv-action='use']")!.ClassList);
        Assert.DoesNotContain("sfv__button--primary", rows[2].QuerySelector("[data-sfv-action='use']")!.ClassList);
        Assert.Equal("different-release", rows[2].QuerySelector("[data-sfv-tag='different-release']")!.GetAttribute("data-sfv-tag"));
        Assert.Equal("WEB-DL.720p", rows[2].QuerySelector(".sfv__release")!.GetAttribute("data-overflow-tooltip"));
        Assert.Equal("Your choice applies to everyone watching this title.", cut.Find("[data-sfv-note]").TextContent);
    }

    [Fact]
    public async Task TheSearchRunsOnceWhenTheViewOpens_NotWhenThePanelOpens()
    {
        var api = Api(() => Found(Item("a")));
        var presenter = Presenter(api);
        var cut = Render<AudioSubtitlesPanel>(parameters => parameters.Add(p => p.Find, presenter));
        var panel = cut.FindComponent<PlaybackPanel>();
        await cut.InvokeAsync(() => panel.Instance.OpenAsync(true));
        cut.WaitForElement("[data-audio-subtitles]");
        Assert.Empty(_searches);

        await cut.Find("[data-asa-action='find']").ClickAsync();
        cut.WaitForElement("[data-sfv-results]");

        Assert.Single(_searches);
        Assert.Equal("subtitles", _searches[0].Kind);
    }

    [Fact]
    public async Task UsingAResult_ChoosesIt_TellsTheHostTheTracksChanged_AndMarksTheRowInUse()
    {
        var api = Api(() => Found(Item("a", current: true), Item("b")));
        var cut = await OpenFindAsync(Presenter(api));
        cut.WaitForElement("[data-sfv-results]");

        await cut.Find("[data-sfv-result='b'] [data-sfv-action='use']").ClickAsync();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-sfv-result='b'] [data-sfv-tag='current']")));
        var choice = Assert.Single(_chosen);
        Assert.Equal(("subtitles", "subdl", "b", "English"), (choice.kind, choice.provider, choice.source_id, choice.language));
        Assert.Equal([Guid.Parse("11111111-1111-1111-1111-111111111111")], _tracksChanged.Select(id => id!.Value));
        Assert.Empty(cut.FindAll("[data-sfv-result='a'] [data-sfv-tag='current']"));
        Assert.NotNull(cut.Find("[data-sfv-result='b'] [data-sfv-action='wrong']"));
    }

    [Fact]
    public async Task AFailedDownload_SaysSoOnThatRowOnly_AndTheListStays()
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackAlternativesAsync), _ => Task.FromResult<TextTrackAlternativesResponse?>(Found(Item("a"), Item("b"))));
            stub.SetHandler(nameof(IEngineApiClient.ChooseTextTrackAlternativeAsync), _ => Task.FromResult<TextTrackChoiceResponse?>(new() { status = "Unavailable" }));
        });
        var cut = await OpenFindAsync(Presenter(api));
        cut.WaitForElement("[data-sfv-results]");

        await cut.Find("[data-sfv-result='b'] [data-sfv-action='use']").ClickAsync();

        cut.WaitForAssertion(() => Assert.Equal("Could not download this one", cut.Find("[data-sfv-result='b'] [data-sfv-row-error]").TextContent));
        Assert.Equal("status", cut.Find("[data-sfv-result='b'] [data-sfv-row-error]").GetAttribute("role"));
        Assert.Empty(cut.FindAll("[data-sfv-result='a'] [data-sfv-row-error]"));
        Assert.Equal(2, cut.FindAll("[data-sfv-result]").Count);
        Assert.Empty(_tracksChanged);
    }

    [Fact]
    public async Task ThisOneIsWrong_MarksTheTrackInUseWrong_ReloadsTheTracks_AndSearchesAgain()
    {
        var current = Guid.NewGuid();
        var api = Api(() => Found(Item("a", current: true), Item("b")));
        var presenter = Presenter(api);
        presenter.CurrentTrackFor = provider => provider == "subdl" ? current : null;
        var cut = await OpenFindAsync(presenter);
        cut.WaitForElement("[data-sfv-results]");

        await cut.Find("[data-sfv-result='a'] [data-sfv-action='wrong']").ClickAsync();

        cut.WaitForAssertion(() => Assert.Equal(2, _searches.Count));
        Assert.Equal([current], _markedWrong);
        Assert.Single(_tracksChanged);
    }

    [Fact]
    public async Task TheSearchStartsInTheProfilesPreferredSubtitleLanguage()
    {
        var api = Api(() => Found(Item("a")));
        var look = new SubtitleLanguageSettingsDto { DefaultSubtitleLanguage = "French", AlternateSubtitleLanguages = ["German"] };

        var cut = await OpenFindAsync(Presenter(api), look: look);
        cut.WaitForElement("[data-sfv-results]");

        Assert.Equal("French", _searches[0].Language);
        var options = cut.FindAll("[data-sfv-language] option").Select(option => option.TextContent).ToList();
        Assert.Equal(["French", "German"], options.Take(2));
        Assert.Contains("English", options);
        Assert.Equal(options.Count, options.Distinct().Count());
    }

    [Fact]
    public async Task ChangingTheLanguage_SearchesAgainInThatLanguage()
    {
        var api = Api(() => Found(Item("a")));
        var cut = await OpenFindAsync(Presenter(api));
        cut.WaitForElement("[data-sfv-results]");

        await cut.Find("[data-sfv-language] select").ChangeAsync(new ChangeEventArgs { Value = "Spanish" });

        cut.WaitForAssertion(() => Assert.Equal(2, _searches.Count));
        Assert.Equal("English", _searches[0].Language);
        Assert.Equal("Spanish", _searches[1].Language);
    }

    [Fact]
    public async Task ASearchThatTakesLong_SaysStillSearching_KeepsTheLanguageChoice_AndThenShowsTheAnswer()
    {
        var gate = new TaskCompletionSource<TextTrackAlternativesResponse?>();
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetTextTrackAlternativesAsync), _ => gate.Task));
        var cut = await OpenFindAsync(Presenter(api, TimeSpan.FromMilliseconds(60)));

        Assert.Equal("Searching…", cut.Find("[data-sfv-head]").TextContent);
        cut.WaitForAssertion(() => Assert.Equal("Still searching…", cut.Find("[data-sfv-head]").TextContent));
        Assert.NotNull(cut.Find("[data-sfv-language] select"));

        gate.SetResult(Found(Item("a")));
        cut.WaitForElement("[data-sfv-results]");
        Assert.Equal("1 result from SubDL", cut.Find("[data-sfv-head]").TextContent);
    }

    [Theory]
    [InlineData("NoneFound", false, "No English subtitles were found for this movie.", "Try another language.")]
    [InlineData("NoneFound", true, "No English subtitles were found for this episode.", "Try another language.")]
    [InlineData("NoSourceOn", false, "No subtitle source is switched on.", "")]
    [InlineData("SourceKeyRequired", false, "SubDL did not accept its key.", "")]
    [InlineData("IdentityUnverified", true, "This episode could not be matched for an online search.", "Searching needs the show to be matched and a movie database key switched on.")]
    [InlineData("IdentityUnverified", false, "This movie could not be matched for an online search.", "Searching needs the movie to be matched.")]
    [InlineData("LimitReached", false, "SubDL has reached its limit for now. Try again later.", "")]
    [InlineData("Unavailable", false, "SubDL could not be reached.", "")]
    [InlineData("Blocked", false, "This library never looks anything up online.", "")]
    [InlineData("NotAllowed", false, "Your profile cannot search for subtitles.", "")]
    [InlineData("LanguageUnknown", false, "That language is not supported for subtitle search.", "")]
    public async Task EveryOutcome_ShowsItsOwnSentence_NotTheEnginesMessage(string status, bool episode, string headline, string detail)
    {
        var api = Api(() => Status(status));
        var cut = await OpenFindAsync(Presenter(api), episode: episode);

        var reason = cut.WaitForElement($"[data-subtitles-state='{status}']");

        Assert.Equal("status", reason.GetAttribute("role"));
        Assert.Equal(headline, reason.QuerySelector("b")!.TextContent);
        Assert.Equal(detail, reason.QuerySelector("span")?.TextContent ?? string.Empty);
        Assert.DoesNotContain("internal sentence", cut.Markup);
    }

    [Fact]
    public async Task AnEngineThatCannotBeReached_SaysTheSearchCouldNotBeCompleted_WithTryAgain()
    {
        var calls = 0;
        var api = Api(() => ++calls == 1 ? null : Found(Item("a")));
        var cut = await OpenFindAsync(Presenter(api));

        var reason = cut.WaitForElement("[data-subtitles-state='Failed']");

        Assert.Equal("The search could not be completed.", reason.QuerySelector("b")!.TextContent);
        await cut.Find("[data-sfv-action='retry']").ClickAsync();
        cut.WaitForElement("[data-sfv-results]");
        Assert.Equal(2, _searches.Count);
    }

    [Theory]
    [InlineData("Unavailable", true)]
    [InlineData("LimitReached", false)]
    public async Task OnlyTheUnreachableSourceOffersTryAgain(string status, bool offersRetry)
    {
        var api = Api(() => Status(status));
        var cut = await OpenFindAsync(Presenter(api));

        cut.WaitForElement($"[data-subtitles-state='{status}']");

        Assert.Equal(offersRetry, cut.FindAll("[data-sfv-action='retry']").Count == 1);
    }

    [Theory]
    [InlineData("NoSourceOn", true, true)]
    [InlineData("SourceKeyRequired", true, true)]
    [InlineData("NoSourceOn", false, false)]
    [InlineData("SourceKeyRequired", false, false)]
    [InlineData("NoneFound", true, false)]
    [InlineData("LimitReached", true, false)]
    public async Task AdministratorsGetThePluginsLinkForTheTwoSourceStates_AndNobodyElseDoes(string status, bool administrator, bool linked)
    {
        var api = Api(() => Status(status));
        var cut = await OpenFindAsync(Presenter(api), administrator: administrator);

        cut.WaitForElement($"[data-subtitles-state='{status}']");

        Assert.Equal(linked, cut.FindAll("a[href='/settings/plugins']").Count == 1);
    }

    [Fact]
    public async Task WhenTheOpeningCheckSaysNoSourceIsOn_NothingIsSearched()
    {
        var api = Api(() => Found(Item("a")));
        var cut = await OpenFindAsync(Presenter(api), noSource: true, administrator: true);

        Assert.Equal("No subtitle source is switched on.", cut.Find("[data-subtitles-state='NoSourceOn'] b").TextContent);
        Assert.NotNull(cut.Find("a[href='/settings/plugins']"));
        Assert.Empty(_searches);
    }

    [Fact]
    public async Task OnAPhoneSheet_EachResultIsOneColumnWithAFullWidthAction()
    {
        var api = Api(() => Found(Item("a"), Item("b")));
        var cut = await OpenFindAsync(Presenter(api), phone: true);

        cut.WaitForElement("[data-sfv-results]");

        Assert.Equal("true", cut.Find("[data-subtitle-find]").GetAttribute("data-sheet"));
        Assert.Equal(2, cut.FindAll("[data-sfv-action='use']").Count);
    }
}
