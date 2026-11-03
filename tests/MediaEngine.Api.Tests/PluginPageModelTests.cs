using System.Text.Json;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.Setup;
using MediaEngine.Api.Services.Plugins.Trakt;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugin.ListenBrainz;
using MediaEngine.Plugins;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The plugin page model (Plugins and Integrations screens v5 WP22): the Engine builds each plugin's page from the plugin's own JSON,
/// whichever of the two file formats it is. TMDB, LRCLIB and StashDB are provider source files; Trakt and ListenBrainz are plugin
/// manifests; the last is a private JSON plugin written for this test.
/// </summary>
public sealed class PluginPageModelTests
{
    private static readonly string[] Cards = ["access", "provides", "settings", "privacy", "about"];

    [Theory]
    [InlineData("tmdb")]
    [InlineData("lrclib")]
    [InlineData("stashbox")]
    public void ASourceFile_GivesEveryCardFromItsJsonAlone(string name)
    {
        var page = SourcePage(name);

        Assert.Equal(Cards, page.Cards.Select(card => card.Key));
        Assert.Equal("source_file", page.Definition);
        Assert.NotEmpty(Card(page, "access").Access!.VaultWords);
        Assert.NotEmpty(Card(page, "provides").Provides);
        Assert.NotEmpty(Card(page, "settings").Controls);
        Assert.NotEmpty(Card(page, "privacy").Destinations);
        Assert.NotEmpty(Card(page, "about").Text!);
    }

    [Fact]
    public void ASourceFile_ShowsItsTunablesAsAdvancedSettings()
    {
        var settings = Card(SourcePage("tmdb"), "settings").Controls.ToDictionary(control => control.Key);

        Assert.False(settings["language_strategy"].Advanced);
        Assert.Equal(3, settings["language_strategy"].Choices.Count);
        Assert.True(settings["timeout_seconds"].Advanced);
        Assert.Equal("seconds", settings["timeout_seconds"].Unit);
        Assert.Equal(10, settings["timeout_seconds"].Value!.Value.GetInt32());
        Assert.True(settings["throttle_ms"].Advanced);
        Assert.True(settings["weight"].ReadOnly);
        Assert.True(settings["requests_per_second"].ReadOnly);
        Assert.Equal(20, settings["requests_per_second"].Value!.Value.GetDouble());
    }

    [Fact]
    public void ACredentialInASourceFile_IsInTheOneCredentialsList_WithLinksAndLengths()
    {
        var access = Card(SourcePage("tmdb", configured: ["api_key"]), "access");

        var key = Assert.Single(access.Credentials);
        Assert.Equal("api_key", key.Name);
        Assert.True(key.Required);
        Assert.Equal("household", key.Scope);
        Assert.True(key.Configured);
        Assert.Contains(access.Links, link => link is { Role: "signup", Url: "https://www.themoviedb.org/settings/api" });
        Assert.Contains(Card(SourcePage("tmdb"), "privacy").Links, link => link.Role == "privacy");
        Assert.True(Card(SourcePage("tmdb"), "access").NeedsAttention);
        Assert.False(access.NeedsAttention);
    }

    [Fact]
    public void ASourceFile_ImpliesItsTests_FromItsJson_UnlessItDeclaresThem()
    {
        var tmdb = Card(SourcePage("tmdb"), "settings").Actions.Select(action => action.Kind).ToList();
        var lrclib = Card(SourcePage("lrclib"), "settings").Actions.Select(action => action.Kind).ToList();
        var stashbox = Card(SourcePage("stashbox"), "settings").Actions.Select(action => action.Kind).ToList();

        Assert.Equal(["test_connection", "check_now"], tmdb);
        Assert.Equal(["test_connection", "check_now"], lrclib);
        Assert.Equal(["test_connection"], stashbox);
    }

    [Fact]
    public void StashDb_DescribesItsServersAsALockedGroupList_WithoutTheDashboardNamingIt()
    {
        var servers = new Dictionary<string, JsonElement>
        {
            ["servers"] = JsonSerializer.SerializeToElement(new object[]
            {
                new { id = "stashdb", title = "StashDB", subtitle = "https://stashdb.org/graphql", state = "connected", locked = true },
                new { id = "mine", title = "My stash-box", subtitle = "https://stash.example", state = "not_connected", locked = false },
            }),
            ["fingerprint_permission"] = JsonSerializer.SerializeToElement(true),
        };

        var page = SourcePage("stashbox", external: servers);

        var controls = Card(page, "settings").Controls.ToDictionary(control => control.Key);
        var list = controls["servers"].GroupList!;
        Assert.Equal("group_list", controls["servers"].Control);
        Assert.Equal("Add a server", list.AddLabel);
        Assert.Equal(["test_connection", "remove"], list.RowActions);
        Assert.Equal(["name", "address", "api_key"], list.Fields.Select(field => field.Key));
        Assert.Equal("secret", list.Fields.Single(field => field.Key == "api_key").Control);
        Assert.True(list.Items.Single(item => item.Id == "stashdb").Locked);
        Assert.False(list.Items.Single(item => item.Id == "mine").Locked);
        Assert.True(controls["fingerprint_permission"].Value!.Value.GetBoolean());
        Assert.Equal("toggle", controls["fingerprint_permission"].Control);
        Assert.Contains(Card(page, "access").Actions, action => action.Kind == "test_connection");
        Assert.True(Card(page, "privacy").VaultWords!.Length > 0);
    }

