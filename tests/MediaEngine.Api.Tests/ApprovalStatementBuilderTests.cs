using MediaEngine.Api.Services.Approvals;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Tests;

public sealed class ApprovalStatementBuilderTests
{
    [Theory]
    [InlineData("media.read", "Reads your media files")]
    [InlineData("network.http", "Connects to the internet")]
    [InlineData("process.execute", "Runs helper programs")]
    [InlineData("tool.download", "Downloads helper tools")]
    [InlineData("ai.infer", "Uses Local AI")]
    [InlineData("storage.plugin", "Keeps its own data")]
    [InlineData("secrets.plugin", "Keeps its own keys and logins")]
    [InlineData("future.thing", "future.thing")]
    public void PermissionIds_AreShownInPlainWords_AndAnUnknownIdAsItIs(string id, string words)
    {
        Assert.Equal(words, ApprovalStatementBuilder.PermissionText(id));
    }

    [Theory]
    [InlineData("smart_labeling", "Reads file and folder names", "Suggests titles, authors, years and episode numbers when files are added")]
    [InlineData("type_logic", "Reads file names and basic file facts", "Decides the media type when it is unclear")]
    [InlineData("series_alignment", "Reads series and title details", "Suggests the order within a series")]
    [InlineData("vibe_tags", "Reads descriptions and genres", "Adds mood tags")]
    [InlineData("description_intelligence", "Reads descriptions", "Adds themes, mood, setting, period, audience, pace, content warnings and people")]
    public void EachAiFeature_HasItsPlainWords(string key, string first, string second)
    {
        Assert.Equal([first, second], ApprovalStatementBuilder.AiFeatureAllowedTo(key));
    }

    [Fact]
    public void TheShortSummaryFeature_HasItsWords_AndAnUnknownFeatureHasNone()
    {
        Assert.Equal(["Shows the short summary made by Description insights"], ApprovalStatementBuilder.AiFeatureAllowedTo("tldr"));
        Assert.Empty(ApprovalStatementBuilder.AiFeatureAllowedTo("not_a_feature"));
        Assert.Null(ApprovalStatementBuilder.AiFeatureSendsDataTo("tldr"));
    }

    [Fact]
    public void PluginStatement_ListsEachPermissionToolAiAbilityAndHowItRunsInPlainWords()
    {
        var manifest = new PluginManifest
        {
            Id = "tuvima.test.words",
            Name = "Word test",
            Permissions = ["media.read", "process.execute", "ai.infer", "storage.plugin", "odd.id"],
            ToolRequirements = [new PluginToolRequirement { Id = "ffmpeg" }, new PluginToolRequirement { Id = "somethingelse" }],
            AiPermissions = [new PluginAiPermission { Role = "text_quality" }, new PluginAiPermission { Role = "text_fast" }],
        };

        var statement = ApprovalStatementBuilder.ForPlugin(manifest, PluginTrustLevel.Official);

        Assert.Equal("Word test", statement.Title);
        Assert.Equal("Plugin", statement.KindLabel);
        Assert.Equal(
            [
                "Reads your media files",
                "Runs helper programs",
                "Uses Local AI",
                "Keeps its own data",
                "odd.id",
                "Needs FFmpeg",
                "Needs somethingelse",
                "Uses Local AI: write text (careful)",
                "Uses Local AI: write text (quick)",
                "Runs as: Official built-in",
            ],
            statement.CanDo);
    }

    [Theory]
    [InlineData("text_fast", "Uses Local AI: write text (quick)")]
    [InlineData("text_quality", "Uses Local AI: write text (careful)")]
    [InlineData("text_scholar", "Uses Local AI: write text (long)")]
    [InlineData("text_cjk", "Uses Local AI: write text (careful, Chinese, Japanese, Korean)")]
    [InlineData("future_role", "Uses Local AI: future_role")]
    public void PluginStatement_NamesEachLocalAiAbilityItAsksFor_AndAnUnknownRoleByItsId(string role, string line)
    {
        var manifest = new PluginManifest { Id = "p", Name = "P", AiPermissions = [new PluginAiPermission { Role = role }] };

        Assert.Equal([line, "Runs as: Official built-in"], ApprovalStatementBuilder.ForPlugin(manifest, PluginTrustLevel.Official).CanDo);
    }

