using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Tests;

public sealed class PluginDeclarationRulesTests
{
    [Fact]
    public void NoDeclaration_IsAdministratorsOnly_LightQuietAndKeepsOutputOnRemoval()
    {
        var resolved = PluginDeclarationRules.Resolve(null, sendsDataOut: false, mediaTypes: null, category: null);

        Assert.Equal(PluginAudience.Administrators, resolved.Audience);
        Assert.False(resolved.AudienceStated);
        Assert.Equal(PluginWeightClass.Light, resolved.WeightClass);
        Assert.False(resolved.TellMeOnFailure);
        Assert.True(resolved.RetryOnFailure);
        Assert.False(resolved.RemoveOutputOnRemoval);
        Assert.Empty(resolved.MediaTypes);
        Assert.Equal("utilities", resolved.Category);
    }

    [Theory]
    [InlineData(false, PluginVaultUse.LocalOnly)]
    [InlineData(true, PluginVaultUse.FollowsSectionLookup)]
    public void VaultUse_IsWorkedOutFromWhereTheItemSendsData(bool sendsDataOut, PluginVaultUse expected)
    {
        var resolved = PluginDeclarationRules.Resolve(new PluginDeclaration { Audience = "everyone" }, sendsDataOut, ["music"], "metadata");

        Assert.Equal(expected, resolved.VaultUse);
        Assert.Equal(PluginAudience.Everyone, resolved.Audience);
        Assert.True(resolved.AudienceStated);
    }

    [Fact]
    public void VaultWordsNeverAndOwnSource_OverrideTheDerivedRule()
    {
        Assert.Equal(PluginVaultUse.Never, PluginDeclarationRules.Resolve(new PluginDeclaration { Vault = "never" }, false, null, null).VaultUse);
        Assert.Equal(PluginVaultUse.OwnSource, PluginDeclarationRules.Resolve(new PluginDeclaration { Vault = "own_source" }, true, null, null).VaultUse);
    }

    [Fact]
    public void ADeclarationThatStatesNothingWrong_HasNoProblems()
    {
        var declaration = new PluginDeclaration
        {
            Audience = "adults",
            WeightClass = "heavy",
            OnFailure = "skip",
            Notify = "tell_me",
            Changelog = "Now reads chapter names first.",
            Languages = ["en", "pt-BR"],
            DisplaySlots = ["home_hero", "item_section"],
            Requires = [new PluginRequirement { Kind = "local_ai", Id = "text_fast", Optional = true }, new PluginRequirement { Kind = "plugin", Id = "tuvima.skip-markers", VersionRange = "1.0.0-2.0.0" }],
            Stores = new PluginStores { Records = true, SavedOutput = "skip markers", OnRemoval = "remove" },
            Actions = [new PluginActionDeclaration { Id = "find-lyrics", Kind = "find_lyrics", Label = "Look up lyrics", Slot = "player_core" }],
            TextTrack = "lyrics",
            CredentialScopes = { ["token"] = "personal" },
        };

        Assert.Empty(PluginDeclarationRules.Validate(declaration));
    }

    [Fact]
    public void Validate_NamesEveryProblem()
    {
        var declaration = new PluginDeclaration
        {
            Audience = "kids",
            Vault = "yes",
            WeightClass = "huge",
            OnFailure = "explode",
            Notify = "loud",
            Changelog = new string('x', 201),
            TextTrack = "video",
            Languages = ["english!"],
            DisplaySlots = ["everywhere"],
            Requires = [new PluginRequirement { Kind = "wish", Id = "x" }, new PluginRequirement { Kind = "plugin", Id = "a", VersionRange = "new" }],
            Actions =
            [
                new PluginActionDeclaration { Id = "Bad Id", Kind = "dance", Label = "", Slot = "toolbar", Priority = 500 },
                new PluginActionDeclaration { Id = "rescan", Kind = "rescan_skip_markers", Label = "Re-scan", Slot = "player_core" },
            ],
            CredentialScopes = { ["token"] = "shared" },
        };

        var problems = string.Join("\n", PluginDeclarationRules.Validate(declaration));

        Assert.Contains("'audience'", problems);
        Assert.Contains("'vault'", problems);
        Assert.Contains("'weight_class'", problems);
        Assert.Contains("'on_failure'", problems);
        Assert.Contains("'notify'", problems);
        Assert.Contains("'changelog'", problems);
        Assert.Contains("'text_track'", problems);
        Assert.Contains("english!", problems);
        Assert.Contains("everywhere", problems);
        Assert.Contains("kind 'wish'", problems);
        Assert.Contains("version_range", problems);
        Assert.Contains("Bad Id", problems);
        Assert.Contains("priority must be 0 to 100", problems);
        Assert.Contains("core player button", problems);
        Assert.Contains("household or personal", problems);
    }

    [Fact]
    public void ApluginCannotAskForALooseCorePlayerButton()
    {
        var declaration = new PluginDeclaration
        {
            Actions = [new PluginActionDeclaration { Id = "scan", Kind = "refresh_lore", Label = "Scan", Slot = "player_core" }],
        };

        Assert.Contains(PluginDeclarationRules.Validate(declaration), problem => problem.Contains("core player button"));
    }

    [Fact]
    public void AnActionCanNarrowButNeverWidenTheItemsAudience()
    {
        var narrower = new PluginActionDeclaration { Audience = "administrators" };
        var wider = new PluginActionDeclaration { Audience = "everyone" };

        Assert.Equal(PluginAudience.Administrators, PluginDeclarationRules.ActionAudience(narrower, PluginAudience.Adults));
        Assert.Equal(PluginAudience.Adults, PluginDeclarationRules.ActionAudience(wider, PluginAudience.Adults));
        Assert.Equal(PluginAudience.Everyone, PluginDeclarationRules.ActionAudience(new PluginActionDeclaration(), PluginAudience.Everyone));
    }

    [Theory]
    [InlineData("find_lyrics", false)]
    [InlineData("find_subtitles", false)]
    [InlineData("rescan_skip_markers", false)]
    [InlineData("refresh_details", true)]
    public void LookupsAreNotEdits_UnlessTheActionSaysSo(string kind, bool edits)
    {
        Assert.Equal(edits, PluginDeclarationRules.ActionEdits(new PluginActionDeclaration { Kind = kind }));
        Assert.True(PluginDeclarationRules.ActionEdits(new PluginActionDeclaration { Kind = kind, Edits = true }));
    }
}
