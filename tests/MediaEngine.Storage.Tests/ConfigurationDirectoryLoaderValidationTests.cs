using MediaEngine.Domain.Configuration;
using MediaEngine.Storage;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Storage.Tests;

public sealed class ConfigurationDirectoryLoaderValidationTests
{
    [Fact]
    public void CoreAuthentication_AllowsMultipleProtocolSpecificProviders()
    {
        var core = new CoreConfiguration
        {
            Auth = new AuthSettings
            {
                ExternalProviders =
                [
                    new ExternalAuthProviderSettings
                    {
                        Id = "google",
                        Kind = ExternalAuthProviderKinds.OpenIdConnect,
                        Enabled = true,
                        DisplayName = "Google",
                        Authority = "https://accounts.google.com",
                        ClientId = "client-id",
                        Scopes = ["openid", "profile", "email"],
                    },
                    new ExternalAuthProviderSettings
                    {
                        Id = "github",
                        Kind = ExternalAuthProviderKinds.OAuth,
                        Enabled = true,
                        DisplayName = "GitHub",
                        Issuer = "https://github.com",
                        ClientId = "client-id",
                        Scopes = ["read:user", "user:email"],
                        AuthorizationEndpoint = "https://github.com/login/oauth/authorize",
                        TokenEndpoint = "https://github.com/login/oauth/access_token",
                        UserInformationEndpoint = "https://api.github.com/user",
                    },
                ],
            },
        };

        var errors = JsonConfigValidator.Validate(core, "core.json");

        Assert.DoesNotContain(errors, error => error.StartsWith("auth.", StringComparison.Ordinal));
    }

