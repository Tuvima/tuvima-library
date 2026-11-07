using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Contracts.Tests;

/// <summary>
/// Keeps <c>clients/contracts/v1/player-controls.md</c> (unified player v2 A19) and the Dashboard's player the same thing. The contract marks each
/// machine-readable table with an HTML comment (<c>&lt;!-- contract-table: name --&gt;</c>) and a fixed header row; these tests read those tables
/// and compare them with the control catalog, the playback defaults, the English string table and the lock-screen metadata. A failure names the
/// table and the row that differs, so the fix is either a Dashboard change that was not meant or a contract line that was not updated.
/// </summary>
public sealed class PlayerControlsContractTests
{
    private const string ContractPath = "clients/contracts/v1/player-controls.md";

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly Lazy<IReadOnlyDictionary<string, ContractTable>> Tables = new(() => ReadTables(ReadRepoFile(ContractPath)));
    private static readonly Lazy<IReadOnlySet<string>> EnglishStrings = new(ReadEnglishStrings);

    /// <summary>The header every marked table must have, so a client reading a table can rely on its columns.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> ExpectedHeaders = new Dictionary<string, string[]>
    {
        ["transport-music"] = ["Order", "Control", "Label", "Dimmed in place"],
        ["transport-audiobook"] = ["Order", "Control", "Label", "Dimmed in place"],
        ["transport-tv"] = ["Order", "Control", "Label", "Dimmed in place"],
        ["transport-film"] = ["Order", "Control", "Label", "Dimmed in place"],
        ["tools-music"] = ["Order", "Control", "Label", "Shown when"],
        ["tools-audiobook"] = ["Order", "Control", "Label", "Shown when"],
        ["tools-tv"] = ["Order", "Control", "Label", "Shown when"],
        ["tools-film"] = ["Order", "Control", "Label", "Shown when"],
        ["tools-lesson"] = ["Order", "Control", "Label", "Shown when"],
        ["skip-defaults"] = ["Setting", "Default", "Video choices", "Audio choices"],
        ["panels"] = ["Panel", "Media", "Opened from", "Contents", "Footer"],
        ["sleep-timer-presets"] = ["Choice", "Label"],
        ["speed-presets"] = ["Preset", "Label"],
        ["end-card"] = ["Row", "Words"],
        ["lock-screen"] = ["Media", "Line 1", "Line 2", "Actions"],
        // First definition (Shy 07:01Z): no Dashboard source yet, so only the shape is checked here.
        ["tv-keys"] = ["Key", "Action"],
        ["car-list"] = ["Order", "Entry"],
    };

    private static readonly IReadOnlyDictionary<PlaybackControlKey, string> ControlIds = new Dictionary<PlaybackControlKey, string>
    {
        [PlaybackControlKey.Shuffle] = "shuffle",
        [PlaybackControlKey.PreviousItem] = "previous",
        [PlaybackControlKey.SkipBack] = "skip-back",
        [PlaybackControlKey.PlayPause] = "play-pause",
        [PlaybackControlKey.SkipForward] = "skip-forward",
        [PlaybackControlKey.NextItem] = "next",
        [PlaybackControlKey.Repeat] = "repeat",
        [PlaybackControlKey.Lyrics] = "lyrics",
        [PlaybackControlKey.Queue] = "queue",
        [PlaybackControlKey.Bookmarks] = "bookmarks",
        [PlaybackControlKey.Volume] = "volume",
        [PlaybackControlKey.NowPlaying] = "now-playing",
        [PlaybackControlKey.Close] = "close",
        [PlaybackControlKey.Chapters] = "chapters",
        [PlaybackControlKey.Speed] = "speed",
        [PlaybackControlKey.SleepTimer] = "sleep-timer",
        [PlaybackControlKey.Episodes] = "episodes",
        [PlaybackControlKey.AudioSubtitles] = "audio-subtitles",
        [PlaybackControlKey.Settings] = "playback-settings",
        [PlaybackControlKey.Fullscreen] = "fullscreen",
    };

