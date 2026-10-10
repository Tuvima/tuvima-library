using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// Unit tests for <see cref="FileOrganizer.CalculatePath"/>.
///
/// Key invariant: the resolved relative path must NEVER contain a
/// duplicated filename segment. The template owns the full path
/// (directory segments + filename + extension); callers must not
/// append Path.GetFileName on top of the resolved result.
/// </summary>
public class FileOrganizerTests
{
    [Theory]
    [InlineData("Collection/Original.epub", "Collection/Original.epub")]
    [InlineData(".data/staging/Collection/Original.epub", "Collection/Original.epub")]
    public void KeepOriginalNamesPreservesSourceStructureWithoutStagingFolders(string input, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "library-naming-test");
        var candidate = BuildCandidate(Path.Combine(root, input), MediaType.Books);
        var library = new LibraryFolderEntry
        {
            OrganizationMode = MediaEngine.Domain.Configuration.LibraryOrganizationModes.KeepOriginalNames,
            Sources = [new LibrarySourceEntry { Path = root }],
        };
        var result = MediaEngine.Ingestion.Services.LibraryOrganizationPath.Calculate(CreateOrganizer(), candidate, library, "{Title}.{Extension}");
        Assert.Equal(expected, result.Replace('\\', '/'));
    }

    [Fact]
    public void LibraryCustomNamingOverridesTheGlobalTemplate()
    {
        var candidate = BuildCandidate(Path.Combine(Path.GetTempPath(), "original.epub"), MediaType.Books,
            new() { ["title"] = "Dune" });
        var library = new LibraryFolderEntry
        {
            OrganizationMode = MediaEngine.Domain.Configuration.LibraryOrganizationModes.Custom,
            CustomOrganizationTemplate = "Custom/{Title}.{Extension}",
        };
        var result = MediaEngine.Ingestion.Services.LibraryOrganizationPath.Calculate(CreateOrganizer(), candidate, library, "Global/{Title}.{Extension}");
        Assert.Equal("Custom/Dune.epub", result.Replace('\\', '/'));
    }

    private static FileOrganizer CreateOrganizer() =>
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger<FileOrganizer>.Instance);

    private static IngestionCandidate BuildCandidate(
        string filePath,
        MediaType mediaType,
        Dictionary<string, string>? meta = null)
    {
        var candidate = new IngestionCandidate
        {
            Path = filePath,
            EventType = FileEventType.Created,
            DetectedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
            DetectedMediaType = mediaType,
        };
        if (meta is not null)
        {
            candidate.Metadata = meta;
        }

        return candidate;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bug regression: extra folder should NOT appear
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CalculatePath_UserTemplate_DoesNotDuplicateFilename()
    {
        // Arrange
        var organizer = CreateOrganizer();
        var template = "{Category}/{Author}/{Title} ({Edition}){Ext}";

        var candidate = BuildCandidate(
            @"C:\watch\Abaddon's Gate.epub",
            MediaType.Books,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "Abaddon's Gate",
                ["author"] = "James S.A. Corey",
                ["edition"] = "Paperback",
            });

        // Act
        var relative = organizer.CalculatePath(candidate, template);

        // Assert: relative must be a single path with filename at the leaf,
        // NOT something like "Books/James S.A. Corey/Abaddon's Gate.epub/..."
        Assert.Equal(
            "Books/James S.A. Corey/Abaddon's Gate (Paperback).epub",
            relative);

        // Confirm no path segment looks like a file (i.e. no .epub directory)
        var segments = relative.Split('/');
        Assert.Equal(3, segments.Length); // Category / Author / Filename
        Assert.EndsWith(".epub", segments[^1]);
        // Middle segment is the author name — it must NOT itself end with a file extension
        // (it can contain dots e.g. "James S.A. Corey" — just not be a filename)
        Assert.DoesNotMatch(@"(?i)\.[a-z]{2,5}$", segments[1]);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Category mapping: Movies and TV
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CalculatePath_MoviesCategory_ReturnsMoviesNotVideos()
    {
        var organizer = CreateOrganizer();
        var template = "{Category}/{Title} - {Qid}/{Title}{Ext}";

        var candidate = BuildCandidate(
            @"C:\watch\dune-part-two.mkv",
            MediaType.Movies,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "Dune Part Two",
            });

        var relative = organizer.CalculatePath(candidate, template);

        Assert.StartsWith("Movies/", relative);
        Assert.DoesNotContain("Videos", relative);
    }

    [Fact]
    public void CalculatePath_TvCategory_ReturnsTvNotTvShows()
    {
        var organizer = CreateOrganizer();
        var template = "{Category}/{Title} - {Qid}/{Title}{Ext}";

        var candidate = BuildCandidate(
            @"C:\watch\s01e01.mkv",
            MediaType.TV,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "Breaking Bad",
            });

        var relative = organizer.CalculatePath(candidate, template);

        Assert.StartsWith("TV/", relative);
        Assert.DoesNotContain("TV Shows", relative);
    }

    [Theory]
    [InlineData("1", "2", "Show/Show - s01e01-e02.mkv")]   // unbroken two-episode file
    [InlineData("1", "6", "Show/Show - s01e01-e06.mkv")]   // longest run allowed
    [InlineData("1", "9", "Show/Show - s01e01.mkv")]       // over the per-file cap: plain name
    [InlineData("1", "1", "Show/Show - s01e01.mkv")]       // no real range
    [InlineData("3", "2", "Show/Show - s01e03.mkv")]       // reversed: plain name
    [InlineData("1", null, "Show/Show - s01e01.mkv")]      // single-episode file: unchanged
    public void CalculatePath_TvCombinedFile_NamesTheEpisodeRun(string episode, string? episodeEnd, string expected)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = "Show",
            ["season"] = "1",
            ["episode"] = episode,
        };
        if (episodeEnd is not null)
        {
            meta["episode_end"] = episodeEnd;
        }

        var candidate = BuildCandidate(@"C:\watch\show.mkv", MediaType.TV, meta);

        var relative = CreateOrganizer().CalculatePath(candidate, "{Title}/{Title} - s{Season}e{Episode}{Ext}");

        Assert.Equal(expected, relative.Replace('\\', '/'));
    }

    [Fact]
    public void CalculatePath_MultipartAudiobookUsesBookFolderAndPartIdentity()
    {
        var candidate = BuildCandidate(
            @"C:\watch\001.mp3",
            MediaType.Audiobooks,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["author"] = "Andy Weir",
                ["book_title"] = "Project Hail Mary",
                ["title"] = "Part 01",
                ["audiobook_part_number"] = "1",
                ["year"] = "2021",
            });

        var relative = CreateOrganizer().CalculatePath(
            candidate,
            "Audiobooks/{Author}/{BookTitle} ({Year})/{TrackNumber?} {TrackTitle}{Ext}");

        Assert.Equal("Audiobooks/Andy Weir/Project Hail Mary (2021)/01 Part 01.mp3", relative);
    }

    [Fact]
    public void CalculatePath_MusicUsesAlbumArtistWithoutChangingTrackTitle()
    {
        var candidate = BuildCandidate(
            @"C:\watch\under-pressure.flac",
            MediaType.Music,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["artist"] = "Queen & David Bowie",
                ["album_artist"] = "Queen",
                ["album"] = "Hot Space",
                ["title"] = "Under Pressure",
                ["track_number"] = "11",
            });

        var relative = CreateOrganizer().CalculatePath(
            candidate,
            "Music/{AlbumArtist}/{Album}/{TrackNumber} - {Title}{Ext}");

        Assert.Equal("Music/Queen/Hot Space/11 - Under Pressure.flac", relative);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Default template: hyphen-dash separator + Q0 placeholder
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CalculatePath_DefaultTemplate_UsesDashSeparatorWithQ0Placeholder()
    {
        // When no QID is in metadata, {Qid} resolves to "Q0" (not empty)
        var organizer = CreateOrganizer();
        var template = "{Category}/{Title} - {Qid}/{Title}{Ext}";

        var candidate = BuildCandidate(
            @"C:\watch\dune.epub",
            MediaType.Books,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "Dune",
                // no wikidata_qid in metadata
            });

        var relative = organizer.CalculatePath(candidate, template);

        Assert.Equal("Books/Dune - Q0/Dune.epub", relative);
    }

    [Fact]
    public void CalculatePath_DefaultTemplate_UsesDashSeparatorWithRealQid()
    {
        // When a QID is present, it appears verbatim after the hyphen-dash
        var organizer = CreateOrganizer();
        var template = "{Category}/{Title} - {Qid}/{Title}{Ext}";

        var candidate = BuildCandidate(
            @"C:\watch\dune.epub",
            MediaType.Books,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "Dune",
                ["wikidata_qid"] = "Q190159",
            });

        var relative = organizer.CalculatePath(candidate, template);

        Assert.Equal("Books/Dune - Q190159/Dune.epub", relative);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Books per-media-type template: format subfolder
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CalculatePath_BooksTemplate_IncludesFormatSubfolder()
    {
        // The Books per-media-type template adds a {Format} subfolder between
        // the title-QID folder and the filename.
        // {Format} resolves to DetectedMediaType.ToString() → "Books"
        var organizer = CreateOrganizer();
        var template = "{Category}/{Title} - {Qid}/{Format}/{Title}{Ext}";

        var candidate = BuildCandidate(
            @"C:\watch\dune.epub",
            MediaType.Books,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "Dune",
                // no QID — placeholder Q0 expected
            });

        var relative = organizer.CalculatePath(candidate, template);

        Assert.Equal("Books/Dune - Q0/Books/Dune.epub", relative);
        var segments = relative.Split('/');
        Assert.Equal(4, segments.Length); // Category / Title-Qid / Format / Filename
    }

    [Fact]
    public void CalculatePath_DefaultTemplate_DoesNotDuplicateFilename()
    {
        // The default template: {Category}/{CollectionName} ({Year})/{Format}/{CollectionName} ({Edition}){Ext}
        var organizer = CreateOrganizer();
        var template = "{Category}/{CollectionName} ({Year})/{Format}/{CollectionName} ({Edition}){Ext}";

        var candidate = BuildCandidate(
            @"C:\watch\sample.epub",
            MediaType.Books,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "Sample Book",
                ["year"] = "2024",
                ["edition"] = "Hardcover",
            });

        var relative = organizer.CalculatePath(candidate, template);

        // 4 segments: Category / CollectionName (Year) / Format / CollectionName (Edition).ext
        var segments = relative.Split('/');
        Assert.Equal(4, segments.Length);
        Assert.EndsWith(".epub", segments[^1]);
        // Intermediate segments must NOT contain a .epub extension
        Assert.DoesNotContain(".", segments[1]);
        Assert.DoesNotContain(".", segments[2]);
    }

    [Fact]
    public void CalculatePath_MissingEdition_CollapsesConditionalGroup()
    {
        // {Edition} is empty → " ()" group should be collapsed entirely, not left as " ()"
        var organizer = CreateOrganizer();
        var template = "{Category}/{Author}/{Title} ({Edition}){Ext}";

        var candidate = BuildCandidate(
            @"C:\watch\book.epub",
            MediaType.Books,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "My Book",
                ["author"] = "Some Author",
                // no "edition" key
            });

        var relative = organizer.CalculatePath(candidate, template);

        // Edition is missing → conditional group collapses → no trailing " ()"
        Assert.Equal("Books/Some Author/My Book.epub", relative);
        Assert.DoesNotContain("()", relative);
    }

    [Fact]
    public void CalculatePath_EpubCandidate_ProducesCorrectCategory()
    {
        var organizer = CreateOrganizer();
        var template = "{Category}/{Author}/{Title}{Ext}";

        var candidate = BuildCandidate(
            @"C:\watch\dune.epub",
            MediaType.Books,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "Dune",
                ["author"] = "Frank Herbert",
            });

        var relative = organizer.CalculatePath(candidate, template);

        Assert.StartsWith("Books/", relative);
        Assert.EndsWith(".epub", relative);
    }

    [Fact]
    public void CalculatePath_PathCombineSimulation_NeverCreatesExtraFolder()
    {
        // Simulate what IngestionEngine does:
        //   destPath = Path.Combine(libraryRoot, relative)
        // The result must be a path to a FILE, not a DIRECTORY.
        var organizer = CreateOrganizer();
        var template = "{Category}/{Author}/{Title} ({Edition}){Ext}";
        var libraryRoot = @"C:\Users\shaya\Downloads\books\library";

        var candidate = BuildCandidate(
            @"C:\watch\Abaddon's Gate.epub",
            MediaType.Books,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "Abaddon's Gate",
                ["author"] = "James S.A. Corey",
                ["edition"] = "Paperback",
            });

        var relative = organizer.CalculatePath(candidate, template);
        var destPath = Path.Combine(libraryRoot, relative);

        // Expected final path ends with the filename (not a directory then filename)
        Assert.EndsWith("Abaddon's Gate (Paperback).epub", destPath);

        // Specifically: the path must NOT contain "Abaddon's Gate.epub\Abaddon's Gate"
        Assert.DoesNotContain(".epub" + Path.DirectorySeparatorChar, destPath);
        Assert.DoesNotContain(".epub/", destPath);
    }
}
