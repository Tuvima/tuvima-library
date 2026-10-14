using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tuvima.Wikidata;
using Xunit.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// Integration tests for <see cref="ReconciliationAdapter"/> against the live
/// Wikidata Reconciliation API (wikidata.reconci.link) and Data Extension API.
///
/// These tests require network access and must never run in CI.
/// Run locally with: dotnet test --filter "Category=Integration"
///
/// Stable Wikidata QIDs used:
///   Q190159  — Dune (novel, 1965, Frank Herbert)
///   Q44413   — Frank Herbert (author)
///   Q662029  — Neuromancer (novel, William Gibson)
///   Q328511  — Foundation (novel, Isaac Asimov)
///   Q6142591 — James S.A. Corey (pen name used by Daniel Abraham + Ty Franck)
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReconciliationAdapterTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ReconciliationAdapter _adapter;

    public ReconciliationAdapterTests(ITestOutputHelper output)
    {
        _output = output;
        _adapter = BuildAdapter();
    }

    // ── Reconciliation: single query ─────────────────────────────────────────

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task Reconcile_Dune_ReturnsQ190159_WithHighScore()
    {
        var constraints = new Dictionary<string, string> { ["P50"] = "Frank Herbert" };

        var results = await _adapter.ReconcileAsync("Dune", constraints);

        LogCandidates("Reconcile: Dune + P50=Frank Herbert", results);

        Assert.NotEmpty(results);

        var dune = results.FirstOrDefault(r =>
            string.Equals(r.Id, "Q190159", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(dune);
        Assert.True(dune.Score >= 90,
            $"Expected score >= 90 for Q190159 but got {dune.Score}");
    }

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task Reconcile_Neuromancer_ReturnsQ662029()
    {
        var results = await _adapter.ReconcileAsync("Neuromancer");

        LogCandidates("Reconcile: Neuromancer", results);

        Assert.NotEmpty(results);
        Assert.Contains(results, r =>
            string.Equals(r.Id, "Q662029", StringComparison.OrdinalIgnoreCase));
    }

    // ── Reconciliation: batch ─────────────────────────────────────────────────

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task ReconcileBatch_MultipleQueries_ReturnsAllResults()
    {
        var requests = new List<(string QueryId, string Query, Dictionary<string, string>? PropertyConstraints, MediaType MediaType)>
        {
            ("q0", "Dune",        new Dictionary<string, string> { ["P50"] = "Frank Herbert" }, MediaType.Books),
            ("q1", "Neuromancer", new Dictionary<string, string> { ["P50"] = "William Gibson" }, MediaType.Books),
            ("q2", "Foundation",  new Dictionary<string, string> { ["P50"] = "Isaac Asimov" }, MediaType.Books),
        };

        var results = await _adapter.ReconcileBatchAsync(requests);

        _output.WriteLine($"Batch reconciliation: {results.Count} query keys returned.");
        foreach (var kvp in results)
        {
            var key = kvp.Key;
            var candidates = kvp.Value;
            _output.WriteLine($"  [{key}]: {candidates.Count} candidates");
            foreach (var c in candidates.Take(3))
            {
                _output.WriteLine($"    {c.Id}  \"{c.Name}\"  score={c.Score:F1}");
            }
        }

        Assert.Equal(3, results.Count);
        Assert.True(results["q0"].Count > 0, "q0 (Dune) returned no results");
        Assert.True(results["q1"].Count > 0, "q1 (Neuromancer) returned no results");
        Assert.True(results["q2"].Count > 0, "q2 (Foundation) returned no results");
    }

    // ── Data Extension: work properties ──────────────────────────────────────

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task Extend_Q190159_P50_ReturnsFrankHerbert()
    {
        var extensions = await _adapter.ExtendAsync(["Q190159"], ["P50"]);

        LogExtensions("Extend Q190159 P50 (author)", extensions);

        Assert.NotEmpty(extensions);

        Assert.True(extensions.TryGetValue("Q190159", out var duneProps),
            "Q190159 not present in extension result");

        Assert.True(duneProps.ContainsKey("P50"),
            "P50 (author) not present in extension result for Q190159");

        var authorValues = duneProps["P50"];
        Assert.NotEmpty(authorValues);

        var frankHerbert = authorValues.FirstOrDefault(v =>
            (v.Value?.RawValue is not null && v.Value.RawValue.Contains("Frank Herbert", StringComparison.OrdinalIgnoreCase))
            || (v.Value?.EntityId is not null && v.Value.EntityId == "Q44413"));

        Assert.NotNull(frankHerbert);
    }

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task Extend_Q44413_P18_ReturnsCommonsFilename()
    {
        // Q44413 = Frank Herbert; P18 = image (person headshot — Wikimedia Commons filename)
        var extensions = await _adapter.ExtendAsync(["Q44413"], ["P18"]);

        LogExtensions("Extend Q44413 P18 (image/headshot)", extensions);

        Assert.NotEmpty(extensions);

        Assert.True(extensions.TryGetValue("Q44413", out var herbertProps),
            "Q44413 not present in extension result");

        Assert.True(herbertProps.ContainsKey("P18"),
            "P18 (image) not present for Frank Herbert (Q44413)");

        var imageValues = herbertProps["P18"];
        Assert.NotEmpty(imageValues);

        // The raw value from the Data Extension API is a Wikimedia Commons filename string.
        var filename = imageValues[0].Value?.RawValue;
        Assert.False(string.IsNullOrWhiteSpace(filename),
            "Expected a non-empty Commons filename for P18");

        _output.WriteLine($"  Commons filename: {filename}");
    }

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task Extend_Q6142591_P527_ReturnsMultipleAuthors()
    {
        // Q6142591 = James S.A. Corey (pen name); P527 = has parts (Daniel Abraham + Ty Franck)
        var extensions = await _adapter.ExtendAsync(["Q6142591"], ["P527"]);

        LogExtensions("Extend Q6142591 P527 (has_parts)", extensions);

        Assert.NotEmpty(extensions);

        Assert.True(extensions.TryGetValue("Q6142591", out var coreyProps),
            "Q6142591 not present in extension result");

        Assert.True(coreyProps.ContainsKey("P527"),
            "P527 (has_parts) not present for Q6142591");

        var parts = coreyProps["P527"];
        Assert.True(parts.Count >= 2,
            $"Expected at least 2 parts (Daniel Abraham + Ty Franck) but got {parts.Count}");

        _output.WriteLine($"  Parts ({parts.Count}):");
        foreach (var p in parts)
        {
            _output.WriteLine($"    id={p.Value?.EntityId}  label={p.Value?.RawValue}");
        }
    }

    // ── Data Extension: instance_of (media type filtering) ───────────────────

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task Extend_Q190159_P31_ReturnsLiteraryWorkClass()
    {
        // P31 = instance_of; Dune should be an instance of a literary work class
        var extensions = await _adapter.ExtendAsync(["Q190159"], ["P31"]);

        LogExtensions("Extend Q190159 P31 (instance_of)", extensions);

        Assert.NotEmpty(extensions);

        Assert.True(extensions.TryGetValue("Q190159", out var duneProps),
            "Q190159 not present in extension result");

        Assert.True(duneProps.ContainsKey("P31"),
            "P31 (instance_of) not present for Q190159");

        var classValues = duneProps["P31"];
        Assert.NotEmpty(classValues);

        // Configured Books classes: Q7725634, Q571, Q8261, Q47461344, Q277759, Q1238720
        var booksClasses = new HashSet<string>(
            ["Q7725634", "Q571", "Q8261", "Q47461344", "Q277759", "Q1238720"],
            StringComparer.OrdinalIgnoreCase);

        var classQids = classValues
            .Where(v => v.Value?.EntityId is not null)
            .Select(v => v.Value!.EntityId!)
            .ToList();

        _output.WriteLine($"  instance_of QIDs: {string.Join(", ", classQids)}");

        Assert.True(classQids.Any(q => booksClasses.Contains(q)),
            $"None of the P31 QIDs [{string.Join(", ", classQids)}] match the configured Books class list");
    }

    // ── SearchAsync (FetchAsync convenience) ─────────────────────────────────

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task FetchAsync_Dune_ReturnsWikidataQidClaim()
    {
        var request = new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Books,
            Title = "Dune",
            Author = "Frank Herbert",
        };

        var claims = await _adapter.FetchAsync(request);

        LogClaims("FetchAsync: Dune (Books)", claims);

        Assert.NotEmpty(claims);

        var qidClaim = claims.FirstOrDefault(c =>
            string.Equals(c.Key, "wikidata_qid", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(qidClaim);
        Assert.Equal("Q190159", qidClaim.Value, ignoreCase: true);
        Assert.Equal(1.0, qidClaim.Confidence);
    }

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task SearchAsync_Dune_ReturnsQ190159AsProviderItemId()
    {
        var request = new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Books,
            Title = "Dune",
            Author = "Frank Herbert",
        };

        var results = await _adapter.SearchAsync(request, limit: 10);

        _output.WriteLine($"SearchAsync: Dune — {results.Count} result(s)");
        foreach (var r in results)
        {
            _output.WriteLine($"  {r.ProviderItemId}  \"{r.Title}\"  confidence={r.Confidence:F2}  desc={r.Description}");
        }

        Assert.NotEmpty(results);

        var dune = results.FirstOrDefault(r =>
            string.Equals(r.ProviderItemId, "Q190159", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(dune);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose() { /* HttpClient owned by IHttpClientFactory, cleaned up by ServiceProvider */ }

    // ── Builder ───────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Loads <c>config/providers/wikidata_reconciliation.json</c> and
    /// creates a <see cref="ReconciliationAdapter"/> with a real HTTP client factory.
    /// </summary>
    private static ReconciliationAdapter BuildAdapter()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "config", "providers", "wikidata_reconciliation.json");
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ReconciliationProviderConfig>(json, s_jsonOptions)
                       ?? throw new InvalidOperationException("Failed to deserialize wikidata_reconciliation.json");

        // Reduce throttle for tests so the suite runs faster.
        config.ThrottleMs = 100;

        var factory = BuildHttpFactory("wikidata_reconciliation", "headshot_download");
        return new ReconciliationAdapter(config, factory, NullLogger<ReconciliationAdapter>.Instance, new StubFuzzyMatchingService());
    }

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(ReconciliationAdapterTests).Assembly.Location);
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

    // ── Log helpers ───────────────────────────────────────────────────────────

    private void LogCandidates(string label, IReadOnlyList<ReconciliationResult> candidates)
    {
        _output.WriteLine($"\n═══ {label} — {candidates.Count} candidate(s) ═══");
        foreach (var c in candidates)
        {
            _output.WriteLine($"  {c.Id}  \"{c.Name}\"  score={c.Score:F1}  match={c.Match}  desc={c.Description}");
        }

        _output.WriteLine("");
    }

    private void LogExtensions(string label, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>> extensions)
    {
        _output.WriteLine($"\n═══ {label} — {extensions.Count} entity result(s) ═══");
        foreach (var (qid, props) in extensions)
        {
            _output.WriteLine($"  QID: {qid}  properties: {props.Count}");
            foreach (var (pCode, values) in props)
            {
                foreach (var v in values)
                {
                    var str = v.Value?.Kind == WikidataValueKind.String ? v.Value.RawValue : null;
                    var id = v.Value?.EntityId;
                    var monoLabel = v.Value?.Kind == WikidataValueKind.MonolingualText ? v.Value.RawValue : null;
                    var date = v.Value?.Kind == WikidataValueKind.Time ? v.Value.RawValue : null;
                    _output.WriteLine($"    [{pCode}]  str={str}  id={id}  label={monoLabel}  date={date}");
                }
            }
        }
        _output.WriteLine("");
    }

    private void LogClaims(string label, IReadOnlyList<ProviderClaim> claims)
    {
        _output.WriteLine($"\n═══ {label} — {claims.Count} claim(s) ═══");
        foreach (var c in claims)
        {
            var display = c.Value.Length > 120 ? c.Value[..120] + "…" : c.Value;
            _output.WriteLine($"  [{c.Key}] = \"{display}\"  confidence={c.Confidence:F2}");
        }
        _output.WriteLine("");
    }

    /// <summary>
    /// Stub fuzzy matching service for integration tests — always returns 1.0 (pass-through).
    /// </summary>
    private sealed class StubFuzzyMatchingService : IFuzzyMatchingService
    {
        public double ComputeTokenSetRatio(string a, string b) => 1.0;
        public double ComputePartialRatio(string a, string b) => 1.0;
        public FieldMatchResult ScoreCandidate(LocalMetadata local, CandidateMetadata candidate) =>
            new() { TitleScore = 1.0, AuthorScore = 1.0, YearScore = 1.0, CompositeScore = 1.0 };
    }
}
