using System.Security.Claims;
using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The book reader starts from the profile's reading defaults (device choices are merged on top by the script),
/// and saves what the person changes back to the profile.
/// </summary>
public sealed class BookReaderProfileDefaultsTests : AsyncBunitContext
{
    private static readonly Guid ActiveId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly UserPlaybackSettingsDto _profile = UserPlaybackSettingsDto.CreateDefaults(ActiveId);
    private readonly List<UserPlaybackSettingsDto> _saved = [];
    private readonly BunitJSModuleInterop _module;
    private readonly FakeAccessor _accessor;
    private TaskCompletionSource? _firstPutGate;

    public BookReaderProfileDefaultsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _profile.Reading.Theme = ReadingPreferenceValues.ThemeSepia;
        _profile.Reading.Font = ReadingPreferenceValues.FontSerif;
        _profile.Reading.FontSizePx = 24;
        _profile.Reading.LineHeight = 1.9m;
        _profile.Reading.PageWidth = ReadingPreferenceValues.PageWidthWide;
        _profile.Reading.ReadingMode = PlaybackPreferenceValues.Scroll;
        _accessor = new FakeAccessor(_profile);
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(_accessor);

        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
            {
                new(ActiveId, "Test User", "#C9922E", "Administrator", DateTimeOffset.UtcNow),
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackSettingsAsync), args =>
                Task.FromResult<UserPlaybackSettingsDto?>(Copy(_profile)));
            stub.SetHandler(nameof(IEngineApiClient.UpdatePlaybackSettingsAsync), args => PutAsync(args));
        });
        Services.AddSingleton(api);
        var profiles = new ActiveProfileSessionService(null!, api, authenticationStateProvider: new ProfileAuthentication(ActiveId));
        Services.AddSingleton(profiles);
        Services.AddSingleton(new UIOrchestratorService(api, new UniverseStateContainer(), profiles, new ConfigurationManager(), NullLogger<UIOrchestratorService>.Instance));

        // What the script answers: the profile base with this device's choice (a dark theme) on top.
        _module = JSInterop.SetupModule("./js/book-reader.js");
        _module.Mode = JSRuntimeMode.Loose;
        _module.Setup<BookReaderOpenOptions>("loadSettings", _ => true)
            .SetResult(ReaderProfileSettings.ToOpenOptions(_profile.Reading) with { Theme = "dark" });
        _module.Setup<BookReaderOpenResult>("open", _ => true)
            .SetResult(new BookReaderOpenResult { Ok = true, Title = "Dune", Layout = "reflowable" });
        _module.Setup<List<BookReaderTocItem>>("getToc").SetResult([]);
    }

    [Fact]
    public void OpeningTheReader_HandsTheProfilesReadingDefaultsToTheScriptAndOpensWithTheMergedResult()
    {
        var cut = Render<BookReader>(parameters => parameters.Add(page => page.AssetId, Guid.NewGuid()));

        cut.WaitForAssertion(() => Assert.Single(_module.Invocations["open"]));

        var load = Assert.Single(_module.Invocations["loadSettings"]);
        var baseOptions = Assert.IsType<BookReaderOpenOptions>(Assert.Single(load.Arguments));
        Assert.Equal("sepia", baseOptions.Theme);
        Assert.Equal("serif", baseOptions.Font);
        Assert.Equal(24, baseOptions.FontSize);
        Assert.Equal(1.9, baseOptions.LineHeight, 3);
        Assert.Equal("wide", baseOptions.PageWidth);
        Assert.Equal("scrolled", baseOptions.Flow);

        // The book opens with what the script merged (device wins per key), not with the bare profile base.
        var open = Assert.IsType<BookReaderOpenOptions>(_module.Invocations["open"].Single().Arguments[2]);
        Assert.Equal("dark", open.Theme);
        Assert.Equal(24, open.FontSize);
        Assert.Equal("scrolled", open.Flow);
    }

    [Fact]
    public async Task ChangingOneSetting_WritesAndSavesOnlyThatSetting()
    {
        var cut = OpenSettingsPanel();

        Choose(cut, "Narrow");

        // The device gets only the changed key, now; the profile gets nothing until the debounce has passed.
        var written = Assert.IsAssignableFrom<IDictionary<string, object>>(Assert.Single(_module.Invocations["saveSettings"]).Arguments[0]);
        Assert.Equal(["pageWidth"], written.Keys);
        Assert.Equal("narrow", written["pageWidth"]);
        Assert.Empty(_saved);

        // The save runs in the background and does not re-render the reader, so it is polled for.
        await WaitForAsync(() => _saved.Count == 1);

        var saved = Assert.Single(_saved);
        Assert.Equal("narrow", saved.Reading.PageWidth);
        // Everything else stays what the profile held (the dark theme is only this device's choice and is not pushed up).
        Assert.Equal("sepia", saved.Reading.Theme);
        Assert.Equal("serif", saved.Reading.Font);
        Assert.Equal(24, saved.Reading.FontSizePx);
        Assert.Equal(1.9m, saved.Reading.LineHeight);
        Assert.Equal(PlaybackPreferenceValues.Scroll, saved.Reading.ReadingMode);
        Assert.Equal(1, _accessor.UpdateCalls);
    }

    [Fact]
    public async Task SeveralChangesInsideTheDebounce_AreOneSaveOfOnlyTheChangedSettings()
    {
        var cut = OpenSettingsPanel();

        Choose(cut, "Narrow");
        Choose(cut, "Light");

        await WaitForAsync(() => _saved.Count >= 1);
        await Task.Delay(900);

        var saved = Assert.Single(_saved);
        Assert.Equal("narrow", saved.Reading.PageWidth);
        Assert.Equal("light", saved.Reading.Theme);
        Assert.Equal("serif", saved.Reading.Font);
        Assert.Equal(24, saved.Reading.FontSizePx);
    }

    [Fact]
    public async Task ProfileSaves_RunOneAtATimeAndANewerSaveNeverLosesToAnOlderOne()
    {
        _firstPutGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = OpenSettingsPanel();

        Choose(cut, "Narrow");
        await WaitForAsync(() => _saved.Count == 1);

        // The first write is still on its way when the next change's debounce ends: that save has to wait its turn.
        Choose(cut, "Light");
        await Task.Delay(1200);
        Assert.Single(_saved);

        _firstPutGate.SetResult();
        await WaitForAsync(() => _saved.Count == 2);

        // The second save started from what the first wrote, so it carries both changes and ends the profile's history.
        Assert.Equal("narrow", _saved[0].Reading.PageWidth);
        Assert.Equal("sepia", _saved[0].Reading.Theme);
        Assert.Equal("narrow", _saved[1].Reading.PageWidth);
        Assert.Equal("light", _saved[1].Reading.Theme);
        Assert.Equal("narrow", _profile.Reading.PageWidth);
        Assert.Equal("light", _profile.Reading.Theme);
    }

    [Fact]
    public void ResetToMyDefaults_ClearsTheDeviceChoicesAndShowsTheProfileValues()
    {
        var cut = Render<BookReader>(parameters => parameters.Add(page => page.AssetId, Guid.NewGuid()));
        cut.WaitForAssertion(() => Assert.Single(_module.Invocations["open"]));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Reading settings']")));
        cut.Find("button[aria-label='Reading settings']").Click();

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Reset to my defaults").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(_module.Invocations["clearSettings"]);
            var shown = _module.Invocations["setAppearance"].Last().Arguments[0]!;
            var theme = shown.GetType().GetProperty("theme")!.GetValue(shown);
            var size = shown.GetType().GetProperty("fontSize")!.GetValue(shown);
            Assert.Equal("sepia", theme);
            Assert.Equal(24, size);
        });
        // Resetting is not a change to save.
        Assert.Empty(_module.Invocations["saveSettings"]);
        Assert.Empty(_saved);
    }

    private async Task<UserPlaybackSettingsDto?> PutAsync(object?[]? args)
    {
        var settings = (UserPlaybackSettingsDto)args![1]!;
        var gate = _firstPutGate;
        _saved.Add(settings);
        if (gate is not null && _saved.Count == 1)
        {
            await gate.Task;
        }

        // The Engine now holds what was written, as the next read-modify-write will see it.
        _profile.Reading.Theme = settings.Reading.Theme;
        _profile.Reading.Font = settings.Reading.Font;
        _profile.Reading.FontSizePx = settings.Reading.FontSizePx;
        _profile.Reading.LineHeight = settings.Reading.LineHeight;
        _profile.Reading.PageWidth = settings.Reading.PageWidth;
        _profile.Reading.ReadingMode = settings.Reading.ReadingMode;
        return settings;
    }

    private IRenderedComponent<BookReader> OpenSettingsPanel()
    {
        var cut = Render<BookReader>(parameters => parameters.Add(page => page.AssetId, Guid.NewGuid()));
        cut.WaitForAssertion(() => Assert.Single(_module.Invocations["open"]));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Reading settings']")));
        cut.Find("button[aria-label='Reading settings']").Click();
        return cut;
    }

    private static void Choose(IRenderedComponent<BookReader> cut, string label) =>
        cut.FindAll(".app-segmented-control button").Single(button => button.TextContent.Trim() == label).Click();

    private static UserPlaybackSettingsDto Copy(UserPlaybackSettingsDto source)
    {
        var copy = UserPlaybackSettingsDto.CreateDefaults(source.ProfileId);
        copy.Reading.Theme = source.Reading.Theme;
        copy.Reading.Font = source.Reading.Font;
        copy.Reading.FontSizePx = source.Reading.FontSizePx;
        copy.Reading.LineHeight = source.Reading.LineHeight;
        copy.Reading.PageWidth = source.Reading.PageWidth;
        copy.Reading.ReadingMode = source.Reading.ReadingMode;
        return copy;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The background save did not happen in time.");
            await Task.Delay(25);
        }
    }

    private sealed class FakeAccessor(UserPlaybackSettingsDto settings) : IUserPlaybackPreferencesAccessor
    {
        public int UpdateCalls { get; private set; }
        public Guid? ActiveProfileId => settings.ProfileId;

        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) =>
            Task.FromResult<UserPlaybackSettingsDto?>(settings);

        public void UpdateCache(UserPlaybackSettingsDto next) => UpdateCalls++;

        public void Invalidate() { }
    }

    private sealed class ProfileAuthentication(Guid profileId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("tuvima:active_profile_id", profileId.ToString("D"))], "test"))));
    }
}
