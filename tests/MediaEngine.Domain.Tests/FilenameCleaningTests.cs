using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

/// <summary>config/filename_cleaning.json (universal editor v5, D33): one list of file-name noise for the editor, the Videos cleaner and the scan.</summary>
public sealed class FilenameCleaningTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tuvima-filename-cleaning-" + Guid.NewGuid().ToString("N"));

    public FilenameCleaningTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void TheShippedFile_StatesEveryRuleAndCompiles()
    {
        var shipped = FilenameCleaningConfigurationProvider.TryRead(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "config", "filename_cleaning.json")), fallback: null, out var problem);

        Assert.True(shipped is not null, problem);
        Assert.NotEmpty(shipped!.Configuration.NoiseWords);
        Assert.NotEmpty(shipped.SeasonEpisode);
        Assert.NotEmpty(shipped.TrackPatterns);
        Assert.NotEmpty(shipped.Configuration.EditionMarkers);
    }

    [Theory]
    [InlineData("Dune.Part.One.2021.2160p.UHD.BluRay.x265.HDR10.DTS-HD.MA.5.1-GROUP", "Dune Part One", "2021")]
    [InlineData("The.Glass.Harbor.2023.1080p.H264.ATMOS", "The Glass Harbor", "2023")]
    [InlineData("Heat (1995) 720p WEB-DL AAC2.0 H.264 PROPER", "Heat", "1995")]
    [InlineData("Movie.Name.2019.480p.DVDRip.XviD.AC3.REPACK", "Movie Name", "2019")]
    public void ANameWithEveryFamilyOfNoiseWord_CleansToItsTitleAndYear(string name, string title, string year)
    {
        var rules = FilenameCleaningConfigurationProvider.BuiltIn;

        var stripped = rules.Tidy(rules.StripYear(rules.StripTags(name)));

        Assert.Equal(title, stripped);
        Assert.Equal(year, rules.FindYear(name));
    }

    [Theory]
    [InlineData("1080p")]
    [InlineData("WEB-DL")]
    [InlineData("webdl")]
    [InlineData("x265")]
    [InlineData("H.264")]
    [InlineData("TrueHD")]
    [InlineData("PROPER")]
    [InlineData("mkv")]
    [InlineData("540p")]
    public void EachFamilyOfNoise_IsRecognisedAsANoiseWord(string token)
    {
        Assert.True(FilenameCleaningConfigurationProvider.BuiltIn.IsNoiseWord(token));
    }

    [Theory]
    [InlineData("Harbor")]
    [InlineData("51")]
    [InlineData("1984")]
    public void AnOrdinaryTitleWord_IsNotNoise(string token)
    {
        Assert.False(FilenameCleaningConfigurationProvider.BuiltIn.IsNoiseWord(token));
    }

    [Fact]
    public void AWordAPersonAdded_IsStrippedLikeTheShippedOnes()
    {
        File.WriteAllText(Path.Combine(_directory, "filename_cleaning.json"), "{ \"version\": \"1\", \"custom_noise_words\": [\"FanSub\"] }");
        var provider = new FilenameCleaningConfigurationProvider(() => _directory);

        var rules = provider.Current;

        Assert.True(rules.IsNoiseWord("fansub"));
        Assert.Equal("My Show", rules.Tidy(rules.StripTags("My.Show.FanSub.1080p")));
        // The short file only added a word: every shipped rule still applies.
        Assert.True(rules.IsNoiseWord("x264"));
        Assert.NotEmpty(rules.SeasonEpisode);
    }

    [Fact]
    public void AFileThatChangesOnDisk_IsReadAgain()
    {
        var path = Path.Combine(_directory, "filename_cleaning.json");
        File.WriteAllText(path, "{ \"version\": \"1\" }");
        var provider = new FilenameCleaningConfigurationProvider(() => _directory);
        Assert.False(provider.Current.IsNoiseWord("fansub"));

        File.WriteAllText(path, "{ \"version\": \"1\", \"custom_noise_words\": [\"fansub\"] }");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.True(provider.Current.IsNoiseWord("fansub"));
    }

    [Fact]
    public void ABrokenFile_FallsBackToTheShippedRules_AndSaysSo()
    {
        File.WriteAllText(Path.Combine(_directory, "filename_cleaning.json"), "{ \"version\": \"1\", \"season_episode_patterns\": [\"(unclosed\"] }");
        var warnings = new List<string>();
        var provider = new FilenameCleaningConfigurationProvider(() => _directory, warnings.Add);

        var rules = provider.Current;

        Assert.Same(FilenameCleaningConfigurationProvider.BuiltIn, rules);
        Assert.Single(warnings);
        Assert.Contains("filename_cleaning.json", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ASeasonPatternWithoutTheGroups_IsRefused()
    {
        var read = FilenameCleaningConfigurationProvider.TryRead(
            "{ \"version\": \"1\", \"season_episode_patterns\": [\"S\\\\d+E\\\\d+\"] }", FilenameCleaningConfigurationProvider.BuiltIn, out var problem);

        Assert.Null(read);
        Assert.Contains("groups s and e", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1-05 Karma Police", 1, 5, "Karma Police")]
    [InlineData("03 - Paranoid Android", null, 3, "Paranoid Android")]
    [InlineData("Disc 2 - 07 Song Name", 2, 7, "Song Name")]
    public void AMusicFileName_GivesItsDiscAndTrackNumber(string name, int? disc, int track, string rest)
    {
        var read = FilenameCleaningConfigurationProvider.BuiltIn.ReadTrack(name);

        Assert.Equal(disc, read.Disc);
        Assert.Equal(track, read.Track);
        Assert.Equal(rest, read.Remainder);
    }

    [Theory]
    [InlineData("The Movie (Extended Cut) 2019", "Extended cut")]
    [InlineData("Movie.2019.Remastered.1080p.BluRay", "Remastered")]
    [InlineData("Live and Let Die 1973", null)]
    public void AnEditionMarker_CountsInBracketsOrAfterTheNoiseStarts_NotInsideATitle(string name, string? edition)
    {
        Assert.Equal(edition, FilenameCleaningConfigurationProvider.BuiltIn.FindEdition(name));
    }

    [Theory]
    [InlineData("Blade Runner 2049 (2017) 2160p HEVC", "Blade Runner 2049", "2017")]
    [InlineData("Magnum Opus", "Magnum Opus", null)]
    [InlineData("The Internal Affairs", "The Internal Affairs", null)]
    public void TheLookupHints_ShareTheSameNoiseWords(string input, string title, string? year)
    {
        var hints = ReleaseTitleHints.Parse(input, FilenameCleaningConfigurationProvider.BuiltIn);

        Assert.Equal(title, hints.Title);
        Assert.Equal(year, hints.Year);
    }

    [Fact]
    public void AWordAPersonAdded_AlsoReachesTheLookupHints()
    {
        File.WriteAllText(Path.Combine(_directory, "filename_cleaning.json"), "{ \"version\": \"1\", \"custom_noise_words\": [\"FanSub\"] }");
        var rules = new FilenameCleaningConfigurationProvider(() => _directory).Current;

        Assert.Equal("My Show", ReleaseTitleHints.Parse("My Show - FanSub", rules).Title);
    }

    // A catastrophic pattern in a person's file (nested quantifiers) runs out of time on a long name: the name is read as if it had no match,
    // the warning is heard once for the pattern, and nothing throws into the scan or the editor's pre-fill.
    private const string Hostile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!";

    private static FilenameCleaningRules RulesWith(string json, List<string> warnings)
    {
        var rules = FilenameCleaningConfigurationProvider.TryRead(json, FilenameCleaningConfigurationProvider.BuiltIn, out var problem, warnings.Add);
        Assert.True(rules is not null, problem);
        return rules!;
    }

    [Fact]
    public void ACatastrophicTrackPattern_IsReadAsNoMatch_AndWarnedOnce()
    {
        var warnings = new List<string>();
        var rules = RulesWith("{ \"version\": \"1\", \"track_patterns\": [\"^(?<track>(a+)+)$\"] }", warnings);

        var first = rules.ReadTrack(Hostile);
        var second = rules.ReadTrack(Hostile);

        Assert.Equal((null, null, Hostile), first);
        Assert.Equal(first, second);
        Assert.Single(warnings);
        Assert.Contains("took too long", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ACatastrophicNoisePattern_StripsNothing_FindsNoEdition_AndDoesNotThrow()
    {
        var warnings = new List<string>();
        var rules = RulesWith("{ \"version\": \"1\", \"noise_patterns\": [\"(a+)+$\"] }", warnings);

        var stripped = rules.StripTags(Hostile, out var edition);

        Assert.Null(edition);
        Assert.Equal(Hostile, stripped);
        Assert.False(rules.IsNoiseWord(Hostile));
        Assert.NotEmpty(warnings);
        Assert.Equal(warnings.Distinct().Count(), warnings.Count);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