    [Fact]
    public void CoreAuthentication_RejectsDuplicateIdsAndInsecureEndpoints()
    {
        var core = new CoreConfiguration
        {
            Auth = new AuthSettings
            {
                ExternalProviders =
                [
                    new ExternalAuthProviderSettings
                    {
                        Id = "github",
                        Kind = ExternalAuthProviderKinds.OAuth,
                        Enabled = true,
                        DisplayName = "GitHub",
                        Issuer = "http://github.com",
                        ClientId = "client-id",
                        AuthorizationEndpoint = "http://github.com/login/oauth/authorize",
                        TokenEndpoint = "https://github.com/login/oauth/access_token",
                        UserInformationEndpoint = "https://api.github.com/user",
                    },
                    new ExternalAuthProviderSettings { Id = "github", DisplayName = "Duplicate" },
                ],
            },
        };

        var errors = JsonConfigValidator.Validate(core, "core.json");

        Assert.Contains(errors, error => error.Contains("id must be unique", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("issuer must be an absolute HTTPS URL", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("authorization_endpoint must be an absolute HTTPS URL", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidCoreJson_LoadsSuccessfully()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);

        var core = loader.LoadCore();

        Assert.Equal("2.0", core.SchemaVersion);
    }

    [Fact]
    public void LoadCore_RejectsObsoleteWatchDirectories()
    {
        using var temp = TempConfig.Create();
        var corePath = System.IO.Path.Combine(temp.Path, "core.json");
        File.WriteAllText(corePath, """
            {
              "schema_version": "2.0",
              "database_path": "library.db",
              "server_name": "Tuvima",
              "watch_directories": ["C:\\drop-a"]
            }
            """);

        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var ex = Assert.Throws<ConfigValidationException>(() => loader.LoadCore());

        Assert.Contains("watch_directories is not supported by core schema 2.0", ex.Message);
    }

    [Fact]
    public void SaveLibraries_RoundTripsSchemaSixCataloguedLibrariesAndViewStorage()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = new LibrariesConfiguration
        {
            SchemaVersion = "6.0",
            StorageLocations =
            [
                new ServerStorageLocationConfig
                {
                    Id = "media",
                    Label = "Media",
                    Path = Drive,
                    AllowWrite = true,
                },
            ],
            PersonalLibraryPolicy = new PersonalLibraryPolicyConfig
            {
                AllowMobileBackup = false,
                AllowConnectedDeviceImport = false,
                DefaultVisibility = LibraryVisibility.Shared,
            },
            Libraries =
            [
                new LibraryFolderConfig
                {
                    Id = "44444444-4444-4444-8444-444444444444",
                    Name = "Movies",
                    Category = "Movies",
                    Kind = LibraryKinds.Catalogued,
                    Area = LibraryAreas.Watch,
                    Presentation = LibraryPresentations.Catalogue,
                    MetadataPolicy = LibraryMetadataPolicies.Enriched,
                    MediaTypes = ["Movies"],
                    Sources =
                    [
                        new LibrarySourceConfig
                        {
                            Id = "44444444-aaaa-4444-8444-444444444444",
                            Path = P(Drive, "media/home-movies"),
                            Role = LibrarySourceRoles.PrimaryDestination,
                            ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                            AccessMode = LibrarySourceAccessModes.Writable,
                            ParticipatesInOrganization = true,
                            IntakeRole = LibrarySourceIntakeRoles.Direct,
                            WritebackOverride = false,
                        },
                        new LibrarySourceConfig
                        {
                            Id = "44444444-bbbb-4444-8444-444444444444",
                            Path = P(OtherDrive, "archive/home-movies"),
                            Role = LibrarySourceRoles.Secondary,
                            ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
                            AccessMode = LibrarySourceAccessModes.ReadOnly,
                        },
                    ],
                    PrimaryDestinationSourceId = "44444444-aaaa-4444-8444-444444444444",
                    Visibility = LibraryVisibility.Household,
                    AcceptedIntakeModes = [LibraryIntakeModes.DragAndDrop, LibraryIntakeModes.BrowserUpload],
                    DuplicatePolicy = LibraryDuplicatePolicies.SkipExact,
                    OrganizationPolicy = new()
                    {
                        Mode = LibraryOrganizationModes.KeepOriginalNames,
                        PreserveOriginals = true,
                    },
                },
            ],
        };

        loader.SaveLibraries(config);
        var roundTrip = loader.LoadLibraries().Libraries.Single();

        Assert.Equal("Movies", roundTrip.Name);
        Assert.Equal("44444444-4444-4444-8444-444444444444", roundTrip.Id);
        Assert.Equal(LibraryKinds.Catalogued, roundTrip.Kind);
        Assert.Equal(LibraryAreas.Watch, roundTrip.Area);
        Assert.Equal(LibraryPresentations.Catalogue, roundTrip.Presentation);
        Assert.Equal(LibraryMetadataPolicies.Enriched, roundTrip.MetadataPolicy);
        Assert.Equal(["Movies"], roundTrip.MediaTypes);
        Assert.Equal(2, roundTrip.Sources.Count);
        Assert.Equal(P(Drive, "media/home-movies"), roundTrip.PrimaryDestination?.Path);
        Assert.True(roundTrip.PrimaryDestination?.AllowsFileMutation);
        Assert.False(roundTrip.Sources[1].AllowsFileMutation);
        Assert.False(loader.LoadLibraries().PersonalLibraryPolicy.AllowMobileBackup);
        Assert.False(loader.LoadLibraries().PersonalLibraryPolicy.AllowConnectedDeviceImport);
        Assert.Equal(LibraryVisibility.Shared, loader.LoadLibraries().PersonalLibraryPolicy.DefaultVisibility);
    }

    [Fact]
    public void SaveLibraries_RejectsUnsupportedPersonalLibraryDefaultVisibility()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = CreateValidCataloguedLibrary();
        config.PersonalLibraryPolicy.DefaultVisibility = "organization_wide";

        var ex = Assert.Throws<ConfigValidationException>(() => loader.SaveLibraries(config));

        Assert.Contains("personal_library_policy.default_visibility", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveLibraries_RequiresViewRootToReferenceWritableStorage()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = CreateValidCataloguedLibrary();
        config.StorageLocations =
        [
            new ServerStorageLocationConfig
            {
                Id = "media",
                Label = "Media",
                Path = Drive,
                AllowWrite = false,
            },
        ];
        config.ViewStorage = new ViewStorageConfig
        {
            StorageLocationId = "media",
            RelativeRoot = "../outside",
        };

        var ex = Assert.Throws<ConfigValidationException>(() => loader.SaveLibraries(config));

        Assert.Contains("must reference a writable storage location", ex.Message, StringComparison.Ordinal);
        Assert.Contains("must be a contained relative path", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveLibraries_RejectsObsoletePersonalLibraryConfiguration()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = new LibrariesConfiguration
        {
            Libraries =
            [
                CreateValidPersonalLibrary(
                    "11111111-1111-4111-8111-111111111111",
                    "11111111-aaaa-4111-8111-111111111111",
                    P(Drive, "media/phone")),
                CreateValidPersonalLibrary(
                    "22222222-2222-4222-8222-222222222222",
                    "22222222-aaaa-4222-8222-222222222222",
                    P(OtherDrive, "media/archive")),
            ],
        };

        var ex = Assert.Throws<ConfigValidationException>(() => loader.SaveLibraries(config));

        Assert.Contains("personal libraries are obsolete", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveLibraries_RejectsMissingIdentityUnsupportedPolicyAndImplicitPrimaryDestination()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = new LibrariesConfiguration
        {
            Libraries =
            [
                new LibraryFolderConfig
                {
                    Name = "Home Videos",
                    Category = "Home Videos",
                    Kind = LibraryKinds.Personal,
                    Area = LibraryAreas.View,
                    Presentation = LibraryPresentations.Video,
                    MetadataPolicy = "provider_guessing",
                    OwnerProfileId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                    Sources =
                    [
                        new LibrarySourceConfig
                        {
                            Id = "aaaaaaaa-bbbb-4aaa-8aaa-aaaaaaaaaaaa",
                            Path = P(Drive, "media/home-videos"),
                            ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                            AccessMode = LibrarySourceAccessModes.Writable,
                        },
                    ],
                },
            ],
        };

        var ex = Assert.Throws<ConfigValidationException>(() => loader.SaveLibraries(config));

        Assert.Contains("id must be a non-empty GUID", ex.Message);
        Assert.Contains("metadata_policy", ex.Message);
        Assert.Contains("primary_destination_source_id", ex.Message);
    }

    [Fact]
    public void LoadLibraries_RejectsSchemaTwoAndLegacyFlatFolderFields()
    {
        using var temp = TempConfig.Create();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "libraries.json"), """
            {
              "schema_version": "2.0",
              "libraries": [
                {
                  "id": "44444444-4444-4444-8444-444444444444",
                  "name": "Photos",
                  "kind": "photos",
                  "source_paths": ["C:\\media\\photos"],
                  "library_root": "C:\\media",
                  "read_only": true
                }
              ]
            }
            """);

        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var ex = Assert.Throws<ConfigValidationException>(() => loader.LoadLibraries());

        Assert.Contains("schema_version must be 6.0", ex.Message);
        Assert.Contains("kind must be catalogued or personal", ex.Message);
        Assert.Contains("source_paths is not supported", ex.Message);
        Assert.Contains("library_root is not supported", ex.Message);
        Assert.Contains("read_only is not supported", ex.Message);
    }

    [Fact]
    public void SaveLibraries_RejectsOverlappingAndUnsafeExistingSources()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = CreateValidCataloguedLibrary();
        config.Libraries[0].Sources.Add(new LibrarySourceConfig
        {
            Id = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            Path = P(Drive, "media/movies/archive"),
            ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
            AccessMode = LibrarySourceAccessModes.Writable,
            WritebackOverride = true,
            ParticipatesInOrganization = true,
        });

        var ex = Assert.Throws<ConfigValidationException>(() => loader.SaveLibraries(config));

        Assert.Contains("must not overlap", ex.Message);
        Assert.Contains("existing libraries must use read_only access", ex.Message);
        Assert.Contains("existing libraries cannot participate in organization", ex.Message);
        Assert.Contains("existing libraries cannot enable writeback", ex.Message);
    }

    [Fact]
    public void MalformedJson_FallsBackToValidBackup()
    {
        using var temp = TempConfig.Create();
        var corePath = System.IO.Path.Combine(temp.Path, "core.json");
        File.Copy(corePath, corePath + ".bak", overwrite: true);
        File.WriteAllText(corePath, "{");

        var loader = new ConfigurationDirectoryLoader(temp.Path);

        Assert.Equal("2.0", loader.LoadCore().SchemaVersion);
    }

    [Fact]
    public void MalformedJsonWithoutValidBackup_ThrowsClearValidationException()
    {
        using var temp = TempConfig.Create();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "core.json"), "{");

