using Bunit;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The "Data store version: step" line left Back up and restore for System (settings-alignment D10): the backups page no longer shows
/// it, and no longer even asks for it. System shows it as a fact on the This server card (see SystemSummaryPageTests).
/// </summary>
public sealed class BackupRecoveryPanelVersionLineTests : AsyncBunitContext
{
    private readonly EngineApiClientStub _stub;

    public BackupRecoveryPanelVersionLineTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetBackupsAsync),
                _ => Task.FromResult<IReadOnlyList<BackupArchiveDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetDataStoreVersionAsync),
                _ => Task.FromResult<DataStoreVersionDto?>(new DataStoreVersionDto(3, "add_note", DateTimeOffset.UtcNow)));
        });
        _stub = (EngineApiClientStub)(object)api;
        Services.AddSingleton(api);
        Services.AddSingleton(new DeviceContextService(api));
    }

    [Fact]
    public void BackUpAndRestore_NoLongerShowsTheDataStoreVersionLine_OrAsksForIt()
    {
        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.Contains("Backup status", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='data-store-version']"));
        Assert.DoesNotContain("Data store version", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetDataStoreVersionAsync), _stub.Calls);
    }
}
