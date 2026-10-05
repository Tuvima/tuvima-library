using MediaEngine.Domain.Configuration;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Storage.Tests;

public sealed class RetailMatrixValidationTests
{
    private static Dictionary<string, MediaTypePipeline> Valid() => new() { ["Books"] = new() { Scoring = new() { Scopes = new()
    {
        ["default"] = new() { Fields = new() { ["title"] = new() { Weight = .5, IfMissing = "zero" }, ["author"] = new() { Weight = .5, IfMissing = "zero-if-file-has" } }, Gates = ["format"] }
    } } } };

    [Fact]
    public void ValidMatrixPasses() => Assert.Empty(JsonConfigValidator.Validate(Valid(), "pipelines.json"));

    [Theory]
    [InlineData("weight")]
    [InlineData("missing")]
    [InlineData("genre")]
    [InlineData("gate")]
    [InlineData("bonus")]
    [InlineData("scope")]
    public void InvalidMatrixIsRejected(string defect)
    {
        var config = Valid(); var scopes = config["Books"].Scoring.Scopes; var matrix = scopes["default"];
        switch (defect) {
            case "weight": matrix.Fields["title"].Weight = .4; break;
            case "missing": matrix.Fields["author"].IfMissing = "ignore"; break;
            case "genre": matrix.Bonuses["genre"] = .1; break;
            case "gate": matrix.Gates.Clear(); break;
            case "bonus": matrix.Bonuses["exact_id"] = double.NaN; break;
            case "scope": scopes.Clear(); break;
        }
        Assert.NotEmpty(JsonConfigValidator.Validate(config, "pipelines.json"));
    }
}
