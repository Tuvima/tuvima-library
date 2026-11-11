using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Tests.Helpers;
using MediaEngine.Processors.Models;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

public sealed class MediaTypeResolverTests
{
    [Fact]
    public async Task LibraryFolderPrior_MatchesEveryConfiguredSourcePath()
    {
        var rootA = TestRoot("books-a");
        var rootB = TestRoot("books-b");
        var options = Options(
            watchDirectories: [rootA, rootB],
            libraryFolders:
            [
                new LibraryFolderEntry
                {
                    Sources = [Source(rootA), Source(rootB)],
                    MediaTypes = [MediaType.Audiobooks],
                },
            ]);

        var resolver = CreateResolver(options);
        var filePath = Path.Combine(rootB, "incoming", "incoming.mp3");

        var resolution = await resolver.ResolveAsync(
            filePath,
            Result(MediaType.Unknown,
            [
                Candidate(MediaType.Music, 0.72),
                Candidate(MediaType.Audiobooks, 0.62),
            ]),
            0,
            CancellationToken.None);

        Assert.Equal(MediaType.Audiobooks, resolution.MediaType);
        Assert.False(resolution.NeedsReview);
        Assert.True(resolution.Candidates[0].Confidence >= 0.98);
    }

    [Fact]
    public async Task RootWatchFolderCap_AppliesPerConfiguredRoot_AndSkipsUnambiguousExtensions()
    {
        var rootA = TestRoot("root-a");
        var rootB = TestRoot("root-b");
        var options = Options(watchDirectories: [rootA, rootB]);
        var resolver = CreateResolver(options);

        var ambiguous = await resolver.ResolveAsync(
            Path.Combine(rootB, "clip.mp3"),
            Result(MediaType.Unknown, [Candidate(MediaType.Music, 0.95)]),
            0.1,
            CancellationToken.None);

        Assert.True(ambiguous.RootWatchFolderReview);
        Assert.True(ambiguous.NeedsReview);
        Assert.Equal(MediaType.Unknown, ambiguous.MediaType);
        Assert.Equal(0.0, ambiguous.CategoryConfidencePrior);
        Assert.Equal(0.40, ambiguous.Candidates[0].Confidence, precision: 2);

        var unambiguous = await resolver.ResolveAsync(
            Path.Combine(rootB, "book.epub"),
            Result(MediaType.Books, [Candidate(MediaType.Books, 0.95)]),
            0.1,
            CancellationToken.None);

        Assert.False(unambiguous.RootWatchFolderReview);
        Assert.False(unambiguous.NeedsReview);
        Assert.Equal(MediaType.Books, unambiguous.MediaType);
        Assert.Equal(0.1, unambiguous.CategoryConfidencePrior);
    }

    [Fact]
    public async Task RootWatchFolderCap_DoesNotDowngradeSingleTypeLibraryRoot()
    {
        var booksRoot = TestRoot("single-type-books");
        var options = Options(
            watchDirectories: [booksRoot],
            libraryFolders:
            [
                new LibraryFolderEntry
                {
                    Sources = [Source(booksRoot)],
                    MediaTypes = [MediaType.Books],
                },
            ]);
        var resolver = CreateResolver(options);

        var resolution = await resolver.ResolveAsync(
            Path.Combine(booksRoot, "Dune - Scott Brick.mp3"),
            Result(MediaType.Unknown, [Candidate(MediaType.Music, 0.95)]),
            0.1,
            CancellationToken.None);

        Assert.False(resolution.RootWatchFolderReview);
        Assert.False(resolution.NeedsReview);
        Assert.Equal(MediaType.Books, resolution.MediaType);
        Assert.True(resolution.Candidates[0].Confidence >= 0.95);
        Assert.Equal(0.1, resolution.CategoryConfidencePrior);
    }

    [Fact]
    public async Task SingleTypeComicsFolder_CanOverrideAmbiguousPdfBookDetection()
    {
        var comicsRoot = TestRoot("single-type-comics-pdf");
        var options = Options(
            watchDirectories: [comicsRoot],
            libraryFolders:
            [
                new LibraryFolderEntry
                {
                    Sources = [Source(comicsRoot)],
                    MediaTypes = [MediaType.Comics],
                },
            ]);
        var resolver = CreateResolver(options);

        var resolution = await resolver.ResolveAsync(
            Path.Combine(comicsRoot, "Saga", "Saga 001.pdf"),
            Result(MediaType.Books, [Candidate(MediaType.Books, 0.91)]),
            0,
            CancellationToken.None);

        Assert.Equal(MediaType.Comics, resolution.MediaType);
        Assert.False(resolution.NeedsReview);
        Assert.Equal(MediaType.Comics, resolution.Candidates[0].Type);
        Assert.True(resolution.Candidates[0].Confidence >= 0.95);
    }

