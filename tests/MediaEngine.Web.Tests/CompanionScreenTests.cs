using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Companion;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The phone companion screen (spec 4A.3) for a film, an episode, a book chapter and an audiobook chapter: the title and place,
/// the "Up to here" / "Everything" switch, "Who's here", "Where you've seen them", "Previously" and the honest empty answers.
/// The screen only shows what the Engine sent; nothing here is invented.
/// </summary>
public sealed class CompanionScreenTests : AsyncBunitContext
{
    public CompanionScreenTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddSingleton(EngineApiClientStub.CreateDefault());
    }

    private IRenderedComponent<CompanionScreen> RenderScreen(
        CompanionContextDto context,
        string mode = CompanionModes.Manual,
        string? device = null,
        Action<CompanionCharacterDto>? onOpen = null,
        Action<string>? onSpoiler = null) =>
        Render<CompanionScreen>(parameters => parameters
            .Add(screen => screen.Context, context)
            .Add(screen => screen.Mode, mode)
            .Add(screen => screen.DeviceLabel, device)
            .Add(screen => screen.OnOpenAlsoAppearsIn, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<CompanionCharacterDto>(this, character => onOpen?.Invoke(character)))
            .Add(screen => screen.OnSpoilerLevelChanged, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<string>(this, level => onSpoiler?.Invoke(level))));

    private static CompanionCharacterDto Character(string name, string visibility = CompanionCharacterVisibility.Shown, int others = 0, string? actor = null) =>
        new()
        {
            Qid = $"Q-{name.ToLowerInvariant()}",
            Name = name,
            UniverseQid = "Q4242",
            Source = "Wikidata",
            Visibility = visibility,
            OtherWorkCount = others,
            PerformedBy = actor,
        };

    private static CompanionContextDto Context(string kind, string title, string position) =>
        new() { WorkId = Guid.NewGuid(), Kind = kind, Title = title, PositionLabel = position, Synopsis = "A synopsis." };

    [Fact]
    public void AFilm_ShowsItsPlace_TheSpoilerSwitch_AndSaysPlainlyWhenThereAreNoCharacterDetails()
    {
        var context = Context(CompanionKinds.Film, "The Fellowship of the Ring", "Film") with
        {
            Cast = ["Elijah Wood", "Ian McKellen"],
            Universe = new CompanionUniverseLinkDto { Qid = "Q4242", Name = "Middle-earth" },
        };

        var cut = RenderScreen(context, CompanionModes.Follow, "Living Room TV");

        Assert.Equal("The Fellowship of the Ring", cut.Find(".companion__title").TextContent.Trim());
        Assert.Equal("Film", cut.Find("[data-part='position']").TextContent.Trim());
        Assert.Contains("Following Living Room TV", cut.Find("[data-part='mode']").TextContent);
        Assert.Equal("No character details for this one yet.", cut.Find("[data-note='no-character-details']").TextContent.Trim());
        // What the Library does have is still shown, and nothing is made up to fill the gap.
        Assert.Contains("Elijah Wood, Ian McKellen", cut.Find("[data-part='cast']").TextContent);
        Assert.Empty(cut.FindAll("[data-section='previously']"));
        Assert.Empty(cut.FindAll("[data-section='where-seen']"));
        Assert.Contains(cut.FindAll("a"), link => link.GetAttribute("href") == "/universe/Q4242");
    }

    [Fact]
    public void AnEpisode_ListsWhosHereUpToTheEpisode_WhereElseYouHaveSeenThem_AndPreviously()
    {
        var context = Context(CompanionKinds.Episode, "Second", "Season 1 · Episode 2") with
        {
            ShowTitle = "Lanterns",
            WhosHere = new CompanionWhosHereDto
            {
                HasCharacterDetails = true,
                HeldBackCount = 2,
                Characters =
                [
                    Character("Alice", others: 3, actor: "Dana Test"),
                    Character("Cleo", CompanionCharacterVisibility.NoPlotDetails),
                ],
            },
            Previously = new CompanionPreviouslyDto { WorkId = Guid.NewGuid(), Label = "Season 1 · Episode 1", Title = "Pilot", Synopsis = "It began." },
        };

        var cut = RenderScreen(context);

        Assert.Equal("Lanterns", cut.Find(".companion__eyebrow").TextContent.Trim());
        var characters = cut.FindAll("[data-section='whos-here'] .companion__character");
        Assert.Equal(2, characters.Count);
        Assert.Contains("Alice", characters[0].TextContent);
        Assert.Contains("Dana Test · Wikidata", characters[0].TextContent);
        Assert.Contains("No plot details", characters[1].TextContent);
        Assert.Equal("2 more characters are held back to avoid spoilers.", cut.Find("[data-note='held-back']").TextContent.Trim());
        var seen = Assert.Single(cut.FindAll("[data-section='where-seen'] button"));
        Assert.Contains("Alice", seen.TextContent);
        Assert.Contains("Also in 3 other works you own", seen.TextContent);
        var previously = cut.Find("[data-section='previously']");
        Assert.Contains("Season 1 · Episode 1 · Pilot", previously.TextContent);
        Assert.Contains("It began.", previously.TextContent);
    }

    [Fact]
    public void TappingAWhereYouHaveSeenThemRow_RaisesTheCharacterSoThePageCanOpenAlsoAppearsIn()
    {
        var opened = new List<CompanionCharacterDto>();
        var context = Context(CompanionKinds.Episode, "Second", "Season 1 · Episode 2") with
        {
            WhosHere = new CompanionWhosHereDto { HasCharacterDetails = true, Characters = [Character("Alice", others: 1)] },
        };
        var cut = RenderScreen(context, onOpen: opened.Add);

        cut.Find("[data-section='where-seen'] button").Click();

        var character = Assert.Single(opened);
        Assert.Equal("Q-alice", character.Qid);
        Assert.Equal("Q4242", character.UniverseQid);
    }

    [Fact]
    public void ABookChapter_ShowsTheChapter_AndExplainsWhyCharactersCarryNoPlotDetails()
    {
        var context = Context(CompanionKinds.Book, "The Gate", "Chapter 12: The Gate") with
        {
            ChapterIndex = 11,
            ChapterTitle = "The Gate",
            WhosHere = new CompanionWhosHereDto
            {
                HasCharacterDetails = true,
                Characters = [Character("Dana", CompanionCharacterVisibility.NoPlotDetails)],
            },
        };

        var cut = RenderScreen(context);

        Assert.Equal("Chapter 12: The Gate", cut.Find("[data-part='position']").TextContent.Trim());
        Assert.Contains("Place set by you", cut.Find("[data-part='mode']").TextContent);
        Assert.Contains("No source says who appears in which chapter", cut.Find("[data-note='no-chapter-level']").TextContent);
        Assert.Empty(cut.FindAll("[data-section='where-seen']"));
    }

    [Fact]
    public void AnAudiobookChapter_FollowsTheDevice_AndNeedsNoPreviouslySection()
    {
        var context = Context(CompanionKinds.Audiobook, "The Gate (read aloud)", "Chapter 2: The Road") with
        {
            WhosHere = new CompanionWhosHereDto { HasCharacterDetails = true, Characters = [] },
        };

        var cut = RenderScreen(context, CompanionModes.Follow, "Kitchen Speaker");

        Assert.Contains("Following Kitchen Speaker", cut.Find("[data-part='mode']").TextContent);
        Assert.Equal("Chapter 2: The Road", cut.Find("[data-part='position']").TextContent.Trim());
        // Details exist for the Work but none are reachable yet: the screen says they arrive as you go, not "none exist".
        Assert.Equal("Characters show up here as you reach them.", cut.Find("[data-note='nothing-yet']").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-section='previously']"));
    }

    [Fact]
    public void ThereIsNoInventedSynopsis_WhenTheLibraryHoldsNone()
    {
        var context = Context(CompanionKinds.Episode, "Second", "Season 1 · Episode 2") with { Synopsis = null };

        var cut = RenderScreen(context);

        Assert.Equal("No synopsis for this one yet.", cut.Find("[data-section='synopsis'] .companion__empty").TextContent.Trim());
    }

    [Theory]
    [InlineData("up_to_progress", "Up to here", "everything")]
    [InlineData("everything", "Everything", "up_to_progress")]
    public void TheSpoilerSwitch_ShowsTheProfilesLevel_AndRaisesTheOtherOneWhenTapped(string current, string pressed, string other)
    {
        var raised = new List<string>();
        var context = Context(CompanionKinds.Film, "Film", "Film") with { SpoilerLevel = current };
        var cut = RenderScreen(context, onSpoiler: raised.Add);

        var buttons = cut.FindAll("[data-part='spoiler'] button");
        Assert.Equal(["Up to here", "Everything"], buttons.Select(button => button.TextContent.Trim()));
        Assert.Equal(pressed, buttons.Single(button => button.GetAttribute("aria-pressed") == "true").TextContent.Trim());

        buttons.Single(button => button.GetAttribute("aria-pressed") == "false").Click();

        Assert.Equal([other], raised);
    }

    [Fact]
    public void ThePositionPicker_ListsEpisodes_MarksTheCurrentOne_AndRaisesTheChoice()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var picked = new List<CompanionPickerChoiceDto>();
        var picker = new CompanionPickerDto
        {
            Kind = CompanionPickerKinds.Episode,
            Choices =
            [
                new CompanionPickerChoiceDto { WorkId = first, Label = "Season 1 · Episode 1", Title = "Pilot" },
                new CompanionPickerChoiceDto { WorkId = second, Label = "Season 1 · Episode 2", Title = "Second" },
            ],
        };

        var cut = Render<CompanionPositionPicker>(parameters => parameters
            .Add(component => component.Picker, picker)
            .Add(component => component.Kind, CompanionKinds.Episode)
            .Add(component => component.CurrentWorkId, second)
            .Add(component => component.OnPick, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<CompanionPickerChoiceDto>(this, picked.Add)));

        var buttons = cut.FindAll("button");
        Assert.Equal(2, buttons.Count);
        Assert.Equal("true", buttons[1].GetAttribute("aria-pressed"));
        Assert.Equal("false", buttons[0].GetAttribute("aria-pressed"));

        buttons[0].Click();

        Assert.Equal(first, Assert.Single(picked).WorkId);
    }

    [Fact]
    public void ABookWithoutAChapterList_GetsAChapterNumberStepper_ThatNeverGoesBelowChapterOne()
    {
        var work = Guid.NewGuid();
        var picked = new List<CompanionPickerChoiceDto>();

        var cut = Render<CompanionPositionPicker>(parameters => parameters
            .Add(component => component.Picker, null)
            .Add(component => component.Kind, CompanionKinds.Book)
            .Add(component => component.CurrentWorkId, work)
            .Add(component => component.CurrentChapterIndex, 0)
            .Add(component => component.OnPick, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<CompanionPickerChoiceDto>(this, picked.Add)));

        Assert.Equal("Chapter 1", cut.Find(".companion-picker__number").TextContent.Trim());
        Assert.True(cut.Find("button[aria-label='Previous chapter']").HasAttribute("disabled"));

        cut.Find("button[aria-label='Next chapter']").Click();

        var next = Assert.Single(picked);
        Assert.Equal(work, next.WorkId);
        Assert.Equal(1, next.ChapterIndex);
    }

    [Fact]
    public void AFilmHasNothingToPick_AndTheScreenSaysSo()
    {
        var cut = Render<CompanionPositionPicker>(parameters => parameters
            .Add(component => component.Picker, null)
            .Add(component => component.Kind, CompanionKinds.Film)
            .Add(component => component.CurrentWorkId, Guid.NewGuid()));

        Assert.Empty(cut.FindAll("button"));
        Assert.Contains("nothing to pick", cut.Markup, StringComparison.Ordinal);
    }
}
