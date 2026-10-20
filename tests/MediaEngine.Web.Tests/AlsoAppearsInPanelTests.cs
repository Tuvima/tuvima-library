using Bunit;
using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Universe;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// "Also appears in": the panel the Universe page opens from a Cast row (and a later Characters list). One section per
/// character plus a "More with" section for the person, each loading and failing on its own, with the honest gap notes
/// under character sections and every row linking to the route the Engine gave it.
/// </summary>
public sealed class AlsoAppearsInPanelTests : AsyncBunitContext
{
    private const string Universe = "Q4242";
    private static readonly Guid Person = Guid.Parse("40000000-0000-0000-0000-000000000004");

    public AlsoAppearsInPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private readonly List<(string? Entity, Guid? Person)> _requests = [];

    private IRenderedComponent<AlsoAppearsInPanel> RenderPanel(
        Func<string?, Guid?, Task<EntityAppearancesDto?>> respond,
        IReadOnlyList<(string Qid, string Label)> characters,
        Guid? personId = null,
        string? personName = null,
        IAppDialogContext? dialog = null,
        EventCallback onClose = default)
    {
        Services.AddSingleton(EngineApiClientStub.Create(stub => stub.SetHandler(
            nameof(IEngineApiClient.GetEntityAppearancesAsync),
            args =>
            {
                var entity = (string?)args![1];
                var person = (Guid?)args[2];
                lock (_requests)
                {
                    _requests.Add((entity, person));
                }

                return respond(entity, person);
            })));

        return Render<AlsoAppearsInPanel>(parameters =>
        {
            if (dialog is not null)
            {
                parameters.AddCascadingValue(dialog);
            }

            parameters
                .Add(component => component.OnClose, onClose)
                .Add(component => component.UniverseQid, Universe)
                .Add(component => component.Characters, characters)
                .Add(component => component.PersonId, personId)
                .Add(component => component.PersonName, personName);
        });
    }

    private static Task<EntityAppearancesDto?> Ok(EntityAppearancesDto dto) => Task.FromResult<EntityAppearancesDto?>(dto);

    private static EntityAppearancesDto Appearances(
        string label,
        IReadOnlyList<EntityAppearanceGroupDto>? groups = null,
        int withoutData = 0,
        int notYetChecked = 0) =>
        new()
        {
            UniverseQid = Universe,
            SubjectKind = EntityAppearanceSubjectKinds.Character,
            SubjectLabel = label,
            Groups = groups ?? [],
            TotalWorks = groups?.Sum(group => group.Works.Count) ?? 0,
            WorksWithoutCharacterData = withoutData,
            WorksNotYetChecked = notYetChecked,
        };

    private static EntityAppearanceGroupDto Group(string kind, string label, params EntityAppearanceWorkDto[] works) =>
        new() { MediaKind = kind, Label = label, Works = works };

    private static EntityAppearanceWorkDto Work(string title, string route, int? year = null, string? role = null, string? cover = null) =>
        new() { WorkId = Guid.NewGuid(), Title = title, Route = route, Year = year, Role = role, CoverUrl = cover };

    [Fact]
    public void ASectionRendersForEachCharacterAndForThePerson()
    {
        var cut = RenderPanel(
            (entity, person) => Ok(Appearances(entity ?? "person")),
            [("Q1", "Hal Jordan"), ("Q2", "John Stewart")],
            Person,
            "Dana Test");

        var headings = cut.FindAll(".also-appears-in__heading").Select(heading => heading.TextContent.Trim()).ToList();
        Assert.Equal(["Hal Jordan also appears in", "John Stewart also appears in", "More with Dana Test"], headings);
        Assert.Contains(("Q1", (Guid?)null), _requests);
        Assert.Contains(("Q2", (Guid?)null), _requests);
        Assert.Contains(((string?)null, (Guid?)Person), _requests);
        Assert.Equal(3, _requests.Count);
    }