    // ---- The contract file itself ----

    [Fact]
    public void EveryMarkedTableHasItsFixedColumnsAndNoUnknownTableIsPresent()
    {
        var problems = new List<string>();
        foreach (var (name, expected) in ExpectedHeaders)
        {
            if (!Tables.Value.TryGetValue(name, out var table))
            {
                problems.Add($"Table '{name}' is missing from {ContractPath}.");
            }
            else if (!table.Header.SequenceEqual(expected))
            {
                problems.Add($"Table '{name}' has the columns [{string.Join(" | ", table.Header)}] but the contract fixes [{string.Join(" | ", expected)}].");
            }
            else if (table.Rows.Count == 0 || table.Rows.Any(row => row.Length != expected.Length))
            {
                problems.Add($"Table '{name}' is empty or has a row with the wrong number of cells.");
            }
        }

        problems.AddRange(Tables.Value.Keys.Except(ExpectedHeaders.Keys).Select(name =>
            $"Table '{name}' in {ContractPath} is not known to PlayerControlsContractTests; add its header and a comparison."));
        AssertNone(problems);
    }

    [Fact]
    public void TheApiContractReadmeLinksThePlayerContract()
    {
        Assert.Contains("player-controls.md", ReadRepoFile("clients/contracts/v1/README.md"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("clients/roku/README.md")]
    [InlineData("clients/android/README.md")]
    [InlineData("clients/apple/README.md")]
    [InlineData("clients/tv-web/README.md")]
    public void EveryClientReadmeHasAPlayerSectionThatPointsAtTheContract(string readme)
    {
        var text = ReadRepoFile(readme);
        var heading = Regex.Match(text, @"^## Player\s*$", RegexOptions.Multiline);
        Assert.True(heading.Success, $"{readme} has no '## Player' section.");
        var section = text[heading.Index..];
        var next = Regex.Match(section[heading.Length..], @"^## ", RegexOptions.Multiline);
        if (next.Success)
        {
            section = section[..(heading.Length + next.Index)];
        }

        Assert.Contains("contracts/v1/player-controls.md", section, StringComparison.Ordinal);
    }

    // ---- Transport and tools against the control catalog ----

    [Theory]
    [InlineData("transport-music", PlaybackBarMedia.Music)]
    [InlineData("transport-audiobook", PlaybackBarMedia.Audiobook)]
    [InlineData("transport-tv", PlaybackBarMedia.Tv)]
    [InlineData("transport-film", PlaybackBarMedia.Movie)]
    public void TransportOrderNamesAndDimmedInPlaceRowsMatchTheDashboardBar(string tableName, PlaybackBarMedia media)
    {
        var built = PlaybackControlCatalog.BuildBarTransport(media, new PlaybackBarState());
        var table = Tables.Value[tableName];
        var problems = new List<string>();
        if (table.Rows.Count != built.Count)
        {
            problems.Add($"{tableName}: the contract lists {table.Rows.Count} controls but the Dashboard bar builds {built.Count}.");
        }

        for (var index = 0; index < Math.Min(table.Rows.Count, built.Count); index++)
        {
            var row = table.Rows[index];
            var control = built[index];
            var label = $"{tableName} row {row[0]} ({row[1]})";
            Compare(problems, label, "order", (index + 1).ToString(CultureInfo.InvariantCulture), row[0]);
            Compare(problems, label, "control", IdOf(control), row[1]);
            Compare(problems, label, "label", control.Label, row[2]);
            // With nothing before or after, Previous and Next are returned disabled, in place; every other control can act.
            Compare(problems, label, "dimmed in place", control.IsDisabled ? "yes" : "no", row[3]);
        }

        AssertNone(problems);
    }

    [Theory]
    [InlineData(PlaybackBarMedia.Music)]
    [InlineData(PlaybackBarMedia.Audiobook)]
    [InlineData(PlaybackBarMedia.Tv)]
    [InlineData(PlaybackBarMedia.Movie)]
    public void PreviousAndNextStayInTheRowWhenTheyCannotActAndEnableWhenTheyCan(PlaybackBarMedia media)
    {
        var idle = PlaybackControlCatalog.BuildBarTransport(media, new PlaybackBarState());
        var ready = PlaybackControlCatalog.BuildBarTransport(media, new PlaybackBarState(CanPrevious: true, CanNext: true));

        Assert.Equal(idle.Select(IdOf), ready.Select(IdOf));
        Assert.True(idle.Single(control => control.Key == PlaybackControlKey.PreviousItem).IsDisabled);
        Assert.True(idle.Single(control => control.Key == PlaybackControlKey.NextItem).IsDisabled);
        Assert.False(ready.Single(control => control.Key == PlaybackControlKey.PreviousItem).IsDisabled);
        Assert.False(ready.Single(control => control.Key == PlaybackControlKey.NextItem).IsDisabled);
    }

    [Theory]
    [InlineData("tools-music", PlaybackBarMedia.Music, false)]
    [InlineData("tools-audiobook", PlaybackBarMedia.Audiobook, false)]
    [InlineData("tools-tv", PlaybackBarMedia.Tv, true)]
    [InlineData("tools-film", PlaybackBarMedia.Movie, false)]
    [InlineData("tools-lesson", PlaybackBarMedia.Movie, true)]
    public void ToolOrderNamesAndShownWhenRowsMatchTheDashboardBar(string tableName, PlaybackBarMedia media, bool hasEpisodes)
    {
        var everything = new PlaybackBarState(HasChapters: true, HasEpisodes: hasEpisodes, CanBookmark: true, CanFullscreen: true, HasQueue: true);
        var nothing = new PlaybackBarState(HasChapters: false, HasEpisodes: false, CanBookmark: false, CanFullscreen: false, HasQueue: false);
        var built = PlaybackControlCatalog.BuildBarTools(media, everything);
        var table = Tables.Value[tableName];
        var problems = new List<string>();
        if (table.Rows.Count != built.Count)
        {
            problems.Add($"{tableName}: the contract lists {table.Rows.Count} tools but the Dashboard bar builds {built.Count} ({string.Join(", ", built.Select(IdOf))}).");
        }

        for (var index = 0; index < Math.Min(table.Rows.Count, built.Count); index++)
        {
            var row = table.Rows[index];
            var control = built[index];
            var label = $"{tableName} row {row[0]} ({row[1]})";
            Compare(problems, label, "order", (index + 1).ToString(CultureInfo.InvariantCulture), row[0]);
            Compare(problems, label, "control", IdOf(control), row[1]);
            Compare(problems, label, "label", control.Label, row[2]);

            // "Shown when": a conditional tool disappears when its condition is false; an "always" tool survives every condition being false.
            var without = row[3] switch
            {
                "always" => nothing,
                "has queue" => everything with { HasQueue = false },
                "has chapters" => everything with { HasChapters = false },
                "has episodes" => everything with { HasEpisodes = false },
                "can bookmark" => everything with { CanBookmark = false },
                "can fullscreen" => everything with { CanFullscreen = false },
                _ => null,
            };
            if (without is null)
            {
                problems.Add($"{label}: 'Shown when' says '{row[3]}', which is not one of always, has queue, has chapters, has episodes, can bookmark, can fullscreen.");
                continue;
            }

            var present = PlaybackControlCatalog.BuildBarTools(media, without).Any(tool => tool.Key == control.Key);
            var expectedPresent = row[3] == "always";
            if (present != expectedPresent)
            {
                problems.Add($"{label}: 'Shown when' says '{row[3]}' but with that condition off the Dashboard {(present ? "still shows" : "hides")} the tool.");
            }
        }

        AssertNone(problems);
    }

    // ---- Skip and restart defaults ----

    [Fact]
    public void SkipAndRestartDefaultsMatchTheConfigurationAndTheDashboardChoices()
    {
        using var config = JsonDocument.Parse(ReadRepoFile("config/ui/playback-client.json"));
        var back = config.RootElement.GetProperty("default_skip_back_seconds").GetInt32();
        var forward = config.RootElement.GetProperty("default_skip_forward_seconds").GetInt32();
        var restart = config.RootElement.GetProperty("music_previous_restart_seconds").GetInt32();
        var rows = Tables.Value["skip-defaults"].Rows.ToDictionary(row => row[0], row => row);
        var problems = new List<string>();

        // Every place that holds a default must agree with the one the contract states.
        var places = new Dictionary<string, (int Back, int Forward)>
        {
            ["config/ui/playback-client.json"] = (back, forward),
            ["the bar state"] = (new PlaybackBarState().SkipBackSeconds, new PlaybackBarState().SkipForwardSeconds),
            ["the control state"] = (new PlaybackControlState().SkipBackSeconds, new PlaybackControlState().SkipForwardSeconds),
            ["the client fallback"] = (ListenPlaybackClientSettings.FallbackSkipBackSeconds, ListenPlaybackClientSettings.FallbackSkipForwardSeconds),
            ["the watching settings"] = (new WatchingSettingsDto().SkipBackSeconds, new WatchingSettingsDto().SkipForwardSeconds),
            ["the listening settings"] = (new ListeningSettingsDto().SkipBackSeconds, new ListeningSettingsDto().SkipForwardSeconds),
        };
        foreach (var (place, value) in places)
        {
            Compare(problems, $"skip-defaults row 'Skip back' in {place}", "default", value.Back.ToString(CultureInfo.InvariantCulture), rows["Skip back"][1]);
            Compare(problems, $"skip-defaults row 'Skip forward' in {place}", "default", value.Forward.ToString(CultureInfo.InvariantCulture), rows["Skip forward"][1]);
        }

        Compare(problems, "skip-defaults row 'Previous restarts a song after'", "default", restart.ToString(CultureInfo.InvariantCulture), rows["Previous restarts a song after"][1]);
        Compare(problems, "skip-defaults row 'Skip back'", "video choices", Join(PlaybackSkipChoices.Options(true, PlaybackSkipDirection.Back)), rows["Skip back"][2]);
        Compare(problems, "skip-defaults row 'Skip back'", "audio choices", Join(PlaybackSkipChoices.Options(false, PlaybackSkipDirection.Back)), rows["Skip back"][3]);
        Compare(problems, "skip-defaults row 'Skip forward'", "video choices", Join(PlaybackSkipChoices.Options(true, PlaybackSkipDirection.Forward)), rows["Skip forward"][2]);
        Compare(problems, "skip-defaults row 'Skip forward'", "audio choices", Join(PlaybackSkipChoices.Options(false, PlaybackSkipDirection.Forward)), rows["Skip forward"][3]);
        AssertNone(problems);
    }

    [Fact]
    public void ThePreviousRestartRuleFollowsTheConfiguredThreshold()
    {
        using var config = JsonDocument.Parse(ReadRepoFile("config/ui/playback-client.json"));
        var threshold = config.RootElement.GetProperty("music_previous_restart_seconds").GetDouble();
        var rule = new MusicPreviousRule(threshold);
        var song = Guid.NewGuid();

        Assert.Equal(PlaybackPreviousAction.Restart, rule.Press(song, threshold + 0.5, hasPreviousSong: true));
        Assert.Equal(PlaybackPreviousAction.GoToPrevious, rule.Press(song, threshold + 0.5, hasPreviousSong: true));
        Assert.Equal(PlaybackPreviousAction.Restart, new MusicPreviousRule(threshold).Press(song, 0.5, hasPreviousSong: false));
        Assert.Equal(PlaybackPreviousAction.GoToPrevious, new MusicPreviousRule(threshold).Press(song, threshold - 0.5, hasPreviousSong: true));
    }

    // ---- Panels, presets, the end card ----

    [Fact]
    public void EveryPanelNameContentsAndFooterWordIsAStringTheDashboardShows()
    {
        var toolLabels = new[] { "tools-music", "tools-audiobook", "tools-tv", "tools-film", "tools-lesson" }
            .SelectMany(name => Tables.Value[name].Rows.Select(row => row[2]))
            .ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var row in Tables.Value["panels"].Rows)
        {
            var label = $"panels row '{row[0]}'";
            RequireString(problems, label, "panel name", row[0]);
            foreach (var item in SplitItems(row[3]))
            {
                RequireString(problems, label, "contents", item);
            }

            foreach (var item in SplitItems(row[4]))
            {
                RequireString(problems, label, "footer", item);
            }

            if (!toolLabels.Contains(row[2]) && row[2] != "Audio & subtitles footer")
            {
                problems.Add($"{label}: 'Opened from' says '{row[2]}', which is not the label of a tool in the tools tables.");
            }
        }

        AssertNone(problems);
    }

    [Fact]
    public void SleepTimerPresetsMatchTheDashboardPanel()
    {
        var table = Tables.Value["sleep-timer-presets"].Rows;
        var expected = PlaybackSleepTimerChoices.Presets
            .Select(minutes => (Choice: minutes.ToString(CultureInfo.InvariantCulture), Label: Format("{0} min", minutes)))
            .Append((Choice: "end-of-chapter", Label: "End of chapter"))
            .Append((Choice: "custom", Label: "Custom duration…"))
            .ToList();
        var problems = new List<string>();
        Compare(problems, "sleep-timer-presets", "row count", expected.Count.ToString(CultureInfo.InvariantCulture), table.Count.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < Math.Min(expected.Count, table.Count); index++)
        {
            Compare(problems, $"sleep-timer-presets row {index + 1}", "choice", expected[index].Choice, table[index][0]);
            Compare(problems, $"sleep-timer-presets row {index + 1}", "label", expected[index].Label, table[index][1]);
        }

        RequireString(problems, "sleep-timer-presets", "end of chapter", "End of chapter");
        RequireString(problems, "sleep-timer-presets", "custom duration", "Custom duration…");
        AssertNone(problems);
    }

    [Fact]
    public void SpeedPresetsMatchTheDashboardSpeedPanel()
    {
        var table = Tables.Value["speed-presets"].Rows;
        var expected = PlaybackSpeedChoices.Presets
            .Select(preset => (Preset: preset.ToString("0.##", CultureInfo.InvariantCulture), Label: PlaybackSpeedChoices.Format(preset)))
            .ToList();
        var problems = new List<string>();
        Compare(problems, "speed-presets", "row count", expected.Count.ToString(CultureInfo.InvariantCulture), table.Count.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < Math.Min(expected.Count, table.Count); index++)
        {
            Compare(problems, $"speed-presets row {index + 1}", "preset", expected[index].Preset, table[index][0]);
            Compare(problems, $"speed-presets row {index + 1}", "label", expected[index].Label, table[index][1]);
        }

        AssertNone(problems);
    }

    [Fact]
    public void EndCardRowsUseTheWordsOfTheDashboardEndCard()
    {
        var rows = Tables.Value["end-card"].Rows.ToDictionary(row => row[0], row => row[1]);
        var problems = new List<string>();
        foreach (var name in new[] { "Heading", "Countdown", "Kind (TV)", "Kind (lesson)", "Homework", "Buttons" })
        {
            if (!rows.ContainsKey(name))
            {
                problems.Add($"end-card is missing the row '{name}'.");
            }
        }

        foreach (var (name, words) in rows)
        {
            foreach (var item in SplitItems(words))
            {
                RequireString(problems, $"end-card row '{name}'", "words", item);
            }
        }

        AssertNone(problems);
    }

    // ---- The lock screen and system controls (D27, A17) ----

    [Fact]
    public void LockScreenRowsMatchTheMetadataTheDashboardHandsTheOperatingSystem()
    {
        var samples = new Dictionary<string, PlaybackSessionContext>
        {
            ["Music"] = Context(PlaybackBarMedia.Music, new() { MediaType = "Music", Title = "Stay a Little Longer", Subtitle = "Maya Ellis", Album = "Northern Lights" }),
            ["Audiobook"] = Context(PlaybackBarMedia.Audiobook, new() { MediaType = "Audiobook", Title = "The Way Beyond", AuthorName = "Maya Ellis" }),
            ["TV episode with a next episode"] = Context(PlaybackBarMedia.Tv, Episode(), hasNext: true),
            ["TV episode, the last one"] = Context(PlaybackBarMedia.Tv, Episode(), hasNext: false),
            ["Film"] = Context(PlaybackBarMedia.Movie, new() { MediaType = "Movie", Title = "The Glass Harbor", Year = "2026" }),
        };
        var problems = new List<string>();
        var rows = Tables.Value["lock-screen"].Rows;
        foreach (var row in rows)
        {
            if (!samples.TryGetValue(row[0], out var context))
            {
                problems.Add($"lock-screen row '{row[0]}' has no sample in PlayerControlsContractTests.");
                continue;
            }

            var metadata = PlaybackSessionMetadata.Create(context);
            if (metadata is null)
            {
                problems.Add($"lock-screen row '{row[0]}': the Dashboard sends no metadata for this sample.");
                continue;
            }

            Compare(problems, $"lock-screen row '{row[0]}'", "line 1", metadata.Title, row[1]);
            Compare(problems, $"lock-screen row '{row[0]}'", "line 2", metadata.Artist, row[2]);
            Compare(problems, $"lock-screen row '{row[0]}'", "actions", string.Join(", ", metadata.Actions), row[3]);
        }

        problems.AddRange(samples.Keys.Except(rows.Select(row => row[0])).Select(name => $"lock-screen has no row for '{name}'."));
        AssertNone(problems);
    }

    [Fact]
    public void LockScreenArtworkSizesSkipSecondsAndVaultRuleMatchTheContractText()
    {
        var text = ReadRepoFile(ContractPath);
        Assert.Equal([512, 1024], PlaybackSessionMetadata.ArtworkSizes);
        Assert.Contains("at 512 and 1024 pixels", text, StringComparison.Ordinal);

        var item = new ListenQueueItem { MediaType = "Audiobook", Title = "The Way Beyond", AuthorName = "Maya Ellis" };
        var metadata = PlaybackSessionMetadata.Create(Context(PlaybackBarMedia.Audiobook, item) with { SkipBackSeconds = 15, SkipForwardSeconds = 45 })!;
        Assert.Equal(15, metadata.SkipBackSeconds);
        Assert.Equal(45, metadata.SkipForwardSeconds);

        var vault = item with { Manifest = new PlaybackManifestDto { IsVault = true } };
        Assert.Null(PlaybackSessionMetadata.Create(Context(PlaybackBarMedia.Audiobook, vault) with { VaultLocked = true }));
    }

    [Fact]
    public void TelevisionKeysSeekByTheSameSecondsAsTheSkipButtonsAndCarsListTheirEntriesInOrder()
    {
        using var config = JsonDocument.Parse(ReadRepoFile("config/ui/playback-client.json"));
        var back = config.RootElement.GetProperty("default_skip_back_seconds").GetInt32();
        var forward = config.RootElement.GetProperty("default_skip_forward_seconds").GetInt32();
        var keys = Tables.Value["tv-keys"].Rows.ToDictionary(row => row[0], row => row[1]);
        var problems = new List<string>();
        var seek = keys.FirstOrDefault(pair => pair.Key.StartsWith("Left and Right with the timeline focused", StringComparison.Ordinal)).Value;
        if (seek is null || !seek.Contains($"({back} and {forward} by default)", StringComparison.Ordinal))
        {
            problems.Add($"tv-keys: the timeline seek row must say ({back} and {forward} by default), the configured skip defaults.");
        }

        foreach (var key in new[] { "Play/Pause", "Rewind", "Fast-forward", "Back", "Select" })
        {
            if (!keys.ContainsKey(key))
            {
                problems.Add($"tv-keys is missing the key '{key}'.");
            }
        }

        var cars = Tables.Value["car-list"].Rows.Select(row => row[1]).ToList();
        if (!cars.SequenceEqual(["Continue", "The Listen shelves", "Audiobooks", "Playlists", "Recently played"]))
        {
            problems.Add($"car-list: expected Continue, The Listen shelves, Audiobooks, Playlists, Recently played in that order but found {string.Join(", ", cars)}.");
        }

        AssertNone(problems);
    }

    // ---- Helpers ----

    private static ListenQueueItem Episode() => new()
    {
        MediaType = "TV",
        Title = "Shimmer and Shine",
        Album = "Shimmer and Shine",
        EpisodeTitle = "The First Wish",
        SeasonNumber = "1",
        EpisodeNumber = "1",
    };

    private static PlaybackSessionContext Context(PlaybackBarMedia media, ListenQueueItem item, bool hasNext = false) =>
        new(media, item, new Uri("https://library.example/"), ShowTitle: "Shimmer and Shine", ChapterTitle: "Chapter 8",
            SkipBackSeconds: 10, SkipForwardSeconds: 30, HasNextEpisode: hasNext);

    private static string IdOf(PlaybackControlDefinition control) =>
        ControlIds.TryGetValue(control.Key, out var id) ? id : $"unknown:{control.Key}";

    private static string Join(IEnumerable<int> values) => string.Join(", ", values.Select(value => value.ToString(CultureInfo.InvariantCulture)));

    private static string Format(string pattern, int value) => string.Format(CultureInfo.InvariantCulture, pattern, value);

    private static IEnumerable<string> SplitItems(string cell) =>
        cell.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void RequireString(List<string> problems, string row, string column, string value)
    {
        if (!EnglishStrings.Value.Contains(value))
        {
            problems.Add($"{row}: the {column} '{value}' is not a string in SharedStrings.resx, so the Dashboard does not show it.");
        }
    }

    private static void Compare(List<string> problems, string row, string column, string dashboard, string contract)
    {
        if (!string.Equals(dashboard, contract, StringComparison.Ordinal))
        {
            problems.Add($"{row}: {column} is '{dashboard}' in the Dashboard but '{contract}' in {ContractPath}.");
        }
    }

    private static void AssertNone(List<string> problems) =>
        Assert.True(problems.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, problems));

