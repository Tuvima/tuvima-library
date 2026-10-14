using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Regression tests for four pipeline bugs discovered 2026-04-10:
///
/// <list type="number">
///   <item>Bug 1 — field_priorities.json referenced non-existent provider names, blocking cover art.</item>
///   <item>Bug 2 — visibility filter excluded staging-only review items from the Action Center list.</item>
///   <item>Bug 3 — Priority Cascade Tier C ignored confidence for authors, breaking pen names (tested in PriorityCascadeRestrictionTests).</item>
///   <item>Bug 4 — cirrus-type-filters.json was a stale snapshot; consolidated into instance_of_classes.</item>
/// </list>
/// </summary>
public sealed class PipelineBugRegressionTests
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // ── Bug 1: field_priorities.json must reference real provider names ──

    [Fact]
    public void FieldPriorities_AllProviderNamesExistInProviderConfigs()
    {
        var root = FindRepoRoot();

        // Collect all provider names from config/providers/*.json
        var providerDir = Path.Combine(root, "config", "providers");
        var providerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(providerDir, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("name", out var nameProp))
            {
                providerNames.Add(nameProp.GetString()!);
            }
        }

        // Parse field_priorities.json and verify every referenced provider exists
        var fpPath = Path.Combine(root, "config", "field_priorities.json");
        Assert.True(File.Exists(fpPath), $"field_priorities.json not found at {fpPath}");

        using var fpDoc = JsonDocument.Parse(File.ReadAllText(fpPath));
        var overrides = fpDoc.RootElement.GetProperty("field_overrides");

        foreach (var field in overrides.EnumerateObject())
        {
            if (!field.Value.TryGetProperty("priority", out var priorityArr))
            {
                continue;
            }

            foreach (var providerRef in priorityArr.EnumerateArray())
            {
                var name = providerRef.GetString()!;
                Assert.True(providerNames.Contains(name),
                    $"field_priorities.json field \"{field.Name}\" references provider " +
                    $"\"{name}\" which does not exist in config/providers/. " +
                    $"Known providers: [{string.Join(", ", providerNames.Order())}]");
            }
        }
    }

    [Fact]
    public void FieldPriorities_CoverFieldHasAtLeastOneProvider()
    {
        var root = FindRepoRoot();
        var json = File.ReadAllText(Path.Combine(root, "config", "field_priorities.json"));
        using var doc = JsonDocument.Parse(json);

        var cover = doc.RootElement.GetProperty("field_overrides").GetProperty("cover");
        var providers = cover.GetProperty("priority");

        Assert.True(providers.GetArrayLength() >= 1,
            "Cover field priority must list at least one provider for cover art to download.");
    }

    // ── Bug 4: instance_of_classes must include manga for Comics ─────────

    [Fact]
    public void InstanceOfClasses_ComicsIncludesManga()
    {
        var root = FindRepoRoot();
        var json = File.ReadAllText(
            Path.Combine(root, "config", "providers", "wikidata_reconciliation.json"));
        var config = JsonSerializer.Deserialize<ReconciliationProviderConfig>(json, s_jsonOpts);
        Assert.NotNull(config);

        Assert.True(config!.InstanceOfClasses.ContainsKey("Comics"),
            "instance_of_classes must contain a 'Comics' key.");

        var comicTypes = config.InstanceOfClasses["Comics"];

        // Q1004 = comics, Q838795 = comic (alternative), Q21198342 = manga
        Assert.Contains("Q1004", comicTypes);     // western comics
        Assert.Contains("Q21198342", comicTypes);  // manga — was missing in the stale cirrus-type-filters.json
    }

    [Fact]
    public void InstanceOfClasses_MoviesIncludesAnimatedAndAnimeFilms()
    {
        var root = FindRepoRoot();
        var json = File.ReadAllText(
            Path.Combine(root, "config", "providers", "wikidata_reconciliation.json"));
        var config = JsonSerializer.Deserialize<ReconciliationProviderConfig>(json, s_jsonOpts);
        Assert.NotNull(config);

        Assert.True(config!.InstanceOfClasses.ContainsKey("Movies"),
            "instance_of_classes must contain a 'Movies' key.");

        var movieTypes = config.InstanceOfClasses["Movies"];

        // Q202866 = animated film, Q20650540 = anime film.
        // Spirited Away is directly instance_of Q20650540, so direct QID and
        // title searches must keep it in the Movies result set.
        Assert.Contains("Q202866", movieTypes);
        Assert.Contains("Q20650540", movieTypes);
    }

    [Fact]
    public void InstanceOfClasses_CoversAllResolvableMediaTypes()
    {
        var root = FindRepoRoot();
        var json = File.ReadAllText(
            Path.Combine(root, "config", "providers", "wikidata_reconciliation.json"));
        var config = JsonSerializer.Deserialize<ReconciliationProviderConfig>(json, s_jsonOpts);
        Assert.NotNull(config);

        // Every media type that the adapter resolves via text fallback must
        // have at least one P31 class in instance_of_classes.
        var resolvableTypes = new[]
        {
            MediaType.Books, MediaType.Audiobooks, MediaType.Movies,
            MediaType.TV, MediaType.Music, MediaType.Comics,
        };

        foreach (var mt in resolvableTypes)
        {
            var key = mt.ToString();
            Assert.True(
                config!.InstanceOfClasses.ContainsKey(key) && config.InstanceOfClasses[key].Count > 0,
                $"instance_of_classes must contain a non-empty entry for \"{key}\". " +
                $"Without it, CirrusSearch text fallback is disabled for {mt}.");
        }
    }

    [Fact]
    public void CirrusTypeFiltersJson_DoesNotExist()
    {
        // The stale config file was consolidated into instance_of_classes.
        // If it's ever re-created, it will drift again.
        var root = FindRepoRoot();
        var staleFile = Path.Combine(root, "config", "cirrus-type-filters.json");
        Assert.False(File.Exists(staleFile),
            "cirrus-type-filters.json must not exist — CirrusSearch type filters " +
            "are consolidated into instance_of_classes in wikidata_reconciliation.json.");
    }

    // ── Config consolidation: edition-pivot merged into wikidata_reconciliation ──

    [Fact]
    public void EditionPivotJson_DoesNotExist()
    {
        // edition-pivot.json was consolidated into wikidata_reconciliation.json.
        // If it's ever re-created, it will drift.
        var root = FindRepoRoot();
        var staleFile = Path.Combine(root, "config", "edition-pivot.json");
        Assert.False(File.Exists(staleFile),
            "edition-pivot.json must not exist — edition pivot rules are consolidated " +
            "into the edition_pivot section of wikidata_reconciliation.json.");
    }

    [Fact]
    public void SlotsJson_DoesNotExist()
    {
        // slots.json was fully superseded by pipelines.json.
        var root = FindRepoRoot();
        var staleFile = Path.Combine(root, "config", "slots.json");
        Assert.False(File.Exists(staleFile),
            "slots.json must not exist — provider slot assignments are consolidated " +
            "into pipelines.json with the ranked pipeline system.");
    }

    [Fact]
    public void UniverseWikidataJson_DoesNotExist()
    {
        // config/universe/wikidata.json was dead configuration (nothing loaded it).
        // Property map moved to docs/reference/wikidata-property-map.md.
        // instance_of_classes consolidated into wikidata_reconciliation.json.
        var root = FindRepoRoot();
        var staleFile = Path.Combine(root, "config", "universe", "wikidata.json");
        Assert.False(File.Exists(staleFile),
            "config/universe/wikidata.json must not exist — property map is in docs, " +
            "instance_of_classes consolidated into wikidata_reconciliation.json.");
    }

    // ── Dead config files: never re-create these ─────────────────────────

    [Fact]
    public void DescriptionMatchingJson_DoesNotExist()
    {
        // description_matching.json was dead configuration (nothing loaded it).
        // Removed to prevent stale config from drifting silently.
        var root = FindRepoRoot();
        var staleFile = Path.Combine(root, "config", "description_matching.json");
        Assert.False(File.Exists(staleFile),
            "config/description_matching.json must not exist — it was dead configuration " +
            "with no loader. Do not re-create it.");
    }

    [Fact]
    public void FieldNormalizationJson_DoesNotExist()
    {
        // field_normalization.json was dead configuration (nothing loaded it).
        // FieldDictionary.cs (Web) was the in-code equivalent and has also been removed.
        var root = FindRepoRoot();
        var staleFile = Path.Combine(root, "config", "field_normalization.json");
        Assert.False(File.Exists(staleFile),
            "config/field_normalization.json must not exist — it was dead configuration " +
            "with no loader. Do not re-create it.");
    }

    [Fact]
    public void CollectionsJson_DoesNotExist()
    {
        // collections.json was dead configuration (nothing loaded it).
        // CollectionDisplaySettings.cs (Storage) was the corresponding unused model and has also been removed.
        var root = FindRepoRoot();
        var staleFile = Path.Combine(root, "config", "collections.json");
        Assert.False(File.Exists(staleFile),
            "config/collections.json must not exist — it was dead configuration " +
            "with no loader. Do not re-create it.");
    }

    [Fact]
    public void MediaTypeIdsJson_DoesNotExist()
    {
        // media_type_ids.json was dead configuration (nothing loaded it).
        // Removed to prevent stale config from drifting silently.
        var root = FindRepoRoot();
        var staleFile = Path.Combine(root, "config", "media_type_ids.json");
        Assert.False(File.Exists(staleFile),
            "config/media_type_ids.json must not exist — it was dead configuration " +
            "with no loader. Do not re-create it.");
    }

    [Fact]
    public void TanasteJson_DoesNotExist()
    {
        // tanaste.json was dead configuration (nothing loaded it).
        // Removed to prevent stale config from drifting silently.
        var root = FindRepoRoot();
        var staleFile = Path.Combine(root, "config", "tanaste.json");
        Assert.False(File.Exists(staleFile),
            "config/tanaste.json must not exist — it was dead configuration " +
            "with no loader. Do not re-create it.");
    }

    [Fact]
    public void WikidataReconciliation_ContainsEditionPivot()
    {
        // Verify the edition_pivot section exists in the consolidated config.
        var root = FindRepoRoot();
        var json = File.ReadAllText(
            Path.Combine(root, "config", "providers", "wikidata_reconciliation.json"));
        var config = JsonSerializer.Deserialize<ReconciliationProviderConfig>(json, s_jsonOpts);
        Assert.NotNull(config);

        var editionPivot = config!.GetEditionPivotConfiguration();
        var audiobookRule = editionPivot.GetRuleFor(MediaType.Audiobooks);
        Assert.NotNull(audiobookRule);
        Assert.True(audiobookRule!.PreferEdition,
            "Audiobooks must have prefer_edition = true for P747 edition discovery.");
        Assert.Contains("Q122731938", audiobookRule.EditionClasses);

        var bookRule = editionPivot.GetRuleFor(MediaType.Books);
        Assert.NotNull(bookRule);
        Assert.False(bookRule!.PreferEdition);

        // Movies, TV, Comics should not have edition pivot rules.
        Assert.Null(editionPivot.GetRuleFor(MediaType.Movies));
        Assert.Null(editionPivot.GetRuleFor(MediaType.TV));
        Assert.Null(editionPivot.GetRuleFor(MediaType.Comics));
    }

    [Fact]
    public void PipelinesJson_CoversAllMediaTypes()
    {
        var root = FindRepoRoot();
        var json = File.ReadAllText(Path.Combine(root, "config", "pipelines.json"));
        using var doc = JsonDocument.Parse(json);

        var expectedTypes = new[] { "Books", "Audiobooks", "Movies", "TV", "Music", "Comics" };
        foreach (var mt in expectedTypes)
        {
            Assert.True(doc.RootElement.TryGetProperty(mt, out var pipeline),
                $"pipelines.json must contain a '{mt}' entry.");
            Assert.True(pipeline.TryGetProperty("providers", out var providers),
                $"pipelines.json '{mt}' must have a 'providers' array.");
            Assert.True(providers.GetArrayLength() >= 1,
                $"pipelines.json '{mt}' must have at least one provider.");
        }
    }

    // ── Bug 2: visibility filter regression (structural) ────────────────

    [Fact]
    public void VisibilityFilter_ReviewItemsDoNotRequireNonStagingAsset()
    {
        // This is a structural test that reads the LibraryItemRepository source
        // to verify the visibility filter doesn't re-introduce the staging
        // asset requirement for review items. The old bug required
        // `rd.review_id IS NOT NULL AND ad.asset_id IS NOT NULL` which
        // excluded items whose only files were still in .data/staging/.
        var root = FindRepoRoot();
        var repoSource = File.ReadAllText(
            Path.Combine(root, "src", "MediaEngine.Storage", "LibraryItemRepository.cs"));

        // The projection must classify pending review items directly as review_only
        // without requiring any non-staging asset alias in the filter.
        Assert.Contains("rd.review_id IS NOT NULL", repoSource);
        Assert.Contains("rd.review_trigger != 'WritebackFailed'", repoSource);
        Assert.Contains("THEN 'review_only'", repoSource);

        // The old buggy pattern must NOT be present.
        Assert.DoesNotContain(
            "rd.review_id IS NOT NULL AND ad.asset_id IS NOT NULL",
            repoSource);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(PipelineBugRegressionTests).Assembly.Location);
        while (dir is not null)
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
