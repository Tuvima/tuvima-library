using Bunit;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class HomeSettingsPageTests : AsyncBunitContext
{
    private DisplayHeroSettingsDto? _remote = new(true, 5, true, true, true, false);
    private readonly List<DisplayHeroSettingsDto> _saved = [];
    private bool _saveFails;

    public HomeSettingsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetDisplayHeroSettingsAsync), _ => Task.FromResult(_remote));
            stub.SetHandler(nameof(IEngineApiClient.SetDisplayHeroSettingsAsync), args =>
            {
                _saved.Add((DisplayHeroSettingsDto)args![0]!);
                return Task.FromResult(!_saveFails);
            });
        }));
        Render<AppPopoverHost>();
    }

    [Fact]
    public void TheTypeSwitches_ReflectTheSavedSettings()
    {
        _remote = new(true, 3, true, false, true, true);

        var cut = Render<HomeSettingsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Find("input[aria-label='Movies and shows']").HasAttribute("checked"));
            Assert.False(cut.Find("input[aria-label='Books and comics']").HasAttribute("checked"));
            Assert.False(cut.Find("input[aria-label='Include new items']").HasAttribute("checked"));
        });
    }

    [Fact]
    public void SwitchingOffBooks_SavesTheWholeSettingWithOnlyThatChanged()
    {
        var cut = Render<HomeSettingsPage>();
        cut.WaitForAssertion(() => cut.Find("input[aria-label='Books and comics']"));

        cut.Find("input[aria-label='Books and comics']").Change(false);

        cut.WaitForAssertion(() => Assert.Equal(new DisplayHeroSettingsDto(true, 5, true, false, true, false), Assert.Single(_saved)));
    }

    [Fact]
    public void TurningTheBannerOff_HidesTheOtherRows()
    {
        var cut = Render<HomeSettingsPage>();
        cut.WaitForAssertion(() => cut.Find("input[aria-label='Show the top banner']"));

        cut.Find("input[aria-label='Show the top banner']").Change(false);

        cut.WaitForAssertion(() =>
        {
            Assert.False(Assert.Single(_saved).Enabled);
            Assert.Empty(cut.FindAll("input[aria-label='Movies and shows']"));
        });
    }

    [Fact]
    public void AChangeTheEngineRefuses_IsPutBack()
    {
        _saveFails = true;
        var cut = Render<HomeSettingsPage>();
        cut.WaitForAssertion(() => cut.Find("input[aria-label='Books and comics']"));

        cut.Find("input[aria-label='Books and comics']").Change(false);

        cut.WaitForAssertion(() =>
        {
            Assert.Single(_saved);
            Assert.True(cut.Find("input[aria-label='Books and comics']").HasAttribute("checked"));
        });
    }

    [Fact]
    public void SettingsThatCannotBeRead_ShowAnErrorWithRetry()
    {
        _remote = null;

        var cut = Render<HomeSettingsPage>();

        cut.WaitForAssertion(() => Assert.Contains("Home preferences are unavailable", cut.Markup));
    }
}
