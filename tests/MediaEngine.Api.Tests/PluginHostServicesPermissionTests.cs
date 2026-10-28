using MediaEngine.Api.Services.Approvals;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Plugin.CommercialSkip;
using MediaEngine.Plugin.FandomLore;
using MediaEngine.Plugin.MediaSegments;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The five host-service permissions: they are known to the Library, shown in plain words at approval, change the approval
/// fingerprint when a plugin gains one, and are not declared by the plugins that ship today. Capabilities that break the
/// host-service rules are a plain load error.
/// </summary>
public sealed class PluginHostServicesPermissionTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    private HostServicesHarness NewHost(params HostServicesTestPlugin[] plugins)
    {
        var host = new HostServicesHarness(null, plugins);
        _disposables.Add(host);
        return host;
    }

    [Theory]
    [InlineData("plugin-records", "Keeps its own records in the Library's data store")]
    [InlineData("scheduled-work", "Runs its own recurring jobs, listed in Scheduled tasks")]
    [InlineData("library-events", "Is told when things happen in the Library (items added, ingestion finished, metadata changes)")]
    [InlineData("watch-activity", "Is told what people watch, read and listen to")]
    [InlineData("background-work", "Runs continuously in the background while switched on")]
    [InlineData("plugin-pages", "Adds pages and menu entries to the Library")]
    public void TheApprovalStatement_SaysEachNewPermissionInPlainWords(string permission, string expectedLine)
    {
        Assert.Contains(permission, PluginPermissionIds.All);
        var manifest = new PluginManifest { Id = "test.words", Name = "Words", Permissions = [permission] };

        var statement = ApprovalStatementBuilder.ForPlugin(manifest, PluginTrustLevel.Private);

        Assert.Contains(expectedLine, statement.CanDo);
        Assert.Contains(expectedLine, ApprovalStatementBuilder.PluginAllowedTo(manifest));
        Assert.Equal(expectedLine, ApprovalStatementBuilder.PermissionText(permission));
    }

    [Theory]
    [InlineData("plugin-records")]
    [InlineData("scheduled-work")]
    [InlineData("library-events")]
    [InlineData("watch-activity")]
    [InlineData("background-work")]
    [InlineData("plugin-pages")]
    public void AGainedHostPermission_ChangesTheFingerprint_SoTheAdministratorIsAskedAgain(string permission)
    {
        var before = new PluginManifest { Id = "test.words", Name = "Words", Permissions = [PluginPermissionIds.MediaRead] };
        var after = before with { Permissions = [PluginPermissionIds.MediaRead, permission] };

        Assert.NotEqual(
            ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(before, PluginTrustLevel.Private)),
            ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(after, PluginTrustLevel.Private)));
    }

    [Fact]
    public void ThePluginsThatShipToday_DeclareNoneOfTheHostServicePermissions()
    {
        var hostServices = new[]
        {
            PluginPermissionIds.PluginRecords, PluginPermissionIds.ScheduledWork, PluginPermissionIds.LibraryEvents,
            PluginPermissionIds.WatchActivity, PluginPermissionIds.BackgroundWork,
        };
        var shipped = new ITuvimaPlugin[] { new CommercialSkipPlugin(), new FandomLorePlugin(), new SkipMarkersPlugin() };

        Assert.All(shipped, plugin => Assert.Empty(plugin.Manifest.Permissions.Intersect(hostServices)));
    }

    [Fact]
    public void AJobKeyThatIsNotAPlainName_IsALoadError()
    {
        var plugin = new HostServicesTestPlugin();
        plugin.AddJob("Not A Key!");

        Assert.Equal(
            "A plugin job needs a key of 1 to 64 lower-case letters, digits, hyphens or underscores.",
            NewHost(plugin).Catalog.Get(plugin.Manifest.Id)!.LoadError);
    }

    [Fact]
    public void TwoJobsWithTheSameKey_AreALoadError()
    {
        var plugin = new HostServicesTestPlugin();
        plugin.AddJob("refresh");
        plugin.AddJob("refresh");

        Assert.Equal("Plugin job 'refresh' is listed more than once.", NewHost(plugin).Catalog.Get(plugin.Manifest.Id)!.LoadError);
    }

    [Fact]
    public void ACronScheduleThatCannotBeRead_IsALoadError()
    {
        var plugin = new HostServicesTestPlugin();
        plugin.AddJob("nightly", PluginSchedule.OnCron("not a cron"));

        var error = NewHost(plugin).Catalog.Get(plugin.Manifest.Id)!.LoadError;

        Assert.Equal("Plugin job 'nightly' has a schedule that cannot be used: the cron expression could not be read.", error);
    }

    [Fact]
    public void ASubscriptionWithNoEventTypes_AndTwoAlwaysOnServices_AreLoadErrors()
    {
        var empty = new HostServicesTestPlugin("test.empty");
        empty.Subscribe();
        var two = new HostServicesTestPlugin("test.two");
        two.RunInBackground((_, _, token) => Task.Delay(Timeout.Infinite, token));
        two.RunInBackground((_, _, token) => Task.Delay(Timeout.Infinite, token));
        var host = NewHost(empty, two);

        Assert.Equal("A plugin event subscription needs at least one event type.", host.Catalog.Get("test.empty")!.LoadError);
        Assert.Equal("A plugin can run only one always-on service.", host.Catalog.Get("test.two")!.LoadError);
    }

    [Fact]
    public void AValidPluginWithAllFourHostCapabilities_LoadsWithoutAnError()
    {
        var plugin = new HostServicesTestPlugin();
        plugin.AddJob("refresh");
        plugin.AddJob("nightly", PluginSchedule.OnCron("0 3 * * *"));
        plugin.Subscribe("library.item_added", "ingestion.completed", "playback.started");
        plugin.RunInBackground((_, _, token) => Task.Delay(Timeout.Infinite, token));

        Assert.Null(NewHost(plugin).Catalog.Get(plugin.Manifest.Id)!.LoadError);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }
}
