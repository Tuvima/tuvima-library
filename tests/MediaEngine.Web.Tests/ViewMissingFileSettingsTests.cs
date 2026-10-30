using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// The Missing files panel on a View kind's File handling tab (safer deleting in View, WP4): one switch and a number of days, saved at
// once with an inline "Saved" (no toast), 0 meaning "never remove".
public sealed class ViewMissingFileSettingsTests : AsyncBunitContext
{
    private ViewMissingFileSettingsDto _settings = new(90, 90);
    private readonly List<int> _saved = [];

    public ViewMissingFileSettingsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetViewMissingFileSettingsAsync), _ => Task.FromResult<ViewMissingFileSettingsDto?>(_settings));
            stub.SetHandler(nameof(IEngineApiClient.UpdateViewMissingFileSettingsAsync), args =>
            {
                var days = (int)args![0]!;
                _saved.Add(days);
                _settings = new ViewMissingFileSettingsDto(days, 90);
                return Task.FromResult<ViewMissingFileSettingsDto?>(_settings);
            });
        }));
    }

    private IRenderedComponent<ViewFileHandlingTab> Open(bool readOnly = false) =>
        Render<ViewFileHandlingTab>(parameters => parameters
            .Add(x => x.Kind, ViewContentKinds.Documents)
            .Add(x => x.ReadOnly, readOnly));

    [Fact]
    public void MissingFilesPanel_ShowsTheSavedDaysWhileRemovalIsOn()
    {
        var cut = Open();

        cut.WaitForAssertion(() => Assert.Contains("Missing files", cut.Markup, StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Contains("Days to wait", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("never deletes anything because a file went missing", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFilesPanel_WhenRemovalIsOff_HidesTheDaysAndKeepsRecordsForever()
    {
        _settings = new ViewMissingFileSettingsDto(0, 90);

        var cut = Open();

        cut.WaitForAssertion(() => Assert.Contains("Missing files", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Days to wait", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFilesPanel_ReadOnly_DoesNotSave()
    {
        var cut = Open(readOnly: true);

        cut.WaitForAssertion(() => Assert.Contains("Days to wait", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(_saved);
    }
}
