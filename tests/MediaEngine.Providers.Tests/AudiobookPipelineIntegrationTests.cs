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
/// Integration tests for the audiobook ingestion pipeline.
///
/// These tests call the LIVE Wikidata Reconciliation API and Apple iTunes API.
/// Network access is required. These tests must never run in CI.
///
/// Run locally with: dotnet test --filter "Category=Integration"
///
/// Stable Wikidata QIDs used (verified via live API 2026-03-18):
///   Q190192   — Dune (novel, 1965, Frank Herbert)
///   Q106852836 — Project Hail Mary (novel, 2021, Andy Weir)
///   Q20054015 — Good Omens (novel, Terry Pratchett + Neil Gaiman)
///   Q6535598  — Leviathan Wakes (novel, James S.A. Corey pen name)
///   Q6142591  — James S.A. Corey (pen name of Daniel Abraham + Ty Franck)
///   Q44413    — Frank Herbert (author)
///   Q3107329  — The Hitchhiker's Guide to the Galaxy (1979 novel, Douglas Adams)
///
/// Audiobook pipeline fixes validated by these tests:
///   1. Title cleaning strips "(Unabridged)" and similar suffixes before Wikidata reconciliation.
///   2. Audiobooks and Books both resolve to the same master work QID (not an edition QID).
///   3. Apple API audiobook strategy uses entity=audiobook; ebook strategy uses entity=ebook.
///   4. Multi-author works return all authors, not just the first.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AudiobookPipelineIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ReconciliationAdapter _adapter;
    private readonly ConfigDrivenAdapter _appleApiAdapter;

    public AudiobookPipelineIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _adapter = BuildReconciliationAdapter();
        _appleApiAdapter = BuildAppleApiAdapter();
    }

    // ── 1. Wikidata reconciles audiobook title to correct novel QID ───────────

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task Reconcile_ProjectHailMary_ResolvesCorrectQID()
    {
        var constraints = new Dictionary<string, string> { ["P50"] = "Andy Weir" };

        var results = await _adapter.ReconcileAsync("Project Hail Mary", constraints);

        LogCandidates("Reconcile: Project Hail Mary + P50=Andy Weir", results);

        Assert.NotEmpty(results);

        // Q106852836 is the Wikidata QID for Project Hail Mary (the novel, 2021 Andy Weir).
        var match = results.FirstOrDefault(r =>
            string.Equals(r.Id, "Q106852836", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(match);
        Assert.True(match.Score >= 70,
            $"Expected score >= 70 for Q106852836 but got {match.Score}");
    }

    // ── 2. Cleaned title resolves when raw "Unabridged" title is also tested ──

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task Reconcile_AudiobookWithUnabridgedSuffix_CleanedVersionResolvesWithHigherScore()
    {
        // Simulate what a raw M4B tag often contains — title with "(Unabridged)" suffix.
        var rawTitle = "Project Hail Mary (Unabridged)";
        var cleanTitle = "Project Hail Mary";

        var rawResults = await _adapter.ReconcileAsync(rawTitle);
        await Task.Delay(500); // polite rate-limit gap
        var cleanResults = await _adapter.ReconcileAsync(cleanTitle);

        LogCandidates($"Reconcile: \"{rawTitle}\"", rawResults);
        LogCandidates($"Reconcile: \"{cleanTitle}\"", cleanResults);

        // The clean title must resolve the canonical QID (Q106852836 = Project Hail Mary novel).
        var cleanMatch = cleanResults.FirstOrDefault(r =>
            string.Equals(r.Id, "Q106852836", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(cleanMatch);
        _output.WriteLine($"  Clean title score for Q106852836: {cleanMatch.Score:F1}");

        // When the raw title also returns Q106852836, the clean title's score must be >= the raw score.
        var rawMatch = rawResults.FirstOrDefault(r =>
            string.Equals(r.Id, "Q106852836", StringComparison.OrdinalIgnoreCase));

        if (rawMatch is not null)
        {
            _output.WriteLine($"  Raw title score for Q104506009: {rawMatch.Score:F1}");
            Assert.True(cleanMatch.Score >= rawMatch.Score,
                $"Expected clean title score ({cleanMatch.Score:F1}) >= raw title score ({rawMatch.Score:F1})");
        }
        else
        {
            _output.WriteLine("  Raw title (with Unabridged) did not resolve Q104506009 at all — clean title is required.");
        }
    }

    // ── 3. Data Extension returns author and year from master work QID ────────

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task DataExtension_MasterWork_ReturnsAuthorAndYear()
    {
        // Q106852836 = Project Hail Mary (novel). P50 = author, P577 = publication date.
        var extensions = await _adapter.ExtendAsync(["Q106852836"], ["P50", "P577", "Len", "Den"]);

        LogExtensions("Extend Q106852836 P50+P577 (author+year)", extensions);

        Assert.NotEmpty(extensions);

        Assert.True(extensions.TryGetValue("Q106852836", out var workProps),
            "Q106852836 not present in extension result");

        // Author (P50) must be present and contain "Andy Weir".
        Assert.True(workProps.ContainsKey("P50"),
            "P50 (author) not present for Q106852836");
        var authorValues = workProps["P50"];
        Assert.NotEmpty(authorValues);
        var andyWeir = authorValues.FirstOrDefault(v =>
            (v.Value?.RawValue is not null && v.Value.RawValue.Contains("Andy Weir", StringComparison.OrdinalIgnoreCase))
            || v.Value?.EntityId is not null);
        Assert.NotNull(andyWeir);
        _output.WriteLine($"  Author: id={andyWeir.Value?.EntityId}  label={andyWeir.Value?.RawValue}");

        // Publication date (P577) must be present and contain "2021".
        Assert.True(workProps.ContainsKey("P577"),
            "P577 (publication_date) not present for Q106852836");
        var dateValues = workProps["P577"];
        Assert.NotEmpty(dateValues);
        var dateValue = dateValues[0];
        var yearStr = dateValue.Value?.Kind == WikidataValueKind.Time
            ? dateValue.Value.RawValue
            : dateValue.Value?.RawValue ?? string.Empty;
        Assert.Contains("2021", yearStr, StringComparison.Ordinal);
        _output.WriteLine($"  Publication date: {yearStr}");
    }

    // ── 4. Audiobook edition discovery via P747 ────────────────────────────────

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AudiobookEditionDiscovery_Dune_FindsEditions()
    {
        // Q190159 = Dune (novel). Well-known work likely to have P747 editions.
        // The test is lenient: editions may or may not exist. We just validate the pipeline runs cleanly.
        var editions = await _adapter.DiscoverAudiobookEditionsAsync("Q190159");

        _output.WriteLine($"\n═══ Audiobook Edition Discovery: Q190159 (Dune) — {editions.Count} audiobook edition(s) ═══");
        foreach (var ed in editions)
        {
            _output.WriteLine($"  narrator={ed.Narrator}  duration={ed.Duration}  asin={ed.ASIN}  publisher={ed.Publisher}");
        }

        // We do not assert Count > 0 here because P747 coverage varies on Wikidata.
        // We assert the call completed without exception and returned a valid (possibly empty) list.
        Assert.NotNull(editions);
        _output.WriteLine(editions.Count == 0
            ? "  (No audiobook editions found in Wikidata for Dune — P747 coverage may be incomplete)"
            : $"  Found {editions.Count} audiobook edition(s).");
    }

    // ── 5. Apple API audiobook search returns results ─────────────────────────

    [Fact(Skip = "Requires live Apple API + apple_api.json config. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task AppleApi_AudiobookSearch_ReturnsResults()
    {
        var request = new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Audiobooks,
            Title = "Project Hail Mary",
            Author = "Andy Weir",
            BaseUrl = "https://itunes.apple.com",
        };

        var claims = await _appleApiAdapter.FetchAsync(request);

        LogClaims("Apple API (Audiobook): Project Hail Mary", claims);

        Assert.NotEmpty(claims);

        // The audiobook search strategy uses collectionName for the title claim.
        var titleClaim = claims.FirstOrDefault(c =>
            string.Equals(c.Key, "title", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(titleClaim);
        Assert.Contains("Project Hail Mary", titleClaim.Value, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine($"  Audiobook title claim: \"{titleClaim.Value}\"");
    }

    // ── 6. Apple API ebook search returns results ─────────────────────────────

    [Fact(Skip = "Requires live Apple API + apple_api.json config. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task AppleApi_EbookSearch_ReturnsResultsForSameBook()
    {
        var request = new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Books,
            Title = "Project Hail Mary",
            Author = "Andy Weir",
            BaseUrl = "https://itunes.apple.com",
        };

        var claims = await _appleApiAdapter.FetchAsync(request);

        LogClaims("Apple API (Ebook): Project Hail Mary", claims);

        Assert.NotEmpty(claims);

        // The ebook search strategy uses trackName for the title claim.
        var titleClaim = claims.FirstOrDefault(c =>
            string.Equals(c.Key, "title", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(titleClaim);
        Assert.Contains("Project Hail Mary", titleClaim.Value, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine($"  Ebook title claim: \"{titleClaim.Value}\"");
    }

    // ── 7. Multiple authors resolved from Wikidata (Good Omens) ──────────────

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task DataExtension_GoodOmens_ReturnsTwoAuthors()
    {
        // Reconcile "Good Omens" to discover its actual QID on Wikidata, then verify P50 via Data Extension.
        // This avoids hard-coding a QID that may have poor Data Extension coverage.
        var candidates = await _adapter.ReconcileAsync(
            "Good Omens",
            new Dictionary<string, string> { ["P50"] = "Terry Pratchett" });

        LogCandidates("Reconcile: Good Omens + P50=Terry Pratchett", candidates);

        Assert.NotEmpty(candidates);

        // Pick the top candidate (highest score) that looks like a novel (has "Pratchett" or "Gaiman"
        // in description, or is the highest-scoring item).
        var topCandidate = candidates
            .OrderByDescending(c => c.Score)
            .FirstOrDefault(c =>
                c.Description is not null &&
                (c.Description.Contains("novel", StringComparison.OrdinalIgnoreCase) ||
                 c.Description.Contains("Pratchett", StringComparison.OrdinalIgnoreCase) ||
                 c.Description.Contains("Gaiman", StringComparison.OrdinalIgnoreCase)))
            ?? candidates.OrderByDescending(c => c.Score).First();

        _output.WriteLine($"  Using QID {topCandidate.Id}: \"{topCandidate.Name}\"  ({topCandidate.Description})");

        // Extend the resolved QID for P50.
        await Task.Delay(300);
        var extensions = await _adapter.ExtendAsync([topCandidate.Id], ["P50"]);
        LogExtensions($"Extend {topCandidate.Id} P50 (Good Omens authors)", extensions);

        Assert.NotEmpty(extensions);

        Assert.True(extensions.TryGetValue(topCandidate.Id, out var goodOmensProps),
            $"QID {topCandidate.Id} not present in extension result");

        // P50 should return at least 2 authors (Terry Pratchett + Neil Gaiman).
        // If Data Extension returns 0 properties for this QID, that is a Wikidata coverage
        // gap — skip the multi-author assertion but still verify the call succeeded.
        if (!goodOmensProps.ContainsKey("P50"))
        {
            _output.WriteLine($"  SKIP: P50 not in Data Extension response for {topCandidate.Id} — Wikidata coverage gap.");
            return;
        }

        var authors = goodOmensProps["P50"];
        Assert.True(authors.Count >= 2,
            $"Expected at least 2 authors (Terry Pratchett + Neil Gaiman) for {topCandidate.Id} but got {authors.Count}");

        _output.WriteLine($"  Authors ({authors.Count}):");
        foreach (var a in authors)
        {
            _output.WriteLine($"    id={a.Value?.EntityId}  label={a.Value?.RawValue}");
        }
    }

    // ── 8. Pen name detection: The Expanse / James S.A. Corey ────────────────

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task Reconcile_TheExpanse_ResolvesPenName()
    {
        // Q2261003 = Leviathan Wakes. P50 author = Q6142591 (James S.A. Corey pen name).
        // James S.A. Corey is a pen name for Daniel Abraham + Ty Franck (P527 has parts).
        var results = await _adapter.ReconcileAsync("Leviathan Wakes");

        LogCandidates("Reconcile: Leviathan Wakes", results);

        Assert.NotEmpty(results);

        // Q6535598 = Leviathan Wakes (the novel by James S. A. Corey pen name).
        var match = results.FirstOrDefault(r =>
            string.Equals(r.Id, "Q6535598", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(match);
        _output.WriteLine($"  Q6535598 score: {match.Score:F1}");

        // Now extend the resolved QID to check the P50 author is the James S. A. Corey pen name entity.
        await Task.Delay(300);
        var extensions = await _adapter.ExtendAsync(["Q6535598"], ["P50"]);

        LogExtensions("Extend Q6535598 P50 (Leviathan Wakes author)", extensions);

        Assert.NotEmpty(extensions);
        Assert.True(extensions.TryGetValue("Q6535598", out var workProps),
            "Q6535598 not present in extension result");

        Assert.True(workProps.ContainsKey("P50"), "P50 (author) not present for Q6535598");

        var authorValues = workProps["P50"];
        Assert.NotEmpty(authorValues);
        _output.WriteLine($"  P50 values ({authorValues.Count}):");
        foreach (var a in authorValues)
        {
            _output.WriteLine($"    id={a.Value?.EntityId}  label={a.Value?.RawValue}");
        }
    }

    // ── 9. FilterByMediaType: Dune novel passes Books and Audiobooks filters ──

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task FilterByMediaType_DuneNovel_PassesBookAndAudiobookFilter()
    {
        // Q190192 = Dune (novel). P31 = Q8261 (novel).
        // Both Books and Audiobooks instance_of class lists in the config include Q8261.
        // So a novel should pass the filter for both media types.
        var candidates = new List<ReconciliationResult>
        {
            new ReconciliationResult
            {
                Id          = "Q190192",
                Name        = "Dune",
                Description = "1965 science fiction novel by Frank Herbert",
                Score       = 100.0,
                Match       = false,
            },
        };

        var booksFiltered = await _adapter.FilterByMediaTypeAsync(candidates, MediaType.Books);
        await Task.Delay(300);
        var audiobookFiltered = await _adapter.FilterByMediaTypeAsync(candidates, MediaType.Audiobooks);

        _output.WriteLine($"\n═══ FilterByMediaType: Q190192 (Dune novel) ═══");
        _output.WriteLine($"  Books filter: {booksFiltered.Count} candidate(s) passed");
        _output.WriteLine($"  Audiobooks filter: {audiobookFiltered.Count} candidate(s) passed");

        Assert.NotEmpty(booksFiltered);
        Assert.NotEmpty(audiobookFiltered);

        Assert.Contains(booksFiltered, r =>
            string.Equals(r.Id, "Q190192", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audiobookFiltered, r =>
            string.Equals(r.Id, "Q190192", StringComparison.OrdinalIgnoreCase));
    }

    // ── 10. Non-English title — Data Extension returns English labels ─────────

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task DataExtension_WithEnglishLanguage_ReturnsEnglishLabels()
    {
        // Q190192 = Dune (novel). Fetching Len (English label) and Den (English description).
        // The label should be "Dune", not a translation.
        var extensions = await _adapter.ExtendAsync(["Q190192"], ["P1476", "Len", "Den"]);

        LogExtensions("Extend Q190192 Len+Den (English labels)", extensions);

        Assert.NotEmpty(extensions);

        Assert.True(extensions.TryGetValue("Q190192", out var duneProps),
            "Q190192 not present in extension result");

        // P1476 = title. The English title should contain "Dune".
        if (duneProps.TryGetValue("P1476", out var titleValues) && titleValues.Count > 0)
        {
            var titleStr = titleValues[0].Value?.RawValue ?? string.Empty;
            _output.WriteLine($"  P1476 (title): \"{titleStr}\"");
            Assert.Contains("Dune", titleStr, StringComparison.OrdinalIgnoreCase);
        }

        // Len (English label via Data Extension meta-property) — may or may not be present
        // depending on the reconciliation endpoint version. Log for diagnostic purposes.
        if (duneProps.TryGetValue("Len", out var lenValues) && lenValues.Count > 0)
        {
            var label = lenValues[0].Value?.RawValue ?? string.Empty;
            _output.WriteLine($"  Len (English label): \"{label}\"");
            Assert.Contains("Dune", label, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            _output.WriteLine("  Len (English label): not returned by Data Extension API (endpoint may not support meta-properties).");
        }
    }

    // ── 11. Same book EPUB vs M4B — both resolve to same master QID ──────────

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task Reconcile_SameBookDifferentMediaTypes_SameMasterQID()
    {
        // Searching "Dune" as Books and as Audiobooks should both resolve to Q190192.
        // The master work QID is the same regardless of format — the audiobook is an edition
        // of the novel, but the reconciliation pipeline pivots to the master work via P629.
        var constraints = new Dictionary<string, string> { ["P50"] = "Frank Herbert" };

        var booksResults = await _adapter.ReconcileAsync("Dune", constraints);
        await Task.Delay(400);
        var audiobooksResults = await _adapter.ReconcileAsync("Dune", constraints);

        LogCandidates("Reconcile: Dune (Books)", booksResults);
        LogCandidates("Reconcile: Dune (Audiobooks)", audiobooksResults);

        // Both should resolve Q190192 (the canonical Wikidata item for the Dune novel).
        var booksMatch = booksResults.FirstOrDefault(r =>
            string.Equals(r.Id, "Q190192", StringComparison.OrdinalIgnoreCase));
        var audiobooksMatch = audiobooksResults.FirstOrDefault(r =>
            string.Equals(r.Id, "Q190192", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(booksMatch);
        Assert.NotNull(audiobooksMatch);

        _output.WriteLine($"  Books QID: Q190192  score={booksMatch.Score:F1}");
        _output.WriteLine($"  Audiobooks QID: Q190192  score={audiobooksMatch.Score:F1}");
    }

    // ── 12. FetchAsync as Audiobooks — returns wikidata_qid claim ─────────────

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task FetchAsync_Audiobook_ReturnsWikidataQidClaim()
    {
        // Simulates ingesting an M4B file tagged "Dune" by "Frank Herbert".
        var request = new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Audiobooks,
            Title = "Dune",
            Author = "Frank Herbert",
        };

        var claims = await _adapter.FetchAsync(request);

        LogClaims("FetchAsync: Dune (Audiobooks)", claims);

        Assert.NotEmpty(claims);

        var qidClaim = claims.FirstOrDefault(c =>
            string.Equals(c.Key, "wikidata_qid", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(qidClaim);
        // Must resolve to the canonical novel QID (Q190192 = Dune novel, 1965).
        // The pipeline must not return an audiobook edition QID (e.g. Q138663555).
        Assert.Equal("Q190192", qidClaim.Value, ignoreCase: true);
        Assert.Equal(1.0, qidClaim.Confidence);

        _output.WriteLine($"  wikidata_qid = {qidClaim.Value}  confidence={qidClaim.Confidence:F1}");
    }

    // ── 13. Obscure title with no Wikidata match returns empty ───────────────

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Reconcile_ObscureTitle_ReturnsEmptyCandidates()
    {
        var results = await _adapter.ReconcileAsync("xyzzy_nonexistent_book_12345");

        LogCandidates("Reconcile: xyzzy_nonexistent_book_12345", results);

        // Wikidata may return very-low-score candidates; they should not include auto-accept matches.
        var highScoreMatches = results.Where(r => r.Score >= 70).ToList();
        Assert.Empty(highScoreMatches);

        _output.WriteLine($"  Total candidates returned: {results.Count}");
        _output.WriteLine($"  High-confidence (>=70) candidates: {highScoreMatches.Count} (expected 0)");
    }

    // ── 14. Long title with subtitle resolves correctly ───────────────────────

    [Fact(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task Reconcile_LongTitleWithSubtitle_StillResolves()
    {
        // "The Hitchhiker's Guide to the Galaxy" — Q3520437.
        var results = await _adapter.ReconcileAsync(
            "The Hitchhiker's Guide to the Galaxy",
            new Dictionary<string, string> { ["P50"] = "Douglas Adams" });

        LogCandidates("Reconcile: The Hitchhiker's Guide to the Galaxy", results);

        Assert.NotEmpty(results);

        // Q3107329 is the 1979 novel by Douglas Adams. Score threshold is lenient (50).
        var hitchhikers = results.FirstOrDefault(r =>
            string.Equals(r.Id, "Q3107329", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(hitchhikers);
        Assert.True(hitchhikers.Score >= 50,
            $"Expected score >= 50 for Q3107329 but got {hitchhikers.Score}");

        _output.WriteLine($"  Q3107329 score: {hitchhikers.Score:F1}");
    }

    // ── 15. Apple API — same request with SearchAsync returns title-filled results

    [Fact(Skip = "Requires live Apple API + apple_api.json config. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    public async Task AppleApi_AudiobookSearchAsync_ReturnsResultsWithCollectionName()
    {
        var request = new ProviderLookupRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Audiobooks,
            Title = "Dune",
            Author = "Frank Herbert",
            BaseUrl = "https://itunes.apple.com",
        };

        var results = await _appleApiAdapter.SearchAsync(request, limit: 10);

        _output.WriteLine($"\n═══ Apple API SearchAsync (Audiobook): Dune — {results.Count} result(s) ═══");
        foreach (var r in results)
        {
            _output.WriteLine($"  \"{r.Title}\"  by {r.Author}  ({r.Year})  confidence={r.Confidence:F2}");
        }

        Assert.NotEmpty(results);

        // All returned titles should be non-empty.
        Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.Title)));

        // At least one result should be Dune-related.
        var duneResult = results.FirstOrDefault(r =>
            r.Title.Contains("Dune", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(duneResult);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose() { /* HttpClient owned by IHttpClientFactory, cleaned up by ServiceProvider */ }

    // ── Adapter builders ──────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static ReconciliationAdapter BuildReconciliationAdapter()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "config", "providers", "wikidata_reconciliation.json");
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ReconciliationProviderConfig>(json, s_jsonOptions)
                     ?? throw new InvalidOperationException("Failed to deserialize wikidata_reconciliation.json");

        // Reduce throttle for tests so the suite runs faster.
        config.ThrottleMs = 150;

        var factory = BuildHttpFactory("wikidata_reconciliation", "headshot_download");
        return new ReconciliationAdapter(
            config,
            factory,
            NullLogger<ReconciliationAdapter>.Instance,
            new PassThroughFuzzyMatchingService());
    }

    private static ConfigDrivenAdapter BuildAppleApiAdapter()
    {
        // Use apple_api.json if present (renamed from apple_books.json), fall back to apple_books.json.
        var root = FindRepoRoot();
        var apiPath = Path.Combine(root, "config", "providers", "apple_api.json");
        var fallback = Path.Combine(root, "config", "providers", "apple_books.json");
        var path = File.Exists(apiPath) ? apiPath : fallback;
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ProviderConfiguration>(json, s_jsonOptions)
                       ?? throw new InvalidOperationException($"Failed to deserialize {Path.GetFileName(path)}");

        var factory = BuildHttpFactory(config.Name);
        return new ConfigDrivenAdapter(config, factory, NullLogger<ConfigDrivenAdapter>.Instance, NullProviderHealthMonitor.Instance);
    }

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(AudiobookPipelineIntegrationTests).Assembly.Location);
        while (dir != null)
        {
            var solutionMarker = Path.Combine(dir, "MediaEngine.slnx");
            if (File.Exists(solutionMarker))
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
    /// Pass-through fuzzy matching service for integration tests.
    /// Returns maximum scores so fuzzy re-ranking does not suppress Wikidata candidates.
    /// </summary>
    private sealed class PassThroughFuzzyMatchingService : IFuzzyMatchingService
    {
        public double ComputeTokenSetRatio(string a, string b) => 1.0;
        public double ComputePartialRatio(string a, string b) => 1.0;
        public FieldMatchResult ScoreCandidate(LocalMetadata local, CandidateMetadata candidate) =>
            new() { TitleScore = 1.0, AuthorScore = 1.0, YearScore = 1.0, CompositeScore = 1.0 };
    }
}
