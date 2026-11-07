using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Storage.Tests;

/// <summary>Which provider the editor's Match tab offers first for each kind of item is data in the provider files (universal editor v5, D8).</summary>
public sealed class ProviderEditorMatchConfigurationTests
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static ProviderConfiguration Shipped(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return JsonSerializer.Deserialize<ProviderConfiguration>(
            File.ReadAllText(Path.Combine(dir!, "config", "providers", name + ".json")), s_json)!;
    }

    [Theory]
    [InlineData("tmdb", "movie")]
    [InlineData("tvdb", "tv_show")]
    [InlineData("tvdb", "tv_season")]
    [InlineData("tvdb", "tv_episode")]
    [InlineData("openlibrary", "book")]
    [InlineData("apple_api", "audiobook")]
    [InlineData("comicvine", "comic")]
    [InlineData("musicbrainz", "album")]
    [InlineData("musicbrainz", "song")]
    [InlineData("youtube", "video")]
    public void EachKind_HasItsDefaultProvider(string provider, string kind)
    {
        var config = Shipped(provider);

        Assert.Contains(kind, config.EditorMatch?.DefaultFor ?? []);
    }

    [Theory]
    [InlineData("tmdb")]
    [InlineData("tvdb")]
    [InlineData("openlibrary")]
    [InlineData("apple_api")]
    [InlineData("comicvine")]
    [InlineData("musicbrainz")]
    [InlineData("youtube")]
    public void TheShippedFile_HasNoErrors(string provider) =>
        Assert.Empty(JsonConfigValidator.Validate(Shipped(provider), $"providers/{provider}.json"));
}
