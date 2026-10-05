using System.Text.Json;
using MediaEngine.Domain.Configuration;
using ContractPipeline = MediaEngine.Contracts.Settings.PipelineConfiguration;
using MediaEngine.Contracts.Search;

namespace MediaEngine.Contracts.Tests;

public sealed class RetailMatrixContractTests
{
    [Fact]
    public void SettingsRoundTripPreservesEveryMatrixRule()
    {
        var source = new PipelineConfiguration();
        source.Pipelines["Books"] = new() { Scoring = new() { Scopes = new()
        {
            ["default"] = new() {
                Fields = new() { ["title"] = new() { Weight = .5, IfMissing = "zero" }, ["author"] = new() { Weight = .5, IfMissing = "zero-if-file-has" } },
                Gates = ["format", "not_derivative"], Bonuses = new() { ["exact_id"] = .35 }, Penalties = new() { ["language"] = .1 }
            }
        } } };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var contract = JsonSerializer.Deserialize<ContractPipeline>(JsonSerializer.Serialize(source, options), options)!;
        var restored = JsonSerializer.Deserialize<PipelineConfiguration>(JsonSerializer.Serialize(contract, options), options)!;
        Assert.Equal(JsonSerializer.Serialize(source, options), JsonSerializer.Serialize(restored, options));
    }

    [Fact]
    public void FieldExplanationPreservesNullScoreAndEngineVerdict()
    {
        var source = new FieldMatchScoresDto { FieldScores = [new FieldScoreDto {
            Key = "author", Label = "Author", Score = null, Weight = .35, Missing = true,
            Role = "weighted", MissingPolicy = "zero-if-file-has", Verdict = "required_missing", LocalValue = "Known author"
        }] };
        var json = JsonSerializer.Serialize(source);
        Assert.Contains("\"field_scores\"", json);
        Assert.Contains("\"missing_policy\"", json);
        var restored = JsonSerializer.Deserialize<FieldMatchScoresDto>(json)!;
        var field = Assert.Single(restored.FieldScores!);
        Assert.Null(field.Score);
        Assert.Equal("required_missing", field.Verdict);
        Assert.Equal("Known author", field.LocalValue);
    }
}
