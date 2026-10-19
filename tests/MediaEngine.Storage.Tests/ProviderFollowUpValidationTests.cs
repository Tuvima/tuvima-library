using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Storage.Tests;

/// <summary>A bad shape or an unknown property in a provider's follow-up lookups fails when the file loads.</summary>
public sealed class ProviderFollowUpValidationTests
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private const string GoodRule = """{ "claim_key": "description", "json_path": "overview", "confidence": 0.8 }""";

    private static IReadOnlyList<string> Validate(string followUpsJson)
    {
        var json = $$"""{ "name": "sample", "enabled": true, "weight": 1, "follow_ups": {{followUpsJson}} }""";
        var provider = JsonSerializer.Deserialize<ProviderConfiguration>(json, s_json)!;
        return JsonConfigValidator.Validate(provider, "providers/sample.json");
    }

    private static string FollowUp(string rules, string extra = "") => $$"""
        [{ "name": "detail", "inputs": { "id": ["claim:sample_id"] },
           "url": "{base_url}/thing/{id}?api_key={api_key}", {{extra}} "rules": [{{rules}}] }]
        """;

    [Fact]
    public void EveryShippedProviderFile_Validates()
    {
        var directory = Path.Combine(FindRepoRoot(), "config", "providers");
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var provider = JsonSerializer.Deserialize<ProviderConfiguration>(File.ReadAllText(file), s_json)!;

            var errors = JsonConfigValidator.Validate(provider, $"providers/{Path.GetFileName(file)}");

            Assert.True(errors.Count == 0, $"{Path.GetFileName(file)}: {string.Join("; ", errors)}");
        }
    }

    [Fact]
    public void WellFormedFollowUp_HasNoErrors() =>
        Assert.Empty(Validate(FollowUp(GoodRule)));

    [Fact]
    public void UnknownRuleProperty_IsReportedWithItsPath()
    {
        var errors = Validate(FollowUp("""{ "claim_key": "x", "json_path": "y", "confidence": 0.5, "json_pth": "z" }"""));

        Assert.Contains("follow_ups['detail'].rules[0].json_pth is not a known property.", errors);
    }

    [Fact]
    public void UnknownFollowUpProperty_IsReported()
    {
        var errors = Validate(FollowUp(GoodRule, """ "retries": 3, """));

        Assert.Contains("follow_ups['detail'].retries is not a known property.", errors);
    }

    [Fact]
    public void UnknownRuleKind_ListsTheValidKinds()
    {
        var errors = Validate(FollowUp("""{ "kind": "magic" }"""));

        Assert.Contains(errors, error => error.Contains("kind 'magic' must be one of", StringComparison.Ordinal));
    }

    [Fact]
    public void PropertyFromAnotherKind_IsRejected()
    {
        var errors = Validate(FollowUp("""{ "claim_key": "x", "json_path": "y", "confidence": 0.5, "path": "z" }"""));

        Assert.Contains("follow_ups['detail'].rules[0].path is not valid for a field rule.", errors);
    }

    [Fact]
    public void UrlPlaceholderThatNothingDeclares_IsRejected()
    {
        var errors = Validate("""
            [{ "name": "detail", "url": "{base_url}/x/{missing}", "rules": [ { "claim_key": "x", "json_path": "y", "confidence": 0.5 } ] }]
            """);

        Assert.Contains(errors, error => error.Contains("{missing}", StringComparison.Ordinal));
    }

    [Fact]
    public void InputSourceWithoutAKnownKind_IsRejected()
    {
        var errors = Validate("""
            [{ "name": "detail", "inputs": { "id": ["somewhere:id", "claim"] }, "url": "{base_url}/{id}",
               "rules": [ { "claim_key": "x", "json_path": "y", "confidence": 0.5 } ] }]
            """);

        Assert.Equal(2, errors.Count(error => error.Contains("must look like kind:name", StringComparison.Ordinal)));
    }

    [Fact]
    public void DuplicateFollowUpNames_AreRejected()
    {
        var errors = Validate($$"""
            [{ "name": "a", "url": "{base_url}/x", "rules": [{{GoodRule}}] },
             { "name": "A", "url": "{base_url}/y", "rules": [{{GoodRule}}] }]
            """);

        Assert.Contains("follow_ups[].name values must be unique.", errors);
    }

    [Theory]
    [InlineData("""{ "claim_key": "x", "confidence": 0.5 }""", "needs json_path, json_paths, template, value or join")]
    [InlineData("""{ "claim_key": "x", "json_path": "a", "template": "{a}", "confidence": 0.5 }""", "only one of json_path")]
    [InlineData("""{ "claim_key": "x", "json_path": "a", "confidence": 0 }""", "confidence must be greater than 0")]
    [InlineData("""{ "claim_key": "x", "json_path": "a", "confidence": 1.5 }""", "at most 1")]
    [InlineData("""{ "claim_key": "{role}", "json_path": "a", "confidence": 0.5 }""", "{role} outside an items rule")]
    [InlineData("""{ "claim_key": "x", "json_path": "a", "confidence": 0.5, "add": "sometimes" }""", "add must be always")]
    public void FieldRuleShapes_AreChecked(string rule, string expected)
    {
        var errors = Validate(FollowUp(rule));

        Assert.Contains(errors, error => error.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void ItemsRuleWithoutChildrenOrWithBadSort_IsRejected()
    {
        var errors = Validate(FollowUp("""
            { "kind": "items", "path": "cast", "sort": [ { "path": "n", "type": "date", "null_as": 3 } ] }
            """));

        Assert.Contains(errors, error => error.Contains("needs at least one of each, first and joined", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("sort[0].type must be number or text", StringComparison.Ordinal));
    }

    [Fact]
    public void SequenceThatReadsPositionNeedsACurrentId()
    {
        var errors = Validate(FollowUp("""
            { "kind": "sequence", "items_path": "parts", "item": { "id_path": "id", "title_paths": ["title"] },
              "emit": [ { "claim_key": "p", "from": "position", "confidence": 0.9 } ] }
            """));

        Assert.Contains(errors, error => error.Contains("current_id is required", StringComparison.Ordinal));
    }

    [Fact]
    public void MemoryKeyOnTheResponseCache_AndLookupsNestedTooDeep_AreRejected()
    {
        var nested = """{ "kind": "lookup", "lookup": { "name": "l1", "url": "{base_url}/1", "rules": [ { "kind": "lookup", "lookup": { "name": "l2", "url": "{base_url}/2", "rules": [ { "kind": "lookup", "lookup": { "name": "l3", "url": "{base_url}/3", "rules": [ {{GOOD}} ] } } ] } } ] } }"""
            .Replace("{{GOOD}}", GoodRule, StringComparison.Ordinal);

        var errors = Validate(FollowUp(nested, """ "cache": { "store": "response_cache", "key": "k" }, """));

        Assert.Contains(errors, error => error.Contains("cache.key is only valid for the memory store", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("nested more than 3 lookups deep", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadingAProviderFileWithABadFollowUp_FailsWithTheErrorList()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tuvima-followup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "providers"));
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "providers", "sample.json"),
                """{ "name": "sample", "enabled": true, "weight": 1, "follow_ups": [ { "name": "x", "url": "{base_url}/a", "rules": [ { "kind": "magic" } ] } ] }""");
            using var loader = new ConfigurationDirectoryLoader(directory);

            var failure = Assert.Throws<ConfigValidationException>(() => loader.LoadProvider("sample"));

            Assert.Contains("kind 'magic'", string.Join(' ', failure.ValidationMessages), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Could not find repository root.");
    }
}