    private static IReadOnlyDictionary<string, ContractTable> ReadTables(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var tables = new Dictionary<string, ContractTable>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            var marker = Regex.Match(lines[index], @"^<!-- contract-table: (?<name>[a-z0-9-]+) -->\s*$");
            if (!marker.Success)
            {
                continue;
            }

            var rows = new List<string[]>();
            for (var next = index + 1; next < lines.Length && lines[next].StartsWith('|'); next++)
            {
                rows.Add(SplitRow(lines[next]));
            }

            // The first line is the header and the second the separator row (--- cells).
            if (rows.Count < 2 || !rows[1].All(cell => cell.Length > 0 && cell.All(character => character == '-')))
            {
                throw new InvalidDataException($"The table marked '{marker.Groups["name"].Value}' in {ContractPath} has no header and separator row.");
            }

            if (!tables.TryAdd(marker.Groups["name"].Value, new ContractTable(rows[0], rows.Skip(2).ToList())))
            {
                throw new InvalidDataException($"The table '{marker.Groups["name"].Value}' is marked twice in {ContractPath}.");
            }
        }

        return tables;
    }

    private static string[] SplitRow(string line) =>
        line.Trim().Trim('|').Split('|', StringSplitOptions.None).Select(cell => cell.Trim()).ToArray();

    private static IReadOnlySet<string> ReadEnglishStrings()
    {
        var document = XDocument.Load(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Resources", "SharedStrings.resx"));
        return document.Root!.Elements("data")
            .Select(data => data.Element("value")?.Value)
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ReadRepoFile(string relativePath) => File.ReadAllText(Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed record ContractTable(string[] Header, IReadOnlyList<string[]> Rows);
}
