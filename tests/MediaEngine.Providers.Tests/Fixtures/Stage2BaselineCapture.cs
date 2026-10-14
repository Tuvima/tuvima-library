using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using MediaEngine.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tuvima.Wikidata;
using Xunit.Abstractions;

namespace MediaEngine.Providers.Tests.Fixtures;

/// <summary>
/// Parity baseline capture — runs the curated Stage 2 fixture set through the
/// CURRENT <c>ReconciliationAdapter.ResolveBatchAsync</c> facade and writes the
/// outcome to <c>tests/fixtures/stage2-baseline.json</c>.
///
/// <para>
/// Run once before starting the adapter slimdown work to capture a "before"
/// snapshot. Every later commit's parity test diffs <c>ResolveBatchAsync</c>
/// output against this baseline. See <c>.claude/plans/adapter-slimdown-remediation.md</c>
/// Phase 0.5.
/// </para>
///
/// <para>
/// Requires live network access to Wikidata. Marked <c>[Skip]</c> by default
/// so it does not run in CI. Invoke locally with:
/// </para>
/// <code>
/// dotnet test --filter "FullyQualifiedName~Stage2BaselineCapture"
/// </code>
/// </summary>
[Trait("Category", "Baseline")]
public sealed class Stage2BaselineCapture : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ReconciliationAdapter _adapter;

    public Stage2BaselineCapture(ITestOutputHelper output)
    {
        _output = output;
        _adapter = BuildAdapter();
    }

    /// <summary>
    /// Hand-curated request set covering all Stage 2 strategies and the edge
    /// cases the adapter slimdown plan calls out:
    ///   • Bridge ID resolution (ISBN, TMDB, IMDB, MusicBrainz)
    ///   • Edition pivot (audiobook → work, book ISBN → work)
    ///   • Music album resolution
    ///   • Text reconciliation fallback
    ///   • Pseudonym Pattern 1 (Richard Bachman)
    ///   • Pseudonym Pattern 3 (James S.A. Corey)
    ///   • Multi-author (Good Omens — Gaiman & Pratchett)
    /// </summary>
    private static IReadOnlyList<WikidataResolveRequest> BuildRequests() =>
    [
        // ── Books: bridge ID (ISBN) ──────────────────────────────────────
        new()
        {
            CorrelationKey = "book-dune-isbn",
            MediaType = MediaType.Books,
            BridgeIds = new Dictionary<string, string> { ["isbn13"] = "9780441172719" },
            WikidataProperties = new Dictionary<string, string> { ["isbn13"] = "P212" },
            IsEditionAware = true,
            Title = "Dune",
            Author = "Frank Herbert",
        },
        new()
        {
            CorrelationKey = "book-foundation-isbn",
            MediaType = MediaType.Books,
            BridgeIds = new Dictionary<string, string> { ["isbn13"] = "9780553293357" },
            WikidataProperties = new Dictionary<string, string> { ["isbn13"] = "P212" },
            IsEditionAware = true,
            Title = "Foundation",
            Author = "Isaac Asimov",
        },

        // ── Books: text reconciliation only ──────────────────────────────
        new()
        {
            CorrelationKey = "book-neuromancer-text",
            MediaType = MediaType.Books,
            Title = "Neuromancer",
            Author = "William Gibson",
        },

        // ── Pattern 3 multi-author / collective pseudonym ────────────────
        new()
        {
            CorrelationKey = "book-good-omens-text",
            MediaType = MediaType.Books,
            Title = "Good Omens",
            Author = "Neil Gaiman & Terry Pratchett",
        },
        new()
        {
            CorrelationKey = "book-leviathan-wakes-text",
            MediaType = MediaType.Books,
            Title = "Leviathan Wakes",
            Author = "James S.A. Corey",
        },

        // ── Pattern 1 reverse pen name ────────────────────────────────────
        new()
        {
            CorrelationKey = "book-bachman-rage-text",
            MediaType = MediaType.Books,
            Title = "Rage",
            Author = "Richard Bachman",
        },

        // ── Movies: TMDB bridge ID ───────────────────────────────────────
        new()
        {
            CorrelationKey = "movie-dune-2021-tmdb",
            MediaType = MediaType.Movies,
            BridgeIds = new Dictionary<string, string> { ["tmdb_id"] = "438631" },
            WikidataProperties = new Dictionary<string, string> { ["tmdb_id"] = "P4947" },
            Title = "Dune",
        },

        // ── TV: text reconciliation ──────────────────────────────────────
        new()
        {
            CorrelationKey = "tv-breaking-bad-text",
            MediaType = MediaType.TV,
            Title = "Breaking Bad",
        },
        new()
        {
            CorrelationKey = "tv-shogun-text",
            MediaType = MediaType.TV,
            Title = "Shogun",
        },

        // ── Music: album resolution ──────────────────────────────────────
        new()
        {
            CorrelationKey = "music-ram-album",
            MediaType = MediaType.Music,
            Strategy = ResolveStrategy.MusicAlbum,
            AlbumTitle = "Random Access Memories",
            Artist = "Daft Punk",
            IsEditionAware = true,
        },
        new()
        {
            CorrelationKey = "music-ok-computer-album",
            MediaType = MediaType.Music,
            Strategy = ResolveStrategy.MusicAlbum,
            AlbumTitle = "OK Computer",
            Artist = "Radiohead",
            IsEditionAware = true,
        },

        // ── Audiobooks: edition pivot via title/author text ──────────────
        new()
        {
            CorrelationKey = "audiobook-project-hail-mary-text",
            MediaType = MediaType.Audiobooks,
            Title = "Project Hail Mary",
            Author = "Andy Weir",
            IsEditionAware = true,
        },
    ];

    [Fact(Skip = "Baseline capture — run manually with --filter \"FullyQualifiedName~Stage2BaselineCapture\". Requires live Wikidata. Re-run only when intentionally re-baselining.")]
    [Trait("Category", "Baseline")]
    public async Task CaptureStage2Baseline()
    {
        var requests = BuildRequests();
        var results = await _adapter.ResolveBatchAsync(requests);

        var baseline = new SortedDictionary<string, BaselineEntry>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            if (!results.TryGetValue(request.CorrelationKey, out var result))
            {
                baseline[request.CorrelationKey] = new BaselineEntry
                {
                    Found = false,
                    MatchedBy = "MissingFromBatchResult",
                };
                _output.WriteLine($"⚠ {request.CorrelationKey}: missing from batch result");
                continue;
            }

            baseline[request.CorrelationKey] = new BaselineEntry
            {
                Found = result.Found,
                Qid = result.Qid,
                IsEdition = result.IsEdition,
                WorkQid = result.WorkQid,
                EditionQid = result.EditionQid,
                PrimaryBridgeIdType = result.PrimaryBridgeIdType,
                MatchedBy = result.MatchedBy.ToString(),
                ClaimCount = result.Claims.Count,
                CollectedBridgeIdKeys = result.CollectedBridgeIds.Keys
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToList(),
            };

            _output.WriteLine(
                $"✓ {request.CorrelationKey}: {result.Qid ?? "<none>"} " +
                $"via {result.MatchedBy} (claims={result.Claims.Count})");
        }

        var json = JsonSerializer.Serialize(baseline, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });

        var root = FindRepoRoot();
        var dir = Path.Combine(root, "tests", "fixtures");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "stage2-baseline.json");
        await File.WriteAllTextAsync(path, json + Environment.NewLine);

        _output.WriteLine($"\nBaseline written to {path} ({baseline.Count} entries)");
    }

    /// <summary>
    /// Runs the 12-request fixture set through the current Stage 2 path and
    /// writes the outcome to <c>tests/fixtures/stage2-baseline-v2.json</c>.
    /// After Commit F2 of the adapter slimdown remediation, the legacy
    /// hand-rolled path has been deleted and <c>ResolveBatchAsync</c> is a
    /// thin pass-through to <c>Tuvima.Wikidata.BridgeResolutionService</c>.
    ///
    /// <para>
    /// Constructs the adapter with a real <see cref="ConfigurationDirectoryLoader"/>
    /// pointing at <c>{repoRoot}/config</c>. Re-run this fixture to re-baseline
    /// when the library version changes or when Stage 2 behaviour intentionally
    /// shifts.
    /// </para>
    /// </summary>
    [Fact(Skip = "Phase 2 parity capture — run manually with --filter \"FullyQualifiedName~CaptureStage2BaselineViaLibraryPath\". Requires live Wikidata. Re-run only when re-baselining v2.")]
    [Trait("Category", "Baseline")]
    public async Task CaptureStage2BaselineViaLibraryPath()
    {
        var adapter = BuildAdapterWithLibraryPath();
        var requests = BuildRequests();
        var results = await adapter.ResolveBatchAsync(requests);

        var baseline = new SortedDictionary<string, BaselineEntry>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            if (!results.TryGetValue(request.CorrelationKey, out var result))
            {
                baseline[request.CorrelationKey] = new BaselineEntry
                {
                    Found = false,
                    MatchedBy = "MissingFromBatchResult",
                };
                _output.WriteLine($"⚠ {request.CorrelationKey}: missing from batch result");
                continue;
            }

            baseline[request.CorrelationKey] = new BaselineEntry
            {
                Found = result.Found,
                Qid = result.Qid,
                IsEdition = result.IsEdition,
                WorkQid = result.WorkQid,
                EditionQid = result.EditionQid,
                PrimaryBridgeIdType = result.PrimaryBridgeIdType,
                MatchedBy = result.MatchedBy.ToString(),
                ClaimCount = result.Claims.Count,
                CollectedBridgeIdKeys = result.CollectedBridgeIds.Keys
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToList(),
            };

            _output.WriteLine(
                $"✓ {request.CorrelationKey}: {result.Qid ?? "<none>"} " +
                $"via {result.MatchedBy} (claims={result.Claims.Count})");
        }

        var json = JsonSerializer.Serialize(baseline, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });

        var root = FindRepoRoot();
        var dir = Path.Combine(root, "tests", "fixtures");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "stage2-baseline-v2.json");
        await File.WriteAllTextAsync(path, json + Environment.NewLine);

        _output.WriteLine($"\nLibrary-path baseline written to {path} ({baseline.Count} entries)");
    }

    private static ReconciliationAdapter BuildAdapterWithLibraryPath()
    {
        var root = FindRepoRoot();
        var configDir = Path.Combine(root, "config");
        var loader = new ConfigurationDirectoryLoader(configDir);

        var providerCfgPath = Path.Combine(configDir, "providers", "wikidata_reconciliation.json");
        var providerCfgJson = File.ReadAllText(providerCfgPath);
        var providerCfg = JsonSerializer.Deserialize<ReconciliationProviderConfig>(providerCfgJson, s_jsonOptions)
                              ?? throw new InvalidOperationException("Failed to deserialize wikidata_reconciliation.json");
        providerCfg.ThrottleMs = 100;

        var factory = BuildHttpFactory("wikidata_reconciliation", "headshot_download", "WikidataReconciliation");
        var reconcilerHttp = factory.CreateClient("WikidataReconciliation");
        var reconciler = new WikidataReconciler(reconcilerHttp, new WikidataReconcilerOptions
        {
            UserAgent = "Tuvima Library/Stage2BaselineV2 (mailto:test@tuvima.dev)",
            MaxLag = 0,
            TypeHierarchyDepth = 3,
            IncludeSitelinkLabels = true,
        });

        return new ReconciliationAdapter(
            providerCfg,
            factory,
            NullLogger<ReconciliationAdapter>.Instance,
            new StubFuzzyMatchingService(),
            responseCache: null,
            configLoader: loader,
            reconciler: reconciler);
    }

    public void Dispose() { /* HttpClient owned by IHttpClientFactory */ }

    /// <summary>
    /// Per-request baseline entry. Deliberately excludes the full claim list —
    /// claim contents are diffed separately by the per-commit parity tests.
    /// Only the count is captured here so a regression in property fetching
    /// shows up as a count delta.
    /// </summary>
    public sealed class BaselineEntry
    {
        public bool Found { get; init; }
        public string? Qid { get; init; }
        public bool IsEdition { get; init; }
        public string? WorkQid { get; init; }
        public string? EditionQid { get; init; }
        public string? PrimaryBridgeIdType { get; init; }
        public string MatchedBy { get; init; } = "";
        public int ClaimCount { get; init; }
        public IReadOnlyList<string> CollectedBridgeIdKeys { get; init; } = [];
    }

    // ── Builder helpers (mirror WikidataParityTests) ─────────────────────────

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

        var factory = BuildHttpFactory("wikidata_reconciliation", "headshot_download", "WikidataReconciliation");

        // Instantiate a real WikidataReconciler — without it the adapter
        // short-circuits every Stage 2 path to NotFound.
        var reconcilerHttp = factory.CreateClient("WikidataReconciliation");
        var reconciler = new WikidataReconciler(reconcilerHttp, new WikidataReconcilerOptions
        {
            UserAgent = "Tuvima Library/Stage2Baseline (mailto:test@tuvima.dev)",
            MaxLag = 0,
            TypeHierarchyDepth = 3,
            IncludeSitelinkLabels = true,
        });

        return new ReconciliationAdapter(
            config,
            factory,
            NullLogger<ReconciliationAdapter>.Instance,
            new StubFuzzyMatchingService(),
            reconciler: reconciler);
    }

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(Stage2BaselineCapture).Assembly.Location);
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
                    "Tuvima Library/Stage2Baseline (mailto:test@tuvima.dev)");
            });
        }
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    private sealed class StubFuzzyMatchingService : IFuzzyMatchingService
    {
        public double ComputeTokenSetRatio(string a, string b) => 1.0;
        public double ComputePartialRatio(string a, string b) => 1.0;
        public FieldMatchResult ScoreCandidate(LocalMetadata local, CandidateMetadata candidate) =>
            new() { TitleScore = 1.0, AuthorScore = 1.0, YearScore = 1.0, CompositeScore = 1.0 };
    }
}
