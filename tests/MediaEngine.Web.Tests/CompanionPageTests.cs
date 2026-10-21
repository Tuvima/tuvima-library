using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Web.Components.Companion;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Services.Companion;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The phone companion's behaviour end to end on the Dashboard side: following the device the person chose, holding a place by
/// hand (the "Companion" item in a title's menu), the spoiler switch writing the same Spoilers preference as Settings, the
/// Home "Now playing" card, and the guarantee that only the active profile's own devices are ever asked about. The Engine is a
/// hand-written stub; the Engine's own rules (own profile only, spoiler judgement) are covered by its tests.
/// </summary>
public sealed class CompanionPageTests : AsyncBunitContext
{
    private static readonly Guid Profile = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Episode1 = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Episode2 = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private const string Phone = "test-phone";

    private readonly List<string> _calls = [];
    private readonly List<(Guid Work, int? Chapter, double? Seconds, Guid? Profile)> _contextRequests = [];
    private readonly List<CompanionManualPositionRequestDto> _manual = [];
    private readonly List<CompanionFollowRequestDto> _follows = [];
    private readonly List<(Guid Profile, string Level)> _spoilerSaves = [];
    private readonly List<Guid?> _deviceRequestsProfiles = [];
    private CompanionSessionDto _session = new() { ProfileId = Profile, CompanionDeviceId = Phone, Mode = CompanionModes.None };
    private IReadOnlyList<CompanionLiveDeviceDto> _devices = [];
    private Func<Guid, CompanionContextDto?> _context = id => Context(id, "Pilot");
    private bool _spoilerSaveFails;