    [Theory]
    [InlineData(MediaType.Movies)]
    [InlineData(MediaType.TV)]
    [InlineData(MediaType.Music)]
    public async Task APdfNoTitleClaimedInAVideoOrMusicLibrary_GoesToReview_NeverGuessedIntoThatType(MediaType libraryType)
    {
        var root = TestRoot("stray-pdf-" + libraryType);
        var options = Options(
            watchDirectories: [root],
            libraryFolders: [new LibraryFolderEntry { Sources = [Source(root)], MediaTypes = [libraryType] }]);
        var resolver = CreateResolver(options);

        var resolution = await resolver.ResolveAsync(
            Path.Combine(root, "booklet.pdf"),
            Result(MediaType.Books, [Candidate(MediaType.Books, 0.91)]),
            0.5,
            CancellationToken.None);

        Assert.Equal(MediaType.Unknown, resolution.MediaType);
        Assert.True(resolution.NeedsReview);
        Assert.Equal(0.0, resolution.CategoryConfidencePrior);
        Assert.DoesNotContain(resolution.Candidates, candidate => candidate.Type == libraryType);
    }

    [Fact]
    public async Task SingleTypeBooksFolder_KeepsPdfBooksAsBooks()
    {
        var booksRoot = TestRoot("single-type-books-pdf");
        var options = Options(
            watchDirectories: [booksRoot],
            libraryFolders:
            [
                new LibraryFolderEntry
                {
                    Sources = [Source(booksRoot)],
                    MediaTypes = [MediaType.Books],
                },
            ]);
        var resolver = CreateResolver(options);

        var resolution = await resolver.ResolveAsync(
            Path.Combine(booksRoot, "Dune.pdf"),
            Result(MediaType.Books, [Candidate(MediaType.Books, 0.91)]),
            0,
            CancellationToken.None);

        Assert.Equal(MediaType.Books, resolution.MediaType);
        Assert.False(resolution.NeedsReview);
        Assert.Equal(MediaType.Books, resolution.Candidates[0].Type);
    }

    [Theory]
    [InlineData(MediaType.TV, "Shows", "Breaking Bad - S01E01.mkv")]
    [InlineData(MediaType.Movies, "Movies", "Dune Part Two.mp4")]
    public async Task SingleTypeWatchFolders_CanClassifyAmbiguousVideoContainers(
        MediaType configuredType,
        string folderName,
        string fileName)
    {
        var videoRoot = TestRoot($"single-type-{folderName.ToLowerInvariant()}-video");
        var options = Options(
            watchDirectories: [videoRoot],
            libraryFolders:
            [
                new LibraryFolderEntry
                {
                    Sources = [Source(videoRoot)],
                    MediaTypes = [configuredType],
                },
            ]);
        var resolver = CreateResolver(options);

        var resolution = await resolver.ResolveAsync(
            Path.Combine(videoRoot, folderName, fileName),
            Result(MediaType.Unknown, []),
            0,
            CancellationToken.None);

        Assert.Equal(configuredType, resolution.MediaType);
        Assert.False(resolution.NeedsReview);
        Assert.Equal(configuredType, resolution.Candidates[0].Type);
    }

    [Theory]
    [InlineData(".epub", MediaType.Books, MediaType.Comics)]
    [InlineData(".cbz", MediaType.Comics, MediaType.Books)]
    [InlineData(".cbr", MediaType.Comics, MediaType.Books)]
    [InlineData(".m4b", MediaType.Audiobooks, MediaType.Music)]
    public async Task StrongFormats_DoNotCrossMediaTypeBecauseOfSingleTypeFolder(
        string extension,
        MediaType processorType,
        MediaType configuredType)
    {
        var root = TestRoot($"strong-format-{extension.TrimStart('.')}");
        var options = Options(
            watchDirectories: [root],
            libraryFolders:
            [
                new LibraryFolderEntry
                {
                    Sources = [Source(root)],
                    MediaTypes = [configuredType],
                },
            ]);
        var resolver = CreateResolver(options);

        var resolution = await resolver.ResolveAsync(
            Path.Combine(root, "incoming", $"sample{extension}"),
            Result(processorType, [Candidate(processorType, 0.92), Candidate(configuredType, 0.65)]),
            0,
            CancellationToken.None);

        Assert.Equal(processorType, resolution.MediaType);
        Assert.False(resolution.NeedsReview);
        Assert.Equal(processorType, resolution.Candidates[0].Type);
    }

