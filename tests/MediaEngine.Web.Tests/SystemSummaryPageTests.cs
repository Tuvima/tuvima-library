using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The System main page: three cards (this server, processor and memory, storage) read from the Engine, in plain
/// words, repeated while the page is open and stopped when it closes.
/// </summary>
public sealed class SystemSummaryPageTests : AsyncBunitContext
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private SystemResourcesDto? _resources = Sample();
    private NetworkRuntimeStatusDto? _network = new()
    {
        PreferredLocalAddress = "192.168.1.20",
        ExternalAddress = "https://tuvima.example.com",
    };
    private int _resourceCalls;
    private int _networkCalls;

    public SystemSummaryPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetSystemResourcesAsync), _ =>
            {
                Interlocked.Increment(ref _resourceCalls);
                return Task.FromResult(_resources);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetNetworkRuntimeStatusAsync), _ =>
            {
                Interlocked.Increment(ref _networkCalls);
                return Task.FromResult(_network);
            });
        });
        Services.AddSingleton(api);
    }

    [Fact]
    public void Page_ShowsThisServerProcessorAndMemoryAndStorageCards()
    {
        var cut = Render<SystemSummaryPage>();

        cut.WaitForAssertion(() =>
        {
            var server = cut.Find("[data-testid='system-summary-server']").TextContent;
            Assert.Contains("1.4.2", server, StringComparison.Ordinal);
            Assert.Contains("Running for", server, StringComparison.Ordinal);
            Assert.Contains("192.168.1.20", server, StringComparison.Ordinal);
            Assert.Contains("https://tuvima.example.com", server, StringComparison.Ordinal);

            var load = cut.Find("[data-testid='system-summary-load']").TextContent;
            Assert.Contains("The Library uses 12.5% of the processor", load, StringComparison.Ordinal);
            Assert.Contains("1.5 GB of 16 GB memory", load, StringComparison.Ordinal);

            Assert.Contains("This server", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Processor and memory", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Storage", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Storage_ShowsUsedAndFreeSpacePerDrive_WhichThingsLiveThere_AndTheDataStoreSize()
    {
        _resources = Sample(drives:
        [
            new SystemDriveDto { Root = @"D:\", Label = "Media", TotalBytes = 4000 * Gigabyte, FreeBytes = 1000 * Gigabyte, HoldsLibrary = true },
            new SystemDriveDto { Root = @"C:\", Label = "", TotalBytes = 500 * Gigabyte, FreeBytes = 250 * Gigabyte, HoldsDataStore = true },
        ], dataStoreBytes: 3 * Gigabyte / 2);

        var cut = Render<SystemSummaryPage>();

        cut.WaitForAssertion(() =>
        {
            var drives = cut.FindAll("[data-testid='system-summary-drive']");
            Assert.Equal(2, drives.Count);
            Assert.Contains("Media (D:\\)", drives[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("2.9 TB used, 1000 GB free of 3.9 TB", drives[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("Holds your library", drives[0].TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("data store", drives[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("C:\\", drives[1].TextContent, StringComparison.Ordinal);
            Assert.Contains("Holds the data store", drives[1].TextContent, StringComparison.Ordinal);
            Assert.Equal("75", drives[0].QuerySelector("[role='progressbar']")!.GetAttribute("aria-valuenow"));
            Assert.Contains("Data store size1.5 GB", cut.Find("[data-testid='system-summary-data-store']").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void DriveHoldingBoth_SaysSo()
    {
        _resources = Sample(drives:
        [
            new SystemDriveDto { Root = @"C:\", TotalBytes = 100 * Gigabyte, FreeBytes = 40 * Gigabyte, HoldsLibrary = true, HoldsDataStore = true },
        ]);

        var cut = Render<SystemSummaryPage>();

        cut.WaitForAssertion(() =>
            Assert.Contains("Holds your library and the data store", cut.Find("[data-testid='system-summary-drive']").TextContent, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, "less than a minute")]
    [InlineData(59, "less than a minute")]
    [InlineData(60, "1 minute")]
    [InlineData(7 * 60 + 5, "7 minutes")]
    [InlineData(3600 + 12 * 60, "1 hour, 12 minutes")]
    [InlineData(5 * 3600 + 60, "5 hours, 1 minute")]
    [InlineData(86_400 + 3600, "1 day, 1 hour")]
    [InlineData(3 * 86_400 + 4 * 3600 + 59 * 60, "3 days, 4 hours")]
    public void Uptime_IsWrittenInPlainWords(long seconds, string expected)
    {
        _resources = Sample(uptimeSeconds: seconds);

        var cut = Render<SystemSummaryPage>();

        cut.WaitForAssertion(() =>
            Assert.Contains($"Running for{expected}", cut.Find("[data-testid='system-summary-server']").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutNetworkStatus_TheAddressesSayTheyAreUnavailableOrNotSetUp()
    {
        _network = null;

        var cut = Render<SystemSummaryPage>();

        cut.WaitForAssertion(() =>
        {
            var server = cut.Find("[data-testid='system-summary-server']").TextContent;
            Assert.Contains("Address at homeUnavailable", server, StringComparison.Ordinal);
            Assert.Contains("Address outside your homeNot set up", server, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void WhileLoading_ShowsAReadingState_ThenTheCards()
    {
        var pending = new TaskCompletionSource<SystemResourcesDto?>();
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetSystemResourcesAsync), _ => pending.Task));
        Services.AddSingleton(api);

        var cut = Render<SystemSummaryPage>();

        Assert.Contains("Reading this server", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='system-summary-server']"));
        pending.SetResult(Sample());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='system-summary-server']")));
    }

    [Fact]
    public void WhenTheEngineCannotBeRead_SaysSo_AndRecoversOnTheNextReading()
    {
        _resources = null;

        var cut = Render<SystemSummaryPage>(parameters => parameters.Add(page => page.RefreshInterval, TimeSpan.FromMilliseconds(50)));

        cut.WaitForAssertion(() => Assert.Contains("This server could not be read", cut.Markup, StringComparison.Ordinal));
        _resources = Sample();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='system-summary-server']")));
    }

    [Fact]
    public void WhenALaterReadingFails_ThePreviousReadingStaysWithANote()
    {
        var cut = Render<SystemSummaryPage>(parameters => parameters.Add(page => page.RefreshInterval, TimeSpan.FromMilliseconds(50)));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='system-summary-server']")));

        _resources = null;

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='system-summary-stale']")));
        Assert.Single(cut.FindAll("[data-testid='system-summary-server']"));
    }

    [Fact]
    public void Page_ReadsAgainOnItsInterval_WhileOpen_AndStopsWhenDisposed()
    {
        var cut = Render<SystemSummaryPage>(parameters => parameters.Add(page => page.RefreshInterval, TimeSpan.FromMilliseconds(50)));
        cut.WaitForAssertion(() => Assert.True(Volatile.Read(ref _resourceCalls) >= 3 && Volatile.Read(ref _networkCalls) >= 3));

        cut.Instance.Dispose();
        var settled = Volatile.Read(ref _resourceCalls);
        Thread.Sleep(300);

        Assert.Equal(settled, Volatile.Read(ref _resourceCalls));
    }

    [Fact]
    public void Page_DefaultsToTakingANewReadingEveryTenSeconds()
    {
        var cut = Render<SystemSummaryPage>();

        Assert.Equal(TimeSpan.FromSeconds(10), cut.Instance.RefreshInterval);
    }

    [Fact]
    public void Page_UsesPlainWordsAndNoDatabaseJargon()
    {
        var cut = Render<SystemSummaryPage>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='system-summary-server']")));
        Assert.DoesNotContain("database", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localStorage", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    private static SystemResourcesDto Sample(
        long uptimeSeconds = 90_000,
        IEnumerable<SystemDriveDto>? drives = null,
        long? dataStoreBytes = 52_428_800) => new()
        {
            EngineVersion = "1.4.2",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-uptimeSeconds),
            UptimeSeconds = uptimeSeconds,
            ProcessorPercent = 12.5,
            MemoryUsedMb = 1536,
            MemoryTotalMb = 16_384,
            Drives = drives?.ToList() ??
            [
                new SystemDriveDto { Root = @"C:\", Label = "System", TotalBytes = 500 * Gigabyte, FreeBytes = 200 * Gigabyte, HoldsLibrary = true, HoldsDataStore = true },
            ],
            DataStoreSizeBytes = dataStoreBytes,
        };
}
