using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Extras;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Tests.Helpers;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// The placement rule for supporting files (editor v5 spec section 3b and acceptance A6): who a file belongs to, which episode or lesson
/// it is shown alongside, what it is, which group it falls in, and what is never a supporting file at all.
/// </summary>
public sealed class SupportingFilePlacementTests : IDisposable
{
    private static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".mpeg", ".mpg", ".m2ts",
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_supporting_{Guid.NewGuid():N}");

    public SupportingFilePlacementTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ExtrasLibraryContext Tv() => new("tv", [MediaType.TV], _root, VideoExtensions);

    private ExtrasLibraryContext Movies() => new("movies", [MediaType.Movies], _root, VideoExtensions);

    private ExtrasLibraryContext Course() => new("videos", [MediaType.Videos], _root, VideoExtensions);

    private string Touch(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[4]);
        return path;
    }

    private ExtraCandidate Placed(ExtrasLibraryContext context, string itemFolder, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return ExtrasRule.ScanItemFolder(context, Path.Combine(_root, itemFolder)).Single(extra => extra.FilePath == path);
    }

    // ── A6: where a file belongs ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFileInAShowsRootFolder_BelongsToTheShow()
    {
        Touch("Show/Season 01/Show S01E01.mkv");
        Touch("Show/Season 02/Show S02E01.mkv");
        Touch("Show/Workbook.pdf");

        var workbook = Placed(Tv(), "Show", "Show/Workbook.pdf");

        Assert.Equal(ExtraScope.Root, workbook.Scope);
        Assert.Null(workbook.Alongside);
        Assert.Equal(ExtraRoles.Worksheet, workbook.Role);
        Assert.Equal("Workbook", workbook.Title);
    }

    [Fact]
    public void AFileInASeasonFolder_BelongsToThatSeason()
    {
        Touch("Show/Season 01/Show S01E01.mkv");
        Touch("Show/Season 02/Show S02E01.mkv");
        Touch("Show/Season 02/season2-characters.pdf");

        var guide = Placed(Tv(), "Show", "Show/Season 02/season2-characters.pdf");

        Assert.Equal(ExtraScope.ForSeason(2), guide.Scope);
        Assert.Null(guide.Alongside);
        Assert.Equal(ExtraRoles.Other, guide.Role);
    }

    [Fact]
    public void AFileInASpecialsFolder_BelongsToSeasonZero()
    {
        Touch("Show/Season 01/Show S01E01.mkv");
        Touch("Show/Specials/Show S00E01.mkv");
        Touch("Show/Specials/notes.pdf");

        Assert.Equal(ExtraScope.ForSeason(0), Placed(Tv(), "Show", "Show/Specials/notes.pdf").Scope);
    }

    [Fact]
    public void AWorksheetBesideALessonInACoursesSection_BelongsToTheSection_AndIsShownAlongsideThatLesson()
    {
        Touch("Course/Section 1/01 Welcome.mkv");
        Touch("Course/Section 1/02 Components.mkv");
        Touch("Course/Section 1/03 State.mkv");
        Touch("Course/Section 1/02-component-exercises.pdf");
        Touch("Course/Section 2/01 Routing.mkv");

        var worksheet = Placed(Course(), "Course", "Course/Section 1/02-component-exercises.pdf");

        Assert.Equal(ExtraScope.ForFolder("Section 1"), worksheet.Scope);
        Assert.Equal(ExtraScope.ForFile("Section 1/02 Components.mkv"), worksheet.Alongside);
        Assert.Equal(ExtraRoles.Worksheet, worksheet.Role);
        Assert.Equal("02-component-exercises", worksheet.Title);
    }

    [Fact]
    public void AFileAtTheTopOfACourse_BelongsToTheCourse_AndSitsAlongsideNothingUnlessItsNameSaysWhich()
    {
        Touch("Course/Section 1/01 Welcome.mkv");
        Touch("Course/Syllabus.pdf");

        var syllabus = Placed(Course(), "Course", "Course/Syllabus.pdf");

        Assert.Equal(ExtraScope.Root, syllabus.Scope);
        Assert.Null(syllabus.Alongside);
    }

    [Fact]
    public void AFileNamedAfterAnEpisodeWithASuffix_BelongsToThatEpisode()
    {
        Touch("Show/Season 02/Show S02E03.mkv");
        Touch("Show/Season 02/Show S02E04.mkv");
        Touch("Show/Season 02/Show S02E03-notes.pdf");

        var notes = Placed(Tv(), "Show", "Show/Season 02/Show S02E03-notes.pdf");

        Assert.Equal(ExtraScope.ForEpisode(2, 3), notes.Scope);
        Assert.Null(notes.Alongside);
        Assert.Equal(ExtraRoles.Notes, notes.Role);
    }

    [Fact]
    public void AFileInAFolderNamedAfterAnEpisode_BelongsToThatEpisode()
    {
        Touch("Show/Season 02/Show S02E03.mkv");
        Touch("Show/Season 02/Show S02E04.mkv");
        Touch("Show/Season 02/Show S02E03/handout.pdf");

        Assert.Equal(ExtraScope.ForEpisode(2, 3), Placed(Tv(), "Show", "Show/Season 02/Show S02E03/handout.pdf").Scope);
    }

    [Fact]
    public void ASeasonGuideThatNamesAnEpisodeCode_IsShownAlongsideThatEpisode()
    {
        Touch("Show/Season 02/Show S02E03.mkv");
        Touch("Show/Season 02/Show S02E04.mkv");
        Touch("Show/Season 02/homework for S02E04.pdf");
        Touch("Show/Season 02/homework for S02E09.pdf");

        var homework = Placed(Tv(), "Show", "Show/Season 02/homework for S02E04.pdf");
        var noSuchEpisode = Placed(Tv(), "Show", "Show/Season 02/homework for S02E09.pdf");

        Assert.Equal(ExtraScope.ForSeason(2), homework.Scope);
        Assert.Equal(ExtraScope.ForEpisode(2, 4), homework.Alongside);
        Assert.Null(noSuchEpisode.Alongside);
    }

    [Fact]
    public void AFileInAnExtrasFolder_StillBelongsToTheSeasonOrShowAboveIt()
    {
        Touch("Show/Season 02/Show S02E01.mkv");
        Touch("Show/Season 02/Extras/Interviews/cast talk.mkv");
        Touch("Show/Extras/Making of the pilot.mkv");

        Assert.Equal(ExtraScope.ForSeason(2), Placed(Tv(), "Show", "Show/Season 02/Extras/Interviews/cast talk.mkv").Scope);
        Assert.Equal(ExtraScope.Root, Placed(Tv(), "Show", "Show/Extras/Making of the pilot.mkv").Scope);
    }

    // ── Role, group and title ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Show/Trailer.mkv", ExtraRoles.Trailer)]
    [InlineData("Show/Extras/Behind the scenes of the pilot.mkv", ExtraRoles.BehindTheScenes)]
    [InlineData("Show/Extras/Deleted scenes.mkv", ExtraRoles.DeletedScene)]
    [InlineData("Show/Extras/Cast interview.mkv", ExtraRoles.Interview)]
    [InlineData("Show/Extras/Something else.mkv", ExtraRoles.Clip)]
    [InlineData("Show/Extras/Trailers/teaser.mkv", ExtraRoles.Trailer)]
    [InlineData("Show/Documents/Study guide.pdf", ExtraRoles.Guide)]
    [InlineData("Show/Documents/Worksheets/week1.pdf", ExtraRoles.Worksheet)]
    [InlineData("Show/Documents/Lecture transcript.pdf", ExtraRoles.Transcript)]
    [InlineData("Show/Documents/Liner notes.pdf", ExtraRoles.Booklet)]
    [InlineData("Show/Documents/Reading list.pdf", ExtraRoles.Other)]
    [InlineData("Show/Extras/Concept art/a.jpg", ExtraRoles.Pictures)]
    public void ARoleIsGuessedFromTheTypeThenTheFileAndFolderNames(string relative, string expected)
    {
        Touch("Show/Season 01/Show S01E01.mkv");
        Touch("Show/Season 01/Show S01E02.mkv");
        Touch(relative);

        Assert.Equal(expected, Placed(Tv(), "Show", relative).Role);
    }

    [Fact]
    public void ARoleWordThatDoesNotFitTheKindOfFile_IsIgnored()
    {
        Touch("Show/Season 01/Show S01E01.mkv");
        Touch("Show/Documents/Trailer script.pdf");

        // "trailer" is a video role; a document can only be a document role ("script" says Transcript).
        Assert.Equal(ExtraRoles.Transcript, Placed(Tv(), "Show", "Show/Documents/Trailer script.pdf").Role);
    }

    [Fact]
    public void ANamedFolderUnderExtras_GivesTheGroup_ButARoleFolderDoesNot()
    {
        Touch("Show/Season 01/Show S01E01.mkv");
        Touch("Show/Extras/Character guides/x.pdf");
        Touch("Show/Extras/Trailers/teaser.mkv");
        Touch("Show/Extras/loose.pdf");
        Touch("Show/Season 01/Documents/Week 1/exercises.pdf");

        Assert.Equal("Character guides", Placed(Tv(), "Show", "Show/Extras/Character guides/x.pdf").Group);
        Assert.Null(Placed(Tv(), "Show", "Show/Extras/Trailers/teaser.mkv").Group);
        Assert.Null(Placed(Tv(), "Show", "Show/Extras/loose.pdf").Group);
        var week = Placed(Tv(), "Show", "Show/Season 01/Documents/Week 1/exercises.pdf");
        Assert.Equal("Week 1", week.Group);
        Assert.Equal(ExtraScope.ForSeason(1), week.Scope);
    }

    [Fact]
    public void APictureFolder_StaysOneItemAndKeepsItsOwnPlace()
    {
        Touch("Show/Season 01/Show S01E01.mkv");
        Touch("Show/Extras/Concept art/a.jpg");
        Touch("Show/Extras/Concept art/b.jpg");

        var pictures = ExtrasRule.ScanItemFolder(Tv(), Path.Combine(_root, "Show"));

        Assert.All(pictures, picture => Assert.Equal("Extras/Concept art", picture.GroupKey));
        Assert.All(pictures, picture => Assert.Equal("Concept art", picture.Group));
        Assert.All(pictures, picture => Assert.Equal(ExtraScope.Root, picture.Scope));
    }

    // ── What is never a supporting file ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchivesMetadataAndSubtitlesNeverBecomeSupportingFiles()
    {
        Touch("Show/Season 01/Show S01E01.mkv");
        Touch("Show/Season 01/Show S01E01.nfo");
        Touch("Show/Season 01/Show S01E01.en.srt");
        Touch("Show/Season 01/Show S01E01.ass");
        Touch("Show/Season 01/Show S01E01.vtt");
        Touch("Show/Extras/bonus material.zip");
        Touch("Show/Extras/pack.7z");
        Touch("Show/Extras/pack.rar");
        Touch("Show/Season 01/tvshow.xml");
        Touch("Show/Season 01/info.json");
        Touch("Show/Season 01/Show S01E01-poster.jpg");
        Touch("Show/poster.jpg");
        Touch("Show/Season 01/fanart.jpg");
        Touch("Show/Workbook.pdf");

        var found = ExtrasRule.ScanItemFolder(Tv(), Path.Combine(_root, "Show"))
            .Select(extra => Path.GetFileName(extra.FilePath))
            .ToList();

        Assert.Equal(["Workbook.pdf"], found);
    }

    [Fact]
    public void TheRuleKnowsWhichFilesAreHidden_SoTheBrowserCanListThemGreyed()
    {
        Touch("Show/Season 01/Show S01E01.mkv");
        var zip = Touch("Show/Extras/pack.zip");
        var subtitles = Touch("Show/Season 01/Show S01E01.srt");
        var poster = Touch("Show/poster.jpg");
        var guide = Touch("Show/Workbook.pdf");

        Assert.True(ExtrasRule.IsHidden(Tv(), zip));
        Assert.True(ExtrasRule.IsHidden(Tv(), subtitles));
        Assert.True(ExtrasRule.IsHidden(Tv(), poster));
        Assert.False(ExtrasRule.IsHidden(Tv(), guide));
        Assert.True(ExtrasRule.IsHiddenType(".nfo"));
        Assert.False(ExtrasRule.IsExtraKindExtension(".zip"));
    }

    // ── The suffix rule that missed "Season 1 trailer.mkv" ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AClipWhoseNameEndsInAWordAfterASpace_IsAnExtra()
    {
        Touch("Film/Film.mkv");
        Touch("Film/Film trailer.mkv");
        Touch("Show/Season 1/Show S01E01.mkv");
        Touch("Show/Season 1/Season 1 trailer.mkv");

        var filmTrailer = Assert.Single(ExtrasRule.ScanItemFolder(Movies(), Path.Combine(_root, "Film")));
        Assert.Equal("Film trailer.mkv", Path.GetFileName(filmTrailer.FilePath));
        Assert.Equal(ExtraRoles.Trailer, filmTrailer.Role);
        var showTrailer = Placed(Tv(), "Show", "Show/Season 1/Season 1 trailer.mkv");
        Assert.Equal(ExtraRoles.Trailer, showTrailer.Role);
        Assert.Equal(ExtraScope.ForSeason(1), showTrailer.Scope);
    }

    [Fact]
    public void AnEpisodeWhoseTitleEndsInARoleWord_StaysAnEpisode()
    {
        Touch("Show/Season 1/Show S01E01.mkv");
        Touch("Show/Season 1/Show S01E02 The Interview.mkv");
        Touch("Show/Season 1/Show S01E03 Short.mkv");

        Assert.Empty(ExtrasRule.ScanItemFolder(Tv(), Path.Combine(_root, "Show")));
    }

    [Fact]
    public void AStandaloneTrailerWithDashesAndDots_StillWorks()
    {
        Touch("Film/Film.mkv");
        Touch("Film/Film-trailer.mkv");
        Touch("Film/Film.deleted.mkv");

        var found = ExtrasRule.ScanItemFolder(Movies(), Path.Combine(_root, "Film")).Select(extra => extra.Role).Order().ToList();

        Assert.Equal([ExtraRoles.DeletedScene, ExtraRoles.Trailer], found);
    }

    // ── The words live in configuration ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheShippedRulesFile_IsValid_AndListsEveryKindOfFileWithItsRoles()
    {
        var rules = ExtrasConfigurationProvider.BuiltIn;

        Assert.Equal(["guide", "worksheet", "transcript", "notes", "booklet", "other"], rules.RolesFor(ItemExtraKinds.Document).Select(role => role.Id));
        Assert.Equal(["trailer", "featurette", "behind_the_scenes", "deleted_scene", "interview", "clip"], rules.RolesFor(ItemExtraKinds.Clip).Select(role => role.Id));
        Assert.Equal(["pictures"], rules.RolesFor(ItemExtraKinds.Picture).Select(role => role.Id));
        Assert.Equal("Behind the scenes", rules.LabelFor(ItemExtraKinds.Clip, ExtraRoles.BehindTheScenes));
        Assert.Equal("Documents", rules.FamilyFor(ItemExtraKinds.Document).Label);
        Assert.Equal(30, rules.MissingFileKeepDays);
        Assert.Contains(".zip", rules.HiddenTypes.Extensions);
        Assert.All(rules.RoleWords, entry => Assert.Contains(rules.Roles.Values.SelectMany(list => list), role => role.Id == entry.Role));
    }

    [Fact]
    public void TheConfigFileInTheRepository_IsTheSameAsTheOneBuiltIntoTheEngine()
    {
        var repoFile = FindRepoFile("config/extras.json");
        var onDisk = ExtrasConfigurationProvider.TryRead(File.ReadAllText(repoFile));

        Assert.NotNull(onDisk);
        Assert.Equal(ExtrasConfigurationProvider.BuiltIn.AcceptedTypes.Documents, onDisk.AcceptedTypes.Documents);
        Assert.Equal(ExtrasConfigurationProvider.BuiltIn.RoleWords.Count, onDisk.RoleWords.Count);
    }

    [Fact]
    public void AWordAddedToTheConfiguration_ChangesTheGuessWithoutAnyCodeChange()
    {
        Touch("Show/Season 1/Show S01E01.mkv");
        Touch("Show/Documents/Syllabus-2024.pdf");
        var rules = ExtrasConfigurationProvider.TryRead(File.ReadAllText(FindRepoFile("config/extras.json")))!;
        rules.RoleWords.Single(entry => entry.Role == ExtraRoles.Guide).Words.Add("syllabus");
        var context = Tv() with { Rules = rules };

        var guide = ExtrasRule.ScanItemFolder(context, Path.Combine(_root, "Show")).Single();

        Assert.Equal(ExtraRoles.Guide, guide.Role);
    }

    private static string FindRepoFile(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{relative} was not found above the test folder.");
    }
}