    [Fact]
    public void PluginStatement_ListsEachLocalAiAbilityOnceAndSorted_WhateverOrderTheManifestGivesThem()
    {
        var manifest = new PluginManifest
        {
            Id = "p",
            Name = "P",
            AiPermissions =
            [
                new PluginAiPermission { Role = "text_scholar" },
                new PluginAiPermission { Role = "text_fast" },
                new PluginAiPermission { Role = "text_scholar", MaxTokens = 9 },
            ],
        };

        var statement = ApprovalStatementBuilder.ForPlugin(manifest, PluginTrustLevel.Official);

        Assert.Equal(
            ["Uses Local AI: write text (long)", "Uses Local AI: write text (quick)", "Runs as: Official built-in"],
            statement.CanDo);
    }

    [Fact]
    public void PluginHash_ChangesWhenAnotherLocalAiAbilityIsAdded_ButNotWhenTheyAreReordered()
    {
        PluginManifest With(params string[] roles) => new()
        {
            Id = "p",
            Name = "P",
            AiPermissions = roles.Select(role => new PluginAiPermission { Role = role }).ToList(),
        };

        string HashOf(PluginManifest manifest) =>
            ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(manifest, PluginTrustLevel.Official));

        Assert.NotEqual(HashOf(With("text_fast")), HashOf(With("text_fast", "text_quality")));
        Assert.Equal(HashOf(With("text_fast", "text_quality")), HashOf(With("text_quality", "text_fast")));
    }

    [Theory]
    [InlineData(PluginKind.Official, "Runs as: Official built-in")]
    [InlineData(PluginKind.Declarative, "Runs as: JSON-only plugin")]
    [InlineData(PluginKind.Service, "Runs as: separate program")]
    [InlineData(PluginKind.Sandboxed, "Runs as: sandboxed plugin")]
    [InlineData((PluginKind)99, "Runs as: 99")]
    public void PluginStatement_SaysHowThePluginRuns(PluginKind kind, string line)
    {
        var statement = ApprovalStatementBuilder.ForPlugin(new PluginManifest { Id = "p", Name = "P", Kind = kind }, PluginTrustLevel.Official);

        Assert.Equal([line], statement.CanDo);
    }

    [Fact]
    public void PluginHash_ChangesWhenThePluginRunsAnotherWay()
    {
        string HashOf(PluginKind kind) => ApprovalStatementBuilder.Hash(
            ApprovalStatementBuilder.ForPlugin(new PluginManifest { Id = "p", Name = "P", Kind = kind }, PluginTrustLevel.Official));

        Assert.NotEqual(HashOf(PluginKind.Official), HashOf(PluginKind.Service));
        Assert.NotEqual(HashOf(PluginKind.Declarative), HashOf(PluginKind.Sandboxed));
    }

    [Fact]
    public void PluginStatement_NamesTheKeysAndLoginsItDeclares_ByLabel()
    {
        var manifest = new PluginManifest
        {
            Id = "p",
            Name = "P",
            Permissions = ["secrets.plugin"],
            Secrets =
            [
                new PluginSecretDeclaration { Name = "api_token", Label = "API token" },
                new PluginSecretDeclaration { Name = "login", Label = "Account login" },
            ],
        };

        var statement = ApprovalStatementBuilder.ForPlugin(manifest, PluginTrustLevel.Community);

        Assert.Equal(["Keeps its own keys and logins: API token, Account login", "Runs as: Official built-in"], statement.CanDo);
    }

    [Theory]
    [InlineData(PluginTrustLevel.Official, "Official plugin from Tuvima")]
    [InlineData(PluginTrustLevel.Verified, "Verified")]
    [InlineData(PluginTrustLevel.Community, "Community, not checked by Tuvima")]
    [InlineData(PluginTrustLevel.Private, "Added by hand, not checked by Tuvima")]
    public void PluginStatement_SaysHowFarTheLibraryTrustsItAndThatPluginsAreNotSandboxed(PluginTrustLevel trust, string trustWords)
    {
        var statement = ApprovalStatementBuilder.ForPlugin(new PluginManifest { Id = "p", Name = "P" }, trust);

        Assert.Equal(
            [trustWords, "Plugins run inside the server without a sandbox. Only turn on plugins you trust."],
            statement.Notes);
    }

    [Fact]
    public void PluginStatement_SendsDataOutOnlyToTheInternet_AndOnlyWithNetworkAccess()
    {
        var withNetwork = ApprovalStatementBuilder.ForPlugin(
            new PluginManifest { Id = "p", Name = "P", Permissions = ["network.http"] }, PluginTrustLevel.Official);
        var without = ApprovalStatementBuilder.ForPlugin(
            new PluginManifest { Id = "p", Name = "P", Permissions = ["media.read"] }, PluginTrustLevel.Official);

        Assert.True(withNetwork.SendsDataOut);
        Assert.Equal("the internet", withNetwork.SendsDataTo);
        Assert.False(without.SendsDataOut);
        Assert.Null(without.SendsDataTo);
    }

    [Fact]
    public void PluginStatement_WithDestinations_HasOneContactsLinePerDestination_AndNamesTheHosts()
    {
        var manifest = new PluginManifest
        {
            Id = "p",
            Name = "P",
            Permissions = ["media.read", "network.http"],
            Network =
            [
                new PluginNetworkDestination { Host = "www.wikidata.org", Sends = "Item IDs of Universes in your Library" },
                new PluginNetworkDestination { Host = "*.fandom.com", Sends = " Requests for a wiki's page list " },
            ],
        };

        var statement = ApprovalStatementBuilder.ForPlugin(manifest, PluginTrustLevel.Official);

        Assert.Equal(
            [
                "Reads your media files",
                "Contacts www.wikidata.org: Item IDs of Universes in your Library",
                "Contacts *.fandom.com: Requests for a wiki's page list",
                "Runs as: Official built-in",
            ],
            statement.CanDo);
        Assert.DoesNotContain("Connects to the internet", statement.CanDo);
        Assert.True(statement.SendsDataOut);
        Assert.Equal("www.wikidata.org, *.fandom.com", statement.SendsDataTo);
    }

    [Fact]
    public void PluginStatement_WithoutDestinations_StillSaysItConnectsToTheInternet()
    {
        var statement = ApprovalStatementBuilder.ForDirectoryEntry(
            new PluginManifest { Id = "p", Name = "P", Permissions = ["network.http"] }, PluginTrustLevel.Community);

        Assert.Contains("Connects to the internet", statement.CanDo);
        Assert.True(statement.SendsDataOut);
        Assert.Equal("the internet", statement.SendsDataTo);
    }

    [Theory]
    [InlineData(PluginKind.Declarative, "This plugin is a description file; it runs no code of its own.")]
    [InlineData(PluginKind.Service, "This plugin runs as a separate program and talks to the Library through its published contract. Only turn on plugins you trust.")]
    [InlineData(PluginKind.Sandboxed, "This plugin runs in a sandbox with limited access.")]
    [InlineData(PluginKind.Official, "This plugin ships with the Library.")]
    public void DirectoryInstallPreview_SaysWhereThePluginRuns_ByItsKind_NeverThatItRunsInsideTheServer(PluginKind kind, string expected)
    {
        var statement = ApprovalStatementBuilder.ForDirectoryEntry(
            new PluginManifest { Id = "p", Name = "P", Kind = kind }, PluginTrustLevel.Community);

        Assert.Contains(expected, statement.Notes);
        Assert.DoesNotContain(statement.Notes, note => note.Contains("without a sandbox", StringComparison.Ordinal));
    }

    [Fact]
    public void StatementHash_ChangesWhenADestinationIsAddedOrChanged_AndNotWhenItIsRestated()
    {
        PluginManifest With(params PluginNetworkDestination[] destinations) => new()
        {
            Id = "p",
            Name = "P",
            Permissions = ["network.http"],
            Network = destinations,
        };

        var original = With(new PluginNetworkDestination { Host = "api.example.com", Sends = "Searches for titles" });
        var originalHash = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(original, PluginTrustLevel.Official));
        var sameAgain = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(
            With(new PluginNetworkDestination { Host = "api.example.com", Sends = "Searches for titles", MaxRequestsPerMinute = 30 }), PluginTrustLevel.Official));
        var otherHost = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(
            With(new PluginNetworkDestination { Host = "api.other.example", Sends = "Searches for titles" }), PluginTrustLevel.Official));
        var otherWords = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(
            With(new PluginNetworkDestination { Host = "api.example.com", Sends = "Sends your whole library" }), PluginTrustLevel.Official));
        var extra = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(
            With(
                new PluginNetworkDestination { Host = "api.example.com", Sends = "Searches for titles" },
                new PluginNetworkDestination { Host = "cdn.example.com", Sends = "Downloads pictures" }), PluginTrustLevel.Official));

        Assert.Equal(originalHash, sameAgain);
        Assert.NotEqual(originalHash, otherHost);
        Assert.NotEqual(originalHash, otherWords);
        Assert.NotEqual(originalHash, extra);
    }

    [Fact]
    public void AiFeatureStatement_UsesTheFeatureWordsAndSaysItRunsOnThisMachine()
    {
        var statement = ApprovalStatementBuilder.ForAiFeature("vibe_tags", modelReady: true);

        Assert.Equal("Vibe tags", statement.Title);
        Assert.Equal("Local AI feature", statement.KindLabel);
        Assert.Equal(["Reads descriptions and genres", "Adds mood tags", "Runs on this machine with Local AI"], statement.CanDo);
        Assert.False(statement.SendsDataOut);
        Assert.Null(statement.SendsDataTo);
        Assert.Empty(statement.Notes);
    }

    [Fact]
    public void AiFeatureStatement_WarnsOfAModelDownload_WhenTheModelIsNotOnThisMachine()
    {
        var statement = ApprovalStatementBuilder.ForAiFeature("smart_labeling", modelReady: false);

        Assert.Equal(["May download an AI model to this machine first"], statement.Notes);
    }

    [Fact]
    public void AiFeatureHash_IgnoresTheModelDownloadNote_SoDownloadingTheModelLaterDoesNotAskAgain()
    {
        var ready = ApprovalStatementBuilder.ForAiFeature("smart_labeling", modelReady: true);
        var notReady = ApprovalStatementBuilder.ForAiFeature("smart_labeling", modelReady: false);

        Assert.Equal(["May download an AI model to this machine first"], notReady.Notes);
        Assert.Equal(ApprovalStatementBuilder.Hash(ready), ApprovalStatementBuilder.Hash(notReady));
        Assert.Equal(["May download an AI model to this machine first"], ApprovalStatementBuilder.ToPreview("ai_feature", "smart_labeling", notReady).Notes);
    }

    [Fact]
    public void ApplicationStatement_SaysWhatItsKeysCanDo_AndHowManyExistingKeysItReaches()
    {
        var statement = ApprovalStatementBuilder.ForApplication("Home Assistant", ["Read events", "Read the library"], activeKeyCount: 2);
        var single = ApprovalStatementBuilder.ForApplication("Home Assistant", ["Read the library"], activeKeyCount: 1);
        var none = ApprovalStatementBuilder.ForApplication("Home Assistant", ["Read the library"], activeKeyCount: 0);

        Assert.Equal("Keys for Home Assistant", statement.Title);
        Assert.Equal("Integration", statement.KindLabel);
        Assert.Equal(["Its keys can: Read events, Read the library"], statement.CanDo);
        Assert.True(statement.SendsDataOut);
        Assert.Equal("the software that holds its keys", statement.SendsDataTo);
        Assert.Equal(["Applies to 2 existing keys"], statement.Notes);
        Assert.Equal(["Applies to 1 existing key"], single.Notes);
        Assert.Equal(["Applies to 0 existing keys"], none.Notes);
    }

    [Fact]
    public void ApplicationHash_DoesNotDependOnTheOrderPermissionsAreGivenIn_ButChangesWhenOneIsAdded()
    {
        var one = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForApplication("A", ["x", "y"], 1));
        var reordered = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForApplication("A", ["y", "x"], 1));
        var added = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForApplication("A", ["x", "y", "z"], 1));

        Assert.Equal(one, reordered);
        Assert.NotEqual(one, added);
    }

    [Fact]
    public void CredentialStatement_ListsThePermissionsByTheirNamesAndTheExpiry()
    {
        var expiring = ApprovalStatementBuilder.ForCredential(
            "Home Assistant", ["Read the library", "Read events"], new DateTimeOffset(2027, 3, 5, 14, 0, 0, TimeSpan.Zero));
        var forever = ApprovalStatementBuilder.ForCredential("Home Assistant", ["Read the library"], null);

        Assert.Equal("Key for Home Assistant", expiring.Title);
        Assert.Equal("Application key", expiring.KindLabel);
        Assert.Equal(["Read the library", "Read events", "Expires on 5 March 2027"], expiring.CanDo);
        Assert.Equal(["Read the library", "Never expires"], forever.CanDo);
        Assert.True(expiring.SendsDataOut);
        Assert.Equal("the software that holds this key", expiring.SendsDataTo);
        Assert.Equal(["Anyone with this key can do the above until it is revoked."], expiring.Notes);
    }

    [Fact]
    public void WebhookStatement_ShowsTheEventsAndTheReceiversHostOnly()
    {
        var statement = ApprovalStatementBuilder.ForWebhook(
            new Uri("https://hooks.example.com:8443/secret/path?token=abc"), ["ingestion.completed", "backup.finished"], allowLocalNetwork: false);

        Assert.Equal("Webhook to hooks.example.com", statement.Title);
        Assert.Equal("Webhook", statement.KindLabel);
        Assert.Equal(["Receives these events: backup.finished, ingestion.completed"], statement.CanDo);
        Assert.True(statement.SendsDataOut);
        Assert.Equal("hooks.example.com", statement.SendsDataTo);
        Assert.Empty(statement.Notes);
        var shown = System.Text.Json.JsonSerializer.Serialize(ApprovalStatementBuilder.ToPreview("webhook", "", statement));
        Assert.DoesNotContain("secret", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("token", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("8443", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void WebhookStatement_SaysSoWhenItMayReachADeviceOnTheHomeNetwork()
    {
        var statement = ApprovalStatementBuilder.ForWebhook(new Uri("http://192.168.1.20/hook"), ["x"], allowLocalNetwork: true);

        Assert.Equal(["Allowed to reach a device on your home network"], statement.Notes);
    }

    [Fact]
    public void StatementHash_IsStableForTheSameWords_AndChangesWhenAPermissionIsAdded()
    {
        var before = new PluginManifest { Id = "p", Name = "P", Permissions = ["media.read"] };
        var after = new PluginManifest { Id = "p", Name = "P", Permissions = ["media.read", "network.http"] };

        var first = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(before, PluginTrustLevel.Official));
        var again = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(before, PluginTrustLevel.Official));
        var added = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(after, PluginTrustLevel.Official));

        Assert.Equal(first, again);
        Assert.NotEqual(first, added);
        Assert.Equal(64, first.Length);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void StatementHash_ChangesWhenTheTrustNoteOrTheDataDestinationChanges()
    {
        var manifest = new PluginManifest { Id = "p", Name = "P" };

        var official = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(manifest, PluginTrustLevel.Official));
        var community = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(manifest, PluginTrustLevel.Community));
        var local = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForWebhook(new Uri("https://a.example/x"), ["e"], false));
        var other = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForWebhook(new Uri("https://b.example/x"), ["e"], false));

        Assert.NotEqual(official, community);
        Assert.NotEqual(local, other);
    }
}