        var loader = new ConfigurationDirectoryLoader(temp.Path);

        var ex = Assert.Throws<ConfigValidationException>(() => loader.LoadCore());
        Assert.Contains("core.schema.json", ex.SchemaName);
        Assert.Contains("core.json", ex.FilePath);
    }

    [Fact]
    public void InvalidCoreRange_ThrowsClearValidationException()
    {
        using var temp = TempConfig.Create();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "core.json"), """
            {
              "schema_version": "2.0",
              "database_path": "library.db",
              "server_name": "Tuvima",
              "date_format": "century"
            }
            """);

        var loader = new ConfigurationDirectoryLoader(temp.Path);

        var ex = Assert.Throws<ConfigValidationException>(() => loader.LoadCore());
        Assert.Contains("date_format", ex.Message);
    }

    [Fact]
    public void ProviderMissingNameOrInvalidTimeout_FailsClearly()
    {
        using var temp = TempConfig.Create();
        var providerPath = System.IO.Path.Combine(temp.Path, "providers", "broken.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(providerPath)!);
        File.WriteAllText(providerPath, """
            {
              "enabled": true,
              "weight": 0.5,
              "http_client": { "timeout_seconds": 0 }
            }
            """);

        var loader = new ConfigurationDirectoryLoader(temp.Path);

        var ex = Assert.Throws<ConfigValidationException>(() => loader.LoadProvider("broken"));
        Assert.Contains("name is required", ex.Message);
        Assert.Contains("timeout_seconds", ex.Message);
    }

    [Fact]
    public void LibraryPreferences_RequireExplicitPolicyForEveryMediaType()
    {
        using var temp = TempConfig.Create();
        var uiDirectory = System.IO.Path.Combine(temp.Path, "ui");
        Directory.CreateDirectory(uiDirectory);
        File.WriteAllText(System.IO.Path.Combine(uiDirectory, "library-preferences.json"), """
            {
              "missing_item_display": {
                "tv": {
                  "enabled": true,
                  "default_visibility": "shown",
                  "presentation": "all",
                  "page_size": 50,
                  "detail_hydration": "owned_only"
                }
              },
              "view_modes": {},
              "lane_group_display": {}
            }
            """);
        var loader = new ConfigurationDirectoryLoader(temp.Path);

        var ex = Assert.Throws<ConfigValidationException>(() =>
            loader.LoadConfig<LibraryPreferencesSettings>("ui", "library-preferences"));

        Assert.Contains("missing_item_display.comics is required", ex.Message);
        Assert.Contains("ui/library-preferences.schema.json", ex.SchemaName);
    }

    [Fact]
    public void ProviderSequenceManifest_RejectsImageFields()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var provider = new ProviderConfiguration
        {
            Name = "comicvine",
            SequenceManifest = new ProviderSequenceManifestRequestConfiguration
            {
                Enabled = true,
                UrlTemplate = "{base_url}/issues",
                Fields = ["id", "image.original_url"],
                PageSize = 100,
                MaxPages = 20,
                ContainerKind = "ComicVolume",
                ExpectedTotalKind = "issues",
            },
        };

        var ex = Assert.Throws<ConfigValidationException>(() => loader.SaveProvider(provider));

        Assert.Contains("must not request image fields", ex.Message);
    }

    [Fact]
    public void StructuredProviderQuery_RejectsUnsupportedConfiguredOperators()
    {
        var provider = new ProviderConfiguration
        {
            Name = "catalog",
            SearchStrategies =
            [
                new SearchStrategyConfig
                {
                    Name = "configured-search",
                    Priority = 1,
                    UrlTemplate = "{base_url}?query={query}",
                    Query = new QueryCompositionConfig
                    {
                        Syntax = "provider-specific-code",
                        Operator = "THEN",
                        Clauses =
                        [
                            new QueryClauseConfig
                            {
                                Field = "recording",
                                Value = "title",
                                Match = "approximately",
                                Required = true,
                            },
                        ],
                    },
                },
            ],
        };

        var errors = JsonConfigValidator.Validate(provider, "providers/catalog.json");

        Assert.Contains(errors, error => error.Contains("query.syntax", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("query.operator", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("clauses[].match", StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderTransition_RequiresConfiguredTargetAndBoundedAttemptBudget()
    {
        var pipelines = new Dictionary<string, MediaTypePipeline>(StringComparer.OrdinalIgnoreCase)
        {
            ["Music"] = new()
            {
                MaxProviderAttempts = 2,
                Providers =
                [
                    new PipelineProviderEntry
                    {
                        Rank = 1,
                        Name = "fallback",
                        Purpose = "enrichment",
                        RequiresIdentity = true,
                        UseAsIdentityFallback = true,
                        AcceptedTransition = new AcceptedProviderTransitionConfiguration
                        {
                            Provider = "missing-provider",
                            MaxAttempts = 2,
                            HintFields = ["title"],
                        },
                    },
                ],
            },
        };

        var errors = JsonConfigValidator.Validate(pipelines, "pipelines.json");

        Assert.Contains(errors, error => error.Contains("not configured", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("lower than max_provider_attempts", StringComparison.Ordinal));
    }

    [Fact]
    public void SecretValues_AreNotIncludedInValidationException()
    {
        using var temp = TempConfig.Create();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "core.json"), """
            {
              "schema_version": "2.0",
              "database_path": "library.db",
              "server_name": "Tuvima",
              "client_secret": "do-not-leak",
              "date_format": "bad"
            }
            """);

        var loader = new ConfigurationDirectoryLoader(temp.Path);

        var ex = Assert.Throws<ConfigValidationException>(() => loader.LoadCore());
        Assert.DoesNotContain("do-not-leak", ex.Message);
    }

    [Fact]
    public async Task Watcher_DebouncesValidReloadAndKeepsLastKnownGoodOnInvalidChange()
    {
        using var temp = TempConfig.Create();
        using var loader = new ConfigurationDirectoryLoader(temp.Path);
        var seen = new List<bool>();
        using var signal = new SemaphoreSlim(0);
        loader.ConfigurationChanged += (_, args) =>
        {
            seen.Add(args.Applied);
            signal.Release();
        };

        _ = loader.LoadCore();
        loader.StartWatching();
        var corePath = System.IO.Path.Combine(temp.Path, "core.json");
        File.WriteAllText(corePath, """
            {
              "schema_version": "2.0",
              "database_path": "library.db",
              "server_name": "Reloaded"
            }
            """);

        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Reloaded", loader.LoadCore().ServerName);

        File.WriteAllText(corePath, "{");
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("Reloaded", loader.LoadCore().ServerName);
        Assert.Contains(true, seen);
        Assert.Contains(false, seen);
    }

    // Paths must be rooted for the OS the tests run on: validation uses Path.IsPathFullyQualified.
    private static string Drive => OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static string OtherDrive => OperatingSystem.IsWindows() ? @"D:\" : "/mnt/d";

    private static string P(string root, string relative) =>
        System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static LibrariesConfiguration CreateValidCataloguedLibrary() => new()
    {
        Libraries =
        [
            new LibraryFolderConfig
            {
                Id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                Name = "Movies",
                Category = "Movies",
                Kind = LibraryKinds.Catalogued,
                Area = LibraryAreas.Watch,
                Presentation = LibraryPresentations.Catalogue,
                MetadataPolicy = LibraryMetadataPolicies.Enriched,
                MediaTypes = ["Movies"],
                Sources =
                [
                    new LibrarySourceConfig
                    {
                        Id = "aaaaaaaa-bbbb-4aaa-8aaa-aaaaaaaaaaaa",
                        Path = P(Drive, "media/movies"),
                        Role = LibrarySourceRoles.PrimaryDestination,
                        ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                        AccessMode = LibrarySourceAccessModes.Writable,
                        ParticipatesInOrganization = true,
                    },
                ],
                PrimaryDestinationSourceId = "aaaaaaaa-bbbb-4aaa-8aaa-aaaaaaaaaaaa",
                Visibility = LibraryVisibility.Household,
                AcceptedIntakeModes = [LibraryIntakeModes.IncomingFolder],
                DuplicatePolicy = LibraryDuplicatePolicies.SkipExact,
                OrganizationPolicy = new()
                {
                    Mode = LibraryOrganizationModes.TuvimaStandard,
                },
            },
        ],
    };

    private static LibraryFolderConfig CreateValidPersonalLibrary(
        string libraryId,
        string sourceId,
        string path) => new()
        {
            Id = libraryId,
            Name = "Personal Space",
            Kind = LibraryKinds.Personal,
            Area = LibraryAreas.View,
            Presentation = LibraryPresentations.MixedGallery,
            MetadataPolicy = LibraryMetadataPolicies.LocalOnly,
            Sources =
        [
            new LibrarySourceConfig
            {
                Id = sourceId,
                Path = path,
                Role = LibrarySourceRoles.PrimaryDestination,
                ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                AccessMode = LibrarySourceAccessModes.Writable,
                ParticipatesInOrganization = true,
            },
        ],
            PrimaryDestinationSourceId = sourceId,
            OwnerProfileId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            Visibility = LibraryVisibility.Private,
            AcceptedIntakeModes = [LibraryIntakeModes.BrowserUpload],
            DuplicatePolicy = LibraryDuplicatePolicies.SkipExact,
            OrganizationPolicy = new() { Mode = LibraryOrganizationModes.CaptureYearMonth },
        };

    private sealed class TempConfig : IDisposable
    {
        private TempConfig(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempConfig Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tuvima-config-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            Directory.CreateDirectory(System.IO.Path.Combine(path, "providers"));
            File.WriteAllText(System.IO.Path.Combine(path, "core.json"), System.Text.Json.JsonSerializer.Serialize(new CoreConfiguration()));
            return new TempConfig(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