    [Fact]
    public void Trakt_GivesEveryCardFromItsManifest_AndItsAccountIsAPerPersonSecret()
    {
        var page = ManifestPage(new TraktPlugin().Manifest, configured: ["client_id"]);

        Assert.Equal(Cards, page.Cards.Select(card => card.Key));
        var access = Card(page, "access");
        Assert.Equal(["client_id", "client_secret", "account"], access.Credentials.Select(credential => credential.Name));
        Assert.Equal(true, access.Credentials[0].Configured);
        Assert.Equal(false, access.Credentials[1].Configured);
        Assert.Null(access.Credentials[2].Configured);
        Assert.Equal("profile", access.Credentials[2].Scope);
        Assert.Equal("device_code", access.Credentials[2].SignInMethod);
        Assert.True(access.NeedsAttention);

        var account = Assert.Single(Card(page, "settings").Controls);
        Assert.Equal("account", account.Key);
        Assert.Equal("secret", account.Control);
        Assert.Equal("profile", account.Scope);
        Assert.Null(account.Value);

        Assert.Equal(["Lists & ratings", "Account sync"], Card(page, "provides").Provides);
        Assert.Equal("api.trakt.tv", Assert.Single(Card(page, "privacy").Destinations).Host);
        Assert.Contains("Trakt", Card(page, "about").Text);
    }

    [Fact]
    public void ListenBrainz_GivesEveryCardFromItsManifest_AndItsTokenIsAPerPersonSecret()
    {
        var page = ManifestPage(new ListenBrainzPlugin().Manifest);

        Assert.Equal(Cards, page.Cards.Select(card => card.Key));
        var token = Assert.Single(Card(page, "settings").Controls);
        Assert.Equal("user_token", token.Key);
        Assert.Equal("profile", token.Scope);
        Assert.Equal(["Account sync"], Card(page, "provides").Provides);
        Assert.Equal("api.listenbrainz.org", Assert.Single(Card(page, "privacy").Destinations).Host);
        Assert.False(Card(page, "access").NeedsAttention);
    }

    [Fact]
    public void APrivateDeclarativePlugin_GivesEveryCard_WithTheWholeVocabulary_AndNoCodeWrittenForIt()
    {
        var page = ManifestPage(PrivatePlugin(), trust: PluginTrustLevel.Private, settings: new Dictionary<string, JsonElement>
        {
            ["mode"] = JsonSerializer.SerializeToElement("fast"),
            ["volume"] = JsonSerializer.SerializeToElement(7),
        });

        Assert.Equal(Cards, page.Cards.Select(card => card.Key));
        Assert.Equal("manifest", page.Definition);
        var settings = Card(page, "settings");
        var controls = settings.Controls.ToDictionary(control => control.Key);

        Assert.Equal("toggle", controls["notify"].Control);
        Assert.Equal("choice", controls["mode"].Control);
        Assert.Equal(["Fast", "Careful"], controls["mode"].Choices.Select(choice => choice.Label));
        Assert.Equal("fast", controls["mode"].Value!.Value.GetString());
        Assert.Equal("number", controls["volume"].Control);
        Assert.Equal("percent", controls["volume"].Unit);
        Assert.Equal(0, controls["volume"].Minimum);
        Assert.Equal(11, controls["volume"].Maximum);
        Assert.Equal("Playback", controls["volume"].Group);
        Assert.Equal("Type here", controls["nickname"].Placeholder);
        Assert.Equal("url", controls["home"].Control);
        Assert.Equal("path", controls["folder"].Control);
        Assert.True(controls["folder"].Advanced);
        Assert.True(controls["fixed"].ReadOnly);
        Assert.Equal("choices", controls["tags"].Control);
        Assert.Equal("profile", controls["mine"].Scope);
        Assert.Equal("mode", controls["fast_only"].ShowWhen!.Setting);
        Assert.Equal("fast", controls["fast_only"].ShowWhen!.Value!.Value.GetString());
        Assert.Equal("tool", controls["needs_tool"].Requires!.Kind);
        Assert.Equal("Needs ffmpeg, which is not installed.", controls["needs_tool"].Requires!.Reason);
        Assert.Equal("secret", controls["secret_word"].Control);
        Assert.Null(controls["secret_word"].Value);
        Assert.Equal(["dry_run"], settings.Actions.Select(action => action.Kind));
        Assert.Equal("Rehearse", settings.Actions[0].Label);
        Assert.Equal(["Insights"], Card(page, "provides").Provides);
        Assert.Equal("ntfy.sh", Assert.Single(Card(page, "privacy").Destinations).Host);
    }

