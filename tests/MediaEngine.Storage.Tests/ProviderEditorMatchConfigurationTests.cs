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
    [InlineData("tmdb", "movie", true)]
    [InlineData("tmdb", "tv_episode", false)]
    [InlineData("tvdb", "tv_episode", true)]
    [InlineData("tvdb", "movie", false)]
    [InlineData("youtube", "video", true)]
    [InlineData("youtube", "movie", false)]
    [InlineData("musicbrainz", "song", true)]
    [InlineData("musicbrainz", "book", false)]
    public void EachProvider_ListsTheKindsItIsOfferedFor_SoNoCodeNamesAProvider(string provider, string kind, bool offered) =>
        Assert.Equal(offered, Shipped(provider).EditorMatch!.Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase));

    [Theory]
    [InlineData("apple_api", "isbn", "isbn")]
    [InlineData("comicvine", "comicvine", "comic_vine_id")]
    [InlineData("musicbrainz", "isrc", "isrc")]
    [InlineData("musicbrainz", "musicbrainz", "musicbrainz_recording_id")]
    public void AProviderThatCanLookAnIdentifierUp_NamesTheSearchFieldItNeeds(string provider, string identifier, string field) =>
        Assert.Equal(field, Shipped(provider).EditorMatch!.IdLookup[identifier]);

    [Theory]
    [InlineData("tmdb")]
    [InlineData("tvdb")]
    [InlineData("openlibrary")]
    [InlineData("youtube")]
    public void AProviderWithNoIdLookup_TakesATypedIdentifierAsEvidenceOnly(string provider) =>
        Assert.Empty(Shipped(provider).EditorMatch!.IdLookup);

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
