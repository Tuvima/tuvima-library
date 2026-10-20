using System.Text;
using System.Text.Json;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Options;
using ContractWipeScope = MediaEngine.Contracts.Development.DevHarnessWipeScope;
using DevHarnessReingestResponse = MediaEngine.Contracts.Development.DevHarnessReingestResponse;
using DevHarnessResetResponse = MediaEngine.Contracts.Development.DevHarnessResetResponse;
using DevHarnessWipeResponse = MediaEngine.Contracts.Development.DevHarnessWipeResponse;

namespace MediaEngine.Api.DevSupport;

public enum DevelopmentFixtureSet
{
    Standard,
    Stress,
}

/// <summary>
/// Development-only endpoints for seeding the library with test data.
/// Registered conditionally when <c>ASPNETCORE_ENVIRONMENT == "Development"</c>.
///
/// Endpoints:
///   POST /dev/seed-library  — Drop test files into media-type-specific Watch Folders
///   POST /dev/wipe           — Wipe DB, library root, watch folder, and reinitialize
///   POST /dev/full-test      — Wipe → Seed → return summary
/// </summary>
public static partial class DevSeedEndpoints
{
    private static readonly HashSet<string> StandardBookTitles = new(StringComparer.Ordinal)
    {
        "Dune",
        "Leviathan Wakes",
        "Caliban's War",
    };

    private static readonly HashSet<string> StandardMusicTitles = new(StringComparer.Ordinal)
    {
        "Bohemian Rhapsody",
        "You're My Best Friend",
        "Death on Two Legs",
    };

    private static readonly HashSet<string> StandardComicTitles = new(StringComparer.Ordinal)
    {
        "Batman: Year One Part 1",
        "Batman: Year One Part 2",
    };
    /// <summary>A seed EPUB definition.</summary>
    /// <remarks>
    /// <para><c>ExpectedQid</c> — when set, the reconciliation pass asserts the
    /// resolved Wikidata QID exactly matches this value. Leave null for real
    /// fixtures where any non-placeholder QID is acceptable; no-QID outcomes
    /// are only acceptable when <c>ExpectIdentified</c> is false and the fixture
    /// declares a review trigger or known no-entity reason.</para>
    /// <para><c>ExpectedCoverArt</c> — when true (default), the library display
    /// validation asserts that cover art was successfully downloaded for this
    /// item. Set to false for fixtures where no cover art is expected (e.g.
    /// placeholder titles or review-queue items).</para>
    /// </remarks>
    private sealed record SeedBook(
        string Title,
        string Author,
        string Isbn,
        int Year,
        string Description,
        string? Publisher = null,
        string Language = "en",
        string[]? AdditionalAuthors = null,
        string? Series = null,
        int? SeriesPosition = null,
        string? TestCategory = null,
        bool ExpectIdentified = true,
        string? ExpectedReviewTrigger = null,
        string? ExpectedReason = null,
        string? ExpectedProvider = null,
        string? ExpectedQid = null,
        bool ExpectedCoverArt = true,
        string? ReconciliationTitle = null);

    /// <summary>A seed MP3 audiobook definition.</summary>
    private sealed record SeedAudiobook(
        string Title,
        string Artist,
        string Narrator,
        int Year,
        string Language = "eng",
        string? Series = null,
        int? SeriesPosition = null,
        string? Asin = null,
        string? TestCategory = null,
        bool ExpectIdentified = true,
        string? ExpectedReviewTrigger = null,
        string? ExpectedReason = null,
        string? ExpectedProvider = null,
        string? ExpectedQid = null,
        bool ExpectedCoverArt = true,
        int PartCount = 1,
        bool IncludeAbsMetadataSidecar = false);

    /// <summary>A seed MP4 movie/TV definition.</summary>
    private sealed record SeedVideo(
        string Title,
        string? Director,
        int Year,
        string MediaType,  // "Movie" or "TV"
        string? Series = null,
        int? SeasonNumber = null,
        int? EpisodeNumber = null,
        string? EpisodeTitle = null,
        string? FileNameOverride = null,
        string? TestCategory = null,
        bool ExpectIdentified = true,
        string? ExpectedReviewTrigger = null,
        string? ExpectedReason = null,
        string? ExpectedProvider = null,
        string? ExpectedQid = null,
        bool ExpectedCoverArt = true,
        string? ReconciliationTitle = null);

    /// <summary>A seed FLAC music track definition.</summary>
    private sealed record SeedMusic(
        string Title,
        string Artist,
        string? Album = null,
        int Year = 0,
        string? Genre = null,
        int? TrackNumber = null,
        string? TestCategory = null,
        bool ExpectIdentified = true,
        string? ExpectedReviewTrigger = null,
        string? ExpectedReason = null,
        string? ExpectedProvider = null,
        string? ExpectedQid = null,
        bool ExpectedCoverArt = true,
        string? AlbumArtist = null);

    /// <summary>A seed CBZ comic definition.</summary>
    private sealed record SeedComic(
        string Title,
        string? Writer = null,
        string? Series = null,
        int? Number = null,
        int Year = 0,
        string? Genre = null,
        string? Summary = null,
        string? Publisher = null,
        string? Penciller = null,
        string? TestCategory = null,
        bool ExpectIdentified = true,
        string? ExpectedReviewTrigger = null,
        string? ExpectedReason = null,
        string? ExpectedProvider = null,
        string? ExpectedQid = null,
        bool ExpectedCoverArt = true);

    // ── EPUB Seed definitions ────────────────────────────────────────────────
    // Real ISBNs so the hydration pipeline can fetch real cover art and metadata.

    private static readonly SeedBook[] SeedBooks =
    [
        // ── Category 1: Standard Cases (clean metadata, strong Wikidata presence) ──

        new("Dune",
            "Frank Herbert",
            "9780441013593", 1965,
            "Set on the desert planet Arrakis, Dune is the story of the boy Paul Atreides, heir to a noble family tasked with ruling an inhospitable world where the only thing of value is a spice capable of extending life and expanding consciousness.",
            TestCategory: "Standard",
            ExpectedQid: "Q190192"),

        new("Project Hail Mary",
            "Andy Weir",
            "9780593135204", 2021,
            "Ryland Grace is the sole survivor on a desperate, last-chance mission. If he fails, humanity and the earth itself will perish.",
            TestCategory: "Standard",
            ExpectedQid: "Q106852836"),

        new("The Hobbit",
            "J.R.R. Tolkien",
            "9780547928227", 1937,
            "Bilbo Baggins is a hobbit who enjoys a comfortable, unambitious life, rarely travelling further than the pantry of his hobbit-hole in Bag End.",
            TestCategory: "Standard",
            ExpectedQid: "Q74287"),

        // ── Category 2: Pen Names ──────────────────────────────────────────

        new("Leviathan Wakes",
            "James S. A. Corey",
            "9780316129084", 2011,
            "Humanity has colonized the solar system. Jim Holden is XO of an ice hauler that makes a horrifying discovery in the asteroid belt.",
            Series: "The Expanse", SeriesPosition: 1,
            TestCategory: "PenName — collaborative (Daniel Abraham + Ty Franck)"),

        new("Caliban's War",
            "James S. A. Corey",
            "9780316129060", 2012,
            "On Ganymede, breadbasket of the outer planets, a Martian marine watches as her platoon is slaughtered by a monstrous supersoldier.",
            Series: "The Expanse", SeriesPosition: 2,
            TestCategory: "PenName — same collaborative pen name, series book #2"),

        new("The Shining",
            "Stephen King",
            "9780307743657", 1977,
            "Jack Torrance's new job at the Overlook Hotel is the perfect chance for a fresh start. But as the harsh winter weather sets in, the idyllic location feels ever more sinister.",
            TestCategory: "PenName — author also writes as Richard Bachman",
            ExpectedQid: "Q470937"),

        new("The Long Walk",
            "Richard Bachman",
            "9781501143823", 1979,
            "On the first day of May, one hundred teenage boys meet for an annual walking contest called The Long Walk.",
            TestCategory: "PenName — Stephen King writing as Richard Bachman",
            ExpectedQid: "Q384160"),

        // ── Category 3: Foreign Language ───────────────────────────────────

        new("Le Petit Prince",
            "Antoine de Saint-Exupéry",
            "9782070612758", 1943,
            "Un pilote, forcé d'atterrir dans le Sahara, rencontre un petit garçon venu d'une autre planète.",
            Language: "fr",
            TestCategory: "Foreign — French, accented author name",
            ExpectedQid: "Q25338"),

        new("Cien años de soledad",
            "Gabriel García Márquez",
            "9780307474728", 1967,
            "La historia de la familia Buendía a lo largo de siete generaciones en el pueblo ficticio de Macondo.",
            Language: "es",
            TestCategory: "Foreign — Spanish, special chars in title AND author",
            ReconciliationTitle: "One Hundred Years of Solitude"),

        new("Die Verwandlung",
            "Franz Kafka",
            "9783150091319", 1915,
            "Als Gregor Samsa eines Morgens aus unruhigen Träumen erwachte, fand er sich in seinem Bett zu einem ungeheueren Ungeziefer verwandelt.",
            Language: "de",
            TestCategory: "Foreign — German"),

        new("ノルウェイの森",
            "村上春樹",
            "9784062748681", 1987,
            "ワタナベトオルが、亡き親友キズキの恋人であった直子との関係を中心に、1960年代後半の東京での大学生活を回想する。",
            Language: "ja",
            TestCategory: "Foreign — Japanese, CJK title and author",
            ReconciliationTitle: "Norwegian Wood"),

        // ── Category 4: Series Books (Collection grouping + sequence) ─────────────

        new("Harry Potter and the Philosopher's Stone",
            "J.K. Rowling",
            "9780747532699", 1997,
            "Harry Potter has never even heard of Hogwarts when the letters start dropping on the doormat at number four, Privet Drive.",
            Series: "Harry Potter", SeriesPosition: 1,
            TestCategory: "Series — position 1",
            ExpectedReviewTrigger: ReviewTrigger.RetailMatchFailed),

        new("Harry Potter and the Chamber of Secrets",
            "J.K. Rowling",
            "9780747538486", 1998,
            "Harry Potter's summer has included the worst birthday ever, doomy warnings from a house-elf called Dobby, and rescue from the Dursleys by his friend Ron Weasley in a magical flying car!",
            Series: "Harry Potter", SeriesPosition: 2,
            TestCategory: "Series — position 2, same author/series as above"),

        new("The Fellowship of the Ring",
            "J.R.R. Tolkien",
            "9780547928210", 1954,
            "In ancient times the Rings of Power were crafted by the Elven-smiths, and Sauron, the Dark Lord, forged the One Ring, filling it with his own power so that he could rule all others.",
            Series: "The Lord of the Rings", SeriesPosition: 1,
            TestCategory: "Series — same author as The Hobbit, different series",
            ExpectedQid: "Q208002"),

        // ── Category 5: Multiple Authors (co-authored, not pen name) ───────

        new("Good Omens",
            "Terry Pratchett",
            "9780060853983", 1990,
            "According to The Nice and Accurate Prophecies of Agnes Nutter, Witch, the world will end on a Saturday. Next Saturday, in fact.",
            AdditionalAuthors: ["Neil Gaiman"],
            TestCategory: "MultiAuthor — two distinct real authors"),

        new("The Talisman",
            "Stephen King",
            "9781501192272", 1984,
            "Jack Sawyer, twelve years old, is about to begin a most fantastic journey, an exhilarating, terrifying quest across the country and into another realm.",
            AdditionalAuthors: ["Peter Straub"],
            TestCategory: "MultiAuthor — King again (cross-ref with pen name tests)"),

        // ── Category 6: Edge Cases ─────────────────────────────────────────

        new("Untitled Book",
            "Unknown",
            "", 0,
            "",
            TestCategory: "Edge — minimal metadata: no ISBN, no year, no description",
            ExpectIdentified: false,
            ExpectedReviewTrigger: ReviewTrigger.PlaceholderTitle,
            ExpectedReason: "Placeholder title 'Untitled Book' with no real metadata should trigger review",
            ExpectedCoverArt: false),

        new("A",
            "B",
            "", 2000,
            "A very short title.",
            TestCategory: "Edge — extremely short title and author, no bridge IDs",
            ExpectIdentified: false,
            ExpectedReviewTrigger: ReviewTrigger.PlaceholderTitle,
            ExpectedReason: "Single-character title with no ISBN should trigger placeholder review",
            ExpectedCoverArt: false),

        new("The Extraordinary & Fantastical Adventures of Dr. Enid Hartwell-Smythe III: A Most Peculiar Chronicle",
            "Reginald Fortescue-Pemberton IV",
            "9780000000001", 2020,
            "A book with an extraordinarily long title and author name designed to test truncation, file naming, and display in constrained UI elements.",
            TestCategory: "Edge — very long title and author, special chars (& : .)",
            ExpectIdentified: false,
            ExpectedReviewTrigger: ReviewTrigger.RetailMatchFailed,
            ExpectedReason: "Fictional book with synthetic ISBN correctly fails retail provider matching",
            ExpectedCoverArt: false),

        new("1Q84",
            "Haruki Murakami",
            "9780307593313", 2009,
            "A young woman named Aomame follows a taxi driver's suggestion and climbs down an emergency stairway into a world she calls 1Q84.",
            TestCategory: "Edge — numeric-starting title, same author as Japanese entry"),

        new("The Road",
            "Cormac McCarthy",
            "9780307387899", 2006,
            "A father and his son walk alone through burned America, heading through the ravaged landscape to the coast.",
            TestCategory: "Edge — standalone, no series (single-work Collection)"),

        // ── Category 7: Publisher Metadata ──────────────────────────────────

        new("Frankenstein",
            "Mary Shelley",
            "9780141439471", 1818,
            "Obsessed with creating life itself, Victor Frankenstein plunders graveyards for the material to fashion a new being.",
            Publisher: "Lackington, Hughes, Harding, Mavor & Jones",
            TestCategory: "Publisher — very old book, long publisher name with special chars"),

        // ── Category 8: Standalone classics (audiobook pairing targets) ─────

        new("Neuromancer",
            "William Gibson",
            "9780441569595", 1984,
            "The sky above the port was the color of television, tuned to a dead channel.",
            TestCategory: "Standalone — cyberpunk classic, audiobook pair target"),
    ];

