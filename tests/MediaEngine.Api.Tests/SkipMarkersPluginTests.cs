using System.Text.Json;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugin.CommercialSkip;
using MediaEngine.Plugin.FandomLore;
using MediaEngine.Plugin.MediaSegments;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Tests;

/// <summary>Intro, credits and recap are one plugin with a switch per kind of marker; the other code plugins state their declarations.</summary>
public sealed class SkipMarkersPluginTests
{
    private static readonly PluginMediaAssetContext Episode = new() { FilePath = "episode.mkv", MediaType = "TV" };

    [Fact]
    public void SkipMarkers_IsOnePluginWithThreeDetectorsAndAHealthCheck()
    {
        var plugin = new SkipMarkersPlugin();
        var capabilities = plugin.CreateCapabilities();

        Assert.Equal("tuvima.skip-markers", plugin.Manifest.Id);
        Assert.Equal(3, capabilities.OfType<IPlaybackSegmentDetector>().Count());
        Assert.Single(capabilities.OfType<IPluginHealthCheck>());
        Assert.Null(PluginCompatibility.ValidateManifest(plugin.Manifest));
    }

    [Theory]
    [InlineData(SkipMarkersPlugin.DetectIntroKey, typeof(IntroSkipSegmentDetector))]
    [InlineData(SkipMarkersPlugin.DetectCreditsKey, typeof(CreditsSegmentDetector))]
    [InlineData(SkipMarkersPlugin.DetectRecapKey, typeof(RecapSegmentDetector))]
    public void SwitchingOneKindOfMarkerOff_StopsOnlyThatDetector(string switchKey, Type detectorType)
    {
        var detectors = new SkipMarkersPlugin().CreateCapabilities().OfType<IPlaybackSegmentDetector>().ToList();

        var allOn = new Context(new Dictionary<string, JsonElement>());
        Assert.All(detectors, detector => Assert.True(detector.CanAnalyze(Episode, allOn)));

        var oneOff = new Context(new Dictionary<string, JsonElement> { [switchKey] = JsonSerializer.SerializeToElement(false) });
        foreach (var detector in detectors)
        {
            Assert.Equal(detector.GetType() != detectorType, detector.CanAnalyze(Episode, oneOff));
        }
    }

    [Fact]
    public void SkipMarkers_AreForEveryone_AndCommercialSkipIsHeavyAndForEveryoneToo()
    {
        var skip = new SkipMarkersPlugin().Manifest.ResolvedDeclaration;
        var commercial = new CommercialSkipPlugin().Manifest.ResolvedDeclaration;

        Assert.Equal(PluginAudience.Everyone, skip.Audience);
        Assert.Equal(PluginWeightClass.Medium, skip.WeightClass);
        Assert.Equal(PluginAudience.Everyone, commercial.Audience);
        Assert.Equal(PluginWeightClass.Heavy, commercial.WeightClass);
    }

    [Fact]
    public void FandomLore_IsForEveryone_ButNeverUsedForVaultItems()
    {
        var lore = new FandomLorePlugin().Manifest.ResolvedDeclaration;

        Assert.Equal(PluginAudience.Everyone, lore.Audience);
        Assert.Equal(PluginVaultUse.Never, lore.VaultUse);
    }

    [Fact]
    public void EveryCodePlugin_HasAValidDeclaration_AndASettingsForm()
    {
        ITuvimaPlugin[] plugins = [new SkipMarkersPlugin(), new CommercialSkipPlugin(), new FandomLorePlugin()];

        Assert.All(plugins, plugin =>
        {
            Assert.NotNull(plugin.Manifest.Declaration);
            Assert.Empty(PluginDeclarationRules.Validate(plugin.Manifest.Declaration));
            Assert.False(string.IsNullOrWhiteSpace(plugin.Manifest.Declaration!.Changelog));
            Assert.NotNull(plugin.Manifest.SettingsSchema);
        });
    }

    private sealed class Context(IReadOnlyDictionary<string, JsonElement> settings) : IPluginExecutionContext
    {
        public string PluginId => "tuvima.skip-markers";

        public IReadOnlyDictionary<string, JsonElement> Settings { get; } = settings;

        public IPluginMediaAccess Media { get; } = new Everything();

        public IPluginHttpClient Http => throw new NotSupportedException();

        public IPluginStorage Storage => throw new NotSupportedException();

        public IPluginToolRuntime Tools => throw new NotSupportedException();

        public IPluginAiClient Ai => throw new NotSupportedException();

        public IPluginSecrets Secrets => throw new NotSupportedException();

        public IPluginRecordStore Records => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Everything : IPluginMediaAccess
        {
            public bool Exists(PluginMediaAssetContext asset) => true;
        }
    }
}
