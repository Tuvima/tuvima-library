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
    private readonly List<SaveBackupDestinationRequest> _added = [];
    private readonly List<string> _removed = [];
    private BackupSettingsResponse _settings = new()
    {
        KeepLast = 10,
        Destinations =
        [
            new() { Id = BackupDestinationIds.Local, Name = "This computer", StateLabel = "Working", IsConfigured = true, Enabled = true },
            new() { Id = "folder", Name = "Another folder", LeavesMachine = true, StateLabel = "Needs attention", IsConfigured = true },
            new() { Id = "webdav-1", Name = "Nextcloud", Kind = BackupDestinationKinds.WebDav, Location = "https://dav.example.com/backups", LeavesMachine = true, StateLabel = "Working", IsConfigured = true, Enabled = true, Removable = true },
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
            stub.SetHandler(nameof(IEngineApiClient.SaveBackupDestinationAsync), args =>
            {
                _added.Add((SaveBackupDestinationRequest)args![0]!);
                return Task.FromResult<BackupSettingsResponse?>(_settings);
            });
            stub.SetHandler(nameof(IEngineApiClient.RemoveBackupDestinationAsync), args =>
            {
                _removed.Add((string)args![0]!);
                return Task.FromResult(true);
            });
        }));
    }

    [Fact]
    public void TheSection_AsksForNoPassphrase()
    {
        var cut = Render<BackupDestinationsSection>();

        cut.WaitForElement("[data-testid='backup-destinations']");
        Assert.DoesNotContain("passphrase", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("input[type='password']"));
    }

    [Fact]
    public void EachDestination_IsListedWithItsState()
    {
        var cut = Render<BackupDestinationsSection>();

        cut.WaitForAssertion(() => Assert.Contains("Needs attention", cut.Find("[data-testid='destination-folder']").TextContent));
        Assert.Contains("This computer", cut.Find("[data-testid='destination-local']").TextContent);
    }

    [Fact]
    public void AnAddedDestination_ShowsItsKindAndAddress_AndCanBeRemoved()
    {
        var cut = Render<BackupDestinationsSection>();

        var row = cut.WaitForElement("[data-testid='destination-webdav-1']");
        Assert.Contains("WebDAV · https://dav.example.com/backups", row.TextContent);
        Assert.DoesNotContain("Remove", cut.Find("[data-testid='destination-folder']").TextContent);
        row.QuerySelectorAll("button").Single(button => button.TextContent.Contains("Remove")).Click();

        cut.WaitForAssertion(() => Assert.Equal(["webdav-1"], _removed));
    }

    [Fact]
    public void AddingAFolder_SendsItsNameAndPath()
    {
        var cut = Render<BackupDestinationsSection>();
        cut.WaitForElement("[data-testid='backup-destinations']");
        cut.FindAll("button").Single(button => button.TextContent.Contains("Add a destination")).Click();

        var inputs = cut.Find("[data-testid='add-destination']").QuerySelectorAll("input");
        cut.FindAll("[data-testid='add-destination'] input")[0].Input("Spare drive");
        cut.FindAll("[data-testid='add-destination'] input")[1].Input("D:\\Backups");
        cut.FindAll("button").Single(button => button.TextContent.Contains("Add destination")).Click();

        cut.WaitForAssertion(() => Assert.Single(_added));
        Assert.Equal((BackupDestinationKinds.Folder, "Spare drive", "D:\\Backups"), (_added[0].Kind, _added[0].Name, _added[0].Location));
        Assert.NotEmpty(inputs);
    }

    [Fact]
    public void AWebDestination_CannotBeAdded_UntilThePersonAgreesToSendingToItsAddress()
    {
        var cut = Render<BackupDestinationsSection>();
        cut.WaitForElement("[data-testid='backup-destinations']");
        cut.FindAll("button").Single(button => button.TextContent.Contains("Add a destination")).Click();
        cut.Find("[data-testid='add-destination'] select").Change(BackupDestinationKinds.WebDav);
        var inputs = cut.FindAll("[data-testid='add-destination'] input");
        inputs[0].Input("Nextcloud");
        inputs[1].Input("https://dav.example.com/backups");

        Assert.Contains("Send backups to dav.example.com", cut.Find("[data-testid='add-destination']").TextContent);
        Assert.True(cut.FindAll("button").Single(button => button.TextContent.Contains("Add destination")).HasAttribute("disabled"));
    }
}
