using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Extras;

namespace MediaEngine.Ingestion.Tests;

public sealed class ExtrasRuleTests : IDisposable
{
    private static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".mpeg", ".mpg", ".m2ts",
    };

    private static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".ogg", ".wav", ".opus", ".aac", ".wma",
    };

    private static readonly IReadOnlySet<string> BookExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".azw3",
    };

    private static readonly IReadOnlySet<string> ComicExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cbz", ".cbr", ".cb7",
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_extras_{Guid.NewGuid():N}");

    public ExtrasRuleTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ExtrasLibraryContext Tv() => new("tv", [MediaType.TV], _root, VideoExtensions);

    private ExtrasLibraryContext Movies() => new("movies", [MediaType.Movies], _root, VideoExtensions);

    private ExtrasLibraryContext Music() => new("music", [MediaType.Music], _root, AudioExtensions);

    private ExtrasLibraryContext Books() => new("books", [MediaType.Books], _root, BookExtensions);

    private ExtrasLibraryContext Comics() => new("comics", [MediaType.Comics], _root, ComicExtensions);

    private ExtrasLibraryContext Courses() => new("videos", [MediaType.Videos], _root, VideoExtensions);

    private string Touch(string relative, int bytes = 4)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private IReadOnlyList<string> Titles(ExtrasLibraryContext context, string itemFolder) =>
        [.. ExtrasRule.ScanItemFolder(context, Path.Combine(_root, itemFolder)).Select(extra => Path.GetRelativePath(_root, extra.FilePath).Replace('\\', '/'))];

    // ── The Steph Curry MasterClass journey ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AGuidebookBesideNumberedLessons_IsAnExtra_AndTheLessonsAreNot()
    {
        for (var lesson = 1; lesson <= 18; lesson++)
        {
            Touch($"Steph Curry Teaches Shooting/{lesson:00} Lesson.mp4");
        }

        Touch("Steph Curry Teaches Shooting/Workbook.pdf");

        Assert.Equal(["Steph Curry Teaches Shooting/Workbook.pdf"], Titles(Tv(), "Steph Curry Teaches Shooting"));
    }

    [Fact]
    public void AClipNamedLikeALesson_StaysAnEpisode_EvenInAFolderOfUnnumberedFiles()
    {
        Touch("Course/Lesson 07.mp4");
        Touch("Course/Season 1/Lesson 08.mp4");

        Assert.Empty(Titles(Tv(), "Course"));
    }

    // ── Shows ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ABehindTheScenesClipBesideTheSeasons_IsAnExtra()
    {
        Touch("Show/Season 1/Show S01E01.mkv");
        Touch("Show/Season 1/Show S01E02.mkv");
        Touch("Show/Making of.mkv");

        Assert.Equal(["Show/Making of.mkv"], Titles(Tv(), "Show"));
    }

    [Fact]
    public void ANumberedEpisode_IsNeverAnExtra_WhateverItsFolder()
    {
        Touch("Show/Season 1/S01E01.mkv");
        Touch("Show/Season 2/2x05 Title.mkv");
        Touch("Show/Specials/Episode 3.mkv");

        Assert.Empty(Titles(Tv(), "Show"));
    }

    [Fact]
    public void AClipInAnExtrasFolder_IsAnExtra_EvenWithAnEpisodeLikeName()
    {
        Touch("Show/Season 1/S01E01.mkv");
        Touch("Show/Extras/S01E01 Bloopers.mkv");

        Assert.Equal(["Show/Extras/S01E01 Bloopers.mkv"], Titles(Tv(), "Show"));
    }

    [Fact]
    public void Pictures_GroupByFolder_AndArtworkIsNotListed()
    {
        Touch("Show/Season 1/S01E01.mkv");
        Touch("Show/poster.jpg");
        Touch("Show/fanart.jpg");
        Touch("Show/Season 1/season01-poster.jpg");
        Touch("Show/Season 1/S01E01-thumb.jpg");
        Touch("Show/Extras/Concept art/a.jpg");
        Touch("Show/Extras/Concept art/b.jpg");

        var extras = ExtrasRule.ScanItemFolder(Tv(), Path.Combine(_root, "Show"));

        Assert.Equal(2, extras.Count);
        Assert.All(extras, extra => Assert.Equal(ItemExtraKinds.Picture, extra.Kind));
        Assert.All(extras, extra => Assert.Equal("Extras/Concept art", extra.GroupKey));
    }

    [Fact]
    public void FilesRuleIgnores_NeverBecomeExtras()
    {
        Touch("Show/Season 1/S01E01.mkv");
        Touch("Show/Season 1/S01E01.srt");
        Touch("Show/tvshow.nfo");
        Touch("Show/Thumbs.db");
        Touch("Show/.hidden.pdf");
        Touch("Show/.data/cache.pdf");
        Touch("Show/Guide.pdf.part");
        Touch("Show/tool.exe");

        Assert.Empty(Titles(Tv(), "Show"));
    }

    [Fact]
    public void ALooseFileInTheLibraryFolder_BelongsToNoTitle()
    {
        var loose = Touch("Workbook.pdf");

        Assert.Null(ExtrasRule.Classify(Tv(), loose));
    }

    // ── Films ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFilmsTrailerAndFeaturetteFolder_AreExtras_ButTheFilmIsNot()
    {
        Touch("Dune (2021)/Dune (2021).mkv");
        Touch("Dune (2021)/Dune (2021)-trailer.mkv");
        Touch("Dune (2021)/Featurettes/The sound.mp4");
        Touch("Dune (2021)/poster.jpg");
        Touch("Dune (2021)/Dune (2021).jpg");

        Assert.Equal(
            ["Dune (2021)/Dune (2021)-trailer.mkv", "Dune (2021)/Featurettes/The sound.mp4"],
            Titles(Movies(), "Dune (2021)"));
    }

    [Fact]
    public void TwoPartsOfOneFilm_StayTheFilmsOwnFiles()
    {
        Touch("Epic (2000)/Epic (2000) part1.mkv");
        Touch("Epic (2000)/Epic (2000) part2.mkv");

        Assert.Empty(Titles(Movies(), "Epic (2000)"));
    }

    [Fact]
    public void AnExtrasFolderIsNotTheFilmsFolder()
    {
        Touch("Film/Film.mkv");
        var clip = Touch("Film/Extras/Interview.mkv");

        Assert.Equal(Path.Combine(_root, "Film"), ExtrasRule.ResolveItemFolder(Movies(), clip));
    }

    // ── Albums, books, comics ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAlbumsBookletPdf_IsAnExtra_ButItsCoverIsNot()
    {
        Touch("Artist/Album/01 Song.flac");
        Touch("Artist/Album/02 Song.flac");
        Touch("Artist/Album/Booklet.pdf");
        Touch("Artist/Album/cover.jpg");
        Touch("Artist/Album/folder.jpg");
        Touch("Artist/Album/Video.mp4");

        Assert.Equal(["Artist/Album/Booklet.pdf", "Artist/Album/Video.mp4"], Titles(Music(), "Artist/Album"));
    }

    [Fact]
    public void ABooksPdf_IsTheBookItself_UnlessItsNameSaysGuide()
    {
        Touch("Author/Novel/Novel.epub");
        Touch("Author/Novel/Novel.pdf");
        Touch("Author/Novel/Reading guide.pdf");

        Assert.Equal(["Author/Novel/Reading guide.pdf"], Titles(Books(), "Author/Novel"));
    }

    [Fact]
    public void AComicSeriesBonusPdf_IsAnExtra_ButAPdfIssueIsNot()
    {
        Touch("Saga/Saga 001.cbz");
        Touch("Saga/Saga 002.cbz");
        Touch("Saga/Saga 003.pdf");
        Touch("Saga/Bonus sketchbook.pdf");

        Assert.Equal(["Saga/Bonus sketchbook.pdf"], Titles(Comics(), "Saga"));
    }

    [Fact]
    public void AFolderOfManyFilms_IsNotOneTitle_SoItsStrayFilesBelongToNothing()
    {
        for (var film = 1; film <= 5; film++)
        {
            Touch($"Pile/Film {film}.mkv");
        }

        var pdf = Touch("Pile/Notes.pdf");

        Assert.Null(ExtrasRule.Classify(Movies(), pdf));
    }

    // ── Videos courses ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACoursesHomeworkIsAnExtra_ButItsVideosAreAllLessons()
    {
        Touch("Course/Intro.mp4");
        Touch("Course/Basics.mp4");
        Touch("Course/Homework.pdf");

        Assert.Equal(["Course/Homework.pdf"], Titles(Courses(), "Course"));
    }

    // ── Folders to check ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ForShows_EveryTopFolderIsATitle()
    {
        Touch("Show A/S01E01.mkv");
        Touch("Show B/S01E01.mkv");

        var folders = ExtrasRule.EnumerateItemFolders(Tv()).Select(Path.GetFileName).Order().ToList();

        Assert.Equal(["Show A", "Show B"], folders);
    }

    [Fact]
    public void ForFilms_OnlyFoldersHoldingAFilmAreTitles_AndExtrasFoldersAreNot()
    {
        Touch("Film A/Film A.mkv");
        Touch("Film A/Extras/Clip.mkv");

        var folders = ExtrasRule.EnumerateItemFolders(Movies()).Select(Path.GetFileName).ToList();

        Assert.Equal(["Film A"], folders);
    }
}