    // ── MP3 Audiobook Seed definitions ───────────────────────────────────────
    // Paired with EPUBs above to test cross-format Collection grouping and Stage 2
    // bridge resolution. Genre tag set to "Audiobook" for disambiguation.

    private static readonly SeedAudiobook[] SeedAudiobooks =
    [
        new("The Clockmaker's Kaleidoscope of Vanishing Summers", "Percival Moon", "Imogen Vale", 2020,
            TestCategory: "Edge - synthetic audiobook title, should fail retail matching and enter review",
            ExpectIdentified: false,
            ExpectedReviewTrigger: ReviewTrigger.RetailMatchFailed,
            ExpectedReason: "Synthetic audiobook title should not match any retail provider",
            ExpectedCoverArt: false),

        // ── Paired with EPUB counterparts ─────────────────────────────────────

        new("Dune", "Frank Herbert", "Simon Vance", 1965,
            Series: "Dune Chronicles", SeriesPosition: 1,
            TestCategory: "Audiobook pair — Dune (Simon Vance narrator)"),

        new("Project Hail Mary", "Andy Weir", "Ray Porter", 2021,
            TestCategory: "Edge — multi-file audiobook with ABS metadata sidecar",
            PartCount: 3,
            IncludeAbsMetadataSidecar: true),

        new("The Hobbit", "J.R.R. Tolkien", "Andy Serkis", 1937,
            TestCategory: "Audiobook pair — celebrity narrator"),

        new("Good Omens", "Terry Pratchett and Neil Gaiman", "Martin Jarvis", 1990,
            TestCategory: "Audiobook pair — multi-author work"),

        new("1Q84", "Haruki Murakami", "Allison Hiroto", 2009,
            TestCategory: "Audiobook pair — numeric-starting title"),

        new("The Shining", "Stephen King", "Campbell Scott", 1977,
            TestCategory: "Audiobook pair — pen name author (King/Bachman)"),

        new("Le Petit Prince", "Antoine de Saint-Exupery", "Bernard Giraudeau", 1943,
            Language: "fra",
            TestCategory: "Audiobook pair — foreign language (French)"),

        new("Harry Potter and the Philosopher's Stone", "J.K. Rowling", "Stephen Fry", 1997,
            Series: "Harry Potter", SeriesPosition: 1,
            TestCategory: "Audiobook pair — series book with famous narrator"),

        new("The Name of the Wind", "Patrick Rothfuss", "Nick Podehl", 2007,
            Series: "The Kingkiller Chronicle", SeriesPosition: 1,
            TestCategory: "Audiobook pair — series (no EPUB counterpart in series list)"),

        new("Leviathan Wakes", "James S. A. Corey", "Jefferson Mays", 2011,
            Series: "The Expanse", SeriesPosition: 1,
            TestCategory: "Audiobook pair — pen name series"),

        new("Foundation", "Isaac Asimov", "Scott Brick", 1951,
            Series: "Foundation", SeriesPosition: 1,
            TestCategory: "Audiobook pair — classic series (no EPUB counterpart)"),

        new("The Fellowship of the Ring", "J.R.R. Tolkien", "Rob Inglis", 1954,
            Series: "The Lord of the Rings", SeriesPosition: 1,
            TestCategory: "Audiobook pair — classic series with iconic narrator"),

        new("Neuromancer", "William Gibson", "Robertson Dean", 1984,
            TestCategory: "Audiobook pair — standalone classic"),

        new("The Road", "Cormac McCarthy", "Tom Stechschulte", 2006,
            TestCategory: "Audiobook pair — standalone"),

        // ── Multiple editions test (same work, different narrator) ──────────

        new("Dune", "Frank Herbert", "Scott Brick", 1965,
            Series: "Dune Chronicles", SeriesPosition: 1,
            TestCategory: "Multiple editions — Dune with alternate narrator"),
    ];

    // ── MP4 Movie / TV Seed definitions ────────────────────────────────────
    // Titles chosen for strong TMDB + Wikidata presence.