    [Fact]
    public void UnknownWords_AreReportedAsThingsToKnow_AndNeverThrown()
    {
        var page = ManifestPage(PrivatePlugin());

        var words = page.ThingsToKnow.Select(note => (note.Where, note.Word)).ToList();
        Assert.Contains(("", "colour_scheme"), words);
        Assert.Contains(("mode", "sparkle"), words);
        Assert.Contains(("volume", "control"), words);
        Assert.Contains(("", "actions"), words);
        Assert.Contains(("hidden_default", "settings_schema"), words);
        Assert.DoesNotContain(Card(page, "settings").Controls, control => control.Key == "hidden_default");
        Assert.All(page.ThingsToKnow, note => Assert.NotEmpty(note.Message));
    }

    [Fact]
    public void ASettingsDescriptionThatIsNotAnObject_IsIgnoredWithANote()
    {
        var description = PluginSettingsDescriptionReader.Read(JsonDocument.Parse("[1,2]").RootElement);

        Assert.Empty(description.Settings);
        Assert.Single(description.ThingsToKnow);
    }

    [Fact]
    public void TheOlderSettingsSchemaShape_StillReadsUnchanged()
    {
        using var document = JsonDocument.Parse("""
        {
          "properties": {
            "request_delay_ms": { "type": "integer", "title": "Request delay", "description": "Delay.", "minimum": 100, "maximum": 10000, "default": 500, "advanced": true },
            "source_mode": { "type": "string", "enum": ["hybrid_review", "manual_only"], "default": "hybrid_review" },
            "auto": { "type": "boolean", "label": "Auto", "help_text": "Help", "x-advanced": true }
          },
          "required": ["auto"]
        }
        """);

        var settings = PluginSettingsDescriptionReader.Read(document.RootElement).Settings.ToDictionary(setting => setting.Key);

        Assert.Equal("number", settings["request_delay_ms"].Control);
        Assert.True(settings["request_delay_ms"].IsInteger);
        Assert.True(settings["request_delay_ms"].Advanced);
        Assert.Equal(500, settings["request_delay_ms"].Default!.Value.GetInt32());
        Assert.Equal("choice", settings["source_mode"].Control);
        Assert.Equal(["Hybrid review", "Manual only"], settings["source_mode"].Choices.Select(choice => choice.Label));
        Assert.Equal("toggle", settings["auto"].Control);
        Assert.Equal("Auto", settings["auto"].Title);
        Assert.Equal("Help", settings["auto"].Description);
        Assert.True(settings["auto"].Advanced);
    }

    [Fact]
    public void TheTwoFileFormats_MapToTheSameCredentialShape()
    {
        var fromSource = PluginCredentialMapper.FromSourceFile(Provider("tmdb")).Credentials.Single();
        var fromManifest = PluginCredentialMapper.FromManifest(new TraktPlugin().Manifest).Credentials.First();

        Assert.IsType<PluginCredential>(fromSource);
        Assert.IsType<PluginCredential>(fromManifest);
        Assert.Equal("household", fromSource.Scope);
        Assert.Equal("household", fromManifest.Scope);
        Assert.False(string.IsNullOrWhiteSpace(fromSource.Label));
        Assert.False(string.IsNullOrWhiteSpace(fromManifest.Help));
    }

    [Fact]
    public void ThePageModel_NeverCarriesASecretValue()
    {
        var page = ManifestPage(PrivatePlugin(), settings: new Dictionary<string, JsonElement>
        {
            ["secret_word"] = JsonSerializer.SerializeToElement("hunter2"),
        });

        Assert.DoesNotContain("hunter2", JsonSerializer.Serialize(page));
    }

    [Fact]
    public void ThePageModel_SerializesWithTheWireNames()
    {
        var json = JsonSerializer.Serialize(SourcePage("tmdb"));

        Assert.Contains("\"plugin_id\":\"tmdb\"", json);
        Assert.Contains("\"things_to_know\":", json);
        Assert.Contains("\"needs_attention\":", json);
    }

    private static PluginSetupCardDto Card(PluginSetupPageResponse page, string key) => page.Cards.Single(card => card.Key == key);