    public CompanionPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        var accessor = new ActiveProfileAccessor();
        accessor.SetProfile(Profile);
        Services.AddSingleton<IActiveProfileAccessor>(accessor);
        Services.AddSingleton<ICompanionDeviceIdStore>(new FixedDeviceId());
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetCompanionDevicesAsync), args =>
            {
                _deviceRequestsProfiles.Add((Guid?)args![0]);
                return Task.FromResult<CompanionDeviceListDto?>(new CompanionDeviceListDto { ProfileId = Profile, Devices = _devices });
            });
            stub.SetHandler(nameof(IEngineApiClient.GetCompanionSessionAsync), args =>
            {
                _calls.Add("session:" + args![0]);
                return Task.FromResult<CompanionSessionDto?>(_session);
            });
            stub.SetHandler(nameof(IEngineApiClient.FollowCompanionDeviceAsync), args =>
            {
                var request = (CompanionFollowRequestDto)args![0]!;
                _follows.Add(request);
                var device = _devices.First(candidate => candidate.DeviceId == request.DeviceId);
                _session = new CompanionSessionDto
                {
                    ProfileId = Profile,
                    CompanionDeviceId = request.CompanionDeviceId ?? Phone,
                    Mode = CompanionModes.Follow,
                    FollowedDeviceId = device.DeviceId,
                    Followed = device,
                    IsFollowedDeviceLive = true,
                };
                return Task.FromResult<CompanionSessionDto?>(_session);
            });
            stub.SetHandler(nameof(IEngineApiClient.SetCompanionManualPositionAsync), args =>
            {
                var request = (CompanionManualPositionRequestDto)args![0]!;
                _manual.Add(request);
                _session = new CompanionSessionDto
                {
                    ProfileId = Profile,
                    CompanionDeviceId = request.CompanionDeviceId ?? Phone,
                    Mode = CompanionModes.Manual,
                    Manual = new CompanionManualPositionDto { WorkId = request.WorkId, ChapterIndex = request.ChapterIndex },
                };
                return Task.FromResult<CompanionSessionDto?>(_session);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetCompanionContextAsync), args =>
            {
                var work = (Guid)args![0]!;
                _contextRequests.Add((work, (int?)args[1], (double?)args[2], (Guid?)args[3]));
                return Task.FromResult(_context(work));
            });
            stub.SetHandler(nameof(IEngineApiClient.SaveSpoilerLevelAsync), args =>
            {
                var level = (string)args![1]!;
                _spoilerSaves.Add(((Guid)args[0]!, level));
                return Task.FromResult<ProfileSpoilerPreferenceDto?>(
                    _spoilerSaveFails ? null : new ProfileSpoilerPreferenceDto { ProfileId = (Guid)args[0]!, SpoilerLevel = level });
            });
        });
        Services.AddSingleton(api);
        Services.AddScoped<CompanionState>();
    }

    private static CompanionContextDto Context(Guid work, string title, string level = "up_to_progress") =>
        new()
        {
            WorkId = work,
            Kind = CompanionKinds.Episode,
            Title = title,
            ShowTitle = "Lanterns",
            PositionLabel = work == Episode1 ? "Season 1 · Episode 1" : "Season 1 · Episode 2",
            SpoilerLevel = level,
            Synopsis = "A synopsis.",
            WhosHere = new CompanionWhosHereDto
            {
                HasCharacterDetails = true,
                Characters = [new CompanionCharacterDto { Qid = "Q-alice", Name = "Alice", Source = "Wikidata", UniverseQid = "Q4242" }],
            },
            Picker = new CompanionPickerDto
            {
                Kind = CompanionPickerKinds.Episode,
                Choices =
                [
                    new CompanionPickerChoiceDto { WorkId = Episode1, Label = "Season 1 · Episode 1", Title = "Pilot" },
                    new CompanionPickerChoiceDto { WorkId = Episode2, Label = "Season 1 · Episode 2", Title = "Second" },
                ],
            },
        };

    private static CompanionLiveDeviceDto Live(string id, string? name, Guid work, string title, double seconds = 120) =>
        new()
        {
            DeviceId = id,
            DeviceName = name,
            Client = "roku",
            PlaybackState = PlayerPlaybackStates.Playing,
            PositionSeconds = seconds,
            WorkId = work,
            AssetId = Guid.NewGuid(),
            Item = new PlayerQueueItemDto { WorkId = work, Title = title, MediaType = "TV", SeasonNumber = "1", EpisodeNumber = "2" },
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private IRenderedComponent<CompanionPage> RenderPage(Guid? workId = null) =>
        Render<CompanionPage>(parameters => parameters.Add(page => page.WorkId, workId));

    [Fact]
    public void FromATitlesMenu_TheCompanionHoldsThatTitle_OpensThePicker_AndReadsTheContextAtThatPlace()
    {
        var cut = RenderPage(Episode2);

        cut.WaitForAssertion(() => Assert.Contains("Season 1 · Episode 2", cut.Markup, StringComparison.Ordinal));
        var held = Assert.Single(_manual);
        Assert.Equal(Episode2, held.WorkId);
        Assert.Equal(Phone, held.CompanionDeviceId);
        Assert.Null(held.ChapterIndex);
        Assert.Contains((Episode2, (int?)null, (double?)null, (Guid?)Profile), _contextRequests);
        // Manual is first-class: "Place set by you", with the episode list already open to change it.
        Assert.Contains("Place set by you", cut.Find("[data-part='mode']").TextContent);
        Assert.Equal(2, cut.FindAll("[data-section='picker'] button").Count);
    }

    [Fact]
    public void PickingAnotherEpisode_HoldsIt_AndReloadsTheContextThere()
    {
        var cut = RenderPage(Episode2);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-section='picker'] button")));

        cut.FindAll("[data-section='picker'] button")[0].Click();

        cut.WaitForAssertion(() => Assert.Contains("Season 1 · Episode 1", cut.Find("[data-part='position']").TextContent, StringComparison.Ordinal));
        Assert.Equal([Episode2, Episode1], _manual.Select(request => request.WorkId));
        Assert.Equal(Episode1, _contextRequests[^1].Work);
    }

    [Fact]
    public void AnAudiobookChapterPick_HoldsTheChapterIndex()
    {
        var book = Guid.NewGuid();
        _context = id => new CompanionContextDto
        {
            WorkId = id,
            Kind = CompanionKinds.Audiobook,
            Title = "The Gate",
            PositionLabel = "No chapter chosen",
            Picker = new CompanionPickerDto
            {
                Kind = CompanionPickerKinds.Chapter,
                Choices =
                [
                    new CompanionPickerChoiceDto { WorkId = id, ChapterIndex = 0, Label = "Chapter 1", Title = "Opening" },
                    new CompanionPickerChoiceDto { WorkId = id, ChapterIndex = 1, Label = "Chapter 2", Title = "The Gate" },
                ],
            },
        };
        var cut = RenderPage(book);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-section='picker'] button").Count));

        cut.FindAll("[data-section='picker'] button")[1].Click();

        cut.WaitForAssertion(() => Assert.Equal(2, _manual.Count));
        Assert.Equal(1, _manual[^1].ChapterIndex);
        Assert.Equal(book, _manual[^1].WorkId);
        Assert.Equal((book, (int?)1, (double?)null, (Guid?)Profile), _contextRequests[^1]);
    }

    [Fact]
    public void FollowingADevice_ReadsTheContextAtTheDevicesPositionAndNamesTheDevice()
    {
        var device = Live("living-room", "Living Room TV", Episode2, "Second", seconds: 321);
        _devices = [device];
        _session = new CompanionSessionDto
        {
            ProfileId = Profile,
            CompanionDeviceId = Phone,
            Mode = CompanionModes.Follow,
            FollowedDeviceId = device.DeviceId,
            Followed = device,
            IsFollowedDeviceLive = true,
        };

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Contains("Following Living Room TV", cut.Find("[data-part='mode']").TextContent));
        Assert.Empty(_manual);
        Assert.Equal((Episode2, (int?)null, (double?)321, (Guid?)Profile), _contextRequests[0]);
        // A followed device is not edited by hand, but the person can stop following.
        Assert.Contains("Stop following", cut.Markup, StringComparison.Ordinal);
        Assert.Contains($"session:{Phone}", _calls);
    }

    [Fact]
    public void WithNothingPlayingAndNothingChosen_TheCompanionSaysSo_AndTellsHowToPickByHand()
    {
        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Contains("Nothing is playing", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("choose Companion from its ... menu", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(_contextRequests);
    }

    [Fact]
    public void WithSeveralDevicesPlaying_ThePersonChoosesOne_AndFollowsIt()
    {
        _devices = [Live("kitchen", "Kitchen", Episode1, "Pilot"), Live("living-room", "Living Room TV", Episode2, "Second")];
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-section='devices'] button").Count));

        cut.FindAll("[data-section='devices'] button")[1].Click();

        cut.WaitForAssertion(() => Assert.Contains("Following Living Room TV", cut.Find("[data-part='mode']").TextContent));
        var follow = Assert.Single(_follows);
        Assert.Equal("living-room", follow.DeviceId);
        Assert.Equal(Phone, follow.CompanionDeviceId);
        Assert.Equal(Profile, follow.ProfileId);
    }

    [Fact]
    public void TheSpoilerSwitch_SavesTheSameSpoilersPreferenceAsSettings_ThenReadsTheContextAgain()
    {
        var level = "up_to_progress";
        _context = id => Context(id, "Pilot", level);
        var cut = RenderPage(Episode1);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-part='spoiler'] button")));
        var readsBefore = _contextRequests.Count;

        level = "everything";
        cut.FindAll("[data-part='spoiler'] button").Single(button => button.TextContent.Trim() == "Everything").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "Everything",
            cut.FindAll("[data-part='spoiler'] button").Single(button => button.GetAttribute("aria-pressed") == "true").TextContent.Trim()));
        // The ordinary profile setting (ProfileId, "everything"): no second setting of the companion's own.
        Assert.Equal((Profile, "everything"), Assert.Single(_spoilerSaves));
        Assert.True(_contextRequests.Count > readsBefore);
    }

    [Fact]
    public void WhenTheSpoilerSettingCannotBeSaved_TheSwitchKeepsItsLevel()
    {
        _spoilerSaveFails = true;
        var cut = RenderPage(Episode1);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-part='spoiler'] button")));

        cut.FindAll("[data-part='spoiler'] button").Single(button => button.TextContent.Trim() == "Everything").Click();

        cut.WaitForAssertion(() => Assert.Single(_spoilerSaves));
        Assert.Equal(
            "Up to here",
            cut.FindAll("[data-part='spoiler'] button").Single(button => button.GetAttribute("aria-pressed") == "true").TextContent.Trim());
    }

    [Fact]
    public void ATitleTheProfileCannotSee_IsNotDescribed()
    {
        _context = _ => null;

        var cut = RenderPage(Guid.NewGuid());

        cut.WaitForAssertion(() => Assert.Contains("This title isn't available.", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheActiveProfilesDevicesAreEverAskedAbout()
    {
        _devices = [Live("kitchen", "Kitchen", Episode1, "Pilot")];

        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.NotEmpty(_deviceRequestsProfiles));

        Assert.All(_deviceRequestsProfiles, requested => Assert.Equal(Profile, requested));
        Assert.All(_contextRequests, request => Assert.Equal(Profile, request.Profile));
    }

    [Fact]
    public void TheHomeCard_ShowsWhatThisProfileIsPlaying_AndItsCompanionButtonFollowsItAndOpensTheCompanion()
    {
        _devices = [Live("living-room", "Living Room TV", Episode2, "Lanterns")];
        var cut = Render<CompanionNowPlayingCard>();

        cut.WaitForAssertion(() => Assert.Contains("Living Room TV", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("Now playing", cut.Find(".companion-now__eyebrow").TextContent.Trim());
        Assert.Equal("Lanterns · S1E2", cut.Find(".companion-now__title").TextContent.Trim());
        Assert.Equal("on Living Room TV", cut.Find(".companion-now__where").TextContent.Trim());

        cut.Find(".companion-now button").Click();

        cut.WaitForAssertion(() => Assert.Single(_follows));
        Assert.Equal("living-room", _follows[0].DeviceId);
        Assert.EndsWith("/companion", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHomeCard_IsNotDrawnWhileNothingPlays()
    {
        var cut = Render<CompanionNowPlayingCard>();

        cut.WaitForAssertion(() => Assert.NotEmpty(_deviceRequestsProfiles));
        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    [Fact]
    public void WithSeveralDevicesPlaying_TheHomeCardMentionsTheOthers_AndOpensTheCompanionToChoose()
    {
        _devices = [Live("kitchen", "Kitchen", Episode1, "Pilot"), Live("living-room", "Living Room TV", Episode2, "Second")];
        var cut = Render<CompanionNowPlayingCard>();
        cut.WaitForAssertion(() => Assert.Contains("and 1 more", cut.Markup, StringComparison.Ordinal));

        cut.Find(".companion-now button").Click();

        cut.WaitForAssertion(() => Assert.EndsWith("/companion", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal));
        Assert.Empty(_follows);
    }

    [Fact]
    public void AFollowedDevicesPlace_ReadsTheChapterFromThePositionOnlyForTheEngine()
    {
        var device = Live("kitchen", "Kitchen", Episode1, "Pilot", seconds: 700);

        var place = CompanionPlace.From(new CompanionSessionDto
        {
            Mode = CompanionModes.Follow,
            FollowedDeviceId = "kitchen",
            Followed = device,
            IsFollowedDeviceLive = true,
        });

        Assert.NotNull(place);
        Assert.Equal(Episode1, place.Value.WorkId);
        Assert.Null(place.Value.ChapterIndex);
        Assert.Equal(700, place.Value.PositionSeconds);
        Assert.Null(CompanionPlace.From(new CompanionSessionDto { Mode = CompanionModes.Follow, FollowedDeviceId = "gone", IsFollowedDeviceLive = false }));
        Assert.Null(CompanionPlace.From(new CompanionSessionDto { Mode = CompanionModes.None }));
        Assert.Equal("Roku", CompanionPlace.DeviceLabelOf(new CompanionLiveDeviceDto { Client = "roku" }));
    }

    private sealed class FixedDeviceId : ICompanionDeviceIdStore
    {
        public ValueTask<string> GetAsync() => ValueTask.FromResult(Phone);
    }
}