    [Fact]
    public void AtMostSixCharactersAreLookedUp()
    {
        var characters = Enumerable.Range(1, 8).Select(index => ($"Q{index}", $"Character {index}")).ToList();

        var cut = RenderPanel((entity, _) => Ok(Appearances(entity ?? string.Empty)), characters);

        Assert.Equal(6, cut.FindAll(".also-appears-in__section").Count);
        Assert.Equal(6, _requests.Count);
    }

    [Fact]
    public void ThePersonSectionIsLeftOutWithoutAPerson()
    {
        var cut = RenderPanel((entity, _) => Ok(Appearances(entity ?? string.Empty)), [("Q1", "Hal Jordan")]);

        Assert.Single(cut.FindAll(".also-appears-in__section"));
        Assert.DoesNotContain("More with", cut.Markup);
    }

    [Fact]
    public void WorksAreListedUnderTheirFormatHeading_EachRowLinkingToTheRouteTheEngineGave()
    {
        var showRoute = "/details/tvshow/10000000-0000-0000-0000-000000000001?context=watch";
        var comicRoute = "/details/work/20000000-0000-0000-0000-000000000002?context=read";
        var cut = RenderPanel(
            (_, _) => Ok(Appearances(
                "Hal Jordan",
                [
                    Group("TV", "TV", Work("Lanterns", showRoute, 2024, "protagonist", "/stream/artwork/a")),
                    Group("Comic", "Comics", Work("Green Lantern #1", comicRoute, 1960)),
                ])),
            [("Q1", "Hal Jordan")]);

        Assert.Equal(["TV", "Comics"], cut.FindAll(".also-appears-in__group").Select(group => group.TextContent.Trim()));
        var rows = cut.FindAll("a.also-appears-in__row");
        Assert.Equal([showRoute, comicRoute], rows.Select(row => row.GetAttribute("href")));
        Assert.Contains("Lanterns", rows[0].TextContent);
        Assert.Contains("2024", rows[0].TextContent);
        Assert.Contains("protagonist", rows[0].TextContent);
        Assert.Contains("/stream/artwork/a", rows[0].InnerHtml);
        Assert.Contains("Green Lantern #1", rows[1].TextContent);
        // A row without a role or cover invents neither.
        Assert.DoesNotContain("<img", rows[1].InnerHtml);
    }

    [Fact]
    public void ANothingElseSection_SaysSoWithTheCharactersName()
    {
        var cut = RenderPanel((_, _) => Ok(Appearances("Hal Jordan")), [("Q1", "Hal Jordan")]);

        Assert.Equal("Nothing else in your Library shares Hal Jordan.", cut.Find(".also-appears-in__empty").TextContent.Trim());
        Assert.Empty(cut.FindAll("a.also-appears-in__row"));
    }

    [Fact]
    public void ANothingElseSection_ForAPerson_SaysSoWithTheirName()
    {
        var cut = RenderPanel((_, _) => Ok(Appearances("Dana Test")), [], Person, "Dana Test");

        Assert.Equal("Nothing else in your Library features Dana Test.", cut.Find(".also-appears-in__empty").TextContent.Trim());
    }

    [Fact]
    public void GapNotes_SayHowManyWorksMayBeMissing_AndNothingWhenThereAreNone()
    {
        var withGaps = RenderPanel(
            (_, _) => Ok(Appearances("Hal Jordan", [Group("Movie", "Movies", Work("Film", "/details/work/x"))], withoutData: 3, notYetChecked: 2)),
            [("Q1", "Hal Jordan")]);

        Assert.Equal(
            "3 works in this Universe have no character details on Wikidata, so they may be missing here.",
            withGaps.Find("[data-note='no-character-data']").TextContent.Trim());
        Assert.Equal("2 works haven't been checked yet.", withGaps.Find("[data-note='not-yet-checked']").TextContent.Trim());
    }

    [Fact]
    public void GapNotes_UseTheSingularForOneWork()
    {
        var one = RenderPanel(
            (_, _) => Ok(Appearances("Hal Jordan", withoutData: 1, notYetChecked: 1)),
            [("Q1", "Hal Jordan")]);
        Assert.Equal(
            "1 work in this Universe has no character details on Wikidata, so it may be missing here.",
            one.Find("[data-note='no-character-data']").TextContent.Trim());
        Assert.Equal("1 work hasn't been checked yet.", one.Find("[data-note='not-yet-checked']").TextContent.Trim());
    }

