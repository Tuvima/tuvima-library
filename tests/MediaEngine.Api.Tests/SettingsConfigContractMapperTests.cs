using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Api.Endpoints;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using ContractPipelineConfiguration = MediaEngine.Contracts.Settings.PipelineConfiguration;
using StoragePipelineConfiguration = MediaEngine.Domain.Configuration.PipelineConfiguration;

namespace MediaEngine.Api.Tests;

public sealed class SettingsConfigContractMapperTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Libraries_ProbeDetailsAreNeverPersistedAsUnknownConfiguration()
    {
        var stored = SettingsContractMapper.ToStorage(new UpdateLibrariesRequest
        {
            StorageLocations = [new ServerStorageLocationDto
            {
                Id = "media", Label = "Media storage", Path = @"C:\Media", AllowWrite = true,
                AvailableBytes = 123456, FileSystem = "NTFS",
            }],
        });
        var location = Assert.Single(stored.StorageLocations);
        Assert.Equal("media", location.Id);
        Assert.Equal("Media storage", location.Label);
        Assert.Equal(@"C:\Media", location.Path);
        Assert.True(location.AllowWrite);
        Assert.Null(location.UnmappedProperties);
        var json = JsonSerializer.Serialize(stored, JsonOptions);
        Assert.DoesNotContain("available_bytes", json);
        Assert.DoesNotContain("file_system", json);
    }

    [Fact]
    public void MediaTypes_RoundTripEveryFieldAndAuthoritativeDefaultExtension()
    {
        var storage = new MediaTypeConfiguration();

        var contract = SettingsContractMapper.ToContract(storage);
        var roundTrip = SettingsContractMapper.ToStorage(contract);

        AssertJsonEqual(storage, contract);
        AssertJsonEqual(storage, roundTrip);
        Assert.Contains(".pdf", contract.Types.Single(type => type.Key == "books").Extensions);
        Assert.Contains(".cb7", contract.Types.Single(type => type.Key == "comics").Extensions);
        Assert.Contains(".m2ts", contract.Types.Single(type => type.Key == "movies").Extensions);
        Assert.Contains(".opus", contract.Types.Single(type => type.Key == "music").Extensions);
    }

    [Fact]
    public void Hydration_RoundTripsCompleteEngineConfiguration()
    {
        var storage = new HydrationSettings
        {
            MaxConcurrentRetailProviderJobs = 7,
            MaxConcurrentWikidataJobs = 2,
            MaxConcurrentWriteBackJobs = 3,
            SkipWikipediaWithoutQid = false,
            WikipediaDescriptionMaxChars = 2048,
            PostHydrationOrganizeThreshold = 0.77,
            CollectionRollupRelationshipTypes = ["series", "franchise"],
            TwoPassEnabled = false,
            LocalMatchFuzzyThreshold = 0.97,
            IdentityRetryMaxAttempts = 9,
            IdentityRetryJitterMaxMilliseconds = 2200,
            FetchTemporalQualifiers = false,
            SeriesManifestRefreshDays = 14,
            TimelineRetentionDays = 90,
        };

        var contract = SettingsContractMapper.ToContract(storage);
        var roundTrip = SettingsContractMapper.ToStorage(contract);

        AssertJsonEqual(storage, contract);
        AssertJsonEqual(storage, roundTrip);
        Assert.Equal(7, contract.MaxConcurrentRetailProviderJobs);
        Assert.Equal(2200, contract.IdentityRetryJitterMaxMilliseconds);
        Assert.Equal(90, contract.TimelineRetentionDays);
    }

    [Fact]
    public void UISettings_RoundTripGlobalDeviceProfileAndResolvedShapes()
    {
        var global = new UIGlobalSettings
        {
            SchemaVersion = "2.0",
            AccentColor = "#123456",
            Features = new UIFeatureFlags { SearchButton = false },
            Shell = new UIShellSettings { IntentDockItems = ["Read", "Listen"] },
            Pages = new UIPageSettings
            {
                Home = new UIHomePageSettings { PendingFilesDisplay = "badge" },
            },
        };
        var device = new UIDeviceProfile
        {
            DeviceClass = "television",
            DisplayName = "TV",
            DarkMode = true,
            Constraints = new UIDeviceConstraints
            {
                FeaturesDisabled = ["theme_toggle"],
                PagesDisabled = ["server_settings"],
                AllowTextInput = false,
                MinTouchTargetPx = 64,
            },
        };
        var profile = new UIProfileSettings
        {
            ProfileId = "profile-1",
            AccentColor = "#654321",
            BorderRadius = 20,
        };
        var resolved = new ResolvedUISettings
        {
            DeviceClass = "television",
            AccentColor = "#654321",
            Constraints = device.Constraints,
            Features = global.Features,
            Shell = global.Shell,
            Pages = global.Pages,
        };

        AssertJsonEqual(global, SettingsContractMapper.ToContract(global));
        AssertJsonEqual(device, SettingsContractMapper.ToContract(device));
        AssertJsonEqual(profile, SettingsContractMapper.ToContract(profile));
        AssertJsonEqual(resolved, SettingsContractMapper.ToContract(resolved));
        AssertJsonEqual(global, SettingsContractMapper.ToStorage(SettingsContractMapper.ToContract(global)));
        AssertJsonEqual(device, SettingsContractMapper.ToStorage(SettingsContractMapper.ToContract(device)));
        AssertJsonEqual(profile, SettingsContractMapper.ToStorage(SettingsContractMapper.ToContract(profile)));
    }

    [Fact]
    public void TranscodingPipelineAndLibraryPreferences_UseLosslessContractBoundaries()
    {
        var transcoding = new MediaEngine.Domain.Configuration.TranscodingSettings
        {
            HardwareAcceleration = "nvenc",
            MaxConcurrentTranscodes = 3,
            DefaultMobileProfile = "mobile-standard",
        };
        var pipelines = new StoragePipelineConfiguration
        {
            Pipelines = new()
            {
                ["Books"] = new MediaEngine.Domain.Configuration.MediaTypePipeline
                {
                    MaxProviderAttempts = 3,
                    Scoring = new MediaEngine.Domain.Configuration.RetailScoringPolicyConfiguration
                    {
                        CreatorListMode = "local-primary-containment",
                    },
                    Providers =
                    [
                        new MediaEngine.Domain.Configuration.PipelineProviderEntry
                        {
                            Rank = 1,
                            Name = "open_library",
                            Purpose = "identity",
                            RequiresIdentity = true,
                            UseAsIdentityFallback = true,
                            AcceptedTransition = new MediaEngine.Domain.Configuration.AcceptedProviderTransitionConfiguration
                            {
                                Provider = "open_library",
                                MaxAttempts = 1,
                                HintFields = ["title", "author"],
                            },
                        },
                    ],
                },
            },
        };
        var preferences = new MediaEngine.Domain.Configuration.LibraryPreferencesSettings
        {
            ViewModes = new() { ["movies"] = "series" },
        };

        var transcodingContract = SettingsContractMapper.ToContract(transcoding);
        ContractPipelineConfiguration pipelineContract = SettingsContractMapper.ToContract(pipelines);
        var preferencesContract = SettingsContractMapper.ToContract(preferences);

        AssertJsonEqual(transcoding, transcodingContract);
        AssertJsonEqual(pipelines, pipelineContract);
        AssertJsonEqual(preferences, preferencesContract);
        Assert.True(pipelineContract.Pipelines["Books"].Providers.Single().RequiresIdentity);
        Assert.True(pipelineContract.Pipelines["Books"].Providers.Single().UseAsIdentityFallback);
        Assert.Equal(3, pipelineContract.Pipelines["Books"].MaxProviderAttempts);
        Assert.Equal(
            "open_library",
            pipelineContract.Pipelines["Books"].Providers.Single().AcceptedTransition?.Provider);
        AssertJsonEqual(transcoding, SettingsContractMapper.ToStorage(transcodingContract));
        AssertJsonEqual(pipelines, SettingsContractMapper.ToStorage(pipelineContract));
        AssertJsonEqual(preferences, SettingsContractMapper.ToStorage(preferencesContract));
    }

    private static void AssertJsonEqual(object expected, object actual)
    {
        var expectedNode = JsonSerializer.SerializeToNode(expected, expected.GetType(), JsonOptions);
        var actualNode = JsonSerializer.SerializeToNode(actual, actual.GetType(), JsonOptions);
        Assert.True(
            JsonNode.DeepEquals(expectedNode, actualNode),
            $"Expected: {expectedNode}{Environment.NewLine}Actual: {actualNode}");
    }
}