    private static readonly SeedVideo[] SeedVideos =
    [
        new("Chronicles of the Titanium Orchard", "Alistair Wren", 2023, "Movie",
            TestCategory: "Movie - synthetic title, should fail TMDB matching and enter review",
            ExpectIdentified: false,
            ExpectedReviewTrigger: ReviewTrigger.RetailMatchFailed,
            ExpectedReason: "Synthetic movie title should not match TMDB",
            ExpectedCoverArt: false),

        new("Paper Storm", null, 2023, "TV",
            Series: "Department of Clockwork Rain", SeasonNumber: 1, EpisodeNumber: 1,
            TestCategory: "TV - synthetic series, should fail TMDB matching and enter review",
            ExpectIdentified: false,
            ExpectedReviewTrigger: ReviewTrigger.RetailMatchFailed,
            ExpectedReason: "Synthetic TV series should not match TMDB",
            ExpectedCoverArt: false,
            ReconciliationTitle: "Department of Clockwork Rain"),

        // ── Movies ──────────────────────────────────────────────────────────

        new("Blade Runner 2049", "Denis Villeneuve", 2017, "Movie",
            TestCategory: "Movie — same director as Dune films, strong TMDB match",
            ExpectedQid: "Q21500755"),

        new("Dune: Part One", "Denis Villeneuve", 2021, "Movie",
            FileNameOverride: "Dune Part One (2021) {imdb-tt1160419}.mp4",
            TestCategory: "Movie - Dune cross-media fixture for audiobook/movie linkage",
            ExpectedProvider: "tmdb",
            ReconciliationTitle: "Dune"),

        new("Dune: Part Two", "Denis Villeneuve", 2024, "Movie",
            FileNameOverride: "Dune Part Two (2024) {imdb-tt15239678}.mp4",
            TestCategory: "Movie - Dune sequel cross-media fixture for series totals and linkage",
            ExpectedProvider: "tmdb"),

        new("Batman Begins", "Christopher Nolan", 2005, "Movie",
            FileNameOverride: "Batman Begins (2005) {imdb-tt0372784}.mp4",
            TestCategory: "Movie - Batman cross-media fixture for comic/movie linkage",
            ExpectedProvider: "tmdb"),

        new("The Dark Knight", "Christopher Nolan", 2008, "Movie",
            FileNameOverride: "The Dark Knight (2008) {imdb-tt0468569}.mp4",
            TestCategory: "Movie - Batman sequel cross-media fixture for comic/movie linkage",
            ExpectedProvider: "tmdb"),

        new("The Matrix", "Lana Wachowski", 1999, "Movie",
            TestCategory: "Movie — classic, strong Wikidata presence",
            ExpectedQid: "Q83495"),

        new("Arrival", "Denis Villeneuve", 2016, "Movie",
            TestCategory: "Movie — same director as Blade Runner, cross-reference test",
            ExpectedQid: "Q20382729"),

        new("Spirited Away", "Hayao Miyazaki", 2001, "Movie",
            TestCategory: "Movie — Japanese film, foreign language metadata",
            ExpectedQid: "Q155653"),

        new("Interstellar", "Christopher Nolan", 2014, "Movie",
            TestCategory: "Movie — strong TMDB match, popular film",
            ExpectedQid: "Q13417189"),

        new("The Shawshank Redemption", "Frank Darabont", 1994, "Movie",
            TestCategory: "Movie — Stephen King adaptation (cross-ref with books)",
            ExpectedQid: "Q172241"),

        new("Rise of the Planet of the Apes", "Rupert Wyatt", 2011, "Movie",
            FileNameOverride: "Rise of the Planet of the Apes (2011) {imdb-tt1318514}.mp4",
            TestCategory: "Movie — Andy Serkis lead actor credit cross-linked with The Hobbit audiobook narrator",
            ExpectedProvider: "tmdb"),

        new("Drive", "Nicolas Winding Refn", 2011, "Movie",
            FileNameOverride: "Drive (2011) {imdb-tt0780504}.mp4",
            TestCategory: "Movie — Bryan Cranston actor credit cross-linked with Breaking Bad TV",
            ExpectedProvider: "tmdb"),

        // ── TV Episodes ─────────────────────────────────────────────────────

        new("Breaking Bad", null, 2008, "TV",
            Series: "Breaking Bad", SeasonNumber: 1, EpisodeNumber: 1,
            TestCategory: "TV — S01E01, strong TMDB match",
            ExpectedProvider: "tmdb"),

        new("Breaking Bad", null, 2008, "TV",
            Series: "Breaking Bad", SeasonNumber: 1, EpisodeNumber: 2,
            TestCategory: "TV — S01E02, same series grouping test",
            ExpectedProvider: "tmdb"),

        new("The Expanse", null, 2015, "TV",
            Series: "The Expanse", SeasonNumber: 1, EpisodeNumber: 1,
            TestCategory: "TV — cross-ref with book series (Leviathan Wakes)",
            ExpectedProvider: "tmdb"),

        new("Shogun", null, 2024, "TV",
            Series: "Shogun", SeasonNumber: 1, EpisodeNumber: 1,
            TestCategory: "TV — recent series, cross-media potential",
            ExpectedProvider: "tmdb"),

        // ── New TV fixtures: filename pattern coverage (Phase: scoring fix) ──
        // Each fixture targets a different on-disk filename pattern so the
        // VideoProcessor's TV regex variants and the structural-bonus scoring
        // path are all exercised end-to-end by the integration test.

        new("Pilot", null, 2008, "TV",
            Series: "Breaking Bad", SeasonNumber: 1, EpisodeNumber: 1,
            EpisodeTitle: "Pilot",
            FileNameOverride: "Breaking Bad/Season 01/Breaking Bad - S01E01 - Pilot.mp4",
            TestCategory: "TV pattern — show + SxxExx + episode title in nested folder",
            ExpectedProvider: "tmdb"),

        new("Anjin", null, 2024, "TV",
            Series: "Shogun", SeasonNumber: 1, EpisodeNumber: 1,
            EpisodeTitle: "Anjin",
            FileNameOverride: "Shogun (2024)/Season 01/Shogun - S01E01 - Anjin WEBRip-1080p v2.mp4",
            TestCategory: "TV pattern — release-quality suffix and v2 marker are excluded from episode title",
            ExpectedProvider: "tmdb"),

        new("Chapter 1: The Mandalorian", null, 2019, "TV",
            Series: "The Mandalorian", SeasonNumber: 1, EpisodeNumber: 1,
            EpisodeTitle: "Chapter 1 - The Mandalorian",
            FileNameOverride: "The Mandalorian/Season 01/S01E01 - Chapter 1 - The Mandalorian.mp4",
            TestCategory: "TV pattern — leading SxxExx (no show prefix), show inferred from folder",
            ExpectedProvider: "tmdb"),

        new("The Mathematician's Ghost", null, 2021, "TV",
            Series: "Foundation", SeasonNumber: 1, EpisodeNumber: 3,
            EpisodeTitle: "The Mathematician's Ghost",
            FileNameOverride: "Foundation/Season 01/Foundation - S01E03 - The Mathematician's Ghost.mp4",
            TestCategory: "TV pattern — non-pilot episode with possessive in title",
            ExpectedProvider: "tmdb"),

        new("The You You Are", null, 2022, "TV",
            Series: "Severance", SeasonNumber: 1, EpisodeNumber: 4,
            EpisodeTitle: "The You You Are",
            FileNameOverride: "Severance/Season 01/Severance.S01E04.The.You.You.Are.mp4",
            TestCategory: "TV pattern — dot-separated filename convention",
            ExpectedProvider: "tmdb"),

        new("Ozymandias", null, 2013, "TV",
            Series: "Breaking Bad", SeasonNumber: 5, EpisodeNumber: 14,
            EpisodeTitle: "Ozymandias",
            FileNameOverride: "Breaking Bad/Season 05/Breaking Bad - S05E14 - Ozymandias.mp4",
            TestCategory: "TV pattern — higher season and two-digit episode, stress late-series batching",
            ExpectedProvider: "tmdb"),

        new("Seven Thirty-Seven", null, 2009, "TV",
            Series: "Breaking Bad", SeasonNumber: 2, EpisodeNumber: 1,
            EpisodeTitle: "Seven Thirty-Seven",
            FileNameOverride: "Breaking Bad/Season 02/Breaking Bad - S02E01 - Seven Thirty-Seven.mp4",
            TestCategory: "TV pattern - second season episode, validates season selector grouping",
            ExpectedProvider: "tmdb"),

        new("No Mas", null, 2010, "TV",
            Series: "Breaking Bad", SeasonNumber: 3, EpisodeNumber: 1,
            EpisodeTitle: "No Mas",
            FileNameOverride: "Breaking Bad/Season 03/Breaking Bad - S03E01 - No Mas.mp4",
            TestCategory: "TV pattern - third season episode, validates multi-season library display",
            ExpectedProvider: "tmdb"),

        new("Safe", null, 2017, "TV",
            Series: "The Expanse", SeasonNumber: 2, EpisodeNumber: 1,
            EpisodeTitle: "Safe",
            FileNameOverride: "The Expanse/Season 02/The Expanse - S02E01 - Safe.mp4",
            TestCategory: "TV pattern - second season sci-fi episode, validates cross-season grouping",
            ExpectedProvider: "tmdb"),

        new("Spider-Man: Into the Spider-Verse", "Bob Persichetti", 2018, "Movie",
            TestCategory: "Movie — colon title with subtitle and punctuation, strong TMDB match",
            ExpectedProvider: "tmdb"),
    ];

    // ── FLAC Music Seed definitions ────────────────────────────────────────
    // FLAC is unambiguously routed to Music by AudioProcessor (0.95 confidence).

    private static readonly SeedMusic[] SeedMusicTracks =
    [
        new("Nebula Teacup Waltz", "Professor Thimblewick",
            Album: "Songs for Mechanical Owls", Year: 2020, Genre: "Experimental", TrackNumber: 1,
            TestCategory: "Music - synthetic artist and track, should fail Apple Music matching and enter review",
            ExpectIdentified: false,
            ExpectedReviewTrigger: ReviewTrigger.RetailMatchFailed,
            ExpectedReason: "Synthetic music track should not match Apple Music",
            ExpectedCoverArt: false),

        // ── Category 1: Standard (strong Apple Music presence) ─────────────
        new("Bohemian Rhapsody", "Queen",
            Album: "A Night at the Opera", Year: 1975, Genre: "Rock", TrackNumber: 11,
            TestCategory: "Music — classic track, strong Apple Music match"),

        // Regression albums: incomplete ownership must still produce a complete
        // provider manifest, and repeated artists must span multiple album roots.
        new("Five Years", "David Bowie",
            Album: "The Rise and Fall of Ziggy Stardust and the Spiders from Mars", Year: 1972, Genre: "Rock", TrackNumber: 1,
            TestCategory: "Music — Bowie album one; verifies multi-album artist grouping"),

        new("Beauty and the Beast", "David Bowie",
            Album: "Heroes", Year: 1977, Genre: "Rock", TrackNumber: 1,
            TestCategory: "Music — Bowie album two; must not match a 50-track box set"),

        new("Come Together", "The Beatles",
            Album: "Abbey Road", Year: 1969, Genre: "Rock", TrackNumber: 1,
            TestCategory: "Music — Abbey Road manifest and cover regression"),

        new("Something", "The Beatles",
            Album: "Abbey Road", Year: 1969, Genre: "Rock", TrackNumber: 2,
            TestCategory: "Music — Abbey Road second owned track with remaining tracks missing"),

        new("BLOOD.", "Kendrick Lamar",
            Album: "DAMN.", Year: 2017, Genre: "Hip-Hop", TrackNumber: 1,
            TestCategory: "Music — one owned DAMN. track; all other album tracks must show missing"),

        new("Dreaming of the Crash", "Hans Zimmer",
            Album: "Interstellar: Original Motion Picture Soundtrack", Year: 2014, Genre: "Soundtrack", TrackNumber: 1,
            TestCategory: "Music — Interstellar MusicBrainz release and Cover Art Archive regression"),

        new("Cornfield Chase", "Hans Zimmer",
            Album: "Interstellar: Original Motion Picture Soundtrack", Year: 2014, Genre: "Soundtrack", TrackNumber: 2,
            TestCategory: "Music — Interstellar second owned track"),

        new("Time", "Hans Zimmer",
            Album: "Inception (Music from the Motion Picture)", Year: 2010, Genre: "Soundtrack", TrackNumber: 12,
            TestCategory: "Music — Hans Zimmer second album for artist shelf validation"),

        new("Clair de Lune", "Claude Debussy",
            Album: "Suite bergamasque", Year: 1905, Genre: "Classical", TrackNumber: 3,
            TestCategory: "Music — classical, foreign artist name — Apple bridge IDs lack Wikidata P-code mapping",
            ExpectIdentified: true,
            ExpectedCoverArt: false),

        new("Lose Yourself", "Eminem",
            Album: "8 Mile: Music from and Inspired by the Motion Picture", Year: 2002, Genre: "Hip-Hop", TrackNumber: 1,
            TestCategory: "Music — soundtrack, must resolve to 8 Mile OST via Apple Music"),

        new("Nuvole Bianche", "Ludovico Einaudi",
            Album: "Una Mattina", Year: 2004, Genre: "Classical", TrackNumber: 6,
            TestCategory: "Music — contemporary classical, Italian artist"),

        new("Across the Stars", "John Williams",
            Album: "Star Wars: Attack of the Clones", Year: 2002, Genre: "Soundtrack", TrackNumber: 3,
            TestCategory: "Music — film soundtrack, franchise cross-ref"),

        // ── Category 2: Album grouping (multiple tracks, same album) ──────
        new("You're My Best Friend", "Queen",
            Album: "A Night at the Opera", Year: 1975, Genre: "Rock", TrackNumber: 4,
            TestCategory: "Music — same album as Bohemian Rhapsody, Collection grouping test"),

        new("Death on Two Legs", "Queen",
            Album: "A Night at the Opera", Year: 1975, Genre: "Rock", TrackNumber: 1,
            TestCategory: "Music — same album, track 1, Collection grouping test"),

        // ── Category 3: Multi-artist / featured / collaboration ───────────
        new("Love of My Life", "Queen",
            Album: "A Night at the Opera", Year: 1975, Genre: "Rock", TrackNumber: 9,
            TestCategory: "Music — expands same-album Queen batch, stresses grouped retail and Wikidata batching"),

        new("Seaside Rendezvous", "Queen",
            Album: "A Night at the Opera", Year: 1975, Genre: "Rock", TrackNumber: 7,
            TestCategory: "Music — deep-cut same album track, stresses larger grouped album distribution"),

        new("Under Pressure", "Queen & David Bowie",
            Album: "Hot Space", Year: 1982, Genre: "Rock", TrackNumber: 11,
            TestCategory: "Music — track artist differs from album artist",
            AlbumArtist: "Queen"),

        new("Stan", "Eminem",
            Album: "The Marshall Mathers LP", Year: 2000, Genre: "Hip-Hop", TrackNumber: 3,
            TestCategory: "Music — same artist as Lose Yourself, different album"),

        // ── Category 4: Foreign language / non-Latin ──────────────────────
        new("La Vie en rose", "Édith Piaf",
            Album: "La Vie en rose", Year: 1947, Genre: "Chanson", TrackNumber: 1,
            TestCategory: "Music — French, accented artist name, classic",
            ExpectIdentified: true,
            ExpectedQid: "Q11986"),

        new("Für Elise", "Ludwig van Beethoven",
            Album: "Beethoven: Piano Pieces", Year: 1810, Genre: "Classical", TrackNumber: 1,
            TestCategory: "Music — German umlaut in title, historical classical"),

        new("99 Luftballons", "Nena",
            Album: "99 Luftballons", Year: 1983, Genre: "New Wave", TrackNumber: 1,
            TestCategory: "Music — German title, one-name artist"),

        // ── Category 5: Disambiguation / common titles ────────────────────
        new("Yesterday", "The Beatles",
            Album: "Help!", Year: 1965, Genre: "Pop", TrackNumber: 13,
            TestCategory: "Music — extremely common title, must resolve to Beatles version"),

        new("Imagine", "John Lennon",
            Album: "Imagine", Year: 1971, Genre: "Pop", TrackNumber: 1,
            TestCategory: "Music — album same name as track, iconic single"),

        // ── Category 6: Instrumental / soundtrack / orchestral ────────────
        new("We Will Rock You", "Queen",
            Album: "News of the World", Year: 1977, Genre: "Rock", TrackNumber: 1,
            TestCategory: "Music — same artist, second album group, stresses per-album natural-key batching"),

        new("We Are the Champions", "Queen",
            Album: "News of the World", Year: 1977, Genre: "Rock", TrackNumber: 2,
            TestCategory: "Music — adjacent track in second Queen album group, stresses grouped batch fan-out"),

        new("The Imperial March", "John Williams",
            Album: "Star Wars: The Empire Strikes Back", Year: 1980, Genre: "Soundtrack", TrackNumber: 3,
            TestCategory: "Music — same artist as Across the Stars, different franchise entry"),

        new("In the Hall of the Mountain King", "Edvard Grieg",
            Album: "Peer Gynt Suite No. 1", Year: 1875, Genre: "Classical", TrackNumber: 4,
            TestCategory: "Music — public domain classical, Norwegian composer"),

        // ── Category 7: Edge cases ────────────────────────────────────────
        new("4'33\"", "John Cage",
            Album: "John Cage: 4'33\"", Year: 1952, Genre: "Avant-Garde", TrackNumber: 1,
            TestCategory: "Edge — special chars in title (apostrophe + quotes), silent piece — exact retail match despite punctuation",
            ExpectIdentified: true,
            ExpectedProvider: "apple_api"),

        new("MMMBop", "Hanson",
            Album: "Middle of Nowhere", Year: 1997, Genre: "Pop", TrackNumber: 1,
            TestCategory: "Edge — unusual capitalization, 90s one-hit wonder"),

        new("Take Five", "Dave Brubeck",
            Album: "Time Out", Year: 1959, Genre: "Jazz", TrackNumber: 4,
            TestCategory: "Music — jazz standard, strong Apple Music presence"),

        new("Smells Like Teen Spirit", "Nirvana",
            Album: "Nevermind", Year: 1991, Genre: "Grunge", TrackNumber: 1,
            TestCategory: "Music — 90s rock, strong Wikidata QID presence"),
    ];

