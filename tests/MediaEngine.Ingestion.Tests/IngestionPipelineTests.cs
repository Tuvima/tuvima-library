using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// Pipeline-level tests validating config loading, URL construction,
/// and file organizer behaviour for the ingestion subsystem.
/// </summary>
public class IngestionPipelineTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // ── All example configs deserialize ───────────────────────────────────

    [Fact]
    public void AllExampleConfigs_Deserialize_Successfully()
    {
        var root = FindRepoRoot();
        var configDir = Path.Combine(root, "config", "providers");
        var files = Directory.GetFiles(configDir, "*.json");

        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var json = File.ReadAllText(file);
            var config = JsonSerializer.Deserialize<ProviderConfiguration>(json, s_jsonOptions);

            Assert.NotNull(config);
            Assert.False(
                string.IsNullOrWhiteSpace(config!.Name),
                $"Provider config {Path.GetFileName(file)} has no name.");
        }
    }

    // ── URL construction from search strategy templates ───────────────────

    [Fact]
    public void ConfigDrivenAdapter_SearchStrategy_BuildsValidUrls()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "config", "providers", "apple_api.json");
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ProviderConfiguration>(json, s_jsonOptions)!;

        // The ebook_search strategy should contain the URL template.
        var strategy = config.SearchStrategies?
            .FirstOrDefault(s => s.Name == "ebook_search");

        Assert.NotNull(strategy);
        Assert.Contains("{base_url}", strategy!.UrlTemplate);
        Assert.Contains("{query}", strategy.UrlTemplate);
        Assert.Contains("entity=ebook", strategy.UrlTemplate);

        // Verify required fields are declared.
        Assert.Contains("title", strategy.RequiredFields);
    }

    // ── FileOrganizer category mapping ────────────────────────────────────

    [Theory]
    [InlineData(MediaType.Books, "Books")]
    [InlineData(MediaType.Audiobooks, "Audiobooks")]
    [InlineData(MediaType.Comics, "Comics")]
    [InlineData(MediaType.Movies, "Movies")]
    public void FileOrganizer_MapsMediaType_ToExpectedCategory(
        MediaType mediaType, string expectedPrefix)
    {
        var organizer = new FileOrganizer(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FileOrganizer>.Instance);

        var candidate = new IngestionCandidate
        {
            Path = @"C:\watch\test.epub",
            EventType = FileEventType.Created,
            DetectedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "Test Title",
                ["author"] = "Test Author",
            },
            DetectedMediaType = mediaType,
        };

        var relative = organizer.CalculatePath(candidate, "{Category}/{Title}{Ext}");

        Assert.StartsWith(expectedPrefix + "/", relative);
    }

    // ── Provider config field weight validation ───────────────────────────

    [Fact]
    public void AllExampleConfigs_HaveValidFieldWeights()
    {
        var root = FindRepoRoot();
        var configDir = Path.Combine(root, "config", "providers");

        foreach (var file in Directory.GetFiles(configDir, "*.json"))
        {
            var json = File.ReadAllText(file);
            var config = JsonSerializer.Deserialize<ProviderConfiguration>(json, s_jsonOptions);

            if (config?.FieldWeights is { Count: > 0 })
            {
                foreach (var (field, weight) in config.FieldWeights)
                {
                    Assert.True(
                        weight is >= 0.0 and <= 1.0,
                        $"{Path.GetFileName(file)}: field_weight '{field}' = {weight} out of [0, 1] range.");
                }
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(IngestionPipelineTests).Assembly.Location);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not find repository root (MediaEngine.slnx)");
    }
}
