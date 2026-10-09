// ──────────────────────────────────────────────────────────────────────────────
// GenerateTestEpubs — Creates a full test library spanning the configured
// watch roots and exercising every major ingestion edge case.
//
// Scenario groups:
//   EPUBs  1– 8  : Confidence gates, Collection? grouping, person records, pseudonyms
//   EPUB   9     : Corrupt file → quarantine
//   EPUB  10     : Duplicate hash → skip
//   M4Bs 11–16   : Cross-format Collection? link, narrators, orphanage, pseudonym
//   M4Bs 17–18   : Ingestion hinting — hp-series/ folder (sibling files)
//   M4Bs 19–20   : Ingestion hinting — expanse-audio/ folder (sibling files)
//   EPUBs 21–23  : Pseudonym (individual), no-ISBN search, title mismatch
//   EPUBs 24–26  : Foreign language metadata (Russian, Spanish, French)
//   EPUB  27     : Same-author different-work
//   EPUB  28     : Same-title different-edition disambiguation
//   EPUBs 29–30  : Multi-author works
//
// Usage:
//   dotnet run --project tools/GenerateTestEpubs [watch-root-or-books-directory] [--clean] [--large]
//
// Default output : C:\temp\tuvima-watch
// --clean        : Wipes the watch root before generating
// ──────────────────────────────────────────────────────────────────────────────

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

// ── Args ──────────────────────────────────────────────────────────────────────

