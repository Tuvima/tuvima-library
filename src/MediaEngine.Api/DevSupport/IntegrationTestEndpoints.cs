using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.DevSupport;

/// <summary>
/// Integration test endpoint that runs a full ingestion cycle, validates each media type,
/// tests manual search, verifies universe enrichment, and produces an HTML report.
///
///   POST /dev/integration-test  — Full cycle: wipe → seed → ingest → validate → report (HTML)
/// </summary>
public static class IntegrationTestEndpoints
{
    // ── Test case definitions ──────────────────────────────────────────────
    //
    // Expectations are now read at runtime from DevSeedEndpoints.GetAllExpectations()
    // so the seed records themselves are the single source of truth. The previous
    // hardcoded TestExpectation[] arrays drifted out of sync with the seed list and
    // were never actually consulted by the reconciliation pass.

    // ── Test result models ────────────────────────────────────────────────

    private sealed class TestReport
    {
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
        public TimeSpan TotalDuration { get; set; }
        public string WipeStatus { get; set; } = "";
        public string SeedStatus { get; set; } = "";
        public int TotalFilesSeeded { get; set; }
        public TimeSpan IngestionDuration { get; set; }
        /// <summary>Stage level tested: 1, 12, or 123.</summary>
        public int StagesLevel { get; set; } = 12;
        public ExpectationPreflightSummary ExpectationPreflight { get; set; } = new();
        public List<MediaTypeResult> MediaTypeResults { get; set; } = [];
        public List<ManualSearchResult> ManualSearchResults { get; set; } = [];
        public List<UniverseResult> UniverseResults { get; set; } = [];
        public List<LibraryCheckResult> LibraryChecks { get; set; } = [];
        public List<FileSystemCheckResult> FileSystemChecks { get; set; } = [];
        public List<WatchFolderCheckResult> WatchFolderChecks { get; set; } = [];
        public List<StageGatingResult> StageGatingResults { get; set; } = [];
        public List<IngestionProgressSnapshot> IngestionProgressSnapshots { get; set; } = [];
        public List<IngestionWaitStageResult> IngestionWaitStages { get; set; } = [];
        public List<Stage3ArtworkSummary> Stage3ArtworkSummaries { get; set; } = [];
        public List<SeriesHarnessCheckResult> SeriesHarnessChecks { get; set; } = [];
        public List<CrossMediaSeriesCheckResult> CrossMediaSeriesChecks { get; set; } = [];
        public List<PersonCoverageCheckResult> PersonCoverageChecks { get; set; } = [];
        public List<DetailRouteCheckResult> DetailRouteChecks { get; set; } = [];
        public List<MusicAlbumHarnessCheckResult> MusicAlbumChecks { get; set; } = [];
        public List<CharacterArtworkCheckResult> CharacterArtworkChecks { get; set; } = [];
        public List<DescriptionSourceCheckResult> DescriptionSourceChecks { get; set; } = [];
        public List<string> IssuesFound { get; set; } = [];
        public List<string> FixesApplied { get; set; } = [];
        public int TotalItems { get; set; }
        public int TotalIdentified { get; set; }
        public int TotalNeedsReview { get; set; }
        public int TotalFailed { get; set; }
        public bool OverallPass =>
            IssuesFound.Count == 0
            && (Reconciliation is null || Reconciliation.Mismatches.Count == 0)
            && SkippedTypes.Keys.All(type => !RequestedTypes.Contains(type))
            && MediaTypeResults.All(result => result.Pass)
            && ManualSearchResults.All(result => result.Pass)
            && LibraryChecks.All(result => result.Pass)
            && FileSystemChecks.All(result => result.Pass)
            && WatchFolderChecks.All(result => result.Pass)
            && StageGatingResults.All(result => result.Pass)
            && IngestionProgressSnapshots.All(result => result.Pass)
            && IngestionWaitStages.All(result => result.Pass)
            && Stage3ArtworkSummaries.All(result => result.Pass)
            && SeriesHarnessChecks.All(result => result.Pass)
            && CrossMediaSeriesChecks.All(result => result.Pass)
            && PersonCoverageChecks.All(result => result.Pass)
            && DetailRouteChecks.All(result => result.Pass)
            && MusicAlbumChecks.All(result => result.Pass)
            && CharacterArtworkChecks.All(result => result.Pass)
            && DescriptionSourceChecks.All(result => result.Pass);
        public HashSet<string> RequestedTypes { get; set; } = [];
        public HashSet<string> ActiveTypes { get; set; } = [];
        public Dictionary<string, string> SkippedTypes { get; set; } = [];
        public Dictionary<string, bool> ProviderHealth { get; set; } = [];
        public Dictionary<string, string> ProviderHealthDetails { get; set; } = [];
        public ReconciliationSummary? Reconciliation { get; set; }
        /// <summary>
        /// Structured reconciliation report for JSON consumers (e.g. CI tooling).
        /// Populated alongside <see cref="Reconciliation"/> in Phase 4d.
        /// </summary>
        public ReconciliationReport? ReconciliationReport { get; set; }
    }

    /// <summary>Per-item library display validation result.</summary>
    private sealed class LibraryCheckResult
    {
        public string Title { get; set; } = "";
        public string MediaType { get; set; } = "";
        public Guid EntityId { get; set; }
        public bool HasCoverArt { get; set; }
        public bool HasTitle { get; set; }
        public bool HasCreator { get; set; }
        public bool HasStatus { get; set; }
        public bool HasRetailMatch { get; set; }
        public bool HasWikidataQid { get; set; }
        public string? Status { get; set; }
        public string? CoverUrl { get; set; }
        public string? Creator { get; set; }
        public string? RetailMatch { get; set; }
        public string? WikidataQid { get; set; }
        public string? ExpectedRetailProvider { get; set; }
        public string? ActualRetailProvider { get; set; }
        public string? IdentityProvider { get; set; }
        public string? EnrichmentProviders { get; set; }
        public string? BridgeProvider { get; set; }
        public string? BridgeIdType { get; set; }
        public string? ArtworkCompleteness { get; set; }
        public bool HasExpectedRetailProvider { get; set; } = true;
        public bool RequiresCreator { get; set; } = true;
        public bool RequiresRetailProvider =>
            string.Equals(RetailMatch, "matched", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(ExpectedRetailProvider);
        public bool Pass =>
            HasTitle
            && (!RequiresCreator || HasCreator)
            && HasStatus
            && HasRetailMatch
            && (!RequiresRetailProvider || HasExpectedRetailProvider);
    }

    /// <summary>On-disk placement and artwork validation result.</summary>
    private sealed class FileSystemCheckResult
    {
        public string Title { get; set; } = "";
        public string MediaType { get; set; } = "";
        public string Status { get; set; } = "";
        public string? WikidataQid { get; set; }
        public string? ExpectedLocation { get; set; }
        public string? ExpectedRelativePath { get; set; }
        public string? ActualFilePath { get; set; }
        public string? ActualDisplayPath { get; set; }
        public bool FileExists { get; set; }
        public bool InLibraryRoot { get; set; }
        public bool InStagingRoot { get; set; }
        public bool LocationMatchesExpectation { get; set; }
        public bool RequiresTemplateMatch { get; set; }
        public bool PathMatchesTemplate { get; set; }
        public bool ExpectedCoverArt { get; set; }
        public bool RequiresSidecarArtwork { get; set; }
        public bool HasPoster { get; set; }
        public bool HasPosterThumb { get; set; }
        public bool HasLegacyHeroSidecar { get; set; }
        public bool RequiresStoredArtwork { get; set; }
        public bool HasStoredCover { get; set; }
        public bool HasStoredCoverSmall { get; set; }
        public bool HasStoredCoverMedium { get; set; }
        public bool HasStoredCoverLarge { get; set; }
        public bool HasStoredPalette { get; set; }
        public bool HasStoredLegacyHero { get; set; }
        public bool HasStoredBackground { get; set; }
        public bool HasStoredLogo { get; set; }
        public bool HasStoredBanner { get; set; }
        public bool HasStoredSeasonPoster { get; set; }
        public bool HasStoredSeasonThumb { get; set; }
        public bool HasStoredEpisodeStill { get; set; }
        public bool HasArtworkBridgeId { get; set; }
        public string? Detail { get; set; }
        public bool Pass =>
            FileExists
            && LocationMatchesExpectation
            && (!RequiresTemplateMatch || PathMatchesTemplate)
            && (!RequiresSidecarArtwork || (HasPoster && HasPosterThumb))
            && (!RequiresStoredArtwork || (HasStoredCover && HasStoredCoverSmall && HasStoredCoverMedium && HasStoredCoverLarge && HasStoredPalette))
            && !HasLegacyHeroSidecar
            && !HasStoredLegacyHero;
    }

    /// <summary>Checks that watch/source folders were drained after ingestion.</summary>
    private sealed class WatchFolderCheckResult
    {
        public string Directory { get; set; } = "";
        public int RemainingMediaFiles { get; set; }
        public int IgnoredExpectedStagingFiles { get; set; }
        public bool Pass => RemainingMediaFiles == 0;
    }

    /// <summary>Stage gating validation result.</summary>
    private sealed class StageGatingResult
    {
        public string Title { get; set; } = "";
        public string Check { get; set; } = "";
        public bool Pass { get; set; }
        public string? Detail { get; set; }
    }

    private sealed class ExpectationPreflightSummary
    {
        public int TotalFiles { get; set; }
        public int ExpectedResolved { get; set; }
        public int ExpectedExactQid { get; set; }
        public int ExpectedIdentityOnly { get; set; }
        public int ExpectedReview { get; set; }
        public int ExpectedKnownNoQid { get; set; }
        public int ExpectedDuplicate { get; set; }
        public int ExpectedSkipped { get; set; }
        public int ExpectedCorrupt { get; set; }
        public List<ExpectationPreflightItem> Items { get; set; } = [];
    }

    private sealed class ExpectationPreflightItem
    {
        public string Title { get; set; } = "";
        public string MediaType { get; set; } = "";
        public string ExpectedStatus { get; set; } = "";
        public string? ExpectedQid { get; set; }
        public string? ExpectedProvider { get; set; }
        public string? ExpectedReviewTrigger { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>Point-in-time ingestion count sample used to catch impossible progress math.</summary>
    private sealed class IngestionProgressSnapshot
    {
        public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
        public int AssetCount { get; set; }
        public int ExpectedCount { get; set; }
        public int ResolvedCount { get; set; }
        public int WorkCount { get; set; }
        public int PendingCount { get; set; }
        public int ClaimCount { get; set; }
        public int ActiveJobCount { get; set; }
        public int StableSnapshots { get; set; }
        public double ProgressPercent { get; set; }
        public bool Pass =>
            AssetCount >= 0
            && ExpectedCount >= 0
            && ResolvedCount >= 0
            && WorkCount >= 0
            && PendingCount >= 0
            && ClaimCount >= 0
            && ActiveJobCount >= 0
            && ProgressPercent is >= 0 and <= 100
            && (ExpectedCount == 0 || AssetCount <= ExpectedCount || ProgressPercent == 100);
    }

    private sealed class IngestionWaitStageResult
    {
        public string StageKey { get; set; } = "";
        public string Label { get; set; } = "";
        public double BudgetSeconds { get; set; }
        public double ElapsedSeconds { get; set; }
        public bool TimedOut { get; set; }
        public bool Completed { get; set; }
        public bool CompletedAfterTimeout { get; set; }
        public string? Detail { get; set; }
        public bool Pass => Completed || CompletedAfterTimeout || !TimedOut;
    }

    private sealed record IngestionWaitPlan(
        TimeSpan FileRegistration,
        TimeSpan IdentityResolution,
        TimeSpan Stage3Enrichment,
        TimeSpan FinalIdle)
    {
        public TimeSpan Total => FileRegistration + IdentityResolution + Stage3Enrichment + FinalIdle;

        public TimeSpan BudgetFor(string stageKey) => stageKey switch
        {
            WaitStageFileRegistration => FileRegistration,
            WaitStageIdentityResolution => IdentityResolution,
            WaitStageStage3Enrichment => Stage3Enrichment,
            WaitStageFinalIdle => FinalIdle,
            _ => FinalIdle,
        };
    }

    // ── Reconciliation models ─────────────────────────────────────────────

    /// <summary>Type-level evidence that Stage 3 TMDB artwork assets were stored.</summary>
    private sealed class Stage3ArtworkSummary
    {
        public string MediaType { get; set; } = "";
        public int EligibleCount { get; set; }
        public int WithAnyArtwork { get; set; }
        public int WithBackground { get; set; }
        public int WithLogo { get; set; }
        public int WithBanner { get; set; }
        public int WithSeasonPoster { get; set; }
        public int WithSeasonThumb { get; set; }
        public int WithEpisodeStill { get; set; }
        public bool Pass =>
            EligibleCount == 0
            || (WithAnyArtwork > 0
                && (!string.Equals(MediaType, "TV", StringComparison.OrdinalIgnoreCase)
                    || WithEpisodeStill > 0));
    }

    private sealed class DescriptionSourceCheckResult
    {
        public string Title { get; set; } = "";
        public string MediaType { get; set; } = "";
        public Guid EntityId { get; set; }
        public string DescriptionKey { get; set; } = "";
        public bool RequiresWikipediaDescription { get; set; }
        public bool RequiresRetailDescription { get; set; }
        public bool HasWikipediaDescription { get; set; }
        public bool HasRetailDescription { get; set; }
        public bool HasAnyDescription { get; set; }
        public bool AllowsGeneratedFallback { get; set; }
        public bool CanonicalUsesWikipedia { get; set; }
        public string? CanonicalProvider { get; set; }
        public bool HasTagline { get; set; }
        public string? Detail { get; set; }

        public bool Pass =>
            (HasAnyDescription || AllowsGeneratedFallback)
            && (!HasWikipediaDescription || CanonicalUsesWikipedia);
    }

    private sealed class OptionalArtworkState
    {
        public bool HasBackground { get; set; }
        public bool HasLogo { get; set; }
        public bool HasBanner { get; set; }
        public bool HasSeasonPoster { get; set; }
        public bool HasSeasonThumb { get; set; }
        public bool HasEpisodeStill { get; set; }

        public void Merge(OptionalArtworkState other)
        {
            HasBackground |= other.HasBackground;
            HasLogo |= other.HasLogo;
            HasBanner |= other.HasBanner;
            HasSeasonPoster |= other.HasSeasonPoster;
            HasSeasonThumb |= other.HasSeasonThumb;
            HasEpisodeStill |= other.HasEpisodeStill;
        }

        public bool HasAny =>
            HasBackground
            || HasLogo
            || HasBanner
            || HasSeasonPoster
            || HasSeasonThumb
            || HasEpisodeStill;
    }

    private sealed record PreferredArtworkRecord(
        string LocalImagePath,
        string? LocalImagePathSmall,
        string? LocalImagePathMedium,
        string? LocalImagePathLarge,
        string? PrimaryHex,
        string? SecondaryHex,
        string? AccentHex);

    private sealed record WorkHierarchyNode(Guid WorkId, Guid? ParentWorkId);

    private sealed class ReconciliationItemResult
    {
        public string Title { get; set; } = "";
        public string MediaType { get; set; } = "";
        /// <summary>What the seed fixture declared (Identified or InReview with trigger).</summary>
        public string Expected { get; set; } = "";
        /// <summary>What the pipeline actually produced.</summary>
        public string Actual { get; set; } = "";
        public string? ExpectedQid { get; set; }
        public string? ActualQid { get; set; }
        public string? ExpectedProvider { get; set; }
        public string? ActualProvider { get; set; }
        public string? ExpectedTrigger { get; set; }
        public string? ActualTrigger { get; set; }
        /// <summary>Match / UnexpectedReview / UnexpectedIdentified / WrongTrigger / NotFound</summary>
        public string Classification { get; set; } = "";
        /// <summary>Human-readable explanation from the seed fixture, or a generated note.</summary>
        public string? Reason { get; set; }
    }

    private sealed class ReconciliationSummary
    {
        public int ExpectedTotal { get; set; }
        public int ExpectedResolved { get; set; }
        public int ActualResolved { get; set; }
        public int ExpectedExactQid { get; set; }
        public int ActualExactQid { get; set; }
        public int ExpectedReview { get; set; }
        public int ActualReview { get; set; }
        public int Matched { get; set; }
        public List<ReconciliationItemResult> Items { get; set; } = [];
        public List<ReconciliationItemResult> Mismatches { get; set; } = [];
        public Dictionary<string, int> ByClassification { get; set; } = new()
        {
            ["Match"] = 0,
            ["UnexpectedReview"] = 0,
            ["UnexpectedIdentified"] = 0,
            ["WrongTrigger"] = 0,
            ["NotFound"] = 0,
            ["WrongQid"] = 0,
            ["MissingCoverArt"] = 0,
            ["WrongProvider"] = 0,
        };
    }

    private sealed class MediaTypeResult
    {
        public string MediaType { get; set; } = "";
        public int Count { get; set; }
        public int OwnedCount { get; set; }
        public int CatalogOnlyCount { get; set; }
        public int Identified { get; set; }
        public int NeedsReview { get; set; }
        public int Failed { get; set; }
        public List<ItemResult> Items { get; set; } = [];
        public bool Pass => Failed == 0 && Count > 0;
    }

    private sealed class ItemResult
    {
        public string Title { get; set; } = "";
        public string FileName { get; set; } = "";
        public string? FilePath { get; set; }
        public string MediaType { get; set; } = "";
        public string Status { get; set; } = "";
        public double Confidence { get; set; }
        public string? RetailMatch { get; set; }
        public string? WikidataQid { get; set; }
        public string? ReviewTrigger { get; set; }
        public string? Author { get; set; }
        public string? Year { get; set; }
    }

    private sealed class ManualSearchResult
    {
        public string Query { get; set; } = "";
        public string ProviderName { get; set; } = "";
        public string MediaType { get; set; } = "";
        public int ResultCount { get; set; }
        public string? TopResultTitle { get; set; }
        public double TopResultConfidence { get; set; }
        public string? Error { get; set; }
        public bool Pass => ResultCount > 0 && Error is null;
    }

    private sealed class UniverseResult
    {
        public string Name { get; set; } = "";
        public string? WikidataQid { get; set; }
        public int WorkCount { get; set; }
        public int SeriesCount { get; set; }
        public bool Found { get; set; }
    }

    private sealed class SeriesHarnessCheckResult
    {
        public string Series { get; set; } = "";
        public string MediaType { get; set; } = "";
        public int OwnedCount { get; set; }
        public int KnownCount { get; set; }
        public int ExpectedMinimumKnown { get; set; }
        public bool HasManifest { get; set; }
        public bool Pass => OwnedCount > 0 && KnownCount >= ExpectedMinimumKnown;
        public string Detail => Pass
            ? $"{OwnedCount} owned item(s), {KnownCount} known series item(s)."
            : $"{OwnedCount} owned item(s), {KnownCount} known series item(s); expected at least {ExpectedMinimumKnown}.";
    }

    private sealed class MusicAlbumHarnessCheckResult
    {
        public string Album { get; set; } = "";
        public string Artist { get; set; } = "";
        public int OwnedTrackCount { get; set; }
        public int? KnownTrackCount { get; set; }
        public int ExpectedOwnedTrackCount { get; set; }
        public int? ExpectedKnownTrackCount { get; set; }
        public bool HasManifest { get; set; }
        public bool HasCover { get; set; }
        public bool RequiresManifest => ExpectedKnownTrackCount.HasValue;
        public bool Pass =>
            OwnedTrackCount >= ExpectedOwnedTrackCount
            && (!RequiresManifest
                || (HasManifest && KnownTrackCount == ExpectedKnownTrackCount))
            && HasCover;
        public string Detail => Pass
            ? $"{OwnedTrackCount} owned, {KnownTrackCount?.ToString() ?? "provider-specific"} known, cover present."
            : $"{OwnedTrackCount} owned (expected {ExpectedOwnedTrackCount}), {KnownTrackCount?.ToString() ?? "no"} known (expected {ExpectedKnownTrackCount?.ToString() ?? "provider-specific"}), manifest={HasManifest}, cover={HasCover}.";
    }

    // ── Dynamic type selection + provider health ─────────────────────────

    private sealed class CrossMediaSeriesCheckResult
    {
        public string Name { get; set; } = "";
        public List<string> RequiredMediaTypes { get; set; } = [];
        public List<string> FoundMediaTypes { get; set; } = [];
        public int OwnedCount { get; set; }
        public bool HasSharedParentCollection { get; set; }
        public bool HasSharedNarrativeRoot { get; set; }
        public bool HasAllRequiredMediaTypes =>
            RequiredMediaTypes.All(required =>
                FoundMediaTypes.Any(found => string.Equals(found, required, StringComparison.OrdinalIgnoreCase)));
        public bool HasCrossMediaLink => HasSharedParentCollection;
        public bool Pass => OwnedCount > 0 && HasAllRequiredMediaTypes && HasCrossMediaLink;
        public string Detail => Pass
            ? $"Found {string.Join(", ", FoundMediaTypes)} with shared cross-media linkage."
            : $"Found {string.Join(", ", FoundMediaTypes.DefaultIfEmpty("none"))}; required {string.Join(", ", RequiredMediaTypes)}; shared parent={HasSharedParentCollection}; shared narrative root={HasSharedNarrativeRoot}.";
    }

    private sealed class PersonCoverageCheckResult
    {
        public string Name { get; set; } = "";
        public bool PersonFound { get; set; }
        public List<PersonCreditRequirementResult> Requirements { get; set; } = [];
        public bool Pass => PersonFound && Requirements.All(requirement => requirement.Pass);
        public string Detail => !PersonFound
            ? "No canonical person record was found."
            : string.Join(
                "; ",
                Requirements.Select(requirement =>
                    $"{requirement.MediaType}/{requirement.Role}"
                    + (string.IsNullOrWhiteSpace(requirement.TitlePattern) ? "" : $" containing '{requirement.TitlePattern}'")
                    + $": {requirement.ActualCount}/{requirement.MinimumCount}"));
    }

    private sealed class DetailRouteCheckResult
    {
        public string Surface { get; set; } = "";
        public string Title { get; set; } = "";
        public string Route { get; set; } = "";
        public bool Resolved { get; set; }
        public bool UsesExpectedTabs { get; set; } = true;
        public bool SuppressesCollectionCredits { get; set; } = true;
        public bool Pass => Resolved && UsesExpectedTabs && SuppressesCollectionCredits;
        public string Detail => !Resolved
            ? "The outward browse ID did not compose a canonical detail page."
            : !UsesExpectedTabs
                ? "The detail page did not use the expected shared navigation contract."
                : !SuppressesCollectionCredits
                    ? "A standard collection exposed a media-credit shelf."
                    : "The outward browse ID resolves through the canonical detail composer.";
    }

    private sealed class PersonCreditRequirementResult
    {
        public string MediaType { get; set; } = "";
        public string Role { get; set; } = "";
        public string? TitlePattern { get; set; }
        public int MinimumCount { get; set; }
        public int ActualCount { get; set; }
        public bool Pass => ActualCount >= MinimumCount;
    }

    private static readonly string[] AllTestableTypes = ["books", "audiobooks", "movies", "tv", "music", "comics"];
    private const string WaitStageFileRegistration = "file_registration";
    private const string WaitStageIdentityResolution = "identity_resolution";
    private const string WaitStageStage3Enrichment = "stage3_enrichment";
    private const string WaitStageFinalIdle = "final_idle";
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".webm", ".ts",
        ".m4b", ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wav", ".aac",
        ".epub", ".pdf",
        ".cbz", ".cbr", ".cb7",
    };

    private static readonly Dictionary<string, string[]> ProviderToTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["apple_api"] = ["books", "audiobooks", "music"],
        ["musicbrainz"] = ["music"],
        ["tmdb"] = ["movies", "tv"],
        ["comicvine"] = ["comics"],
    };

    private static readonly Dictionary<string, string[]> TypeProviderRequirements = new(StringComparer.OrdinalIgnoreCase)
    {
        ["books"] = ["apple_api"],
        ["audiobooks"] = ["apple_api"],
        ["music"] = ["musicbrainz", "apple_api"],
        ["movies"] = ["tmdb"],
        ["tv"] = ["tmdb"],
        ["comics"] = ["comicvine"],
    };

    private static readonly Dictionary<string, string> ProviderHealthUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["apple_api"] = "https://itunes.apple.com/search?term=test&limit=1",
        ["musicbrainz"] = "https://musicbrainz.org/ws/2/recording?query=bohemian%20rhapsody&fmt=json&limit=1",
        ["tmdb"] = "https://api.themoviedb.org/3/configuration",
        ["comicvine"] = "https://comicvine.gamespot.com/api/search/?query=batman&resources=issue&limit=1&format=json&api_key=placeholder",
    };
    private const int ProviderHealthTimeoutSeconds = 15;