    // ── CBZ Comic Seed definitions ─────────────────────────────────────────
    // Comics with ComicInfo.xml metadata — tests the new ComicProcessor parsing.

    private static readonly SeedComic[] SeedComics =
    [
        new("Captain Semaphore #404", Writer: "Mara Quill",
            Series: "Captain Semaphore", Number: 404, Year: 2021, Genre: "Science Fiction",
            Summary: "A synthetic comic issue created to force a clean review-path exercise.",
            Publisher: "Signal House", Penciller: "Ivo North",
            TestCategory: "Comic - synthetic issue, should fail ComicVine matching and enter review",
            ExpectIdentified: false,
            ExpectedReviewTrigger: ReviewTrigger.RetailMatchFailed,
            ExpectedReason: "Synthetic comic issue should not match ComicVine",
            ExpectedCoverArt: false),

        // Comics expectations: ComicVine issue search should identify known real issues.
        // Some may still miss a Wikidata QID, but retail identification remains valid
        // and the item should stay usable without being forced into review.
        new("Batman: Year One Part 1", Writer: "Frank Miller",
            Series: "Batman", Number: 404, Year: 1987, Genre: "Superhero",
            Summary: "Bruce Wayne returns to Gotham City after years abroad.",
            Publisher: "DC Comics", Penciller: "David Mazzucchelli",
            TestCategory: "Comic — classic DC, series with issue number",
            ExpectedReason: "When the issue item is missing on Wikidata, Stage 2 should roll up to the parent Batman comic series entity",
            ExpectedQid: "Q2633138"),

        new("Saga Chapter One", Writer: "Brian K. Vaughan",
            Series: "Saga", Number: 1, Year: 2012, Genre: "Science Fiction, Fantasy",
            Summary: "A new epic from the creators of Y: The Last Man.",
            Publisher: "Image Comics", Penciller: "Fiona Staples",
            TestCategory: "Comic — Image Comics, multi-genre"),

        new("The Sandman: Sleep of the Just", Writer: "Neil Gaiman",
            Series: "The Sandman", Number: 1, Year: 1989, Genre: "Fantasy, Horror",
            Summary: "Morpheus, the King of Dreams, is captured and held prisoner for 70 years.",
            Publisher: "DC Comics/Vertigo", Penciller: "Sam Kieth",
            TestCategory: "Comic — Neil Gaiman (cross-ref with Good Omens book)",
            ExpectIdentified: true),

        new("Akira Vol 1", Writer: "Katsuhiro Otomo",
            Series: "Akira", Number: 1, Year: 1982, Genre: "Science Fiction",
            Summary: "In the year 2019, Neo-Tokyo has risen from the ashes of World War III.",
            Publisher: "Kodansha", Penciller: "Katsuhiro Otomo",
            TestCategory: "Comic — manga, Japanese creator",
            ExpectIdentified: true),

        new("Batman: Year One Part 2", Writer: "Frank Miller",
            Series: "Batman", Number: 405, Year: 1987, Genre: "Superhero",
            Summary: "Jim Gordon and Bruce Wayne continue their first year in Gotham.",
            Publisher: "DC Comics", Penciller: "David Mazzucchelli",
            TestCategory: "Comic - consecutive issue, validates volume ordering"),

        new("Batman: Year One Part 3", Writer: "Frank Miller",
            Series: "Batman", Number: 406, Year: 1987, Genre: "Superhero",
            Summary: "Batman becomes a symbol while Gordon closes in on Gotham corruption.",
            Publisher: "DC Comics", Penciller: "David Mazzucchelli",
            TestCategory: "Comic - consecutive issue, validates volume ordering"),

        new("Batman: Year One Part 4", Writer: "Frank Miller",
            Series: "Batman", Number: 407, Year: 1987, Genre: "Superhero",
            Summary: "The first-year arc closes with Batman and Gordon finding their footing.",
            Publisher: "DC Comics", Penciller: "David Mazzucchelli",
            TestCategory: "Comic - consecutive issue, validates volume ordering"),

        new("Saga Chapter Two", Writer: "Brian K. Vaughan",
            Series: "Saga", Number: 2, Year: 2012, Genre: "Science Fiction, Fantasy",
            Summary: "Alana, Marko, and Hazel flee across a hostile galaxy.",
            Publisher: "Image Comics", Penciller: "Fiona Staples",
            TestCategory: "Comic - same series second issue, validates comic shelf grouping"),

        new("Saga Chapter Three", Writer: "Brian K. Vaughan",
            Series: "Saga", Number: 3, Year: 2012, Genre: "Science Fiction, Fantasy",
            Summary: "The chase intensifies as both sides hunt the new family.",
            Publisher: "Image Comics", Penciller: "Fiona Staples",
            TestCategory: "Comic - same series third issue, validates comic shelf grouping"),

        new("The Sandman: Imperfect Hosts", Writer: "Neil Gaiman",
            Series: "The Sandman", Number: 2, Year: 1989, Genre: "Fantasy, Horror",
            Summary: "Dream begins to recover the tools of his office.",
            Publisher: "DC Comics/Vertigo", Penciller: "Sam Kieth",
            TestCategory: "Comic - second Sandman issue, validates creator and series grouping",
            ExpectIdentified: true),
    ];

    // ── Supported test media types and their provider health-check URLs ────
    // Provider → media types it gates. If the provider's API endpoint is unreachable,
    // those media types are skipped with a reason in the response.

    private static readonly string[] AllTestableTypes = ["books", "audiobooks", "movies", "tv", "music", "comics"];

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
        // Apple can identify and enrich the deterministic music fixtures on its own.
        // MusicBrainz remains probed and reported, but a transient outage must not
        // remove the entire Music lane from a full integration run.
        ["music"] = ["apple_api"],
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

    /// <summary>
    /// Parse the <c>?types=books,comics,music</c> query parameter.
    /// Returns a normalised set of requested types (default: all).
    /// </summary>
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

    /// <summary>
    /// Probe each provider's API endpoint in parallel. Returns a dictionary of
    /// provider name → (healthy, reason). Timeout: 8 seconds per provider.
    /// Credentials are read from the loaded provider config (secrets applied) so no
    /// credentials are ever hardcoded here. A 2xx response is required — 401 is
    /// treated as "key missing or invalid", not as "healthy".
    /// </summary>
    private static async Task<Dictionary<string, (bool Healthy, string Reason)>> CheckProviderHealthAsync(
        ILogger logger,
        Domain.Contracts.IConfigurationLoader configLoader)
    {
        var results = new Dictionary<string, (bool, string)>(StringComparer.OrdinalIgnoreCase);
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TuvimaLibrary/1.0 (integration-test)");

        var tasks = ProviderHealthUrls.Select(async kvp =>
        {
            try
            {
                // Load provider config — secrets (api_key, username, password) are applied
                // automatically by ConfigurationDirectoryLoader.ApplySecrets().
                var providerConfig = configLoader.LoadProvider(kvp.Key);
                var http = providerConfig?.HttpClient;
                var delivery = http?.ApiKeyDelivery?.ToLowerInvariant() ?? "";

                // Resolve the effective health-check URL.
                // For query-param delivery, append the key directly to the URL.
                string healthUrl = kvp.Value;
                if (!string.IsNullOrWhiteSpace(http?.ApiKey) && delivery is "query" or "query_param")
                {
                    var paramName = string.IsNullOrWhiteSpace(http.ApiKeyParamName) ? "api_key" : http.ApiKeyParamName;
                    var separator = healthUrl.Contains('?') ? "&" : "?";
                    healthUrl = healthUrl + separator + paramName + "=" + Uri.EscapeDataString(http.ApiKey);
                }

                // Build a per-request message so auth headers don't bleed across providers.
                using var req = new HttpRequestMessage(HttpMethod.Get, healthUrl);

                if (http is not null)
                {
                    if (!string.IsNullOrWhiteSpace(http.ApiKey) && delivery == "bearer")
                    {
                        req.Headers.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", http.ApiKey);
                    }
                    else if (!string.IsNullOrWhiteSpace(http.Username) && !string.IsNullOrWhiteSpace(http.Password))
                    {
                        var creds = Convert.ToBase64String(
                            System.Text.Encoding.UTF8.GetBytes($"{http.Username}:{http.Password}"));
                        req.Headers.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", creds);
                    }
                }

                using var response = await httpClient.SendAsync(req);

                // A 2xx response confirms the endpoint is reachable AND the credentials work.
                // 401 → key missing or invalid; treat as unhealthy so the seed skips that type.
                bool ok = response.IsSuccessStatusCode;
                string reason = ok ? $"HTTP {(int)response.StatusCode}" : $"HTTP {(int)response.StatusCode} — key missing or invalid";
                return (kvp.Key, Healthy: ok, Reason: reason);
            }
            catch (Exception ex)
            {
                return (kvp.Key, Healthy: false, Reason: $"{ex.GetType().Name}: {ex.Message}");
            }
        });

        foreach (var result in await Task.WhenAll(tasks))
        {
            results[result.Key] = (result.Healthy, result.Reason);
            logger.LogInformation("[HealthCheck] {Provider}: {Status} ({Reason})", result.Key,
                result.Healthy ? "HEALTHY" : "UNAVAILABLE", result.Reason);
        }

        return results;
    }

