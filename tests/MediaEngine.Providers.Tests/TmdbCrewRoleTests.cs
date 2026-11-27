using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Configuration;
using MediaEngine.Providers.Adapters;


namespace MediaEngine.Providers.Tests;

/// <summary>
/// A film's credited crew jobs are matched whole, so a Music Supervisor is never the composer and an Assistant Director is never the director.
/// </summary>
public sealed class TmdbCrewRoleTests
{
    private static ClaimRoleConfiguration TmdbCrewRole()
    {
        var dir = Path.GetDirectoryName(typeof(TmdbCrewRoleTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "config", "providers", "tmdb.json")),
            documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })!;
        var crew = Find(root, "credits.crew") ?? throw new InvalidOperationException("tmdb.json has no credits.crew rule.");
        return crew["role"]!.Deserialize<ClaimRoleConfiguration>()!;
    }

    private static JsonObject? Find(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["path"]?.GetValue<string>() == path && obj["role"] is not null)
                {
                    return obj;
                }

                return obj.Select(pair => Find(pair.Value, path)).FirstOrDefault(found => found is not null);
            case JsonArray array:
                return array.Select(item => Find(item, path)).FirstOrDefault(found => found is not null);
            default:
                return null;
        }
    }

    [Theory]
    [InlineData("Director", "director")]
    [InlineData("Assistant Director", null)]
    [InlineData("Second Unit Director", null)]
    [InlineData("Original Music Composer", "composer")]
    [InlineData("Music", "composer")]
    [InlineData("Music Supervisor", null)]
    [InlineData("Music Editor", null)]
    [InlineData("Screenplay", "screenwriter")]
    [InlineData("Storyboard Artist", null)]
    public void CrewJobs_AreMatchedWhole(string job, string? expectedClaim)
    {
        var item = new JsonObject { ["job"] = job };

        Assert.Equal(expectedClaim, ClaimRuleEngine.ResolveRole(item, TmdbCrewRole()));
    }
}
