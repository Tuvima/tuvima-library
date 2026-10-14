using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tuvima.Wikidata;
using Xunit.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// Parity tests — verify that the manual search path
/// (<c>SearchService.SearchUniverseAsync</c> → <c>WikidataProvider.SearchAsync</c>
/// → <c>ReconciliationAdapter.ReconcileAsync</c>) and the automated pipeline path
/// (<c>WikidataBridgeWorker</c> → <c>ReconciliationAdapter.ReconcileBatchAsync</c>)
/// produce identical top-K QIDs and scores for the same query inputs.
///
/// Manual search must stay separate from automatic bridge-ID resolution so
/// title-only matches are never silently accepted by the pipeline.
///
/// These tests require live network access and must never run in CI.
/// Run locally with: dotnet test --filter "Category=Integration"
/// </summary>
[Trait("Category", "Integration")]
public sealed class WikidataParityTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ReconciliationAdapter _adapter;

    public WikidataParityTests(ITestOutputHelper output)
    {
        _output = output;
        _adapter = BuildAdapter();
    }

    public static IEnumerable<object?[]> Fixtures()
    {
        yield return new object?[]
        {
            "Dune", MediaType.Books,
            new Dictionary<string, string> { ["P50"] = "Frank Herbert" },
        };
        yield return new object?[] { "Neuromancer", MediaType.Books, null };
        yield return new object?[]
        {
            "Foundation", MediaType.Books,
            new Dictionary<string, string> { ["P50"] = "Isaac Asimov" },
        };
        yield return new object?[] { "Breaking Bad", MediaType.TV, null };
        yield return new object?[] { "Shogun", MediaType.TV, null };
        yield return new object?[] { "Star of Edo", MediaType.Books, null };
    }

    [Theory(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    [MemberData(nameof(Fixtures))]
    public async Task ManualAndBatch_ProduceIdenticalTopK(
        string query,
        MediaType mediaType,
        Dictionary<string, string>? constraints)
    {
        // ── Manual path (single ReconcileAsync) ─────────────────────────────
        var manual = await _adapter.ReconcileAsync(query, constraints, mediaType: mediaType);

        // ── Pipeline batch path (ReconcileBatchAsync) ───────────────────────
        var batchInput = new List<(string QueryId, string Query, Dictionary<string, string>? PropertyConstraints, MediaType MediaType)>
        {
            (query, query, constraints, mediaType),
        };
        var batchDict = await _adapter.ReconcileBatchAsync(batchInput);

        Assert.True(batchDict.TryGetValue(query, out var batch),
            $"Batch path returned no entry for query '{query}'");

        LogPair(query, manual, batch);

        // Both paths must return at least one candidate (or both be empty).
        Assert.Equal(manual.Count == 0, batch.Count == 0);

        if (manual.Count == 0)
        {
            return;
        }

        // ── Compare top-5 QIDs in order ─────────────────────────────────────
        var manualTop = manual.Take(5).Select(c => c.Id).ToList();
        var batchTop = batch.Take(5).Select(c => c.Id).ToList();

        Assert.Equal(manualTop, batchTop);

        // ── Compare top-result score within tolerance (0.5 points) ──────────
        var manualScore = manual[0].Score;
        var batchScore = batch[0].Score;
        var diff = Math.Abs(manualScore - batchScore);
        Assert.True(diff <= 0.5,
            $"Top score drift for '{query}': manual={manualScore:F2} batch={batchScore:F2} diff={diff:F2}");
    }

    public void Dispose() { /* HttpClient owned by IHttpClientFactory, cleaned up by ServiceProvider */ }

    // ── Builder helpers (mirrors ReconciliationAdapterTests) ─────────────────

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static ReconciliationAdapter BuildAdapter()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "config", "providers", "wikidata_reconciliation.json");
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ReconciliationProviderConfig>(json, s_jsonOptions)
                     ?? throw new InvalidOperationException("Failed to deserialize wikidata_reconciliation.json");

        config.ThrottleMs = 100;

        var factory = BuildHttpFactory("wikidata_reconciliation", "headshot_download");
        return new ReconciliationAdapter(
            config,
            factory,
            NullLogger<ReconciliationAdapter>.Instance,
            new StubFuzzyMatchingService());
    }

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(WikidataParityTests).Assembly.Location);
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

    private static IHttpClientFactory BuildHttpFactory(params string[] clientNames)
    {
        var services = new ServiceCollection();
        foreach (var name in clientNames)
        {
            services.AddHttpClient(name, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add(
                    "User-Agent",
                    "Tuvima Library/IntegrationTest (mailto:test@tuvima.dev)");
            });
        }
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    private void LogPair(
        string query,
        IReadOnlyList<ReconciliationResult> manual,
        IReadOnlyList<ReconciliationResult> batch)
    {
        _output.WriteLine($"\n═══ Parity check: '{query}' ═══");
        _output.WriteLine($"Manual ({manual.Count} candidates):");
        foreach (var c in manual.Take(5))
        {
            _output.WriteLine($"  {c.Id}  \"{c.Name}\"  score={c.Score:F2}");
        }

        _output.WriteLine($"Batch  ({batch.Count} candidates):");
        foreach (var c in batch.Take(5))
        {
            _output.WriteLine($"  {c.Id}  \"{c.Name}\"  score={c.Score:F2}");
        }

        _output.WriteLine("");
    }

    /// <summary>Stub fuzzy matching service — always returns 1.0 (pass-through).</summary>
    private sealed class StubFuzzyMatchingService : IFuzzyMatchingService
    {
        public double ComputeTokenSetRatio(string a, string b) => 1.0;
        public double ComputePartialRatio(string a, string b) => 1.0;
        public FieldMatchResult ScoreCandidate(LocalMetadata local, CandidateMetadata candidate) =>
            new() { TitleScore = 1.0, AuthorScore = 1.0, YearScore = 1.0, CompositeScore = 1.0 };
    }
}