    /// <summary>
    /// Given the requested types and provider health results, compute the set of
    /// types that are actually active (requested AND provider healthy).
    /// Also returns skip reasons for types that were excluded.
    /// </summary>
    private static (HashSet<string> ActiveTypes, Dictionary<string, string> SkipReasons) ResolveActiveTypes(
        HashSet<string> requestedTypes,
        Dictionary<string, (bool Healthy, string Reason)> health)
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

    // ── Endpoint registration ────────────────────────────────────────────────

    public static void MapDevSeedEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapDevelopmentGroup();

        group.MapGet("/check-keys", CheckKeysAsync)
            .WithSummary("Probe each configured provider with real credentials — confirms all API keys are valid before seeding");

        group.MapPost("/reset-and-seed", ResetAndSeedAsync)
            .WithSummary("Preserve application configuration, reset generated library data, create Standard or Stress fixtures, and queue normal ingestion");

        group.MapPost("/reset-library-data", ResetLibraryDataAsync)
            .WithSummary("Clear catalogued library, enrichment, artwork, and ingestion state while preserving application configuration and source media");

        group.MapPost("/factory-reset", FactoryResetAsync)
            .WithSummary("Reset the development database and generated state without deleting configured source media");

        group.MapPost("/seed-library", SeedLibraryAsync)
            .WithSummary($"Drop up to {SeedBooks.Length + SeedAudiobooks.Length + SeedVideos.Length + SeedMusicTracks.Length + SeedComics.Length} test files into Watch Folders (?types=books,comics,… to filter; providers health-checked automatically)");

        group.MapPost("/wipe", WipeAsync)
            .WithSummary("Wipe generated harness state by default; pass ?wipeScope=full for the dangerous full source wipe");

        group.MapPost("/full-test", FullTestAsync)
            .WithSummary("Wipe generated state -> seed test files -> scan fixtures (?types= to filter; ?wipe=false to skip reset)");

        group.MapPost("/reingest-library", ReingestLibraryAsync)
            .WithSummary("Development-only clean database/cache reset, then scan every configured library source path. FSW remains paused.");

        group.MapGet("/pipeline-status", PipelineStatusAsync)
            .WithSummary("Show identity job counts by state + details of non-Completed jobs");

        group.MapPost("/view-photo-harness", async (
                ViewPhotoHarnessService harness, CancellationToken ct) =>
            {
                var report = await harness.RunAsync(ct);
                return report.Passed ? Results.Ok(report) : Results.Json(report, statusCode: 422);
            })
            .WithSummary("Seed ten attributed photos plus 360 synthetic images with neighborhood and regional locations; update existing fixture coordinates and verify discovery");

        group.MapPost("/generate-ingestion-edge-fixtures", async (CancellationToken ct) =>
            {
                var root = Path.Combine(Path.GetTempPath(), $"tuvima-ingestion-edge-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
                return Results.Ok(await RealWorldEdgeFixtureBuilder.CreateAsync(root, ct));
            })
            .WithSummary("Generate a disposable manifest and tiny files mirroring real-world ingestion edge cases without importing them");
    }

    // ── GET /dev/check-keys ─────────────────────────────────────────────────

    private static async Task<IResult> CheckKeysAsync(
        Domain.Contracts.IConfigurationLoader configLoader,
        ILogger<Program> logger)
    {
        logger.LogInformation("[CheckKeys] Probing all configured provider API keys");

        var health = await CheckProviderHealthAsync(logger, configLoader);

        // Summarise per-provider with a human-readable diagnosis.
        var summary = health.ToDictionary(
            kvp => kvp.Key,
            kvp => new
            {
                status = kvp.Value.Healthy ? "OK" : "FAIL",
                reason = kvp.Value.Reason,
                affects = ProviderToTypes.TryGetValue(kvp.Key, out var types) ? types : Array.Empty<string>(),
            });

        bool allHealthy = health.Values.All(v => v.Healthy);
        string verdict = allHealthy
            ? "All provider keys verified — ready to seed."
            : "One or more providers failed. Fill in the missing keys in config/secrets/ before seeding.";

        return Results.Ok(new
        {
            verdict,
            all_healthy = allHealthy,
            providers = summary,
        });
    }

    // ── POST /dev/seed-library ───────────────────────────────────────────────