    private static HashSet<string> ParseTypes(HttpContext context)
    {
        if (context.Request.Query.TryGetValue("types", out var typesParam) && !string.IsNullOrWhiteSpace(typesParam))
        {
            return typesParam.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToLowerInvariant())
                .Where(t => AllTestableTypes.Contains(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return new HashSet<string>(AllTestableTypes, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<string, (bool Healthy, string Reason, string Url, TimeSpan Elapsed, int TimeoutSeconds)>> CheckProviderHealthAsync(ILogger logger)
    {
        var results = new Dictionary<string, (bool, string, string, TimeSpan, int)>(StringComparer.OrdinalIgnoreCase);
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(ProviderHealthTimeoutSeconds) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TuvimaLibrary/1.0 (integration-test)");

        var tasks = ProviderHealthUrls.Select(async kvp =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var response = await httpClient.GetAsync(kvp.Value);
                sw.Stop();
                bool ok = response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Unauthorized;
                var reason = ok
                    ? $"OK in {sw.Elapsed.TotalSeconds:F1}s"
                    : $"HTTP {(int)response.StatusCode} in {sw.Elapsed.TotalSeconds:F1}s";
                return (kvp.Key, Healthy: ok, Reason: reason, Url: kvp.Value, Elapsed: sw.Elapsed, TimeoutSeconds: ProviderHealthTimeoutSeconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                var message = ex.Message;
                if (ex is TaskCanceledException && sw.Elapsed >= TimeSpan.FromSeconds(ProviderHealthTimeoutSeconds - 1))
                {
                    message = $"timed out after {ProviderHealthTimeoutSeconds}s";
                }

                return (
                    kvp.Key,
                    Healthy: false,
                    Reason: $"{ex.GetType().Name}: {message} (elapsed {sw.Elapsed.TotalSeconds:F1}s, timeout {ProviderHealthTimeoutSeconds}s)",
                    Url: kvp.Value,
                    Elapsed: sw.Elapsed,
                    TimeoutSeconds: ProviderHealthTimeoutSeconds);
            }
        });

        foreach (var result in await Task.WhenAll(tasks))
        {
            results[result.Key] = (result.Healthy, result.Reason, result.Url, result.Elapsed, result.TimeoutSeconds);
            logger.LogInformation("[HealthCheck] {Provider}: {Status} ({Reason})", result.Key,
                result.Healthy ? "HEALTHY" : "UNAVAILABLE", result.Reason);
        }

        return results;
    }

    private static (HashSet<string> ActiveTypes, Dictionary<string, string> SkipReasons) ResolveActiveTypes(
        HashSet<string> requestedTypes,
        Dictionary<string, (bool Healthy, string Reason, string Url, TimeSpan Elapsed, int TimeoutSeconds)> health)
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in AllTestableTypes)
        {
            if (!requestedTypes.Contains(type))
            {
                skipped[type] = "Not requested";
                continue;
            }

            if (!TypeProviderRequirements.TryGetValue(type, out var requiredProviders) || requiredProviders.Length == 0)
            {
                active.Add(type);
                continue;
            }

            var unavailable = requiredProviders
                .Where(provider => !health.TryGetValue(provider, out var status) || !status.Healthy)
                .Select(provider =>
                    health.TryGetValue(provider, out var status)
                        ? $"{provider} ({status.Reason})"
                        : $"{provider} (unknown)")
                .ToList();

            if (unavailable.Count == 0)
            {
                active.Add(type);
            }
            else
            {
                skipped[type] = $"Required provider(s) unavailable: {string.Join(", ", unavailable)}";
            }
        }

        return (active, skipped);
    }

    // ── Endpoint registration ─────────────────────────────────────────────

    public static void MapIntegrationTestEndpoints(this WebApplication app)
    {
        var group = app.MapDevelopmentGroup();

        group.MapPost("/integration-test", RunIntegrationTestAsync)
            .WithSummary("Full integration test: wipe -> seed -> ingest -> validate -> HTML report")
            .Produces(200, contentType: "text/html; charset=utf-8");

        // Development listing for tools/Test-BookIngestion.ps1; the product Engine has no item list.
        group.MapGet("/library-items", (string? search, int? limit, ILibraryItemRepository libraryItemRepo, CancellationToken ct) =>
                libraryItemRepo.GetPageAsync(new LibraryItemQuery(Offset: 0, Limit: Math.Clamp(limit ?? 50, 1, 5000), Search: search, IncludeAll: true), ct))
            .WithSummary("Development-only listing of every library item, including staging and all statuses.")
            .Produces(200, contentType: "application/json");
    }

    // ── POST /dev/integration-test ────────────────────────────────────────

    private static async Task<IResult> RunIntegrationTestAsync(
        HttpContext context,
        IDatabaseConnection db,
        IOptions<IngestionOptions> options,
        IConfigurationLoader configLoader,
        IIngestionEngine ingestionEngine,
        DevHarnessResetService resetService,
        IIdentityJobRepository identityJobRepo,
        ILibraryItemRepository libraryItemRepo,
        IReviewQueueRepository reviewRepo,
        ICanonicalValueArrayRepository canonicalArrayRepo,
        IPersonRepository personRepo,
        IPersonCreditReadService personCreditReadService,
        ICollectionBrowseReadService collectionBrowseReadService,
        DetailComposerService detailComposer,
        IEnumerable<IExternalMetadataProvider> providers,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("IntegrationTest");
        var report = new TestReport();
        var sw = Stopwatch.StartNew();

        // ── Parse optional stages parameter ─────────────────────────────
        // 1 = Stage 1 only (retail), 12 = Stage 1+2 (default), 123 = full pipeline
        int stages = 12;
        if (context.Request.Query.TryGetValue("stages", out var stagesParam) && int.TryParse(stagesParam, out var s))
        {
            stages = s;
        }

        report.StagesLevel = stages;

        DevHarnessWipeScope wipeScope;
        try
        {
            wipeScope = DevHarnessResetService.ParseScope(
                context.Request.Query.TryGetValue("wipeScope", out var scopeParam)
                    ? scopeParam.ToString()
                    : DevHarnessResetService.GeneratedStateScopeName);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        string stageLabel = stages switch
        {
            1 => "Stage 1 (Retail Identification only)",
            12 => "Stage 1+2 (Retail + Wikidata)",
            123 => "Stage 1+2+3 (Full pipeline including Universe Enrichment)",
            _ => $"Stage level {stages} (unknown - defaulting to 1+2)"
        };

        // ── Parse optional types parameter ──────────────────────────────
        var requestedTypes = ParseTypes(context);
        var health = await CheckProviderHealthAsync(logger);
        var (activeTypes, skipReasons) = ResolveActiveTypes(requestedTypes, health);
        report.RequestedTypes = requestedTypes;
        report.ActiveTypes = activeTypes;
        report.SkippedTypes = skipReasons;
        report.ProviderHealth = health.ToDictionary(h => h.Key, h => h.Value.Healthy);
        report.ProviderHealthDetails = health.ToDictionary(
            h => h.Key,
            h => $"{h.Value.Reason}; url={h.Value.Url}; elapsed={h.Value.Elapsed.TotalSeconds:F1}s; timeout={h.Value.TimeoutSeconds}s",
            StringComparer.OrdinalIgnoreCase);
        report.ExpectationPreflight = BuildExpectationPreflight(activeTypes, skipReasons);

        string typesLabel = string.Join(", ", activeTypes.OrderBy(t => t));

        logger.LogInformation("╔══════════════════════════════════════════╗");
        logger.LogInformation("║   INTEGRATION TEST — Starting            ║");
        logger.LogInformation("║   {StageLabel}                           ║", stageLabel);
        logger.LogInformation("║   Active types: {Types}                  ║", typesLabel);
        if (skipReasons.Count > 0)
        {
            logger.LogInformation("║   Skipped: {Skipped}                     ║",
                string.Join(", ", skipReasons.Select(s => $"{s.Key} ({s.Value})")));
        }

        logger.LogInformation("╚══════════════════════════════════════════╝");

        try
        {
            // ── Phase 1: Wipe ─────────────────────────────────────────────────
            LogExpectationPreflight(report.ExpectationPreflight, logger);
            logger.LogInformation("[Phase 1] Wiping harness state ({Scope})...", wipeScope);
            try
            {
                DevHarnessResetResult resetResult = await resetService.WipeAsync(
                    wipeScope,
                    resumeWatcher: false,
                    ct);
                report.WipeStatus = $"OK - {resetResult.Scope}";
            }
            catch (Exception ex)
            {
                report.WipeStatus = $"FAILED: {ex.Message}";
                report.IssuesFound.Add($"Wipe failed: {ex.Message}");
            }

            // ── Phase 2: Seed ────────────────────────────────────────────────
            logger.LogInformation("[Phase 2] Seeding test files...");
            try
            {
                int seeded = await SeedInternalAsync(options, configLoader, activeTypes, logger);
                report.SeedStatus = $"OK - {seeded} files";
                report.TotalFilesSeeded = seeded;
            }
            catch (Exception ex)
            {
                report.SeedStatus = $"FAILED: {ex.Message}";
                report.IssuesFound.Add($"Seed failed: {ex.Message}");
            }

            // ── Phase 3: Trigger scans and wait for ingestion ─────────────────
            // Brief settle to let file system flush.
            await Task.Delay(2000, ct);

            logger.LogInformation("[Phase 3] Triggering directory scans...");
            var scanConfig = configLoader.LoadLibraries();
            var scanTargets = new List<IngestionScanTarget>();
            foreach (var lib in scanConfig.Libraries)
            {
                bool libraryIsActive =
                    activeTypes.Contains(NormalizeHarnessMediaTypeKey(lib.Category))
                    || lib.MediaTypes.Any(mt => activeTypes.Contains(NormalizeHarnessMediaTypeKey(mt)));

                if (!libraryIsActive)
                {
                    continue;
                }

                foreach (var source in lib.ScannableSources)
                {
                    var sourcePath = source.Path;
                    if (!Directory.Exists(sourcePath))
                    {
                        continue;
                    }

                    int fileCount = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories).Length;
                    scanTargets.Add(new IngestionScanTarget(sourcePath, source.IncludeSubdirectories));
                    logger.LogInformation("  Scan target collected: {Path} ({Category}, {Files} files)", sourcePath, lib.Category, fileCount);
                }
            }

            if (scanTargets.Count > 0)
            {
                await ingestionEngine.ScanDirectories(scanTargets, ct);
                logger.LogInformation("[Phase 3] Grouped ScanDirectories triggered for {Count} target(s)", scanTargets.Count);
            }

            var ingestionWaitPlan = ResolveIngestionWaitPlan(stages, activeTypes.Count);
            logger.LogInformation(
                "[Phase 3] Waiting for ingestion to complete (budgets: registration={Registration}, identity={Identity}, stage3={Stage3}, idle={Idle}, hard={Total})...",
                ingestionWaitPlan.FileRegistration,
                ingestionWaitPlan.IdentityResolution,
                ingestionWaitPlan.Stage3Enrichment,
                ingestionWaitPlan.FinalIdle,
                ingestionWaitPlan.Total);
            var ingestionSw = Stopwatch.StartNew();
            bool ingestionComplete = await WaitForIngestionAsync(
                db,
                logger,
                report,
                ingestionWaitPlan,
                report.TotalFilesSeeded,
                stages,
                ct);
            ingestionSw.Stop();
            report.IngestionDuration = ingestionSw.Elapsed;

            if (!ingestionComplete)
            {
                report.IssuesFound.Add($"Ingestion did not complete within {ingestionWaitPlan.Total}");
                logger.LogWarning("[Phase 3] Ingestion timeout - proceeding with partial results");
            }

            // ── Phase 4: Validate results per media type ──────────────────────
            logger.LogInformation("[Phase 4] Validating ingestion results...");
            await ValidateResultsAsync(libraryItemRepo, report, logger, ct);

            // ── Phase 4b: Library display validation ──────────────────────────
            logger.LogInformation("[Phase 4b] Library display validation...");
            await ValidateLibraryDisplayAsync(db, libraryItemRepo, report, stages, logger, ct);

            // ── Phase 4c: File system and artwork validation ───────────────────
            if (stages < 123)
            {
                logger.LogInformation("[Phase 4c] File system and artwork validation...");
                await ValidateFileSystemAsync(db, options, configLoader, libraryItemRepo, report, loggerFactory, logger, ct);
            }
            else
            {
                logger.LogInformation("[Phase 4c] Deferring file system validation until after Stage 3 artwork completes...");
            }

            // ── Phase 4d: Stage Gating Validation ───────────────────────────
            logger.LogInformation("[Phase 4d] Stage gating validation...");
            await ValidateStageGatingAsync(libraryItemRepo, report, stages, logger, ct);

            // ── Phase 4e: Reconciliation — expected vs. actual outcomes ─────────
            logger.LogInformation("[Phase 4e] Running reconciliation pass...");
            await RunReconciliationAsync(db, report, activeTypes, logger, ct);

            logger.LogInformation("[Phase 4f] Validating description source priority and fallback storage...");
            await ValidateDescriptionSourcesAsync(db, libraryItemRepo, report, logger, ct);

            logger.LogInformation("[Phase 4g] Validating cross-media series totals...");
            await ValidateSeriesHarnessAsync(db, report, logger, ct);
            await ValidateMusicAlbumHarnessAsync(db, report, logger, ct);

            // ── Phase 5: Test manual search for review items ──────────────────
            logger.LogInformation("[Phase 5] Testing manual search on review items...");
            await TestManualSearchAsync(libraryItemRepo, providers, report, logger, ct);

            // ── Phase 6: Check universe enrichment ────────────────────────────
            logger.LogInformation("[Phase 6] Checking universe enrichment...");
            await CheckUniversesAsync(libraryItemRepo, report, logger, ct);

            // ── Phase 7: Stage 3 — Universe Enrichment (conditional) ────────
            if (stages >= 123)
            {
                logger.LogInformation("[Phase 7] Triggering Stage 3 Universe Enrichment...");
                await RunStage3EnrichmentAsync(context, libraryItemRepo, db, report, logger, ct);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);

                logger.LogInformation("[Phase 7b] File system and Stage 3 artwork validation...");
                report.FileSystemChecks.Clear();
                report.WatchFolderChecks.Clear();
                await ValidateFileSystemAsync(db, options, configLoader, libraryItemRepo, report, loggerFactory, logger, ct);
                ValidateStage3ArtworkAsync(report, logger);
                await ValidateCharacterArtworkAsync(db, personCreditReadService, report, logger, ct);
                await ValidatePersonCoverageAsync(
                    personRepo,
                    personCreditReadService,
                    report,
                    logger,
                    ct);
            }

            logger.LogInformation("[Phase 8] Validating outward links against the canonical detail composer...");
            await ValidateOutwardDetailRoutesAsync(
                collectionBrowseReadService,
                detailComposer,
                report,
                logger,
                ct);

            sw.Stop();
            report.TotalDuration = sw.Elapsed;

            // ── Generate HTML report ──────────────────────────────────────────
            string html = GenerateHtmlReport(report);

            // Save to disk — prefer repo root tools/reports/, fall back to CWD
            string reportsDir = Path.Combine(
                Path.GetDirectoryName(typeof(IntegrationTestEndpoints).Assembly.Location) ?? ".",
                "..", "..", "..", "..", "..", "tools", "reports");
            string? savedAbsolutePath = null;
            try
            {
                if (!Directory.Exists(reportsDir))
                {
                    reportsDir = Path.Combine(Directory.GetCurrentDirectory(), "tools", "reports");
                }

                if (!Directory.Exists(reportsDir))
                {
                    Directory.CreateDirectory(reportsDir);
                }

                string fileName = $"integration-test-{DateTime.Now:yyyy-MM-dd-HHmmss}.html";
                string filePath = Path.Combine(reportsDir, fileName);
                await File.WriteAllTextAsync(filePath, html, Encoding.UTF8, ct);
                savedAbsolutePath = Path.GetFullPath(filePath);
                logger.LogInformation("[TEST] HTML report saved to: {Path}", savedAbsolutePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Report] Could not save report to disk");
                report.IssuesFound.Add($"Report save failed: {ex.Message}");
                html = GenerateHtmlReport(report);
            }

            logger.LogInformation("╔══════════════════════════════════════════╗");
            logger.LogInformation("║   INTEGRATION TEST — Complete            ║");
            logger.LogInformation("║   Duration: {Duration}                   ║", report.TotalDuration);
            logger.LogInformation("║   Result: {Result}                       ║", report.OverallPass ? "PASS" : "ISSUES FOUND");
            logger.LogInformation("╚══════════════════════════════════════════╝");

            return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8);
        }
        finally
        {
            await resetService.ResumeWatcherAsync(ct: ct).ConfigureAwait(false);
        }
    }

    // ── Internal seed ────────────────────────────────────────────────────
    //
    // Delegates entirely to DevSeedEndpoints.SeedAllAsync, which is the single
    // source of truth for fixture seeding. This wrapper exists only so the
    // integration-test Phase 2 can call it without the HTTP endpoint plumbing.
    //
    // CRITICAL: do not reintroduce inline seed arrays here. They will silently
    // drift from the canonical DevSeedEndpoints data and cause "NotFound" false
    // negatives in the reconciliation pass.

    private static Task<int> SeedInternalAsync(
        IOptions<IngestionOptions> options,
        IConfigurationLoader configLoader,
        HashSet<string> activeTypes,
        ILogger logger)
        => DevSeedEndpoints.SeedAllAsync(options, configLoader, activeTypes, logger);

    private static ExpectationPreflightSummary BuildExpectationPreflight(
        IReadOnlyCollection<string> activeTypes,
        IReadOnlyDictionary<string, string> skippedTypes)
    {
        var activeSet = new HashSet<string>(activeTypes, StringComparer.OrdinalIgnoreCase);
        var summary = new ExpectationPreflightSummary();
        foreach (var expectation in DevSeedEndpoints.GetAllExpectations()
            .Where(e => activeSet.Contains(NormalizeHarnessMediaTypeKey(e.MediaType))))
        {
            var typeKey = NormalizeHarnessMediaTypeKey(expectation.MediaType);
            if (skippedTypes.ContainsKey(typeKey))
            {
                continue;
            }

            var status = expectation.ExpectedIdentityStatus;
            var item = new ExpectationPreflightItem
            {
                Title = expectation.Title,
                MediaType = expectation.MediaType,
                ExpectedStatus = status,
                ExpectedQid = expectation.ExpectedQid,
                ExpectedProvider = GetExpectedRetailProvider(expectation, expectation.MediaType),
                ExpectedReviewTrigger = expectation.ExpectedReviewTrigger,
                Reason = expectation.ExpectedReason,
            };
            summary.Items.Add(item);
            summary.TotalFiles++;

            if (expectation.ExpectIdentified)
            {
                summary.ExpectedResolved++;
                if (string.IsNullOrWhiteSpace(expectation.ExpectedQid))
                {
                    summary.ExpectedIdentityOnly++;
                }
                else
                {
                    summary.ExpectedExactQid++;
                }
            }
            else if (expectation.KnownNoWikidataEntity)
            {
                summary.ExpectedKnownNoQid++;
            }
            else
            {
                summary.ExpectedReview++;
            }
        }

        return summary;
    }

    private static void LogExpectationPreflight(ExpectationPreflightSummary summary, ILogger logger)
    {
        logger.LogInformation(
            "[Preflight] Expected outcomes: {Total} files; {Identified} should identify ({Exact} exact QID, {IdentityOnly} QID optional); {Review} expected review; {KnownNoQid} known no-QID",
            summary.TotalFiles,
            summary.ExpectedResolved,
            summary.ExpectedExactQid,
            summary.ExpectedIdentityOnly,
            summary.ExpectedReview,
            summary.ExpectedKnownNoQid);
    }

    private static string NormalizeHarnessMediaTypeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "book" or "books" or "ebook" or "epub" => "books",
            "audiobook" or "audiobooks" => "audiobooks",
            "movie" or "movies" => "movies",
            "tv" or "television" => "tv",
            "music" => "music",
            "comic" or "comics" => "comics",
            var other => other,
        };
    }

    private static string NormalizeHarnessMediaTypeDisplay(string? value) =>
        NormalizeHarnessMediaTypeKey(value) switch
        {
            "books" => "Books",
            "audiobooks" => "Audiobooks",
            "movies" => "Movies",
            "tv" => "TV",
            "music" => "Music",
            "comics" => "Comics",
            var other when !string.IsNullOrWhiteSpace(other) => other,
            _ => string.Empty,
        };

    private static bool IncludesMediaType(SeriesHarnessExpectation expectation, string typeKey) =>
        expectation.MediaTypeKeys.Contains(typeKey, StringComparer.OrdinalIgnoreCase);

    private static bool MatchesCrossMediaPattern(CrossMediaHarnessRow row, CrossMediaSeriesExpectation expected) =>
        expected.MatchPatterns.Any(pattern =>
            ContainsIgnoreCase(row.Title, pattern)
            || ContainsIgnoreCase(row.SeriesName, pattern));

    private static bool ContainsIgnoreCase(string? value, string pattern) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(pattern, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesHarnessContributorRole(string? actualRole, string expectedRole)
    {
        if (string.Equals(actualRole, expectedRole, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(expectedRole, "Artist", StringComparison.OrdinalIgnoreCase)
            && string.Equals(actualRole, "Performer", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SharedCrossMediaQids(CrossMediaHarnessRow row)
    {
        foreach (var value in new[]
        {
            row.ParentCollectionQid,
            row.NarrativeRootQid,
            row.FictionalUniverseQid,
            row.FranchiseQid,
            row.SeriesQid,
        })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value.Trim();
            }
        }
    }

    /// <summary>Known-fixture validation for movie-scoped actor-character display and verified portrait handling.</summary>
    private sealed class CharacterArtworkCheckResult
    {
        public string Title { get; set; } = "";
        public string WorkQid { get; set; } = "";
        public string ActorName { get; set; } = "";
        public string CharacterName { get; set; } = "";
        public bool WorkFound { get; set; }
        public bool HasMovieScopedActorCharacterLink { get; set; }
        public bool HasPortraitRow { get; set; }
        public string? PortraitSourceProvider { get; set; }
        public bool HasUnverifiedPortrait { get; set; }
        public bool HasDownloadedPortraitFile { get; set; }
        public bool HasDisplayCharacterName { get; set; }
        public bool HasDisplayCharacterImage { get; set; }
        public int? CastPosition { get; set; }
        public bool IsInPrimaryCastPreview { get; set; }
        public string? PortraitImageUrl { get; set; }
        public string? PortraitLocalImagePath { get; set; }
        public string? DisplayImageUrl { get; set; }
        public string? Detail { get; set; }
        public bool Pass =>
            WorkFound
            && HasMovieScopedActorCharacterLink
            && HasDisplayCharacterName
            && IsInPrimaryCastPreview
            && !HasUnverifiedPortrait;
    }

    private static IngestionWaitPlan ResolveIngestionWaitPlan(
        int stages,
        int activeTypeCount)
    {
        var fileRegistration = TimeSpan.FromMinutes(activeTypeCount >= 6 ? 20 : 10);
        var identityResolution = stages >= 12
            ? TimeSpan.FromMinutes(activeTypeCount >= 6 ? 80 : 45)
            : TimeSpan.FromMinutes(activeTypeCount >= 6 ? 25 : 15);
        var stage3Enrichment = stages >= 123
            ? TimeSpan.FromMinutes(activeTypeCount >= 6 ? 60 : 35)
            : TimeSpan.Zero;
        var finalIdle = TimeSpan.FromMinutes(10);

        return new(fileRegistration, identityResolution, stage3Enrichment, finalIdle);
    }

    // ── Wait for ingestion ────────────────────────────────────────────────

    private static async Task<bool> WaitForIngestionAsync(
        IDatabaseConnection db,
        ILogger logger,
        TestReport report,
        IngestionWaitPlan waitPlan,
        int expectedCount,
        int stages,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + waitPlan.Total;

        int lastAssetCount = -1;
        int lastResolvedCount = -1;
        int lastClaimCount = -1;
        int lastPendingCount = -1;
        int lastActiveJobCount = -1;
        int stableSnapshots = 0;
        bool sawExpectedAssetCount = false;
        string? waitStageKey = null;
        DateTimeOffset waitStageStarted = DateTimeOffset.UtcNow;
        bool waitStageTimedOut = false;

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(5000, ct);

            int assetCount;
            int totalWorks;
            int resolvedCount;
            int claimCount;
            int pendingCount;
            string jobStateSummary;

            using (var conn = db.CreateConnection())
            {
                assetCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM media_assets;");
                totalWorks = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works;");
                resolvedCount = conn.ExecuteScalar<int>("""
                    SELECT COUNT(DISTINCT w.id) FROM works w
                    LEFT JOIN editions e ON e.work_id = w.id
                    LEFT JOIN media_assets ma ON ma.edition_id = e.id
                    LEFT JOIN review_queue rq ON rq.entity_id = ma.id AND rq.status = 'Pending'
                    WHERE w.wikidata_qid IS NOT NULL OR rq.id IS NOT NULL OR w.curator_state IS NOT NULL
                    """);
                claimCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM metadata_claims;");
                pendingCount = conn.ExecuteScalar<int>("""
                    SELECT COUNT(DISTINCT w.id) FROM works w
                    LEFT JOIN editions e ON e.work_id = w.id
                    LEFT JOIN media_assets ma ON ma.edition_id = e.id
                    LEFT JOIN review_queue rq ON rq.entity_id = ma.id AND rq.status = 'Pending'
                    WHERE w.wikidata_qid IS NULL AND rq.id IS NULL AND w.curator_state IS NULL
                    """);
                var jobStates = conn.Query<(string State, int Count)>("""
                    SELECT state AS State, COUNT(*) AS Count
                    FROM identity_jobs
                    GROUP BY state
                    ORDER BY state;
                    """);
                jobStateSummary = string.Join(", ", jobStates.Select(s => $"{s.State}:{s.Count}"));
            }

            int activeIdentityJobs = await CountActiveIdentityJobsAsync(db, stages, ct);
            sawExpectedAssetCount |= assetCount >= expectedCount;
            var nextWaitStageKey = ResolveIngestionWaitStageKey(
                assetCount,
                expectedCount,
                activeIdentityJobs,
                jobStateSummary,
                stages);

            if (!string.Equals(waitStageKey, nextWaitStageKey, StringComparison.OrdinalIgnoreCase))
            {
                if (waitStageKey is not null)
                {
                    RecordIngestionWaitStage(
                        report,
                        waitStageKey,
                        waitPlan.BudgetFor(waitStageKey),
                        waitStageStarted,
                        completed: true,
                        timedOut: waitStageTimedOut,
                        detail: "Phase advanced.");
                }

                waitStageKey = nextWaitStageKey;
                waitStageStarted = DateTimeOffset.UtcNow;
                waitStageTimedOut = false;
            }

            var waitStageBudget = waitPlan.BudgetFor(waitStageKey ?? WaitStageFileRegistration);
            var waitStageElapsed = DateTimeOffset.UtcNow - waitStageStarted;
            if (!waitStageTimedOut
                && waitStageBudget > TimeSpan.Zero
                && waitStageElapsed > waitStageBudget)
            {
                waitStageTimedOut = true;
                RecordIngestionWaitStage(
                    report,
                    waitStageKey ?? WaitStageFileRegistration,
                    waitStageBudget,
                    waitStageStarted,
                    completed: false,
                    timedOut: true,
                    detail: $"Soft budget exceeded with assets={assetCount}/{expectedCount}, activeJobs={activeIdentityJobs}, pending={pendingCount}.");
                logger.LogWarning(
                    "  Ingestion wait phase {Stage} exceeded soft budget {Budget} with assets={Assets}/{Expected}, activeJobs={Jobs}, pending={Pending}",
                    waitStageKey,
                    waitStageBudget,
                    assetCount,
                    expectedCount,
                    activeIdentityJobs,
                    pendingCount);
            }

            bool snapshotStable =
                assetCount == lastAssetCount
                && resolvedCount == lastResolvedCount
                && claimCount == lastClaimCount
                && pendingCount == lastPendingCount
                && activeIdentityJobs == lastActiveJobCount;

            stableSnapshots = snapshotStable ? stableSnapshots + 1 : 0;

            logger.LogInformation(
                "  Ingestion: assets={Assets}/{Expected}, resolved={Resolved}/{Works}, pending={Pending}, claims={Claims}, activeJobs={Jobs}, stable={Stable}/4, jobStates=[{JobStates}]",
                assetCount,
                expectedCount,
                resolvedCount,
                totalWorks,
                pendingCount,
                claimCount,
                activeIdentityJobs,
                stableSnapshots,
                jobStateSummary);

            report.IngestionProgressSnapshots.Add(new IngestionProgressSnapshot
            {
                AssetCount = assetCount,
                ExpectedCount = expectedCount,
                ResolvedCount = resolvedCount,
                WorkCount = totalWorks,
                PendingCount = pendingCount,
                ClaimCount = claimCount,
                ActiveJobCount = activeIdentityJobs,
                StableSnapshots = stableSnapshots,
                ProgressPercent = expectedCount > 0
                    ? Math.Clamp(assetCount * 100d / expectedCount, 0d, 100d)
                    : 0d,
            });

            if (assetCount >= expectedCount && totalWorks > 0 && pendingCount == 0 && activeIdentityJobs == 0 && stableSnapshots >= 2)
            {
                RecordIngestionWaitStage(
                    report,
                    waitStageKey ?? WaitStageFinalIdle,
                    waitPlan.BudgetFor(waitStageKey ?? WaitStageFinalIdle),
                    waitStageStarted,
                    completed: true,
                    timedOut: waitStageTimedOut,
                    detail: "Ingestion reached terminal idle state.");
                return true;
            }

            if (assetCount >= expectedCount && activeIdentityJobs == 0 && stableSnapshots >= 4)
            {
                RecordIngestionWaitStage(
                    report,
                    waitStageKey ?? WaitStageFinalIdle,
                    waitPlan.BudgetFor(waitStageKey ?? WaitStageFinalIdle),
                    waitStageStarted,
                    completed: true,
                    timedOut: waitStageTimedOut,
                    detail: "All expected assets were registered and identity work is idle.");
                return true;
            }

            if (sawExpectedAssetCount && activeIdentityJobs == 0 && stableSnapshots >= 6)
            {
                RecordIngestionWaitStage(
                    report,
                    waitStageKey ?? WaitStageFinalIdle,
                    waitPlan.BudgetFor(waitStageKey ?? WaitStageFinalIdle),
                    waitStageStarted,
                    completed: true,
                    timedOut: waitStageTimedOut,
                    detail: "Expected asset count was seen and the pipeline settled.");
                return true;
            }

            if (sawExpectedAssetCount && activeIdentityJobs > 0 && stableSnapshots >= 12)
            {
                logger.LogWarning(
                    "  Ingestion wait still waiting after {StableSnapshots} stable snapshots with {ActiveJobs} active identity job(s) and {Pending} pending work(s)",
                    stableSnapshots,
                    activeIdentityJobs,
                    pendingCount);
            }

            lastAssetCount = assetCount;
            lastResolvedCount = resolvedCount;
            lastClaimCount = claimCount;
            lastPendingCount = pendingCount;
            lastActiveJobCount = activeIdentityJobs;
        }

        if (lastActiveJobCount > 0)
        {
            RecordIngestionWaitStage(
                report,
                waitStageKey ?? WaitStageFinalIdle,
                waitPlan.BudgetFor(waitStageKey ?? WaitStageFinalIdle),
                waitStageStarted,
                completed: false,
                timedOut: true,
                detail: $"{lastActiveJobCount} active identity job(s) remained at hard timeout.");
            logger.LogWarning(
                "  Ingestion wait timed out with {ActiveJobs} active identity job(s) remaining",
                lastActiveJobCount);
            return false;
        }

        RecordIngestionWaitStage(
            report,
            waitStageKey ?? WaitStageFinalIdle,
            waitPlan.BudgetFor(waitStageKey ?? WaitStageFinalIdle),
            waitStageStarted,
            completed: sawExpectedAssetCount || lastResolvedCount > 0,
            timedOut: !(sawExpectedAssetCount || lastResolvedCount > 0),
            detail: "Hard timeout reached after the pipeline stopped changing.");
        return sawExpectedAssetCount || lastResolvedCount > 0;
    }

    // ── Validate results ──────────────────────────────────────────────────

    private static string ResolveIngestionWaitStageKey(
        int assetCount,
        int expectedCount,
        int activeIdentityJobs,
        string jobStateSummary,
        int stages)
    {
        if (expectedCount > 0 && assetCount < expectedCount)
        {
            return WaitStageFileRegistration;
        }

        if (activeIdentityJobs == 0)
        {
            return WaitStageFinalIdle;
        }

        if (stages >= 123
            && jobStateSummary.Contains(nameof(IdentityJobState.UniverseEnriching), StringComparison.OrdinalIgnoreCase))
        {
            return WaitStageStage3Enrichment;
        }

        return WaitStageIdentityResolution;
    }

    private static void RecordIngestionWaitStage(
        TestReport report,
        string stageKey,
        TimeSpan budget,
        DateTimeOffset startedAt,
        bool completed,
        bool timedOut,
        string detail)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt.ToUniversalTime();
        var existing = report.IngestionWaitStages.FirstOrDefault(stage =>
            stage.StageKey.Equals(stageKey, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = new IngestionWaitStageResult
            {
                StageKey = stageKey,
                Label = IngestionWaitStageLabel(stageKey),
                BudgetSeconds = Math.Max(0d, budget.TotalSeconds),
            };
            report.IngestionWaitStages.Add(existing);
        }

        existing.ElapsedSeconds = Math.Max(existing.ElapsedSeconds, elapsed.TotalSeconds);
        existing.TimedOut |= timedOut;
        existing.Completed |= completed;
        existing.CompletedAfterTimeout |= completed && existing.TimedOut;
        existing.Detail = detail;
    }

    private static string IngestionWaitStageLabel(string stageKey) => stageKey switch
    {
        WaitStageFileRegistration => "File registration",
        WaitStageIdentityResolution => "Stage 1/2 identity",
        WaitStageStage3Enrichment => "Stage 3 enrichment",
        WaitStageFinalIdle => "Final idle",
        _ => stageKey,
    };

    private static int WaitStageSort(string stageKey) => stageKey switch
    {
        WaitStageFileRegistration => 0,
        WaitStageIdentityResolution => 1,
        WaitStageStage3Enrichment => 2,
        WaitStageFinalIdle => 3,
        _ => 99,
    };

    private static async Task ValidateResultsAsync(
        ILibraryItemRepository libraryItemRepo,
        TestReport report,
        ILogger logger,
        CancellationToken ct)
    {
        // Get all items
        var allItems = await libraryItemRepo.GetPageAsync(new LibraryItemQuery(Offset: 0, Limit: 5000, IncludeAll: true), ct);
        var ownedItems = allItems.Items.Where(IsOwnedValidationItem).ToList();
        int catalogOnlyCount = allItems.Items.Count - ownedItems.Count;
        report.TotalItems = ownedItems.Count;

        logger.LogInformation(
            "  Total items in libraryItem: {OwnedCount} owned, {CatalogOnlyCount} catalog-only ({TotalCount} total rows)",
            ownedItems.Count,
            catalogOnlyCount,
            allItems.Items.Count);

        // Group by media type
        var grouped = allItems.Items.GroupBy(i => i.MediaType).OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            var ownedGroupItems = group.Where(IsOwnedValidationItem).ToList();
            var mtResult = new MediaTypeResult
            {
                MediaType = group.Key,
                Count = ownedGroupItems.Count,
                OwnedCount = ownedGroupItems.Count,
                CatalogOnlyCount = group.Count() - ownedGroupItems.Count,
            };

            foreach (var item in ownedGroupItems)
            {
                var ir = new ItemResult
                {
                    Title = item.Title,
                    FileName = item.FileName ?? "",
                    FilePath = item.FilePath,
                    MediaType = item.MediaType,
                    Status = item.Status ?? "",
                    Confidence = item.Confidence,
                    RetailMatch = item.RetailMatch,
                    WikidataQid = item.WikidataQid,
                    ReviewTrigger = item.ReviewTrigger,
                    Author = item.Author,
                    Year = item.Year,
                };
                mtResult.Items.Add(ir);

                if (IsReviewStatus(item.Status))
                {
                    mtResult.NeedsReview++;
                }
                else if (IsFailureStatus(item.Status))
                {
                    mtResult.Failed++;
                }
                else if (IsIdentifiedStatus(item.Status))
                {
                    mtResult.Identified++;
                }
                else
                {
                    mtResult.NeedsReview++; // Default to review if unknown status
                }
            }

            report.MediaTypeResults.Add(mtResult);
            report.TotalIdentified += mtResult.Identified;
            report.TotalNeedsReview += mtResult.NeedsReview;
            report.TotalFailed += mtResult.Failed;

            logger.LogInformation(
                "  {Type}: {OwnedCount} owned, {CatalogOnlyCount} catalog-only ({Identified} identified, {Review} review, {Failed} failed)",
                group.Key,
                mtResult.OwnedCount,
                mtResult.CatalogOnlyCount,
                mtResult.Identified,
                mtResult.NeedsReview,
                mtResult.Failed);
        }

        // Check for expected media types that have zero items
        foreach (var expected in report.ActiveTypes.Select(t => t switch
        {
            "books" => "Books",
            "audiobooks" => "Audiobooks",
            "movies" => "Movies",
            "tv" => "TV",
            "music" => "Music",
            "comics" => "Comics",
            _ => t
        }))
        {
            if (!report.MediaTypeResults.Any(r => r.MediaType.Equals(expected, StringComparison.OrdinalIgnoreCase)))
            {
                report.IssuesFound.Add($"No items found for media type: {expected}");
                report.MediaTypeResults.Add(new MediaTypeResult { MediaType = expected, Count = 0 });
            }
        }
    }

    // ── Test manual search ────────────────────────────────────────────────

    private static async Task TestManualSearchAsync(
        ILibraryItemRepository libraryItemRepo,
        IEnumerable<IExternalMetadataProvider> providers,
        TestReport report,
        ILogger logger,
        CancellationToken ct)
    {
        // Test one search per media type using the correct provider
        var allSearchTests = new (string title, string? creator, string providerName, string mediaType, MediaType enumType, string typeKey)[]
        {
            ("Dune", "Frank Herbert", "apple_api", "Books", MediaType.Books, "books"),
            ("Blade Runner 2049", null, "tmdb", "Movies", MediaType.Movies, "movies"),
            ("Breaking Bad", null, "tmdb", "TV", MediaType.TV, "tv"),
            ("Bohemian Rhapsody", "Queen", "musicbrainz", "Music identity", MediaType.Music, "music"),
            ("Lose Yourself", "Eminem", "musicbrainz", "Music identity", MediaType.Music, "music"),
            ("Yesterday", "The Beatles", "musicbrainz", "Music identity", MediaType.Music, "music"),
            ("Clair de Lune", "Claude Debussy", "musicbrainz", "Music identity", MediaType.Music, "music"),
            ("Bohemian Rhapsody", "Queen", "apple_api", "Music enrichment", MediaType.Music, "music"),
            ("Batman: Year One", null, "comicvine", "Comics", MediaType.Comics, "comics"),
        };
        var searchTests = allSearchTests
            .Where(t => report.ActiveTypes.Contains(t.typeKey))
            .Select(t => (t.title, t.creator, t.providerName, t.mediaType, t.enumType))
            .ToArray();

        var providerList = providers.ToList();

        foreach (var (title, creator, providerName, mediaType, enumType) in searchTests)
        {
            var query = string.Join(' ', new[] { title, creator }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var result = new ManualSearchResult
            {
                Query = query,
                ProviderName = providerName,
                MediaType = mediaType,
            };

            try
            {
                // Find the provider
                var provider = providerList.FirstOrDefault(p =>
                    p.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase));

                if (provider is null)
                {
                    result.Error = $"Provider '{providerName}' not found in registered providers";
                    report.IssuesFound.Add($"Manual search: provider '{providerName}' not found for {mediaType}");
                }
                else
                {
                    var lookupRequest = new ProviderLookupRequest
                    {
                        Title = title,
                        Author = enumType is MediaType.Books or MediaType.Audiobooks ? creator : null,
                        Artist = enumType == MediaType.Music ? creator : null,
                        Composer = enumType == MediaType.Music ? creator : null,
                        MediaType = enumType,
                    };

                    var searchResults = await provider.SearchAsync(lookupRequest, 5, ct);
                    result.ResultCount = searchResults.Count;

                    if (searchResults.Count > 0)
                    {
                        result.TopResultTitle = searchResults[0].Title;
                        result.TopResultConfidence = searchResults[0].Confidence;
                        logger.LogInformation("  Search '{Query}' via {Provider}: {Count} results, top='{Top}'",
                            query, providerName, searchResults.Count, searchResults[0].Title);
                    }
                    else
                    {
                        result.Error = "No results returned";
                        report.IssuesFound.Add($"Manual search returned 0 results: '{query}' via {providerName}");
                        logger.LogWarning("  Search '{Query}' via {Provider}: NO RESULTS", query, providerName);
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                report.IssuesFound.Add($"Manual search error: '{query}' via {providerName}: {ex.Message}");
                logger.LogError(ex, "  Search '{Query}' via {Provider}: ERROR", query, providerName);
            }

            report.ManualSearchResults.Add(result);
        }
    }

    // ── Library display validation ─────────────────────────────────────────

    private static async Task ValidateLibraryDisplayAsync(
        IDatabaseConnection db,
        ILibraryItemRepository libraryItemRepo,
        TestReport report,
        int stages,
        ILogger logger,
        CancellationToken ct)
    {
        var allItems = await libraryItemRepo.GetPageAsync(new LibraryItemQuery(Offset: 0, Limit: 500, IncludeAll: true), ct);
        var validationItems = allItems.Items.Where(IsOwnedValidationItem).ToList();
        var expectations = BuildExpectationIndex();
        var providerNamesById = await LoadProviderNamesByIdAsync(db);

        foreach (var item in validationItems)
        {
            // Fetch detail to get creator fields (author/director/artist)
            var detail = await libraryItemRepo.GetDetailAsync(item.EntityId, ct);
            expectations.TryGetValue(NormalizeExpectationKey(item.Title, item.MediaType), out var expected);

            // Determine creator based on media type
            string? creator = item.MediaType.ToUpperInvariant() switch
            {
                "BOOKS" or "AUDIOBOOKS" => StringHelpers.FirstNonBlank(detail?.Author, FirstCanonicalValue(detail, "author"), FirstClaimValue(detail, "author")),
                "MOVIES" => StringHelpers.FirstNonBlank(detail?.Director, FirstCanonicalValue(detail, "director"), FirstClaimValue(detail, "director")),
                "TV" => StringHelpers.FirstNonBlank(detail?.Director, FirstCanonicalValue(detail, "director"), FirstClaimValue(detail, "director")),
                "MUSIC" => StringHelpers.FirstNonBlank(
                    detail?.Artist,
                    detail?.Author,
                    FirstCanonicalValue(detail, "artist", "album_artist", "author"),
                    FirstClaimValue(detail, "artist", "album_artist", "author"),
                    item.Author),
                _ => StringHelpers.FirstNonBlank(detail?.Author, item.Author),
            };

            var validStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Verified", "Provisional", "NeedsReview", "Identified", "Confirmed",
                "Registered", "InReview", "Quarantined", "Rejected",
                "Queued", "RetailSearching", "RetailMatched", "RetailMatchedNeedsReview",
                "RetailNoMatch", "BridgeSearching", "QidResolved", "QidNeedsReview",
                "QidNoMatch", "Hydrating", "UniverseEnriching", "Ready",
                "ReadyWithoutUniverse", "Failed",
            };

            bool creatorRequired =
                !item.MediaType.Equals("TV", StringComparison.OrdinalIgnoreCase)
                && (expected?.ExpectIdentified ?? true);
            string? expectedProvider = GetExpectedRetailProvider(expected, item.MediaType);
            string? actualProvider = detail is null
                ? null
                : ResolveRetailProvider(detail, providerNamesById);
            string? identityProvider = ResolveIdentityProvider(detail, item.MediaType, providerNamesById);
            string? enrichmentProviders = ResolveEnrichmentProviders(detail, item.MediaType, providerNamesById);
            bool providerMatchesExpectation = ProviderMatchesExpectation(
                item.MediaType,
                expectedProvider,
                actualProvider,
                identityProvider);

            var check = new LibraryCheckResult
            {
                Title = item.Title,
                MediaType = item.MediaType,
                EntityId = item.EntityId,
                HasCoverArt = !string.IsNullOrWhiteSpace(item.CoverUrl),
                HasTitle = !string.IsNullOrWhiteSpace(item.Title),
                HasCreator = !string.IsNullOrWhiteSpace(creator),
                HasStatus = !string.IsNullOrWhiteSpace(item.Status) && validStatuses.Contains(item.Status),
                HasRetailMatch = !string.IsNullOrWhiteSpace(item.RetailMatch) && item.RetailMatch != "none",
                HasWikidataQid = !string.IsNullOrWhiteSpace(item.WikidataQid),
                Status = item.Status,
                CoverUrl = item.CoverUrl,
                Creator = creator,
                RetailMatch = item.RetailMatch,
                WikidataQid = item.WikidataQid,
                RequiresCreator = creatorRequired,
                ExpectedRetailProvider = expectedProvider,
                ActualRetailProvider = actualProvider,
                IdentityProvider = identityProvider,
                EnrichmentProviders = enrichmentProviders,
                BridgeProvider = ResolveBridgeProvider(detail, providerNamesById),
                BridgeIdType = ResolveBridgeIdType(detail, item.MediaType),
                ArtworkCompleteness = !string.IsNullOrWhiteSpace(item.CoverUrl) ? "display-cover-present" : "display-cover-missing",
                HasExpectedRetailProvider = string.IsNullOrWhiteSpace(expectedProvider)
                    || !string.Equals(item.RetailMatch, "matched", StringComparison.OrdinalIgnoreCase)
                    || providerMatchesExpectation,
            };
            report.LibraryChecks.Add(check);

            if (!check.HasTitle)
            {
                report.IssuesFound.Add($"Library: '{item.FileName}' has no title");
            }

            if (creatorRequired && !check.HasCreator)
            {
                report.IssuesFound.Add($"Library: '{item.Title}' has no creator (author/director/artist)");
            }

            if (!check.HasStatus)
            {
                report.IssuesFound.Add($"Library: '{item.Title}' has invalid or empty status: '{item.Status}'");
            }

            if (!check.HasRetailMatch)
            {
                logger.LogWarning("  Library: '{Title}' missing retail match", item.Title);
            }

            if (check.RequiresRetailProvider && !check.HasExpectedRetailProvider)
            {
                report.IssuesFound.Add(BuildProviderExpectationIssue(item.Title, item.MediaType, expectedProvider, actualProvider, identityProvider));
            }

            if (HasComicIssueTitleDrift(item, detail))
            {
                report.IssuesFound.Add($"Library: comic issue '{item.Title}' does not preserve its series and issue identity");
            }

            if (!check.HasCoverArt)
            {
                logger.LogWarning("  Library: '{Title}' missing cover art", item.Title);
            }

            if (stages >= 12 && !check.HasWikidataQid)
            {
                logger.LogDebug("  Library: '{Title}' has no Wikidata QID", item.Title);
                if (!string.IsNullOrWhiteSpace(expected?.ExpectedQid))
                {
                    report.IssuesFound.Add($"Library: '{item.Title}' expected a Wikidata QID but has none");
                }
            }

            if (stages >= 12
                && expected?.ExpectIdentified == true
                && !string.IsNullOrWhiteSpace(expected.ExpectedQid)
                && !string.IsNullOrWhiteSpace(check.WikidataQid)
                && !string.Equals(expected.ExpectedQid, check.WikidataQid, StringComparison.OrdinalIgnoreCase))
            {
                report.IssuesFound.Add($"Library: '{item.Title}' resolved QID '{check.WikidataQid}' but expected '{expected.ExpectedQid}'");
            }
        }

        int passCount = report.LibraryChecks.Count(v => v.Pass);
        logger.LogInformation("  Library checks: {Pass}/{Total} items pass core validation",
            passCount, report.LibraryChecks.Count);
        if (passCount != report.LibraryChecks.Count)
        {
            report.IssuesFound.Add(
                $"Library display validation failed for {report.LibraryChecks.Count - passCount} item(s)");
        }

        // ── Child entity validation (TV episodes, Music tracks) ──────────
        foreach (var item in validationItems)
        {
            if (string.IsNullOrWhiteSpace(item.WikidataQid))
            {
                continue;
            }

            var detail = await libraryItemRepo.GetDetailAsync(item.EntityId, ct);
            if (detail is null)
            {
                continue;
            }

            var canonMap = detail.CanonicalValues.ToDictionary(cv => cv.Key, cv => cv.Value, StringComparer.OrdinalIgnoreCase);

            if (item.MediaType.Equals("TV", StringComparison.OrdinalIgnoreCase))
            {
                var hasSeasonsStr = canonMap.GetValueOrDefault("season_count");
                var hasEpisodesStr = canonMap.GetValueOrDefault("episode_count");
                var hasChildren = canonMap.GetValueOrDefault("child_entities_json");
                int.TryParse(hasSeasonsStr, out var seasonCount);
                int.TryParse(hasEpisodesStr, out var episodeCount);

                if (seasonCount == 0)
                {
                    report.IssuesFound.Add($"Child entities: TV '{item.Title}' has no season_count");
                }

                if (episodeCount == 0)
                {
                    logger.LogWarning("  Child entities: TV '{Title}' has no episode_count", item.Title);
                }

                if (string.IsNullOrWhiteSpace(hasChildren))
                {
                    report.IssuesFound.Add($"Child entities: TV '{item.Title}' has no child_entities_json");
                }
                else
                {
                    logger.LogInformation("  Child entities: TV '{Title}' — {Seasons} seasons, {Episodes} episodes",
                        item.Title, seasonCount, episodeCount);
                }
            }
            else if (item.MediaType.Equals("Music", StringComparison.OrdinalIgnoreCase))
            {
                var hasTracksStr = canonMap.GetValueOrDefault("track_count");
                var hasChildren = canonMap.GetValueOrDefault("child_entities_json");
                int.TryParse(hasTracksStr, out var trackCount);
                var qidMethod = canonMap.GetValueOrDefault(MetadataFieldConstants.QidResolutionMethod);
                int childCount = CountChildEntities(hasChildren);
                bool requiresTrackManifest =
                    string.Equals(qidMethod, "album", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(hasChildren);

                if (!requiresTrackManifest)
                {
                    continue;
                }

                if (trackCount <= 0)
                {
                    report.IssuesFound.Add($"Child entities: Music '{item.Title}' has no track_count");
                }

                if (string.IsNullOrWhiteSpace(hasChildren))
                {
                    report.IssuesFound.Add($"Child entities: Music '{item.Title}' has no child_entities_json");
                }
                else if (childCount <= 0)
                {
                    report.IssuesFound.Add($"Child entities: Music '{item.Title}' has invalid child_entities_json");
                }
                else if (trackCount > 0 && childCount < trackCount)
                {
                    report.IssuesFound.Add($"Child entities: Music '{item.Title}' exposes {childCount} child rows but track_count is {trackCount}");
                }
                else
                {
                    logger.LogInformation("  Child entities: Music '{Title}' — {Tracks} tracks",
                        item.Title, Math.Max(trackCount, childCount));
                }
            }
        }
    }

    // ── Stage gating validation ──────────────────────────────────────────

    private static async Task ValidateFileSystemAsync(
        IDatabaseConnection db,
        IOptions<IngestionOptions> options,
        IConfigurationLoader configLoader,
        ILibraryItemRepository libraryItemRepo,
        TestReport report,
        ILoggerFactory loggerFactory,
        ILogger logger,
        CancellationToken ct)
    {
        var allItems = await libraryItemRepo.GetPageAsync(new LibraryItemQuery(Offset: 0, Limit: 500, IncludeAll: true), ct);
        var validationItems = allItems.Items.Where(IsOwnedValidationItem).ToList();
        var organizer = new FileOrganizer(loggerFactory.CreateLogger<FileOrganizer>());
        var assetPathService = new AssetPathService(options.Value.LibraryRoot);
        var assetIds = await LoadWorkAssetIdsAsync(db);
        var assetCanonicals = await LoadCanonicalValueMapsAsync(db, assetIds.Values);
        var preferredCoverArtwork = await LoadPreferredArtworkRecordsAsync(db, "CoverArt");
        var assetIdsByPath = await LoadAssetIdsByPathAsync(db);
        var optionalArtworkStates = await LoadOptionalArtworkStatesAsync(db);
        var workHierarchy = await LoadWorkHierarchyAsync(db);
        var watchRoots = ResolveLeafSourcePaths(configLoader);
        var requireSidecarArtwork = ShouldRequireSidecarArtwork(configLoader.LoadCore().StoragePolicy);
        var expectations = BuildExpectationIndex();

        foreach (var item in validationItems)
        {
            var detail = await libraryItemRepo.GetDetailAsync(item.EntityId, ct);
            expectations.TryGetValue(NormalizeExpectationKey(item.Title, item.MediaType), out var expected);

            bool expectLibraryPlacement = !IsReviewStatus(item.Status) && !IsFailureStatus(item.Status);
            bool expectedCoverArt = expected?.ExpectedCoverArt
                ?? (!string.IsNullOrWhiteSpace(item.CoverUrl) && expectLibraryPlacement);
            string? filePath = detail?.FilePath ?? item.FilePath;

            var check = new FileSystemCheckResult
            {
                Title = item.Title,
                MediaType = item.MediaType,
                Status = item.Status ?? "",
                WikidataQid = item.WikidataQid,
                ExpectedLocation = expectLibraryPlacement ? "Library" : "Staging",
                ExpectedCoverArt = expectedCoverArt,
            };

            Guid? resolvedAssetId = null;

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                check.ActualFilePath = filePath;
                check.ActualDisplayPath = ToDisplayPath(filePath, options.Value);
                check.FileExists = File.Exists(filePath);
                check.InLibraryRoot = IsUnderRoot(filePath, options.Value.LibraryRoot);
                check.InStagingRoot = IsUnderRoot(filePath, options.Value.StagingPath)
                    || watchRoots.Any(root => IsUnderRoot(filePath, root));
                check.LocationMatchesExpectation = expectLibraryPlacement
                    ? check.InLibraryRoot
                    : check.InStagingRoot;

                if (assetIdsByPath.TryGetValue(NormalizeComparablePath(filePath), out var assetIdByPath))
                {
                    resolvedAssetId = assetIdByPath;
                }
                else if (assetIds.TryGetValue(item.EntityId, out var assetIdByWork))
                {
                    resolvedAssetId = assetIdByWork;
                }

                if (expectLibraryPlacement
                    && resolvedAssetId is Guid pathAssetId
                    && assetCanonicals.TryGetValue(pathAssetId, out var pathMetadata)
                    && !string.IsNullOrWhiteSpace(options.Value.LibraryRoot))
                {
                    string? expectedPath = BuildExpectedLibraryPath(item, filePath, pathMetadata, options.Value, organizer);
                    check.RequiresTemplateMatch = !string.IsNullOrWhiteSpace(expectedPath);
                    check.PathMatchesTemplate = expectedPath is not null && PathsEqual(expectedPath, filePath);
                    if (!string.IsNullOrWhiteSpace(expectedPath))
                    {
                        check.ExpectedRelativePath = Path.GetRelativePath(options.Value.LibraryRoot, expectedPath);
                    }
                }
                else
                {
                    check.PathMatchesTemplate = !expectLibraryPlacement || check.InStagingRoot;
                }

                if (check.FileExists)
                {
                    check.HasLegacyHeroSidecar = File.Exists(Path.Combine(Path.GetDirectoryName(filePath) ?? string.Empty, "hero.jpg"));
                }

                if (check.FileExists && expectLibraryPlacement && expectedCoverArt)
                {
                    check.RequiresStoredArtwork = true;

                    if (requireSidecarArtwork)
                    {
                        check.RequiresSidecarArtwork = true;
                        check.HasPoster = HasAnySidecarPoster(filePath);
                        check.HasPosterThumb = HasAnySidecarPosterThumb(filePath);
                    }

                }
            }

            resolvedAssetId ??= assetIds.TryGetValue(item.EntityId, out var fallbackAssetId) ? fallbackAssetId : null;
            if (resolvedAssetId is Guid assetId && !string.IsNullOrWhiteSpace(options.Value.LibraryRoot))
            {
                var ownerEntityId = ResolveArtworkOwnerEntityId(item.EntityId, item.MediaType, workHierarchy);
                check.HasStoredCover = HasCentralPreferredArtwork(
                    ownerEntityId,
                    preferredCoverArtwork,
                    assetPathService,
                    "Work",
                    "CoverArt");
                check.HasStoredCoverSmall = HasCentralArtworkRendition(ownerEntityId, preferredCoverArtwork, assetPathService, size: "s");
                check.HasStoredCoverMedium = HasCentralArtworkRendition(ownerEntityId, preferredCoverArtwork, assetPathService, size: "m");
                check.HasStoredCoverLarge = HasCentralArtworkRendition(ownerEntityId, preferredCoverArtwork, assetPathService, size: "l");
                check.HasStoredPalette = HasArtworkPalette(preferredCoverArtwork, ownerEntityId);
                check.HasStoredLegacyHero = File.Exists(assetPathService.GetCentralDerivedPath("Work", ownerEntityId, "hero", "hero.jpg"));

                if (assetCanonicals.TryGetValue(assetId, out var artworkMetadata))
                {
                    check.HasArtworkBridgeId = HasTmdbArtworkBridgeId(artworkMetadata);
                }
            }

            var optionalArtwork = ResolveOptionalArtworkState(item.EntityId, workHierarchy, optionalArtworkStates);
            check.HasStoredBackground = optionalArtwork.HasBackground;
            check.HasStoredLogo = optionalArtwork.HasLogo;
            check.HasStoredBanner = optionalArtwork.HasBanner;
            check.HasStoredSeasonPoster = optionalArtwork.HasSeasonPoster;
            check.HasStoredSeasonThumb = optionalArtwork.HasSeasonThumb;
            check.HasStoredEpisodeStill = optionalArtwork.HasEpisodeStill;

            check.Detail = DescribeFileSystemCheck(check);
            report.FileSystemChecks.Add(check);
        }

        ValidateWatchFolders(configLoader, report, logger);

        int failedItems = report.FileSystemChecks.Count(c => !c.Pass);
        if (failedItems > 0)
        {
            report.IssuesFound.Add($"File system validation failed for {failedItems} item(s)");
            logger.LogWarning("  File system validation: {Failed}/{Total} items failed", failedItems, report.FileSystemChecks.Count);
        }

        int watchFailures = report.WatchFolderChecks.Count(c => !c.Pass);
        if (watchFailures > 0)
        {
            int remainingFiles = report.WatchFolderChecks.Sum(c => c.RemainingMediaFiles);
            report.IssuesFound.Add($"{remainingFiles} media file(s) remained in watch folders after ingestion");
            logger.LogWarning("  Watch folders not drained: {Directories} directories still contain {Files} media files",
                watchFailures, remainingFiles);
        }
    }

    private static async Task ValidateStageGatingAsync(
        ILibraryItemRepository libraryItemRepo,
        TestReport report,
        int stages,
        ILogger logger,
        CancellationToken ct)
    {
        var allItems = await libraryItemRepo.GetPageAsync(new LibraryItemQuery(Offset: 0, Limit: 500, IncludeAll: true), ct);
        var validationItems = allItems.Items.Where(IsOwnedValidationItem).ToList();
        var expectations = BuildExpectationIndex();

        if (stages == 1)
        {
            // Stage 1 only: NO items should have a Wikidata QID
            foreach (var item in validationItems)
            {
                bool hasQid = !string.IsNullOrWhiteSpace(item.WikidataQid);
                var result = new StageGatingResult
                {
                    Title = item.Title,
                    Check = "Stage 1 only: no Wikidata QID expected",
                    Pass = !hasQid,
                    Detail = hasQid ? $"Unexpected QID: {item.WikidataQid}" : "Correctly absent",
                };
                report.StageGatingResults.Add(result);
                if (hasQid)
                {
                    report.IssuesFound.Add($"Stage gating: '{item.Title}' has QID '{item.WikidataQid}' but Stage 2 should not have run");
                }
            }
            logger.LogInformation("  Stage 1 gating: {Pass}/{Total} items correctly have no QID",
                report.StageGatingResults.Count(r => r.Pass), report.StageGatingResults.Count);
        }
        else if (stages >= 12)
        {
            // Stage 1+2: known fixtures should resolve to Wikidata before the
            // run is accepted. Retail-only/no-QID is a report failure unless
            // the expectation explicitly marks the fixture as non-identifiable.
            foreach (var item in validationItems)
            {
                expectations.TryGetValue(NormalizeExpectationKey(item.Title, item.MediaType), out var expected);
                bool hasQid = !string.IsNullOrWhiteSpace(item.WikidataQid)
                    && !item.WikidataQid.StartsWith("NF", StringComparison.OrdinalIgnoreCase);
                bool expectsIdentification = expected?.ExpectIdentified ?? IsIdentifiedStatus(item.Status);
                bool expectsReview = expected is { ExpectIdentified: false };
                bool hasIdentifiedState = IsIdentifiedStatus(item.Status) && !IsReviewStatus(item.Status);
                bool exactQidMatches = string.IsNullOrWhiteSpace(expected?.ExpectedQid)
                    || string.Equals(expected.ExpectedQid, item.WikidataQid, StringComparison.OrdinalIgnoreCase);
                if (expectsReview)
                {
                    var result = new StageGatingResult
                    {
                        Title = item.Title,
                        Check = "Stage 2: review expected by manifest",
                        Pass = IsReviewStatus(item.Status) || !string.IsNullOrWhiteSpace(item.ReviewTrigger),
                        Detail = item.ReviewTrigger ?? item.Status ?? "No review state",
                    };
                    report.StageGatingResults.Add(result);
                    if (!result.Pass)
                    {
                        report.IssuesFound.Add($"Stage gating: '{item.Title}' expected review but ended as status '{item.Status ?? "unknown"}'");
                    }
                }
                else if (expectsIdentification)
                {
                    bool requiresExactQid = !string.IsNullOrWhiteSpace(expected?.ExpectedQid);
                    var result = new StageGatingResult
                    {
                        Title = item.Title,
                        Check = requiresExactQid
                            ? "Stage 2: exact QID expected by manifest"
                            : "Stage 2: trusted identity expected; QID optional",
                        Pass = requiresExactQid
                            ? hasQid && exactQidMatches
                            : hasQid || hasIdentifiedState,
                        Detail = hasQid
                            ? exactQidMatches
                                ? $"QID: {item.WikidataQid}"
                                : $"Wrong QID: {item.WikidataQid}; expected {expected?.ExpectedQid}"
                            : hasIdentifiedState
                                ? $"Identified without QID: {item.Status}"
                                : "No trusted identity",
                    };
                    report.StageGatingResults.Add(result);
                    if (!result.Pass)
                    {
                        report.IssuesFound.Add($"Stage gating: '{item.Title}' did not reach its declared identity outcome; status '{item.Status ?? "unknown"}', retail '{item.RetailMatch ?? "none"}'");
                    }
                }
                else
                {
                    report.StageGatingResults.Add(new StageGatingResult
                    {
                        Title = item.Title,
                        Check = "Stage 2: no identity outcome declared",
                        Pass = true,
                        Detail = item.Status ?? "Unknown",
                    });
                }
            }

            int pass = report.StageGatingResults.Count(r => r.Pass);
            logger.LogInformation("  Stage 1+2 gating: {Pass}/{Total} items pass gating checks",
                pass, report.StageGatingResults.Count);
        }
    }

    // ── Stage 3: Universe Enrichment (conditional) ───────────────────────

    private static async Task RunStage3EnrichmentAsync(
        HttpContext context,
        ILibraryItemRepository libraryItemRepo,
        IDatabaseConnection db,
        TestReport report,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            // Resolve the UniverseEnrichmentService from DI and trigger a manual sweep
            var universeService = context.RequestServices.GetService<UniverseEnrichmentService>();
            if (universeService is null)
            {
                report.IssuesFound.Add("Stage 3: UniverseEnrichmentService not registered in DI");
                logger.LogWarning("[Phase 7] UniverseEnrichmentService not found in DI — skipping Stage 3");
                return;
            }

            universeService.TriggerManualSweep();
            logger.LogInformation("[Phase 7] Stage 3 manual sweep triggered — waiting for completion...");

            // Poll for universe/parent collection creation (timeout: 3 minutes)
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
            int? lastCollectionCount = null;
            int stableCount = 0;
            while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(5000, ct);
                int collectionCount;
                using (var conn = db.CreateConnection())
                {
                    collectionCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM collections WHERE parent_collection_id IS NOT NULL;");
                }

                if (lastCollectionCount.HasValue && collectionCount == lastCollectionCount.Value)
                {
                    stableCount++;
                }
                else
                {
                    stableCount = 0;
                }

                lastCollectionCount = collectionCount;

                logger.LogInformation("  Stage 3: {Collections} collections with parent, stable={Stable}", collectionCount, stableCount);
                if (stableCount >= 3)
                {
                    break;
                }
            }

            await WaitForArtworkActivityToSettleAsync(db, logger, TimeSpan.FromMinutes(2), ct);

            // Validate universe creation for known test data
            using (var conn = db.CreateConnection())
            {
                // Check for parent collections (universes)
                var parentCollections = (await conn.QueryAsync<ParentCollectionRow>(
                    """
                    SELECT h.id            AS Id,
                           h.display_name  AS DisplayName,
                           h.wikidata_qid  AS WikidataQid
                    FROM collections h
                    WHERE EXISTS (SELECT 1 FROM collections child WHERE child.parent_collection_id = h.id)
                    """)).ToList();

                foreach (var ph in parentCollections)
                {
                    if (!TryReadCurrentGuid(ph.Id, out var collectionId))
                    {
                        continue;
                    }

                    var collectionIdParam = GuidSql.ToBlob(collectionId);
                    int childCount = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM collections WHERE parent_collection_id = @id;",
                        new { id = collectionIdParam });

                    var universeResult = new UniverseResult
                    {
                        Name = ph.DisplayName,
                        WikidataQid = ph.WikidataQid,
                        Found = true,
                        SeriesCount = childCount,
                    };
                    // Count works under child collections
                    int workCount = await conn.ExecuteScalarAsync<int>(
                        """
                        SELECT COUNT(*) FROM works w
                        INNER JOIN collections child ON w.collection_id = child.id
                        WHERE child.parent_collection_id = @id
                        """,
                        new { id = collectionIdParam });
                    universeResult.WorkCount = workCount;

                    report.UniverseResults.Add(universeResult);
                    logger.LogInformation("  Stage 3 Universe: '{Name}' — {Series} series, {Works} works, QID={Qid}",
                        ph.DisplayName, childCount, workCount, ph.WikidataQid ?? "none");
                }

                if (parentCollections.Count == 0)
                {
                    report.IssuesFound.Add("Stage 3: No parent collections (universes) created after enrichment");
                    logger.LogWarning("[Phase 7] No universes found after Stage 3 enrichment");
                }

                var fictionalEntityCount = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM fictional_entities;");
                var workLinkCount = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM fictional_entity_work_links;");
                var performerLinkCount = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM character_performer_links;");

                logger.LogInformation(
                    "  Stage 3 graph: {Entities} fictional entities, {WorkLinks} work links, {PerformerLinks} performer links",
                    fictionalEntityCount,
                    workLinkCount,
                    performerLinkCount);

                if (parentCollections.Count > 0 && fictionalEntityCount == 0)
                {
                    report.IssuesFound.Add("Stage 3: No fictional entities were stored after enrichment");
                    logger.LogWarning("[Phase 7] No fictional entities found after Stage 3 enrichment");
                }

                if (fictionalEntityCount > 0 && workLinkCount == 0)
                {
                    report.IssuesFound.Add("Stage 3: Fictional entities were stored but no work links were created");
                    logger.LogWarning("[Phase 7] No fictional entity work links found after Stage 3 enrichment");
                }
            }
        }
        catch (Exception ex)
        {
            report.IssuesFound.Add($"Stage 3 enrichment error: {ex.Message}");
            logger.LogError(ex, "[Phase 7] Stage 3 enrichment failed");
        }
    }

    // ── Check universes ───────────────────────────────────────────────────

    private static async Task WaitForArtworkActivityToSettleAsync(
        IDatabaseConnection db,
        ILogger logger,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var lastEvidenceCount = -1;
        var stableCount = 0;

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);

            int evidenceCount;
            using (var conn = db.CreateConnection())
            {
                evidenceCount = await conn.ExecuteScalarAsync<int>("""
                    SELECT
                        (SELECT COUNT(*) FROM entity_assets)
                        + (SELECT COUNT(*) FROM character_portraits
                           WHERE local_image_path IS NOT NULL
                             AND TRIM(local_image_path) <> '')
                    """);
            }

            if (evidenceCount == lastEvidenceCount)
            {
                stableCount++;
            }
            else
            {
                stableCount = 0;
            }

            lastEvidenceCount = evidenceCount;
            if (stableCount >= 2)
            {
                break;
            }
        }

        logger.LogInformation("  Stage 3 artwork evidence settled at {Count}", Math.Max(lastEvidenceCount, 0));
    }

    private static async Task ValidateSeriesHarnessAsync(
        IDatabaseConnection db,
        TestReport report,
        ILogger logger,
        CancellationToken ct)
    {
        report.SeriesHarnessChecks.Clear();
        report.CrossMediaSeriesChecks.Clear();

        var expectations = new[]
        {
            new SeriesHarnessExpectation("The Expanse", "Books/Audiobooks", ["books", "audiobooks"], 9, ["books", "audiobooks"]),
            new SeriesHarnessExpectation("Dune Chronicles", "Books/Audiobooks", ["books", "audiobooks"], 1, ["books", "audiobooks"]),
            new SeriesHarnessExpectation("Dune", "Movies", ["movies"], 2, ["movies"]),
            new SeriesHarnessExpectation("Breaking Bad", "TV", ["tv"], 2, ["tv"]),
            new SeriesHarnessExpectation("Shogun", "TV", ["tv"], 1, ["tv"]),
        };

        foreach (var expected in expectations.Where(e => e.ActiveTypeKeys.Any(key => report.ActiveTypes.Contains(key))))
        {
            ct.ThrowIfCancellationRequested();
            using var conn = db.CreateConnection();
            var row = await conn.QueryFirstOrDefaultAsync<SeriesHarnessRow>(new CommandDefinition(
                """
                WITH owned AS (
                    SELECT DISTINCT w.id AS WorkId,
                           COALESCE(
                               NULLIF(TRIM(w.wikidata_qid), ''),
                               (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'wikidata_qid' LIMIT 1),
                               (SELECT cv.value FROM canonical_values cv
                                INNER JOIN editions e ON e.work_id = w.id
                                INNER JOIN media_assets ma ON ma.edition_id = e.id
                                WHERE cv.entity_id = ma.id AND cv.key = 'wikidata_qid' LIMIT 1)
                           ) AS WorkQid,
                           COALESCE(
                               (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'series' LIMIT 1),
                               (SELECT cv.value FROM canonical_values cv
                                INNER JOIN editions e ON e.work_id = w.id
                                INNER JOIN media_assets ma ON ma.edition_id = e.id
                                WHERE cv.entity_id = ma.id AND cv.key = 'series' LIMIT 1),
                               (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = COALESCE(gp.id, p.id, w.id) AND cv.key = 'series' LIMIT 1),
                               (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = COALESCE(gp.id, p.id, w.id) AND cv.key = 'title' LIMIT 1)
                           ) AS SeriesName,
                           COALESCE(
                               (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'series_qid' LIMIT 1),
                               (SELECT cv.value FROM canonical_values cv
                                INNER JOIN editions e ON e.work_id = w.id
                                INNER JOIN media_assets ma ON ma.edition_id = e.id
                                WHERE cv.entity_id = ma.id AND cv.key = 'series_qid' LIMIT 1)
                           ) AS SeriesQid
                    FROM works w
                    LEFT JOIN works p ON p.id = w.parent_work_id
                    LEFT JOIN works gp ON gp.id = p.parent_work_id
                    WHERE NOT EXISTS (SELECT 1 FROM works child WHERE child.parent_work_id = w.id)
                      AND EXISTS (
                          SELECT 1
                          FROM editions owned_e
                          INNER JOIN media_assets owned_ma ON owned_ma.edition_id = owned_e.id
                          WHERE owned_e.work_id = w.id
                            AND owned_ma.file_path_root IS NOT NULL
                            AND TRIM(owned_ma.file_path_root) <> ''
                      )
                      AND (
                          (@includeBooks = 1 AND LOWER(REPLACE(w.media_type, ' ', '')) IN ('book', 'books'))
                          OR (@includeAudiobooks = 1 AND LOWER(REPLACE(w.media_type, ' ', '')) IN ('audiobook', 'audiobooks'))
                          OR (@includeMovies = 1 AND LOWER(REPLACE(w.media_type, ' ', '')) IN ('movie', 'movies', 'video'))
                          OR (@includeTv = 1 AND LOWER(REPLACE(w.media_type, ' ', '')) IN ('tv', 'television', 'tvshow', 'tvshows'))
                          OR (@includeMusic = 1 AND LOWER(REPLACE(w.media_type, ' ', '')) IN ('music', 'track', 'tracks', 'song', 'songs'))
                          OR (@includeComics = 1 AND LOWER(REPLACE(w.media_type, ' ', '')) IN ('comic', 'comics'))
                      )
                ),
                matched AS (
                    SELECT *
                    FROM owned
                    WHERE SeriesName LIKE @seriesPattern
                ),
                manifest AS (
                    SELECT COUNT(DISTINCT smi.item_qid) AS ManifestCount
                    FROM series_manifest_items smi
                    WHERE smi.series_qid IN (
                        SELECT DISTINCT
                               CASE
                                   WHEN instr(SeriesQid, '::') > 0 THEN substr(SeriesQid, 1, instr(SeriesQid, '::') - 1)
                                   ELSE SeriesQid
                               END
                        FROM matched
                        WHERE SeriesQid IS NOT NULL AND TRIM(SeriesQid) <> ''
                    )
                )
                SELECT
                    (SELECT COUNT(*) FROM matched) AS OwnedCount,
                    COALESCE((SELECT ManifestCount FROM manifest), 0) AS ManifestCount;
                """,
                new
                {
                    seriesPattern = $"%{expected.Series}%",
                    includeBooks = IncludesMediaType(expected, "books") ? 1 : 0,
                    includeAudiobooks = IncludesMediaType(expected, "audiobooks") ? 1 : 0,
                    includeMovies = IncludesMediaType(expected, "movies") ? 1 : 0,
                    includeTv = IncludesMediaType(expected, "tv") ? 1 : 0,
                    includeMusic = IncludesMediaType(expected, "music") ? 1 : 0,
                    includeComics = IncludesMediaType(expected, "comics") ? 1 : 0,
                },
                cancellationToken: ct));

            var owned = row?.OwnedCount ?? 0;
            var manifestCount = row?.ManifestCount ?? 0;
            var known = Math.Max(owned, manifestCount);
            var check = new SeriesHarnessCheckResult
            {
                Series = expected.Series,
                MediaType = expected.MediaType,
                OwnedCount = owned,
                KnownCount = known,
                ExpectedMinimumKnown = expected.MinimumKnownCount,
                HasManifest = manifestCount > 0,
            };

            report.SeriesHarnessChecks.Add(check);
            logger.LogInformation(
                "  Series {Series} ({Type}): owned={Owned}, known={Known}, manifest={Manifest}",
                check.Series, check.MediaType, check.OwnedCount, check.KnownCount, check.HasManifest);

            if (!check.Pass)
            {
                report.IssuesFound.Add($"Series totals: {check.Series} ({check.MediaType}) did not meet harness expectations. {check.Detail}");
            }
        }

        await ValidateCrossMediaSeriesHarnessAsync(db, report, logger, ct);
    }

    private static Task ValidateMusicAlbumHarnessAsync(
        IDatabaseConnection db,
        TestReport report,
        ILogger logger,
        CancellationToken ct)
    {
        report.MusicAlbumChecks.Clear();
        if (!report.ActiveTypes.Contains("music"))
        {
            return Task.CompletedTask;
        }

        var expectations = new[]
        {
            new MusicAlbumHarnessExpectation("Heroes", "David Bowie", 1, 10),
            new MusicAlbumHarnessExpectation("Abbey Road", "The Beatles", 2, 17),
            new MusicAlbumHarnessExpectation("DAMN.", "Kendrick Lamar", 1, 14),
            new MusicAlbumHarnessExpectation("Interstellar", "Hans Zimmer", 2, ExpectedKnownTrackCount: null),
        };

        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var rows = connection.Query<MusicAlbumHarnessRow>(new CommandDefinition(
            """
            SELECT parent.id AS RootWorkId,
                   COALESCE(
                       NULLIF((SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = parent.id AND key = 'album' LIMIT 1), ''),
                       NULLIF((SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = parent.id AND key = 'title' LIMIT 1), ''),
                       NULLIF((SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id IN (child.id, asset.id) AND key = 'album' LIMIT 1), '')
                   ) AS Album,
                   COALESCE(
                       NULLIF((SELECT value FROM canonical_value_arrays WHERE entity_id = parent.id AND key IN ('album_artist', 'artist', 'author') ORDER BY ordinal LIMIT 1), ''),
                       NULLIF((SELECT value FROM canonical_value_arrays WHERE entity_id IN (child.id, asset.id) AND key IN ('album_artist', 'artist', 'author') ORDER BY ordinal LIMIT 1), '')
                   ) AS Artist,
                   COUNT(DISTINCT child.id) AS OwnedTrackCount,
                   (SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = parent.id AND key = 'child_entities_json' LIMIT 1) AS ChildEntitiesJson,
                   CASE WHEN EXISTS (
                       SELECT 1
                       FROM canonical_values cover
                       WHERE cover.entity_id IN (parent.id, child.id, asset.id)
                         AND cover.key IN ('cover', 'cover_url')
                         AND NULLIF(TRIM(CAST(cover.value AS TEXT)), '') IS NOT NULL
                   ) THEN 1 ELSE 0 END AS HasCover
            FROM works parent
            INNER JOIN works child ON child.parent_work_id = parent.id
            INNER JOIN editions edition ON edition.work_id = child.id
            INNER JOIN media_assets asset ON asset.edition_id = edition.id
            WHERE LOWER(parent.media_type) = 'music'
              AND parent.work_kind = 'parent'
              AND COALESCE(parent.is_catalog_only, 0) = 0
            GROUP BY parent.id
            """,
            cancellationToken: ct)).AsList();

        foreach (var expectation in expectations)
        {
            var row = rows.FirstOrDefault(candidate =>
                MusicAlbumHarnessNamesMatch(candidate.Album, expectation.Album)
                && string.Equals(candidate.Artist, expectation.Artist, StringComparison.OrdinalIgnoreCase));
            var knownTrackCount = CountMusicManifestTracks(row?.ChildEntitiesJson);
            var check = new MusicAlbumHarnessCheckResult
            {
                Album = expectation.Album,
                Artist = expectation.Artist,
                OwnedTrackCount = row?.OwnedTrackCount ?? 0,
                KnownTrackCount = knownTrackCount,
                ExpectedOwnedTrackCount = expectation.ExpectedOwnedTrackCount,
                ExpectedKnownTrackCount = expectation.ExpectedKnownTrackCount,
                HasManifest = knownTrackCount.HasValue,
                HasCover = row?.HasCover == true,
            };
            report.MusicAlbumChecks.Add(check);
            logger.LogInformation(
                "  Music album {Artist} / {Album}: owned={Owned}, known={Known}, cover={Cover}, pass={Pass}",
                check.Artist,
                check.Album,
                check.OwnedTrackCount,
                check.KnownTrackCount,
                check.HasCover,
                check.Pass);

            if (!check.Pass)
            {
                report.IssuesFound.Add($"Music album: {check.Artist} / {check.Album} did not meet harness expectations. {check.Detail}");
            }
        }

        return Task.CompletedTask;
    }

    private static bool MusicAlbumHarnessNamesMatch(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        static string Normalize(string value)
            => string.Join(
                ' ',
                new string(value
                        .ToLowerInvariant()
                        .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
                        .ToArray())
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal)
            || normalizedLeft.StartsWith(normalizedRight + " ", StringComparison.Ordinal)
            || normalizedRight.StartsWith(normalizedLeft + " ", StringComparison.Ordinal);
    }

    private static int? CountMusicManifestTracks(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            return document.RootElement.TryGetProperty("tracks", out var tracks)
                && tracks.ValueKind == JsonValueKind.Array
                    ? tracks.GetArrayLength()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task ValidateCrossMediaSeriesHarnessAsync(
        IDatabaseConnection db,
        TestReport report,
        ILogger logger,
        CancellationToken ct)
    {
        var expectations = new[]
        {
            new CrossMediaSeriesExpectation(
                "The Expanse books/audiobooks/TV",
                ["the expanse", "leviathan wakes", "caliban"],
                ["books", "audiobooks", "tv"],
                ["Books", "Audiobooks", "TV"]),
            new CrossMediaSeriesExpectation(
                "Dune audiobook/movie",
                ["dune"],
                ["audiobooks", "movies"],
                ["Audiobooks", "Movies"]),
            new CrossMediaSeriesExpectation(
                "Batman comic/movie",
                ["batman", "dark knight"],
                ["comics", "movies"],
                ["Comics", "Movies"]),
        };

        var activeExpectations = expectations
            .Where(expected => expected.RequiredMediaTypeKeys.All(key => report.ActiveTypes.Contains(key)))
            .ToList();

        if (activeExpectations.Count == 0)
        {
            return;
        }

        using var conn = db.CreateConnection();
        var rows = (await conn.QueryAsync<CrossMediaHarnessRow>(new CommandDefinition(
            $"""
            WITH leaf AS (
                SELECT DISTINCT w.id AS work_id,
                       COALESCE(gp.id, p.id, w.id) AS root_work_id,
                       w.media_type,
                       w.collection_id,
                       ma.id AS asset_id
                FROM works w
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                INNER JOIN editions e ON e.work_id = w.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                WHERE NOT EXISTS (SELECT 1 FROM works child WHERE child.parent_work_id = w.id)
            )
            SELECT DISTINCT
                l.media_type AS MediaType,
                COALESCE(
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.asset_id AND cv.key IN ('title', 'episode_title', 'album') LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.work_id AND cv.key IN ('title', 'episode_title', 'album') LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.root_work_id AND cv.key IN ('title', 'show_name', 'album') LIMIT 1)
                ) AS Title,
                COALESCE(
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.asset_id AND cv.key IN ('series', 'show_name', 'album') LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.work_id AND cv.key IN ('series', 'show_name', 'album') LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.root_work_id AND cv.key IN ('series', 'show_name', 'album', 'title') LIMIT 1),
                    c.display_name
                ) AS SeriesName,
                {GuidSql.TextProjection("l.collection_id")} AS CollectionId,
                {GuidSql.TextProjection("c.parent_collection_id")} AS ParentCollectionId,
                c.wikidata_qid AS CollectionQid,
                pc.wikidata_qid AS ParentCollectionQid,
                COALESCE(
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.asset_id AND cv.key = 'fictional_universe_qid' LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.work_id AND cv.key = 'fictional_universe_qid' LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.root_work_id AND cv.key = 'fictional_universe_qid' LIMIT 1),
                    pc.wikidata_qid
                ) AS FictionalUniverseQid,
                COALESCE(
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.asset_id AND cv.key = 'franchise_qid' LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.work_id AND cv.key = 'franchise_qid' LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.root_work_id AND cv.key = 'franchise_qid' LIMIT 1)
                ) AS FranchiseQid,
                COALESCE(
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.asset_id AND cv.key = 'narrative_root_qid' LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.work_id AND cv.key = 'narrative_root_qid' LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.root_work_id AND cv.key = 'narrative_root_qid' LIMIT 1),
                    pc.wikidata_qid
                ) AS NarrativeRootQid,
                COALESCE(
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.asset_id AND cv.key = 'series_qid' LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.work_id AND cv.key = 'series_qid' LIMIT 1),
                    (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = l.root_work_id AND cv.key = 'series_qid' LIMIT 1),
                    c.wikidata_qid
                ) AS SeriesQid
            FROM leaf l
            LEFT JOIN collections c ON c.id = l.collection_id
            LEFT JOIN collections pc ON pc.id = c.parent_collection_id;
            """,
            cancellationToken: ct))).AsList();

        foreach (var expected in activeExpectations)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = rows
                .Where(row => expected.RequiredMediaTypeKeys.Contains(NormalizeHarnessMediaTypeKey(row.MediaType), StringComparer.OrdinalIgnoreCase))
                .Where(row => MatchesCrossMediaPattern(row, expected))
                .ToList();

            var foundTypes = candidates
                .Select(row => NormalizeHarnessMediaTypeDisplay(row.MediaType))
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool hasSharedParent = candidates
                .Where(row => !string.IsNullOrWhiteSpace(row.ParentCollectionId))
                .GroupBy(row => row.ParentCollectionId!, StringComparer.OrdinalIgnoreCase)
                .Any(group => group
                    .Select(row => NormalizeHarnessMediaTypeKey(row.MediaType))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() >= 2);

            bool hasSharedNarrativeRoot = candidates
                .SelectMany(row => SharedCrossMediaQids(row)
                    .Select(qid => new { Qid = qid, TypeKey = NormalizeHarnessMediaTypeKey(row.MediaType) }))
                .GroupBy(row => row.Qid, StringComparer.OrdinalIgnoreCase)
                .Any(group => group
                    .Select(row => row.TypeKey)
                    .Where(type => !string.IsNullOrWhiteSpace(type))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() >= 2);

            var check = new CrossMediaSeriesCheckResult
            {
                Name = expected.Name,
                RequiredMediaTypes = expected.RequiredMediaTypes.ToList(),
                FoundMediaTypes = foundTypes,
                OwnedCount = candidates.Count,
                HasSharedParentCollection = hasSharedParent,
                HasSharedNarrativeRoot = hasSharedNarrativeRoot,
            };

            report.CrossMediaSeriesChecks.Add(check);
            logger.LogInformation(
                "  Cross-media {Name}: owned={Owned}, found={Found}, parent={Parent}, narrative={Narrative}",
                check.Name,
                check.OwnedCount,
                string.Join(", ", check.FoundMediaTypes),
                check.HasSharedParentCollection,
                check.HasSharedNarrativeRoot);

            if (!check.Pass)
            {
                report.IssuesFound.Add($"Cross-media series: {check.Name} did not meet harness expectations. {check.Detail}");
            }
        }
    }

    private static async Task ValidatePersonCoverageAsync(
        IPersonRepository personRepo,
        IPersonCreditReadService personCreditReadService,
        TestReport report,
        ILogger logger,
        CancellationToken ct)
    {
        report.PersonCoverageChecks.Clear();
        var expectations = new[]
        {
            new PersonCoverageExpectation(
                "Stephen King",
                ["books", "audiobooks"],
                [
                    new PersonCreditRequirement("books", "Author", 2),
                    new PersonCreditRequirement("audiobooks", "Author", 1),
                ]),
            new PersonCoverageExpectation(
                "James S. A. Corey",
                ["books", "audiobooks"],
                [
                    new PersonCreditRequirement("books", "Author", 2),
                    new PersonCreditRequirement("audiobooks", "Author", 1),
                ]),
            new PersonCoverageExpectation(
                "David Bowie",
                ["music"],
                [
                    new PersonCreditRequirement("music", "Artist", 2),
                ]),
            new PersonCoverageExpectation(
                "Andy Serkis",
                ["audiobooks", "movies"],
                [
                    new PersonCreditRequirement("audiobooks", "Narrator", 1, "The Hobbit"),
                    new PersonCreditRequirement("movies", "Actor", 1, "Planet of the Apes"),
                ]),
            new PersonCoverageExpectation(
                "Bryan Cranston",
                ["movies", "tv"],
                [
                    new PersonCreditRequirement("movies", "Actor", 1, "Drive"),
                    new PersonCreditRequirement("tv", "Actor", 1, "Breaking Bad"),
                ]),
        };

        foreach (var expected in expectations.Where(expected =>
                     expected.RequiredMediaTypeKeys.All(key => report.ActiveTypes.Contains(key))))
        {
            ct.ThrowIfCancellationRequested();
            var person = await personRepo.FindByNameAsync(expected.Name, ct);
            var check = new PersonCoverageCheckResult
            {
                Name = expected.Name,
                PersonFound = person is not null,
            };

            var credits = person is null
                ? []
                : await personCreditReadService.GetLibraryCreditsAsync(person.Id, ct);
            foreach (var requirement in expected.Requirements)
            {
                var actualCount = credits
                    .Where(credit =>
                        string.Equals(
                            NormalizeHarnessMediaTypeKey(credit.MediaType),
                            requirement.MediaTypeKey,
                            StringComparison.OrdinalIgnoreCase))
                    .Where(credit =>
                        MatchesHarnessContributorRole(credit.Role, requirement.Role))
                    .Where(credit =>
                        string.IsNullOrWhiteSpace(requirement.TitlePattern)
                        || ContainsIgnoreCase(credit.Title, requirement.TitlePattern))
                    .Select(credit => credit.WorkId)
                    .Distinct()
                    .Count();

                check.Requirements.Add(new PersonCreditRequirementResult
                {
                    MediaType = NormalizeHarnessMediaTypeDisplay(requirement.MediaTypeKey),
                    Role = requirement.Role,
                    TitlePattern = requirement.TitlePattern,
                    MinimumCount = requirement.MinimumCount,
                    ActualCount = actualCount,
                });
            }

            report.PersonCoverageChecks.Add(check);
            logger.LogInformation(
                "  Person coverage {Name}: found={Found}, pass={Pass}, {Detail}",
                check.Name,
                check.PersonFound,
                check.Pass,
                check.Detail);
            if (!check.Pass)
            {
                report.IssuesFound.Add(
                    $"Person coverage: {check.Name} did not meet repeat/cross-media expectations. {check.Detail}");
            }
        }
    }

    private static async Task ValidateOutwardDetailRoutesAsync(
        ICollectionBrowseReadService collectionBrowse,
        DetailComposerService detailComposer,
        TestReport report,
        ILogger logger,
        CancellationToken ct)
    {
        report.DetailRouteChecks.Clear();

        if (report.ActiveTypes.Contains("audiobooks"))
        {
            await ValidateSystemViewAsync(
                "Audiobook series",
                "Audiobooks",
                "series",
                DetailEntityType.Collection,
                DetailPresentationContext.Listen,
                group => SystemViewGroupIdentity.CreateId(group, "Audiobooks", "series"),
                id => $"/details/collection/{id:D}?context=listen",
                enforceSharedCollectionContract: true);
        }

        if (report.ActiveTypes.Contains("books"))
        {
            await ValidateSystemViewAsync(
                "Book series",
                "Books",
                "series",
                DetailEntityType.BookSeries,
                DetailPresentationContext.Read,
                group => SystemViewGroupIdentity.CreateId(group, "Books", "series"),
                id => $"/details/bookseries/{id:D}?context=read");
        }

        if (report.ActiveTypes.Contains("music"))
        {
            await ValidateSystemViewAsync(
                "Music album",
                "Music",
                "album",
                DetailEntityType.MusicAlbum,
                DetailPresentationContext.Listen,
                group => group.RootWorkId ?? group.CollectionId,
                id => $"/details/musicalbum/{id:D}?context=listen",
                expectedTabs: ["overview", "details"]);
        }

        if (report.ActiveTypes.Contains("tv"))
        {
            await ValidateSystemViewAsync(
                "TV show",
                "TV",
                "show_name",
                DetailEntityType.TvShow,
                DetailPresentationContext.Watch,
                group => group.RootWorkId ?? group.CollectionId,
                id => $"/details/tvshow/{id:D}?context=watch");
        }

        async Task ValidateSystemViewAsync(
            string surface,
            string mediaType,
            string groupField,
            DetailEntityType entityType,
            DetailPresentationContext context,
            Func<ContentGroupDto, Guid> idSelector,
            Func<Guid, string> routeSelector,
            bool enforceSharedCollectionContract = false,
            IReadOnlyList<string>? expectedTabs = null)
        {
            var groups = await collectionBrowse
                .GetSystemViewGroupsAsync(mediaType, groupField, ct)
                .ConfigureAwait(false);
            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                var id = idSelector(group);
                var check = new DetailRouteCheckResult
                {
                    Surface = surface,
                    Title = group.DisplayName,
                    Route = routeSelector(id),
                };

                try
                {
                    var detail = await detailComposer
                        .BuildAsync(entityType, id, context, ct)
                        .ConfigureAwait(false);
                    check.Resolved = detail is not null;
                    if (detail is not null)
                    {
                        var requiredTabs = expectedTabs
                            ?? (enforceSharedCollectionContract ? ["overview", "details"] : null);
                        check.UsesExpectedTabs = requiredTabs is null
                            || detail.Tabs.Select(tab => tab.Key)
                                .SequenceEqual(requiredTabs, StringComparer.OrdinalIgnoreCase);
                        check.SuppressesCollectionCredits = !enforceSharedCollectionContract
                            || (detail.ContributorGroups.Count == 0 && detail.PreviewContributors.Count == 0);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Outward detail route failed for {Surface} {Title} at {Route}",
                        surface,
                        group.DisplayName,
                        check.Route);
                }

                report.DetailRouteChecks.Add(check);
                logger.LogInformation(
                    "  Detail route {Surface} / {Title}: resolved={Resolved}, tabs={Tabs}, noCollectionCredits={NoCredits}",
                    check.Surface,
                    check.Title,
                    check.Resolved,
                    check.UsesExpectedTabs,
                    check.SuppressesCollectionCredits);
                if (!check.Pass)
                {
                    report.IssuesFound.Add(
                        $"Detail route: {check.Surface} '{check.Title}' failed at {check.Route}. {check.Detail}");
                }
            }
        }
    }

    private static async Task ValidateCharacterArtworkAsync(
        IDatabaseConnection db,
        IPersonCreditReadService personCreditReadService,
        TestReport report,
        ILogger logger,
        CancellationToken ct)
    {
        report.CharacterArtworkChecks.Clear();

        if (!report.ActiveTypes.Contains("movies"))
        {
            return;
        }

        var cases = new[]
        {
            new CharacterArtworkExpectation("The Shawshank Redemption", "Q172241", "Tim Robbins", "Andy Dufresne", "%Dufresne%"),
            new CharacterArtworkExpectation("The Matrix", "Q83495", "Keanu Reeves", "Neo", "%Neo%"),
        };

        foreach (var expected in cases)
        {
            await ValidateCharacterArtworkCaseAsync(db, personCreditReadService, report, logger, expected, ct);
        }
    }

    private static async Task ValidateCharacterArtworkCaseAsync(
        IDatabaseConnection db,
        IPersonCreditReadService personCreditReadService,
        TestReport report,
        ILogger logger,
        CharacterArtworkExpectation expected,
        CancellationToken ct)
    {
        var check = new CharacterArtworkCheckResult
        {
            Title = expected.Title,
            WorkQid = expected.WorkQid,
            ActorName = expected.ActorName,
            CharacterName = expected.CharacterName,
        };

        using var conn = db.CreateConnection();
        var workIdValue = await conn.ExecuteScalarAsync<object?>(
            """
            WITH work_assets AS (
                SELECT w.id AS work_id,
                       COALESCE(gp.id, p.id, w.id) AS root_work_id,
                       ma.id AS asset_id
                FROM works w
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                INNER JOIN editions e ON e.work_id = w.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
            ),
            work_qids AS (
                SELECT wa.work_id,
                       COALESCE(
                           (SELECT w.wikidata_qid FROM works w
                            WHERE w.id = wa.work_id
                              AND w.wikidata_qid IS NOT NULL
                              AND TRIM(w.wikidata_qid) <> ''
                            LIMIT 1),
                           (SELECT cv.value FROM canonical_values cv
                            WHERE cv.entity_id = wa.asset_id AND cv.key = 'wikidata_qid' LIMIT 1),
                           (SELECT cv.value FROM canonical_values cv
                            WHERE cv.entity_id = wa.work_id AND cv.key = 'wikidata_qid' LIMIT 1),
                           (SELECT cv.value FROM canonical_values cv
                            WHERE cv.entity_id = wa.root_work_id AND cv.key = 'wikidata_qid' LIMIT 1),
                           (SELECT ij.resolved_qid FROM identity_jobs ij
                            WHERE ij.entity_id = wa.asset_id
                              AND ij.resolved_qid IS NOT NULL
                              AND TRIM(ij.resolved_qid) <> ''
                            ORDER BY ij.updated_at DESC, ij.created_at DESC
                            LIMIT 1)
                       ) AS qid
                FROM work_assets wa
            )
            SELECT work_id
            FROM work_qids
            WHERE qid = @workQid
            LIMIT 1;
            """,
            new { workQid = expected.WorkQid });

        check.WorkFound = TryReadCurrentGuid(workIdValue, out var workId);
        if (!check.WorkFound)
        {
            check.Detail = $"{expected.Title} work row was not found by Wikidata QID.";
            report.CharacterArtworkChecks.Add(check);
            report.IssuesFound.Add($"Character art: {expected.Title} was not found by QID {expected.WorkQid}");
            logger.LogWarning("[Phase 7c] Character art validation could not find {Title} ({Qid})", expected.Title, expected.WorkQid);
            return;
        }

        var row = await conn.QueryFirstOrDefaultAsync<CharacterArtworkRow>(
            """
            SELECT p.name                  AS ActorName,
                   fe.label                AS CharacterName,
                   fe.wikidata_qid         AS CharacterQid,
                   cp.id                   AS PortraitId,
                   cp.image_url            AS PortraitImageUrl,
                   cp.local_image_path     AS PortraitLocalImagePath,
                   cp.source_provider      AS PortraitSourceProvider,
                   cpl.work_qid            AS WorkQid
            FROM character_performer_links cpl
            INNER JOIN persons p
                ON p.id = cpl.person_id
            INNER JOIN fictional_entities fe
                ON fe.id = cpl.fictional_entity_id
            LEFT JOIN character_portraits cp
                ON cp.person_id = p.id
               AND cp.fictional_entity_id = fe.id
            WHERE cpl.work_qid = @workQid
              AND p.name = @actorName
              AND fe.label LIKE @characterPattern
            LIMIT 1;
            """,
            new
            {
                workQid = expected.WorkQid,
                actorName = expected.ActorName,
                characterPattern = expected.CharacterPattern,
            });

        check.HasMovieScopedActorCharacterLink = row is not null
            && string.Equals(row.WorkQid, expected.WorkQid, StringComparison.OrdinalIgnoreCase);
        check.HasPortraitRow = TryReadCurrentGuid(row?.PortraitId, out _);
        check.PortraitSourceProvider = row?.PortraitSourceProvider;
        check.HasUnverifiedPortrait = string.Equals(row?.PortraitSourceProvider, "tmdb_tagged_images", StringComparison.OrdinalIgnoreCase);
        check.PortraitImageUrl = row?.PortraitImageUrl;
        check.PortraitLocalImagePath = row?.PortraitLocalImagePath;
        check.HasDownloadedPortraitFile = !string.IsNullOrWhiteSpace(row?.PortraitLocalImagePath)
            && File.Exists(row.PortraitLocalImagePath);

        var cast = await personCreditReadService.BuildForWorkAsync(workId, ct);
        var castEntry = cast
            .Select((credit, index) => new { Credit = credit, Position = index + 1 })
            .FirstOrDefault(entry =>
                string.Equals(entry.Credit.Name, expected.ActorName, StringComparison.OrdinalIgnoreCase));
        var timCredit = castEntry?.Credit;
        var portrayal = timCredit?.Characters.FirstOrDefault(character =>
            character.CharacterName?.Contains(expected.CharacterName, StringComparison.OrdinalIgnoreCase) == true);

        check.CastPosition = castEntry?.Position;
        check.IsInPrimaryCastPreview = check.CastPosition is > 0 and <= 5;
        check.HasDisplayCharacterName = portrayal is not null
            && string.Equals(timCredit?.Name, expected.ActorName, StringComparison.OrdinalIgnoreCase);
        check.DisplayImageUrl = portrayal?.PortraitUrl;
        check.HasDisplayCharacterImage = !string.IsNullOrWhiteSpace(portrayal?.PortraitUrl);

        check.Detail = check.Pass
            ? "Actor, character name, preview placement, and verified portrait handling are correct."
            : BuildCharacterArtworkFailureDetail(check);

        report.CharacterArtworkChecks.Add(check);

        logger.LogInformation(
            "  Character art: {Title} / {Actor} as {Character}: link={Link}, portrait={Portrait}, downloaded={Downloaded}, display={Display}",
            expected.Title,
            expected.ActorName,
            expected.CharacterName,
            check.HasMovieScopedActorCharacterLink,
            check.HasPortraitRow,
            check.HasDownloadedPortraitFile,
            check.HasDisplayCharacterImage);

        if (!check.Pass)
        {
            report.IssuesFound.Add($"Character display: {expected.Title} does not safely display {expected.ActorName} as {expected.CharacterName} ({check.Detail})");
        }
    }

    private sealed record CharacterArtworkExpectation(
        string Title,
        string WorkQid,
        string ActorName,
        string CharacterName,
        string CharacterPattern);

    private sealed record SeriesHarnessExpectation(
        string Series,
        string MediaType,
        string[] ActiveTypeKeys,
        int MinimumKnownCount,
        string[] MediaTypeKeys);

    private sealed record CrossMediaSeriesExpectation(
        string Name,
        string[] MatchPatterns,
        string[] RequiredMediaTypeKeys,
        string[] RequiredMediaTypes);

    private sealed record PersonCoverageExpectation(
        string Name,
        string[] RequiredMediaTypeKeys,
        PersonCreditRequirement[] Requirements);

    private sealed record PersonCreditRequirement(
        string MediaTypeKey,
        string Role,
        int MinimumCount,
        string? TitlePattern = null);

    private sealed record MusicAlbumHarnessExpectation(
        string Album,
        string Artist,
        int ExpectedOwnedTrackCount,
        int? ExpectedKnownTrackCount);

    private sealed class SeriesHarnessRow
    {
        public int OwnedCount { get; init; }
        public int ManifestCount { get; init; }
    }

    private sealed class MusicAlbumHarnessRow
    {
        public Guid RootWorkId { get; init; }
        public string? Album { get; init; }
        public string? Artist { get; init; }
        public int OwnedTrackCount { get; init; }
        public string? ChildEntitiesJson { get; init; }
        public bool HasCover { get; init; }
    }

    private sealed class CrossMediaHarnessRow
    {
        public string? MediaType { get; init; }
        public string? Title { get; init; }
        public string? SeriesName { get; init; }
        public string? CollectionId { get; init; }
        public string? ParentCollectionId { get; init; }
        public string? CollectionQid { get; init; }
        public string? ParentCollectionQid { get; init; }
        public string? FictionalUniverseQid { get; init; }
        public string? FranchiseQid { get; init; }
        public string? NarrativeRootQid { get; init; }
        public string? SeriesQid { get; init; }
    }

    private sealed class CharacterArtworkRow
    {
        public string? ActorName { get; init; }
        public string? CharacterName { get; init; }
        public string? CharacterQid { get; init; }
        public object? PortraitId { get; init; }
        public string? PortraitImageUrl { get; init; }
        public string? PortraitLocalImagePath { get; init; }
        public string? PortraitSourceProvider { get; init; }
        public string? WorkQid { get; init; }
    }

    private static string BuildCharacterArtworkFailureDetail(CharacterArtworkCheckResult check)
    {
        var missing = new List<string>();
        if (!check.WorkFound)
        {
            missing.Add("work row");
        }

        if (!check.HasMovieScopedActorCharacterLink)
        {
            missing.Add("movie-scoped actor-character link");
        }

        if (!check.HasDisplayCharacterName)
        {
            missing.Add("detail cast character name");
        }

        if (!check.IsInPrimaryCastPreview)
        {
            missing.Add("top cast preview position");
        }

        if (check.HasUnverifiedPortrait)
        {
            missing.Add("unverified TMDB tagged character portrait");
        }

        return missing.Count == 0
            ? "No missing checks were recorded."
            : "Missing: " + string.Join(", ", missing) + ".";
    }

    private static async Task CheckUniversesAsync(
        ILibraryItemRepository libraryItemRepo,
        TestReport report,
        ILogger logger,
        CancellationToken ct)
    {
        // Check if items that should form universes got QIDs (prerequisite for universe formation)
        var allItems = await libraryItemRepo.GetPageAsync(new LibraryItemQuery(Offset: 0, Limit: 500, IncludeAll: true), ct);

        // Look for Tolkien universe items (The Hobbit + Fellowship of the Ring)
        var tolkienItems = allItems.Items.Where(i =>
            i.Title.Contains("Hobbit", StringComparison.OrdinalIgnoreCase) ||
            i.Title.Contains("Fellowship", StringComparison.OrdinalIgnoreCase) ||
            i.Title.Contains("Lord of the Rings", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var tolkienResult = new UniverseResult
        {
            Name = "Tolkien (Middle-earth)",
            Found = tolkienItems.Any(i => !string.IsNullOrEmpty(i.WikidataQid)),
            WorkCount = tolkienItems.Count,
        };
        tolkienResult.WikidataQid = tolkienItems.FirstOrDefault(i => !string.IsNullOrEmpty(i.WikidataQid))?.WikidataQid;
        report.UniverseResults.Add(tolkienResult);

        // Look for Dune universe items
        var duneItems = allItems.Items.Where(i =>
            i.Title.Contains("Dune", StringComparison.OrdinalIgnoreCase) ||
            i.Title.Contains("Blade Runner", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var duneResult = new UniverseResult
        {
            Name = "Dune / Denis Villeneuve",
            Found = duneItems.Any(i => !string.IsNullOrEmpty(i.WikidataQid)),
            WorkCount = duneItems.Count,
        };
        duneResult.WikidataQid = duneItems.FirstOrDefault(i => !string.IsNullOrEmpty(i.WikidataQid))?.WikidataQid;
        report.UniverseResults.Add(duneResult);

        foreach (var u in report.UniverseResults)
        {
            logger.LogInformation("  Universe '{Name}': {Works} works, QID found={Found}",
                u.Name, u.WorkCount, u.Found);
        }
    }

    // ── Reconciliation pass ───────────────────────────────────────────────

    /// <summary>Dapper result row for the reconciliation SQL query.</summary>
    private sealed class WorkReconRow
    {
        public string TitleLower { get; set; } = "";
        public string MediaTypeLower { get; set; } = "";
        public string? WikidataQid { get; set; }
        public string? CuratorState { get; set; }
        public string? IdentityJobState { get; set; }
        public string? ReviewTrigger { get; set; }
        public bool HasStoredCoverArt { get; set; }
    }

    /// <summary>
    /// Compares each seed fixture's declared expectation against the actual
    /// post-ingestion state of the corresponding Work row. Produces a
    /// <see cref="ReconciliationSummary"/> stored on the report.
    /// </summary>
    private static async Task RunReconciliationAsync(
        IDatabaseConnection db,
        TestReport report,
        IReadOnlyCollection<string> activeTypes,
        ILogger logger,
        CancellationToken ct)
    {
        // Filter expectations to only the media types actually exercised by this run
        // (e.g. skip Comics if the run was launched without comics in the types filter).
        var activeSet = new HashSet<string>(activeTypes, StringComparer.OrdinalIgnoreCase);
        var expectations = DevSeedEndpoints.GetAllExpectations()
            .Where(e => activeSet.Contains(e.MediaType))
            .ToList();
        var summary = new ReconciliationSummary { ExpectedTotal = expectations.Count };

        // Build a lookup of (title_lower, media_type_lower) → (wikidata_qid, curator_state, review_trigger)
        // from the live database. TV episodes share the same show title so we may have
        // duplicates — we treat any row for that (title, type) pair as "one Work" for
        // reconciliation purposes (first resolved row wins for identified check).

        // Dapper anonymous class for SQL projection
        List<WorkReconRow> dbRows;
        using (var conn = db.CreateConnection())
        {
            // For reconciliation, we need to match the seed-supplied title against
            // ANY title we can find for the work — the file processor's claim,
            // the canonical value (which may have been overridden by Wikidata),
            // alternate_title claims, original_title, etc. We emit one row per
            // (work, title-source) pair via UNION so the C# index can lookup
            // the work from any of its known titles.
            dbRows = (await conn.QueryAsync<WorkReconRow>(
                """
                WITH work_assets AS (
                    SELECT w.id AS work_id,
                           COALESCE(gp.id, p.id, w.id) AS root_work_id,
                           w.media_type,
                           w.curator_state,
                           ma.id AS asset_id
                    FROM works w
                    LEFT JOIN works p ON p.id = w.parent_work_id
                    LEFT JOIN works gp ON gp.id = p.parent_work_id
                    INNER JOIN editions e ON e.work_id = w.id
                    INNER JOIN media_assets ma ON ma.edition_id = e.id
                ),
                work_qids AS (
                    SELECT wa.work_id,
                           COALESCE(
                               (SELECT cv.value FROM canonical_values cv
                                WHERE cv.entity_id = wa.asset_id AND cv.key = 'wikidata_qid' LIMIT 1),
                               (SELECT cv.value FROM canonical_values cv
                                WHERE cv.entity_id = wa.work_id AND cv.key = 'wikidata_qid' LIMIT 1),
                               (SELECT cv.value FROM canonical_values cv
                                WHERE cv.entity_id = wa.root_work_id AND cv.key = 'wikidata_qid' LIMIT 1),
                               (SELECT ij.resolved_qid FROM identity_jobs ij
                                WHERE ij.entity_id = wa.asset_id
                                  AND ij.resolved_qid IS NOT NULL
                                  AND TRIM(ij.resolved_qid) <> ''
                                ORDER BY ij.updated_at DESC, ij.created_at DESC
                                LIMIT 1)
                           ) AS qid
                    FROM work_assets wa
                ),
                work_reviews AS (
                    SELECT wa.work_id,
                           (SELECT rq.trigger FROM review_queue rq
                            WHERE rq.entity_id = wa.asset_id AND rq.status = 'Pending'
                            ORDER BY rq.created_at DESC LIMIT 1) AS trigger
                    FROM work_assets wa
                ),
                work_jobs AS (
                    SELECT wa.work_id,
                           (SELECT ij.state FROM identity_jobs ij
                            WHERE ij.entity_id = wa.asset_id
                            ORDER BY ij.updated_at DESC, ij.created_at DESC LIMIT 1) AS state
                    FROM work_assets wa
                ),
                titles AS (
                    -- Canonical title
                    SELECT wa.work_id,
                           wa.root_work_id,
                           wa.media_type,
                           wa.curator_state,
                           LOWER(cv.value) AS title_lower
                    FROM work_assets wa
                    INNER JOIN canonical_values cv ON cv.entity_id = wa.asset_id
                    WHERE cv.key IN ('title', 'original_title', 'show_name', 'series', 'episode_title', 'alternate_title', 'album')
                    UNION
                    -- File processor claim title (this is the seed-supplied title)
                    SELECT wa.work_id,
                           wa.root_work_id,
                           wa.media_type,
                           wa.curator_state,
                           LOWER(mc.claim_value) AS title_lower
                    FROM work_assets wa
                    INNER JOIN metadata_claims mc ON mc.entity_id = wa.asset_id
                    WHERE mc.claim_key IN ('title', 'original_title', 'show_name', 'series', 'episode_title', 'alternate_title', 'album')
                )
                SELECT DISTINCT
                    t.title_lower               AS TitleLower,
                    LOWER(t.media_type)         AS MediaTypeLower,
                    (SELECT qid FROM work_qids WHERE work_id = t.work_id) AS WikidataQid,
                    t.curator_state             AS CuratorState,
                    (SELECT state FROM work_jobs WHERE work_id = t.work_id) AS IdentityJobState,
                    (SELECT trigger FROM work_reviews WHERE work_id = t.work_id) AS ReviewTrigger,
                    EXISTS(
                        SELECT 1
                        FROM entity_assets ea
                        WHERE ea.entity_id = CASE
                                  WHEN t.media_type IN ('Books', 'Audiobooks', 'Comics') THEN t.work_id
                                  ELSE t.root_work_id
                              END
                          AND ea.asset_type = 'CoverArt'
                          AND ea.is_preferred = 1
                          AND ea.local_image_path IS NOT NULL
                          AND TRIM(ea.local_image_path) <> ''
                    ) AS HasStoredCoverArt
                FROM titles t
                WHERE t.title_lower IS NOT NULL AND t.title_lower <> ''
                """)).AsList();
        }

        // Cover-art lookup built from the central managed asset store.
        // Keyed by "title_lower|media_type_lower" → HasStoredCoverArt flag.
        var coverArtByKey = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in dbRows)
        {
            foreach (var key in BuildHarnessTitleMediaKeys(row.TitleLower, row.MediaTypeLower))
            {
                if (!coverArtByKey.TryGetValue(key, out var existing) || (row.HasStoredCoverArt && !existing))
                {
                    coverArtByKey[key] = row.HasStoredCoverArt;
                }
            }
        }

        var retailProviderByKey = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var identityProviderByKey = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var check in report.LibraryChecks)
        {
            foreach (var key in BuildHarnessTitleMediaKeys(check.Title, check.MediaType))
            {
                if (!retailProviderByKey.ContainsKey(key) || !string.IsNullOrWhiteSpace(check.ActualRetailProvider))
                {
                    retailProviderByKey[key] = check.ActualRetailProvider;
                }

                if (!identityProviderByKey.ContainsKey(key) || !string.IsNullOrWhiteSpace(check.IdentityProvider))
                {
                    identityProviderByKey[key] = check.IdentityProvider;
                }
            }
        }

        // Index by "title_lower|media_type_lower" → (wikidata_qid, curator_state, review_trigger)
        // When multiple rows share the same key (e.g. TV episodes, audiobook editions),
        // prefer rows that have a QID so "Identified" beats "Unresolved".
        var index = new Dictionary<string, (string? WikidataQid, string? CuratorState, string? IdentityJobState, string? ReviewTrigger)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in dbRows)
        {
            foreach (var key in BuildHarnessTitleMediaKeys(row.TitleLower, row.MediaTypeLower))
            {
                if (!index.TryGetValue(key, out var existing) ||
                    (!string.IsNullOrWhiteSpace(row.WikidataQid) && string.IsNullOrWhiteSpace(existing.WikidataQid)))
                {
                    index[key] = (WikidataQid: row.WikidataQid, CuratorState: row.CuratorState, IdentityJobState: row.IdentityJobState, ReviewTrigger: row.ReviewTrigger);
                }
            }
        }

        foreach (var exp in expectations)
        {
            string mediaTypeLower = exp.MediaType.ToLowerInvariant();
            var lookupKeys = new[] { exp.Title, exp.ReconciliationTitle }
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .SelectMany(title => BuildHarnessTitleMediaKeys(title, mediaTypeLower))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string expectedDesc = exp.ExpectIdentified
                ? (string.IsNullOrWhiteSpace(exp.ExpectedQid) ? "Identified" : $"Identified as {exp.ExpectedQid}")
                : $"InReview ({exp.ExpectedReviewTrigger ?? "any"})";

            // Not all active types were seeded — skip expectations for skipped types
            string typeKey = mediaTypeLower switch
            {
                "audiobooks" => "audiobooks",
                "movies" => "movies",
                "tv" => "tv",
                "music" => "music",
                "comics" => "comics",
                _ => "books",
            };
            if (report.SkippedTypes.ContainsKey(typeKey))
            {
                // Don't penalise for skipped types — exclude from reconciliation total
                summary.ExpectedTotal--;
                continue;
            }

            if (exp.ExpectIdentified)
            {
                summary.ExpectedResolved++;
                if (!string.IsNullOrWhiteSpace(exp.ExpectedQid))
                {
                    summary.ExpectedExactQid++;
                }
            }
            else if (!exp.KnownNoWikidataEntity)
            {
                summary.ExpectedReview++;
            }

            string? expectedProvider = GetExpectedRetailProvider(exp, exp.MediaType);
            string? actualRetailProvider = lookupKeys
                .Select(key => retailProviderByKey.TryGetValue(key, out var provider) ? provider : null)
                .FirstOrDefault(provider => !string.IsNullOrWhiteSpace(provider));
            string? actualIdentityProvider = lookupKeys
                .Select(key => identityProviderByKey.TryGetValue(key, out var provider) ? provider : null)
                .FirstOrDefault(provider => !string.IsNullOrWhiteSpace(provider));
            string? actualProvider = ProviderForExpectationComparison(
                exp.MediaType,
                expectedProvider,
                actualRetailProvider,
                actualIdentityProvider);

            var matchedActual = false;
            (string? WikidataQid, string? CuratorState, string? IdentityJobState, string? ReviewTrigger) actual = default;
            foreach (var key in lookupKeys)
            {
                if (index.TryGetValue(key, out actual))
                {
                    matchedActual = true;
                    break;
                }
            }

            if (!matchedActual)
            {
                // No matching Work row found
                var item = new ReconciliationItemResult
                {
                    Title = exp.Title,
                    MediaType = exp.MediaType,
                    Expected = expectedDesc,
                    Actual = "NotFound",
                    ExpectedQid = exp.ExpectedQid,
                    ExpectedProvider = expectedProvider,
                    ExpectedTrigger = exp.ExpectedReviewTrigger,
                    Classification = "NotFound",
                    Reason = "No Work row found in database after ingestion",
                };
                summary.Items.Add(item);
                summary.Mismatches.Add(item);
                summary.ByClassification["NotFound"]++;
                logger.LogWarning("[Reconciliation] NotFound: '{Title}' ({Type})", exp.Title, exp.MediaType);
                continue;
            }

            bool hasQid = !string.IsNullOrWhiteSpace(actual.WikidataQid) &&
                                 !actual.WikidataQid!.StartsWith("NF", StringComparison.OrdinalIgnoreCase);
            bool hasReview = !string.IsNullOrWhiteSpace(actual.ReviewTrigger);
            bool hasIdentifiedState = (IsIdentifiedStatus(actual.CuratorState)
                                       || IsIdentifiedStatus(actual.IdentityJobState))
                                      && !hasReview;
            bool hasIdentifiedOutcome = hasQid || hasIdentifiedState;
            string actualTrigger = actual.ReviewTrigger ?? "";

            string actualDesc = hasQid ? "Identified"
                              : hasReview ? $"InReview ({actualTrigger})"
                              : hasIdentifiedState ? $"Identified without QID ({actual.IdentityJobState ?? actual.CuratorState})"
                              : "Unresolved";

            if (hasIdentifiedOutcome)
            {
                summary.ActualResolved++;
            }

            if (hasReview)
            {
                summary.ActualReview++;
            }

            if (!string.IsNullOrWhiteSpace(exp.ExpectedQid)
                && !string.IsNullOrWhiteSpace(actual.WikidataQid)
                && string.Equals(exp.ExpectedQid, actual.WikidataQid, StringComparison.OrdinalIgnoreCase))
            {
                summary.ActualExactQid++;
            }

            string classification;
            if (exp.ExpectIdentified)
            {
                bool requiresExactQid = !string.IsNullOrWhiteSpace(exp.ExpectedQid);
                classification = requiresExactQid
                    ? hasQid ? "Match" : "UnexpectedReview"
                    : hasIdentifiedOutcome ? "Match" : "UnexpectedReview";
            }
            else
            {
                if (hasReview)
                {
                    bool triggerMatches = string.IsNullOrWhiteSpace(exp.ExpectedReviewTrigger) ||
                        actualTrigger.Equals(exp.ExpectedReviewTrigger, StringComparison.OrdinalIgnoreCase);
                    classification = triggerMatches ? "Match" : "WrongTrigger";
                }
                else if (hasQid)
                {
                    classification = "UnexpectedIdentified";
                }
                else
                {
                    // Unresolved but expected InReview — treat as WrongTrigger
                    classification = "WrongTrigger";
                    actualDesc = "Unresolved (no QID, no review entry)";
                }
            }

            // ── Layered strictness checks ────────────────────────────────────
            // Only run if the base classification was Match — a mismatched
            // review/identified state is a bigger problem than a QID/cover drift.
            if (classification == "Match" && exp.ExpectIdentified)
            {
                // WrongQid: seed declares a specific expected QID, actual differs.
                if (!string.IsNullOrWhiteSpace(exp.ExpectedQid) &&
                    !string.IsNullOrWhiteSpace(actual.WikidataQid) &&
                    !string.Equals(exp.ExpectedQid, actual.WikidataQid, StringComparison.OrdinalIgnoreCase))
                {
                    classification = "WrongQid";
                    actualDesc = $"Identified as {actual.WikidataQid} (expected {exp.ExpectedQid})";
                }
                // MissingCoverArt: seed declares ExpectedCoverArt=true, but the
                // central managed asset store has no preferred cover for this title.
                // Only applies to identified items
                // (we never expect cover art on placeholder/review-queue entries).
                else if (exp.ExpectedCoverArt)
                {
                    bool coverKnown = false;
                    bool hasCover = false;
                    foreach (var key in lookupKeys)
                    {
                        if (!coverArtByKey.TryGetValue(key, out var candidateHasCover))
                        {
                            continue;
                        }

                        coverKnown = true;
                        hasCover |= candidateHasCover;
                    }

                    if (coverKnown && !hasCover)
                    {
                        classification = "MissingCoverArt";
                        actualDesc = $"Identified{(actual.WikidataQid is { Length: > 0 } q ? $" as {q}" : "")} but no central stored artwork was persisted";
                    }
                }

                if (classification == "Match")
                {
                    if (!string.IsNullOrWhiteSpace(expectedProvider)
                        && !string.IsNullOrWhiteSpace(actualProvider)
                        && !ProviderMatchesExpectation(exp.MediaType, expectedProvider, actualRetailProvider, actualIdentityProvider))
                    {
                        classification = "WrongProvider";
                        var providerRole = UsesIdentityProviderExpectation(exp.MediaType, expectedProvider)
                            ? "identity"
                            : "retail";
                        actualDesc = $"Identified{(actual.WikidataQid is { Length: > 0 } q ? $" as {q}" : "")} via {providerRole} provider {actualProvider} (expected {expectedProvider})";
                    }
                }
            }

            var result = new ReconciliationItemResult
            {
                Title = exp.Title,
                MediaType = exp.MediaType,
                Expected = expectedDesc,
                Actual = actualDesc,
                ExpectedQid = exp.ExpectedQid,
                ActualQid = actual.WikidataQid,
                ExpectedProvider = expectedProvider,
                ActualProvider = actualProvider,
                ExpectedTrigger = exp.ExpectedReviewTrigger,
                ActualTrigger = actualTrigger,
                Classification = classification,
                Reason = exp.ExpectedReason,
            };

            summary.Items.Add(result);
            summary.ByClassification[classification]++;
            if (classification == "Match")
            {
                summary.Matched++;
                logger.LogInformation("[Reconciliation] Match: '{Title}' ({Type}) — {Actual}",
                    exp.Title, exp.MediaType, actualDesc);
            }
            else
            {
                summary.Mismatches.Add(result);
                logger.LogWarning("[Reconciliation] {Class}: '{Title}' ({Type}) — expected={Expected}, actual={Actual}",
                    classification, exp.Title, exp.MediaType, expectedDesc, actualDesc);
            }
        }

        if (summary.ExpectedResolved != summary.ActualResolved
            || summary.ExpectedExactQid != summary.ActualExactQid
            || summary.ExpectedReview != summary.ActualReview)
        {
            report.IssuesFound.Add(
                $"Reconciliation count mismatch: expected {summary.ExpectedResolved} identified ({summary.ExpectedExactQid} exact QID) and {summary.ExpectedReview} review; actual {summary.ActualResolved} identified ({summary.ActualExactQid} exact QID) and {summary.ActualReview} review.");
        }

        report.Reconciliation = summary;

        // Build the structured ReconciliationReport in parallel for JSON consumers.
        // The HTML report still pulls from ReconciliationSummary for backward compat.
        var structured = new ReconciliationReport
        {
            Total = summary.ExpectedTotal,
            Matched = summary.Matched,
        };
        foreach (var item in summary.Items)
        {
            structured.Items.Add(new ReconciliationReportItem(
                FileName: item.Title,
                MediaType: item.MediaType,
                ExpectedStatus: item.Expected,
                ActualStatus: item.Actual,
                ExpectedQid: item.ExpectedQid,
                ActualQid: item.ActualQid,
                ExpectedProvider: item.ExpectedProvider,
                ActualProvider: item.ActualProvider,
                ExpectedTrigger: item.ExpectedTrigger,
                ActualTrigger: item.ActualTrigger,
                Classification: item.Classification,
                Matched: item.Classification == "Match",
                Reason: item.Reason));
        }
        report.ReconciliationReport = structured;

        logger.LogInformation("[Reconciliation] Complete: {Matched}/{Total} matched, {Mismatches} mismatches",
            summary.Matched, summary.ExpectedTotal, summary.Mismatches.Count);
    }

    // ── HTML Report Generator ─────────────────────────────────────────────

    private static async Task ValidateDescriptionSourcesAsync(
        IDatabaseConnection db,
        ILibraryItemRepository libraryItemRepo,
        TestReport report,
        ILogger logger,
        CancellationToken ct)
    {
        report.DescriptionSourceChecks.Clear();

        var allItems = await libraryItemRepo.GetPageAsync(
            new LibraryItemQuery(Offset: 0, Limit: 500, IncludeAll: true), ct);
        var validationItems = allItems.Items.Where(IsOwnedValidationItem).ToList();
        var providerNamesById = await LoadProviderNamesByIdAsync(db);
        var assetIdsByWork = await LoadWorkAssetIdsAsync(db);
        var hierarchy = await LoadWorkHierarchyAsync(db);

        using var conn = db.CreateConnection();
        foreach (var item in validationItems
            .Where(item => IsIdentifiedStatus(item.Status) && ShouldValidateDescriptionSources(item.MediaType))
            .OrderBy(item => item.MediaType)
            .ThenBy(item => item.Title))
        {
            ct.ThrowIfCancellationRequested();

            var descriptionKey = item.MediaType switch
            {
                var type when string.Equals(type, "TV", StringComparison.OrdinalIgnoreCase) => MetadataFieldConstants.EpisodeDescription,
                var type when string.Equals(type, "Comics", StringComparison.OrdinalIgnoreCase) => MetadataFieldConstants.IssueDescription,
                _ => MetadataFieldConstants.Description,
            };

            var entityIds = ResolveEntityScope(item.EntityId, assetIdsByWork, hierarchy);
            var keys = new[]
            {
                descriptionKey,
                MetadataFieldConstants.Description,
                MetadataFieldConstants.Tagline,
                MetadataFieldConstants.ShortDescription,
            }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            var claims = (await conn.QueryAsync<DescriptionClaimRow>("""
                SELECT claim_key AS ClaimKey,
                       provider_id AS ProviderIdBlob
                FROM metadata_claims
                WHERE entity_id IN @entityIds
                  AND claim_key IN @keys
                  AND claim_value IS NOT NULL
                  AND TRIM(claim_value) <> '';
                """, new
            {
                entityIds = entityIds.Select(GuidSql.ToBlob).ToArray(),
                keys,
            })).ToList();

            var canonicals = (await conn.QueryAsync<DescriptionCanonicalRow>("""
                SELECT key AS Key,
                       winning_provider_id AS WinningProviderIdBlob
                FROM canonical_values
                WHERE entity_id IN @entityIds
                  AND key IN @keys
                  AND value IS NOT NULL
                  AND TRIM(value) <> '';
                """, new
            {
                entityIds = entityIds.Select(GuidSql.ToBlob).ToArray(),
                keys,
            })).ToList();

            var targetClaims = claims
                .Where(row => string.Equals(row.ClaimKey, descriptionKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var targetCanonicals = canonicals
                .Where(row => string.Equals(row.Key, descriptionKey, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var hasWikipediaDescription = targetClaims.Any(row => IsWikipediaProvider(row.ProviderId, providerNamesById));
            var hasRetailDescription = targetClaims.Any(row => IsRetailProvider(row.ProviderId, providerNamesById));
            var hasAnyDescription = targetClaims.Count > 0 || targetCanonicals.Count > 0;
            var canonicalWikipedia = targetCanonicals.Any(row => IsWikipediaProvider(row.WinningProviderId, providerNamesById));
            var canonicalProvider = targetCanonicals
                .Select(row => ProviderLabel(row.WinningProviderId, providerNamesById))
                .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label));

            var check = new DescriptionSourceCheckResult
            {
                Title = item.Title,
                MediaType = item.MediaType,
                EntityId = item.EntityId,
                DescriptionKey = descriptionKey,
                RequiresWikipediaDescription = !string.IsNullOrWhiteSpace(item.WikidataQid),
                RequiresRetailDescription = string.Equals(item.RetailMatch, "matched", StringComparison.OrdinalIgnoreCase),
                HasWikipediaDescription = hasWikipediaDescription,
                HasRetailDescription = hasRetailDescription,
                HasAnyDescription = hasAnyDescription,
                AllowsGeneratedFallback = string.Equals(descriptionKey, MetadataFieldConstants.IssueDescription, StringComparison.OrdinalIgnoreCase),
                CanonicalUsesWikipedia = canonicalWikipedia,
                CanonicalProvider = canonicalProvider,
                HasTagline = claims.Any(row => string.Equals(row.ClaimKey, MetadataFieldConstants.Tagline, StringComparison.OrdinalIgnoreCase))
                    || canonicals.Any(row => string.Equals(row.Key, MetadataFieldConstants.Tagline, StringComparison.OrdinalIgnoreCase)),
            };

            if (!check.Pass)
            {
                check.Detail = BuildDescriptionCheckDetail(check);
                report.IssuesFound.Add(
                    $"Description source validation failed for {check.MediaType} '{check.Title}': {check.Detail}");
            }

            report.DescriptionSourceChecks.Add(check);
        }

        logger.LogInformation(
            "  Description source checks: {Pass}/{Total} passed",
            report.DescriptionSourceChecks.Count(check => check.Pass),
            report.DescriptionSourceChecks.Count);
    }

    private static string GenerateHtmlReport(TestReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>Tuvima Integration Test - {report.Timestamp:yyyy-MM-dd HH:mm}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("""
            * { margin: 0; padding: 0; box-sizing: border-box; }
            body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
                   background: #0f0f1a; color: #e0e0e0; padding: 24px; line-height: 1.5; }
            h1 { color: #C9922E; font-size: 24px; margin-bottom: 4px; }
            h2 { color: #C9922E; font-size: 18px; margin: 32px 0 12px; border-bottom: 1px solid rgba(201,146,46,0.3); padding-bottom: 8px; }
            h3 { color: #8B9DC3; font-size: 14px; margin: 16px 0 8px; text-transform: uppercase; letter-spacing: 0.05em; }
            .subtitle { color: rgba(255,255,255,0.5); font-size: 13px; margin-bottom: 24px; }
            .badge { display: inline-block; padding: 2px 10px; border-radius: 12px; font-size: 12px; font-weight: 600; }
            .badge-pass { background: rgba(93,202,165,0.15); color: #5DCAA5; border: 1px solid rgba(93,202,165,0.3); }
            .badge-fail { background: rgba(226,75,74,0.15); color: #E24B4A; border: 1px solid rgba(226,75,74,0.3); }
            .badge-warn { background: rgba(239,159,39,0.15); color: #EF9F27; border: 1px solid rgba(239,159,39,0.3); }
            .badge-info { background: rgba(96,165,250,0.15); color: #60A5FA; border: 1px solid rgba(96,165,250,0.3); }
            .badge-skip { background: rgba(148,163,184,0.15); color: #94A3B8; border: 1px solid rgba(148,163,184,0.3); }
            .summary-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 12px; margin: 16px 0; }
            .summary-card { background: rgba(255,255,255,0.04); border: 1px solid rgba(255,255,255,0.08); border-radius: 8px; padding: 16px; text-align: center; }
            .summary-card .num { font-size: 28px; font-weight: 700; }
            .summary-card .label { font-size: 11px; text-transform: uppercase; letter-spacing: 0.1em; color: rgba(255,255,255,0.5); margin-top: 4px; }
            table { width: 100%; border-collapse: collapse; font-size: 13px; margin: 8px 0 16px; }
            th { text-align: left; padding: 8px 12px; border-bottom: 2px solid rgba(255,255,255,0.1);
                 color: rgba(255,255,255,0.6); font-size: 11px; text-transform: uppercase; letter-spacing: 0.05em; }
            td { padding: 8px 12px; border-bottom: 1px solid rgba(255,255,255,0.04); }
            tr:hover { background: rgba(255,255,255,0.02); }
            .status-identified { color: #5DCAA5; }
            .status-review { color: #EF9F27; }
            .status-failed { color: #E24B4A; }
            .status-unknown { color: rgba(255,255,255,0.4); }
            .issues-list { list-style: none; margin: 8px 0; }
            .issues-list li { padding: 6px 12px; margin: 4px 0; background: rgba(226,75,74,0.08);
                              border-left: 3px solid #E24B4A; border-radius: 4px; font-size: 13px; }
            .section { background: rgba(255,255,255,0.02); border: 1px solid rgba(255,255,255,0.06);
                       border-radius: 8px; padding: 16px; margin: 12px 0; }
            .media-type-header { display: flex; align-items: center; gap: 8px; margin-bottom: 8px; }
            .mt-dot { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }
            .mono { font-family: 'SF Mono', 'Fira Code', monospace; font-size: 12px; }
            footer { margin-top: 48px; padding-top: 16px; border-top: 1px solid rgba(255,255,255,0.06);
                     color: rgba(255,255,255,0.3); font-size: 11px; text-align: center; }
        """);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // Header
        string overallBadge = report.OverallPass
            ? "<span class=\"badge badge-pass\">ALL PASS</span>"
            : "<span class=\"badge badge-fail\">VALIDATION FAILED</span>";
        sb.AppendLine($"<h1>Tuvima Library - Integration Test Report {overallBadge}</h1>");
        string stageBadge = report.StagesLevel switch
        {
            1 => "<span class=\"badge badge-info\">Stage 1</span>",
            12 => "<span class=\"badge badge-info\">Stage 1+2</span>",
            123 => "<span class=\"badge badge-info\">Stage 1+2+3</span>",
            _ => $"<span class=\"badge badge-warn\">Stage {report.StagesLevel}</span>",
        };
        string typesBadge = report.ActiveTypes.Count == AllTestableTypes.Length
            ? "<span class=\"badge badge-pass\">All Types</span>"
            : $"<span class=\"badge badge-info\">{string.Join(", ", report.ActiveTypes.OrderBy(t => t))}</span>";
        sb.AppendLine($"<p class=\"subtitle\">{report.Timestamp:yyyy-MM-dd HH:mm:ss UTC} | Duration: {report.TotalDuration.TotalSeconds:F1}s | Ingestion: {report.IngestionDuration.TotalSeconds:F1}s | {stageBadge} | {typesBadge}</p>");

        // Summary cards
        sb.AppendLine("<div class=\"summary-grid\">");
        SummaryCard(sb, report.TotalFilesSeeded.ToString(), "Files Seeded", "#60A5FA");
        SummaryCard(sb, report.TotalItems.ToString(), "Items Detected", "#8B9DC3");
        SummaryCard(sb, report.TotalIdentified.ToString(), "Identified", "#5DCAA5");
        SummaryCard(sb, report.TotalNeedsReview.ToString(), "Needs Review", "#EF9F27");
        SummaryCard(sb, report.ExpectationPreflight.ExpectedResolved.ToString(), "Expected Identity", "#F472B6");
        SummaryCard(sb, report.TotalFailed.ToString(), "Failed", "#E24B4A");
        SummaryCard(sb, report.ManualSearchResults.Count(s => s.Pass).ToString() + "/" + report.ManualSearchResults.Count, "Search Tests", "#A78BFA");
        SummaryCard(sb, report.LibraryChecks.Count(v => v.Pass).ToString() + "/" + report.LibraryChecks.Count, "Library Checks", "#22D3EE");
        SummaryCard(sb, report.FileSystemChecks.Count(f => f.Pass).ToString() + "/" + report.FileSystemChecks.Count, "Filesystem", "#38BDF8");
        if (report.DescriptionSourceChecks.Count > 0)
        {
            SummaryCard(sb, report.DescriptionSourceChecks.Count(d => d.Pass) + "/" + report.DescriptionSourceChecks.Count, "Descriptions", "#34D399");
        }

        if (report.SeriesHarnessChecks.Count > 0)
        {
            SummaryCard(sb, report.SeriesHarnessChecks.Count(s => s.Pass) + "/" + report.SeriesHarnessChecks.Count, "Series Totals", "#818CF8");
        }
        if (report.MusicAlbumChecks.Count > 0)
        {
            SummaryCard(sb, report.MusicAlbumChecks.Count(s => s.Pass) + "/" + report.MusicAlbumChecks.Count, "Music Albums", "#EC4899");
        }

        if (report.CrossMediaSeriesChecks.Count > 0)
        {
            SummaryCard(sb, report.CrossMediaSeriesChecks.Count(s => s.Pass) + "/" + report.CrossMediaSeriesChecks.Count, "Cross-Media", "#C084FC");
        }

        if (report.PersonCoverageChecks.Count > 0)
        {
            SummaryCard(sb, report.PersonCoverageChecks.Count(s => s.Pass) + "/" + report.PersonCoverageChecks.Count, "People Coverage", "#A78BFA");
        }

        if (report.DetailRouteChecks.Count > 0)
        {
            SummaryCard(sb, report.DetailRouteChecks.Count(s => s.Pass) + "/" + report.DetailRouteChecks.Count, "Detail Links", "#60A5FA");
        }

        if (report.Stage3ArtworkSummaries.Count > 0)
        {
            SummaryCard(sb, report.Stage3ArtworkSummaries.Sum(s => s.WithAnyArtwork) + "/" + report.Stage3ArtworkSummaries.Sum(s => s.EligibleCount), "Stage 3 Art", "#14B8A6");
        }

        if (report.CharacterArtworkChecks.Count > 0)
        {
            SummaryCard(sb, report.CharacterArtworkChecks.Count(c => c.Pass) + "/" + report.CharacterArtworkChecks.Count, "Character Art", "#F59E0B");
        }

        SummaryCard(sb, report.StageGatingResults.Count(g => g.Pass).ToString() + "/" + report.StageGatingResults.Count, "Stage Gating", "#FB923C");
        if (report.IngestionProgressSnapshots.Count > 0)
        {
            var lastProgress = report.IngestionProgressSnapshots[^1];
            SummaryCard(sb, $"{lastProgress.AssetCount}/{lastProgress.ExpectedCount}", "Progress Samples", "#2DD4BF");
        }

        if (report.IngestionWaitStages.Count > 0)
        {
            SummaryCard(sb, report.IngestionWaitStages.Count(s => s.Pass) + "/" + report.IngestionWaitStages.Count, "Wait Budgets", "#2DD4BF");
        }

        if (report.Reconciliation is not null)
        {
            SummaryCard(sb, report.Reconciliation.Matched + "/" + report.Reconciliation.ExpectedTotal, "Reconciliation", "#F472B6");
        }

        if (report.SkippedTypes.Count > 0)
        {
            SummaryCard(sb, report.SkippedTypes.Count.ToString(), "Skipped Types", "#94A3B8");
        }

        sb.AppendLine("</div>");

        if (report.ExpectationPreflight.TotalFiles > 0)
        {
            var expectation = report.ExpectationPreflight;
            sb.AppendLine("<h2>Preflight Expected Outcomes</h2>");
            sb.AppendLine($"<p class=\"subtitle\">{expectation.TotalFiles} fixture(s): {expectation.ExpectedResolved} expected identified outcomes ({expectation.ExpectedExactQid} exact QID, {expectation.ExpectedIdentityOnly} QID optional), {expectation.ExpectedReview} expected review, {expectation.ExpectedKnownNoQid} known no-QID.</p>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Media Type</th><th>Expected QID</th><th>Any QID</th><th>Expected Review</th><th>Known No-QID</th></tr>");
            foreach (var group in expectation.Items.GroupBy(i => i.MediaType).OrderBy(g => g.Key))
            {
                int exact = group.Count(i => i.ExpectedStatus.Equals("ExactQid", StringComparison.OrdinalIgnoreCase));
                int any = group.Count(i => i.ExpectedStatus.Equals("Identified", StringComparison.OrdinalIgnoreCase));
                int review = group.Count(i => i.ExpectedStatus.Equals("NeedsReview", StringComparison.OrdinalIgnoreCase));
                int noQid = group.Count(i => i.ExpectedStatus.Equals("KnownNoWikidataEntity", StringComparison.OrdinalIgnoreCase));
                sb.AppendLine($"<tr><td>{Esc(group.Key)}</td><td>{exact}</td><td>{any}</td><td>{review}</td><td>{noQid}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        // Provider health section
        if (report.ProviderHealth.Count > 0)
        {
            sb.AppendLine("<h2>Provider Health</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Provider</th><th>Status</th><th>Media Types</th><th>Detail</th></tr>");
            foreach (var (provider, healthy) in report.ProviderHealth.OrderBy(p => p.Key))
            {
                string badge = healthy
                    ? "<span class=\"badge badge-pass\">HEALTHY</span>"
                    : "<span class=\"badge badge-fail\">UNAVAILABLE</span>";
                string types = ProviderToTypes.TryGetValue(provider, out var ts) ? string.Join(", ", ts) : "-";
                report.ProviderHealthDetails.TryGetValue(provider, out var detail);
                sb.AppendLine($"<tr><td>{Esc(provider)}</td><td>{badge}</td><td>{Esc(types)}</td><td class=\"mono\">{Esc(detail)}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        // Skipped types section
        if (report.SkippedTypes.Count > 0)
        {
            sb.AppendLine("<h2>Skipped Media Types</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Media Type</th><th>Reason</th></tr>");
            foreach (var (type, reason) in report.SkippedTypes.OrderBy(s => s.Key))
            {
                sb.AppendLine($"<tr><td>{Esc(type)}</td><td><span class=\"badge badge-skip\">{Esc(reason)}</span></td></tr>");
            }

            sb.AppendLine("</table>");
        }

        if (report.IngestionProgressSnapshots.Count > 0)
        {
            sb.AppendLine("<h2>Ingestion Progress Samples</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Time UTC</th><th>Assets</th><th>Resolved / Works</th><th>Pending</th><th>Claims</th><th>Active Jobs</th><th>Progress</th><th>Status</th></tr>");
            foreach (var sample in report.IngestionProgressSnapshots.TakeLast(12))
            {
                string badge = sample.Pass
                    ? "<span class=\"badge badge-pass\">OK</span>"
                    : "<span class=\"badge badge-fail\">BAD COUNT</span>";
                sb.AppendLine($"<tr><td>{sample.CapturedAt:HH:mm:ss}</td><td>{sample.AssetCount}/{sample.ExpectedCount}</td>" +
                    $"<td>{sample.ResolvedCount}/{sample.WorkCount}</td><td>{sample.PendingCount}</td><td>{sample.ClaimCount}</td>" +
                    $"<td>{sample.ActiveJobCount}</td><td>{sample.ProgressPercent:F1}%</td><td>{badge}</td></tr>");
            }

            sb.AppendLine("</table>");
        }

        if (report.IngestionWaitStages.Count > 0)
        {
            sb.AppendLine("<h2>Ingestion Wait Budgets</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Stage</th><th>Budget</th><th>Elapsed</th><th>Status</th><th>Detail</th></tr>");
            foreach (var stage in report.IngestionWaitStages.OrderBy(s => WaitStageSort(s.StageKey)))
            {
                string badge = stage.CompletedAfterTimeout
                    ? "<span class=\"badge badge-warn\">COMPLETED AFTER TIMEOUT</span>"
                    : stage.Completed
                        ? "<span class=\"badge badge-pass\">COMPLETED</span>"
                        : stage.TimedOut
                            ? "<span class=\"badge badge-fail\">TIMED OUT</span>"
                            : "<span class=\"badge badge-info\">OBSERVED</span>";
                sb.AppendLine($"<tr><td>{Esc(stage.Label)}</td><td>{stage.BudgetSeconds:F0}s</td><td>{stage.ElapsedSeconds:F0}s</td><td>{badge}</td><td>{Esc(stage.Detail ?? "")}</td></tr>");
            }

            sb.AppendLine("</table>");
        }

        // Issues section (if any)
        if (report.IssuesFound.Count > 0)
        {
            sb.AppendLine("<h2>Issues Found</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Category</th><th>Count</th><th>Examples</th></tr>");
            foreach (var group in report.IssuesFound.GroupBy(CategorizeIssue).OrderBy(g => IssueCategorySort(g.Key)).ThenBy(g => g.Key))
            {
                var examples = string.Join("; ", group.Take(3));
                sb.AppendLine($"<tr><td>{Esc(group.Key)}</td><td>{group.Count()}</td><td>{Esc(examples)}</td></tr>");
            }

            sb.AppendLine("</table>");
            sb.AppendLine("<ul class=\"issues-list\">");
            foreach (var issue in report.IssuesFound)
            {
                sb.AppendLine($"  <li>{Esc(issue)}</li>");
            }

            sb.AppendLine("</ul>");
        }

        // Per media type results
        sb.AppendLine("<h2>Results by Media Type</h2>");
        var mtColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Books"] = "#5DCAA5",
            ["Audiobooks"] = "#A78BFA",
            ["Movies"] = "#60A5FA",
            ["TV"] = "#FBBF24",
            ["Music"] = "#22D3EE",
            ["Comics"] = "#7C4DFF",
        };

        foreach (var mt in report.MediaTypeResults.OrderByDescending(m => m.Count))
        {
            string color = mtColors.GetValueOrDefault(mt.MediaType, "#888");
            string badge = mt.Count == 0 ? "<span class=\"badge badge-fail\">MISSING</span>"
                : mt.Failed > 0 ? "<span class=\"badge badge-fail\">FAILURES</span>"
                : mt.NeedsReview > 0 ? "<span class=\"badge badge-warn\">REVIEW</span>"
                : "<span class=\"badge badge-pass\">OK</span>";

            sb.AppendLine("<div class=\"section\">");
            sb.AppendLine($"<div class=\"media-type-header\"><span class=\"mt-dot\" style=\"background:{color}\"></span><strong>{Esc(mt.MediaType)}</strong> - {mt.OwnedCount} owned, {mt.CatalogOnlyCount} catalog-only ({mt.Identified} identified, {mt.NeedsReview} review, {mt.Failed} failed) {badge}</div>");

            if (mt.Items.Count > 0)
            {
                sb.AppendLine("<table>");
                sb.AppendLine("<tr><th>Title</th><th>Author</th><th>Year</th><th>Status</th><th>Confidence</th><th>Retail Match</th><th>QID</th><th>File</th></tr>");
                foreach (var item in mt.Items.OrderBy(i => i.Title))
                {
                    string statusClass = StatusClass(item.Status);
                    sb.AppendLine($"<tr><td>{Esc(item.Title)}</td><td>{Esc(item.Author ?? "-")}</td><td>{Esc(item.Year ?? "-")}</td>" +
                        $"<td class=\"{statusClass}\">{Esc(item.Status)}{(item.ReviewTrigger is not null ? $" <span class=\"mono\">({Esc(item.ReviewTrigger)})</span>" : "")}</td>" +
                        $"<td>{item.Confidence:P0}</td><td>{Esc(item.RetailMatch ?? "none")}</td>" +
                        $"<td class=\"mono\">{Esc(item.WikidataQid ?? "-")}</td><td class=\"mono\" style=\"max-width:200px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap\">{Esc(item.FileName)}</td></tr>");
                }
                sb.AppendLine("</table>");
            }
            sb.AppendLine("</div>");
        }

        // Skipped media types as greyed-out sections
        foreach (var (type, reason) in report.SkippedTypes.OrderBy(s => s.Key))
        {
            // Don't double-render if it already appeared in results
            if (report.MediaTypeResults.Any(r => r.MediaType.Equals(type, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            sb.AppendLine("<div class=\"section\" style=\"opacity: 0.5;\">");
            sb.AppendLine($"<div class=\"media-type-header\"><span class=\"mt-dot\" style=\"background:#94A3B8\"></span><strong>{Esc(type)}</strong> <span class=\"badge badge-skip\">SKIPPED - {Esc(reason)}</span></div>");
            sb.AppendLine("</div>");
        }

        // Manual search results
        sb.AppendLine("<h2>Manual Search Tests</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Query</th><th>Provider</th><th>Media Type</th><th>Results</th><th>Top Result</th><th>Confidence</th><th>Status</th></tr>");
        foreach (var s in report.ManualSearchResults)
        {
            string badge = s.Pass ? "<span class=\"badge badge-pass\">PASS</span>" : $"<span class=\"badge badge-fail\">FAIL: {Esc(s.Error ?? "unknown")}</span>";
            sb.AppendLine($"<tr><td>{Esc(s.Query)}</td><td>{Esc(s.ProviderName)}</td><td>{Esc(s.MediaType)}</td>" +
                $"<td>{s.ResultCount}</td><td>{Esc(s.TopResultTitle ?? "-")}</td><td>{s.TopResultConfidence:P0}</td><td>{badge}</td></tr>");
        }
        sb.AppendLine("</table>");

        // Universe enrichment results
        sb.AppendLine("<h2>Universe Enrichment</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Universe</th><th>Works Found</th><th>QID Present</th><th>Sample QID</th><th>Status</th></tr>");
        foreach (var u in report.UniverseResults)
        {
            string badge = u.Found ? "<span class=\"badge badge-pass\">QID RESOLVED</span>" : "<span class=\"badge badge-warn\">NO QID YET</span>";
            sb.AppendLine($"<tr><td>{Esc(u.Name)}</td><td>{u.WorkCount}</td><td>{u.Found}</td><td class=\"mono\">{Esc(u.WikidataQid ?? "-")}</td><td>{badge}</td></tr>");
        }
        sb.AppendLine("</table>");

        if (report.SeriesHarnessChecks.Count > 0)
        {
            int seriesPass = report.SeriesHarnessChecks.Count(s => s.Pass);
            string seriesBadge = seriesPass == report.SeriesHarnessChecks.Count
                ? "<span class=\"badge badge-pass\">ALL PASS</span>"
                : $"<span class=\"badge badge-warn\">{report.SeriesHarnessChecks.Count - seriesPass} ISSUES</span>";
            sb.AppendLine($"<h2>Series Totals Validation {seriesBadge}</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Series</th><th>Media Type</th><th>Owned</th><th>Known Total</th><th>Manifest</th><th>Status</th><th>Detail</th></tr>");
            foreach (var check in report.SeriesHarnessChecks.OrderBy(s => s.MediaType).ThenBy(s => s.Series))
            {
                string badge = check.Pass
                    ? "<span class=\"badge badge-pass\">PASS</span>"
                    : "<span class=\"badge badge-fail\">FAIL</span>";
                sb.AppendLine($"<tr><td>{Esc(check.Series)}</td><td>{Esc(check.MediaType)}</td><td>{check.OwnedCount}</td>" +
                    $"<td>{check.KnownCount}</td><td>{BoolMark(check.HasManifest)}</td><td>{badge}</td><td>{Esc(check.Detail)}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        if (report.CrossMediaSeriesChecks.Count > 0)
        {
            int crossMediaPass = report.CrossMediaSeriesChecks.Count(s => s.Pass);
            string crossMediaBadge = crossMediaPass == report.CrossMediaSeriesChecks.Count
                ? "<span class=\"badge badge-pass\">ALL PASS</span>"
                : $"<span class=\"badge badge-warn\">{report.CrossMediaSeriesChecks.Count - crossMediaPass} ISSUES</span>";
            sb.AppendLine($"<h2>Cross-Media Series Validation {crossMediaBadge}</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Name</th><th>Required Media</th><th>Found Media</th><th>Owned</th><th>Shared Parent</th><th>Shared Narrative QID</th><th>Status</th><th>Detail</th></tr>");
            foreach (var check in report.CrossMediaSeriesChecks.OrderBy(s => s.Name))
            {
                string badge = check.Pass
                    ? "<span class=\"badge badge-pass\">PASS</span>"
                    : "<span class=\"badge badge-fail\">FAIL</span>";
                sb.AppendLine($"<tr><td>{Esc(check.Name)}</td><td>{Esc(string.Join(", ", check.RequiredMediaTypes))}</td>" +
                    $"<td>{Esc(string.Join(", ", check.FoundMediaTypes.DefaultIfEmpty("none")))}</td><td>{check.OwnedCount}</td>" +
                    $"<td>{BoolMark(check.HasSharedParentCollection)}</td><td>{BoolMark(check.HasSharedNarrativeRoot)}</td>" +
                    $"<td>{badge}</td><td>{Esc(check.Detail)}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        if (report.PersonCoverageChecks.Count > 0)
        {
            int peoplePass = report.PersonCoverageChecks.Count(check => check.Pass);
            string peopleBadge = peoplePass == report.PersonCoverageChecks.Count
                ? "<span class=\"badge badge-pass\">ALL PASS</span>"
                : $"<span class=\"badge badge-warn\">{report.PersonCoverageChecks.Count - peoplePass} ISSUES</span>";
            sb.AppendLine($"<h2>Repeat and Cross-Media People Validation {peopleBadge}</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Person</th><th>Canonical Record</th><th>Requirements</th><th>Status</th></tr>");
            foreach (var check in report.PersonCoverageChecks.OrderBy(check => check.Name))
            {
                string badge = check.Pass
                    ? "<span class=\"badge badge-pass\">PASS</span>"
                    : "<span class=\"badge badge-fail\">FAIL</span>";
                sb.AppendLine($"<tr><td>{Esc(check.Name)}</td><td>{BoolMark(check.PersonFound)}</td>" +
                    $"<td>{Esc(check.Detail)}</td><td>{badge}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        if (report.DetailRouteChecks.Count > 0)
        {
            int detailRoutePass = report.DetailRouteChecks.Count(check => check.Pass);
            string detailRouteBadge = detailRoutePass == report.DetailRouteChecks.Count
                ? "<span class=\"badge badge-pass\">ALL PASS</span>"
                : $"<span class=\"badge badge-warn\">{report.DetailRouteChecks.Count - detailRoutePass} ISSUES</span>";
            sb.AppendLine($"<h2>Outward Detail Link Validation {detailRouteBadge}</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Surface</th><th>Title</th><th>Canonical Route</th><th>Resolved</th><th>Shared Tabs</th><th>No Collection Credits</th><th>Status</th></tr>");
            foreach (var check in report.DetailRouteChecks.OrderBy(check => check.Surface).ThenBy(check => check.Title))
            {
                string badge = check.Pass
                    ? "<span class=\"badge badge-pass\">PASS</span>"
                    : "<span class=\"badge badge-fail\">FAIL</span>";
                sb.AppendLine($"<tr><td>{Esc(check.Surface)}</td><td>{Esc(check.Title)}</td><td class=\"mono\">{Esc(check.Route)}</td>" +
                    $"<td>{BoolMark(check.Resolved)}</td><td>{BoolMark(check.UsesExpectedTabs)}</td>" +
                    $"<td>{BoolMark(check.SuppressesCollectionCredits)}</td><td>{badge}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        if (report.MusicAlbumChecks.Count > 0)
        {
            int musicAlbumPass = report.MusicAlbumChecks.Count(check => check.Pass);
            string musicAlbumBadge = musicAlbumPass == report.MusicAlbumChecks.Count
                ? "<span class=\"badge badge-pass\">ALL PASS</span>"
                : $"<span class=\"badge badge-warn\">{report.MusicAlbumChecks.Count - musicAlbumPass} ISSUES</span>";
            sb.AppendLine($"<h2>Music Album Validation {musicAlbumBadge}</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Artist</th><th>Album</th><th>Owned</th><th>Known Tracks</th><th>Manifest</th><th>Cover</th><th>Status</th><th>Detail</th></tr>");
            foreach (var check in report.MusicAlbumChecks.OrderBy(check => check.Artist).ThenBy(check => check.Album))
            {
                string badge = check.Pass
                    ? "<span class=\"badge badge-pass\">PASS</span>"
                    : "<span class=\"badge badge-fail\">FAIL</span>";
                sb.AppendLine($"<tr><td>{Esc(check.Artist)}</td><td>{Esc(check.Album)}</td><td>{check.OwnedTrackCount}</td>" +
                    $"<td>{Esc(check.KnownTrackCount?.ToString() ?? "-")}</td><td>{BoolMark(check.HasManifest)}</td>" +
                    $"<td>{BoolMark(check.HasCover)}</td><td>{badge}</td><td>{Esc(check.Detail)}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        // Library Display Validation
        if (report.LibraryChecks.Count > 0)
        {
            int libraryPass = report.LibraryChecks.Count(v => v.Pass);
            string libraryBadge = libraryPass == report.LibraryChecks.Count
                ? "<span class=\"badge badge-pass\">ALL PASS</span>"
                : $"<span class=\"badge badge-warn\">{report.LibraryChecks.Count - libraryPass} ISSUES</span>";
            sb.AppendLine($"<h2>Library Display Validation {libraryBadge}</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Title</th><th>Media Type</th><th>Cover Art</th><th>Title</th><th>Creator</th><th>Status</th><th>Retail Match</th><th>Retail Provider</th><th>Identity Provider</th><th>Enrichment Providers</th><th>Bridge Provider</th><th>Bridge ID</th><th>Artwork</th><th>QID</th></tr>");
            foreach (var v in report.LibraryChecks.OrderBy(v => v.MediaType).ThenBy(v => v.Title))
            {
                string Check(bool ok) => ok ? "<span style=\"color:#5DCAA5\">&#x2713;</span>" : "<span style=\"color:#E24B4A\">&#x2717;</span>";
                sb.AppendLine($"<tr><td>{Esc(v.Title)}</td><td>{Esc(v.MediaType)}</td>" +
                    $"<td>{Check(v.HasCoverArt)}</td><td>{Check(v.HasTitle)}</td><td>{Check(v.HasCreator)}</td>" +
                    $"<td>{Check(v.HasStatus)} <span class=\"mono\">{Esc(v.Status ?? "")}</span></td>" +
                    $"<td>{Check(v.HasRetailMatch)} <span class=\"mono\">{Esc(v.RetailMatch ?? "")}</span></td>" +
                    $"<td>{Check(v.HasExpectedRetailProvider)} <span class=\"mono\">{Esc(v.ActualRetailProvider ?? "-")}</span></td>" +
                    $"<td class=\"mono\">{Esc(v.IdentityProvider ?? "-")}</td>" +
                    $"<td class=\"mono\">{Esc(v.EnrichmentProviders ?? "-")}</td>" +
                    $"<td class=\"mono\">{Esc(v.BridgeProvider ?? "-")}</td>" +
                    $"<td class=\"mono\">{Esc(v.BridgeIdType ?? "-")}</td>" +
                    $"<td class=\"mono\">{Esc(v.ArtworkCompleteness ?? "-")}</td>" +
                    $"<td>{Check(v.HasWikidataQid)} <span class=\"mono\">{Esc(v.WikidataQid ?? "")}</span></td></tr>");
            }
            sb.AppendLine("</table>");
        }

        if (report.DescriptionSourceChecks.Count > 0)
        {
            int descriptionPass = report.DescriptionSourceChecks.Count(d => d.Pass);
            string descriptionBadge = descriptionPass == report.DescriptionSourceChecks.Count
                ? "<span class=\"badge badge-pass\">ALL PASS</span>"
                : $"<span class=\"badge badge-warn\">{report.DescriptionSourceChecks.Count - descriptionPass} ISSUES</span>";
            sb.AppendLine($"<h2>Description Source Validation {descriptionBadge}</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Title</th><th>Media Type</th><th>Key</th><th>Wikipedia</th><th>Retail</th><th>Canonical</th><th>Tagline</th><th>Status</th><th>Detail</th></tr>");
            foreach (var check in report.DescriptionSourceChecks.OrderBy(d => d.MediaType).ThenBy(d => d.Title))
            {
                string badge = check.Pass
                    ? "<span class=\"badge badge-pass\">PASS</span>"
                    : "<span class=\"badge badge-fail\">FAIL</span>";
                sb.AppendLine($"<tr><td>{Esc(check.Title)}</td><td>{Esc(check.MediaType)}</td><td class=\"mono\">{Esc(check.DescriptionKey)}</td>" +
                    $"<td>{BoolMark(check.HasWikipediaDescription)}</td><td>{BoolMark(check.HasRetailDescription)}</td>" +
                    $"<td>{BoolMark(check.CanonicalUsesWikipedia)} <span class=\"mono\">{Esc(check.CanonicalProvider ?? "unknown")}</span></td>" +
                    $"<td>{BoolMark(check.HasTagline)}</td><td>{badge}</td><td>{Esc(check.Detail ?? "")}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        // Filesystem Validation
        if (report.FileSystemChecks.Count > 0)
        {
            int fsPass = report.FileSystemChecks.Count(f => f.Pass);
            string fsBadge = fsPass == report.FileSystemChecks.Count
                ? "<span class=\"badge badge-pass\">ALL PASS</span>"
                : $"<span class=\"badge badge-warn\">{report.FileSystemChecks.Count - fsPass} ISSUES</span>";
            sb.AppendLine($"<h2>Filesystem Validation {fsBadge}</h2>");

            if (report.WatchFolderChecks.Count > 0)
            {
                sb.AppendLine("<table>");
                sb.AppendLine("<tr><th>Watch Folder</th><th>Unexpected Media Files</th><th>Ignored Expected Staging</th><th>Status</th></tr>");
                foreach (var check in report.WatchFolderChecks.OrderBy(c => c.Directory))
                {
                    string status = check.Pass
                        ? "<span class=\"badge badge-pass\">EMPTY</span>"
                        : "<span class=\"badge badge-fail\">REMAINING FILES</span>";
                    sb.AppendLine($"<tr><td class=\"mono\">{Esc(check.Directory)}</td><td>{check.RemainingMediaFiles}</td><td>{check.IgnoredExpectedStagingFiles}</td><td>{status}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            var failingChecks = report.FileSystemChecks.Where(f => !f.Pass).OrderBy(f => f.MediaType).ThenBy(f => f.Title).ToList();
            if (failingChecks.Count > 0)
            {
                sb.AppendLine("<table>");
                sb.AppendLine("<tr><th>Title</th><th>Media Type</th><th>Status</th><th>Expected</th><th>Actual</th><th>Sidecars</th><th>Stored Art</th><th>Detail</th></tr>");
                foreach (var check in failingChecks)
                {
                    string sidecars = check.RequiresSidecarArtwork
                        ? $"{BoolMark(check.HasPoster)}/{BoolMark(check.HasPosterThumb)}/{BoolMark(!check.HasLegacyHeroSidecar)}"
                        : $"n/a/{BoolMark(!check.HasLegacyHeroSidecar)}";
                    string stored = check.RequiresStoredArtwork
                        ? $"{BoolMark(check.HasStoredCover)}/{BoolMark(check.HasStoredCoverSmall)}/{BoolMark(check.HasStoredCoverMedium)}/{BoolMark(check.HasStoredCoverLarge)}/{BoolMark(check.HasStoredPalette)}/{BoolMark(!check.HasStoredLegacyHero)}"
                        : $"n/a/{BoolMark(!check.HasStoredLegacyHero)}";

                    sb.AppendLine($"<tr><td>{Esc(check.Title)}</td><td>{Esc(check.MediaType)}</td><td>{Esc(check.Status)}</td>" +
                        $"<td class=\"mono\">{Esc(check.ExpectedLocation)}{(!string.IsNullOrWhiteSpace(check.ExpectedRelativePath) ? "<br>" + Esc(check.ExpectedRelativePath) : "")}</td>" +
                        $"<td class=\"mono\">{Esc(check.ActualDisplayPath ?? check.ActualFilePath ?? "-")}</td>" +
                        $"<td class=\"mono\">{Esc(sidecars)}</td><td class=\"mono\">{Esc(stored)}</td><td>{Esc(check.Detail)}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            sb.AppendLine($"<details{(failingChecks.Count == 0 ? " open" : "")}><summary style=\"cursor:pointer;color:#8B9DC3;font-weight:600\">All filesystem checks ({report.FileSystemChecks.Count})</summary>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Title</th><th>Media Type</th><th>Result</th><th>Expected</th><th>Actual</th><th>Template</th><th>Sidecars (P/T/NoHero)</th><th>Stored Core (O/S/M/L/Pal/NoHero)</th><th>Optional Stored Art (BG/LO/BA/SP/ST/EP)</th></tr>");
            foreach (var check in report.FileSystemChecks.OrderBy(f => f.MediaType).ThenBy(f => f.Title))
            {
                string result = check.Pass
                    ? "<span class=\"badge badge-pass\">PASS</span>"
                    : "<span class=\"badge badge-fail\">FAIL</span>";
                string optionalArt =
                    $"{BoolMark(check.HasStoredBackground)}/{BoolMark(check.HasStoredLogo)}/{BoolMark(check.HasStoredBanner)}/" +
                    $"{BoolMark(check.HasStoredSeasonPoster)}/{BoolMark(check.HasStoredSeasonThumb)}/{BoolMark(check.HasStoredEpisodeStill)}";
                string sidecars = check.RequiresSidecarArtwork
                    ? $"{BoolMark(check.HasPoster)}/{BoolMark(check.HasPosterThumb)}/{BoolMark(!check.HasLegacyHeroSidecar)}"
                    : $"n/a/{BoolMark(!check.HasLegacyHeroSidecar)}";
                string storedCore = check.RequiresStoredArtwork
                    ? $"{BoolMark(check.HasStoredCover)}/{BoolMark(check.HasStoredCoverSmall)}/{BoolMark(check.HasStoredCoverMedium)}/{BoolMark(check.HasStoredCoverLarge)}/{BoolMark(check.HasStoredPalette)}/{BoolMark(!check.HasStoredLegacyHero)}"
                    : $"n/a/{BoolMark(!check.HasStoredLegacyHero)}";
                string templateState = check.RequiresTemplateMatch
                    ? BoolMark(check.PathMatchesTemplate)
                    : "n/a";

                sb.AppendLine($"<tr><td>{Esc(check.Title)}</td><td>{Esc(check.MediaType)}</td><td>{result}</td>" +
                    $"<td class=\"mono\">{Esc(check.ExpectedLocation)}{(!string.IsNullOrWhiteSpace(check.ExpectedRelativePath) ? "<br>" + Esc(check.ExpectedRelativePath) : "")}</td>" +
                    $"<td class=\"mono\">{Esc(check.ActualDisplayPath ?? check.ActualFilePath ?? "-")}</td>" +
                    $"<td class=\"mono\">{Esc(templateState)}</td><td class=\"mono\">{Esc(sidecars)}</td><td class=\"mono\">{Esc(storedCore)}</td><td class=\"mono\">{Esc(optionalArt)}</td></tr>");
            }
            sb.AppendLine("</table>");
            sb.AppendLine("</details>");
        }

        if (report.Stage3ArtworkSummaries.Count > 0)
        {
            int artworkPass = report.Stage3ArtworkSummaries.Count(s => s.Pass);
            string artworkBadge = artworkPass == report.Stage3ArtworkSummaries.Count
                ? "<span class=\"badge badge-pass\">ALL PASS</span>"
                : $"<span class=\"badge badge-warn\">{report.Stage3ArtworkSummaries.Count - artworkPass} ISSUES</span>";
            sb.AppendLine($"<h2>Stage 3 Artwork Validation {artworkBadge}</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Media Type</th><th>TMDB Artwork Eligible</th><th>Any Artwork</th><th>Backgrounds</th><th>Logos</th><th>Banners</th><th>Season Posters</th><th>Season Thumbs</th><th>Episode Stills</th><th>Status</th></tr>");
            foreach (var summary in report.Stage3ArtworkSummaries.OrderBy(s => s.MediaType))
            {
                string badge = summary.Pass
                    ? "<span class=\"badge badge-pass\">PASS</span>"
                    : "<span class=\"badge badge-fail\">FAIL</span>";
                sb.AppendLine($"<tr><td>{Esc(summary.MediaType)}</td><td>{summary.EligibleCount}</td><td>{summary.WithAnyArtwork}</td><td>{summary.WithBackground}</td><td>{summary.WithLogo}</td><td>{summary.WithBanner}</td><td>{summary.WithSeasonPoster}</td><td>{summary.WithSeasonThumb}</td><td>{summary.WithEpisodeStill}</td><td>{badge}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        if (report.CharacterArtworkChecks.Count > 0)
        {
            int characterArtPass = report.CharacterArtworkChecks.Count(c => c.Pass);
            string characterArtBadge = characterArtPass == report.CharacterArtworkChecks.Count
                ? "<span class=\"badge badge-pass\">ALL PASS</span>"
                : $"<span class=\"badge badge-warn\">{report.CharacterArtworkChecks.Count - characterArtPass} ISSUES</span>";
            sb.AppendLine($"<h2>Character Display Validation {characterArtBadge}</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Movie</th><th>Actor</th><th>Character</th><th>Movie Link</th><th>Portrait Row</th><th>Source</th><th>Unverified</th><th>Displayed Name</th><th>Displayed Image</th><th>Top Cast</th><th>Portrait Path</th><th>Status</th><th>Detail</th></tr>");
            foreach (var check in report.CharacterArtworkChecks.OrderBy(c => c.Title).ThenBy(c => c.ActorName))
            {
                string badge = check.Pass
                    ? "<span class=\"badge badge-pass\">PASS</span>"
                    : "<span class=\"badge badge-fail\">FAIL</span>";
                sb.AppendLine($"<tr><td>{Esc(check.Title)} <span class=\"mono\">{Esc(check.WorkQid)}</span></td>" +
                    $"<td>{Esc(check.ActorName)}</td><td>{Esc(check.CharacterName)}</td>" +
                    $"<td>{BoolMark(check.HasMovieScopedActorCharacterLink)}</td><td>{BoolMark(check.HasPortraitRow)}</td>" +
                    $"<td class=\"mono\">{Esc(check.PortraitSourceProvider ?? "none")}</td><td>{BoolMark(check.HasUnverifiedPortrait)}</td><td>{BoolMark(check.HasDisplayCharacterName)}</td>" +
                    $"<td>{BoolMark(check.HasDisplayCharacterImage)}</td><td>{BoolMark(check.IsInPrimaryCastPreview)} <span class=\"mono\">{Esc(check.CastPosition?.ToString() ?? "n/a")}</span></td><td class=\"mono\">{Esc(check.PortraitLocalImagePath ?? check.PortraitImageUrl ?? check.DisplayImageUrl ?? "none")}</td>" +
                    $"<td>{badge}</td><td>{Esc(check.Detail ?? "")}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        // Stage Gating Validation
        if (report.StageGatingResults.Count > 0)
        {
            int gatePass = report.StageGatingResults.Count(r => r.Pass);
            string gateBadge = gatePass == report.StageGatingResults.Count
                ? "<span class=\"badge badge-pass\">ALL PASS</span>"
                : $"<span class=\"badge badge-warn\">{report.StageGatingResults.Count - gatePass} ISSUES</span>";
            sb.AppendLine($"<h2>Stage Gating Validation {gateBadge}</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th>Title</th><th>Check</th><th>Result</th><th>Detail</th></tr>");
            foreach (var g in report.StageGatingResults.OrderBy(g => g.Title))
            {
                string resultBadge = g.Pass
                    ? "<span class=\"badge badge-pass\">PASS</span>"
                    : "<span class=\"badge badge-fail\">FAIL</span>";
                sb.AppendLine($"<tr><td>{Esc(g.Title)}</td><td>{Esc(g.Check)}</td><td>{resultBadge}</td><td class=\"mono\">{Esc(g.Detail ?? "")}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        // Reconciliation section
        if (report.Reconciliation is not null)
        {
            var recon = report.Reconciliation;
            int mismatches = recon.Mismatches.Count;
            string reconBadge = mismatches == 0
                ? "<span class=\"badge badge-pass\">ALL MATCH</span>"
                : $"<span class=\"badge badge-fail\">{mismatches} MISMATCH{(mismatches == 1 ? "" : "ES")}</span>";
            sb.AppendLine($"<h2>Reconciliation - Seed Expectations vs. Pipeline Outcomes {reconBadge}</h2>");
            sb.AppendLine($"<p class=\"subtitle\">{recon.Matched}/{recon.ExpectedTotal} seed fixtures matched their expected outcome | ");
            sb.AppendLine(string.Join(" | ", recon.ByClassification
                .Where(kv => kv.Value > 0)
                .Select(kv => $"{Esc(kv.Key)}: {kv.Value}")));
            sb.AppendLine("</p>");

            // ── Matched section (collapsed by default) ──────────────────────
            var matched = recon.ExpectedTotal - mismatches;
            sb.AppendLine($"<details><summary style=\"cursor:pointer;color:#5DCAA5;font-weight:600\">&#x2713; Matched Expected ({matched})</summary>");
            // Reconstruct matched items by re-running the DB data (we only stored mismatches)
            // Instead enumerate expectations again to build the full set, but since we only have
            // mismatches stored, derive matched count via total - mismatches. Show a simple
            // summary table for mismatches and a collapsed table for everything else.
            sb.AppendLine("<p style=\"color:rgba(255,255,255,0.4);font-size:12px;padding:8px 0\">" +
                $"{matched} item(s) produced the outcome declared in their seed fixture.</p>");
            sb.AppendLine("</details>");

            // ── Mismatch sections by classification ─────────────────────────
            var classOrder = new[] { "UnexpectedReview", "UnexpectedIdentified", "WrongTrigger", "WrongQid", "WrongProvider", "MissingCoverArt", "NotFound" };
            var classLabels = new Dictionary<string, string>
            {
                ["UnexpectedReview"] = "Unexpected Review (expected Identified, got InReview)",
                ["UnexpectedIdentified"] = "Unexpected Identified (expected InReview, got Identified)",
                ["WrongTrigger"] = "Wrong Trigger (in review, but wrong trigger)",
                ["WrongQid"] = "Wrong Wikidata QID (resolved, but to the wrong entity)",
                ["WrongProvider"] = "Wrong Retail Provider (matched, but from an unexpected provider)",
                ["MissingCoverArt"] = "Missing Cover Art (identified, but no cover downloaded)",
                ["NotFound"] = "Not Found (no Work row in database)",
            };
            var classColors = new Dictionary<string, string>
            {
                ["UnexpectedReview"] = "#E24B4A",
                ["UnexpectedIdentified"] = "#E24B4A",
                ["WrongTrigger"] = "#EF9F27",
                ["WrongQid"] = "#E24B4A",
                ["WrongProvider"] = "#EF9F27",
                ["MissingCoverArt"] = "#EF9F27",
                ["NotFound"] = "#94A3B8",
            };

            foreach (var cls in classOrder)
            {
                var items = recon.Mismatches.Where(m => m.Classification == cls).ToList();
                if (items.Count == 0)
                {
                    continue;
                }

                string color = classColors.GetValueOrDefault(cls, "#888");
                sb.AppendLine($"<details open><summary style=\"cursor:pointer;color:{color};font-weight:600\">&#x2717; {Esc(classLabels[cls])} ({items.Count})</summary>");
                sb.AppendLine("<table>");
                sb.AppendLine("<tr><th>Title</th><th>Media Type</th><th>Expected</th><th>Actual</th><th>Reason</th></tr>");
                foreach (var item in items.OrderBy(i => i.MediaType).ThenBy(i => i.Title))
                {
                    sb.AppendLine($"<tr>" +
                        $"<td>{Esc(item.Title)}</td>" +
                        $"<td>{Esc(item.MediaType)}</td>" +
                        $"<td class=\"mono\">{Esc(item.Expected)}</td>" +
                        $"<td class=\"mono\" style=\"color:{color}\">{Esc(item.Actual)}</td>" +
                        $"<td style=\"color:rgba(255,255,255,0.5);font-size:12px\">{Esc(item.Reason ?? "-")}</td>" +
                        $"</tr>");
                }
                sb.AppendLine("</table>");
                sb.AppendLine("</details>");
            }
        }

        // Footer
        sb.AppendLine($"<footer>Generated by Tuvima Library Integration Test Harness | {report.Timestamp:yyyy-MM-dd HH:mm:ss}</footer>");
        sb.AppendLine("</body></html>");

        return sb.ToString();
    }

    // ── HTML helpers ──────────────────────────────────────────────────────

    private static void SummaryCard(StringBuilder sb, string num, string label, string color)
    {
        sb.AppendLine($"<div class=\"summary-card\"><div class=\"num\" style=\"color:{color}\">{num}</div><div class=\"label\">{label}</div></div>");
    }

    private static string Esc(string? s) =>
        System.Net.WebUtility.HtmlEncode(s ?? "");

    private static string BoolMark(bool value) => value ? "Y" : "N";

    private static IEnumerable<string> BuildHarnessTitleMediaKeys(string? title, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(mediaType))
        {
            yield break;
        }

        var mediaTypeKey = mediaType.Trim().ToLowerInvariant();
        var exactTitle = TextEncodingRepair.RepairMojibake(title).Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(exactTitle))
        {
            yield return $"{exactTitle}|{mediaTypeKey}";
        }

        var normalizedTitle = NormalizeHarnessTitleKey(title);
        if (!string.IsNullOrWhiteSpace(normalizedTitle)
            && !string.Equals(normalizedTitle, exactTitle, StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{normalizedTitle}|{mediaTypeKey}";
        }
    }

    private static string NormalizeHarnessTitleKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var repaired = TextEncodingRepair.RepairMojibake(value).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(repaired.Length);
        var pendingSpace = false;

        foreach (var ch in repaired)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(char.ToLowerInvariant(ch));
                pendingSpace = false;
                continue;
            }

            pendingSpace = sb.Length > 0;
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string CategorizeIssue(string issue)
    {
        if (issue.StartsWith("Library:", StringComparison.OrdinalIgnoreCase)
            || issue.StartsWith("Stage gating:", StringComparison.OrdinalIgnoreCase)
            || issue.StartsWith("Reconciliation", StringComparison.OrdinalIgnoreCase)
            || issue.StartsWith("Description source", StringComparison.OrdinalIgnoreCase)
            || issue.StartsWith("Character", StringComparison.OrdinalIgnoreCase)
            || issue.StartsWith("Series totals:", StringComparison.OrdinalIgnoreCase)
            || issue.StartsWith("Cross-media", StringComparison.OrdinalIgnoreCase)
            || issue.StartsWith("File system validation", StringComparison.OrdinalIgnoreCase))
        {
            return "Product data failure";
        }

        if (issue.Contains("expected", StringComparison.OrdinalIgnoreCase)
            || issue.Contains("expectation", StringComparison.OrdinalIgnoreCase)
            || issue.Contains("NotFound", StringComparison.OrdinalIgnoreCase))
        {
            return "Harness expectation drift";
        }

        if (issue.Contains("provider", StringComparison.OrdinalIgnoreCase)
            || issue.Contains("manual search", StringComparison.OrdinalIgnoreCase)
            || issue.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || issue.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return "Provider/runtime transient";
        }

        if (issue.Contains("progress", StringComparison.OrdinalIgnoreCase)
            || issue.Contains("ingestion", StringComparison.OrdinalIgnoreCase)
            || issue.Contains("dashboard", StringComparison.OrdinalIgnoreCase)
            || issue.Contains("operations", StringComparison.OrdinalIgnoreCase))
        {
            return "Dashboard/API tracking";
        }

        return "Harness/runtime";
    }

    private static int IssueCategorySort(string category) => category switch
    {
        "Product data failure" => 0,
        "Harness expectation drift" => 1,
        "Provider/runtime transient" => 2,
        "Dashboard/API tracking" => 3,
        _ => 4,
    };

    private static string StatusClass(string? status)
    {
        if (IsReviewStatus(status))
        {
            return "status-review";
        }

        if (IsFailureStatus(status))
        {
            return "status-failed";
        }

        if (IsIdentifiedStatus(status))
        {
            return "status-identified";
        }

        return "status-unknown";
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async Task<int> CountActiveIdentityJobsAsync(
        IDatabaseConnection db,
        int stages,
        CancellationToken ct)
    {
        if (stages >= 123)
        {
            ct.ThrowIfCancellationRequested();
            using var stage3Conn = db.CreateConnection();
            return await stage3Conn.ExecuteScalarAsync<int>(IngestionSettleQueries.ActiveJobsThroughStage3);
        }

        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(IngestionSettleQueries.ActiveJobsThroughStage2);
    }

    private static async Task<Dictionary<Guid, Guid>> LoadWorkAssetIdsAsync(IDatabaseConnection db)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<WorkAssetIdRow>("""
            SELECT w.id AS WorkId, MIN(ma.id) AS AssetId
            FROM works w
            INNER JOIN editions e ON e.work_id = w.id
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            GROUP BY w.id
            """);

        var result = new Dictionary<Guid, Guid>();
        foreach (var row in rows)
        {
            if (TryReadCurrentGuid(row.WorkId, out var workId)
                && TryReadCurrentGuid(row.AssetId, out var assetId))
            {
                result[workId] = assetId;
            }
        }

        return result;
    }

    private static async Task<Dictionary<string, Guid>> LoadAssetIdsByPathAsync(IDatabaseConnection db)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<AssetPathRow>("""
            SELECT id             AS AssetId,
                   file_path_root AS FilePath
            FROM media_assets
            WHERE file_path_root IS NOT NULL
              AND TRIM(file_path_root) <> '';
            """);

        return rows
            .Where(r => TryReadCurrentGuid(r.AssetId, out _)
                && !string.IsNullOrWhiteSpace(r.FilePath))
            .GroupBy(r => NormalizeComparablePath(r.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => GuidSql.FromDb(g.OrderBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase).First().AssetId),
                StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<Guid, OptionalArtworkState>> LoadOptionalArtworkStatesAsync(IDatabaseConnection db)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<OptionalArtworkRow>("""
            SELECT entity_id        AS EntityId,
                   asset_type       AS AssetType,
                   local_image_path AS LocalImagePath
            FROM entity_assets
            WHERE entity_type = 'Work'
              AND asset_type IN ('Background', 'Logo', 'Banner', 'SeasonPoster', 'SeasonThumb', 'EpisodeStill')
              AND local_image_path IS NOT NULL
              AND TRIM(local_image_path) <> '';
            """);

        var result = new Dictionary<Guid, OptionalArtworkState>();
        foreach (var row in rows)
        {
            if (!TryReadCurrentGuid(row.EntityId, out var workId))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.LocalImagePath) || !File.Exists(row.LocalImagePath))
            {
                continue;
            }

            if (!result.TryGetValue(workId, out var state))
            {
                state = new OptionalArtworkState();
                result[workId] = state;
            }

            switch (row.AssetType)
            {
                case "Background":
                    state.HasBackground = true;
                    break;
                case "Logo":
                    state.HasLogo = true;
                    break;
                case "Banner":
                    state.HasBanner = true;
                    break;
                case "SeasonPoster":
                    state.HasSeasonPoster = true;
                    break;
                case "SeasonThumb":
                    state.HasSeasonThumb = true;
                    break;
                case "EpisodeStill":
                    state.HasEpisodeStill = true;
                    break;
            }
        }

        return result;
    }

    private static async Task<Dictionary<Guid, WorkHierarchyNode>> LoadWorkHierarchyAsync(IDatabaseConnection db)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<WorkHierarchyRow>("""
            SELECT id             AS Id,
                   parent_work_id AS ParentWorkId
            FROM works;
            """);

        var result = new Dictionary<Guid, WorkHierarchyNode>();
        foreach (var row in rows)
        {
            if (!TryReadCurrentGuid(row.Id, out var id))
            {
                continue;
            }

            result[id] = new WorkHierarchyNode(
                id,
                TryReadCurrentGuid(row.ParentWorkId, out var parentWorkId) ? parentWorkId : null);
        }

        return result;
    }

    private static OptionalArtworkState ResolveOptionalArtworkState(
        Guid workId,
        IReadOnlyDictionary<Guid, WorkHierarchyNode> hierarchy,
        IReadOnlyDictionary<Guid, OptionalArtworkState> states)
    {
        var merged = new OptionalArtworkState();
        var visited = new HashSet<Guid>();
        Guid? current = workId;

        while (current.HasValue && visited.Add(current.Value))
        {
            if (states.TryGetValue(current.Value, out var state))
            {
                merged.Merge(state);
            }

            current = hierarchy.TryGetValue(current.Value, out var node)
                ? node.ParentWorkId
                : null;
        }

        return merged;
    }

    private static async Task<Dictionary<Guid, Dictionary<string, string>>> LoadCanonicalValueMapsAsync(
        IDatabaseConnection db,
        IEnumerable<Guid> entityIds)
    {
        var ids = entityIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<CanonicalValueRow>("""
            SELECT entity_id AS EntityId,
                   key       AS Key,
                   value     AS Value
            FROM canonical_values
            WHERE entity_id IN @entityIds
              AND value IS NOT NULL
              AND TRIM(value) <> '';
            """, new
        {
            entityIds = ids.Select(GuidSql.ToBlob).ToArray(),
        });

        var maps = new Dictionary<Guid, Dictionary<string, string>>();
        foreach (var row in rows)
        {
            if (!TryReadCurrentGuid(row.EntityId, out var entityId))
            {
                continue;
            }

            if (!maps.TryGetValue(entityId, out var map))
            {
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                maps[entityId] = map;
            }

            map[row.Key] = row.Value;
        }

        return maps;
    }

    private static async Task<Dictionary<Guid, PreferredArtworkRecord>> LoadPreferredArtworkRecordsAsync(
        IDatabaseConnection db,
        string assetType)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<PreferredArtworkRow>("""
            SELECT entity_id        AS EntityId,
                   local_image_path AS LocalImagePath,
                   local_image_path_s AS LocalImagePathSmall,
                   local_image_path_m AS LocalImagePathMedium,
                   local_image_path_l AS LocalImagePathLarge,
                   primary_hex AS PrimaryHex,
                   secondary_hex AS SecondaryHex,
                   accent_hex AS AccentHex
            FROM entity_assets
            WHERE asset_type = @assetType
              AND is_preferred = 1
              AND local_image_path IS NOT NULL
              AND TRIM(local_image_path) <> '';
            """, new { assetType });

        var result = new Dictionary<Guid, PreferredArtworkRecord>();
        foreach (var row in rows)
        {
            if (!TryReadCurrentGuid(row.EntityId, out var entityId))
            {
                continue;
            }

            result[entityId] = new PreferredArtworkRecord(
                row.LocalImagePath,
                row.LocalImagePathSmall,
                row.LocalImagePathMedium,
                row.LocalImagePathLarge,
                row.PrimaryHex,
                row.SecondaryHex,
                row.AccentHex);
        }

        return result;
    }

    private static Guid ResolveArtworkOwnerEntityId(
        Guid workId,
        string? mediaType,
        IReadOnlyDictionary<Guid, WorkHierarchyNode> hierarchy)
    {
        if (string.Equals(mediaType, "Books", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "Comics", StringComparison.OrdinalIgnoreCase))
        {
            return workId;
        }

        var current = workId;
        var visited = new HashSet<Guid>();

        while (visited.Add(current)
               && hierarchy.TryGetValue(current, out var node)
               && node.ParentWorkId.HasValue)
        {
            current = node.ParentWorkId.Value;
        }

        return current;
    }

    private static bool HasCentralPreferredArtwork(
        Guid ownerEntityId,
        IReadOnlyDictionary<Guid, PreferredArtworkRecord> preferredArtworkRecords,
        AssetPathService assetPathService,
        string ownerKind,
        string assetType)
    {
        if (!preferredArtworkRecords.TryGetValue(ownerEntityId, out var artwork)
            || string.IsNullOrWhiteSpace(artwork.LocalImagePath)
            || !File.Exists(artwork.LocalImagePath))
        {
            return false;
        }

        var artworkDirectory = GetCentralArtworkDirectory(assetPathService, ownerKind, ownerEntityId, assetType);
        return artwork.LocalImagePath.StartsWith(artworkDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCentralArtworkRendition(
        Guid ownerEntityId,
        IReadOnlyDictionary<Guid, PreferredArtworkRecord> preferredArtworkRecords,
        AssetPathService assetPathService,
        string size)
    {
        if (!preferredArtworkRecords.TryGetValue(ownerEntityId, out var artwork))
        {
            return false;
        }

        var renditionPath = size switch
        {
            "s" => artwork.LocalImagePathSmall,
            "m" => artwork.LocalImagePathMedium,
            "l" => artwork.LocalImagePathLarge,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(renditionPath) || !File.Exists(renditionPath))
        {
            return false;
        }

        return renditionPath.StartsWith(assetPathService.DerivedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasArtworkPalette(
        IReadOnlyDictionary<Guid, PreferredArtworkRecord> preferredArtworkRecords,
        Guid ownerEntityId)
    {
        if (!preferredArtworkRecords.TryGetValue(ownerEntityId, out var artwork))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(artwork.PrimaryHex)
               && !string.IsNullOrWhiteSpace(artwork.SecondaryHex)
               && !string.IsNullOrWhiteSpace(artwork.AccentHex);
    }

    private static bool ShouldRequireSidecarArtwork(LibraryStoragePolicy storagePolicy) =>
        storagePolicy.ArtworkExport || storagePolicy.ExportProfile.Artwork;

    private static string GetCentralArtworkDirectory(
        AssetPathService assetPathService,
        string ownerKind,
        Guid ownerEntityId,
        string assetType)
    {
        var samplePath = assetPathService.GetCentralAssetPath(ownerKind, ownerEntityId, assetType, Guid.NewGuid(), ".jpg");
        return Path.GetDirectoryName(samplePath) ?? string.Empty;
    }

    private static string NormalizeExpectationKey(string title, string mediaType) =>
        $"{NormalizeHarnessTitleKey(title)}|{mediaType.Trim().ToLowerInvariant()}";

    private static Dictionary<string, DevSeedEndpoints.SeedExpectation> BuildExpectationIndex()
    {
        var index = new Dictionary<string, DevSeedEndpoints.SeedExpectation>(StringComparer.OrdinalIgnoreCase);
        foreach (var expectation in DevSeedEndpoints.GetAllExpectations())
        {
            foreach (var title in new[] { expectation.Title, expectation.ReconciliationTitle }
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                index.TryAdd(NormalizeExpectationKey(title!, expectation.MediaType), expectation);
            }
        }

        return index;
    }

    private static bool HasComicIssueTitleDrift(LibraryCatalogItem item, LibraryItemDetail? detail)
    {
        if (!item.MediaType.Equals("Comics", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var series = FirstCanonicalValue(detail, "series");
        var issue = FirstCanonicalValue(detail, "series_position", "issue_number", "issue");
        var issueTitle = FirstCanonicalValue(detail, MetadataFieldConstants.IssueTitle);
        var title = detail?.Title ?? item.Title;
        if (string.IsNullOrWhiteSpace(series)
            || string.IsNullOrWhiteSpace(issue)
            || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (ContainsNormalized(title, issue))
        {
            return false;
        }

        var issueTitleEqualsSeries = !string.IsNullOrWhiteSpace(issueTitle)
            && ContainsNormalized(issueTitle, series)
            && ContainsNormalized(series, issueTitle);

        if (!string.IsNullOrWhiteSpace(issueTitle)
            && ContainsNormalized(title, issueTitle)
            && !issueTitleEqualsSeries)
        {
            return false;
        }

        return ContainsNormalized(title, series);
    }

    private static string? FirstCanonicalValue(LibraryItemDetail? detail, params string[] keys)
    {
        if (detail is null)
        {
            return null;
        }

        var values = new List<string?>();
        if (keys.Contains("series", StringComparer.OrdinalIgnoreCase))
        {
            values.Add(detail.Series);
        }

        if (keys.Contains("series_position", StringComparer.OrdinalIgnoreCase)
            || keys.Contains("issue_number", StringComparer.OrdinalIgnoreCase)
            || keys.Contains("issue", StringComparer.OrdinalIgnoreCase))
        {
            values.Add(detail.SeriesPosition);
        }

        values.AddRange(detail.CanonicalValues
            .Where(c => keys.Contains(c.Key, StringComparer.OrdinalIgnoreCase))
            .Select(c => c.Value));

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? FirstClaimValue(LibraryItemDetail? detail, params string[] keys)
    {
        if (detail is null)
        {
            return null;
        }

        return detail.ClaimHistory
            .Where(claim => keys.Contains(claim.ClaimKey, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(claim => claim.Confidence)
            .ThenByDescending(claim => claim.ClaimedAt)
            .Select(claim => claim.ClaimValue)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }


    private static bool ContainsNormalized(string text, string fragment)
    {
        static string Normalize(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
            }

            return builder.ToString();
        }

        var normalizedText = Normalize(text);
        var normalizedFragment = Normalize(fragment);
        return normalizedFragment.Length > 0
            && normalizedText.Contains(normalizedFragment, StringComparison.Ordinal);
    }

    private sealed class DescriptionClaimRow
    {
        public string ClaimKey { get; set; } = "";
        public byte[]? ProviderIdBlob { get; set; }
        public string? ProviderId => ProviderIdBlob is { Length: > 0 }
            ? GuidSql.FromDb(ProviderIdBlob).ToString("D")
            : null;
    }

    private sealed class DescriptionCanonicalRow
    {
        public string Key { get; set; } = "";
        public byte[]? WinningProviderIdBlob { get; set; }
        public string? WinningProviderId => WinningProviderIdBlob is { Length: > 0 }
            ? GuidSql.FromDb(WinningProviderIdBlob).ToString("D")
            : null;
    }

    private static IReadOnlyList<Guid> ResolveEntityScope(
        Guid workId,
        IReadOnlyDictionary<Guid, Guid> assetIdsByWork,
        IReadOnlyDictionary<Guid, WorkHierarchyNode> hierarchy)
    {
        var ids = new List<Guid> { workId };
        if (assetIdsByWork.TryGetValue(workId, out var assetId))
        {
            ids.Add(assetId);
        }

        var visited = new HashSet<Guid> { workId };
        Guid? current = workId;
        while (current.HasValue
            && hierarchy.TryGetValue(current.Value, out var node)
            && node.ParentWorkId is { } parentId
            && visited.Add(parentId))
        {
            ids.Add(parentId);
            current = parentId;
        }

        return ids.Distinct().ToList();
    }

    private static bool IsIdentifiedStatus(string? status)
    {
        var value = status ?? "";
        return value.Contains("Identified", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Confirmed", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Registered", StringComparison.OrdinalIgnoreCase)
            || value.Equals("RetailMatched", StringComparison.OrdinalIgnoreCase)
            || value.Equals("BridgeSearching", StringComparison.OrdinalIgnoreCase)
            || value.Equals("QidResolved", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Hydrating", StringComparison.OrdinalIgnoreCase)
            || value.Equals("UniverseEnriching", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Ready", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ReadyWithoutUniverse", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOwnedValidationItem(LibraryCatalogItem item) =>
        !string.IsNullOrWhiteSpace(item.FilePath)
        || item.IsReadyForLibrary
        || string.Equals(item.LibraryVisibility, "review_only", StringComparison.OrdinalIgnoreCase);

    private static bool IsWikipediaProvider(
        string? providerId,
        IReadOnlyDictionary<Guid, string> providerNamesById)
    {
        if (!Guid.TryParse(providerId, out var id))
        {
            return false;
        }

        if (id == WellKnownProviders.Wikidata || id == WellKnownProviders.Wikipedia)
        {
            return true;
        }

        return providerNamesById.TryGetValue(id, out var name)
            && (name.Contains("wikidata", StringComparison.OrdinalIgnoreCase)
                || name.Contains("wikipedia", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRetailProvider(
        string? providerId,
        IReadOnlyDictionary<Guid, string> providerNamesById)
    {
        if (!Guid.TryParse(providerId, out var id))
        {
            return false;
        }

        if (id == WellKnownProviders.AppleApi
            || id == WellKnownProviders.OpenLibrary
            || id == WellKnownProviders.Tmdb
            || id == WellKnownProviders.ComicVine
            || id == WellKnownProviders.MusicBrainz)
        {
            return true;
        }

        return providerNamesById.TryGetValue(id, out var name)
            && !IsNonRetailProvider(name)
            && (name.Contains("apple", StringComparison.OrdinalIgnoreCase)
                || name.Contains("open_library", StringComparison.OrdinalIgnoreCase)
                || name.Contains("tmdb", StringComparison.OrdinalIgnoreCase)
                || name.Contains("comic", StringComparison.OrdinalIgnoreCase)
                || name.Contains("musicbrainz", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ProviderLabel(
        string? providerId,
        IReadOnlyDictionary<Guid, string> providerNamesById)
    {
        if (!Guid.TryParse(providerId, out var id))
        {
            return null;
        }

        return providerNamesById.TryGetValue(id, out var name)
            ? name
            : id.ToString();
    }

    private static string BuildDescriptionCheckDetail(DescriptionSourceCheckResult check)
    {
        var missing = new List<string>();
        if (!check.HasAnyDescription && !check.AllowsGeneratedFallback)
        {
            missing.Add($"{check.DescriptionKey} missing");
        }

        if (check.HasWikipediaDescription && !check.CanonicalUsesWikipedia)
        {
            missing.Add($"canonical winner is {check.CanonicalProvider ?? "unknown"}, expected Wikipedia/Wikidata");
        }

        return missing.Count == 0 ? "unknown description source issue" : string.Join("; ", missing);
    }

    private static bool ShouldValidateDescriptionSources(string? mediaType)
    {
        // Music tracks do not need per-song prose. If we add music description
        // validation later, it should target album-level metadata only.
        return !string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadCurrentGuid(object? value, out Guid guid)
    {
        guid = default;
        if (value is null or DBNull)
        {
            return false;
        }

        try
        {
            guid = GuidSql.FromDb(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }

    private sealed class ParentCollectionRow
    {
        public object Id { get; set; } = Array.Empty<byte>();
        public string DisplayName { get; set; } = "";
        public string? WikidataQid { get; set; }
    }

    private sealed class WorkAssetIdRow
    {
        public object WorkId { get; set; } = Array.Empty<byte>();
        public object AssetId { get; set; } = Array.Empty<byte>();
    }

    private sealed class AssetPathRow
    {
        public object AssetId { get; set; } = Array.Empty<byte>();
        public string FilePath { get; set; } = "";
    }

    private sealed class OptionalArtworkRow
    {
        public object EntityId { get; set; } = Array.Empty<byte>();
        public string AssetType { get; set; } = "";
        public string LocalImagePath { get; set; } = "";
    }

    private sealed class WorkHierarchyRow
    {
        public object Id { get; set; } = Array.Empty<byte>();
        public object? ParentWorkId { get; set; }
    }

    private sealed class CanonicalValueRow
    {
        public object EntityId { get; set; } = Array.Empty<byte>();
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private sealed class PreferredArtworkRow
    {
        public object EntityId { get; set; } = Array.Empty<byte>();
        public string LocalImagePath { get; set; } = "";
        public string? LocalImagePathSmall { get; set; }
        public string? LocalImagePathMedium { get; set; }
        public string? LocalImagePathLarge { get; set; }
        public string? PrimaryHex { get; set; }
        public string? SecondaryHex { get; set; }
        public string? AccentHex { get; set; }
    }

    private sealed class ProviderNameRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    private static async Task<IReadOnlyDictionary<Guid, string>> LoadProviderNamesByIdAsync(IDatabaseConnection db)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<ProviderNameRow>(
            "SELECT id AS Id, name AS Name FROM metadata_providers");

        var providerNames = new Dictionary<Guid, string>();
        foreach (var row in rows)
        {
            var normalized = NormalizeProviderName(row.Name);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            providerNames[row.Id] = normalized;
        }

        return providerNames;
    }

    private static string? GetExpectedRetailProvider(DevSeedEndpoints.SeedExpectation? expected, string mediaType)
    {
        var provider = expected?.ExpectedProvider ?? InferExpectedRetailProvider(mediaType);
        return NormalizeProviderName(provider);
    }

    private static bool ProviderMatchesExpectation(
        string mediaType,
        string? expectedProvider,
        string? actualRetailProvider,
        string? actualIdentityProvider)
    {
        if (string.IsNullOrWhiteSpace(expectedProvider))
        {
            return true;
        }

        var actualProvider = ProviderForExpectationComparison(
            mediaType,
            expectedProvider,
            actualRetailProvider,
            actualIdentityProvider);

        return string.Equals(expectedProvider, actualProvider, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ProviderForExpectationComparison(
        string mediaType,
        string? expectedProvider,
        string? actualRetailProvider,
        string? actualIdentityProvider)
        => UsesIdentityProviderExpectation(mediaType, expectedProvider) && !string.IsNullOrWhiteSpace(actualIdentityProvider)
            ? actualIdentityProvider
            : actualRetailProvider;

    private static bool UsesIdentityProviderExpectation(string mediaType, string? expectedProvider)
        => string.Equals(NormalizeHarnessMediaTypeKey(mediaType), "music", StringComparison.OrdinalIgnoreCase)
           && string.Equals(expectedProvider, "musicbrainz", StringComparison.OrdinalIgnoreCase);

    private static string BuildProviderExpectationIssue(
        string title,
        string mediaType,
        string? expectedProvider,
        string? actualRetailProvider,
        string? actualIdentityProvider)
    {
        if (UsesIdentityProviderExpectation(mediaType, expectedProvider))
        {
            return $"Library: '{title}' identity provider '{actualIdentityProvider ?? "unknown"}' did not match expected '{expectedProvider}'";
        }

        return $"Library: '{title}' retail provider '{actualRetailProvider ?? "unknown"}' did not match expected '{expectedProvider}'";
    }

    private static string? InferExpectedRetailProvider(string mediaType) =>
        mediaType.Trim().ToLowerInvariant() switch
        {
            "audiobooks" => "apple_api",
            "music" => "musicbrainz",
            "movies" or "tv" => "tmdb",
            "comics" => "comicvine",
            _ => null,
        };

    private static string? ResolveRetailProvider(
        LibraryItemDetail detail,
        IReadOnlyDictionary<Guid, string> providerNamesById)
    {
        var provider = detail.ClaimHistory
            .Where(c => c.ProviderId != Guid.Empty
                        && providerNamesById.TryGetValue(c.ProviderId, out _)
                        && IsRetailProviderClaim(c.ClaimKey))
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.ClaimedAt)
            .Select(c => providerNamesById[c.ProviderId])
            .FirstOrDefault(p => !IsNonRetailProvider(p));

        if (!string.IsNullOrWhiteSpace(provider))
        {
            return provider;
        }

        return NormalizeProviderName(detail.MatchSource);
    }

    private static string? ResolveIdentityProvider(
        LibraryItemDetail? detail,
        string mediaType,
        IReadOnlyDictionary<Guid, string> providerNamesById)
    {
        var explicitProvider = detail?.CanonicalValues
            .FirstOrDefault(value => string.Equals(
                value.Key,
                MetadataFieldConstants.IdentityProvider,
                StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return !string.IsNullOrWhiteSpace(explicitProvider)
            ? NormalizeProviderName(explicitProvider)
            : ResolveCanonicalProvider(detail, providerNamesById, IdentityProviderKeys(mediaType));
    }

    private static string? ResolveEnrichmentProviders(
        LibraryItemDetail? detail,
        string mediaType,
        IReadOnlyDictionary<Guid, string> providerNamesById)
    {
        if (detail is null)
        {
            return null;
        }

        var providers = detail.CanonicalValues
            .Where(value => EnrichmentProviderKeys(mediaType).Contains(value.Key, StringComparer.OrdinalIgnoreCase))
            .Select(value => ProviderLabel(value.WinningProviderId, providerNamesById))
            .Where(provider => !string.IsNullOrWhiteSpace(provider))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(provider => provider)
            .ToList();

        return providers.Count == 0 ? null : string.Join(", ", providers);
    }

    private static string? ResolveBridgeProvider(
        LibraryItemDetail? detail,
        IReadOnlyDictionary<Guid, string> providerNamesById)
        => ResolveCanonicalProvider(detail, providerNamesById, BridgeIdKeys.WikidataQid);

    private static string? ResolveBridgeIdType(LibraryItemDetail? detail, string mediaType)
    {
        if (detail is null)
        {
            return null;
        }

        return BridgeIdKeysForMediaType(mediaType)
            .FirstOrDefault(key => detail.CanonicalValues.Any(value =>
                string.Equals(value.Key, key, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value.Value)));
    }

    private static string? ResolveCanonicalProvider(
        LibraryItemDetail? detail,
        IReadOnlyDictionary<Guid, string> providerNamesById,
        params string[] keys)
    {
        if (detail is null)
        {
            return null;
        }

        return detail.CanonicalValues
            .Where(value => keys.Contains(value.Key, StringComparer.OrdinalIgnoreCase))
            .Select(value => ProviderLabel(value.WinningProviderId, providerNamesById))
            .FirstOrDefault(provider => !string.IsNullOrWhiteSpace(provider));
    }

    private static string[] IdentityProviderKeys(string mediaType) =>
        mediaType.Trim().ToLowerInvariant() switch
        {
            "music" =>
            [
                MetadataFieldConstants.Title,
                "artist",
                "album_artist",
                MetadataFieldConstants.Album,
                "musicbrainz_recording_id",
                "musicbrainz_release_id",
                "musicbrainz_release_group_id",
                "musicbrainz_id",
                "isrc",
            ],
            "movies" or "tv" =>
            [
                MetadataFieldConstants.Title,
                MetadataFieldConstants.ShowName,
                MetadataFieldConstants.EpisodeTitle,
                "tmdb_id",
                "tmdb_movie_id",
                "tmdb_tv_id",
            ],
            "comics" =>
            [
                MetadataFieldConstants.Title,
                MetadataFieldConstants.IssueTitle,
                MetadataFieldConstants.Series,
                "comicvine_issue_id",
            ],
            _ =>
            [
                MetadataFieldConstants.Title,
                MetadataFieldConstants.Author,
                BridgeIdKeys.Isbn,
                BridgeIdKeys.Asin,
            ],
        };

    private static string[] EnrichmentProviderKeys(string mediaType) =>
        mediaType.Trim().ToLowerInvariant() switch
        {
            "music" =>
            [
                MetadataFieldConstants.CoverUrl,
                "cover",
                "genre",
                "year",
                "apple_music_id",
                "apple_music_collection_id",
                "apple_artist_id",
            ],
            _ =>
            [
                MetadataFieldConstants.CoverUrl,
                "cover",
                MetadataFieldConstants.Description,
                "genre",
                "year",
                "rating",
            ],
        };

    private static string[] BridgeIdKeysForMediaType(string mediaType) =>
        mediaType.Trim().ToLowerInvariant() switch
        {
            "music" =>
            [
                "musicbrainz_recording_id",
                "musicbrainz_release_id",
                "musicbrainz_release_group_id",
                "musicbrainz_id",
                "isrc",
                "apple_music_id",
                "apple_music_collection_id",
            ],
            "movies" or "tv" => ["tmdb_id", "tmdb_movie_id", "tmdb_tv_id"],
            "comics" => ["comicvine_issue_id", "comicvine_series_id"],
            _ => [BridgeIdKeys.Isbn, BridgeIdKeys.Asin, "open_library_id"],
        };

    private static bool IsRetailProviderClaim(string claimKey) =>
        claimKey is MetadataFieldConstants.Title
            or MetadataFieldConstants.Album
            or MetadataFieldConstants.ShowName
            or MetadataFieldConstants.EpisodeTitle
            or MetadataFieldConstants.Series;

    private static bool IsNonRetailProvider(string? provider) =>
        string.IsNullOrWhiteSpace(provider)
        || provider.StartsWith("local", StringComparison.OrdinalIgnoreCase)
        || provider.Contains("wikidata", StringComparison.OrdinalIgnoreCase)
        || provider.Contains("libraryscan", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeProviderName(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return null;
        }

        Span<char> buffer = stackalloc char[providerName.Length];
        int index = 0;
        bool previousUnderscore = false;

        foreach (var ch in providerName.Trim().ToLowerInvariant())
        {
            var normalized = char.IsLetterOrDigit(ch) ? ch : '_';
            if (normalized == '_')
            {
                if (previousUnderscore)
                {
                    continue;
                }

                previousUnderscore = true;
            }
            else
            {
                previousUnderscore = false;
            }

            buffer[index++] = normalized;
        }

        var result = new string(buffer[..index]).Trim('_');
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static int CountChildEntities(string? childEntitiesJson)
    {
        if (string.IsNullOrWhiteSpace(childEntitiesJson))
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(childEntitiesJson);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => doc.RootElement.GetArrayLength(),
                JsonValueKind.Object when doc.RootElement.TryGetProperty("items", out var items)
                    && items.ValueKind == JsonValueKind.Array => items.GetArrayLength(),
                JsonValueKind.Object when doc.RootElement.TryGetProperty("tracks", out var tracks)
                    && tracks.ValueKind == JsonValueKind.Array => tracks.GetArrayLength(),
                JsonValueKind.Object when doc.RootElement.TryGetProperty("episodes", out var episodes)
                    && episodes.ValueKind == JsonValueKind.Array => episodes.GetArrayLength(),
                JsonValueKind.Object when doc.RootElement.TryGetProperty("issues", out var issues)
                    && issues.ValueKind == JsonValueKind.Array => issues.GetArrayLength(),
                _ => 0,
            };
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsReviewStatus(string? status) =>
        (status ?? "").Contains("review", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailureStatus(string? status)
    {
        var value = status ?? "";
        return value.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || value.Equals("RetailNoMatch", StringComparison.OrdinalIgnoreCase)
            || value.Contains("quarantine", StringComparison.OrdinalIgnoreCase)
            || value.Contains("reject", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderRoot(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToDisplayPath(string path, IngestionOptions options)
    {
        if (IsUnderRoot(path, options.LibraryRoot))
        {
            return $"library/{Path.GetRelativePath(options.LibraryRoot, path).Replace('\\', '/')}";
        }

        if (IsUnderRoot(path, options.StagingPath))
        {
            return $"staging/{Path.GetRelativePath(options.StagingPath, path).Replace('\\', '/')}";
        }

        return path;
    }

    private static string? BuildExpectedLibraryPath(
        LibraryCatalogItem item,
        string? filePath,
        IReadOnlyDictionary<string, string> metadata,
        IngestionOptions options,
        FileOrganizer organizer)
    {
        if (string.IsNullOrWhiteSpace(options.LibraryRoot) || string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        MediaType? mediaType = Enum.TryParse<MediaType>(item.MediaType, ignoreCase: true, out var parsed)
            ? parsed
            : null;

        var candidate = new IngestionCandidate
        {
            Path = filePath,
            EventType = FileEventType.Created,
            DetectedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase),
            DetectedMediaType = mediaType,
        };

        string template = options.ResolveTemplate(mediaType?.ToString() ?? item.MediaType);
        string relative = organizer.CalculatePath(candidate, template).Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(options.LibraryRoot, relative);
    }

    private static void ValidateStage3ArtworkAsync(TestReport report, ILogger logger)
    {
        report.Stage3ArtworkSummaries.Clear();

        foreach (var mediaType in new[] { "Movies", "TV" })
        {
            var eligible = report.FileSystemChecks
                .Where(check =>
                    string.Equals(check.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(check.ExpectedLocation, "Library", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(check.WikidataQid)
                    && check.HasArtworkBridgeId
                    && check.FileExists
                    && check.LocationMatchesExpectation)
                .ToList();

            if (eligible.Count == 0)
            {
                continue;
            }

            var summary = new Stage3ArtworkSummary
            {
                MediaType = mediaType,
                EligibleCount = eligible.Count,
                WithAnyArtwork = eligible.Count(check =>
                    check.HasStoredBackground
                    || check.HasStoredLogo
                    || check.HasStoredBanner
                    || check.HasStoredSeasonPoster
                    || check.HasStoredSeasonThumb
                    || check.HasStoredEpisodeStill),
                WithBackground = eligible.Count(check => check.HasStoredBackground),
                WithLogo = eligible.Count(check => check.HasStoredLogo),
                WithBanner = eligible.Count(check => check.HasStoredBanner),
                WithSeasonPoster = eligible.Count(check => check.HasStoredSeasonPoster),
                WithSeasonThumb = eligible.Count(check => check.HasStoredSeasonThumb),
                WithEpisodeStill = eligible.Count(check => check.HasStoredEpisodeStill),
            };

            report.Stage3ArtworkSummaries.Add(summary);
            logger.LogInformation(
                "  Stage 3 TMDB artwork: {MediaType} {WithAny}/{Eligible} items have stored artwork evidence",
                summary.MediaType,
                summary.WithAnyArtwork,
                summary.EligibleCount);

            if (!summary.Pass)
            {
                var reason = string.Equals(summary.MediaType, "TV", StringComparison.OrdinalIgnoreCase)
                    && summary.WithEpisodeStill == 0
                    ? "no stored episode stills were created"
                    : "no stored optional artwork assets were created";
                report.IssuesFound.Add(
                    $"Stage 3 TMDB artwork: {reason} for eligible {summary.MediaType} items");
            }
        }
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    private static bool HasTmdbArtworkBridgeId(IReadOnlyDictionary<string, string> metadata)
    {
        static bool HasValue(IReadOnlyDictionary<string, string> map, params string[] keys) =>
            keys.Any(key => map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value));

        return HasValue(
            metadata,
            BridgeIdKeys.TmdbId,
            "tmdb_movie_id",
            "tmdb_tv_id");
    }

    private static string DescribeFileSystemCheck(FileSystemCheckResult check)
    {
        if (!check.FileExists)
        {
            return "File path is missing on disk";
        }

        if (!check.LocationMatchesExpectation)
        {
            return check.ExpectedLocation == "Library"
                ? "File never reached the organized library"
                : "File should still be in staging";
        }

        if (check.RequiresTemplateMatch && !check.PathMatchesTemplate)
        {
            return $"Template drift: expected {check.ExpectedRelativePath}";
        }

        if (check.HasLegacyHeroSidecar)
        {
            return "Legacy hero.jpg sidecar still exists next to the media file";
        }

        if (check.HasStoredLegacyHero)
        {
            return "Legacy hero.jpg still exists in the central asset store";
        }

        if (check.RequiresSidecarArtwork && (!check.HasPoster || !check.HasPosterThumb))
        {
            return "Sidecar artwork is incomplete next to the media file";
        }

        if (check.RequiresStoredArtwork && (!check.HasStoredCover || !check.HasStoredCoverSmall || !check.HasStoredCoverMedium || !check.HasStoredCoverLarge || !check.HasStoredPalette))
        {
            return "Stored artwork renditions or palette metadata are incomplete in the central asset store";
        }

        return "OK";
    }

    private static void ValidateWatchFolders(
        IConfigurationLoader configLoader,
        TestReport report,
        ILogger logger)
    {
        var expectedStagingPaths = report.FileSystemChecks
            .Where(c => c.ExpectedLocation == "Staging"
                && c.FileExists
                && !string.IsNullOrWhiteSpace(c.ActualFilePath))
            .Select(c => NormalizeComparablePath(c.ActualFilePath!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string path in ResolveLeafSourcePaths(configLoader))
        {
            report.WatchFolderChecks.Add(new WatchFolderCheckResult
            {
                Directory = path,
                RemainingMediaFiles = CountMediaFiles(path, expectedStagingPaths),
                IgnoredExpectedStagingFiles = CountIgnoredMediaFiles(path, expectedStagingPaths),
            });
        }

        logger.LogInformation("  Watch folders: {Empty}/{Total} drained",
            report.WatchFolderChecks.Count(c => c.Pass),
            report.WatchFolderChecks.Count);
    }

    private static IReadOnlyList<string> ResolveLeafSourcePaths(IConfigurationLoader configLoader)
    {
        var allPaths = configLoader.LoadLibraries().Libraries
            .SelectMany(lib => lib.ScannableSources.Select(source => source.Path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .ToList();

        return allPaths
            .Where(path => !allPaths.Any(other =>
                !path.Equals(other, StringComparison.OrdinalIgnoreCase)
                && other.StartsWith(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(path => path)
            .ToList();
    }

    private static int CountMediaFiles(string directory, ISet<string>? ignoredPaths = null)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Count(path =>
                    MediaExtensions.Contains(Path.GetExtension(path))
                    && (ignoredPaths is null || !ignoredPaths.Contains(NormalizeComparablePath(path))));
        }
        catch
        {
            return 0;
        }
    }

    private static int CountIgnoredMediaFiles(string directory, ISet<string> ignoredPaths)
    {
        if (!Directory.Exists(directory) || ignoredPaths.Count == 0)
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Count(path =>
                    MediaExtensions.Contains(Path.GetExtension(path))
                    && ignoredPaths.Contains(NormalizeComparablePath(path)));
        }
        catch
        {
            return 0;
        }
    }

    private static bool HasAnySidecarPoster(string mediaFilePath) =>
        EnumeratePosterCandidates(mediaFilePath).Any(File.Exists);

    private static bool HasAnySidecarPosterThumb(string mediaFilePath) =>
        EnumeratePosterThumbCandidates(mediaFilePath).Any(File.Exists);

    private static IEnumerable<string> EnumeratePosterCandidates(string mediaFilePath)
    {
        if (string.IsNullOrWhiteSpace(mediaFilePath))
        {
            yield break;
        }

        var dir = Path.GetDirectoryName(mediaFilePath) ?? string.Empty;
        var basename = Path.GetFileNameWithoutExtension(mediaFilePath);
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in new[]
        {
            AssetPathService.GetMediaFilePosterPath(mediaFilePath),
            Path.Combine(dir, "poster.jpg"),
            Path.Combine(dir, $"{basename}-poster.jpg"),
        })
        {
            if (yielded.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumeratePosterThumbCandidates(string mediaFilePath)
    {
        if (string.IsNullOrWhiteSpace(mediaFilePath))
        {
            yield break;
        }

        var dir = Path.GetDirectoryName(mediaFilePath) ?? string.Empty;
        var basename = Path.GetFileNameWithoutExtension(mediaFilePath);
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in new[]
        {
            AssetPathService.GetMediaFileThumbPath(mediaFilePath),
            Path.Combine(dir, "poster-thumb.jpg"),
            Path.Combine(dir, $"{basename}-poster-thumb.jpg"),
        })
        {
            if (yielded.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static string NormalizeComparablePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);




    private static string SanitizeFileName(string title)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(title.Length);
        foreach (char c in title)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }

        return sb.ToString();
    }
}