bool clean = args.Any(a => a.Equals("--clean", StringComparison.OrdinalIgnoreCase));
bool large = args.Any(a => a.Equals("--large", StringComparison.OrdinalIgnoreCase));
var requestedOutputDir = args.FirstOrDefault(a => !a.StartsWith("--")) ?? @"C:\temp\tuvima-watch";
var normalizedRequestedOutputDir = Path.GetFullPath(requestedOutputDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
var watchRoot = string.Equals(Path.GetFileName(normalizedRequestedOutputDir), "books", StringComparison.OrdinalIgnoreCase)
    ? Directory.GetParent(normalizedRequestedOutputDir)?.FullName ?? normalizedRequestedOutputDir
    : normalizedRequestedOutputDir;
var booksDir = Path.Combine(watchRoot, "books");
var audiobooksDir = Path.Combine(watchRoot, "audiobooks");
var moviesDir = Path.Combine(watchRoot, "movies");
var tvDir = Path.Combine(watchRoot, "tv");
var musicDir = Path.Combine(watchRoot, "music");
var comicsDir = Path.Combine(watchRoot, "comics");
var generalDir = Path.Combine(watchRoot, "general");
var tempDir   = Path.Combine(Path.GetTempPath(), "tuvima-test-gen");
var ffmpegPath = FindFfmpeg();

// ── Clean ─────────────────────────────────────────────────────────────────────

if (clean && Directory.Exists(watchRoot))
{
    Console.WriteLine($"  --clean  Wiping {watchRoot}");
    Directory.Delete(watchRoot, recursive: true);
    Console.WriteLine();
}

Directory.CreateDirectory(booksDir);
Directory.CreateDirectory(audiobooksDir);
Directory.CreateDirectory(moviesDir);
Directory.CreateDirectory(tvDir);
Directory.CreateDirectory(musicDir);
Directory.CreateDirectory(comicsDir);
Directory.CreateDirectory(generalDir);
Directory.CreateDirectory(tempDir);

Console.WriteLine($"Watch root       : {watchRoot}");
Console.WriteLine($"Books            : {booksDir}");
Console.WriteLine($"Audiobooks       : {audiobooksDir}");
Console.WriteLine($"Movies           : {moviesDir}");
Console.WriteLine($"TV               : {tvDir}");
Console.WriteLine($"Music            : {musicDir}");
Console.WriteLine($"Comics           : {comicsDir}");
Console.WriteLine($"General          : {generalDir}");
Console.WriteLine($"Corpus           : {(large ? "large" : "standard")}");
Console.WriteLine($"FFmpeg           : {ffmpegPath ?? "NOT FOUND - M4B files will be skipped; MP4/MP3 use built-in fallback"}");
Console.WriteLine();

// ── EPUB definitions ─────────────────────────────────────────────────────────
//
// Scenarios 1-8. Output to the configured Books watch folder.
//
var epubs = new EpubSpec[]
{
    // Scenario 1 — Fully tagged: title, author, ISBN, series, embedded cover.
    //   Expected: auto-organized into library; "Dune" Collection? created; cover from file.
    new("dune.epub",
        "Dune",
        Author: "Frank Herbert",         SecondAuthor: null,
        Isbn: "9780441172719",           Year: "1965",
        Publisher: "Ace Books",
        Description: "A science fiction masterpiece set on the desert planet Arrakis.",
        Series: "Dune Chronicles",       SeriesPosition: "1",
        Language: "en",                  IncludeCover: true,
        CoverHex: "#B5651D"),

    // Scenario 2 — Rich metadata, no embedded cover art.
    //   Expected: Hub created; cover fetched from provider (Apple Books / Open Library).
    new("neuromancer.epub",
        "Neuromancer",
        Author: "William Gibson",        SecondAuthor: null,
        Isbn: "9780441569595",           Year: "1984",
        Publisher: "Ace Books",
        Description: "Case was the sharpest data-thief in the matrix.",
        Series: null,                    SeriesPosition: null,
        Language: "en",                  IncludeCover: false,
        CoverHex: "#2C6B3E"),

    // Scenario 3 — Author tagged as "Asimov, Isaac" (Last, First reversed format).
    //   Expected: author conflict flagged; two Person records may be created
    //   (one per format variant until Wikidata normalises to canonical name).
    new("foundation.epub",
        "Foundation",
        Author: "Asimov, Isaac",          SecondAuthor: null,
        Isbn: "9780553293357",            Year: "1951",
        Publisher: "Gnome Press",
        Description: "The Foundation series is set in the far future.",
        Series: "Foundation",            SeriesPosition: "1",
        Language: "en",                  IncludeCover: false,
        CoverHex: "#1A237E"),

    // Scenario 4 — Series + series_pos present in OPF metadata.
    //   Expected: `series` and `series_pos` canonical values correctly set on Work.
    new("the-name-of-the-wind.epub",
        "The Name of the Wind",
        Author: "Patrick Rothfuss",      SecondAuthor: null,
        Isbn: "9780756404079",           Year: "2007",
        Publisher: "DAW Books",
        Description: "I have stolen princesses back from sleeping barrow kings.",
        Series: "The Kingkiller Chronicle",  SeriesPosition: "1",
        Language: "en",                  IncludeCover: true,
        CoverHex: "#8B4513"),

    // Scenario 5 — Author is a collective pen name: "James S.A. Corey" resolves
    //   to two real people (Daniel Abraham + Ty Franck) via Wikidata P1773.
    //   Expected: collective pseudonym link; two real-author Person records.
    new("leviathan-wakes.epub",
        "Leviathan Wakes",
        Author: "James S.A. Corey",      SecondAuthor: null,
        Isbn: "9780316129084",           Year: "2011",
        Publisher: "Orbit",
        Description: "Humanity has colonized the solar system.",
        Series: "The Expanse",           SeriesPosition: "1",
        Language: "en",                  IncludeCover: true,
        CoverHex: "#1A237E"),

    // Scenario 6 — PSEUDONYM: "Richard Bachman" is Stephen King's pen name
    //   (Wikidata Q3324300 → P1773 → Q39829).
    //   Expected: Person record for Bachman linked to King via pseudonym.
    new("the-running-man.epub",
        "The Running Man",
        Author: "Richard Bachman",       SecondAuthor: null,
        Isbn: "9780451197962",           Year: "1982",
        Publisher: "Signet",
        Description: "A desperate man enters a deadly game show in a dystopian future.",
        Series: null,                    SeriesPosition: null,
        Language: "en",                  IncludeCover: true,
        CoverHex: "#8B0000"),

    // Scenario 7 — PSEUDONYM: "Robert Galbraith" is J.K. Rowling's pen name
    //   (Wikidata Q16308388 → P1773 → Q34660).
    //   Expected: Person record for Galbraith linked to Rowling via pseudonym.
    new("the-cuckoos-calling.epub",
        "The Cuckoo's Calling",
        Author: "Robert Galbraith",      SecondAuthor: null,
        Isbn: "9780316206846",           Year: "2013",
        Publisher: "Mulholland Books",
        Description: "A brilliant mystery novel featuring private detective Cormoran Strike.",
        Series: "Cormoran Strike",       SeriesPosition: "1",
        Language: "en",                  IncludeCover: true,
        CoverHex: "#2F4F4F"),

    // Scenario 8 — Filename only: OPF contains no usable metadata (all fields empty).
    //   Expected: overall confidence < 0.40 → file moved to .orphans/; review queue entry.
    new("phantom-signal-filename-only.epub",
        Title: "",                        Author: "",
        SecondAuthor: null,
        Isbn: "",                         Year: "",
        Publisher: "",                    Description: "",
        Series: null,                    SeriesPosition: null,
        Language: "en",                  IncludeCover: false,
        CoverHex: "#212121"),

    // Scenario 21 — INDIVIDUAL PEN NAME: "J.D. Robb" is Nora Roberts's crime-fiction pen name
    //   (Wikidata Q4808063 is the pseudonym entity, P1773 → Q231811 Nora Roberts).
    //   Unlike the collective pseudonym (James S.A. Corey), this is one real author behind one name.
    //   Expected: author audit finds pseudonym entity, emits "J.D. Robb" at confidence 1.0.
    //   The "author" canonical value should stay "J.D. Robb" (the published name on the cover).
    new("naked-in-death.epub",
        "Naked in Death",
        Author: "J.D. Robb",             SecondAuthor: null,
        Isbn: "9780425148990",           Year: "1995",
        Publisher: "Berkley Books",
        Description: "Detective Eve Dallas investigates a futuristic murder in New York.",
        Series: "In Death",              SeriesPosition: "1",
        Language: "en",                  IncludeCover: true,
        CoverHex: "#1B1B2F"),

    // Scenario 22 — NO ISBN: forces Tier 2 structured SPARQL title+author search.
    //   "Frankenstein" by Mary Shelley is a well-known work with a clear Wikidata entry
    //   (Q192676) but an 1818 publication date means no ISBN exists in most embedded metadata.
    //   Expected: Tier 2 search resolves Q192676; no bridge lookup used.
    new("frankenstein.epub",
        "Frankenstein",
        Author: "Mary Shelley",          SecondAuthor: null,
        Isbn: "",                        Year: "1818",
        Publisher: "Lackington, Hughes, Harding, Mavor & Jones",
        Description: "The modern Prometheus creates life and faces the consequences.",
        Series: null,                    SeriesPosition: null,
        Language: "en",                  IncludeCover: false,
        CoverHex: "#1A1A1A"),

    // Scenario 23 — TITLE MISMATCH / DISAMBIGUATION: EPUB title is "1984" but Wikidata
    //   calls the work "Nineteen Eighty-Four" (Q208592). No ISBN — forces title search.
    //   "1984" as a search term returns many candidates (year references, other works).
    //   Expected: Tier 2 search with author cross-check finds Q208592; no ISBN bridge used.
    //   Verifies that the search service matches "1984" ↔ "Nineteen Eighty-Four" correctly.
    new("nineteen-eighty-four.epub",
        "1984",
        Author: "George Orwell",         SecondAuthor: null,
        Isbn: "",                        Year: "1949",
        Publisher: "Secker & Warburg",
        Description: "Big Brother is watching you. A dystopian novel of totalitarian surveillance.",
        Series: null,                    SeriesPosition: null,
        Language: "en",                  IncludeCover: true,
        CoverHex: "#0A0A0A"),

    // ── Foreign Language Metadata ────────────────────────────────────────

    // Scenario 24 — FOREIGN LANGUAGE: Russian title and author in Cyrillic.
    //   <dc:language>ru</dc:language> triggers LanguageMismatch review.
    //   Expected: routed to review queue with "File metadata is in Russian" detail.
    new("war-and-peace.epub",
        "Ð’Ð¾Ð¹Ð½Ð° Ð¸ Ð¼Ð¸Ñ€",
        Author: "Ð›ÐµÐ² Ð¢Ð¾Ð»ÑÑ‚Ð¾Ð¹",              SecondAuthor: null,
        Isbn: "9780140447934",               Year: "1869",
        Publisher: "Penguin Classics",
        Description: "Ð­Ð¿Ð¸Ñ‡ÐµÑÐºÐ¸Ð¹ Ñ€Ð¾Ð¼Ð°Ð½ Ð¾ Ñ€ÑƒÑÑÐºÐ¾Ð¼ Ð¾Ð±Ñ‰ÐµÑÑ‚Ð²Ðµ Ð²Ð¾ Ð²Ñ€ÐµÐ¼Ñ Ð½Ð°Ð¿Ð¾Ð»ÐµÐ¾Ð½Ð¾Ð²ÑÐºÐ¸Ñ… Ð²Ð¾Ð¹Ð½.",
        Series: null,                        SeriesPosition: null,
        Language: "ru",                      IncludeCover: true,
        CoverHex: "#8D6E63"),

    // Scenario 25 — FOREIGN LANGUAGE: Spanish title.
    //   <dc:language>es</dc:language> triggers LanguageMismatch review.
    //   Expected: routed to review queue with "File metadata is in Spanish" detail.
    new("don-quijote.epub",
        "Don Quijote de la Mancha",
        Author: "Miguel de Cervantes",       SecondAuthor: null,
        Isbn: "9788420412146",               Year: "1605",
        Publisher: "Real Academia Española",
        Description: "La historia del ingenioso hidalgo Don Quijote de la Mancha.",
        Series: null,                        SeriesPosition: null,
        Language: "es",                      IncludeCover: true,
        CoverHex: "#D84315"),

    // Scenario 26 — FOREIGN LANGUAGE: French title.
    //   <dc:language>fr</dc:language> triggers LanguageMismatch review.
    //   Expected: routed to review queue with "File metadata is in French" detail.
    new("les-trois-mousquetaires.epub",
        "Les Trois Mousquetaires",
        Author: "Alexandre Dumas",           SecondAuthor: null,
        Isbn: "9782070409228",               Year: "1844",
        Publisher: "Gallimard",
        Description: "Les aventures de d'Artagnan et des trois mousquetaires.",
        Series: null,                        SeriesPosition: null,
        Language: "fr",                      IncludeCover: true,
        CoverHex: "#1565C0"),

    // ── Same-Author Different-Work ───────────────────────────────────────

    // Scenario 27 — SAME AUTHOR: George Orwell also wrote #23 (1984).
    //   Expected: separate Work, same Person record as scenario 23.
    new("animal-farm.epub",
        "Animal Farm",
        Author: "George Orwell",             SecondAuthor: null,
        Isbn: "9780451526342",               Year: "1945",
        Publisher: "Secker & Warburg",
        Description: "A satirical allegory of Soviet totalitarianism.",
        Series: null,                        SeriesPosition: null,
        Language: "en",                      IncludeCover: true,
        CoverHex: "#33691E"),

    // ── Title Disambiguation ─────────────────────────────────────────────

    // Scenario 28 — SAME TITLE DIFFERENT EDITION: "Foundation" with different ISBN than #3.
    //   Expected: different Edition under same Work; ISBN mismatch may trigger review.
    new("foundation-del-rey.epub",
        "Foundation",
        Author: "Isaac Asimov",              SecondAuthor: null,
        Isbn: "9780553803716",               Year: "1951",
        Publisher: "Del Rey",
        Description: "The first novel in Asimov's Foundation series, Del Rey edition.",
        Series: "Foundation",                SeriesPosition: "1",
        Language: "en",                      IncludeCover: false,
        CoverHex: "#283593"),

    // ── Multi-Author ─────────────────────────────────────────────────────

    // Scenario 29 — MULTI-AUTHOR: Two <dc:creator> entries in OPF.
    //   Expected: two Person records created (Pratchett + Gaiman), both linked.
    new("good-omens.epub",
        "Good Omens",
        Author: "Terry Pratchett",           SecondAuthor: "Neil Gaiman",
        Isbn: "9780060853983",               Year: "1990",
        Publisher: "Workman Publishing",
        Description: "The Nice and Accurate Prophecies of Agnes Nutter, Witch.",
        Series: null,                        SeriesPosition: null,
        Language: "en",                      IncludeCover: true,
        CoverHex: "#FFD54F"),

    // Scenario 30 — MULTI-AUTHOR: Stephen King + Peter Straub collaboration.
    //   Expected: two Person records; King already exists from #6 (Bachman pseudonym).
    new("the-talisman.epub",
        "The Talisman",
        Author: "Stephen King",              SecondAuthor: "Peter Straub",
        Isbn: "9781501192272",               Year: "1984",
        Publisher: "Viking Press",
        Description: "A boy's epic quest across two parallel worlds.",
        Series: null,                        SeriesPosition: null,
        Language: "en",                      IncludeCover: true,
        CoverHex: "#4A148C"),
};

// ── Generate EPUBs 1–8, 21–30 ────────────────────────────────────────────────

int total = 0, failed = 0;
var manifest = new List<ManifestEntry>();
var generatedFiles = new List<(string Temp, string Final)>();
var expectedPeople = new[]
{
    new ExpectedPersonEntry(
        "Frank Herbert",
        ExpectedWikidataQid: "Q7934",
        MinimumOwnedCredits: 5,
        MinimumMediaItems: 5,
        ExpectedMediaTypes: ["Books", "Audiobooks", "Movies"],
        ExpectedTitles: ["Dune", "Dune Messiah", "Children of Dune", "Dune: Part One", "Dune: Part Two"],
        RequireBiography: true,
        RequireHeadshot: true,
        Note: "Creator appears across Dune EPUBs, the Dune audiobook, and Dune movie adaptations."),
    new ExpectedPersonEntry(
        "Denis Villeneuve",
        ExpectedWikidataQid: "Q212961",
        MinimumOwnedCredits: 3,
        MinimumMediaItems: 3,
        ExpectedMediaTypes: ["Movies"],
        ExpectedTitles: ["Dune: Part One", "Dune: Part Two", "Arrival"],
        RequireBiography: true,
        RequireHeadshot: true,
        Note: "Director appears across the Dune movie series and standalone Arrival fixture."),
    new ExpectedPersonEntry(
        "J. R. R. Tolkien",
        ExpectedWikidataQid: "Q892",
        MinimumOwnedCredits: 3,
        MinimumMediaItems: 3,
        ExpectedMediaTypes: ["Movies"],
        ExpectedTitles:
        [
            "The Lord of the Rings: The Fellowship of the Ring",
            "The Lord of the Rings: The Two Towers",
            "The Lord of the Rings: The Return of the King"
        ],
        RequireBiography: true,
        RequireHeadshot: true,
        Note: "Creator appears across the Middle-earth movie series."),
    new ExpectedPersonEntry(
        "George Orwell",
        ExpectedWikidataQid: "Q3335",
        MinimumOwnedCredits: 2,
        MinimumMediaItems: 2,
        ExpectedMediaTypes: ["Books"],
        ExpectedTitles: ["1984", "Animal Farm"],
        RequireBiography: true,
        RequireHeadshot: true,
        Note: "Same author, different works."),
    new ExpectedPersonEntry(
        "J.K. Rowling",
        ExpectedWikidataQid: "Q34660",
        MinimumOwnedCredits: 2,
        MinimumMediaItems: 2,
        ExpectedMediaTypes: ["Audiobooks"],
        ExpectedTitles: ["Harry Potter and the Philosopher's Stone", "Harry Potter and the Chamber of Secrets"],
        RequireBiography: true,
        RequireHeadshot: true,
        Note: "Same author across sibling audiobook files."),
    new ExpectedPersonEntry(
        "James S.A. Corey",
        ExpectedWikidataQid: "Q6142591",
        MinimumOwnedCredits: 3,
        MinimumMediaItems: 3,
        ExpectedMediaTypes: ["Books", "Audiobooks"],
        ExpectedTitles: ["Leviathan Wakes", "Caliban's War"],
        RequireBiography: true,
        RequireHeadshot: false,
        Note: "Collective pen name appears across EPUB and audiobook fixtures."),
    new ExpectedPersonEntry(
        "Jefferson Mays",
        ExpectedWikidataQid: null,
        MinimumOwnedCredits: 2,
        MinimumMediaItems: 2,
        ExpectedMediaTypes: ["Audiobooks"],
        ExpectedTitles: ["Leviathan Wakes", "Caliban's War"],
        RequireBiography: true,
        RequireHeadshot: true,
        Note: "Narrator appears across multiple Expanse audiobooks."),
    new ExpectedPersonEntry(
        "Vince Gilligan",
        ExpectedWikidataQid: "Q310285",
        MinimumOwnedCredits: 2,
        MinimumMediaItems: 2,
        ExpectedMediaTypes: ["TV"],
        ExpectedTitles: ["Breaking Bad: Pilot", "Breaking Bad: Cat's in the Bag"],
        RequireBiography: true,
        RequireHeadshot: true,
        Note: "Creator appears across multiple TV episodes in a series."),
    new ExpectedPersonEntry(
        "Aaron Paul",
        ExpectedWikidataQid: "Q302491",
        MinimumOwnedCredits: 2,
        MinimumMediaItems: 2,
        ExpectedMediaTypes: ["TV"],
        ExpectedTitles: ["Breaking Bad: Pilot", "Breaking Bad: Cat's in the Bag"],
        RequireBiography: true,
        RequireHeadshot: true,
        Note: "Actor appears across multiple Breaking Bad episode fixtures and should be reconciled through Wikidata/Wikipedia enrichment."),
    new ExpectedPersonEntry(
        "Anna Gunn",
        ExpectedWikidataQid: "Q271050",
        MinimumOwnedCredits: 2,
        MinimumMediaItems: 2,
        ExpectedMediaTypes: ["TV"],
        ExpectedTitles: ["Breaking Bad: Pilot", "Breaking Bad: Cat's in the Bag"],
        RequireBiography: true,
        RequireHeadshot: true,
        Note: "Actor appears across multiple Breaking Bad episode fixtures and should be reconciled through Wikidata/Wikipedia enrichment."),
    new ExpectedPersonEntry(
        "David Bowie",
        ExpectedWikidataQid: "Q5383",
        MinimumOwnedCredits: 4,
        MinimumMediaItems: 4,
        ExpectedMediaTypes: ["Music"],
        ExpectedTitles: ["Five Years", "Soul Love", "Beauty and the Beast", "Heroes"],
        RequireBiography: true,
        RequireHeadshot: true,
        Note: "Artist appears across multiple owned albums so artist shelves and More by can be validated."),
    new ExpectedPersonEntry(
        "Alan Moore",
        ExpectedWikidataQid: "Q183581",
        MinimumOwnedCredits: 2,
        MinimumMediaItems: 2,
        ExpectedMediaTypes: ["Comics"],
        ExpectedTitles: ["Watchmen"],
        RequireBiography: true,
        RequireHeadshot: true,
        Note: "Writer appears across multiple comic issues in a series."),
    new ExpectedPersonEntry(
        "Dave Gibbons",
        ExpectedWikidataQid: "Q313350",
        MinimumOwnedCredits: 2,
        MinimumMediaItems: 2,
        ExpectedMediaTypes: ["Comics"],
        ExpectedTitles: ["Watchmen"],
        RequireBiography: true,
        RequireHeadshot: true,
        Note: "Artist appears across multiple comic issues in a series."),
};

Console.WriteLine($"━━━ EPUBs (scenarios 1–8, 21–30) ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
string? duneEpubPath = null;

// Scenarios 1–8 are indices 0–7; scenarios 21–23 are indices 8–10; scenarios 24–30 are indices 11–17.
// Map index → scenario number for the manifest and console output.
static int EpubScenarioNum(int index) => index switch
{
    < 8 => index + 1,         // 0–7  → scenarios 1–8
    < 11 => 21 + (index - 8), // 8–10 → scenarios 21–23
    _ => 24 + (index - 11),   // 11–17 → scenarios 24–30
};

for (int i = 0; i < epubs.Length; i++)
{
    var spec    = epubs[i];
    var outPath = Path.Combine(tempDir, spec.FileName);
    var finalPath = Path.Combine(booksDir, spec.FileName);
    var num     = EpubScenarioNum(i);
    try
    {
        byte[]? cover = null;
        if (spec.IncludeCover && ffmpegPath is not null)
        {
            cover = GeneratePng(ffmpegPath, tempDir, spec.CoverHex, 400, 600);
        }

        CreateEpub(outPath, spec, cover);
        if (spec.FileName == "dune.epub")
        {
            duneEpubPath = outPath;
        }
        generatedFiles.Add((outPath, finalPath));

        var label = $"[{num,2}] {spec.FileName,-46}";
        Console.WriteLine($"  ✓  {label} {(cover is not null ? "[cover]" : "[no cover]")}");
        manifest.Add(new(num, spec.FileName, "epub", $"Scenario {num}"));
        total++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗  [{num,2}] {spec.FileName}: {ex.Message}");
        failed++;
    }
}

// ── Scenario 9 — Corrupt EPUB ────────────────────────────────────────────────
//   File has .epub extension but contains garbage bytes (not a valid ZIP).
//   Expected: processor returns IsCorrupt=true; no asset in DB; MediaFailed activity.
{
    const int num = 9;
    var outPath = Path.Combine(tempDir, "corrupt-epub.epub");
    var finalPath = Path.Combine(booksDir, "corrupt-epub.epub");
    try
    {
        // Valid ZIP magic is PK\x03\x04; we write garbage that will fail ZIP parsing.
        var garbage = new byte[512];
        new Random(42).NextBytes(garbage);
        garbage[0] = 0xFF; garbage[1] = 0xFE;   // deliberate non-ZIP header
        File.WriteAllBytes(outPath, garbage);
        generatedFiles.Add((outPath, finalPath));

        Console.WriteLine($"  ✓  [{num,2}] {"corrupt-epub.epub",-46} [corrupt bytes — not a valid ZIP]");
        manifest.Add(new(num, "corrupt-epub.epub", "epub", "Scenario 9 — corrupt"));
        total++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗  [{num,2}] corrupt-epub.epub: {ex.Message}");
        failed++;
    }
}

// ── Scenario 10 — Duplicate (byte-identical copy of dune.epub) ───────────────
//   Expected: hash check catches duplicate before processor runs; DuplicateSkipped
//   activity logged; no second asset created in DB.
{
    const int num = 10;
    var outPath = Path.Combine(tempDir, "dune-duplicate.epub");
    var finalPath = Path.Combine(booksDir, "dune-duplicate.epub");
    try
    {
        if (duneEpubPath is not null && File.Exists(duneEpubPath))
        {
            File.Copy(duneEpubPath, outPath, overwrite: true);
            generatedFiles.Add((outPath, finalPath));
            Console.WriteLine($"  ✓  [{num,2}] {"dune-duplicate.epub",-46} [byte-identical copy of dune.epub]");
            manifest.Add(new(num, "dune-duplicate.epub", "epub", "Scenario 10 — duplicate"));
            total++;
        }
        else
        {
            Console.WriteLine($"  ✗  [{num,2}] dune-duplicate.epub: dune.epub was not generated — cannot copy");
            failed++;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗  [{num,2}] dune-duplicate.epub: {ex.Message}");
        failed++;
    }
}

// ── Extra linked book-series fixtures ───────────────────────────────────────
// Scenarios 31-32 expand the Dune book series so collection ordering can be
// tested across several books, while scenario 11 remains the matching audiobook
// for scenario 1.
var extraBookSeries = new (int Scenario, EpubSpec Spec)[]
{
    new(31, new EpubSpec("dune-messiah.epub",
        "Dune Messiah",
        Author: "Frank Herbert", SecondAuthor: null,
        Isbn: "9780593098233", Year: "1969",
        Publisher: "Ace Books",
        Description: "The second novel in the Dune Chronicles.",
        Series: "Dune Chronicles", SeriesPosition: "2",
        Language: "en", IncludeCover: true,
        CoverHex: "#7A4A24")),

    new(32, new EpubSpec("children-of-dune.epub",
        "Children of Dune",
        Author: "Frank Herbert", SecondAuthor: null,
        Isbn: "9780441104024", Year: "1976",
        Publisher: "Ace Books",
        Description: "The third novel in the Dune Chronicles.",
        Series: "Dune Chronicles", SeriesPosition: "3",
        Language: "en", IncludeCover: true,
        CoverHex: "#8A6F2A")),
};

Console.WriteLine();
Console.WriteLine($"━━━ Extra EPUB series fixtures (scenarios 31-32) ━━━━━━━━━━━━━━━━━━━━━━━━━");
foreach (var (num, spec) in extraBookSeries)
{
    var outPath = Path.Combine(tempDir, spec.FileName);
    var finalPath = Path.Combine(booksDir, spec.FileName);
    try
    {
        byte[]? cover = null;
        if (spec.IncludeCover && ffmpegPath is not null)
        {
            cover = GeneratePng(ffmpegPath, tempDir, spec.CoverHex, 400, 600);
        }

        CreateEpub(outPath, spec, cover);
        generatedFiles.Add((outPath, finalPath));
        Console.WriteLine($"  ✓  [{num,2}] {spec.FileName,-46} {(cover is not null ? "[cover]" : "[no cover]")}");
        manifest.Add(new(num, spec.FileName, "epub", $"Scenario {num} — Dune book series"));
        total++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗  [{num,2}] {spec.FileName}: {ex.Message}");
        failed++;
    }
}
// ── M4B definitions ──────────────────────────────────────────────────────────
//
// Scenarios 11-16 output to the configured Books/Audiobooks watch folder.
// Scenarios 17–18 output to hp-series/ subfolder  (ingestion hinting).
// Scenarios 19–20 output to expanse-audio/ subfolder (ingestion hinting).
//
// Audiobook covers MUST be square (1:1 aspect ratio).

var m4bsFlat = new M4bSpec[]
{
    // Scenario 11 — Same title, author, and series as dune.epub (#1).
    //   Expected: joins the existing Dune Collection? — no new Collection created.
    new("dune-audiobook.m4b",
        Title: "Dune",
        Artist: "Frank Herbert",         AlbumArtist: "Frank Herbert",
        Album: "Dune",                   Narrator: "Scott Brick",
        Year: "1965",                    Genre: "Science Fiction",
        Comment: "Narrated by Scott Brick",
        TrackNum: "1",
        Series: "Dune Chronicles",       SeriesPos: "1",
        IncludeCover: false,             CoverHex: "#B5651D"),

    // Scenario 12 — Narrator credited in ID3 comment tag.
    //   Expected: Narrator Person record created for Stephen Fry.
    new("hitchhikers-guide.m4b",
        Title: "The Hitchhiker's Guide to the Galaxy",
        Artist: "Douglas Adams",         AlbumArtist: "Douglas Adams",
        Album: "Hitchhiker's Guide to the Galaxy",
        Narrator: "Stephen Fry",
        Year: "1979",                    Genre: "Science Fiction",
        Comment: "Narrated by Stephen Fry",
        TrackNum: "1",
        Series: "The Hitchhiker's Guide to the Galaxy",
        SeriesPos: "1",
        IncludeCover: true,              CoverHex: "#0097A7"),

    // Scenario 13 — Narrator field contains two names joined by " and ".
    //   Expected: two separate Narrator Person records (one per name).
    new("wool-omnibus.m4b",
        Title: "Wool",
        Artist: "Hugh Howey",            AlbumArtist: "Hugh Howey",
        Album: "Wool Omnibus",           Narrator: "Amanda Donahoe and Tim Gerard Reynolds",
        Year: "2012",                    Genre: "Science Fiction",
        Comment: "Narrated by Amanda Donahoe and Tim Gerard Reynolds",
        TrackNum: "1",
        Series: "Silo",                  SeriesPos: "1",
        IncludeCover: true,              CoverHex: "#4E342E"),

    // Scenario 14 — Audiobook with series, no embedded cover.
    //   Expected: Hub created; cover fetched from provider (Audnexus / Apple Books).
    new("enders-game.m4b",
        Title: "Ender's Game",
        Artist: "Orson Scott Card",      AlbumArtist: "Orson Scott Card",
        Album: "Ender's Game",           Narrator: "Stefan Rudnicki",
        Year: "1985",                    Genre: "Science Fiction",
        Comment: "Narrated by Stefan Rudnicki",
        TrackNum: "1",
        Series: "Ender's Saga",          SeriesPos: "1",
        IncludeCover: false,             CoverHex: "#006064"),

    // Scenario 15 — No ID3 tags at all (filename-only audiobook).
    //   Expected: overall confidence < 0.40 → .orphans/ quarantine; review entry.
    new("echoes-filename-only.m4b",
        Title: "",                        Artist: "",
        AlbumArtist: "",                 Album: "",
        Narrator: "",                    Year: "",
        Genre: "",                        Comment: "",
        TrackNum: "",
        Series: null,                    SeriesPos: null,
        IncludeCover: false,             CoverHex: "#212121"),

    // Scenario 16 — PSEUDONYM: "Iain Banks" has pen name "Iain M. Banks"
    //   (Wikidata Q14469 → P742 → Q214540).
    //   Expected: pseudonym link discovered; both Person records linked.
    new("the-wasp-factory.m4b",
        Title: "The Wasp Factory",
        Artist: "Iain Banks",            AlbumArtist: "Iain Banks",
        Album: "The Wasp Factory",       Narrator: "Peter Kenny",
        Year: "1984",                    Genre: "Fiction",
        Comment: "Narrated by Peter Kenny",
        TrackNum: "1",
        Series: null,                    SeriesPos: null,
        IncludeCover: true,              CoverHex: "#4A0E0E"),
};

// ── Ingestion hinting — hp-series/ subdirectory ───────────────────────────────
//
// Both files go into the same source subfolder.  The Engine primes a FolderHint
// when the first file is ingested, then applies it to the second — skipping a
// redundant Stage 1 SPARQL query and pre-assigning the second file to the same Collection.

var hpSubdir = Path.Combine(audiobooksDir, "hp-series");
var tempHpSubdir = Path.Combine(tempDir, "hp-series");
Directory.CreateDirectory(tempHpSubdir);
var m4bsHpSeries = new M4bSpec[]
{
    // Scenario 17 — Harry Potter #1. First file in hp-series/ folder.
    //   Expected: full three-stage pipeline; ingestion hint primed with HP Collection? ID + QID.
    new("harry-potter-philosophers-stone.m4b",
        Title: "Harry Potter and the Philosopher's Stone",
        Artist: "J.K. Rowling",          AlbumArtist: "J.K. Rowling",
        Album: "Harry Potter",           Narrator: "Jim Dale",
        Year: "1997",                    Genre: "Fantasy",
        Comment: "Narrated by Jim Dale",
        TrackNum: "1",
        Series: "Harry Potter",          SeriesPos: "1",
        IncludeCover: true,              CoverHex: "#7B1FA2"),

    // Scenario 18 — Harry Potter #2. Sibling in hp-series/ folder.
    //   Expected: FolderHint applied from #17; Collection? pre-assigned; Stage 1 SPARQL skipped.
    new("harry-potter-chamber-of-secrets.m4b",
        Title: "Harry Potter and the Chamber of Secrets",
        Artist: "J.K. Rowling",          AlbumArtist: "J.K. Rowling",
        Album: "Harry Potter",           Narrator: "Jim Dale",
        Year: "1998",                    Genre: "Fantasy",
        Comment: "Narrated by Jim Dale",
        TrackNum: "2",
        Series: "Harry Potter",          SeriesPos: "2",
        IncludeCover: true,              CoverHex: "#558B2F"),
};

// ── Ingestion hinting — expanse-audio/ subdirectory ──────────────────────────

var expanseSubdir = Path.Combine(audiobooksDir, "expanse-audio");
var tempExpanseSubdir = Path.Combine(tempDir, "expanse-audio");
Directory.CreateDirectory(tempExpanseSubdir);
var m4bsExpanse = new M4bSpec[]
{
    // Scenario 19 — The Expanse #1. First file in expanse-audio/ folder.
    //   Expected: full pipeline; hint primed with Expanse Collection? ID + bridge IDs.
    new("leviathan-wakes-audio.m4b",
        Title: "Leviathan Wakes",
        Artist: "James S.A. Corey",      AlbumArtist: "James S.A. Corey",
        Album: "The Expanse",            Narrator: "Jefferson Mays",
        Year: "2011",                    Genre: "Science Fiction",
        Comment: "Narrated by Jefferson Mays",
        TrackNum: "1",
        Series: "The Expanse",           SeriesPos: "1",
        IncludeCover: true,              CoverHex: "#0D47A1"),

    // Scenario 20 — The Expanse #2. Sibling in expanse-audio/ folder.
    //   Expected: FolderHint applied from #19; same Collection; Stage 1 SPARQL skipped.
    new("calibans-war-audio.m4b",
        Title: "Caliban's War",
        Artist: "James S.A. Corey",      AlbumArtist: "James S.A. Corey",
        Album: "The Expanse",            Narrator: "Jefferson Mays",
        Year: "2012",                    Genre: "Science Fiction",
        Comment: "Narrated by Jefferson Mays",
        TrackNum: "2",
        Series: "The Expanse",           SeriesPos: "2",
        IncludeCover: true,              CoverHex: "#1565C0"),
};

// ── Generate M4Bs ─────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine($"━━━ M4Bs flat (scenarios 11–16) ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

if (ffmpegPath is null)
{
    Console.WriteLine("  ✗  FFmpeg not found — cannot create M4B files.");
    Console.WriteLine("     Run: powershell -ExecutionPolicy Bypass -File tools/Download-FFmpeg.ps1");
    failed += m4bsFlat.Length + m4bsHpSeries.Length + m4bsExpanse.Length;
}
else
{
    foreach (var (spec, idx) in m4bsFlat.Select((s, i) => (s, i)))
    {
        var num      = 11 + idx;
        var outPath  = Path.Combine(tempDir, spec.FileName);
        var finalPath = Path.Combine(audiobooksDir, spec.FileName);
        try
        {
            byte[]? cover = spec.IncludeCover ? GeneratePng(ffmpegPath, tempDir, spec.CoverHex, 400, 400) : null;
            CreateM4b(ffmpegPath, tempDir, outPath, spec, cover);
            generatedFiles.Add((outPath, finalPath));
            Console.WriteLine($"  ✓  [{num,2}] {spec.FileName,-46} {(cover is not null ? "[sq cover]" : "[no cover]")}");
            manifest.Add(new(num, Path.Combine("audiobooks", spec.FileName).Replace('\\', '/'), "m4b", $"Scenario {num}"));
            total++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗  [{num,2}] {spec.FileName}: {ex.Message}");
            failed++;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"━━━ M4Bs hp-series/ (scenarios 17–18, ingestion hinting) ━━━━━━━━━━━━━━━━━━━");
    Directory.CreateDirectory(hpSubdir);

    foreach (var (spec, idx) in m4bsHpSeries.Select((s, i) => (s, i)))
    {
        var num      = 17 + idx;
        var outPath  = Path.Combine(tempHpSubdir, spec.FileName);
        var finalPath = Path.Combine(hpSubdir, spec.FileName);
        try
        {
            byte[]? cover = spec.IncludeCover ? GeneratePng(ffmpegPath, tempDir, spec.CoverHex, 400, 400) : null;
            CreateM4b(ffmpegPath, tempDir, outPath, spec, cover);
            generatedFiles.Add((outPath, finalPath));
            Console.WriteLine($"  ✓  [{num,2}] hp-series/{spec.FileName,-38} {(cover is not null ? "[sq cover]" : "[no cover]")}");
            manifest.Add(new(num, Path.Combine("audiobooks", "hp-series", spec.FileName).Replace('\\', '/'), "m4b", $"Scenario {num}"));
            total++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗  [{num,2}] hp-series/{spec.FileName}: {ex.Message}");
            failed++;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"━━━ M4Bs expanse-audio/ (scenarios 19–20, ingestion hinting) ━━━━━━━━━━━━━━");
    Directory.CreateDirectory(expanseSubdir);

    foreach (var (spec, idx) in m4bsExpanse.Select((s, i) => (s, i)))
    {
        var num      = 19 + idx;
        var outPath  = Path.Combine(tempExpanseSubdir, spec.FileName);
        var finalPath = Path.Combine(expanseSubdir, spec.FileName);
        try
        {
            byte[]? cover = spec.IncludeCover ? GeneratePng(ffmpegPath, tempDir, spec.CoverHex, 400, 400) : null;
            CreateM4b(ffmpegPath, tempDir, outPath, spec, cover);
            generatedFiles.Add((outPath, finalPath));
            Console.WriteLine($"  ✓  [{num,2}] expanse-audio/{spec.FileName,-34} {(cover is not null ? "[sq cover]" : "[no cover]")}");
            manifest.Add(new(num, Path.Combine("audiobooks", "expanse-audio", spec.FileName).Replace('\\', '/'), "m4b", $"Scenario {num}"));
            total++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗  [{num,2}] expanse-audio/{spec.FileName}: {ex.Message}");
            failed++;
        }
    }
}

// ── Movie-series fixtures ──────────────────────────────────────────────────
// Scenarios 33-39 add real movie shapes with IMDb bridge IDs in the
// path. The video processor reads filename/year, and OrganizationHintParser
// seeds the bridge identifiers for provider matching.
var movieSeries = new VideoSpec[]
{
    new(33, "dune-films", "Dune Part One (2021) {imdb-tt1160419}.mp4", "Dune: Part One", "2021", "Science Fiction", "#6D4C41"),
    new(34, "dune-films", "Dune Part Two (2024) {imdb-tt15239678}.mp4", "Dune: Part Two", "2024", "Science Fiction", "#A66A2E"),
    new(35, "middle-earth", "The Lord of the Rings The Fellowship of the Ring (2001) {imdb-tt0120737}.mp4", "The Lord of the Rings: The Fellowship of the Ring", "2001", "Fantasy", "#2E5E3F"),
    new(36, "middle-earth", "The Lord of the Rings The Two Towers (2002) {imdb-tt0167261}.mp4", "The Lord of the Rings: The Two Towers", "2002", "Fantasy", "#455A64"),
    new(37, "middle-earth", "The Lord of the Rings The Return of the King (2003) {imdb-tt0167260}.mp4", "The Lord of the Rings: The Return of the King", "2003", "Fantasy", "#795548"),
    new(38, "standalone", "Arrival (2016) {imdb-tt2543164}.mp4", "Arrival", "2016", "Science Fiction", "#1565C0"),
    new(39, "standalone", "The Shawshank Redemption (1994) {imdb-tt0111161}.mp4", "The Shawshank Redemption", "1994", "Drama", "#5D4037"),
};

Console.WriteLine();
Console.WriteLine($"━━━ MP4 movie fixtures (scenarios 33-39) ━━━━━━━━━━━━━━━━━━━━━━━━━━");
foreach (var spec in movieSeries)
{
    var tempMovieDir = Path.Combine(tempDir, spec.Subdir);
    var finalMovieDir = Path.Combine(moviesDir, spec.Subdir);
    Directory.CreateDirectory(tempMovieDir);
    var outPath = Path.Combine(tempMovieDir, spec.FileName);
    var finalPath = Path.Combine(finalMovieDir, spec.FileName);
    try
    {
        var usedFallback = CreateMp4Fixture(ffmpegPath, outPath, spec);
        generatedFiles.Add((outPath, finalPath));
        var displayPath = Path.Combine(spec.Subdir, spec.FileName).Replace('\\', '/');
        Console.WriteLine($"  ✓  [{spec.Scenario,2}] {displayPath}{(usedFallback ? " [fallback]" : "")}");
        manifest.Add(new(spec.Scenario, displayPath, "mp4", $"Scenario {spec.Scenario} — movie series"));
        total++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗  [{spec.Scenario,2}] {spec.FileName}: {ex.Message}");
        failed++;
    }
}
// ── Batch copy to output ───────────────────────────────────────────────
var tvSeries = new VideoSpec[]
{
    new(40, Path.Combine("breaking-bad", "Season 01"), "Breaking Bad S01E01 Pilot (2008) {imdb-tt0959621}.mp4", "Breaking Bad: Pilot", "2008", "Drama", "#2E7D32"),
    new(41, Path.Combine("breaking-bad", "Season 01"), "Breaking Bad S01E02 Cat's in the Bag (2008) {imdb-tt1054724}.mp4", "Breaking Bad: Cat's in the Bag", "2008", "Drama", "#33691E"),
    new(42, Path.Combine("shogun-2024", "Season 01"), "Shogun S01E01 Anjin (2024).mp4", "Shogun: Anjin", "2024", "Drama", "#7B1FA2"),
};

var musicTracks = new MusicSpec[]
{
    new(43, Path.Combine("David Bowie", "The Rise and Fall of Ziggy Stardust"), "01 Five Years.mp3", "Five Years", "David Bowie", "The Rise and Fall of Ziggy Stardust and the Spiders from Mars", "1972", "Rock", "1"),
    new(44, Path.Combine("David Bowie", "The Rise and Fall of Ziggy Stardust"), "02 Soul Love.mp3", "Soul Love", "David Bowie", "The Rise and Fall of Ziggy Stardust and the Spiders from Mars", "1972", "Rock", "2"),
    new(48, Path.Combine("David Bowie", "Heroes"), "01 Beauty and the Beast.mp3", "Beauty and the Beast", "David Bowie", "Heroes", "1977", "Rock", "1"),
    new(49, Path.Combine("David Bowie", "Heroes"), "03 Heroes.mp3", "Heroes", "David Bowie", "Heroes", "1977", "Rock", "3"),
    new(50, Path.Combine("The Beatles", "Abbey Road"), "01 Come Together.mp3", "Come Together", "The Beatles", "Abbey Road", "1969", "Rock", "1"),
    new(51, Path.Combine("The Beatles", "Abbey Road"), "02 Something.mp3", "Something", "The Beatles", "Abbey Road", "1969", "Rock", "2"),
    new(52, Path.Combine("Kendrick Lamar", "DAMN"), "01 BLOOD.mp3", "BLOOD.", "Kendrick Lamar", "DAMN.", "2017", "Hip-Hop", "1"),
    new(53, Path.Combine("Hans Zimmer", "Interstellar"), "01 Dreaming of the Crash.mp3", "Dreaming of the Crash", "Hans Zimmer", "Interstellar: Original Motion Picture Soundtrack", "2014", "Soundtrack", "1"),
};

var comics = new ComicSpec[]
{
    new(45, "watchmen", "Watchmen 001 (1986).cbz", "Watchmen", "1", "Alan Moore", "Dave Gibbons", "1986", "Watchmen #1: At Midnight, All the Agents..."),
    new(46, "watchmen", "Watchmen 002 (1986).cbz", "Watchmen", "2", "Alan Moore", "Dave Gibbons", "1986", "Watchmen #2: Absent Friends"),
};

Console.WriteLine();
Console.WriteLine("TV fixtures (scenarios 40-42)");
foreach (var spec in tvSeries)
{
    var tempTvDir = Path.Combine(tempDir, "tv", spec.Subdir);
    var finalTvDir = Path.Combine(tvDir, spec.Subdir);
    Directory.CreateDirectory(tempTvDir);
    var outPath = Path.Combine(tempTvDir, spec.FileName);
    var finalPath = Path.Combine(finalTvDir, spec.FileName);
    try
    {
        var usedFallback = CreateMp4Fixture(ffmpegPath, outPath, spec);
        generatedFiles.Add((outPath, finalPath));
        var displayPath = Path.Combine("tv", spec.Subdir, spec.FileName).Replace('\\', '/');
        Console.WriteLine($"  [{spec.Scenario,2}] {displayPath}{(usedFallback ? " [fallback]" : "")}");
        manifest.Add(new(spec.Scenario, displayPath, "mp4", $"Scenario {spec.Scenario} - TV episode"));
        total++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [{spec.Scenario,2}] {spec.FileName}: {ex.Message}");
        failed++;
    }
}

Console.WriteLine();
Console.WriteLine("Music fixtures (scenarios 43-44 and 48-53)");
foreach (var spec in musicTracks)
{
    var tempMusicDir = Path.Combine(tempDir, "music", spec.Subdir);
    var finalMusicDir = Path.Combine(musicDir, spec.Subdir);
    Directory.CreateDirectory(tempMusicDir);
    var outPath = Path.Combine(tempMusicDir, spec.FileName);
    var finalPath = Path.Combine(finalMusicDir, spec.FileName);
    try
    {
        var usedFallback = CreateMp3Fixture(ffmpegPath, outPath, spec);
        generatedFiles.Add((outPath, finalPath));
        var displayPath = Path.Combine("music", spec.Subdir, spec.FileName).Replace('\\', '/');
        Console.WriteLine($"  [{spec.Scenario,2}] {displayPath}{(usedFallback ? " [fallback]" : "")}");
        manifest.Add(new(spec.Scenario, displayPath, "mp3", $"Scenario {spec.Scenario} - music track"));
        total++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [{spec.Scenario,2}] {spec.FileName}: {ex.Message}");
        failed++;
    }
}

Console.WriteLine();
Console.WriteLine("Comic fixtures (scenarios 45-46)");
foreach (var spec in comics)
{
    var tempComicDir = Path.Combine(tempDir, "comics", spec.Subdir);
    var finalComicDir = Path.Combine(comicsDir, spec.Subdir);
    Directory.CreateDirectory(tempComicDir);
    var outPath = Path.Combine(tempComicDir, spec.FileName);
    var finalPath = Path.Combine(finalComicDir, spec.FileName);
    try
    {
        CreateCbz(outPath, spec);
        generatedFiles.Add((outPath, finalPath));
        var displayPath = Path.Combine("comics", spec.Subdir, spec.FileName).Replace('\\', '/');
        Console.WriteLine($"  [{spec.Scenario,2}] {displayPath}");
        manifest.Add(new(spec.Scenario, displayPath, "cbz", $"Scenario {spec.Scenario} - comic issue"));
        total++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [{spec.Scenario,2}] {spec.FileName}: {ex.Message}");
        failed++;
    }
}

Console.WriteLine();
Console.WriteLine("General drop fixture (scenario 47)");
{
    const int num = 47;
    var outPath = Path.Combine(tempDir, "unsorted-field-note.txt");
    var finalPath = Path.Combine(generalDir, "unsorted-field-note.txt");
    try
    {
        File.WriteAllText(outPath, "Tuvima Library general drop-zone smoke fixture.");
        generatedFiles.Add((outPath, finalPath));
        Console.WriteLine($"  [{num,2}] unsorted-field-note.txt");
        manifest.Add(new(num, "unsorted-field-note.txt", "txt", "Scenario 47 - general drop zone"));
        total++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [{num,2}] unsorted-field-note.txt: {ex.Message}");
        failed++;
    }
}

if (large)
{
    Console.WriteLine();
    Console.WriteLine("Large corpus fixtures (scenarios 1000+)");

    var largeBooks = new (int Scenario, EpubSpec Spec)[]
    {
        new(1000, new("large-the-shining.epub", "The Shining", "Stephen King", null, "9780307743657", "1977", "Doubleday", "A haunted hotel, a blocked writer, and a family under pressure.", null, null, "en", true, "#7F1D1D")),
        new(1001, new("large-doctor-sleep.epub", "Doctor Sleep", "Stephen King", null, "9781476727653", "2013", "Scribner", "Danny Torrance faces a cult that feeds on children who shine.", null, null, "en", true, "#1F2937")),
        new(1002, new("large-it.epub", "It", "Stephen King", null, "9781501142970", "1986", "Viking", "A group of friends confronts an ancient evil in Derry.", null, null, "en", true, "#991B1B")),
        new(1003, new("large-do-androids-dream.epub", "Do Androids Dream of Electric Sheep?", "Philip K. Dick", null, "9780345404473", "1968", "Doubleday", "Rick Deckard hunts androids in a post-apocalyptic future.", null, null, "en", true, "#374151")),
        new(1004, new("large-foundation-and-empire.epub", "Foundation and Empire", "Isaac Asimov", null, "9780553293371", "1952", "Gnome Press", "The second book in the Foundation series.", "Foundation", "2", "en", false, "#1D4ED8")),
        new(1005, new("large-second-foundation.epub", "Second Foundation", "Isaac Asimov", null, "9780553293364", "1953", "Gnome Press", "The third book in the Foundation series.", "Foundation", "3", "en", false, "#2563EB")),
        new(1006, new("large-the-hobbit.epub", "The Hobbit", "J. R. R. Tolkien", null, "9780547928227", "1937", "George Allen & Unwin", "Bilbo Baggins joins a quest to reclaim Erebor.", "Middle-earth", "1", "en", true, "#166534")),
        new(1007, new("large-fellowship-book.epub", "The Fellowship of the Ring", "J. R. R. Tolkien", null, "9780618574940", "1954", "George Allen & Unwin", "The first volume of The Lord of the Rings.", "The Lord of the Rings", "1", "en", true, "#14532D")),
        new(1008, new("large-two-towers-book.epub", "The Two Towers", "J. R. R. Tolkien", null, "9780618574957", "1954", "George Allen & Unwin", "The second volume of The Lord of the Rings.", "The Lord of the Rings", "2", "en", true, "#334155")),
        new(1009, new("large-return-king-book.epub", "The Return of the King", "J. R. R. Tolkien", null, "9780618574971", "1955", "George Allen & Unwin", "The final volume of The Lord of the Rings.", "The Lord of the Rings", "3", "en", true, "#78350F")),
        new(1010, new("large-the-martian.epub", "The Martian", "Andy Weir", null, "9780553418026", "2011", "Crown", "An astronaut must survive alone on Mars.", null, null, "en", true, "#EA580C")),
        new(1011, new("large-project-hail-mary.epub", "Project Hail Mary", "Andy Weir", null, "9780593135204", "2021", "Ballantine", "A lone astronaut wakes with a mission to save Earth.", null, null, "en", true, "#0E7490")),
        new(1012, new("large-game-of-thrones.epub", "A Game of Thrones", "George R. R. Martin", null, "9780553103540", "1996", "Bantam", "The first book in A Song of Ice and Fire.", "A Song of Ice and Fire", "1", "en", true, "#7C2D12")),
        new(1013, new("large-clash-of-kings.epub", "A Clash of Kings", "George R. R. Martin", null, "9780553108033", "1998", "Bantam", "The second book in A Song of Ice and Fire.", "A Song of Ice and Fire", "2", "en", true, "#1E3A8A")),
        new(1014, new("large-the-last-of-us.epub", "The Last of Us", "Neil Druckmann", null, "", "2013", "Dark Horse", "A tie-in story fixture for cross-media creator matching.", null, null, "en", false, "#065F46")),
        new(1015, new("large-murderbot-all-systems-red.epub", "All Systems Red", "Martha Wells", null, "9780765397539", "2017", "Tor.com", "The first Murderbot Diaries novella.", "The Murderbot Diaries", "1", "en", true, "#4338CA")),
    };

    var largeAudiobooks = new (int Scenario, string Subdir, M4bSpec Spec)[]
    {
        new(1020, "stephen-king", new("the-shining-audio.m4b", "The Shining", "Stephen King", "Stephen King", "The Shining", "Campbell Scott", "1977", "Horror", "Narrated by Campbell Scott", "1", null, null, true, "#7F1D1D")),
        new(1021, "stephen-king", new("doctor-sleep-audio.m4b", "Doctor Sleep", "Stephen King", "Stephen King", "Doctor Sleep", "Will Patton", "2013", "Horror", "Narrated by Will Patton", "1", null, null, true, "#1F2937")),
        new(1022, "middle-earth-audio", new("the-hobbit-audio.m4b", "The Hobbit", "J. R. R. Tolkien", "J. R. R. Tolkien", "The Hobbit", "Andy Serkis", "1937", "Fantasy", "Narrated by Andy Serkis", "1", "Middle-earth", "1", true, "#166534")),
        new(1023, "middle-earth-audio", new("fellowship-audio.m4b", "The Fellowship of the Ring", "J. R. R. Tolkien", "J. R. R. Tolkien", "The Lord of the Rings", "Andy Serkis", "1954", "Fantasy", "Narrated by Andy Serkis", "1", "The Lord of the Rings", "1", true, "#14532D")),
        new(1024, "andy-weir-audio", new("the-martian-audio.m4b", "The Martian", "Andy Weir", "Andy Weir", "The Martian", "R. C. Bray", "2011", "Science Fiction", "Narrated by R. C. Bray", "1", null, null, true, "#EA580C")),
        new(1025, "andy-weir-audio", new("project-hail-mary-audio.m4b", "Project Hail Mary", "Andy Weir", "Andy Weir", "Project Hail Mary", "Ray Porter", "2021", "Science Fiction", "Narrated by Ray Porter", "1", null, null, true, "#0E7490")),
        new(1026, "asoiaf-audio", new("game-of-thrones-audio.m4b", "A Game of Thrones", "George R. R. Martin", "George R. R. Martin", "A Song of Ice and Fire", "Roy Dotrice", "1996", "Fantasy", "Narrated by Roy Dotrice", "1", "A Song of Ice and Fire", "1", true, "#7C2D12")),
        new(1027, "murderbot-audio", new("all-systems-red-audio.m4b", "All Systems Red", "Martha Wells", "Martha Wells", "The Murderbot Diaries", "Kevin R. Free", "2017", "Science Fiction", "Narrated by Kevin R. Free", "1", "The Murderbot Diaries", "1", true, "#4338CA")),
    };

    var largeMovies = new VideoSpec[]
    {
        new(1040, "stephen-king", "The Shining (1980) {imdb-tt0081505}.mp4", "The Shining", "1980", "Horror", "#7F1D1D"),
        new(1041, "stephen-king", "Doctor Sleep (2019) {imdb-tt5606664}.mp4", "Doctor Sleep", "2019", "Horror", "#1F2937"),
        new(1042, "blade-runner", "Blade Runner (1982) {imdb-tt0083658}.mp4", "Blade Runner", "1982", "Science Fiction", "#111827"),
        new(1043, "blade-runner", "Blade Runner 2049 (2017) {imdb-tt1856101}.mp4", "Blade Runner 2049", "2017", "Science Fiction", "#92400E"),
        new(1044, "dune-films", "Dune (1984) {imdb-tt0087182}.mp4", "Dune", "1984", "Science Fiction", "#92400E"),
        new(1045, "middle-earth", "The Hobbit An Unexpected Journey (2012) {imdb-tt0903624}.mp4", "The Hobbit: An Unexpected Journey", "2012", "Fantasy", "#166534"),
        new(1046, "middle-earth", "The Hobbit The Desolation of Smaug (2013) {imdb-tt1170358}.mp4", "The Hobbit: The Desolation of Smaug", "2013", "Fantasy", "#7C2D12"),
        new(1047, "harry-potter-films", "Harry Potter and the Philosophers Stone (2001) {imdb-tt0241527}.mp4", "Harry Potter and the Philosopher's Stone", "2001", "Fantasy", "#7B1FA2"),
        new(1048, "harry-potter-films", "Harry Potter and the Chamber of Secrets (2002) {imdb-tt0295297}.mp4", "Harry Potter and the Chamber of Secrets", "2002", "Fantasy", "#558B2F"),
        new(1049, "andy-weir", "The Martian (2015) {imdb-tt3659388}.mp4", "The Martian", "2015", "Science Fiction", "#EA580C"),
        new(1050, "nolan", "Interstellar (2014) {imdb-tt0816692}.mp4", "Interstellar", "2014", "Science Fiction", "#0F172A"),
        new(1051, "nolan", "Oppenheimer (2023) {imdb-tt15398776}.mp4", "Oppenheimer", "2023", "Drama", "#7C2D12"),
        new(1052, "batman-nolan", "Batman Begins (2005) {imdb-tt0372784}.mp4", "Batman Begins", "2005", "Action", "#111827"),
        new(1053, "batman-nolan", "The Dark Knight (2008) {imdb-tt0468569}.mp4", "The Dark Knight", "2008", "Action", "#0F172A"),
        new(1054, "batman-nolan", "The Dark Knight Rises (2012) {imdb-tt1345836}.mp4", "The Dark Knight Rises", "2012", "Action", "#1F2937"),
        new(1055, "matrix", "The Matrix (1999) {imdb-tt0133093}.mp4", "The Matrix", "1999", "Science Fiction", "#14532D"),
        new(1056, "matrix", "The Matrix Reloaded (2003) {imdb-tt0234215}.mp4", "The Matrix Reloaded", "2003", "Science Fiction", "#166534"),
        new(1057, "alien", "Alien (1979) {imdb-tt0078748}.mp4", "Alien", "1979", "Science Fiction", "#111827"),
        new(1058, "alien", "Aliens (1986) {imdb-tt0090605}.mp4", "Aliens", "1986", "Science Fiction", "#1E3A8A"),
        new(1059, "star-wars", "Star Wars A New Hope (1977) {imdb-tt0076759}.mp4", "Star Wars", "1977", "Science Fiction", "#111827"),
        new(1060, "star-wars", "The Empire Strikes Back (1980) {imdb-tt0080684}.mp4", "The Empire Strikes Back", "1980", "Science Fiction", "#1E3A8A"),
    };

    var largeTv = new VideoSpec[]
    {
        new(1080, Path.Combine("breaking-bad", "Season 01"), "Breaking Bad S01E03 And the Bag's in the River (2008) {imdb-tt1054725}.mp4", "Breaking Bad: And the Bag's in the River", "2008", "Drama", "#2E7D32"),
        new(1081, Path.Combine("breaking-bad", "Season 01"), "Breaking Bad S01E04 Cancer Man (2008) {imdb-tt1054726}.mp4", "Breaking Bad: Cancer Man", "2008", "Drama", "#33691E"),
        new(1082, Path.Combine("better-call-saul", "Season 01"), "Better Call Saul S01E01 Uno (2015) {imdb-tt3216986}.mp4", "Better Call Saul: Uno", "2015", "Drama", "#92400E"),
        new(1083, Path.Combine("better-call-saul", "Season 01"), "Better Call Saul S01E02 Mijo (2015) {imdb-tt3486042}.mp4", "Better Call Saul: Mijo", "2015", "Drama", "#A16207"),
        new(1084, Path.Combine("the-expanse", "Season 01"), "The Expanse S01E01 Dulcinea (2015) {imdb-tt3983204}.mp4", "The Expanse: Dulcinea", "2015", "Science Fiction", "#0D47A1"),
        new(1085, Path.Combine("the-expanse", "Season 01"), "The Expanse S01E02 The Big Empty (2015) {imdb-tt4310240}.mp4", "The Expanse: The Big Empty", "2015", "Science Fiction", "#1565C0"),
        new(1086, Path.Combine("foundation-2021", "Season 01"), "Foundation S01E01 The Emperor's Peace (2021) {imdb-tt0804484}.mp4", "Foundation: The Emperor's Peace", "2021", "Science Fiction", "#1D4ED8"),
        new(1087, Path.Combine("foundation-2021", "Season 01"), "Foundation S01E02 Preparing to Live (2021).mp4", "Foundation: Preparing to Live", "2021", "Science Fiction", "#2563EB"),
        new(1088, Path.Combine("game-of-thrones", "Season 01"), "Game of Thrones S01E01 Winter Is Coming (2011) {imdb-tt1480055}.mp4", "Game of Thrones: Winter Is Coming", "2011", "Fantasy", "#334155"),
        new(1089, Path.Combine("game-of-thrones", "Season 01"), "Game of Thrones S01E02 The Kingsroad (2011) {imdb-tt1668746}.mp4", "Game of Thrones: The Kingsroad", "2011", "Fantasy", "#475569"),
        new(1090, Path.Combine("the-last-of-us", "Season 01"), "The Last of Us S01E01 When You're Lost in the Darkness (2023) {imdb-tt3581920}.mp4", "The Last of Us: When You're Lost in the Darkness", "2023", "Drama", "#065F46"),
        new(1091, Path.Combine("the-last-of-us", "Season 01"), "The Last of Us S01E02 Infected (2023) {imdb-tt14500888}.mp4", "The Last of Us: Infected", "2023", "Drama", "#047857"),
        new(1092, Path.Combine("shogun-2024", "Season 01"), "Shogun S01E02 Servants of Two Masters (2024).mp4", "Shogun: Servants of Two Masters", "2024", "Drama", "#7B1FA2"),
        new(1093, Path.Combine("shogun-2024", "Season 01"), "Shogun S01E03 Tomorrow Is Tomorrow (2024).mp4", "Shogun: Tomorrow Is Tomorrow", "2024", "Drama", "#6D28D9"),
        new(1094, Path.Combine("breaking-bad", "Season 02"), "Breaking Bad S02E01 Seven Thirty-Seven (2009).mp4", "Breaking Bad: Seven Thirty-Seven", "2009", "Drama", "#2E7D32"),
        new(1095, Path.Combine("breaking-bad", "Season 03"), "Breaking Bad S03E01 No Mas (2010).mp4", "Breaking Bad: No Mas", "2010", "Drama", "#33691E"),
        new(1096, Path.Combine("the-expanse", "Season 02"), "The Expanse S02E01 Safe (2017).mp4", "The Expanse: Safe", "2017", "Science Fiction", "#0D47A1"),
        new(1097, Path.Combine("game-of-thrones", "Season 02"), "Game of Thrones S02E01 The North Remembers (2012).mp4", "Game of Thrones: The North Remembers", "2012", "Fantasy", "#334155"),
    };

    var largeMusic = new MusicSpec[]
    {
        new(1100, Path.Combine("David Bowie", "The Rise and Fall of Ziggy Stardust"), "03 Moonage Daydream.mp3", "Moonage Daydream", "David Bowie", "The Rise and Fall of Ziggy Stardust and the Spiders from Mars", "1972", "Rock", "3"),
        new(1101, Path.Combine("David Bowie", "Hunky Dory"), "04 Changes.mp3", "Changes", "David Bowie", "Hunky Dory", "1971", "Rock", "4"),
        new(1102, Path.Combine("Radiohead", "OK Computer"), "01 Airbag.mp3", "Airbag", "Radiohead", "OK Computer", "1997", "Alternative", "1"),
        new(1103, Path.Combine("Radiohead", "OK Computer"), "02 Paranoid Android.mp3", "Paranoid Android", "Radiohead", "OK Computer", "1997", "Alternative", "2"),
        new(1104, Path.Combine("The Beatles", "Sgt Peppers"), "01 Sgt Peppers Lonely Hearts Club Band.mp3", "Sgt. Pepper's Lonely Hearts Club Band", "The Beatles", "Sgt. Pepper's Lonely Hearts Club Band", "1967", "Rock", "1"),
        new(1105, Path.Combine("The Beatles", "Sgt Peppers"), "02 With a Little Help from My Friends.mp3", "With a Little Help from My Friends", "The Beatles", "Sgt. Pepper's Lonely Hearts Club Band", "1967", "Rock", "2"),
        new(1106, Path.Combine("Taylor Swift", "1989"), "01 Welcome to New York.mp3", "Welcome to New York", "Taylor Swift", "1989", "2014", "Pop", "1"),
        new(1107, Path.Combine("Taylor Swift", "1989"), "02 Blank Space.mp3", "Blank Space", "Taylor Swift", "1989", "2014", "Pop", "2"),
        new(1108, Path.Combine("Kendrick Lamar", "DAMN"), "02 DNA.mp3", "DNA.", "Kendrick Lamar", "DAMN.", "2017", "Hip-Hop", "2"),
        new(1109, Path.Combine("Kendrick Lamar", "good kid maad city"), "01 Sherane.mp3", "Sherane a.k.a Master Splinter's Daughter", "Kendrick Lamar", "good kid, m.A.A.d city", "2012", "Hip-Hop", "1"),
        new(1110, Path.Combine("Hans Zimmer", "Inception"), "12 Time.mp3", "Time", "Hans Zimmer", "Inception (Music from the Motion Picture)", "2010", "Soundtrack", "12"),
        new(1111, Path.Combine("Hans Zimmer", "Interstellar"), "02 Cornfield Chase.mp3", "Cornfield Chase", "Hans Zimmer", "Interstellar: Original Motion Picture Soundtrack", "2014", "Soundtrack", "2"),
    };

    var largeComics = new ComicSpec[]
    {
        new(1120, "batman-year-one", "Batman 405 Year One Part 2 (1987).cbz", "Batman", "405", "Frank Miller", "David Mazzucchelli", "1987", "Batman #405: Year One Part 2 - War Is Declared"),
        new(1121, "batman-year-one", "Batman 406 Year One Part 3 (1987).cbz", "Batman", "406", "Frank Miller", "David Mazzucchelli", "1987", "Batman #406: Year One Part 3 - Black Dawn"),
        new(1122, "batman-year-one", "Batman 407 Year One Part 4 (1987).cbz", "Batman", "407", "Frank Miller", "David Mazzucchelli", "1987", "Batman #407: Year One Part 4 - Friend in Need"),
        new(1123, "saga", "Saga 001 (2012).cbz", "Saga", "1", "Brian K. Vaughan", "Fiona Staples", "2012", "Saga #1"),
        new(1124, "saga", "Saga 002 (2012).cbz", "Saga", "2", "Brian K. Vaughan", "Fiona Staples", "2012", "Saga #2"),
        new(1125, "saga", "Saga 003 (2012).cbz", "Saga", "3", "Brian K. Vaughan", "Fiona Staples", "2012", "Saga #3"),
        new(1126, "sandman", "The Sandman 001 Sleep of the Just (1989).cbz", "The Sandman", "1", "Neil Gaiman", "Sam Kieth", "1989", "The Sandman #1: Sleep of the Just"),
        new(1127, "sandman", "The Sandman 002 Imperfect Hosts (1989).cbz", "The Sandman", "2", "Neil Gaiman", "Sam Kieth", "1989", "The Sandman #2: Imperfect Hosts"),
        new(1128, "akira", "Akira 001 (1982).cbz", "Akira", "1", "Katsuhiro Otomo", "Katsuhiro Otomo", "1982", "Akira #1"),
        new(1129, "akira", "Akira 002 (1982).cbz", "Akira", "2", "Katsuhiro Otomo", "Katsuhiro Otomo", "1982", "Akira #2"),
    };

    foreach (var (num, spec) in largeBooks)
    {
        var outPath = Path.Combine(tempDir, "large-books", spec.FileName);
        var finalPath = Path.Combine(booksDir, "large", spec.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        byte[]? cover = spec.IncludeCover && ffmpegPath is not null ? GeneratePng(ffmpegPath, tempDir, spec.CoverHex, 400, 600) : null;
        CreateEpub(outPath, spec, cover);
        generatedFiles.Add((outPath, finalPath));
        manifest.Add(new(num, $"large/{spec.FileName}", "epub", "Large corpus - books, canonical titles, series, repeated authors"));
        total++;
    }

    if (ffmpegPath is not null)
    {
        foreach (var (num, subdir, spec) in largeAudiobooks)
        {
            var outPath = Path.Combine(tempDir, "large-audiobooks", subdir, spec.FileName);
            var finalPath = Path.Combine(audiobooksDir, "large-audiobooks", subdir, spec.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var cover = spec.IncludeCover ? GeneratePng(ffmpegPath, tempDir, spec.CoverHex, 400, 400) : null;
            CreateM4b(ffmpegPath, tempDir, outPath, spec, cover);
            generatedFiles.Add((outPath, finalPath));
            manifest.Add(new(num, Path.Combine("audiobooks", "large-audiobooks", subdir, spec.FileName).Replace('\\', '/'), "m4b", "Large corpus - audiobooks, narrators, cross-format works"));
            total++;
        }
    }
    else
    {
        failed += largeAudiobooks.Length;
    }

    foreach (var spec in largeMovies)
    {
        var outPath = Path.Combine(tempDir, "large-movies", spec.Subdir, spec.FileName);
        var finalPath = Path.Combine(moviesDir, "large", spec.Subdir, spec.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var usedFallback = CreateMp4Fixture(ffmpegPath, outPath, spec);
        generatedFiles.Add((outPath, finalPath));
        manifest.Add(new(spec.Scenario, Path.Combine("large", spec.Subdir, spec.FileName).Replace('\\', '/'), "mp4", $"Large corpus - movies{(usedFallback ? " fallback" : "")}"));
        total++;
    }

    foreach (var spec in largeTv)
    {
        var outPath = Path.Combine(tempDir, "large-tv", spec.Subdir, spec.FileName);
        var finalPath = Path.Combine(tvDir, "large", spec.Subdir, spec.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var usedFallback = CreateMp4Fixture(ffmpegPath, outPath, spec);
        generatedFiles.Add((outPath, finalPath));
        manifest.Add(new(spec.Scenario, Path.Combine("large", spec.Subdir, spec.FileName).Replace('\\', '/'), "mp4", $"Large corpus - TV episodes and series{(usedFallback ? " fallback" : "")}"));
        total++;
    }

    foreach (var spec in largeMusic)
    {
        var outPath = Path.Combine(tempDir, "large-music", spec.Subdir, spec.FileName);
        var finalPath = Path.Combine(musicDir, "large", spec.Subdir, spec.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var usedFallback = CreateMp3Fixture(ffmpegPath, outPath, spec);
        generatedFiles.Add((outPath, finalPath));
        manifest.Add(new(spec.Scenario, Path.Combine("large", spec.Subdir, spec.FileName).Replace('\\', '/'), "mp3", $"Large corpus - music albums and repeated artists{(usedFallback ? " fallback" : "")}"));
        total++;
    }

    foreach (var spec in largeComics)
    {
        var outPath = Path.Combine(tempDir, "large-comics", spec.Subdir, spec.FileName);
        var finalPath = Path.Combine(comicsDir, "large", spec.Subdir, spec.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        CreateCbz(outPath, spec);
        generatedFiles.Add((outPath, finalPath));
        manifest.Add(new(spec.Scenario, Path.Combine("large", spec.Subdir, spec.FileName).Replace('\\', '/'), "cbz", "Large corpus - comics, series, and issue ordering"));
        total++;
    }

    Console.WriteLine($"  Added large corpus: {largeBooks.Length} books, {largeAudiobooks.Length} audiobooks, {largeMovies.Length} movies, {largeTv.Length} TV episodes, {largeMusic.Length} music tracks, {largeComics.Length} comics");
}

Console.WriteLine();
Console.WriteLine($"━━━ Copying {generatedFiles.Count} files to watch folder ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
foreach (var (src, dst) in generatedFiles)
{
    var dir = Path.GetDirectoryName(dst);
    if (dir is not null)
    {
        Directory.CreateDirectory(dir);
    }
    File.Copy(src, dst, overwrite: true);
}
Console.WriteLine($"  ✓  {generatedFiles.Count} files copied to {watchRoot}");

// ── Clean up temp ─────────────────────────────────────────────────────────────
try { Directory.Delete(tempDir, recursive: true); } catch { }

// ── Write MANIFEST.json ───────────────────────────────────────────────────────
// Write MANIFEST.json one level above the watch directory so it is not
// picked up as a media file by the Engine's watch folder monitor.
var manifestPath = Path.Combine(watchRoot, "MANIFEST.json");
var expectedPeopleForManifest = large
    ? expectedPeople.Concat(
    [
        new ExpectedPersonEntry(
            "Stephen King",
            ExpectedWikidataQid: "Q39829",
            MinimumOwnedCredits: 5,
            MinimumMediaItems: 5,
            ExpectedMediaTypes: ["Books", "Audiobooks", "Movies"],
            ExpectedTitles: ["The Shining", "Doctor Sleep", "It", "The Talisman"],
            RequireBiography: true,
            RequireHeadshot: true,
            Note: "Large corpus stress case: books, audiobooks, film adaptations, and pseudonym-adjacent titles."),
        new ExpectedPersonEntry(
            "Philip K. Dick",
            ExpectedWikidataQid: "Q171091",
            MinimumOwnedCredits: 3,
            MinimumMediaItems: 3,
            ExpectedMediaTypes: ["Books", "Movies"],
            ExpectedTitles: ["Do Androids Dream of Electric Sheep?", "Blade Runner", "Blade Runner 2049"],
            RequireBiography: true,
            RequireHeadshot: true,
            Note: "Large corpus stress case: one source novel across a movie franchise."),
        new ExpectedPersonEntry(
            "Christopher Nolan",
            ExpectedWikidataQid: "Q25191",
            MinimumOwnedCredits: 5,
            MinimumMediaItems: 5,
            ExpectedMediaTypes: ["Movies"],
            ExpectedTitles: ["Batman Begins", "The Dark Knight", "The Dark Knight Rises", "Interstellar", "Oppenheimer"],
            RequireBiography: true,
            RequireHeadshot: true,
            Note: "Large corpus stress case: repeated director across multiple franchises and standalone films."),
        new ExpectedPersonEntry(
            "Andy Weir",
            ExpectedWikidataQid: "Q4750383",
            MinimumOwnedCredits: 4,
            MinimumMediaItems: 4,
            ExpectedMediaTypes: ["Books", "Audiobooks", "Movies"],
            ExpectedTitles: ["The Martian", "Project Hail Mary"],
            RequireBiography: true,
            RequireHeadshot: true,
            Note: "Large corpus stress case: same author across ebook, audiobook, and movie adaptation."),
        new ExpectedPersonEntry(
            "George R. R. Martin",
            ExpectedWikidataQid: "Q181677",
            MinimumOwnedCredits: 4,
            MinimumMediaItems: 4,
            ExpectedMediaTypes: ["Books", "Audiobooks", "TV"],
            ExpectedTitles: ["A Game of Thrones", "A Clash of Kings", "Game of Thrones: Winter Is Coming"],
            RequireBiography: true,
            RequireHeadshot: true,
            Note: "Large corpus stress case: book series, audiobook, and TV adaptation."),
        new ExpectedPersonEntry(
            "Hans Zimmer",
            ExpectedWikidataQid: "Q76364",
            MinimumOwnedCredits: 4,
            MinimumMediaItems: 4,
            ExpectedMediaTypes: ["Movies", "Music"],
            ExpectedTitles: ["Interstellar", "Dreaming of the Crash", "Cornfield Chase", "Time"],
            RequireBiography: true,
            RequireHeadshot: true,
            Note: "Large corpus stress case: film composer plus soundtrack tracks across multiple albums."),
        new ExpectedPersonEntry(
            "Taylor Swift",
            ExpectedWikidataQid: "Q26876",
            MinimumOwnedCredits: 2,
            MinimumMediaItems: 2,
            ExpectedMediaTypes: ["Music"],
            ExpectedTitles: ["Welcome to New York", "Blank Space"],
            RequireBiography: true,
            RequireHeadshot: true,
            Note: "Large corpus stress case: popular music artist with album grouping."),
        new ExpectedPersonEntry(
            "Kendrick Lamar",
            ExpectedWikidataQid: "Q130798",
            MinimumOwnedCredits: 2,
            MinimumMediaItems: 2,
            ExpectedMediaTypes: ["Music"],
            ExpectedTitles: ["BLOOD.", "DNA."],
            RequireBiography: true,
            RequireHeadshot: true,
            Note: "Large corpus stress case: punctuation-heavy music titles and artist enrichment.")
    ]).ToArray()
    : expectedPeople;
var manifestJson = JsonSerializer.Serialize(new
{
    generated_at = DateTimeOffset.UtcNow.ToString("O"),
    watch_root = watchRoot,
    books_directory = booksDir,
    audiobooks_directory = audiobooksDir,
    movies_directory = moviesDir,
    tv_directory = tvDir,
    music_directory = musicDir,
    comics_directory = comicsDir,
    general_directory = generalDir,
    total_files = total,
    files = manifest.Select(m =>
    {
        var expected = m.ExpectedIdentity ?? InferExpectedIdentity(m);
        return new
        {
            scenario = m.Scenario,
            path = m.Path,
            type = m.Type,
            note = m.Note,
            expected_identity = new
            {
                title = expected.Title,
                media_type = expected.MediaType,
                expected_status = expected.ExpectedStatus,
                expected_work_qid = expected.ExpectedWorkQid,
                expected_bridge_ids = expected.ExpectedBridgeIds,
                expected_retail_provider = expected.ExpectedRetailProvider,
                expected_review_trigger = expected.ExpectedReviewTrigger,
                reason = expected.Reason,
                known_no_wikidata_entity = expected.KnownNoWikidataEntity
            }
        };
    }),
    expected_person_enrichment = expectedPeopleForManifest.Select(p => new
    {
        name = p.Name,
        expected_wikidata_qid = p.ExpectedWikidataQid,
        minimum_owned_credits = p.MinimumOwnedCredits,
        minimum_media_items = p.MinimumMediaItems,
        expected_media_types = p.ExpectedMediaTypes,
        expected_titles = p.ExpectedTitles,
        require_biography = p.RequireBiography,
        require_headshot = p.RequireHeadshot,
        note = p.Note
    })
}, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(manifestPath, manifestJson);

// ── Summary ───────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine($"  Generated : {total} / {(large ? "138" : "53")}");
if (failed > 0)
{
    Console.WriteLine($"  Failed    : {failed}");
}
Console.WriteLine($"  Manifest  : {manifestPath}");
Console.WriteLine();
Console.WriteLine("Next steps:");
Console.WriteLine("  1. Ensure the Engine is running  (dotnet run --project src/MediaEngine.Api)");
Console.WriteLine("  2. The Engine watches the output directory automatically.");
Console.WriteLine("  3. Check results at http://localhost:61495/swagger or the Dashboard.");
Console.WriteLine();
Console.WriteLine($"━━━ Test Coverage Summary ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine($"  Confidence gates     : 4 scenarios (1, 2, 8, 15)");
Console.WriteLine($"  Series & position    : 4 scenarios (4, 5, 17, 18)");
Console.WriteLine($"  Pseudonyms           : 5 scenarios (5, 6, 7, 16, 21)");
Console.WriteLine($"  Cross-format Collection?     : 4 scenarios (1+11, 5+19)");
Console.WriteLine($"  Narrators            : 2 scenarios (12, 13)");
Console.WriteLine($"  Ingestion hinting    : 4 scenarios (17-18, 19-20)");
Console.WriteLine($"  Corrupt & duplicate  : 2 scenarios (9, 10)");
Console.WriteLine($"  Title disambiguation : 4 scenarios (3, 22, 23, 28)");
Console.WriteLine($"  Foreign language     : 3 scenarios (24, 25, 26)");
Console.WriteLine($"  Multi-author         : 2 scenarios (29, 30)");
Console.WriteLine($"  Same-author diff-work: 1 scenario  (27)");
Console.WriteLine($"  All media watch roots: 16 scenarios (38-53)");
Console.WriteLine($"  Repeated-person checks: {expectedPeopleForManifest.Length} people declared in MANIFEST.json");
Console.WriteLine($"  Total: {(large ? "138" : "53")} files covering {(large ? "21" : "14")} test categories");
Console.WriteLine();

return failed > 0 ? 1 : 0;

// ═════════════════════════════════════════════════════════════════════════════
// Helpers
// ═════════════════════════════════════════════════════════════════════════════

static string? FindFfmpeg()
{
    var dir = AppContext.BaseDirectory;
    for (int i = 0; i < 8; i++)
    {
        var candidate = Path.Combine(dir, "tools", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }
        var parent = Directory.GetParent(dir);
        if (parent is null)
        {
            break;
        }
        dir = parent.FullName;
    }
    foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
    {
        var candidate = Path.Combine(p.Trim(), "ffmpeg.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }
    return null;
}

static byte[]? GeneratePng(string ffmpegPath, string tempDir, string hex, int width, int height)
{
    var outFile = Path.Combine(tempDir, $"cover_{Guid.NewGuid():N}.png");
    var args = $"-y -f lavfi -i \"color=c={hex}:s={width}x{height}:r=1\" -vframes 1 \"{outFile}\"";
    RunFfmpeg(ffmpegPath, args);
    if (!File.Exists(outFile))
    {
        return null;
    }
    var bytes = File.ReadAllBytes(outFile);
    File.Delete(outFile);
    return bytes;
}

static void CreateM4b(string ffmpegPath, string tempDir, string outPath, M4bSpec spec, byte[]? cover)
{
    var uid        = Guid.NewGuid().ToString("N");
    var silentFile = Path.Combine(tempDir, $"silent_{uid}.m4a");
    var coverFile  = cover is not null ? Path.Combine(tempDir, $"cover_{uid}.png") : null;

    RunFfmpeg(ffmpegPath,
        $"-y -f lavfi -i \"anullsrc=r=44100:cl=mono\" -t 30 " +
        $"-c:a aac -b:a 32k \"{silentFile}\"");

    if (cover is not null && coverFile is not null)
    {
        File.WriteAllBytes(coverFile, cover);
    }

    var metaArgs = new StringBuilder();
    if (!string.IsNullOrWhiteSpace(spec.Title))
    {
        metaArgs.Append($" -metadata title={Q(spec.Title)}");
    }
    if (!string.IsNullOrWhiteSpace(spec.Artist))
    {
        metaArgs.Append($" -metadata artist={Q(spec.Artist)}");
    }
    if (!string.IsNullOrWhiteSpace(spec.AlbumArtist))
    {
        metaArgs.Append($" -metadata album_artist={Q(spec.AlbumArtist)}");
    }
    if (!string.IsNullOrWhiteSpace(spec.Album))
    {
        metaArgs.Append($" -metadata album={Q(spec.Album)}");
    }
    if (!string.IsNullOrWhiteSpace(spec.Narrator))
    {
        // Write narrator to the Composers tag (TagLib primary extraction path)
        // and to Comment with "Narrated by" prefix (secondary extraction path).
        metaArgs.Append($" -metadata composer={Q(spec.Narrator)}");
        metaArgs.Append($" -metadata comment={Q("Narrated by " + spec.Narrator)}");
    }
    if (!string.IsNullOrWhiteSpace(spec.Year))
    {
        metaArgs.Append($" -metadata date={Q(spec.Year)}");
    }
    if (!string.IsNullOrWhiteSpace(spec.Genre))
    {
        metaArgs.Append($" -metadata genre={Q(spec.Genre)}");
    }
    if (!string.IsNullOrWhiteSpace(spec.TrackNum))
    {
        metaArgs.Append($" -metadata track={Q(spec.TrackNum)}");
    }

    string inputArgs, mapArgs, dispArgs;
    if (coverFile is not null)
    {
        inputArgs = $"-i \"{silentFile}\" -i \"{coverFile}\"";
        mapArgs   = "-map 0:a -map 1:v";
        dispArgs  = "-c:a copy -c:v png -disposition:v:0 attached_pic";
    }
    else
    {
        inputArgs = $"-i \"{silentFile}\"";
        mapArgs   = "-map 0:a";
        dispArgs  = "-c:a copy";
    }

    RunFfmpeg(ffmpegPath,
        $"-y {inputArgs} {mapArgs} {dispArgs}{metaArgs} " +
        $"-movflags +faststart \"{outPath}\"");

    TryDelete(silentFile);
    if (coverFile is not null)
    {
        TryDelete(coverFile);
    }
}

static void RunFfmpeg(string ffmpegPath, string args)
{
    var psi = new ProcessStartInfo(ffmpegPath, args)
    {
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
        CreateNoWindow         = true,
    };
    using var p = Process.Start(psi) ?? throw new Exception("Failed to start FFmpeg");
    var stdoutTask = p.StandardOutput.ReadToEndAsync();
    var stderrTask = p.StandardError.ReadToEndAsync();
    bool exited = p.WaitForExit(120_000);
    Task.WaitAll(stdoutTask, stderrTask);
    if (!exited)
    {
        throw new Exception("FFmpeg timed out after 120 seconds");
    }
    if (p.ExitCode != 0)
    {
        var err = stderrTask.Result;
        throw new Exception($"FFmpeg exited {p.ExitCode}: {err[..Math.Min(400, err.Length)]}");
    }
}

static void TryDelete(string path) { try { File.Delete(path); } catch { } }

static string Q(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

static bool CreateMp4Fixture(string? ffmpegPath, string outPath, VideoSpec spec)
{
    if (ffmpegPath is not null)
    {
        try
        {
            CreateMp4(ffmpegPath, outPath, spec);
            return false;
        }
        catch
        {
            TryDelete(outPath);
        }
    }

    CreateMinimalMp4(outPath, spec);
    return true;
}

static bool CreateMp3Fixture(string? ffmpegPath, string outPath, MusicSpec spec)
{
    if (ffmpegPath is not null)
    {
        try
        {
            CreateMp3(ffmpegPath, outPath, spec);
            return false;
        }
        catch
        {
            TryDelete(outPath);
        }
    }

    CreateMinimalMp3(outPath, spec);
    return true;
}

static void CreateMp4(string ffmpegPath, string outPath, VideoSpec spec)
{
    var args =
        $"-y -f lavfi -i \"color=c={spec.ColorHex}:s=1280x720:r=24\" -f lavfi -i \"anullsrc=r=48000:cl=stereo\" -t 8 " +
        "-c:v libx264 -pix_fmt yuv420p -c:a aac -b:a 96k " +
        $"-metadata title={Q(spec.Title)} " +
        $"-metadata date={Q(spec.Year)} " +
        $"-metadata genre={Q(spec.Genre)} " +
        $"\"{outPath}\"";
    RunFfmpeg(ffmpegPath, args);
}

static void CreateMp3(string ffmpegPath, string outPath, MusicSpec spec)
{
    var args =
        $"-y -f lavfi -i \"anullsrc=r=44100:cl=stereo\" -t 12 " +
        "-c:a libmp3lame -b:a 128k " +
        $"-metadata title={Q(spec.Title)} " +
        $"-metadata artist={Q(spec.Artist)} " +
        $"-metadata album={Q(spec.Album)} " +
        $"-metadata date={Q(spec.Year)} " +
        $"-metadata genre={Q(spec.Genre)} " +
        $"-metadata track={Q(spec.TrackNum)} " +
        $"\"{outPath}\"";
    RunFfmpeg(ffmpegPath, args);
}

static void CreateMinimalMp4(string outPath, VideoSpec spec)
{
    using var stream = new FileStream(outPath, FileMode.Create, FileAccess.Write);
    WriteMp4Ftyp(stream);
    WriteMp4Moov(stream, spec);
    WriteMp4Mdat(stream);
}

static void WriteMp4Ftyp(Stream stream)
{
    WriteBigEndian32(stream, 20);
    stream.Write("ftyp"u8);
    stream.Write("isom"u8);
    WriteBigEndian32(stream, 0x200);
    stream.Write("isom"u8);
}

static void WriteMp4Moov(Stream stream, VideoSpec spec)
{
    using var udta = new MemoryStream();
    WriteMp4StringAtom(udta, "\u00A9nam", spec.Title);
    WriteMp4StringAtom(udta, "\u00A9day", spec.Year);
    WriteMp4StringAtom(udta, "\u00A9gen", spec.Genre);

    var udtaBytes = udta.ToArray();
    const int mvhdSize = 108;
    var udtaSize = udtaBytes.Length == 0 ? 0 : 8 + udtaBytes.Length;
    var moovSize = 8 + mvhdSize + udtaSize;

    WriteBigEndian32(stream, moovSize);
    stream.Write("moov"u8);
    WriteBigEndian32(stream, mvhdSize);
    stream.Write("mvhd"u8);
    stream.Write(new byte[mvhdSize - 8]);

    if (udtaBytes.Length > 0)
    {
        WriteBigEndian32(stream, udtaSize);
        stream.Write("udta"u8);
        stream.Write(udtaBytes);
    }
}

static void WriteMp4Mdat(Stream stream)
{
    WriteBigEndian32(stream, 8);
    stream.Write("mdat"u8);
}

static void WriteMp4StringAtom(Stream stream, string atomName, string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return;
    }

    var valueBytes = Encoding.UTF8.GetBytes(value);
    var dataSize = 8 + 8 + valueBytes.Length;
    var atomSize = 8 + dataSize;
    var atomBytes = Encoding.Latin1.GetBytes(atomName);

    WriteBigEndian32(stream, atomSize);
    stream.Write(atomBytes.AsSpan(0, 4));
    WriteBigEndian32(stream, dataSize);
    stream.Write("data"u8);
    WriteBigEndian32(stream, 1);
    WriteBigEndian32(stream, 0);
    stream.Write(valueBytes);
}

static void CreateMinimalMp3(string outPath, MusicSpec spec)
{
    using var stream = new FileStream(outPath, FileMode.Create, FileAccess.Write);
    using var frames = new MemoryStream();

    WriteId3TextFrame(frames, "TIT2", spec.Title);
    WriteId3TextFrame(frames, "TPE1", spec.Artist);
    WriteId3TextFrame(frames, "TALB", spec.Album);
    WriteId3TextFrame(frames, "TYER", spec.Year);
    WriteId3TextFrame(frames, "TCON", spec.Genre);
    WriteId3TextFrame(frames, "TRCK", spec.TrackNum);

    var frameData = frames.ToArray();
    stream.Write("ID3"u8);
    stream.WriteByte(3);
    stream.WriteByte(0);
    stream.WriteByte(0);
    WriteSyncSafe(stream, frameData.Length);
    stream.Write(frameData);

    stream.WriteByte(0xFF);
    stream.WriteByte(0xFB);
    stream.WriteByte(0x90);
    stream.WriteByte(0x00);
    stream.Write(new byte[413]);
}

static void WriteId3TextFrame(Stream stream, string frameId, string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return;
    }

    var bytes = Encoding.Latin1.GetBytes(value);
    var dataSize = 1 + bytes.Length;
    stream.Write(Encoding.ASCII.GetBytes(frameId));
    WriteBigEndian32(stream, dataSize);
    stream.WriteByte(0);
    stream.WriteByte(0);
    stream.WriteByte(0);
    stream.Write(bytes);
}

static void WriteSyncSafe(Stream stream, int value)
{
    stream.WriteByte((byte)((value >> 21) & 0x7F));
    stream.WriteByte((byte)((value >> 14) & 0x7F));
    stream.WriteByte((byte)((value >> 7) & 0x7F));
    stream.WriteByte((byte)(value & 0x7F));
}

static void WriteBigEndian32(Stream stream, int value)
{
    stream.WriteByte((byte)((value >> 24) & 0xFF));
    stream.WriteByte((byte)((value >> 16) & 0xFF));
    stream.WriteByte((byte)((value >> 8) & 0xFF));
    stream.WriteByte((byte)(value & 0xFF));
}

static void CreateCbz(string outPath, ComicSpec spec)
{
    if (File.Exists(outPath))
    {
        File.Delete(outPath);
    }

    using var fs = new FileStream(outPath, FileMode.Create);
    using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);
    AddText(zip, "ComicInfo.xml", $"""
        <?xml version="1.0" encoding="utf-8"?>
        <ComicInfo>
          <Series>{Esc(spec.Series)}</Series>
          <Number>{Esc(spec.IssueNumber)}</Number>
          <Title>{Esc(spec.IssueTitle ?? $"{spec.Series} #{spec.IssueNumber}")}</Title>
          <Writer>{Esc(spec.Writer)}</Writer>
          <Penciller>{Esc(spec.Artist)}</Penciller>
          <Year>{Esc(spec.Year)}</Year>
          <Publisher>DC Comics</Publisher>
        </ComicInfo>
        """);

    var pageEntry = zip.CreateEntry("page-001.png", CompressionLevel.Optimal);
    using var pageStream = pageEntry.Open();
    pageStream.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVR4nGNkYGBgAAAABAABJzQnCgAAAABJRU5ErkJggg=="));
}

static void CreateEpub(string outputPath, EpubSpec spec, byte[]? coverBytes)
{
    if (File.Exists(outputPath))
    {
        File.Delete(outputPath);
    }

    using var fs  = new FileStream(outputPath, FileMode.Create);
    using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

    var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
    using (var w = new StreamWriter(mime.Open(), Encoding.ASCII))
    {
        w.Write("application/epub+zip");
    }

    AddText(zip, "META-INF/container.xml", """
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """);

    bool hasCover = coverBytes is { Length: > 0 };
    if (hasCover)
    {
        var coverEntry = zip.CreateEntry("OEBPS/cover.png", CompressionLevel.Optimal);
        using var cs = coverEntry.Open();
        cs.Write(coverBytes!);
    }

    var identifierXml = !string.IsNullOrWhiteSpace(spec.Isbn)
        ? $"""<dc:identifier id="isbn">{Esc(spec.Isbn)}</dc:identifier>"""
        : $"""<dc:identifier id="uid">urn:uuid:{Guid.NewGuid()}</dc:identifier>""";

    var authorXml = !string.IsNullOrWhiteSpace(spec.Author)
        ? $"""<dc:creator opf:role="aut">{Esc(spec.Author)}</dc:creator>"""
        : "";
    if (spec.SecondAuthor is not null)
    {
        authorXml += $"\n        <dc:creator opf:role=\"aut\">{Esc(spec.SecondAuthor)}</dc:creator>";
    }

    var titleXml     = !string.IsNullOrWhiteSpace(spec.Title)
        ? $"<dc:title>{Esc(spec.Title)}</dc:title>"
        : "<dc:title>Unknown</dc:title>";
    var dateXml      = !string.IsNullOrWhiteSpace(spec.Year)       ? $"<dc:date>{Esc(spec.Year)}</dc:date>"              : "";
    var publisherXml = !string.IsNullOrWhiteSpace(spec.Publisher)   ? $"<dc:publisher>{Esc(spec.Publisher)}</dc:publisher>" : "";
    var descXml      = !string.IsNullOrWhiteSpace(spec.Description) ? $"<dc:description>{Esc(spec.Description)}</dc:description>" : "";
    var seriesXml    = spec.Series is not null
        ? $"""
          <meta property="belongs-to-collection" id="series">{Esc(spec.Series)}</meta>
          <meta refines="#series" property="collection-type">series</meta>
          """ + (spec.SeriesPosition is not null
              ? $"\n        <meta refines=\"#series\" property=\"group-position\">{Esc(spec.SeriesPosition)}</meta>"
              : "")
        : "";
    var coverManifest = hasCover
        ? """<item id="cover-img" href="cover.png" media-type="image/png" properties="cover-image"/>"""
        : "";

    AddText(zip, "OEBPS/content.opf", $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <package xmlns="http://www.idpf.org/2007/opf"
                 xmlns:dc="http://purl.org/dc/elements/1.1/"
                 xmlns:opf="http://www.idpf.org/2007/opf"
                 unique-identifier="uid" version="3.0">
          <metadata>
            {titleXml}
            {authorXml}
            {identifierXml}
            <dc:language>{spec.Language}</dc:language>
            {publisherXml}
            {descXml}
            {dateXml}
            {seriesXml}
            <meta property="dcterms:modified">2025-01-01T00:00:00Z</meta>
          </metadata>
          <manifest>
            <item id="chapter1" href="chapter1.xhtml" media-type="application/xhtml+xml"/>
            <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
            <item id="toc" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
            {coverManifest}
          </manifest>
          <spine toc="toc">
            <itemref idref="chapter1"/>
          </spine>
        </package>
        """);

    AddText(zip, "OEBPS/toc.ncx", $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
          <head><meta name="dtb:uid" content="{Esc(spec.Isbn ?? spec.Title)}"/></head>
          <docTitle><text>{Esc(spec.Title)}</text></docTitle>
          <navMap>
            <navPoint id="np1" playOrder="1">
              <navLabel><text>Chapter 1</text></navLabel>
              <content src="chapter1.xhtml"/>
            </navPoint>
          </navMap>
        </ncx>
        """);

    AddText(zip, "OEBPS/nav.xhtml", $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
          <head><title>Navigation</title></head>
          <body>
            <nav epub:type="toc" id="toc">
              <h1>Table of Contents</h1>
              <ol><li><a href="chapter1.xhtml">Chapter 1</a></li></ol>
            </nav>
          </body>
        </html>
        """);

    var authorLine = spec.SecondAuthor is not null
        ? $"{Esc(spec.Author)} &amp; {Esc(spec.SecondAuthor)}"
        : Esc(spec.Author ?? "Unknown");
    AddText(zip, "OEBPS/chapter1.xhtml", $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
          <head><title>{Esc(spec.Title)}</title></head>
          <body>
            <h1>{Esc(spec.Title)}</h1>
            <p>Test EPUB for Tuvima Library pipeline validation.</p>
            <p>Author: {authorLine} — Year: {Esc(spec.Year ?? "Unknown")}</p>
          </body>
        </html>
        """);
}

static void AddText(ZipArchive zip, string entryName, string content)
{
    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
    using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
    w.Write(content.TrimStart());
}

static string Esc(string? s) =>
    (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
             .Replace("\"", "&quot;").Replace("'", "&apos;");

static ExpectedIdentityEntry InferExpectedIdentity(ManifestEntry entry)
{
    if (entry.Scenario == 9)
    {
        return new(
            Title: InferTitleFromPath(entry.Path),
            MediaType: ResolveManifestMediaType(entry),
            ExpectedStatus: "Corrupt",
            ExpectedWorkQid: null,
            ExpectedBridgeIds: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExpectedRetailProvider: null,
            ExpectedReviewTrigger: null,
            Reason: "Corrupt EPUB fixture should fail file processing before identity resolution.",
            KnownNoWikidataEntity: false);
    }

    if (entry.Scenario == 10)
    {
        return new(
            Title: InferTitleFromPath(entry.Path),
            MediaType: ResolveManifestMediaType(entry),
            ExpectedStatus: "Duplicate",
            ExpectedWorkQid: null,
            ExpectedBridgeIds: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExpectedRetailProvider: null,
            ExpectedReviewTrigger: null,
            Reason: "Byte-identical duplicate fixture should be skipped as an already tracked file.",
            KnownNoWikidataEntity: false);
    }

    if (string.Equals(entry.Type, "txt", StringComparison.OrdinalIgnoreCase))
    {
        return new(
            Title: InferTitleFromPath(entry.Path),
            MediaType: "General",
            ExpectedStatus: "Skipped",
            ExpectedWorkQid: null,
            ExpectedBridgeIds: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExpectedRetailProvider: null,
            ExpectedReviewTrigger: null,
            Reason: "General drop-zone note is not a supported media identity fixture.",
            KnownNoWikidataEntity: false);
    }

    var bridgeIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (ExtractImdbId(entry.Path) is { Length: > 0 } imdbId)
    {
        bridgeIds["imdb"] = imdbId;
    }

    return new(
        Title: InferTitleFromPath(entry.Path),
        MediaType: ResolveManifestMediaType(entry),
        ExpectedStatus: "ResolvedQid",
        ExpectedWorkQid: null,
        ExpectedBridgeIds: bridgeIds,
        ExpectedRetailProvider: ResolveExpectedRetailProvider(entry),
        ExpectedReviewTrigger: null,
        Reason: "Known real-world fixture should resolve to a non-placeholder Wikidata QID.",
        KnownNoWikidataEntity: false);
}

static string ResolveManifestMediaType(ManifestEntry entry)
{
    return entry.Type.ToLowerInvariant() switch
    {
        "epub" => "Books",
        "m4b" => "Audiobooks",
        "mp4" when entry.Path.StartsWith("tv/", StringComparison.OrdinalIgnoreCase)
            || entry.Path.StartsWith("large/", StringComparison.OrdinalIgnoreCase)
                && entry.Note.Contains("TV", StringComparison.OrdinalIgnoreCase) => "TV",
        "mp4" => "Movies",
        "mp3" => "Music",
        "cbz" => "Comics",
        _ => "General",
    };
}

static string? ResolveExpectedRetailProvider(ManifestEntry entry)
{
    return ResolveManifestMediaType(entry) switch
    {
        "Books" or "Audiobooks" => "apple_books",
        "Movies" or "TV" => "tmdb",
        "Music" => "apple_music",
        "Comics" => "comicvine",
        _ => null,
    };
}

static string InferTitleFromPath(string path)
{
    var title = Path.GetFileNameWithoutExtension(path)
        .Replace('-', ' ')
        .Replace('_', ' ')
        .Replace("  ", " ")
        .Trim();

    var bridgeStart = title.IndexOf(" {imdb-", StringComparison.OrdinalIgnoreCase);
    if (bridgeStart >= 0)
    {
        title = title[..bridgeStart].Trim();
    }

    var yearStart = title.LastIndexOf(" (", StringComparison.OrdinalIgnoreCase);
    if (yearStart > 0 && title.EndsWith(')'))
    {
        title = title[..yearStart].Trim();
    }

    return title;
}

static string? ExtractImdbId(string path)
{
    const string marker = "{imdb-";
    var start = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
    if (start < 0)
    {
        return null;
    }

    start += marker.Length;
    var end = path.IndexOf('}', start);
    return end > start ? path[start..end] : null;
}

// ── Record types ──────────────────────────────────────────────────────────────

record EpubSpec(
    string FileName,
    string Title,
    string? Author,
    string? SecondAuthor,
    string? Isbn,
    string? Year,
    string? Publisher,
    string? Description,
    string? Series,
    string? SeriesPosition,
    string Language,
    bool IncludeCover,
    string CoverHex);

record M4bSpec(
    string FileName,
    string Title,
    string Artist,
    string AlbumArtist,
    string Album,
    string Narrator,
    string Year,
    string Genre,
    string Comment,
    string TrackNum,
    string? Series,
    string? SeriesPos,
    bool IncludeCover,
    string CoverHex);

record VideoSpec(
    int Scenario,
    string Subdir,
    string FileName,
    string Title,
    string Year,
    string Genre,
    string ColorHex);

record MusicSpec(
    int Scenario,
    string Subdir,
    string FileName,
    string Title,
    string Artist,
    string Album,
    string Year,
    string Genre,
    string TrackNum);

record ComicSpec(
    int Scenario,
    string Subdir,
    string FileName,
    string Series,
    string IssueNumber,
    string Writer,
    string Artist,
    string Year,
    string? IssueTitle = null);

record ManifestEntry(
    int Scenario,
    string Path,
    string Type,
    string Note,
    ExpectedIdentityEntry? ExpectedIdentity = null);

record ExpectedIdentityEntry(
    string Title,
    string MediaType,
    string ExpectedStatus,
    string? ExpectedWorkQid,
    IReadOnlyDictionary<string, string> ExpectedBridgeIds,
    string? ExpectedRetailProvider,
    string? ExpectedReviewTrigger,
    string Reason,
    bool KnownNoWikidataEntity);

record ExpectedPersonEntry(
    string Name,
    string? ExpectedWikidataQid,
    int MinimumOwnedCredits,
    int MinimumMediaItems,
    string[] ExpectedMediaTypes,
    string[] ExpectedTitles,
    bool RequireBiography,
    bool RequireHeadshot,
    string Note);
