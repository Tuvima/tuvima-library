using MediaEngine.Api.Services.Plugins.Files;
using MediaEngine.Api.Services.Plugins.Sources;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The plugin file check (Plugins and Integrations screens v5 WP27, D55): a bad type, a bad range, a locked-field change and a
/// malformed file each give the right line and message, a valid file gives none, and a "thing to know" never blocks.
/// </summary>
public sealed class PluginFileCheckTests
{
    private const string GoodManifest = """
        {
          "id": "my-plugin",
          "name": "My plugin",
          "version": "1.0.0",
          "kind": "declarative",
          "permissions": ["network.http"],
          "network": [
            { "host": "api.example.com", "sends": "Titles you ask about", "max_requests_per_minute": 60 }
          ],
          "declarative": {}
        }
        """;

    [Fact]
    public void AValidFile_GivesNoProblems()
    {
        var result = PluginFileChecker.Check(GoodManifest, PluginFileFormat.Manifest, "plugin.json", baselineText: null);

        Assert.True(result.IsValid);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void ABadType_GivesItsLineAndPlainWords()
    {
        var text = GoodManifest.Replace("\"version\": \"1.0.0\"", "\"version\": 3", StringComparison.Ordinal);

        var problem = Assert.Single(PluginFileChecker.Check(text, PluginFileFormat.Manifest, "plugin.json", null).Problems);

        Assert.Equal(4, problem.Line);
        Assert.Equal(PluginFileSeverity.Error, problem.Severity);
        Assert.Equal(PluginFileProblemCodes.Type, problem.Code);
        Assert.Equal("\"version\" should be text (in quotes), but it is a number.", problem.Message);
    }

    [Fact]
    public void ABadRange_GivesItsLineAndTheLimit()
    {
        var text = GoodManifest.Replace("\"max_requests_per_minute\": 60", "\"max_requests_per_minute\": 5000", StringComparison.Ordinal);

        var problem = Assert.Single(PluginFileChecker.Check(text, PluginFileFormat.Manifest, "plugin.json", null).Problems);

        Assert.Equal(8, problem.Line);
        Assert.Equal(PluginFileProblemCodes.Range, problem.Code);
        Assert.Contains("must be at most 600, but it is 5000", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALibraryRule_PointsAtTheHostItself()
    {
        var text = GoodManifest.Replace("api.example.com", "https://api.example.com/v1", StringComparison.Ordinal);

        var problem = Assert.Single(PluginFileChecker.Check(text, PluginFileFormat.Manifest, "plugin.json", null).Problems);

        Assert.Equal(8, problem.Line);
        Assert.Equal(PluginFileProblemCodes.Rule, problem.Code);
        Assert.Contains("must not include a scheme", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALibraryRuleAboutVersions_PointsAtTheVersionWord()
    {
        var text = GoodManifest.Replace("\"version\": \"1.0.0\",", "\"version\": \"1.0.0\",\n  \"minimum_library_version\": \"soon\",", StringComparison.Ordinal);

        var problem = Assert.Single(PluginFileChecker.Check(text, PluginFileFormat.Manifest, "plugin.json", null).Problems);

        Assert.Equal(5, problem.Line);
        Assert.Contains("minimum_library_version", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(600, true)]
    [InlineData(0, false)]
    [InlineData(601, false)]
    public void TheRequestRateRangeInThePublishedSchema_IsTheOneTheEngineEnforcesOnInstall(int rate, bool allowed)
    {
        Assert.Equal(1, MediaEngine.Plugins.PluginNetworkRules.MinRequestsPerMinute);
        Assert.Equal(600, MediaEngine.Plugins.PluginNetworkRules.MaxRequestsPerMinute);
        var text = GoodManifest.Replace("\"max_requests_per_minute\": 60", $"\"max_requests_per_minute\": {rate}", StringComparison.Ordinal);

        var result = PluginFileChecker.Check(text, PluginFileFormat.Manifest, "plugin.json", null);

        Assert.Equal(allowed, result.IsValid);
    }

    [Fact]
    public void ALockedFieldThatDiffers_IsReportedAsChangedLocked_OnItsLine()
    {
        var text = GoodManifest
            .Replace("\"id\": \"my-plugin\"", "\"id\": \"other\"", StringComparison.Ordinal)
            .Replace("api.example.com", "api.example.net", StringComparison.Ordinal)
            .Replace("[\"network.http\"]", "[\"network.http\", \"process.execute\"]", StringComparison.Ordinal);

        var problems = PluginFileChecker.Check(text, PluginFileFormat.Manifest, "plugin.json", GoodManifest).Problems;

        var locked = problems.Where(problem => problem.Code == PluginFileProblemCodes.ChangedLocked).ToList();
        Assert.Equal(3, locked.Count);
        var id = Assert.Single(locked, problem => problem.Message.StartsWith("\"id\"", StringComparison.Ordinal));
        Assert.Equal(2, id.Line);
        Assert.Contains("in Tuvima's version it is \"my-plugin\"", id.Message, StringComparison.Ordinal);
        Assert.All(locked, problem => Assert.Equal(PluginFileSeverity.Error, problem.Severity));
        var permissions = Assert.Single(locked, problem => problem.Message.Contains("permissions", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(6, permissions.Line);
        var hosts = Assert.Single(locked, problem => problem.Message.Contains("places it may contact", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(7, hosts.Line);
        Assert.Contains("\"api.example.net\"", hosts.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALockedFieldThatIsTheSame_IsNotReported()
    {
        var edited = GoodManifest.Replace("\"My plugin\"", "\"My renamed plugin\"", StringComparison.Ordinal);

        var result = PluginFileChecker.Check(edited, PluginFileFormat.Manifest, "plugin.json", GoodManifest);

        Assert.Empty(result.Problems);
    }

    [Fact]
    public void AMalformedFile_GivesOneSyntaxProblem_OnTheLineWhereItBreaks()
    {
        var text = GoodManifest.Replace("\"name\": \"My plugin\",", "\"name\": \"My plugin\"", StringComparison.Ordinal);

        var problem = Assert.Single(PluginFileChecker.Check(text, PluginFileFormat.Manifest, "plugin.json", null).Problems);

        Assert.Equal(PluginFileProblemCodes.Syntax, problem.Code);
        Assert.Equal(PluginFileSeverity.Error, problem.Severity);
        Assert.Equal(4, problem.Line);
        Assert.False(string.IsNullOrWhiteSpace(problem.Message));
    }

    [Fact]
    public void AFileThatStopsEarly_SaysSo()
    {
        var problem = Assert.Single(PluginFileChecker.Check("{\n  \"id\": \"x\",\n  \"name\": \"X\"", PluginFileFormat.Manifest, "plugin.json", null).Problems);

        Assert.Equal(PluginFileProblemCodes.Syntax, problem.Code);
        Assert.Contains("stops before", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRequiredWord_IsReported()
    {
        var text = GoodManifest.Replace("\"id\": \"my-plugin\",", string.Empty, StringComparison.Ordinal);

        var problem = Assert.Single(PluginFileChecker.Check(text, PluginFileFormat.Manifest, "plugin.json", null).Problems);

        Assert.Equal(PluginFileProblemCodes.Missing, problem.Code);
        Assert.Contains("\"id\" is missing", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyFile_IsAnError()
    {
        var problem = Assert.Single(PluginFileChecker.Check("  ", PluginFileFormat.Manifest, "plugin.json", null).Problems);

        Assert.Equal(1, problem.Line);
        Assert.Contains("empty", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownWordInTheSettingsDescription_IsAThingToKnow_AndNeverBlocks()
    {
        var text = GoodManifest.Replace(
            "\"declarative\": {}",
            "\"declarative\": {},\n  \"settings_schema\": { \"type\": \"object\", \"properties\": { \"wait\": { \"type\": \"integer\", \"sparkle\": true } } }",
            StringComparison.Ordinal);

        var result = PluginFileChecker.Check(text, PluginFileFormat.Manifest, "plugin.json", null);

        var note = Assert.Single(result.Problems);
        Assert.Equal(PluginFileSeverity.Note, note.Severity);
        Assert.Equal(PluginFileProblemCodes.UnknownWord, note.Code);
        Assert.Equal(11, note.Line);
        Assert.True(result.IsValid);
        Assert.Equal(1, result.NoteCount);
        Assert.Equal(0, result.ErrorCount);
    }

    [Fact]
    public void ASourceFileWithAnOutOfRangeWeight_IsReported_OnTheWeightLine()
    {
        var shipped = ShippedProviderFiles.ReadText("tmdb.json")!;
        var text = shipped.Replace("\"weight\": 1,", "\"weight\": 5,", StringComparison.Ordinal);
        var line = text.Split('\n').ToList().FindIndex(row => row.Contains("\"weight\": 5", StringComparison.Ordinal)) + 1;

        var problems = PluginFileChecker.Check(text, PluginFileFormat.SourceFile, "tmdb.json", shipped).Problems;

        var problem = Assert.Single(problems, candidate => candidate.Severity == PluginFileSeverity.Error);
        Assert.Equal(line, problem.Line);
        Assert.Contains("\"weight\" must be at most 1, but it is 5", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALibraryRuleThatTheSchemaCannotSay_StillGivesALineAndAMessage()
    {
        var shipped = ShippedProviderFiles.ReadText("tmdb.json")!;

        // The file is named lrclib.json but says it is tmdb: only Library's own rule knows the two must match.
        var problem = Assert.Single(PluginFileChecker.Check(shipped, PluginFileFormat.SourceFile, "lrclib.json", baselineText: null).Problems);

        Assert.Equal(PluginFileProblemCodes.Rule, problem.Code);
        Assert.Equal(2, problem.Line);
        Assert.Contains("name must match the provider config filename", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceFileWhoseAddressMovesToAnotherHost_IsLocked()
    {
        var shipped = ShippedProviderFiles.ReadText("tmdb.json")!;
        var text = shipped.Replace("api.themoviedb.org", "evil.example.com", StringComparison.Ordinal);

        var problems = PluginFileChecker.Check(text, PluginFileFormat.SourceFile, "tmdb.json", shipped).Problems;

        var locked = Assert.Single(problems, problem => problem.Code == PluginFileProblemCodes.ChangedLocked);
        Assert.Contains("evil.example.com", locked.Message, StringComparison.Ordinal);
        Assert.Contains("api.themoviedb.org", locked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongCacheTime_IsAThingToKnow()
    {
        var shipped = ShippedProviderFiles.ReadText("tmdb.json")!;
        var text = shipped.Replace("\"cache_ttl_hours\": null", "\"cache_ttl_hours\": 2000", StringComparison.Ordinal);

        var result = PluginFileChecker.Check(text, PluginFileFormat.SourceFile, "tmdb.json", shipped);

        Assert.True(result.IsValid);
        Assert.Contains(result.Problems, problem => problem.Severity == PluginFileSeverity.Note && problem.Message.Contains("2000 hours", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryProviderFileThatShipsWithLibrary_IsValid()
    {
        var names = typeof(ShippedProviderFiles).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("MediaEngine.Api.shipped-providers.", StringComparison.Ordinal))
            .Select(name => name["MediaEngine.Api.shipped-providers.".Length..])
            .ToList();
        Assert.NotEmpty(names);

        var failures = new List<string>();
        foreach (var name in names)
        {
            var text = ShippedProviderFiles.ReadText(name)!;
            var errors = PluginFileChecker.Check(text, PluginFileFormat.SourceFile, name, text).Problems.Where(problem => problem.Severity == PluginFileSeverity.Error);
            failures.AddRange(errors.Select(error => $"{name} line {error.Line}: {error.Message}"));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void TheComparison_IgnoresLayoutOrderCommentsAndTheEnabledSwitch_ButSeesARealChange()
    {
        var shipped = ShippedProviderFiles.ReadText("tmdb.json")!;
        var reformatted = shipped.Replace("\"enabled\": true", "\"enabled\": false", StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal);
        var changed = shipped.Replace("\"weight\": 1,", "\"weight\": 0.5,", StringComparison.Ordinal);

        Assert.True(PluginFileComparer.Same(shipped, reformatted, PluginFileComparer.SourceFileChoices));
        Assert.False(PluginFileComparer.Same(shipped, changed, PluginFileComparer.SourceFileChoices));
        Assert.True(PluginFileComparer.Same("{\"a\":1,\"b\":[1,2]}", "{ \"b\": [1, 2], // note\n \"a\": 1.0 }"));
        Assert.False(PluginFileComparer.Same("{\"a\":1}", "{\"a\":2}"));
    }
}