    private static PluginSetupPageResponse SourcePage(
        string name,
        string[]? configured = null,
        IReadOnlyDictionary<string, JsonElement>? external = null)
    {
        var config = Provider(name);
        var sheet = new PluginSourceDto
        {
            Name = name,
            MediaTypes = [.. config.CanHandle?.MediaTypes ?? []],
            Supplies = [.. config.ProviderCapabilities],
            SendsDataTo = [.. config.Endpoints.Values.Select(address => new Uri(address).Host).Distinct()],
            Access = PluginAccessMapper.Map(
                config.Declaration,
                [.. config.Endpoints.Values.Select(address => (new Uri(address).Host, (string?)null)).Distinct()],
                config.RequiresApiKey,
                config.CanHandle?.MediaTypes ?? [],
                config.CanHandle?.MediaTypes,
                "metadata"),
        };
        return PluginPageModelBuilder.Build(PluginPageModelInputs.FromSourceFile(
            config,
            sheet,
            alwaysOn: false,
            new HashSet<string>(configured ?? [], StringComparer.OrdinalIgnoreCase),
            external ?? new Dictionary<string, JsonElement>()));
    }

    private static PluginSetupPageResponse ManifestPage(
        PluginManifest manifest,
        string[]? configured = null,
        PluginTrustLevel trust = PluginTrustLevel.Official,
        IReadOnlyDictionary<string, JsonElement>? settings = null)
    {
        var registration = new PluginRegistration(
            manifest,
            Enabled: true,
            IsBuiltIn: trust == PluginTrustLevel.Official,
            Capabilities: [],
            Settings: settings ?? new Dictionary<string, JsonElement>(),
            LoadError: null,
            SettingsSchema: manifest.SettingsSchema,
            ManifestPath: trust == PluginTrustLevel.Official ? null : "plugins/private-sample/plugin.json",
            Trust: trust,
            Compatibility: PluginCompatibilityResult.Compatible);
        var access = PluginAccessMapper.Map(
            manifest.Declaration,
            [.. manifest.Network.Select(destination => (destination.Host, (string?)destination.Sends))],
            manifest.Secrets.Count > 0,
            PluginAccessMapper.MediaLabelsFor(PluginCategories.ResolveMediaTypes(manifest.MediaTypes)),
            manifest.MediaTypes,
            manifest.Category);
        return PluginPageModelBuilder.Build(PluginPageModelInputs.FromManifest(
            registration,
            access,
            new HashSet<string>(configured ?? [], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, JsonElement>()));
    }

    private static PluginManifest PrivatePlugin() => JsonSerializer.Deserialize<PluginManifest>("""
    {
      "id": "private.sample",
      "name": "Private sample",
      "version": "1.0.0",
      "kind": "declarative",
      "description": "Sends a note to a phone. It is only JSON.",
      "provides": ["Insights"],
      "permissions": ["network.http", "secrets.plugin"],
      "network": [ { "host": "ntfy.sh", "sends": "The note title and message" } ],
      "secrets": [ { "name": "token", "label": "Access token", "description": "Your ntfy token." } ],
      "declarative": { "notification_channels": [] },
      "settings_schema": {
        "colour_scheme": "blue",
        "properties": {
          "notify": { "type": "boolean", "title": "Notify me", "default": true },
          "mode": { "type": "string", "choices": [ { "value": "fast", "label": "Fast" }, { "value": "careful", "label": "Careful" } ], "default": "careful", "sparkle": true },
          "volume": { "type": "integer", "control": "dial", "unit": "percent", "minimum": 0, "maximum": 11, "group": "Playback", "order": 5 },
          "nickname": { "type": "string", "placeholder": "Type here" },
          "home": { "type": "string", "format": "url" },
          "folder": { "type": "string", "format": "path", "advanced": true },
          "fixed": { "type": "string", "read_only": true, "default": "locked" },
          "tags": { "type": "array", "enum": ["a", "b"] },
          "mine": { "type": "string", "scope": "profile" },
          "fast_only": { "type": "boolean", "show_when": { "setting": "mode", "equals": "fast" } },
          "needs_tool": { "type": "boolean", "requires": { "kind": "tool", "name": "ffmpeg" } },
          "secret_word": { "type": "string", "format": "secret" }
        },
        "actions": [ { "kind": "dry_run", "label": "Rehearse" }, { "kind": "explode" } ]
      },
      "default_settings": { "hidden_default": 1 }
    }
    """)!;

    private static ProviderConfiguration Provider(string name) =>
        new ConfigurationDirectoryLoader(Path.Combine(FindRepoRoot(), "config")).LoadProvider(name)
        ?? throw new InvalidOperationException($"No provider file for {name}.");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
