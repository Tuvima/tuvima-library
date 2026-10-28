using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Display slots (Plugin architecture spec C21): a plugin supplies small plain data, the Library reads it without waiting on the plugin,
/// and nothing shows while the plugin is off or lacks the permission.
/// </summary>
public sealed class PluginDisplayTests : IDisposable
{
    private const string PluginId = "tuvima.test.display";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-display", Guid.NewGuid().ToString("N"));
    private readonly ApprovalTestKit _kit = new();
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly PluginCatalog _catalog;
    private readonly PluginDisplayRepository _repository;
    private readonly PluginDisplayService _service;
    private readonly DisplayPlugin _plugin = new();

    public PluginDisplayTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(libraryRoot);
        _loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
        _catalog = new PluginCatalog([_plugin], new PluginSettingsService(_loader), _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        _repository = new PluginDisplayRepository(_kit.Database);
        _service = new PluginDisplayService(_repository, _catalog, _kit.Clock);
        _catalog.SetEnabled(PluginId, true);
    }

    public void Dispose()
    {
        _kit.Dispose();
        _loader.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is left for the OS to clean up.
        }
    }

    [Fact]
    public async Task APublishedLine_IsListedForItsSlotAndItem_StrongestFirst()
    {
        await _service.PublishAsync(PluginId, Line("a", "work-1", "Weak", strength: 10), default);
        await _service.PublishAsync(PluginId, Line("b", "work-1", "Strong", strength: 80), default);
        await _service.PublishAsync(PluginId, Line("c", "work-2", "Other item"), default);

        var items = await _service.ListAsync(PluginDisplaySlot.ItemHero, "work-1", default);

        Assert.Equal(["Strong", "Weak"], items.Select(item => item.Text));
        Assert.All(items, item => Assert.Equal("Display Test", item.PluginName));
        Assert.Empty(await _service.ListAsync(PluginDisplaySlot.TileBadge, "work-1", default));
    }

    [Fact]
    public async Task PublishingTheSameKeyAgain_ReplacesIt_AndWithdrawingRemovesIt()
    {
        await _service.PublishAsync(PluginId, Line("a", "work-1", "First"), default);
        await _service.PublishAsync(PluginId, Line("a", "work-1", "Second"), default);

        Assert.Equal("Second", Assert.Single(await _service.ListAsync(PluginDisplaySlot.ItemHero, "work-1", default)).Text);

        await _service.WithdrawAsync(PluginId, PluginDisplaySlot.ItemHero, "work-1", "a", default);

        Assert.Empty(await _service.ListAsync(PluginDisplaySlot.ItemHero, "work-1", default));
    }

    [Fact]
    public async Task APluginThatIsOff_ShowsNothing_AndShowsAgainWhenSwitchedOn()
    {
        await _service.PublishAsync(PluginId, Line("a", "work-1", "Hello"), default);

        _catalog.SetEnabled(PluginId, false);
        Assert.Empty(await _service.ListAsync(PluginDisplaySlot.ItemHero, "work-1", default));

        _catalog.SetEnabled(PluginId, true);
        Assert.Single(await _service.ListAsync(PluginDisplaySlot.ItemHero, "work-1", default));
    }

    [Fact]
    public async Task Publishing_RefusesTooLongEmptyOrOutOfRangeData()
    {
        await Assert.ThrowsAsync<PluginLimitExceededException>(() => _service.PublishAsync(PluginId, Line("a", "w", new string('x', PluginDisplayService.MaximumTextLength + 1)), default));
        await Assert.ThrowsAsync<PluginLimitExceededException>(() => _service.PublishAsync(PluginId, Line(new string('k', PluginDisplayService.MaximumKeyLength + 1), "w", "ok"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.PublishAsync(PluginId, Line("a", "w", "  "), default));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.PublishAsync(PluginId, Line("a", null, "About no item"), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.PublishAsync(PluginId, Line("a", "w", "ok", strength: 101), default));
        Assert.Equal(0, await _repository.CountAsync(PluginId));
    }

    [Fact]
    public async Task SettingsLines_NeedNoItem()
    {
        await _service.PublishAsync(PluginId, new PluginDisplayContribution(PluginDisplaySlot.SettingsOverview, null, "status", "Working"), default);

        var item = Assert.Single(await _service.ListAsync(PluginDisplaySlot.SettingsOverview, null, default));
        Assert.Null(item.SubjectId);
    }

    [Fact]
    public async Task ThePluginKeepsAtMostTheCap_LeastRecentlyUpdatedFirstToGo()
    {
        var capped = new PluginDisplayRepository(_kit.Database);
        for (var index = 0; index < 5; index++)
        {
            _kit.Clock.Advance(TimeSpan.FromSeconds(1));
            await capped.UpsertAsync(
                new MediaEngine.Storage.Contracts.PluginDisplayRow(PluginId, "ItemHero", "w", $"k{index}", null, "t", null, 0, false, _kit.Clock.GetUtcNow()), 3);
        }

        Assert.Equal(3, await capped.CountAsync(PluginId));
        Assert.Equal(["k4", "k3", "k2"], (await capped.ListAsync("ItemHero", "w")).Select(row => row.Key));
    }

    [Fact]
    public async Task APluginWithoutThePermission_CannotPublish()
    {
        var display = new BoundPluginDisplay(PluginId, new PluginPermissionGate(_catalog), _service);
        _plugin.GrantPermission = false;
        _catalog.Reload();
        _catalog.SetEnabled(PluginId, true);

        await Assert.ThrowsAnyAsync<Exception>(() => display.PublishAsync(Line("a", "w", "Hello")));
        Assert.Equal(0, await _repository.CountAsync(PluginId));
    }

    [Fact]
    public async Task APluginWithThePermission_PublishesThroughItsContext()
    {
        var display = new BoundPluginDisplay(PluginId, new PluginPermissionGate(_catalog), _service);

        await display.PublishAsync(Line("a", "w", "Hello", fromAi: true));

        var item = Assert.Single(await _service.ListAsync(PluginDisplaySlot.ItemHero, "w", default));
        Assert.True(item.FromLocalAi);
        Assert.Equal(PluginId, item.PluginId);
    }

    private static PluginDisplayContribution Line(string key, string? subject, string text, int strength = 0, bool fromAi = false) =>
        new(PluginDisplaySlot.ItemHero, subject, key, text, Strength: strength, FromLocalAi: fromAi);

    private sealed class DisplayPlugin : ITuvimaPlugin
    {
        public bool GrantPermission { get; set; } = true;

        public string Kind => "display";

        public PluginManifest Manifest => new()
        {
            Id = PluginId,
            Name = "Display Test",
            Version = "1.0.0",
            Permissions = GrantPermission ? [PluginPermissionIds.DisplaySlots] : [],
        };

        public IReadOnlyList<IPluginCapability> CreateCapabilities() => [];
    }
}