    private static async Task<IResult> ResetAndSeedAsync(
        HttpContext context,
        DevelopmentTestService developmentTests,
        string? fixtureSet = "standard")
    {
        DevelopmentFixtureSet selectedFixtureSet;
        if (!Enum.TryParse(fixtureSet, ignoreCase: true, out selectedFixtureSet))
        {
            return Results.BadRequest(new { error = "Fixture set must be 'standard' or 'stress'." });
        }

        try
        {
            var result = await developmentTests.ResetAndSeedAsync(
                ParseTypes(context),
                selectedFixtureSet,
                context.RequestAborted);
            return Results.Accepted(value: new
            {
                message = result.Message,
                files_created = result.FilesCreated,
                media_types = result.MediaTypes,
                fixture_set = result.FixtureSet,
                scanned_directories = result.ScannedDirectories,
                reset = MapResetResult(result.Reset),
            });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ResetLibraryDataAsync(
        DevelopmentTestService developmentTests,
        CancellationToken ct)
    {
        try
        {
            var reset = await developmentTests.ResetLibraryDataAsync(ct);
            return Results.Ok(new
            {
                message = "Library data reset. Application configuration and source media were preserved.",
                reset = MapResetResult(reset),
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> FactoryResetAsync(
        DevelopmentTestService developmentTests,
        CancellationToken ct)
    {
        try
        {
            var reset = await developmentTests.FactoryResetAsync(ct);
            return Results.Ok(new
            {
                message = "Development instance reset. Database and generated state were cleared; configured source media was preserved.",
                reset = MapResetResult(reset),
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> SeedLibraryAsync(
        HttpContext context,
        IOptions<IngestionOptions> options,
        Domain.Contracts.IConfigurationLoader configLoader,
        ILogger<Program> logger)
    {
        var requestedTypes = ParseTypes(context);
        var health = await CheckProviderHealthAsync(logger, configLoader);
        var (activeTypes, skipReasons) = ResolveActiveTypes(requestedTypes, health);

        var created = new List<string>();
        var createdPaths = new List<string>();
        var perTypeResults = new Dictionary<string, object>();
        int skipped = 0;

        // ── Seed EPUBs ──────────────────────────────────────────────────────
        var booksDir = ResolveWatchDirectory(configLoader, options, "Books");
        int booksCreated = 0;
        if (activeTypes.Contains("books") && !string.IsNullOrWhiteSpace(booksDir))
        {
            EnsureDirectory(booksDir, logger);
            foreach (SeedBook book in SeedBooks)
            {
                string fileName = $"{SanitizeFileName(book.Title)}.epub";
                string filePath = Path.Combine(booksDir, fileName);

                if (File.Exists(filePath)) { skipped++; continue; }

                byte[] epub = EpubBuilder.Create(
                    book.Title, book.Author, book.Isbn, book.Year, book.Description,
                    book.Publisher, book.Language, book.AdditionalAuthors,
                    book.Series, book.SeriesPosition);
                await File.WriteAllBytesAsync(filePath, epub);
                created.Add(fileName);
                createdPaths.Add(filePath);
                booksCreated++;
                logger.LogInformation("Seed EPUB created: {Path} [{Category}]", filePath, book.TestCategory ?? "Uncategorised");
            }
        }
        perTypeResults["books"] = activeTypes.Contains("books")
            ? new { total = SeedBooks.Length, created = booksCreated, directory = booksDir ?? "not configured" }
            : (object)new { total = SeedBooks.Length, created = 0, skipped_reason = skipReasons.GetValueOrDefault("books", "Excluded") };

        // ── Seed MP3 Audiobooks ─────────────────────────────────────────────
        // Audiobooks have their own Listen-lane library folder.
        int audiobooksCreated = 0;
        var audiobooksDir = ResolveWatchDirectory(configLoader, options, "Audiobooks");
        if (activeTypes.Contains("audiobooks") && !string.IsNullOrWhiteSpace(audiobooksDir))
        {
            foreach (SeedAudiobook ab in SeedAudiobooks)
            {
                var fixture = await CreateAudiobookFixtureAsync(audiobooksDir, ab, logger);
                created.AddRange(fixture.RelativePaths);
                createdPaths.AddRange(fixture.CreatedPaths);
                audiobooksCreated += fixture.Created;
                skipped += fixture.Skipped;
            }
        }
        perTypeResults["audiobooks"] = activeTypes.Contains("audiobooks")
            ? new
            {
                fixture_titles = SeedAudiobooks.Length,
                total_media_files = SeedAudiobooks.Sum(item => item.PartCount),
                created = audiobooksCreated,
                directory = audiobooksDir ?? "not configured",
                edge_cases = new[] { "multi-file audiobook", "ABS metadata.json sidecar", "ordered chapter files" }
            }
            : (object)new { total = SeedAudiobooks.Length, created = 0, skipped_reason = skipReasons.GetValueOrDefault("audiobooks", "Excluded") };

        // ── Seed MP4 Videos (Movies + TV) ─────────────────────────────────
        int moviesCreated = 0, tvCreated = 0;
        foreach (SeedVideo video in SeedVideos)
        {
            var typeKey = video.MediaType == "TV" ? "tv" : "movies";
            if (!activeTypes.Contains(typeKey))
            {
                continue;
            }

            var category = video.MediaType == "TV" ? "TV" : "Movies";
            var videoDir = ResolveWatchDirectory(configLoader, options, category);
            if (string.IsNullOrWhiteSpace(videoDir))
            {
                continue;
            }

            EnsureDirectory(videoDir, logger);

            string fileName;
            string filePath;
            if (!string.IsNullOrWhiteSpace(video.FileNameOverride))
            {
                // Allow nested relative paths (e.g. "Breaking Bad/Season 01/...mp4")
                // so the test harness can exercise filename patterns that depend on
                // parent-folder context (leading SxxExx, year suffix, etc.).
                var rel = video.FileNameOverride.Replace('/', Path.DirectorySeparatorChar);
                filePath = Path.Combine(videoDir, rel);
                fileName = Path.GetFileName(filePath);
                var parentDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    EnsureDirectory(parentDir, logger);
                }
            }
            else
            {
                if (video.MediaType == "TV" && video.SeasonNumber is not null && video.EpisodeNumber is not null)
                {
                    fileName = $"{SanitizeFileName(video.Series ?? video.Title)} S{video.SeasonNumber:D2}E{video.EpisodeNumber:D2}.mp4";
                }
                else
                {
                    fileName = $"{SanitizeFileName(video.Title)} ({video.Year}).mp4";
                }

                filePath = Path.Combine(videoDir, fileName);
            }

            if (File.Exists(filePath)) { skipped++; continue; }

            byte[] mp4 = Mp4Builder.Create(
                video.Title, video.Director, video.Year,
                showName: video.MediaType == "TV" ? video.Series : null,
                seasonNumber: video.SeasonNumber,
                episodeNumber: video.EpisodeNumber);
            await File.WriteAllBytesAsync(filePath, mp4);
            created.Add(fileName);
            createdPaths.Add(filePath);
            if (video.MediaType == "TV")
            {
                tvCreated++;
            }
            else
            {
                moviesCreated++;
            }

            logger.LogInformation("Seed MP4 created: {Path} [{Category}]", filePath, video.TestCategory ?? "Uncategorised");
        }
        var moviesDir = ResolveWatchDirectory(configLoader, options, "Movies");
        var tvDir = ResolveWatchDirectory(configLoader, options, "TV");
        perTypeResults["movies"] = activeTypes.Contains("movies")
            ? new { total = SeedVideos.Count(v => v.MediaType == "Movie"), created = moviesCreated, directory = moviesDir ?? "not configured" }
            : (object)new { total = SeedVideos.Count(v => v.MediaType == "Movie"), created = 0, skipped_reason = skipReasons.GetValueOrDefault("movies", "Excluded") };
        perTypeResults["tv"] = activeTypes.Contains("tv")
            ? new { total = SeedVideos.Count(v => v.MediaType == "TV"), created = tvCreated, directory = tvDir ?? "not configured", edge_cases = new[] { "flat and nested seasons", "release-quality suffix", "v2 episode marker" } }
            : (object)new { total = SeedVideos.Count(v => v.MediaType == "TV"), created = 0, skipped_reason = skipReasons.GetValueOrDefault("tv", "Excluded") };

        // ── Seed FLAC Music ───────────────────────────────────────────────
        var musicDir = ResolveWatchDirectory(configLoader, options, "Music");
        int musicCreated = 0;
        if (activeTypes.Contains("music") && !string.IsNullOrWhiteSpace(musicDir))
        {
            EnsureDirectory(musicDir, logger);
            foreach (SeedMusic track in SeedMusicTracks)
            {
                string fileName = $"{SanitizeFileName(track.Artist)} - {SanitizeFileName(track.Title)}.flac";
                string filePath = Path.Combine(musicDir, fileName);

                if (File.Exists(filePath)) { skipped++; continue; }

                byte[] flac = FlacBuilder.Create(
                    track.Title, track.Artist, track.Album,
                    track.Year, track.Genre, track.TrackNumber, track.AlbumArtist);
                await File.WriteAllBytesAsync(filePath, flac);
                created.Add(fileName);
                createdPaths.Add(filePath);
                musicCreated++;
                logger.LogInformation("Seed FLAC created: {Path} [{Category}]", filePath, track.TestCategory ?? "Uncategorised");
            }
        }
        perTypeResults["music"] = activeTypes.Contains("music")
            ? new { total = SeedMusicTracks.Length, created = musicCreated, directory = musicDir ?? "not configured", edge_cases = new[] { "album artist differs from track artist", "compilation-safe album grouping" } }
            : (object)new { total = SeedMusicTracks.Length, created = 0, skipped_reason = skipReasons.GetValueOrDefault("music", "Excluded") };

        // ── Seed CBZ Comics ──────────────────────────────────────────────
        var comicsDir = ResolveWatchDirectory(configLoader, options, "Comics");
        int comicsCreated = 0;
        if (activeTypes.Contains("comics") && !string.IsNullOrWhiteSpace(comicsDir))
        {
            EnsureDirectory(comicsDir, logger);
            foreach (SeedComic comic in SeedComics)
            {
                string fileName = $"{SanitizeFileName(comic.Title)}.cbz";
                string filePath = Path.Combine(comicsDir, fileName);

                if (File.Exists(filePath)) { skipped++; continue; }

                byte[] cbz = CbzBuilder.Create(
                    comic.Title, comic.Writer, comic.Series, comic.Number,
                    comic.Year, comic.Genre, comic.Summary, comic.Publisher, comic.Penciller);
                await File.WriteAllBytesAsync(filePath, cbz);
                created.Add(fileName);
                createdPaths.Add(filePath);
                comicsCreated++;
                logger.LogInformation("Seed CBZ created: {Path} [{Category}]", filePath, comic.TestCategory ?? "Uncategorised");
            }
        }
        perTypeResults["comics"] = activeTypes.Contains("comics")
            ? new { total = SeedComics.Length, created = comicsCreated, directory = comicsDir ?? "not configured" }
            : (object)new { total = SeedComics.Length, created = 0, skipped_reason = skipReasons.GetValueOrDefault("comics", "Excluded") };

        int totalSeeded = booksCreated + audiobooksCreated + moviesCreated + tvCreated + musicCreated + comicsCreated;
        int totalSeed = SeedBooks.Length + SeedAudiobooks.Length + SeedVideos.Length + SeedMusicTracks.Length + SeedComics.Length;
        string message = totalSeeded > 0
            ? $"{totalSeeded} files dropped into Watch Folders. Ingestion will begin automatically."
            : "All seed files already exist in the Watch Folders.";

        DevFixtureManifestStore.Record(options, createdPaths, logger);

        return Results.Ok(new
        {
            files_created = totalSeeded,
            files_skipped = skipped,
            total_seed_files = totalSeed,
            active_types = activeTypes.ToArray(),
            skipped_types = skipReasons,
            provider_health = health.ToDictionary(h => h.Key, h => h.Value.Healthy ? "healthy" : h.Value.Reason),
            per_type = perTypeResults,
            files = created,
            message
        });
    }

    // ── POST /dev/wipe ──────────────────────────────────────────────────────

    private static async Task<IResult> WipeAsync(
        DevHarnessResetService resetService,
        string? wipeScope = DevHarnessResetService.GeneratedStateScopeName,
        bool startEngineAfterWipe = true,
        CancellationToken ct = default)
    {
        DevHarnessWipeScope scope;
        try
        {
            scope = DevHarnessResetService.ParseScope(wipeScope);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        DevHarnessResetResult result;
        try
        {
            result = await resetService.WipeAsync(scope, startEngineAfterWipe, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        string scopeName = result.Scope == DevHarnessWipeScope.Full
            ? DevHarnessResetService.FullScopeName
            : DevHarnessResetService.GeneratedStateScopeName;

        return Results.Ok(new DevHarnessWipeResponse(
            startEngineAfterWipe
                ? $"Wipe complete ({scopeName}). Database reinitialized. Ready for seeding."
                : $"Wipe complete ({scopeName}). Database reinitialized. Watcher resume deferred.",
            scopeName,
            result.Details));
    }

    private static async Task<IResult> ReingestLibraryAsync(
        HttpContext context,
        Domain.Contracts.IConfigurationLoader configLoader,
        IIngestionEngine ingestionEngine,
        DevHarnessResetService resetService,
        ILogger<Program> logger)
    {
        logger.LogInformation("[ReingestLibrary] Preparing clean storage reset and source scan");

        var resetResult = await resetService.PrepareForReingestAsync(context.RequestAborted);
        var libConfig = configLoader.LoadLibraries();
        var scanTargets = libConfig.Libraries
            .SelectMany(lib =>
            {
                return lib.ScannableSources.Select(source => new IngestionScanTarget(
                    NormalizeDirectoryPath(source.Path),
                    source.IncludeSubdirectories));
            })
            .Where(target => Directory.Exists(target.Path))
            .GroupBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new IngestionScanTarget(
                group.Key,
                group.Any(target => target.IncludeSubdirectories)))
            .ToList();

        if (scanTargets.Count > 0)
        {
            await ingestionEngine.ScanDirectories(scanTargets, context.RequestAborted);
        }

        logger.LogInformation(
            "[ReingestLibrary] Reingest scan queued for {Count} configured source folder(s); FSW remains paused",
            scanTargets.Count);

        return Results.Ok(new DevHarnessReingestResponse(
            "Reingest initiated: database/generated state reset, configured source folders enqueued, FSW remains paused.",
            MapResetResult(resetResult),
            scanTargets.Select(target => target.Path).ToArray(),
            scanTargets.Count,
            true));
    }

    // ── POST /dev/full-test ─────────────────────────────────────────────────

    private static async Task<IResult> FullTestAsync(
        HttpContext context,
        IOptions<IngestionOptions> options,
        Domain.Contracts.IConfigurationLoader configLoader,
        IIngestionEngine ingestionEngine,
        DevHarnessResetService resetService,
        ILogger<Program> logger,
        bool wipe = true,
        string? wipeScope = DevHarnessResetService.GeneratedStateScopeName)
    {
        logger.LogInformation("[FullTest] Starting full ingestion test: {Mode}",
            wipe ? "wipe → seed → scan → start" : "seed → scan → start (no wipe)");

        var requestedTypesForScan = ParseTypes(context);
        var healthForScan = await CheckProviderHealthAsync(logger, configLoader);
        var (activeTypesForScan, _) = ResolveActiveTypes(requestedTypesForScan, healthForScan);

        // Step 1: reset (optional, default true). The reset service always pauses
        // the watcher before fixture files are seeded so ScanDirectory is the only
        // path that enqueues them.
        DevHarnessResetResult? wipeResult = null;
        if (wipe)
        {
            DevHarnessWipeScope scope;
            try
            {
                scope = DevHarnessResetService.ParseScope(wipeScope);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            wipeResult = await resetService.WipeAsync(scope, resumeWatcher: false, context.RequestAborted);
        }
        else
        {
            await resetService.PauseWatcherAsync(ct: context.RequestAborted).ConfigureAwait(false);
            logger.LogInformation("[FullTest] Wipe skipped (wipe=false); FSW paused before fixture seeding");
        }

        // ── Step 2: Seed files (FSW is NOT watching — no spurious events) ─────
        var seedResult = await SeedLibraryAsync(context, options, configLoader, logger);

        // ── Step 3: Enqueue each seeded file directly into the pipeline ────────
        // ScanDirectory bypasses the 30-second FSW quiet-period buffer because it
        // stamps each event with a BatchId before calling Enqueue. Files flow
        // directly into the debounce queue and are processed without any timing
        // ambiguity — no Task.Delay, no settle uncertainty.
        var libConfig = configLoader.LoadLibraries();
        var scanTargets = libConfig.Libraries
            .Where(lib =>
                activeTypesForScan.Contains(NormalizeHarnessMediaTypeKey(lib.Category))
                || lib.MediaTypes.Any(mt => activeTypesForScan.Contains(NormalizeHarnessMediaTypeKey(mt))))
            .SelectMany(lib =>
            {
                return lib.ScannableSources.Select(source => new IngestionScanTarget(
                    NormalizeDirectoryPath(source.Path),
                    source.IncludeSubdirectories));
            })
            .Where(target => Directory.Exists(target.Path))
            .GroupBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new IngestionScanTarget(
                group.Key,
                group.Any(target => target.IncludeSubdirectories)))
            .ToList();

        // Prefer the most specific watch folders and drop any broad parent path
        // that would only create duplicate empty batches during a full test run.
        scanTargets = scanTargets
            .Where(target => !scanTargets.Any(other =>
                !string.Equals(target.Path, other.Path, StringComparison.OrdinalIgnoreCase)
                && IsDirectoryAncestor(target.Path, other.Path)))
            .ToList();

        var scannedPaths = scanTargets.Select(target => target.Path).ToList();

        if (scanTargets.Count > 0)
        {
            await ingestionEngine.ScanDirectories(scanTargets, context.RequestAborted);
            foreach (var target in scanTargets)
            {
                logger.LogInformation("[FullTest] Grouped ScanDirectories target: {Path}", target.Path);
            }
        }

        // ── Step 4: Do NOT resume the FSW ─────────────────────────────────────
        // ScanDirectories already enqueued every seed file directly into the pipeline.
        // Resuming the FSW here causes a race: the watcher fires events for the
        // same files that are already being processed, and the lock probe fails
        // (the first processing attempt holds the file open), quarantining ~50
        // files. The FSW stays paused until the engine is restarted or a manual
        // POST /dev/resume-watcher is called.
        logger.LogInformation("[FullTest] FSW intentionally left paused — grouped ScanDirectories handles all seed files");

        return Results.Ok(new
        {
            message = wipe
                ? "Full test initiated: database wiped, library cleared, seed files enqueued directly into the pipeline (race-free), FSW paused."
                : "Full test initiated (no wipe): seed files enqueued directly into the pipeline (race-free), FSW paused.",
            wipe_performed = wipe,
            wipe = wipeResult is null ? null : MapResetResult(wipeResult),
            seed = seedResult,
            scanned_directories = scannedPaths,
            total_test_files = SeedBooks.Length + SeedAudiobooks.Length + SeedVideos.Length + SeedMusicTracks.Length + SeedComics.Length,
            next_steps = new[]
            {
                "Watch ingestion progress: GET /ingestion/queue-progress",
                    "Check library items: GET /dev/library-items?limit=50",
                "Check review queue: GET /review/pending",
                "Check activity: GET /activity/recent",
                "Monitor SignalR events: MediaAdded, MetadataHarvested, ReviewItemCreated"
            }
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes all files and subdirectories inside a directory, preserving the directory itself.
    /// Returns the number of items deleted.
    /// </summary>
    private static string NormalizeDirectoryPath(string path)
        => Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsDirectoryAncestor(string parent, string child)
    {
        var normalizedParent = NormalizeDirectoryPath(parent) + Path.DirectorySeparatorChar;
        var normalizedChild = NormalizeDirectoryPath(child) + Path.DirectorySeparatorChar;
        return normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the correct source directory for a media type by checking libraries.json.
    /// </summary>
    private static string? ResolveWatchDirectory(
        Domain.Contracts.IConfigurationLoader configLoader,
        IOptions<IngestionOptions> options,
        string mediaTypeCategory)
    {
        var libConfig = configLoader.LoadLibraries();

        // Find the library entry that matches this media type category.
        var lib = libConfig.Libraries.FirstOrDefault(l =>
            string.Equals(l.Category, mediaTypeCategory, StringComparison.OrdinalIgnoreCase));

        var destination = lib?.PrimaryDestination?.Path;
        if (!string.IsNullOrWhiteSpace(destination))
        {
            return destination;
        }

        return libConfig.Libraries
            .Select(library => library.PrimaryDestination?.Path)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
    }

    private static void EnsureDirectory(string path, ILogger logger)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            logger.LogInformation("Created seed target folder at {Path}", path);
        }
    }

    /// <summary>
    /// Removes characters that are invalid in file names.
    /// </summary>
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

    private sealed record AudiobookFixtureResult(
        int Created,
        int Skipped,
        IReadOnlyList<string> RelativePaths,
        IReadOnlyList<string> CreatedPaths);

    private static async Task<AudiobookFixtureResult> CreateAudiobookFixtureAsync(
        string root,
        SeedAudiobook audiobook,
        ILogger logger)
    {
        var created = 0;
        var skipped = 0;
        var relativePaths = new List<string>();
        var createdPaths = new List<string>();
        var directory = audiobook.PartCount > 1
            ? Path.Combine(root, SanitizeFileName(audiobook.Artist), SanitizeFileName(audiobook.Title))
            : root;
        EnsureDirectory(directory, logger);

        for (var part = 1; part <= audiobook.PartCount; part++)
        {
            var fileName = audiobook.PartCount > 1
                ? $"{part:D3} - Part {part:D2}.mp3"
                : $"{SanitizeFileName(audiobook.Title)} - {SanitizeFileName(audiobook.Narrator)}.mp3";
            var filePath = Path.Combine(directory, fileName);
            relativePaths.Add(Path.GetRelativePath(root, filePath));
            if (File.Exists(filePath))
            {
                skipped++;
                continue;
            }

            var trackTitle = audiobook.PartCount > 1 ? $"Part {part:D2}" : audiobook.Title;
            var bytes = Mp3Builder.Create(
                trackTitle,
                audiobook.Artist,
                album: audiobook.Title,
                narrator: audiobook.Narrator,
                year: audiobook.Year,
                language: audiobook.Language,
                series: audiobook.Series,
                seriesPosition: audiobook.SeriesPosition,
                asin: audiobook.Asin,
                trackNumber: audiobook.PartCount > 1 ? part : null);
            await File.WriteAllBytesAsync(filePath, bytes);
            created++;
            createdPaths.Add(filePath);
            logger.LogInformation("Seed MP3 created: {Path} [{Category}]", filePath, audiobook.TestCategory ?? "Uncategorised");
        }

        if (audiobook.IncludeAbsMetadataSidecar)
        {
            var sidecarPath = Path.Combine(directory, "metadata.json");
            if (!File.Exists(sidecarPath))
            {
                var sidecar = new
                {
                    title = audiobook.Title,
                    authors = new[] { audiobook.Artist },
                    narrators = new[] { audiobook.Narrator },
                    genres = new[] { "Science Fiction & Fantasy" },
                    publishedYear = audiobook.Year.ToString(),
                    asin = audiobook.Asin ?? "B-DEV-HARNESS",
                    language = audiobook.Language,
                    chapters = Enumerable.Range(1, audiobook.PartCount)
                        .Select(index => new { id = index - 1, start = 0, end = 1, title = $"Part {index:D2}" })
                        .ToArray(),
                };
                await File.WriteAllTextAsync(sidecarPath, JsonSerializer.Serialize(sidecar, MediaEngineJson.Indented));
                createdPaths.Add(sidecarPath);
                logger.LogInformation("Seed audiobook sidecar created: {Path}", sidecarPath);
            }
        }

        return new AudiobookFixtureResult(created, skipped, relativePaths, createdPaths);
    }

    // ── Seed expectation model ────────────────────────────────────────────────

    /// <summary>
    /// Flattened expectation derived from a seed fixture, used by the integration
    /// test reconciliation pass to compare expected vs. actual pipeline outcomes.
    /// </summary>
    public sealed record SeedExpectation(
        string Title,
        string MediaType,
        string? Author,
        bool ExpectIdentified,
        string? ExpectedReviewTrigger,
        string? ExpectedReason,
        string? ExpectedProvider = null,
        string? ExpectedQid = null,
        bool ExpectedCoverArt = true,
        string? ReconciliationTitle = null,
        bool KnownNoWikidataEntity = false)
    {
        public string ExpectedIdentityStatus =>
            ExpectIdentified
                ? (string.IsNullOrWhiteSpace(ExpectedQid) ? "Identified" : "ExactQid")
                : KnownNoWikidataEntity ? "KnownNoWikidataEntity" : "NeedsReview";
    }

    /// <summary>
    /// Seeds every canonical test fixture (Books, Audiobooks, Movies, TV, Music, Comics)
    /// into the configured Watch Folders. Mirrors the file-creation logic of
    /// <see cref="SeedLibraryAsync"/> but returns a simple file-count instead of an
    /// HTTP result so the integration-test harness can drive seeding without going
    /// through the HTTP endpoint.
    ///
    /// <para>
    /// Critical: this helper is the single source of truth for test-fixture seeding.
    /// IntegrationTestEndpoints must never maintain its own shadow copy of the seed
    /// arrays — doing so causes silent "NotFound" drift when fixtures are added to
    /// <see cref="DevSeedEndpoints"/> but not mirrored in the harness.
    /// </para>
    /// </summary>
    internal static async Task<int> SeedAllAsync(
        IOptions<IngestionOptions> options,
        IConfigurationLoader configLoader,
        HashSet<string> activeTypes,
        ILogger logger,
        DevelopmentFixtureSet fixtureSet = DevelopmentFixtureSet.Stress)
    {
        int created = 0;
        var createdPaths = new List<string>();

        // Books (EPUB)
        string? booksDir = ResolveWatchDirectory(configLoader, options, "Books");
        if (activeTypes.Contains("books") && !string.IsNullOrWhiteSpace(booksDir))
        {
            EnsureDirectory(booksDir, logger);
            foreach (SeedBook book in SeedBooks.Where(book => IncludeFixture(book, fixtureSet)))
            {
                string fileName = $"{SanitizeFileName(book.Title)}.epub";
                string filePath = Path.Combine(booksDir, fileName);
                if (File.Exists(filePath))
                {
                    continue;
                }

                byte[] bytes = EpubBuilder.Create(
                    book.Title, book.Author, book.Isbn, book.Year, book.Description,
                    book.Publisher, book.Language, book.AdditionalAuthors,
                    book.Series, book.SeriesPosition);
                await File.WriteAllBytesAsync(filePath, bytes);
                created++;
                createdPaths.Add(filePath);
            }
        }

        // Audiobooks (MP3) — use the dedicated Listen-lane destination.
        string? audiobooksDir = ResolveWatchDirectory(configLoader, options, "Audiobooks");
        if (activeTypes.Contains("audiobooks") && !string.IsNullOrWhiteSpace(audiobooksDir))
        {
            foreach (SeedAudiobook ab in SeedAudiobooks.Where(audiobook => IncludeFixture(audiobook, fixtureSet)))
            {
                var fixture = await CreateAudiobookFixtureAsync(audiobooksDir, ab, logger);
                created += fixture.Created;
                createdPaths.AddRange(fixture.CreatedPaths);
            }
        }

        // Movies + TV (MP4) — SeedVideos carries MediaType = "Movie" or "TV"
        foreach (SeedVideo video in SeedVideos.Where(video => IncludeFixture(video, fixtureSet)))
        {
            string typeKey = video.MediaType == "TV" ? "tv" : "movies";
            if (!activeTypes.Contains(typeKey))
            {
                continue;
            }

            string category = video.MediaType == "TV" ? "TV" : "Movies";
            string? videoDir = ResolveWatchDirectory(configLoader, options, category);
            if (string.IsNullOrWhiteSpace(videoDir))
            {
                continue;
            }

            EnsureDirectory(videoDir, logger);

            string fileName;
            string filePath;
            if (!string.IsNullOrWhiteSpace(video.FileNameOverride))
            {
                var rel = video.FileNameOverride.Replace('/', Path.DirectorySeparatorChar);
                filePath = Path.Combine(videoDir, rel);
                fileName = Path.GetFileName(filePath);
                var parentDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    EnsureDirectory(parentDir, logger);
                }
            }
            else
            {
                if (video.MediaType == "TV" && video.SeasonNumber is not null && video.EpisodeNumber is not null)
                {
                    fileName = $"{SanitizeFileName(video.Series ?? video.Title)} S{video.SeasonNumber:D2}E{video.EpisodeNumber:D2}.mp4";
                }
                else
                {
                    fileName = $"{SanitizeFileName(video.Title)} ({video.Year}).mp4";
                }

                filePath = Path.Combine(videoDir, fileName);
            }

            if (File.Exists(filePath))
            {
                continue;
            }

            byte[] bytes = Mp4Builder.Create(
                video.Title, video.Director, video.Year,
                showName: video.MediaType == "TV" ? video.Series : null,
                seasonNumber: video.SeasonNumber,
                episodeNumber: video.EpisodeNumber);
            await File.WriteAllBytesAsync(filePath, bytes);
            created++;
            createdPaths.Add(filePath);
        }

        // Music (FLAC)
        string? musicDir = ResolveWatchDirectory(configLoader, options, "Music");
        if (activeTypes.Contains("music") && !string.IsNullOrWhiteSpace(musicDir))
        {
            EnsureDirectory(musicDir, logger);
            foreach (SeedMusic track in SeedMusicTracks.Where(track => IncludeFixture(track, fixtureSet)))
            {
                string fileName = $"{SanitizeFileName(track.Artist)} - {SanitizeFileName(track.Title)}.flac";
                string filePath = Path.Combine(musicDir, fileName);
                if (File.Exists(filePath))
                {
                    continue;
                }

                byte[] bytes = FlacBuilder.Create(
                    track.Title, track.Artist, track.Album,
                    track.Year, track.Genre, track.TrackNumber, track.AlbumArtist);
                await File.WriteAllBytesAsync(filePath, bytes);
                created++;
                createdPaths.Add(filePath);
            }
        }

        // Comics (CBZ)
        string? comicsDir = ResolveWatchDirectory(configLoader, options, "Comics");
        if (activeTypes.Contains("comics") && !string.IsNullOrWhiteSpace(comicsDir))
        {
            EnsureDirectory(comicsDir, logger);
            foreach (SeedComic comic in SeedComics.Where(comic => IncludeFixture(comic, fixtureSet)))
            {
                string fileName = $"{SanitizeFileName(comic.Title)}.cbz";
                string filePath = Path.Combine(comicsDir, fileName);
                if (File.Exists(filePath))
                {
                    continue;
                }

                byte[] bytes = CbzBuilder.Create(
                    comic.Title, comic.Writer, comic.Series, comic.Number,
                    comic.Year, comic.Genre, comic.Summary, comic.Publisher, comic.Penciller);
                await File.WriteAllBytesAsync(filePath, bytes);
                created++;
                createdPaths.Add(filePath);
            }
        }

        DevFixtureManifestStore.Record(options, createdPaths, logger);

        logger.LogInformation("[SeedAllAsync] {Count} seed files written across active types {Types}",
            created, string.Join(",", activeTypes));

        // Allow file handles to fully release before the filesystem watcher fires.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Task.Delay(3000);

        return created;
    }

    private static bool IncludeFixture(SeedBook fixture, DevelopmentFixtureSet fixtureSet) =>
        fixtureSet == DevelopmentFixtureSet.Stress || StandardBookTitles.Contains(fixture.Title);

    private static bool IncludeFixture(SeedAudiobook fixture, DevelopmentFixtureSet fixtureSet) =>
        fixtureSet == DevelopmentFixtureSet.Stress
        || (string.Equals(fixture.Title, "Dune", StringComparison.Ordinal)
            && string.Equals(fixture.Narrator, "Simon Vance", StringComparison.Ordinal));

    private static bool IncludeFixture(SeedVideo fixture, DevelopmentFixtureSet fixtureSet) =>
        fixtureSet == DevelopmentFixtureSet.Stress
        || (string.Equals(fixture.MediaType, "Movie", StringComparison.Ordinal)
            && string.Equals(fixture.Title, "Blade Runner 2049", StringComparison.Ordinal))
        || (string.Equals(fixture.MediaType, "TV", StringComparison.Ordinal)
            && string.Equals(fixture.Series, "Breaking Bad", StringComparison.Ordinal)
            && fixture.SeasonNumber == 1
            && fixture.EpisodeNumber is 1 or 2
            && string.IsNullOrWhiteSpace(fixture.FileNameOverride));

    private static bool IncludeFixture(SeedMusic fixture, DevelopmentFixtureSet fixtureSet) =>
        fixtureSet == DevelopmentFixtureSet.Stress || StandardMusicTitles.Contains(fixture.Title);

    private static bool IncludeFixture(SeedComic fixture, DevelopmentFixtureSet fixtureSet) =>
        fixtureSet == DevelopmentFixtureSet.Stress || StandardComicTitles.Contains(fixture.Title);

    /// <summary>
    /// Returns all seed fixtures as a flat list of expectations.
    /// Called by IntegrationTestEndpoints to build its reconciliation pass without
    /// duplicating the seed data.
    /// </summary>
    public static IReadOnlyList<SeedExpectation> GetAllExpectations()
    {
        var result = new List<SeedExpectation>();

        // Books
        foreach (var b in SeedBooks)
        {
            result.Add(new SeedExpectation(
                Title: b.Title,
                MediaType: "Books",
                Author: b.Author,
                ExpectIdentified: b.ExpectIdentified,
                ExpectedReviewTrigger: b.ExpectedReviewTrigger,
                ExpectedReason: b.ExpectedReason,
                ExpectedProvider: b.ExpectedProvider,
                ExpectedQid: b.ExpectedQid,
                ExpectedCoverArt: b.ExpectedCoverArt,
                ReconciliationTitle: b.ReconciliationTitle));
        }

        // Audiobooks
        foreach (var a in SeedAudiobooks)
        {
            result.Add(new SeedExpectation(
                Title: a.Title,
                MediaType: "Audiobooks",
                Author: a.Artist,
                ExpectIdentified: a.ExpectIdentified,
                ExpectedReviewTrigger: a.ExpectedReviewTrigger,
                ExpectedReason: a.ExpectedReason,
                ExpectedProvider: a.ExpectedProvider,
                ExpectedQid: a.ExpectedQid,
                ExpectedCoverArt: a.ExpectedCoverArt));
        }

        // Videos — split by MediaType field
        foreach (var v in SeedVideos)
        {
            string mt = v.MediaType == "TV" ? "TV" : "Movies";
            result.Add(new SeedExpectation(
                Title: v.Title,
                MediaType: mt,
                Author: v.Director,
                ExpectIdentified: v.ExpectIdentified,
                ExpectedReviewTrigger: v.ExpectedReviewTrigger,
                ExpectedReason: v.ExpectedReason,
                ExpectedProvider: v.ExpectedProvider,
                ExpectedQid: v.ExpectedQid,
                ExpectedCoverArt: v.ExpectedCoverArt,
                ReconciliationTitle: v.ReconciliationTitle));
        }

        // Music
        foreach (var m in SeedMusicTracks)
        {
            result.Add(new SeedExpectation(
                Title: m.Title,
                MediaType: "Music",
                Author: m.Artist,
                ExpectIdentified: m.ExpectIdentified,
                ExpectedReviewTrigger: m.ExpectedReviewTrigger,
                ExpectedReason: m.ExpectedReason,
                ExpectedProvider: m.ExpectedProvider,
                ExpectedQid: m.ExpectedQid,
                ExpectedCoverArt: m.ExpectedCoverArt));
        }

        // Comics
        foreach (var c in SeedComics)
        {
            result.Add(new SeedExpectation(
                Title: c.Title,
                MediaType: "Comics",
                Author: c.Writer,
                ExpectIdentified: c.ExpectIdentified,
                ExpectedReviewTrigger: c.ExpectedReviewTrigger,
                ExpectedReason: c.ExpectedReason,
                ExpectedProvider: c.ExpectedProvider,
                ExpectedQid: c.ExpectedQid,
                ExpectedCoverArt: c.ExpectedCoverArt));
        }

        return result;
    }

    // ── GET /dev/pipeline-status ────────────────────────────────────────────

    private static async Task<IResult> PipelineStatusAsync(
        IIdentityJobRepository jobRepo,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var allStates = Enum.GetValues<IdentityJobState>();
        var counts = new Dictionary<string, int>();
        var nonTerminalDetails = new List<object>();

        foreach (var state in allStates)
        {
            var jobs = await jobRepo.GetByStateAsync(state, 500, ct);
            counts[state.ToString()] = jobs.Count;

            // Show details for states that still need worker or user action.
            if (state is not (IdentityJobState.Ready or IdentityJobState.ReadyWithoutUniverse) && jobs.Count > 0)
            {
                foreach (var job in jobs)
                {
                    nonTerminalDetails.Add(new
                    {
                        job.Id,
                        job.EntityId,
                        State = job.State,
                        job.AttemptCount,
                        job.LastError,
                        job.LeaseOwner,
                        job.LeaseExpiresAt,
                        job.ResolvedQid,
                        job.UpdatedAt
                    });
                }
            }
        }

        var total = counts.Values.Sum();
        logger.LogInformation("[PipelineStatus] {Total} total jobs: {Counts}",
            total, string.Join(", ", counts.Where(c => c.Value > 0).Select(c => $"{c.Key}={c.Value}")));

        return Results.Ok(new
        {
            total,
            counts,
            non_terminal_details = nonTerminalDetails
        });
    }

    private static DevHarnessResetResponse MapResetResult(DevHarnessResetResult source) => new(
        source.Scope == DevHarnessWipeScope.Full
            ? ContractWipeScope.Full
            : ContractWipeScope.GeneratedState,
        source.Details);
}
