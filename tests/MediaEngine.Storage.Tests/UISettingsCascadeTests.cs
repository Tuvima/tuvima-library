using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Tests for the three-tier UI settings cascade (Global → Device → Profile).
/// </summary>
public class UISettingsCascadeTests
{
    // ════════════════════════════════════════════════════════════════════════
    //  Global-only resolution (no device, no profile)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_GlobalOnly_UsesDefaults()
    {
        var loader = new StubConfigLoader();
        var resolver = new UISettingsCascadeResolver(loader);

        var result = resolver.Resolve("web");

        Assert.Equal("web", result.DeviceClass);
        Assert.True(result.DarkMode);
        Assert.Equal("#8852FC", result.AccentColor);
        Assert.Equal(12, result.BorderRadius);
        Assert.Equal("pa-4", result.ContentPadding);
        Assert.True(result.Features.CommandPalette);
        Assert.True(result.Features.ViewToggle);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Device overrides
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_DeviceOverrides_AppliedOnTopOfGlobal()
    {
        var loader = new StubConfigLoader
        {
            DeviceProfiles =
            {
                ["mobile"] = new UIDeviceProfile
                {
                    DeviceClass = "mobile",
                    BorderRadius = 16,
                    ContentPadding = "pa-2",
                    Features = new UIFeatureFlags { ViewToggle = false },
                },
            },
        };
        var resolver = new UISettingsCascadeResolver(loader);

        var result = resolver.Resolve("mobile");

        Assert.Equal("mobile", result.DeviceClass);
        Assert.Equal(16, result.BorderRadius);
        Assert.Equal("pa-2", result.ContentPadding);
        Assert.False(result.Features.ViewToggle);
        // Global defaults preserved where not overridden
        Assert.True(result.DarkMode);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Device constraints — features disabled
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_DeviceConstraints_DisableFeatures()
    {
        var loader = new StubConfigLoader
        {
            DeviceProfiles =
            {
                ["tv"] = new UIDeviceProfile
                {
                    DeviceClass = "television",
                    Constraints = new UIDeviceConstraints
                    {
                        FeaturesDisabled = ["command_palette", "view_toggle", "server_settings"],
                    },
                },
            },
        };
        var resolver = new UISettingsCascadeResolver(loader);

        var result = resolver.Resolve("tv");

        Assert.False(result.Features.CommandPalette);
        Assert.False(result.Features.ViewToggle);
        Assert.False(result.Features.ServerSettings);
        // Other features remain enabled
        Assert.True(result.Features.SearchButton);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Device constraints — force dark mode
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_ForceDarkMode_OverridesDeviceAndProfile()
    {
        var loader = new StubConfigLoader
        {
            DeviceProfiles =
            {
                ["auto"] = new UIDeviceProfile
                {
                    DeviceClass = "automotive",
                    DarkMode = false, // device says light
                    Constraints = new UIDeviceConstraints { ForceDarkMode = true },
                },
            },
            ProfileSettings =
            {
                ["user1"] = new UIProfileSettings
                {
                    ProfileId = "user1",
                    DarkMode = false, // profile wants light
                },
            },
        };
        var resolver = new UISettingsCascadeResolver(loader);

        var result = resolver.Resolve("auto", "user1");

        Assert.True(result.DarkMode); // constraint wins
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Profile overrides
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_ProfileOverrides_AppliedOnTopOfDeviceAndGlobal()
    {
        var loader = new StubConfigLoader
        {
            ProfileSettings =
            {
                ["user1"] = new UIProfileSettings
                {
                    ProfileId = "user1",
                    AccentColor = "#FF0000",
                    BorderRadius = 8,
                },
            },
        };
        var resolver = new UISettingsCascadeResolver(loader);

        var result = resolver.Resolve("web", "user1");

        Assert.Equal("#FF0000", result.AccentColor);
        Assert.Equal(8, result.BorderRadius);
        // Global defaults where profile doesn't override
        Assert.True(result.DarkMode);
        Assert.Equal("pa-4", result.ContentPadding);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Profile cannot override force_dark_mode constraint
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_ProfileCannotOverrideForceDarkMode()
    {
        var loader = new StubConfigLoader
        {
            DeviceProfiles =
            {
                ["auto"] = new UIDeviceProfile
                {
                    Constraints = new UIDeviceConstraints { ForceDarkMode = true },
                },
            },
            ProfileSettings =
            {
                ["user1"] = new UIProfileSettings { DarkMode = false },
            },
        };
        var resolver = new UISettingsCascadeResolver(loader);

        var result = resolver.Resolve("auto", "user1");

        Assert.True(result.DarkMode);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Convenience accessors
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void IsFeatureDisabled_ReturnsTrueForDisabledFeature()
    {
        var settings = new ResolvedUISettings
        {
            Constraints = new UIDeviceConstraints
            {
                FeaturesDisabled = ["command_palette"],
            },
        };

        Assert.True(settings.IsFeatureDisabled("command_palette"));
        Assert.False(settings.IsFeatureDisabled("search_button"));
    }

    [Fact]
    public void IsPageDisabled_ReturnsTrueForDisabledPage()
    {
        var settings = new ResolvedUISettings
        {
            Constraints = new UIDeviceConstraints
            {
                PagesDisabled = ["server_settings"],
            },
        };

        Assert.True(settings.IsPageDisabled("server_settings"));
        Assert.False(settings.IsPageDisabled("preferences"));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Missing configs don't crash
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_MissingDevice_UsesGlobalDefaults()
    {
        var loader = new StubConfigLoader();
        var resolver = new UISettingsCascadeResolver(loader);

        var result = resolver.Resolve("nonexistent_device");

        Assert.Equal("nonexistent_device", result.DeviceClass);
        Assert.True(result.DarkMode);
    }

    [Fact]
    public void Resolve_MissingProfile_UsesDeviceAndGlobal()
    {
        var loader = new StubConfigLoader();
        var resolver = new UISettingsCascadeResolver(loader);

        var result = resolver.Resolve("web", "nonexistent_profile");

        Assert.True(result.DarkMode);
        Assert.Equal("#8852FC", result.AccentColor);
    }
}

// ── Stub config loader ─────────────────────────────────────────────────

file sealed class StubConfigLoader : IConfigurationLoader
{
    public Dictionary<string, UIDeviceProfile> DeviceProfiles { get; } = new();
    public Dictionary<string, UIProfileSettings> ProfileSettings { get; } = new();

    // ── IConfigurationLoader required members ───────────────────────────

    public CoreConfiguration LoadCore() => new();
    public void SaveCore(CoreConfiguration config) { }
    public ScoringSettings LoadScoring() => new();
    public void SaveScoring(ScoringSettings settings) { }
    public MaintenanceSettings LoadMaintenance() => new();
    public void SaveMaintenance(MaintenanceSettings settings) { }
    public HydrationSettings LoadHydration() => new();
    public void SaveHydration(HydrationSettings settings) { }
    public PipelineConfiguration LoadPipelines() => new();
    public void SavePipelines(PipelineConfiguration config) { }
    public DisambiguationSettings LoadDisambiguation() => new();
    public void SaveDisambiguation(DisambiguationSettings settings) { }
    public MediaTypeConfiguration LoadMediaTypes() => new();
    public void SaveMediaTypes(MediaTypeConfiguration config) { }
    public TranscodingSettings LoadTranscoding() => new();
    public void SaveTranscoding(TranscodingSettings settings) { }
    public FieldPriorityConfiguration LoadFieldPriorities() => new();
    public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
    public LibrariesConfiguration LoadLibraries() => new();
    public ProviderConfiguration? LoadProvider(string name) => null;
    public void SaveProvider(ProviderConfiguration config) { }
    public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];

    public T? LoadConfig<T>(string subdirectory, string name) where T : class
    {
        // Route ui/devices/{name} to DeviceProfiles
        if (subdirectory == "ui/devices" && typeof(T) == typeof(UIDeviceProfile))
        {
            return DeviceProfiles.TryGetValue(name, out var device) ? (T)(object)device : null;
        }

        // Route ui/profiles/{name} to ProfileSettings
        if (subdirectory == "ui/profiles" && typeof(T) == typeof(UIProfileSettings))
        {
            return ProfileSettings.TryGetValue(name, out var profile) ? (T)(object)profile : null;
        }

        // Route ui/global to UIGlobalSettings
        if (subdirectory == "ui" && name == "global" && typeof(T) == typeof(UIGlobalSettings))
        {
            return (T)(object)new UIGlobalSettings();
        }

        return null;
    }

    public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
    public T? LoadAi<T>() where T : class => default;
    public void SaveAi<T>(T settings) where T : class { }
    public PaletteConfiguration LoadPalette() => new();
    public void SavePalette(PaletteConfiguration palette) { }
}