/// <summary>A person's values on supporting files survive the scanner's rescans, end to end through the real store.</summary>
public sealed class SupportingFileRescanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_supporting_scan_{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_supporting_scan_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly ItemExtraRepository _extras;
    private readonly ItemExtrasScanner _scanner;

    public SupportingFileRescanTests()
    {
        Directory.CreateDirectory(_root);
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _extras = new ItemExtraRepository(_db);
        var library = new LibraryFolderEntry
        {
            Id = "courses",
            Name = "Courses",
            MediaTypes = [MediaType.Videos],
            Sources = [new LibrarySourceEntry { Id = "courses-1", Path = _root }],
        };
        var options = new OptionsMonitorStub<IngestionOptions>(new IngestionOptions { LibraryFolders = [library] });
        _scanner = new ItemExtrasScanner(
            new LibraryFolderResolver(options),
            new MediaTypeExtensionCatalog(),
            _extras,
            new NoVaultHook(),
            options,
            NullLogger<ItemExtrasScanner>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task APersonsTitleGroupAndScope_SurviveARescan_WhileWhatTheFolderSaysIsRecomputed()
    {
        var course = Path.Combine(_root, "Course");
        Touch("Course/Section 1/01 Welcome.mkv");
        Touch("Course/Section 1/02 Components.mkv");
        var worksheet = Touch("Course/Section 1/02-component-exercises.pdf");
        Touch("Course/Extras/Character guides/people.pdf");
        await _scanner.SyncItemFolderAsync(course);

        var stored = (await _extras.GetByPathAsync(worksheet))!;
        Assert.Equal(ExtraScope.ForFolder("Section 1"), stored.EffectiveScope);
        Assert.Equal(ExtraScope.ForFile("Section 1/02 Components.mkv"), stored.EffectiveAlongside);
        Assert.Equal(ExtraRoles.Worksheet, stored.EffectiveRole);
        var people = (await _extras.GetByPathAsync(Path.Combine(course, "Extras", "Character guides", "people.pdf")))!;
        Assert.Equal("Character guides", people.GroupName);

        await _extras.UpdateOwnedAsync(stored.Id, new ExtraOwnedUpdate
        {
            Title = ExtraValue<string>.Set("Components exercises"),
            GroupName = ExtraValue<string>.Set("Homework"),
            Scope = ExtraValue<ExtraScope>.Set(ExtraScope.Root),
        });

        // The lesson is renamed on disk, so the rule now reads a different alongside; the person's values stand.
        File.Move(Path.Combine(course, "Section 1", "02 Components.mkv"), Path.Combine(course, "Section 1", "02 Components of a page.mkv"));
        await _scanner.SyncItemFolderAsync(course);
        await _scanner.SyncItemFolderAsync(course);

        var after = (await _extras.GetByPathAsync(worksheet))!;
        Assert.Equal(stored.Id, after.Id);
        Assert.Equal("Components exercises", after.DisplayTitle);
        Assert.Equal("Homework", after.GroupName);
        Assert.Equal(ExtraScope.Root, after.EffectiveScope);
        Assert.Equal(ExtraScopeSources.You, after.ScopeSource);
        Assert.Equal(ExtraScope.ForFolder("Section 1"), after.InferredScope);
        Assert.Equal(ExtraScope.ForFile("Section 1/02 Components of a page.mkv"), after.InferredAlongside);
    }

    [Fact]
    public async Task AFileThatDisappears_KeepsItsRecordAndItsOverrides_AsFileMissing()
    {
        var course = Path.Combine(_root, "Course");
        Touch("Course/Section 1/01 Welcome.mkv");
        var worksheet = Touch("Course/Section 1/01-exercises.pdf");
        await _scanner.SyncItemFolderAsync(course);
        var id = (await _extras.GetByPathAsync(worksheet))!.Id;
        await _extras.UpdateOwnedAsync(id, new ExtraOwnedUpdate { Title = ExtraValue<string>.Set("My exercises") });

        File.Delete(worksheet);
        await _scanner.SyncItemFolderAsync(course);

        var kept = (await _extras.GetByPathAsync(worksheet))!;
        Assert.True(kept.IsMissingFromDisk);
        Assert.Equal("My exercises", kept.DisplayTitle);
    }

    private string Touch(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[4]);
        return path;
    }

    private sealed class NoVaultHook : IVaultIngestionHook
    {
        public Task<bool> IsVaultLibraryAsync(string? libraryId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> IsVaultAssetAsync(Guid assetId, CancellationToken ct = default) => Task.FromResult(false);

        public Task OnFileRegisteredAsync(VaultRegisteredFile file, CancellationToken ct = default) => Task.CompletedTask;
    }
}
