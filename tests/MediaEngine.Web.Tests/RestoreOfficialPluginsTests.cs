using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Restore official plugins (Plugins and Integrations screens v5 WP29, D57): a quiet row on Settings > Tools for administrators only;
/// its dialog lists each missing, damaged or edited Tuvima plugin file with "will be restored" or "will be reset" and how many already
/// match, says "All official plugins match." when nothing differs, and confirming asks the Engine to restore. No toast.
/// </summary>
public sealed class RestoreOfficialPluginsTests : AsyncBunitContext
{
    private readonly DashboardSessionAccessor _session = new();
    private PluginRestorePreviewResponse? _preview = new();
    private int _restores;

    public RestoreOfficialPluginsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPluginRestorePreviewAsync), _ => Task.FromResult(_preview));
            stub.SetHandler(nameof(IEngineApiClient.RestoreOfficialPluginsAsync), _ =>
            {
                _restores++;
                return Task.FromResult<PluginRestoreResponse?>(new PluginRestoreResponse
                {
                    RestoredCount = 1,
                    ResetCount = 1,
                    Items = [new PluginRestoreResultItemDto { FileName = "lrclib.json", Outcome = "restored" }, new PluginRestoreResultItemDto { FileName = "tmdb.json", Outcome = "reset" }],
                });
            });
        });
        Services.AddSingleton(api);
        Services.AddSingleton(_session);
    }

    [Fact]
    public void TheRow_ShowsForAnAdministrator_AndNotForAnyoneElse()
    {
        Assert.Empty(Render<RestoreOfficialPluginsRow>().FindAll("[data-testid=restore-plugins-row]"));

        SignIn(administrator: false);
        Assert.Empty(Render<RestoreOfficialPluginsRow>().FindAll("[data-testid=restore-plugins-row]"));

        SignIn(administrator: true);
        var cut = Render<RestoreOfficialPluginsRow>();
        Assert.Contains("Restore official plugins", cut.Find("[data-testid=restore-plugins-row]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDialog_ListsEachFileWithWhatWillHappen_AndTheCountThatMatch()
    {
        _preview = new PluginRestorePreviewResponse
        {
            MatchingCount = 11,
            Items =
            [
                new PluginRestoreItemDto { PluginId = "lrclib", FileName = "lrclib.json", Reason = "missing", Action = "restore" },
                new PluginRestoreItemDto { PluginId = "tmdb", FileName = "tmdb.json", Reason = "edited", Action = "reset" },
            ],
        };

        var cut = Render<RestoreOfficialPluginsDialog>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=restore-plugins-item]").Count));
        var rows = cut.FindAll("[data-testid=restore-plugins-item]");
        Assert.Contains("lrclib.json", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("will be restored", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("tmdb.json", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("will be reset", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("11 other official plugin files already match", cut.Find("[data-testid=restore-plugins-matching]").TextContent, StringComparison.Ordinal);
        Assert.Equal(0, _restores);
    }

    [Fact]
    public void Confirming_AsksTheEngineToRestore_AndSaysSoInTheDialog()
    {
        _preview = new PluginRestorePreviewResponse
        {
            Items = [new PluginRestoreItemDto { PluginId = "tmdb", FileName = "tmdb.json", Reason = "edited", Action = "reset" }],
        };
        var cut = Render<RestoreOfficialPluginsDialog>();
        cut.WaitForElement("[data-testid=restore-plugins-confirm]").Click();

        cut.WaitForAssertion(() => Assert.Contains("2 official plugin files are back", cut.Find("[data-testid=restore-plugins-done]").TextContent, StringComparison.Ordinal));
        Assert.Equal(1, _restores);
        Assert.Empty(cut.FindAll("[data-testid=restore-plugins-confirm]"));
    }

    [Fact]
    public void WhenNothingDiffers_TheDialogSaysAllOfficialPluginsMatch_AndOffersOnlyClose()
    {
        var cut = Render<RestoreOfficialPluginsDialog>();

        cut.WaitForAssertion(() => Assert.Equal("All official plugins match.", cut.Find("[data-testid=restore-plugins-empty]").TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-testid=restore-plugins-confirm]"));
        Assert.NotNull(cut.Find("[data-testid=restore-plugins-close]"));
    }

    [Fact]
    public void TheRow_OpensTheDialog()
    {
        SignIn(administrator: true);
        var host = Render<AppDialogHost>();
        var row = Render<RestoreOfficialPluginsRow>();

        row.Find("[data-testid=restore-plugins-open]").Click();

        host.WaitForElement("[data-testid=restore-plugins-dialog]");
        host.WaitForAssertion(() => Assert.NotNull(host.Find("[data-testid=restore-plugins-empty]")));
    }

    private void SignIn(bool administrator)
    {
        var profile = Guid.NewGuid();
        _session.Set("token", Guid.NewGuid(), profile, Guid.NewGuid(),
            new DashboardAuthorityResponse(Guid.NewGuid(), profile, true, true, 1, 1, administrator, administrator, null, 1, [], [], []));
    }
}
