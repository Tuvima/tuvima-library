using Bunit;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The read-only "Data store version" line above the backups: a step and date, "setting up" on a brand-new
/// install, nothing while loading, and hidden when the Engine could not be asked.
/// </summary>
public sealed class BackupRecoveryPanelVersionLineTests : AsyncBunitContext
{
    private const string Line = "[data-testid='data-store-version']";

    private Task<DataStoreVersionDto?> _version = Task.FromResult<DataStoreVersionDto?>(null);

    public BackupRecoveryPanelVersionLineTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetBackupsAsync),
                _ => Task.FromResult<IReadOnlyList<BackupArchiveDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetDataStoreVersionAsync), _ => _version);
        });
        Services.AddSingleton(api);
        Services.AddSingleton(new DeviceContextService(api));
    }

    [Fact]
    public void RecordedStep_ShowsTheStepNumberAndTheUpgradeDate()
    {
        _version = Task.FromResult<DataStoreVersionDto?>(
            new DataStoreVersionDto(3, "add_note", new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.Zero)));

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() =>
            Assert.Equal("Data store version: step 3, upgraded on 2026-10-18", cut.Find(Line).TextContent));
    }

    [Fact]
    public void StepZero_SaysTheDataStoreIsSettingUp()
    {
        _version = Task.FromResult<DataStoreVersionDto?>(new DataStoreVersionDto(0, string.Empty, null));

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() =>
            Assert.Equal("Data store version: setting up", cut.Find(Line).TextContent));
    }

    [Fact]
    public void WhileLoading_NoVersionLineIsShown()
    {
        var pending = new TaskCompletionSource<DataStoreVersionDto?>();
        _version = pending.Task;

        var cut = Render<BackupRecoveryPanel>();

        Assert.Empty(cut.FindAll(Line));
        pending.SetResult(new DataStoreVersionDto(1, "baseline", DateTimeOffset.UtcNow));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(Line)));
    }

    [Fact]
    public void WhenTheEngineCannotBeAsked_TheVersionLineIsHidden_AndTheRestOfThePanelStillRenders()
    {
        _version = Task.FromResult<DataStoreVersionDto?>(null);

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.Contains("Backup status", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(Line));
    }
}
