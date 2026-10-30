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

    [Theory]
    [InlineData("Read <script>alert(1)</script> now", null)]
    [InlineData("Look at https://example.com/poster.jpg", null)]
    [InlineData("See www.example.com", null)]
    [InlineData("A fine slide", "https://example.com/poster.jpg")]
    [InlineData("A fine slide", "data:image/png;base64,AAAA")]
    public async Task ASlideWithMarkupOrAnAddress_IsRefused_WithTheReasonLogged(string text, string? icon)
    {
        var logs = new List<string>();
        var service = new PluginDisplayService(_repository, _catalog, _kit.Clock, new ListLogger(logs));

        await Assert.ThrowsAsync<ArgumentException>(() => service.PublishAsync(
            PluginId, new PluginDisplayContribution(PluginDisplaySlot.HomeHeroCandidate, "w", "a", text, Icon: icon), default));

        Assert.Equal(0, await _repository.CountAsync(PluginId));
        Assert.Contains(logs, line => line.Contains(PluginId) && line.Contains("refused"));
    }

    [Fact]
    public async Task PlainWordsAndAnIconName_AreAccepted()
    {
        await _service.PublishAsync(
            PluginId, new PluginDisplayContribution(PluginDisplaySlot.HomeHeroCandidate, "w", "a", "Because you finished Dune: Part One", Icon: "auto_awesome"), default);

        Assert.Equal(1, await _repository.CountAsync(PluginId));
    }

    [Fact]
    public async Task Nominations_AreListedAcrossItems_AndHideWhenThePluginIsOff()
    {
        await _service.PublishAsync(PluginId, new PluginDisplayContribution(PluginDisplaySlot.HomeHeroCandidate, "w1", "a", "Weak", Strength: 10), default);
        await _service.PublishAsync(PluginId, new PluginDisplayContribution(PluginDisplaySlot.HomeHeroCandidate, "w2", "a", "Strong", Strength: 90), default);
        await _service.PublishAsync(PluginId, Line("a", "w3", "Not a slide"), default);

        Assert.Equal(["Strong", "Weak"], (await _service.ListSlotAsync(PluginDisplaySlot.HomeHeroCandidate, 10, default)).Select(item => item.Text));

        _catalog.SetEnabled(PluginId, false);
        Assert.Empty(await _service.ListSlotAsync(PluginDisplaySlot.HomeHeroCandidate, 10, default));
    }

    [Fact]
    public async Task HeroPluginSlides_SkipAProfileSwitchOff_AndAnItemTheGateRefuses()
    {
        var profile = Guid.NewGuid();
        using (var connection = _kit.Database.CreateConnection())
        {
            Dapper.SqlMapper.Execute(connection,
                "INSERT INTO profiles(id, display_name, role, created_at) VALUES (@profile, 'Alex', 'StandardUser', @at);",
                new { profile, at = DateTimeOffset.UtcNow.ToString("O") });
        }
        var allowed = Guid.NewGuid();
        var refused = Guid.NewGuid();
        await _service.PublishAsync(PluginId, new PluginDisplayContribution(PluginDisplaySlot.HomeHeroCandidate, allowed.ToString("D"), "a", "Allowed", Strength: 50), default);
        await _service.PublishAsync(PluginId, new PluginDisplayContribution(PluginDisplaySlot.HomeHeroCandidate, refused.ToString("D"), "a", "Refused", Strength: 40), default);
        var settings = new ProfileHeroSettingsRepository(_kit.Database);
        var slides = new MediaEngine.Api.Services.Display.HeroPluginSlides(_service, _catalog, new RefusingGate(refused), settings);

        var nominations = await slides.NominationsAsync(profile, PluginAudience.Adults, default);

        var one = Assert.Single(nominations);
        Assert.Equal(allowed, one.ItemId);
        Assert.Equal("Display Test", one.PluginName);

        await settings.SetPluginSlidesAsync(profile, PluginId, enabled: false);
        Assert.Empty(await slides.NominationsAsync(profile, PluginAudience.Adults, default));
        Assert.False(Assert.Single(await slides.PluginsAsync(profile, default)).Enabled);

        await settings.SetPluginSlidesAsync(profile, PluginId, enabled: true);
        Assert.Single(await slides.NominationsAsync(profile, PluginAudience.Adults, default));
    }

    private sealed class RefusingGate(Guid refusedItem) : MediaEngine.Domain.Contracts.IPluginAccessGate
    {
        public Task<MediaEngine.Domain.Contracts.PluginAccessOutcome> CheckAsync(MediaEngine.Domain.Contracts.PluginAccessQuery query, CancellationToken ct = default) =>
            Task.FromResult(query.EntityId == refusedItem
                ? MediaEngine.Domain.Contracts.PluginAccessOutcome.NotInAudience
                : MediaEngine.Domain.Contracts.PluginAccessOutcome.Allowed);
    }

    private sealed class ListLogger(List<string> lines) : Microsoft.Extensions.Logging.ILogger<PluginDisplayService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception));
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