    [Fact]
    public async Task Advisor_IsCalledOnlyForUnknownOrLowConfidenceCases()
    {
        var options = Options(watchDirectories: [Path.Combine(Path.GetTempPath(), "advisor")]);
        var advisor = new CountingAdvisor(Candidate(MediaType.Audiobooks, 0.91));
        var resolver = CreateResolver(options, advisor);

        await resolver.ResolveAsync(
            Path.Combine(options.WatchDirectories[0], "movie.mkv"),
            Result(MediaType.Movies, [Candidate(MediaType.Movies, 0.92)]),
            0,
            CancellationToken.None);

        Assert.Equal(0, advisor.CallCount);

        await resolver.ResolveAsync(
            Path.Combine(options.WatchDirectories[0], "unknown.m4a"),
            Result(MediaType.Unknown, []),
            0,
            CancellationToken.None);

        Assert.Equal(1, advisor.CallCount);
    }

    [Theory]
    [InlineData(0.90, MediaType.Music, false, false)]
    [InlineData(0.70, MediaType.Music, true, true)]
    [InlineData(0.40, MediaType.Unknown, true, true)]
    public async Task Outcomes_FollowConfiguredThresholds(
        double confidence,
        MediaType expectedType,
        bool expectedConflict,
        bool expectedReview)
    {
        var root = Path.Combine(TestRoot("thresholds"), "nested");
        var options = Options(watchDirectories: [TestRoot("threshold-watch")]);
        var resolver = CreateResolver(options);

        var resolution = await resolver.ResolveAsync(
            Path.Combine(root, "track.mp3"),
            Result(MediaType.Unknown, [Candidate(MediaType.Music, confidence)]),
            0,
            CancellationToken.None);

        Assert.Equal(expectedType, resolution.MediaType);
        Assert.Equal(expectedConflict, resolution.IsConflicted);
        Assert.Equal(expectedReview, resolution.NeedsReview);
    }

    private static MediaTypeResolver CreateResolver(
        IngestionOptions options,
        IMediaTypeAdvisor? advisor = null)
    {
        var monitor = new OptionsMonitorStub<IngestionOptions>(options);
        return new MediaTypeResolver(
            monitor,
            new LibraryFolderResolver(monitor),
            advisor ?? new CountingAdvisor(Candidate(MediaType.Unknown, 0)),
            new MediaTypeExtensionCatalog(new StubConfigurationLoader()),
            NullLogger<MediaTypeResolver>.Instance);
    }

    private static IngestionOptions Options(
        IReadOnlyList<string> watchDirectories,
        IReadOnlyList<LibraryFolderEntry>? libraryFolders = null) => new()
        {
            WatchDirectories = watchDirectories,
            LibraryFolders = libraryFolders ?? [],
            MediaTypeAutoAssignThreshold = 0.85,
            MediaTypeReviewThreshold = 0.50,
        };

    private static ProcessorResult Result(
        MediaType detectedType,
        IReadOnlyList<MediaTypeCandidate> candidates) => new()
        {
            FilePath = "test",
            DetectedType = detectedType,
            MediaTypeCandidates = candidates,
        };

    private static MediaTypeCandidate Candidate(MediaType type, double confidence) => new()
    {
        Type = type,
        Confidence = confidence,
        Reason = "test",
    };

    private static string TestRoot(string name) =>
        Path.Combine(Path.GetTempPath(), "tuvima-media-type-resolver-tests", name);

    private static LibrarySourceEntry Source(string path) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Path = path,
    };

    private sealed class CountingAdvisor : IMediaTypeAdvisor
    {
        private readonly MediaTypeCandidate _candidate;

        public CountingAdvisor(MediaTypeCandidate candidate)
            => _candidate = candidate;

        public int CallCount { get; private set; }

        public Task<MediaTypeCandidate> ClassifyAsync(
            string filename,
            string? container,
            double? durationSeconds,
            int? bitrate,
            string? genre,
            bool hasChapters,
            string? folderPath,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(_candidate);
        }
    }
}
