using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.System;

namespace MediaEngine.Api.Tests;

public sealed class CoreSettingsWireContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CoreSettingsContracts_PreserveEveryEngineField()
    {
        const string json =
            """
            {
              "server_name": "Den",
              "language": "en",
              "display_language": "fr",
              "metadata_language": "de",
              "additional_languages": ["es", "it"],
              "accept_any_language": false,
              "country": "CA",
              "date_format": "yyyy-MM-dd",
              "time_format": "24h"
            }
            """;

        var settings = JsonSerializer.Deserialize<ServerGeneralSettingsDto>(json, JsonOptions);
        var roundTrip = JsonSerializer.Serialize(settings, JsonOptions);

        Assert.Equal("Den", settings!.ServerName);
        Assert.Equal("fr", settings.DisplayLanguage);
        Assert.Equal("de", settings.MetadataLanguage);
        Assert.Equal(["es", "it"], settings.AdditionalLanguages);
        Assert.False(settings.AcceptAnyLanguage);
        Assert.Contains("\"date_format\":\"yyyy-MM-dd\"", roundTrip, StringComparison.Ordinal);
        Assert.Contains("\"time_format\":\"24h\"", roundTrip, StringComparison.Ordinal);
    }

    [Fact]
    public void FolderAndOrganizationContracts_PreserveNestedConfiguration()
    {
        var libraries = new UpdateLibrariesRequest
        {
            Libraries =
            [
                new LibraryFolderDto
                {
                    Name = "Read",
                    Category = "Books",
                    Kind = "catalogued",
                    Area = "read",
                    Presentation = "catalogue",
                    MetadataPolicy = "enriched",
                    MediaTypes = ["Books", "Comics"],
                    Sources =
                    [
                        new LibrarySourceDto
                        {
                            Id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                            Path = @"D:\Library\Read",
                            Role = "primary_destination",
                            ManagementMode = "managed_by_tuvima",
                            IncludeSubdirectories = false,
                            AccessMode = "writable",
                            WritebackOverride = false,
                            ParticipatesInOrganization = true,
                        },
                    ],
                    PrimaryDestinationSourceId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                    AcceptedIntakeModes = ["drag_and_drop"],
                    DuplicatePolicy = "skip_exact",
                    OrganizationPolicy = new LibraryOrganizationPolicyDto
                    {
                        Mode = "tuvima_standard",
                        PreserveOriginals = true,
                    },
                    Notes = "NAS mirror",
                },
            ],
        };
        var organization = new OrganizationTemplateDto(
            "{Author}/{Title}",
            "Author/Title",
            new Dictionary<string, string> { ["books"] = "{Author}/{Series}/{Title}" });

        var libraryJson = JsonSerializer.Serialize(libraries, JsonOptions);
        var organizationJson = JsonSerializer.Serialize(organization, JsonOptions);

        Assert.Contains("\"writeback_override\":false", libraryJson, StringComparison.Ordinal);
        Assert.Contains("\"include_subdirectories\":false", libraryJson, StringComparison.Ordinal);
        Assert.Contains("\"primary_destination_source_id\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\"", libraryJson, StringComparison.Ordinal);
        Assert.DoesNotContain("source_paths", libraryJson, StringComparison.Ordinal);
        Assert.Contains("\"templates\":{\"books\":\"{Author}/{Series}/{Title}\"}", organizationJson, StringComparison.Ordinal);
    }

    [Fact]
    public void LibrarySettingsMapper_PreservesSchemaSixLibrariesAndViewStorage()
    {
        var request = new UpdateLibrariesRequest
        {
            SchemaVersion = "6.0",
            StorageLocations =
            [
                new ServerStorageLocationDto
                {
                    Id = "media",
                    Label = "Media",
                    Path = @"D:\Media",
                    AllowWrite = true,
                },
            ],
            ViewStorage = new ViewStorageDto
            {
                StorageLocationId = "media",
                RelativeRoot = "View",
            },
            PersonalLibraryPolicy = new PersonalLibraryPolicyDto
            {
                AllowMobileBackup = false,
                AllowConnectedDeviceImport = false,
                DefaultVisibility = "private",
            },
            Libraries =
            [
                new LibraryFolderDto
                {
                    Id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                    Name = "Movies",
                    Category = "Movies",
                    Kind = "catalogued",
                    Area = "watch",
                    Presentation = "catalogue",
                    MetadataPolicy = "enriched",
                    MediaTypes = ["Movies"],
                    Sources =
                    [
                        new LibrarySourceDto
                        {
                            Id = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                            Path = @"D:\Media\Movies",
                            Role = "primary_destination",
                            ManagementMode = "managed_by_tuvima",
                            AccessMode = "writable",
                            ParticipatesInOrganization = true,
                        },
                    ],
                    PrimaryDestinationSourceId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                    AcceptedIntakeModes = ["browser_upload"],
                },
            ],
        };

        var storage = SettingsContractMapper.ToStorage(request);
        var contract = SettingsContractMapper.ToContract(storage);

        Assert.Equal("6.0", storage.SchemaVersion);
        Assert.Equal(@"D:\Media", storage.StorageLocations.Single().Path);
        Assert.Equal("View", contract.ViewStorage.RelativeRoot);
        Assert.Equal(@"D:\Media\Movies", storage.Libraries.Single().PrimaryDestination?.Path);
        Assert.True(storage.Libraries.Single().PrimaryDestination?.AllowsFileMutation);
        Assert.False(storage.PersonalLibraryPolicy.AllowMobileBackup);
        Assert.False(contract.PersonalLibraryPolicy.AllowConnectedDeviceImport);
        Assert.Equal("private", contract.PersonalLibraryPolicy.DefaultVisibility);
        Assert.Contains("personal_library_policy", JsonSerializer.Serialize(contract, JsonOptions), StringComparison.Ordinal);
        Assert.DoesNotContain("source_paths", JsonSerializer.Serialize(contract, JsonOptions), StringComparison.Ordinal);
    }

    [Fact]
    public void AuthPathAndSystemContracts_KeepEstablishedJsonNames()
    {
        var authJson = JsonSerializer.Serialize(new AuthSettingsDto
        {
            Mode = "Required",
            LocalhostBypass = false,
            ExternalProviders =
            [
                new ExternalAuthProviderDto
                {
                    Id = "company",
                    Kind = "oidc",
                    Enabled = true,
                    DisplayName = "Company Login",
                    Authority = "https://identity.example",
                    ClientId = "tuvima",
                    Scopes = ["openid", "profile"],
                    CallbackPath = "/signin-tuvima-company",
                },
            ],
        }, JsonOptions);
        var pathJson = JsonSerializer.Serialize(new PathTestResultDto(@"D:\Media", true, true, false), JsonOptions);
        var systemJson = JsonSerializer.Serialize(new SystemStatusResponse
        {
            Status = "ok",
            Version = "1.2.3",
            Language = "de",
        }, JsonOptions);

        Assert.Contains("\"localhost_bypass\":false", authJson, StringComparison.Ordinal);
        Assert.Contains("\"external_providers\":[", authJson, StringComparison.Ordinal);
        Assert.Contains("\"callback_path\":\"/signin-tuvima-company\"", authJson, StringComparison.Ordinal);
        Assert.Contains("\"has_write\":false", pathJson, StringComparison.Ordinal);
        Assert.Contains("\"language\":\"de\"", systemJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderContracts_PreserveStatusMappingArgumentsAndDisabledSampleMessage()
    {
        const string statusJson =
            """
            {
              "name": "open_library",
              "display_name": "Open Library",
              "enabled": true,
              "is_zero_key": true,
              "is_reachable": true,
              "domain": "Book",
              "capability_tags": ["metadata"],
              "default_weight": 0.85,
              "field_weights": {"title": 1.0},
              "hydration_stages": [1, 2],
              "endpoints": {"search": "https://example.test/search"},
              "field_mappings": [{
                "claim_key": "title",
                "json_path": "$.title",
                "confidence": 0.9,
                "transform": "join",
                "transform_args": ", "
              }],
              "throttle_ms": 250,
              "max_concurrency": 2,
              "language_strategy": "localized",
              "available_fields": ["title"],
              "media_types": ["Books"],
              "requires_api_key": false,
              "has_api_key": false,
              "api_key_delivery": null,
              "api_key_param_name": null,
              "timeout_seconds": 30,
              "custom_icon_name": "MenuBook",
              "health_status": "Healthy",
              "consecutive_failures": 0,
              "last_success_at": "2026-07-26T12:00:00Z",
              "last_failure_at": null,
              "last_failure_reason": null,
              "down_since": null
            }
            """;
        const string sampleJson =
            """{"provider_name":"open_library","message":"Provider disabled. Enable it before fetching sample claims.","claims":[]}""";

        var status = JsonSerializer.Deserialize<ProviderStatusDto>(statusJson, JsonOptions);
        var sample = JsonSerializer.Deserialize<ProviderSampleResultDto>(sampleJson, JsonOptions);

        Assert.Equal(", ", status!.FieldMappings!.Single().TransformArgs);
        Assert.Equal(30, status.TimeoutSeconds);
        Assert.Equal("localized", status.LanguageStrategy);
        Assert.Contains("disabled", sample!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderCatalogueContract_PreservesAllDashboardMetadata()
    {
        var catalogue = new ProviderCatalogueDto
        {
            ProviderId = "provider-id",
            Name = "open_library",
            DisplayName = "Open Library",
            Enabled = true,
            Domain = "Book",
            MediaTypes = ["Books"],
            AccentColor = "#123456",
            MaterialIcon = "MenuBook",
            ExternalUrlTemplate = "https://example.test/{id}",
            Category = "Open",
            RequiresKey = false,
            AuthType = "none",
            SearchChips = new() { ["Books"] = ["Title", "Author"] },
            RankingChips = new() { ["Books"] = ["Confidence"] },
            IconPath = "/provider-icons/open-library.svg",
            HydrationStages = [1, 2],
            Capabilities = ["identity", "metadata", "artwork"],
            SystemRole = "canonical_source",
            RequiredSystemProvider = true,
            LanguageStrategy = "source",
            Onboarding = new ProviderOnboardingDto
            {
                Classification = "built_in",
                SupportedLanes = ["read"],
                RequiredScopes = ["Read-only metadata"],
                Credentials = [],
            },
        };

        var json = JsonSerializer.Serialize(catalogue, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ProviderCatalogueDto>(json, JsonOptions);

        Assert.Equal(catalogue.ProviderId, roundTrip!.ProviderId);
        Assert.Equal(catalogue.SearchChips, roundTrip.SearchChips);
        Assert.Equal(catalogue.RankingChips, roundTrip.RankingChips);
        Assert.Equal([1, 2], roundTrip.HydrationStages);
        Assert.Equal(["identity", "metadata", "artwork"], roundTrip.Capabilities);
        Assert.Equal("canonical_source", roundTrip.SystemRole);
        Assert.True(roundTrip.RequiredSystemProvider);
        Assert.Equal("source", roundTrip.LanguageStrategy);
        Assert.Equal("built_in", roundTrip.Onboarding!.Classification);
        Assert.Equal(["read"], roundTrip.Onboarding.SupportedLanes);
    }
}
