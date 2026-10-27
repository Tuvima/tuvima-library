using System.Text.Json;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Tests;

/// <summary>The shared declaration block (Plugin architecture spec PA1): reading, refusing, approving and showing it.</summary>
public sealed class PluginDeclarationTests
{
    private static PluginManifest Manifest(string declarationJson, string extra = "") =>
        JsonSerializer.Deserialize<PluginManifest>($$"""{ "id": "acme.helper", "name": "Helper", "media_types": ["music"] {{extra}}, "declaration": {{declarationJson}} }""")!;

    [Fact]
    public void AManifestWithoutADeclaration_IsAdministratorsOnlyAndLocalOnly()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>("""{ "id": "acme.plain", "name": "Plain" }""")!;

        Assert.Null(manifest.Declaration);
        Assert.Equal(PluginAudience.Administrators, manifest.ResolvedDeclaration.Audience);
        Assert.Equal(PluginVaultUse.LocalOnly, manifest.ResolvedDeclaration.VaultUse);
        Assert.Null(PluginCompatibility.ValidateManifest(manifest));
    }

    [Fact]
    public void ADeclaration_IsReadFromSnakeCaseJson()
    {
        var manifest = Manifest("""{ "audience": "everyone", "weight_class": "heavy", "requires": [{ "kind": "local_ai", "id": "text_fast" }], "stores": { "saved_output": "skip markers", "on_removal": "remove" } }""");

        Assert.Equal(PluginAudience.Everyone, manifest.ResolvedDeclaration.Audience);
        Assert.Equal(PluginWeightClass.Heavy, manifest.ResolvedDeclaration.WeightClass);
        Assert.True(manifest.ResolvedDeclaration.RemoveOutputOnRemoval);
        Assert.Equal("text_fast", manifest.Declaration!.Requires.Single().Id);
        Assert.Null(PluginCompatibility.ValidateManifest(manifest));
    }

    [Fact]
    public void ADeclarationWithAProblem_IsRefusedWithAPlainSentence()
    {
        var problem = PluginCompatibility.ValidateManifest(Manifest("""{ "audience": "kids" }"""));

        Assert.NotNull(problem);
        Assert.Contains("audience", problem);
    }

    [Fact]
    public void ThePluginSendingDataOut_FollowsTheVaultSectionsOnlineLookup()
    {
        var manifest = Manifest("""{ "audience": "everyone" }""", """, "permissions": ["network.http"], "network": [{ "host": "lrclib.net", "sends": "Song titles" }]""");

        Assert.Equal(PluginVaultUse.FollowsSectionLookup, manifest.ResolvedDeclaration.VaultUse);
    }

    [Fact]
    public void ChangingTheAudience_ChangesTheApprovalFingerprint_SoItAsksAgain()
    {
        var admins = Manifest("""{ "audience": "administrators" }""");
        var everyone = Manifest("""{ "audience": "everyone" }""");

        var before = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(admins, PluginTrustLevel.Official));
        var after = ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(everyone, PluginTrustLevel.Official));

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AddingAnActionOrDisplaySlot_ChangesTheApprovalFingerprint()
    {
        var plain = Manifest("""{ "audience": "everyone" }""");
        var withAction = Manifest("""{ "audience": "everyone", "actions": [{ "id": "find-lyrics", "kind": "find_lyrics", "label": "Look up lyrics" }] }""");
        var withSlot = Manifest("""{ "audience": "everyone", "display_slots": ["home_hero"] }""");

        string Hash(PluginManifest manifest) => ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(manifest, PluginTrustLevel.Official));

        Assert.NotEqual(Hash(plain), Hash(withAction));
        Assert.NotEqual(Hash(plain), Hash(withSlot));
    }

    [Fact]
    public void TheApprovalStatement_SaysWhoCanUseItAndWhatItDoesWithTheVault()
    {
        var lines = ApprovalStatementBuilder.PluginAllowedTo(Manifest("""{ "audience": "adults", "actions": [{ "id": "find-lyrics", "kind": "find_lyrics", "label": "Look up lyrics", "audience": "everyone" }] }"""));

        Assert.Contains("Can be used by adults", lines);
        Assert.Contains("Vault items: Allowed (local only)", lines);
        // An action can narrow its item's audience but never widen it.
        Assert.Contains("Adds the action 'Look up lyrics' (find lyrics) for adults", lines);
    }

    [Theory]
    [InlineData(0, false, null, "Nothing leaves this machine")]
    [InlineData(1, false, "Sends titles and years", "Sends titles and years to api.example.com")]
    [InlineData(1, false, "Song titles and artists", "Sends song titles and artists to api.example.com")]
    [InlineData(1, true, "Song titles", "Sends your key and searches to api.example.com")]
    [InlineData(2, false, "Song titles", "Sends titles and details to api.example.com, other.example.com")]
    public void TheDataLeavingLabel_IsWorkedOutFromTheNetworkListAndKeys(int hostCount, bool usesKey, string? sends, string expected)
    {
        var destinations = new[] { "api.example.com", "other.example.com" }.Take(hostCount).Select(host => (host, sends)).ToList();

        Assert.Equal(expected, PluginAccessMapper.DataLeavingLabel(destinations, usesKey));
    }

    [Fact]
    public void AnActionIsShownWithItsEffectiveAudienceAndDesktopOnlyRule()
    {
        var declaration = new PluginDeclaration
        {
            Audience = "adults",
            Actions =
            [
                new PluginActionDeclaration { Id = "refresh", Kind = "refresh_details", Label = "Refresh", Audience = "everyone", Priority = 10 },
                new PluginActionDeclaration { Id = "find-lyrics", Kind = "find_lyrics", Label = "Look up lyrics", Priority = 5 },
            ],
        };

        var access = PluginAccessMapper.Map(declaration, [], false, ["Music"], ["music"], "metadata");

        Assert.Equal(["find-lyrics", "refresh"], access.Actions.Select(action => action.Id));
        Assert.All(access.Actions, action => Assert.Equal("adults", action.Audience));
        Assert.False(access.Actions[0].Edits);
        Assert.True(access.Actions[1].Edits);
        Assert.Equal("Nothing leaves this machine", access.DataLeaving);
    }
}
