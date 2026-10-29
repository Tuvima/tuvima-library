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
    public void LoadLibraries_OnAFreshInstall_GivesViewAWritableStorageLocation()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);

        var libraries = loader.LoadLibraries();

        var storage = Assert.Single(libraries.StorageLocations, location => location.Id == libraries.ViewStorage.StorageLocationId);
        Assert.True(storage.AllowWrite);
        Assert.Empty(JsonConfigValidator.Validate(libraries, "libraries.json", []));
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
            },
            Libraries =
            [
                new LibraryFolderConfig
                {
                    Id = "44444444-4444-4444-8444-444444444444",
                    Name = "Movies",
                    Category = "Movies",
                    Kind = LibraryKinds.Catalogued,
                    KindKey = "movies",
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
                            ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                            AccessMode = LibrarySourceAccessModes.Writable,
                        },
                        new LibrarySourceConfig
                        {
                            Id = "44444444-bbbb-4444-8444-444444444444",
                            Path = P(OtherDrive, "archive/home-movies"),
                            ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
                            AccessMode = LibrarySourceAccessModes.ReadOnly,
                        },
                    ],
                    PrimaryDestinationSourceId = "44444444-aaaa-4444-8444-444444444444",
                    AcceptedIntakeModes = [LibraryIntakeModes.DragAndDrop, LibraryIntakeModes.BrowserUpload],
                    OrganizationPolicy = new()
                    {
                        Mode = LibraryOrganizationModes.KeepOriginalNames,
                        WriteTags = false,
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

        Assert.Contains("older 2.0 format", ex.Message);
        Assert.Contains("kind must be catalogued or personal", ex.Message);
        Assert.Contains("source_paths is not supported", ex.Message);
        Assert.Contains("library_root is not supported", ex.Message);
        Assert.Contains("read_only is not supported", ex.Message);
    }

    [Fact]
    public void LoadLibraries_RefusesTheOlderSixPointZeroFormatWithAPlainMessage()
    {
        using var temp = TempConfig.Create();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "libraries.json"), """
            {
              "schema_version": "6.0",
              "storage_locations": [ { "id": "media", "label": "Media", "path": "C:\\media", "allow_write": true } ],
              "view_storage": { "storage_location_id": "media", "relative_root": "View" },
              "libraries": []
            }
            """);

        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var ex = Assert.Throws<ConfigValidationException>(() => loader.LoadLibraries());

        Assert.Contains("older 6.0 format", ex.Message);
        Assert.Contains("fresh default", ex.Message);
    }

    [Fact]
    public void SaveLibraries_RefusesTheOlderSixPointZeroVersion()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = CreateValidCataloguedLibrary();
        config.SchemaVersion = "6.0";

        var ex = Assert.Throws<ConfigValidationException>(() => loader.SaveLibraries(config));

        Assert.Contains("older 6.0 format", ex.Message);
    }

    [Fact]
    public void LoadLibraries_RejectsTheRemovedFolderFieldsAsUnsupported()
    {
        using var temp = TempConfig.Create();
        new ConfigurationDirectoryLoader(temp.Path).SaveLibraries(CreateValidCataloguedLibrary());
        var path = System.IO.Path.Combine(temp.Path, "libraries.json");
        var json = File.ReadAllText(path)
            .Replace("\"access_mode\"", "\"role\": \"primary_destination\", \"intake_role\": \"direct\", \"accepts_new_media\": true, \"access_mode\"")
            .Replace("\"mode\"", "\"preserve_originals\": true, \"mode\"");
        File.WriteAllText(path, json);
        File.Delete(path + ".bak");

        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var ex = Assert.Throws<ConfigValidationException>(() => loader.LoadLibraries());

        Assert.Contains("role is not supported", ex.Message);
        Assert.Contains("intake_role is not supported", ex.Message);
        Assert.Contains("accepts_new_media is not supported", ex.Message);
        Assert.Contains("preserve_originals is not supported", ex.Message);
    }

    [Fact]
    public void LoadLibraries_RefusesTheOlderEightPointZeroFormatWithAPlainMessage()
    {
        using var temp = TempConfig.Create();
        new ConfigurationDirectoryLoader(temp.Path).SaveLibraries(CreateValidCataloguedLibrary());
        var path = System.IO.Path.Combine(temp.Path, "libraries.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"10.0\"", "\"8.0\"", StringComparison.Ordinal));
        File.Delete(path + ".bak");

        var ex = Assert.Throws<ConfigValidationException>(() => new ConfigurationDirectoryLoader(temp.Path).LoadLibraries());

        Assert.Contains("older 8.0 format", ex.Message);
        Assert.Contains("fresh default", ex.Message);
    }

    [Fact]
    public void LoadLibraries_AcceptsSuitableForLimitedProfilesOnAFolder_AndRoundTripsIt()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = CreateValidCataloguedLibrary();
        config.Libraries[0].Sources[0].OkForLimitedProfiles = true;

        loader.SaveLibraries(config);

        Assert.True(loader.LoadLibraries().Libraries[0].Sources[0].OkForLimitedProfiles);
        Assert.Contains("\"ok_for_limited_profiles\": true", File.ReadAllText(System.IO.Path.Combine(temp.Path, "libraries.json")));
    }

    [Fact]
    public void LoadLibraries_RejectsTheOldLibraryLevelSuitableForLimitedProfilesAsUnsupported()
    {
        using var temp = TempConfig.Create();
        new ConfigurationDirectoryLoader(temp.Path).SaveLibraries(CreateValidCataloguedLibrary());
        var path = System.IO.Path.Combine(temp.Path, "libraries.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"youtube_lookup\"", "\"ok_for_limited_profiles\": true, \"youtube_lookup\"", StringComparison.Ordinal));
        File.Delete(path + ".bak");

        var ex = Assert.Throws<ConfigValidationException>(() => new ConfigurationDirectoryLoader(temp.Path).LoadLibraries());

        Assert.Contains("ok_for_limited_profiles is not supported", ex.Message);
    }

    [Fact]
    public void SaveLibraries_RejectsADuplicatePolicyThatIsNotKeepOneOrKeepBoth()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = CreateValidCataloguedLibrary();
        config.FileHandling.DuplicatePolicy = "replace_existing";

        var ex = Assert.Throws<ConfigValidationException>(() => loader.SaveLibraries(config));

        Assert.Contains("file_handling.duplicate_policy must be keep_one or keep_both", ex.Message);
    }

    [Theory]
    [InlineData(LibraryDuplicatePolicies.KeepOne)]
    [InlineData(LibraryDuplicatePolicies.KeepBoth)]
    public void SaveLibraries_AcceptsBothDuplicatePolicies(string policy)
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = CreateValidCataloguedLibrary();
        config.FileHandling.DuplicatePolicy = policy;

        loader.SaveLibraries(config);

        Assert.Equal(policy, loader.LoadLibraries().FileHandling.DuplicatePolicy);
    }

    [Fact]
    public void Validator_AcceptsThreeOtherVideosLibrariesWithDifferentFolders()
    {
        var config = CreateValidCataloguedLibrary();
        config.Libraries.Add(OtherVideosLibrary(1, "Home Videos", P(Drive, "media/home-videos")));
        config.Libraries.Add(OtherVideosLibrary(2, "TikTok Edits", P(Drive, "media/tiktok")));
        config.Libraries.Add(OtherVideosLibrary(3, "Other Videos", P(Drive, "media/other-videos")));

        var errors = JsonConfigValidator.Validate(config, "libraries.json");

        Assert.Empty(errors);
    }

    [Fact]
    public void SaveLibraries_AcceptsThreeOtherVideosLibrariesAndKeepsAllThree()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = CreateValidCataloguedLibrary();
        config.Libraries.Add(OtherVideosLibrary(1, "Home Videos", P(Drive, "media/home-videos")));
        config.Libraries.Add(OtherVideosLibrary(2, "TikTok Edits", P(Drive, "media/tiktok")));
        config.Libraries.Add(OtherVideosLibrary(3, "Other Videos", P(Drive, "media/other-videos")));

        loader.SaveLibraries(config);

        Assert.Equal(
            ["Movies", "Home Videos", "TikTok Edits", "Other Videos"],
            loader.LoadLibraries().Libraries.Select(library => library.Name).ToArray());
    }

    [Fact]
    public void Validator_RefusesASecondMoviesLibrary_BecauseMoviesFoldersMergeIntoOneLibrary()
    {
        var config = CreateValidCataloguedLibrary();
        var second = OtherVideosLibrary(1, "Old films", P(Drive, "media/old-films"));
        second.Category = "Movies";
        second.KindKey = "movies";
        second.MediaTypes = ["Movies"];
        second.MetadataPolicy = LibraryMetadataPolicies.Enriched;
        second.YoutubeLookup = true;
        config.Libraries.Add(second);

        var errors = JsonConfigValidator.Validate(config, "libraries.json");

        Assert.Contains(errors, error => error.Contains("holds one library"));
    }

    [Fact]
    public void Validator_RefusesTwoLibrariesWhoseFoldersOverlap()
    {
        var config = CreateValidCataloguedLibrary();
        config.Libraries.Add(OtherVideosLibrary(1, "Home Videos", P(Drive, "media/movies/home")));

        var errors = JsonConfigValidator.Validate(config, "libraries.json");

        Assert.Contains(errors, error => error.Contains("must not overlap"));
    }

    [Fact]
    public void Validator_OverlapWithAVaultFolder_NamesNeitherTheEntryNorItsPosition()
    {
        var config = CreateValidCataloguedLibrary();
        var vault = OtherVideosLibrary(1, "Private Movies", P(Drive, "media/movies/home"));
        vault.IsVault = true;
        config.Libraries.Add(vault);

        var errors = JsonConfigValidator.Validate(config, "libraries.json");

        var overlap = Assert.Single(errors, error => error.Contains("overlap", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Two folders must not overlap.", overlap);
        Assert.DoesNotContain(errors, error => error.Contains("libraries[1]", StringComparison.Ordinal) && error.Contains("overlap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_RefusesAFolderWithScanningOffThatOverlapsAnother()
    {
        var config = CreateValidCataloguedLibrary();
        var paused = OtherVideosLibrary(1, "Home Videos", P(Drive, "media/movies"));
        paused.Sources[0].ScanEnabled = false;
        config.Libraries.Add(paused);

        var errors = JsonConfigValidator.Validate(config, "libraries.json");

        Assert.Contains(errors, error => error.Contains("must not overlap"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validator_AcceptsAnOrdinaryAndAVaultLibrary(bool vault)
    {
        var config = CreateValidCataloguedLibrary();
        config.Libraries[0].IsVault = vault;

        Assert.Empty(JsonConfigValidator.Validate(config, "libraries.json"));
    }

    [Fact]
    public void Validator_RefusesALibrariesFileThatStillCarriesTheOldAccessValue()
    {
        var config = CreateValidCataloguedLibrary();
        config.Libraries[0].UnmappedProperties = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["access"] = System.Text.Json.JsonDocument.Parse("\"selected\"").RootElement.Clone(),
        };

        var errors = JsonConfigValidator.Validate(config, "libraries.json");

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validator_RefusesAnOlderLibrariesFileFormatWithThePlainMessage()
    {
        var config = CreateValidCataloguedLibrary();
        config.SchemaVersion = "9.0";

        var errors = JsonConfigValidator.Validate(config, "libraries.json");

        Assert.Contains(errors, error => error.Contains("older 9.0 format") && error.Contains("not converted"));
    }

    [Fact]
    public void Validator_AcceptsALibraryDuplicateRuleOfKeepOneKeepBothOrNone()
    {
        foreach (var policy in new string?[] { null, LibraryDuplicatePolicies.KeepOne, LibraryDuplicatePolicies.KeepBoth })
        {
            var config = CreateValidCataloguedLibrary();
            config.Libraries[0].FileHandling.DuplicatePolicy = policy;

            Assert.Empty(JsonConfigValidator.Validate(config, "libraries.json"));
        }
    }

    [Fact]
    public void Validator_RefusesALibraryDuplicateRuleThatIsNotKeepOneOrKeepBoth()
    {
        var config = CreateValidCataloguedLibrary();
        config.Libraries[0].FileHandling.DuplicatePolicy = "replace_existing";

        var errors = JsonConfigValidator.Validate(config, "libraries.json");

        Assert.Contains(errors, error => error.Contains("libraries[0].file_handling.duplicate_policy must be keep_one or keep_both"));
    }

    [Fact]
    public void Validator_RefusesAFolderDuplicateRuleThatIsNotKeepOneOrKeepBoth()
    {
        var config = CreateValidCataloguedLibrary();
        config.Libraries[0].Sources[0].FileHandlingOverride = new LibrarySourceFileHandlingOverride { DuplicatePolicy = "skip_exact" };

        var errors = JsonConfigValidator.Validate(config, "libraries.json");

        Assert.Contains(errors, error => error.Contains("sources[0].file_handling_override.duplicate_policy must be keep_one or keep_both"));
    }

    [Fact]
    public void SaveLibraries_RoundTripsTheFileHandlingScanAndOverrideFields()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = CreateValidCataloguedLibrary();
        config.Libraries[0].FileHandling.DuplicatePolicy = LibraryDuplicatePolicies.KeepBoth;
        config.Libraries[0].Sources[0].ScanEnabled = false;
        config.Libraries[0].Sources[0].FileHandlingOverride = new LibrarySourceFileHandlingOverride
        {
            DuplicatePolicy = LibraryDuplicatePolicies.KeepOne,
            WriteTags = false,
        };

        loader.SaveLibraries(config);
        var library = loader.LoadLibraries().Libraries[0];

        Assert.Equal(LibraryDuplicatePolicies.KeepBoth, library.FileHandling.DuplicatePolicy);
        Assert.False(library.Sources[0].ScanEnabled);
        Assert.Equal(LibraryDuplicatePolicies.KeepOne, library.Sources[0].FileHandlingOverride!.DuplicatePolicy);
        Assert.False(library.Sources[0].FileHandlingOverride!.WriteTags);
        Assert.Contains("\"scan_enabled\": false", File.ReadAllText(System.IO.Path.Combine(temp.Path, "libraries.json")));
    }

    [Fact]
    public void SaveLibraries_KeepsAFolderOverrideNullWhenEveryFieldIsNull()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = CreateValidCataloguedLibrary();
        config.Libraries[0].Sources[0].FileHandlingOverride = new LibrarySourceFileHandlingOverride();

        loader.SaveLibraries(config);

        Assert.Null(loader.LoadLibraries().Libraries[0].Sources[0].FileHandlingOverride);
    }

    [Fact]
    public void LoadLibraries_RefusesAnUnknownFieldInsideAFolderOverride()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = CreateValidCataloguedLibrary();
        config.Libraries[0].Sources[0].FileHandlingOverride = new LibrarySourceFileHandlingOverride { WriteTags = true };
        loader.SaveLibraries(config);
        var path = System.IO.Path.Combine(temp.Path, "libraries.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"write_tags\": true", "\"write_tags\": true, \"delete_everything\": true", StringComparison.Ordinal));
        File.Delete(path + ".bak");

        var ex = Assert.Throws<ConfigValidationException>(() => new ConfigurationDirectoryLoader(temp.Path).LoadLibraries());

        Assert.Contains("delete_everything is not supported", ex.Message);
    }

    [Fact]
    public void Validator_RejectsADestinationThatIsAsIs()
    {
        var config = CreateValidCataloguedLibrary();
        var library = config.Libraries[0];
        library.Sources.Add(new LibrarySourceConfig
        {
            Id = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            Path = P(OtherDrive, "archive/movies"),
            ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
            AccessMode = LibrarySourceAccessModes.ReadOnly,
        });
        library.PrimaryDestinationSourceId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

        // Validate the raw configuration, the way a hand-edited file arrives (saving re-picks a destination first).
        var errors = JsonConfigValidator.Validate(config, "libraries.json");

        Assert.Contains(errors, error => error.Contains("an as-is folder is never a destination"));
    }

    [Fact]
    public void Validator_RejectsADestinationThatIsNotInTheLibrary()
    {
        var config = CreateValidCataloguedLibrary();
        config.Libraries[0].PrimaryDestinationSourceId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";

        var errors = JsonConfigValidator.Validate(config, "libraries.json");

        Assert.Contains(errors, error => error.Contains("must reference a source in this library"));
    }

    [Fact]
    public void Validator_AcceptsANullDestination()
    {
        var config = CreateValidCataloguedLibrary();
        config.Libraries[0].PrimaryDestinationSourceId = null;

        Assert.Empty(JsonConfigValidator.Validate(config, "libraries.json"));
    }

    [Fact]
    public void Validator_ChecksConversionStateAndOperation()
    {
        var config = CreateValidCataloguedLibrary();
        var source = config.Libraries[0].Sources[0];
        source.ConversionState = LibrarySourceConversionStates.Converting;
        source.ConversionOperationId = null;
        Assert.Contains(JsonConfigValidator.Validate(config, "libraries.json"),
            error => error.Contains("conversion_operation_id must be a non-empty GUID while the folder is converting"));

        source.ConversionOperationId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
        Assert.Empty(JsonConfigValidator.Validate(config, "libraries.json"));

        source.ConversionState = LibrarySourceConversionStates.None;
        Assert.Contains(JsonConfigValidator.Validate(config, "libraries.json"),
            error => error.Contains("only valid when the folder is converting or converted"));

        source.ConversionState = "half-way";
        Assert.Contains(JsonConfigValidator.Validate(config, "libraries.json"),
            error => error.Contains("conversion_state must be none, converting, or converted"));
    }

    [Theory]
    [InlineData(LibrarySourceConversionStates.Converting)]
    [InlineData(LibrarySourceConversionStates.Converted)]
    public void Validator_ConversionRequiresAFolderTuvimaManages(string state)
    {
        var config = CreateValidCataloguedLibrary();
        var source = config.Libraries[0].Sources[0];
        source.ManagementMode = LibrarySourceManagementModes.ExistingLibrary;
        source.AccessMode = LibrarySourceAccessModes.ReadOnly;
        source.ConversionState = state;
        source.ConversionOperationId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
        config.Libraries[0].PrimaryDestinationSourceId = null;

        var errors = JsonConfigValidator.Validate(config, "libraries.json");

        Assert.Contains(errors, error => error.Contains("requires a folder Library manages"));
    }

    [Fact]
    public void SaveLibraries_DerivesAccessFromManagementMode()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = CreateValidCataloguedLibrary();
        config.Libraries[0].Sources.Add(new LibrarySourceConfig
        {
            Id = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            Path = P(OtherDrive, "archive/movies"),
            ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
            AccessMode = LibrarySourceAccessModes.Writable,
        });
        config.Libraries[0].Sources[0].AccessMode = LibrarySourceAccessModes.ReadOnly;

        loader.SaveLibraries(config);

        var saved = loader.LoadLibraries().Libraries[0].Sources;
        Assert.Equal(LibrarySourceAccessModes.Writable, saved[0].AccessMode);
        Assert.Equal(LibrarySourceAccessModes.ReadOnly, saved[1].AccessMode);
    }

    [Fact]
    public void SaveLibraries_PicksTheFirstManagedFolderAsDestinationWhenNoneIsSet()
    {
        using var temp = TempConfig.Create();
        var loader = new ConfigurationDirectoryLoader(temp.Path);
        var config = CreateValidCataloguedLibrary();
        config.Libraries[0].PrimaryDestinationSourceId = null;

        loader.SaveLibraries(config);

        Assert.Equal("aaaaaaaa-bbbb-4aaa-8aaa-aaaaaaaaaaaa", loader.LoadLibraries().Libraries[0].PrimaryDestinationSourceId);
    }

    [Fact]
    public void ShippedConfigurationIsValidSevenPointZeroWithoutDeveloperPaths()
    {
        var repoRoot = FindRepoRoot();
        foreach (var path in new[]
        {
            System.IO.Path.Combine(repoRoot, "config", "libraries.json"),
            System.IO.Path.Combine(repoRoot, "docker", "config", "libraries.json"),
        })
        {
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("real-media", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("real_import", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\\\temp", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"role\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("preserve_originals", json, StringComparison.Ordinal);
            var parsed = System.Text.Json.JsonSerializer.Deserialize<LibrariesConfiguration>(json)!;
            Assert.Equal("10.0", parsed.SchemaVersion);
            Assert.Equal(LibraryDuplicatePolicies.KeepOne, parsed.FileHandling.DuplicatePolicy);
            Assert.NotEmpty(parsed.Libraries);
            Assert.All(parsed.Libraries, library => Assert.NotNull(library.PrimaryDestination));
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find repository root.");
    }

    [Fact]
    public void Validator_RejectsOverlappingAndWritableAsIsSources()
    {
        var config = CreateValidCataloguedLibrary();
        config.Libraries[0].Sources.Add(new LibrarySourceConfig
        {
            Id = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            Path = P(Drive, "media/movies/archive"),
            ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
            AccessMode = LibrarySourceAccessModes.Writable,
        });

        // Saving derives access from the management mode first, so check the raw configuration as a hand-edited file arrives.
        var message = string.Join(" ", JsonConfigValidator.Validate(config, "libraries.json"));

        Assert.Contains("must not overlap", message);
        Assert.Contains("as-is (existing_library) folders must use read_only access", message);
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
    public void ShippedLibraryPreferenceDefaults_OfferShowMissingForEveryMediaType_HiddenUntilTicked()
    {
        using var temp = TempConfig.Create();
        var uiDirectory = System.IO.Path.Combine(temp.Path, "ui");
        Directory.CreateDirectory(uiDirectory);
        File.Copy(
            System.IO.Path.Combine(FindRepoRoot(), "config", "ui", "library-preferences.defaults.json"),
            System.IO.Path.Combine(uiDirectory, "library-preferences.defaults.json"));
        var loader = new ConfigurationDirectoryLoader(temp.Path);

        // With no file of this computer's own, the shipped defaults are what every reader gets.
        var preferences = ((MediaEngine.Domain.Contracts.IConfigurationLoader)loader).LoadLibraryPreferences();

        Assert.NotNull(preferences);
        foreach (var mediaType in Enum.GetValues<MediaEngine.Domain.Enums.MediaType>().Where(type => type != MediaEngine.Domain.Enums.MediaType.Unknown))
        {
            var policy = preferences.MissingItemDisplay[mediaType.ToString().ToLowerInvariant()];
            Assert.True(policy.Enabled);
            Assert.Equal("hidden", policy.DefaultVisibility);
        }

        Assert.Equal("series", preferences.LaneGroupDisplay["watch"].ShelfKey);
    }

    [Fact]
    public void ThisComputersLibraryPreferences_WinOverTheShippedDefaults()
    {
        using var temp = TempConfig.Create();
        var uiDirectory = System.IO.Path.Combine(temp.Path, "ui");
        Directory.CreateDirectory(uiDirectory);
        var defaults = System.IO.Path.Combine(FindRepoRoot(), "config", "ui", "library-preferences.defaults.json");
        File.Copy(defaults, System.IO.Path.Combine(uiDirectory, "library-preferences.defaults.json"));
        File.WriteAllText(
            System.IO.Path.Combine(uiDirectory, "library-preferences.json"),
            File.ReadAllText(defaults).Replace("\"hidden\"", "\"shown\"", StringComparison.Ordinal));
        var loader = new ConfigurationDirectoryLoader(temp.Path);

        var preferences = ((MediaEngine.Domain.Contracts.IConfigurationLoader)loader).LoadLibraryPreferences();

        Assert.NotNull(preferences);
        Assert.Equal("shown", preferences.MissingItemDisplay["movies"].DefaultVisibility);
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

    // The signal is released the moment the watcher reports the change, so this only bounds a watcher that never does;
    // Windows delivers file events late while the machine is busy, which five seconds did not allow for.
    private static readonly TimeSpan WatcherEventLimit = TimeSpan.FromSeconds(30);

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

        Assert.True(await signal.WaitAsync(WatcherEventLimit));
        Assert.Equal("Reloaded", loader.LoadCore().ServerName);

        File.WriteAllText(corePath, "{");
        Assert.True(await signal.WaitAsync(WatcherEventLimit));

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
        StorageLocations =
        [
            new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = Drive, AllowWrite = true },
        ],
        Libraries =
        [
            new LibraryFolderConfig
            {
                Id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                Name = "Movies",
                Category = "Movies",
                Kind = LibraryKinds.Catalogued,
                KindKey = "movies",
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
                        ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                        AccessMode = LibrarySourceAccessModes.Writable,
                    },
                ],
                PrimaryDestinationSourceId = "aaaaaaaa-bbbb-4aaa-8aaa-aaaaaaaaaaaa",
                AcceptedIntakeModes = [LibraryIntakeModes.IncomingFolder],
                OrganizationPolicy = new()
                {
                    Mode = LibraryOrganizationModes.TuvimaStandard,
                },
            },
        ],
    };

    private static LibraryFolderConfig OtherVideosLibrary(int number, string name, string path) => new()
    {
        Id = $"cccccccc-cccc-4ccc-8ccc-00000000000{number}",
        Name = name,
        Category = "Videos",
        Kind = LibraryKinds.Catalogued,
        KindKey = "other_videos",
        Area = LibraryAreas.Watch,
        Presentation = LibraryPresentations.Catalogue,
        MetadataPolicy = LibraryMetadataPolicies.LocalOnly,
        YoutubeLookup = false,
        MediaTypes = ["Videos"],
        Sources =
        [
            new LibrarySourceConfig
            {
                Id = $"cccccccc-dddd-4ccc-8ccc-00000000000{number}",
                Path = path,
                ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                AccessMode = LibrarySourceAccessModes.Writable,
            },
        ],
        PrimaryDestinationSourceId = $"cccccccc-dddd-4ccc-8ccc-00000000000{number}",
        AcceptedIntakeModes = [LibraryIntakeModes.IncomingFolder],
        OrganizationPolicy = new() { Mode = LibraryOrganizationModes.TuvimaStandard },
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
                ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                AccessMode = LibrarySourceAccessModes.Writable,
            },
        ],
            PrimaryDestinationSourceId = sourceId,
            AcceptedIntakeModes = [LibraryIntakeModes.BrowserUpload],
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
