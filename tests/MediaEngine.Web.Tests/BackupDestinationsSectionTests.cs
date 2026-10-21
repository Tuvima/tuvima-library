using Bunit;
using MediaEngine.Contracts.Backups;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Backup destinations (Developer tools spec WP25) as the person sees them in Backup and recovery.</summary>
public sealed class BackupDestinationsSectionTests : AsyncBunitContext
{
    private readonly List<UpdateBackupSettingsRequest> _saved = [];
    private readonly List<SetBackupPassphraseRequest> _passphrases = [];
    private BackupSettingsResponse _settings = new()
    {
        KeepLast = 10,
        Destinations =
        [
            new() { Id = BackupDestinationIds.Local, Name = "This computer", StateLabel = "Working", IsConfigured = true, Enabled = true },
            new() { Id = "folder", Name = "Another folder", LeavesMachine = true, StateLabel = "Needs attention", IsConfigured = true },
        ],
    };

    public BackupDestinationsSectionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton<IAppToastService>(new AppToastService());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetBackupSettingsAsync), _ => Task.FromResult<BackupSettingsResponse?>(_settings));
            stub.SetHandler(nameof(IEngineApiClient.UpdateBackupSettingsAsync), args =>
            {
                _saved.Add((UpdateBackupSettingsRequest)args![0]!);
                return Task.FromResult<BackupSettingsResponse?>(_settings);
            });
            stub.SetHandler(nameof(IEngineApiClient.SetBackupPassphraseAsync), args =>
            {
                _passphrases.Add((SetBackupPassphraseRequest)args![0]!);
                _settings.PassphraseSet = true;
                return Task.FromResult<BackupSettingsResponse?>(_settings);
            });
        }));
    }

    [Fact]
    public void ThePassphraseWarning_SaysLosingItMeansCopiesCannotBeRestored()
    {
        var cut = Render<BackupDestinationsSection>();

        cut.WaitForAssertion(() => Assert.Contains("cannot be restored", cut.Markup));
        Assert.Contains("never store", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EachDestination_IsListedWithItsState()
    {
        var cut = Render<BackupDestinationsSection>();

        cut.WaitForAssertion(() => Assert.Contains("Needs attention", cut.Find("[data-testid='destination-folder']").TextContent));
        Assert.Contains("This computer", cut.Find("[data-testid='destination-local']").TextContent);
    }

    [Fact]
    public void SettingAPassphrase_SendsItOnce_AndClearsTheField()
    {
        var cut = Render<BackupDestinationsSection>();
        cut.WaitForAssertion(() => Assert.Contains("Set passphrase", cut.Markup));

        cut.Find("input[type='password']").Input("a long enough passphrase");
        cut.FindAll("button").Single(button => button.TextContent.Contains("Set passphrase")).Click();

        cut.WaitForAssertion(() => Assert.Single(_passphrases));
        Assert.Equal("a long enough passphrase", _passphrases[0].Passphrase);
        cut.WaitForAssertion(() => Assert.DoesNotContain("a long enough passphrase", cut.Markup));
    }
}
