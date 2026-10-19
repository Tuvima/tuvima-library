using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Storage.Tests;

/// <summary>Bad shapes in a subtitle or lyrics provider's text_track block are reported when the file loads.</summary>
public sealed class ProviderTextTrackValidationTests
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
            File.ReadAllText(Path.Combine(dir!, "config", "providers", $"{name}.json")), s_json)!;
    }

    private static IReadOnlyList<string> Errors(string name, Action<ProviderConfiguration> edit)
    {
        var config = Shipped(name);
        edit(config);
        return JsonConfigValidator.Validate(config, $"providers/{name}.json");
    }

    [Theory]
    [InlineData("subdl")]
    [InlineData("lrclib")]
    public void ShippedFiles_HaveNoErrors(string name) =>
        Assert.Empty(Errors(name, _ => { }));

    [Fact]
    public void TextTrackAdapterWithoutABlock_IsRejected()
    {
        var errors = Errors("subdl", config => config.TextTrack = null);

        Assert.Contains("text_track is required when adapter_type is text_track.", errors);
    }

    [Fact]
    public void UnknownProperty_IsReportedWithItsPath()
    {
        var json = JsonSerializer.Serialize(Shipped("subdl"), s_json)
            .Replace("\"empty_search_minutes\"", "\"empty_minutes\"", StringComparison.Ordinal);
        var config = JsonSerializer.Deserialize<ProviderConfiguration>(json, s_json)!;

        var errors = JsonConfigValidator.Validate(config, "providers/subdl.json");

        Assert.Contains("text_track.search.empty_minutes is not a known property.", errors);
    }

    [Fact]
    public void RequestShapes_AreChecked()
    {
        var errors = Errors("subdl", config =>
        {
            var request = config.TextTrack!.Search.Requests[0];
            request.Path = "api/search";
            request.Needs.Add("ghost");
            request.Query.Add(["only-one"]);
            request.Query.Add(["x", "{nothing}"]);
            request.WhenMediaType = "Cinema";
            request.TitleCheck!.Id = "{nothing}";
        });

        Assert.Contains(errors, error => error.Contains("path must start with '/'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unknown lookup value 'ghost'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("query entries must be [name, value] pairs", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("query uses {nothing}", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("'Cinema' is not a media type", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("title_check.id uses {nothing}", StringComparison.Ordinal));
    }

    [Fact]
    public void DownloadRules_AreChecked()
    {
        var errors = Errors("subdl", config =>
        {
            var download = config.TextTrack!.Download;
            download.AllowedHosts.Clear();
            download.MaxBytes = 0;
            download.PathPrefix = "subtitle/";
            download.Resolve!.Path = "/download";
            download.Archive!.MaxEntries = 0;
        });

        Assert.Contains(errors, error => error.Contains("download.allowed_hosts must list at least one host", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("max_bytes must be greater than 0", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("path_prefix must start with '/'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("resolve.path must start with '/' and contain {id}", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("archive.max_entries and max_expansion_ratio", StringComparison.Ordinal));
    }

    [Fact]
    public void ErrorMappingAndMessages_AreChecked()
    {
        var errors = Errors("subdl", config =>
        {
            config.TextTrack!.Errors.Statuses[0].Status = "Gone";
            config.TextTrack.Errors.Statuses[1].ErrorCode = "x";
            config.TextTrack.Errors.ErrorCodePath = null;
            config.TextTrack.Errors.NotOutageCodes.Add(42);
            config.TextTrack.Messages["mystery"] = "?";
        });

        Assert.Contains(errors, error => error.Contains("statuses[].status must be one of", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("error_code needs error_code_path", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("not_outage_codes must be HTTP status codes", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("messages['mystery']", StringComparison.Ordinal));
    }

    [Fact]
    public void KeyedProviderWithoutAllowedHosts_IsRejected()
    {
        var errors = Errors("subdl", config => config.TextTrack!.Api.AllowedHosts.Clear());

        Assert.Contains(errors, error => error.Contains("api.allowed_hosts must list at least one host when requires_api_key is true", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://api.subdl.com")]
    [InlineData("https://api.subdl.com:8443")]
    [InlineData("https://user:secret@api.subdl.com")]
    [InlineData("https://other.example")]
    public void KeyedProviderWithAnUnsafeApiAddress_IsRejected(string address)
    {
        var errors = Errors("subdl", config => config.Endpoints["api"] = address);

        Assert.Contains(errors, error => error.Contains("endpoints['api'] must be an HTTPS address", StringComparison.Ordinal));
    }

    [Fact]
    public void LyricsRules_AreChecked()
    {
        var errors = Errors("lrclib", config =>
        {
            config.TextTrack!.Search.Lyrics.Sources.Clear();
            config.TextTrack.Search.Lyrics.Duration.RejectBeyondSeconds = 1;
            config.TextTrack.Search.Lyrics.Duration.FullWithinSeconds = 5;
        });

        Assert.Contains(errors, error => error.Contains("lyrics.sources must list at least one source", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("lyrics.duration needs 0 <= full_within_seconds", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownKind_IsRejected()
    {
        var errors = Errors("lrclib", config => config.TextTrack!.Kind = "Podcasts");

        Assert.Contains(errors, error => error.Contains("kind must be Subtitles or Lyrics", StringComparison.Ordinal));
    }

    [Fact]
    public void SubtitleSearchRules_AreChecked()
    {
        var errors = Errors("subdl", config =>
        {
            var search = config.TextTrack!.Search;
            search.ItemsPath = null;
            search.MaxCandidates = 0;
            search.SourceUrl = "https://x.example/{other}";
            search.Item.IdPaths.Clear();
            search.Item.Files!.Max = 0;
        });

        Assert.Contains(errors, error => error.Contains("items_path is required", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("max_items and max_candidates must be at least 1", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("source_url may only use {id}", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("item.id_paths and language_paths", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("item.files needs max", StringComparison.Ordinal));
    }
}