    [Fact]
    public void GapNotes_AreAbsent_WhenNoWorkIsMissingOrUnchecked()
    {
        var none = RenderPanel((_, _) => Ok(Appearances("Hal Jordan")), [("Q1", "Hal Jordan")]);

        Assert.Empty(none.FindAll(".also-appears-in__note"));
    }

    [Fact]
    public void ThePersonSection_NeverShowsUniverseGapNotes()
    {
        var cut = RenderPanel(
            (_, _) => Ok(Appearances("Dana Test", withoutData: 4, notYetChecked: 4)),
            [],
            Person,
            "Dana Test");

        Assert.Empty(cut.FindAll(".also-appears-in__note"));
    }

    [Fact]
    public void ALoadingSection_ShowsASkeleton_UntilItsAnswerArrives()
    {
        var pending = new TaskCompletionSource<EntityAppearancesDto?>();
        var cut = RenderPanel((_, _) => pending.Task, [("Q1", "Hal Jordan")]);

        Assert.NotEmpty(cut.FindAll(".app-skeleton"));
        Assert.Equal("Hal Jordan also appears in", cut.Find(".also-appears-in__heading").TextContent.Trim());

        pending.SetResult(Appearances("Hal Jordan", [Group("Movie", "Movies", Work("Film", "/details/work/x"))]));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".app-skeleton"));
            Assert.Single(cut.FindAll("a.also-appears-in__row"));
        });
    }

    [Fact]
    public void AFailedSection_ShowsAnErrorWithRetry_AndTheOtherSectionsStillShow()
    {
        var attempts = 0;
        var cut = RenderPanel(
            (entity, _) => entity == "Q1" && Interlocked.Increment(ref attempts) == 1
                ? Task.FromResult<EntityAppearancesDto?>(null)
                : Ok(Appearances(entity ?? string.Empty, [Group("Movie", "Movies", Work($"Film for {entity}", $"/details/work/{entity}"))])),
            [("Q1", "Hal Jordan"), ("Q2", "John Stewart")]);

        var sections = cut.FindAll(".also-appears-in__section");
        Assert.Contains("Couldn't load this right now", sections[0].TextContent);
        Assert.Contains("Film for Q2", sections[1].TextContent);
        Assert.DoesNotContain("Couldn't load this right now", sections[1].TextContent);

        cut.Find(".app-error-state__retry").Click();

        cut.WaitForAssertion(() =>
        {
            var retried = cut.FindAll(".also-appears-in__section")[0];
            Assert.DoesNotContain("Couldn't load this right now", retried.TextContent);
            Assert.Contains("Film for Q1", retried.TextContent);
        });
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void ASubjectTheEngineSaysNothingVisibleAbout_ShowsTheEmptyWording_NotAnErrorWithRetry()
    {
        // The client turns the Engine's 404 into this empty result; only a real failure (null) is an error.
        var cut = RenderPanel(
            (_, _) => Ok(new EntityAppearancesDto { UniverseQid = Universe, SubjectKind = EntityAppearanceSubjectKinds.Person, PersonId = Person }),
            [],
            Person,
            "Dana Test");

        Assert.Equal("Nothing else in your Library features Dana Test.", cut.Find(".also-appears-in__empty").TextContent.Trim());
        Assert.Empty(cut.FindAll(".app-error-state"));
        Assert.Empty(cut.FindAll(".app-error-state__retry"));
    }

    [Fact]
    public async Task OnClose_IsRaisedOnce_WhenTheDialogServiceClosesThePanel()
    {
        var dialog = new RecordingDialogContext();
        var raised = 0;
        RenderPanel(
            (_, _) => Ok(Appearances("Hal Jordan")),
            [("Q1", "Hal Jordan")],
            dialog: dialog,
            onClose: EventCallback.Factory.Create(this, () => raised++));

        // Escape and a tap outside close through the dialog context, never through the panel's own close button.
        await dialog.CancelAsync();
        await dialog.CancelAsync();

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task OpeningThePanel_AsksForABottomSheetOnPhoneAndTablet_AndPassesWhatItWasGiven()
    {
        var dialogs = new RecordingDialogService();

        await AlsoAppearsInPanel.OpenAsync(dialogs, Universe, [("Q1", "Hal Jordan")], Person, "Dana Test");

        Assert.Equal(typeof(AlsoAppearsInPanel), dialogs.ComponentType);
        Assert.Equal(AppDialogSheetMode.PhoneAndTablet, dialogs.Options!.Sheet);
        Assert.True(dialogs.Options.CloseOnEscapeKey);
        Assert.True(dialogs.Options.BackdropClick);
        var parameters = dialogs.Parameters!.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(Universe, parameters[nameof(AlsoAppearsInPanel.UniverseQid)]);
        Assert.Equal(Person, parameters[nameof(AlsoAppearsInPanel.PersonId)]);
        Assert.Equal("Dana Test", parameters[nameof(AlsoAppearsInPanel.PersonName)]);
        Assert.Equal([("Q1", "Hal Jordan")], (IReadOnlyList<(string Qid, string Label)>)parameters[nameof(AlsoAppearsInPanel.Characters)]!);
    }

    /// <summary>A dialog context that runs the registered close guard when it is closed, as the real one does.</summary>
    private sealed class RecordingDialogContext : IAppDialogContext
    {
        private Func<Task<bool>>? _guard;

        public Guid Id { get; } = Guid.NewGuid();
        public AppDialogOptions Options { get; } = new();
        public string? PresentationClass { get; private set; }
        public void Close(AppDialogResult? result = null) => _ = CloseAsync(result);
        public void Cancel() => _ = CancelAsync();
        public async Task CloseAsync(AppDialogResult? result = null)
        {
            if (_guard is not null)
            {
                await _guard();
            }
        }

        public Task CancelAsync() => CloseAsync(AppDialogResult.Cancel());
        public Task SetOptionsAsync(AppDialogOptions options) => Task.CompletedTask;
        public Task SetPresentationClassAsync(string? value)
        {
            PresentationClass = value;
            return Task.CompletedTask;
        }

        public void SetCloseGuard(Func<Task<bool>>? guard) => _guard = guard;
        public IDisposable RegisterCloseGuard(Func<Task<bool>> guard)
        {
            _guard = guard;
            return new Registration();
        }

        private sealed class Registration : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    /// <summary>Records what the panel asked the dialog service to show and closes it at once.</summary>
    private sealed class RecordingDialogService : IAppDialogService
    {
        public Type? ComponentType { get; private set; }
        public AppDialogParameters? Parameters { get; private set; }
        public AppDialogOptions? Options { get; private set; }

        public Task<IAppDialogReference> ShowAsync<TComponent>(string? title = null, AppDialogParameters? parameters = null, AppDialogOptions? options = null)
            where TComponent : IComponent
        {
            ComponentType = typeof(TComponent);
            Parameters = parameters;
            Options = options;
            return Task.FromResult<IAppDialogReference>(new ClosedReference());
        }

        public Task<IAppDialogReference> ShowAsync<TComponent>(string? title, AppDialogOptions options)
            where TComponent : IComponent => ShowAsync<TComponent>(title, null, options);

        public Task<bool?> ShowMessageBoxAsync(string? title, string message, string yesText = "OK", string? noText = null, string? cancelText = null, AppDialogOptions? options = null) =>
            Task.FromResult<bool?>(null);

        public Task<bool?> ShowMessageBoxAsync(string? title, MarkupString message, string yesText = "OK", string? noText = null, string? cancelText = null, AppDialogOptions? options = null) =>
            Task.FromResult<bool?>(null);

        private sealed class ClosedReference : IAppDialogReference
        {
            public Guid Id { get; } = Guid.NewGuid();
            public Task<AppDialogResult?> Result => Task.FromResult<AppDialogResult?>(AppDialogResult.Cancel());
            public void Close(AppDialogResult? result = null)
            {
            }
        }
    }
}
