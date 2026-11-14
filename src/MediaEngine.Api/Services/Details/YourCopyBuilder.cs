using System.Globalization;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.Details;

/// <summary>
/// Turns a title's stored file facts into the details page's "Your copy" row (details-page-bottom spec D4, D5, section 4): the chips, the panel's
/// groups and, for a show, an album or an edition with several files, the summary (the most common quality, the exceptions and the totals).
/// Pure: it reads nothing and decides nothing about who may see the result (<see cref="DetailYourCopyService"/> does that). A fact the stored
/// inspection does not hold is left out, never guessed: Dolby Vision and Atmos have no stored source today, so they never appear.
/// </summary>
internal static class YourCopyBuilder
{
    // The most exceptions one fact lists ("12 episodes in 1080p"); the rest of the files are still counted in the totals.
    private const int MaxExceptionLines = 3;
    private const int MaxLanguages = 4;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkv", "mp4", "m4v", "avi", "mov", "wmv", "ts", "m2ts", "webm", "mpg", "mpeg", "flv", "ogv",
    };

    private static readonly HashSet<string> ComicExtensions = new(StringComparer.OrdinalIgnoreCase) { "cbz", "cbr", "cb7", "cbt" };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "flac", "m4a", "aac", "ogg", "oga", "opus", "wav", "wma", "alac", "aiff", "aif", "ape", "wv", "mka",
    };

    public static DetailYourCopyViewModel? Build(DetailPageViewModel page, IReadOnlyList<YourCopyAsset> assets, bool includeLocation)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(assets);
        if (assets.Count == 0 || KindFor(page.EntityType, assets) is not { } kind)
        {
            return null;
        }

        // A show or an album is one summary over all its files; any other title has one row per edition (labelled only when there are several).
        List<(string? Label, IReadOnlyList<YourCopyAsset> Files)> parts;
        if (kind is DetailYourCopyKinds.Show or DetailYourCopyKinds.Album)
        {
            parts = [(null, assets)];
        }
        else
        {
            var editions = assets.GroupBy(asset => asset.EditionId).ToList();
            parts = editions
                .Select((group, index) => (
                    Label: editions.Count > 1 ? group.First().EditionLabel ?? $"Edition {index + 1}" : null,
                    Files: (IReadOnlyList<YourCopyAsset>)group.ToList()))
                .ToList();
        }

        return new DetailYourCopyViewModel
        {
            Blocks = parts.Select(part => BuildBlock(kind, part.Label, part.Files, page, includeLocation)).ToList(),
        };
    }

    private static string? KindFor(DetailEntityType type, IReadOnlyList<YourCopyAsset> assets) => type switch
    {
        DetailEntityType.Movie or DetailEntityType.TvEpisode => DetailYourCopyKinds.Video,
        DetailEntityType.TvShow or DetailEntityType.TvSeason => DetailYourCopyKinds.Show,
        DetailEntityType.MusicAlbum => DetailYourCopyKinds.Album,
        DetailEntityType.Book => DetailYourCopyKinds.Book,
        DetailEntityType.Audiobook => DetailYourCopyKinds.Audiobook,
        DetailEntityType.ComicIssue => DetailYourCopyKinds.Comic,
        DetailEntityType.Work => KindFromFile(assets[0]),
        _ => null,
    };

    // A plain Work page does not say what it is, so its first file does.
    private static string KindFromFile(YourCopyAsset asset)
    {
        var extension = Extension(asset);
        var probedVideo = asset.Probe is { } probe && (!string.IsNullOrWhiteSpace(probe.VideoCodec) || probe.Width is > 0);
        if (probedVideo || VideoExtensions.Contains(extension))
        {
            return DetailYourCopyKinds.Video;
        }

        if (string.Equals(extension, "m4b", StringComparison.OrdinalIgnoreCase))
        {
            return DetailYourCopyKinds.Audiobook;
        }

        if (AudioExtensions.Contains(extension))
        {
            return DetailYourCopyKinds.Audio;
        }

        return ComicExtensions.Contains(extension) ? DetailYourCopyKinds.Comic : DetailYourCopyKinds.Book;
    }

    private static DetailYourCopyBlockViewModel BuildBlock(
        string kind,
        string? label,
        IReadOnlyList<YourCopyAsset> files,
        DetailPageViewModel page,
        bool includeLocation)
    {
        var timed = kind is not (DetailYourCopyKinds.Book or DetailYourCopyKinds.Comic);
        var scanned = files.Where(file => file.Probe is not null).ToList();
        if (timed && scanned.Count == 0)
        {
            // The scan has not finished: no chips, and the Dashboard offers no File details until it has.
            return new DetailYourCopyBlockViewModel { Label = label, Kind = kind, IsReadingFacts = true };
        }

        var (chips, groups) = kind switch
        {
            DetailYourCopyKinds.Video or DetailYourCopyKinds.Show => VideoFacts(kind, files, scanned, string.Equals(page.Facts?.MediaKind, VideoFolderWording.KindLabel, StringComparison.Ordinal)),
            DetailYourCopyKinds.Audio or DetailYourCopyKinds.Album => AudioFacts(kind, files, scanned),
            _ => BookFacts(kind, files, page),
        };

        var single = files.Count == 1 && kind is not (DetailYourCopyKinds.Show or DetailYourCopyKinds.Album);
        return new DetailYourCopyBlockViewModel
        {
            Label = label,
            Kind = kind,
            IsReadingFacts = chips.Count == 0 && groups.Count == 0,
            Chips = chips,
            Groups = groups,
            FileName = single ? Path.GetFileName(files[0].FilePath) is { Length: > 0 } name ? name : null : null,
            FileLocation = single && includeLocation && !string.IsNullOrWhiteSpace(files[0].FilePath) ? files[0].FilePath : null,
        };
    }

    // ---- Video: a movie, an episode, a show, or an edition with several files --------------------------------------------------

    private static (IReadOnlyList<string> Chips, IReadOnlyList<DetailYourCopyGroupViewModel> Groups) VideoFacts(
        string kind,
        IReadOnlyList<YourCopyAsset> files,
        IReadOnlyList<YourCopyAsset> scanned,
        bool videoFolder)
    {
        var many = kind == DetailYourCopyKinds.Show || files.Count > 1;
        // A folder of videos (a course, a home-movie folder) holds videos, not episodes (spec D13).
        var noun = kind == DetailYourCopyKinds.Show ? (videoFolder ? "video" : "episode") : "file";

        var resolution = Mode(scanned.Select(file => ResolutionLabel(file.Probe)), ResolutionRank);
        var codec = Mode(scanned.Select(file => VideoCodecLabel(file.Probe?.VideoCodec)));
        var hdr = Mode(scanned.Select(file => HdrLabel(file.Probe)));
        var audio = Mode(scanned.Select(file => AudioChip(file.Probe)));
        var container = Mode(files.Select(Extension));

        var chips = new List<string>();
        if (many)
        {
            chips.Add(Count(files.Count, noun));
        }

        AddIfKnown(chips, resolution);
        AddIfKnown(chips, hdr is "HDR10" or "HLG" ? hdr : null);
        AddIfKnown(chips, codec);
        AddIfKnown(chips, audio);
        AddIfKnown(chips, container);

        var groups = new List<DetailYourCopyGroupViewModel>();

        // Video
        var video = new List<string>();
        var dimensions = Mode(scanned.Select(file => Dimensions(file.Probe)));
        var resolutionExceptions = Exceptions(scanned.Select(file => ResolutionLabel(file.Probe)), resolution, noun, "in {0}");
        var codecExceptions = Exceptions(scanned.Select(file => VideoCodecLabel(file.Probe?.VideoCodec)), codec, noun, "in {0}");
        var hdrExceptions = Exceptions(scanned.Select(file => HdrLabel(file.Probe)), hdr, noun, "in {0}");
        if (!many)
        {
            var probe = scanned[0].Probe!;
            AddIfKnown(video, Join(dimensions, FrameRateLabel(probe.FrameRate)));
            AddIfKnown(video, Join(codec, BitrateLabel(OverallBitrate(scanned[0]))));
            AddIfKnown(video, hdr);
        }
        else
        {
            var mostly = resolutionExceptions.Count + codecExceptions.Count > 0 ? "Mostly " : string.Empty;
            AddIfKnown(video, Join(dimensions, codec) is { } main ? mostly + main : null);
            var average = Average(scanned.Select(OverallBitrate));
            AddIfKnown(video, Join(hdr, average is { } bits ? $"{BitrateLabel(bits)} average" : null));
            video.AddRange(resolutionExceptions);
            video.AddRange(codecExceptions);
            video.AddRange(hdrExceptions);
        }

        AddGroup(groups, DetailYourCopyGroupKeys.Video, video);

        // Audio and subtitles
        var sound = new List<string>();
        var audioExceptions = Exceptions(scanned.Select(file => AudioChip(file.Probe)), audio, noun, "with {0}");
        if (!many)
        {
            var probe = scanned[0].Probe!;
            var streams = probe.AudioStreams;
            if (streams.Count > 0)
            {
                for (var index = 0; index < streams.Count; index++)
                {
                    var stream = streams[index];
                    // Only the first stream's channels and sample rate are stored, so only it carries them.
                    var codecLabel = index == 0 && probe.AudioCodec is { Length: > 0 } && string.Equals(NormalizeCodec(stream.Codec), NormalizeCodec(probe.AudioCodec), StringComparison.Ordinal)
                        ? AudioChip(probe)
                        : AudioCodecLabel(stream.Codec);
                    AddIfKnown(sound, Join(LanguageName(stream.Language), codecLabel));
                }
            }
            else
            {
                AddIfKnown(sound, Join(LanguageName(probe.AudioLanguage), AudioChip(probe)));
            }

            AddIfKnown(sound, Join(SampleRateLabel(probe.SampleRate), probe.AudioBitrate is > 0 ? BitrateLabel(probe.AudioBitrate.Value * 1000L) : null));
        }
        else
        {
            var language = Mode(scanned.Select(file => LanguageName(FirstAudioLanguage(file.Probe))));
            AddIfKnown(sound, Join(language, audio));
            if (audioExceptions.Count > 0)
            {
                sound.Add($"Most {noun}s");
                sound.AddRange(audioExceptions);
            }
        }

        var subtitles = scanned
            .SelectMany(file => file.Probe!.SubtitleLanguages)
            .Select(LanguageName)
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (subtitles.Count > 0)
        {
            sound.Add("Subtitles · " + LanguageList(subtitles));
        }

        AddGroup(groups, DetailYourCopyGroupKeys.AudioSubtitles, sound);

        // File or Files
        var file = new List<string>();
        var length = Sum(files.Select(Duration));
        var added = files.Select(item => item.AddedAt).Where(date => date is not null).DefaultIfEmpty(null).Max();
        if (!many)
        {
            AddIfKnown(file, Join(container, SizeLabel(files[0].FileSizeBytes)));
            if (length is { } span)
            {
                file.Add("Duration · " + DurationLabel(span));
            }

            if (added is { } date)
            {
                file.Add("Added · " + DateLabel(date));
            }

            AddGroup(groups, DetailYourCopyGroupKeys.File, file);
        }
        else
        {
            file.Add(Join(Count(files.Count, "file"), container, SizeLabel(Sum(files.Select(item => item.FileSizeBytes)))) ?? Count(files.Count, "file"));
            if (length is { } span)
            {
                file.Add("Total · " + DurationLabel(span));
            }

            if (added is { } date)
            {
                file.Add("Added · " + DateLabel(date));
            }

            file.AddRange(Exceptions(files.Select(Extension), container, noun, "in {0}"));
            if (scanned.Count < files.Count)
            {
                file.Add($"{Count(files.Count - scanned.Count, noun)} still being read");
            }

            AddGroup(groups, DetailYourCopyGroupKeys.Files, file);
        }

        return (chips, groups);
    }

    // ---- Audio: a single track or an album ---------------------------------------------------------------------------------------

    private static (IReadOnlyList<string> Chips, IReadOnlyList<DetailYourCopyGroupViewModel> Groups) AudioFacts(
        string kind,
        IReadOnlyList<YourCopyAsset> files,
        IReadOnlyList<YourCopyAsset> scanned)
    {
        var album = kind == DetailYourCopyKinds.Album;
        var many = album || files.Count > 1;
        var noun = album ? "track" : "file";

        var codec = Mode(scanned.Select(file => AudioCodecLabel(file.Probe?.AudioCodec)));
        var quality = Mode(scanned.Select(file => DepthAndRate(file.Probe)));
        var channels = Mode(scanned.Select(file => ChannelWords(file.Probe?.Channels)));

        var chips = new List<string>();
        AddIfKnown(chips, codec);
        AddIfKnown(chips, quality);
        if (many)
        {
            chips.Add(Count(files.Count, noun));
        }

        AddIfKnown(chips, channels);

        var groups = new List<DetailYourCopyGroupViewModel>();
        var sound = new List<string>();
        var codecExceptions = Exceptions(scanned.Select(file => AudioCodecLabel(file.Probe?.AudioCodec)), codec, noun, "in {0}");
        var qualityExceptions = Exceptions(scanned.Select(file => DepthAndRate(file.Probe)), quality, noun, "at {0}");
        var depth = Mode(scanned.Select(file => file.Probe?.BitDepth is > 0 ? $"{file.Probe.BitDepth}-bit" : null));
        var rate = Mode(scanned.Select(file => SampleRateLabel(file.Probe?.SampleRate)));
        AddIfKnown(sound, Join(codec, depth, rate));
        AddIfKnown(sound, channels);
        var average = Average(scanned.Select(file => file.Probe!.AudioBitrate is > 0 ? file.Probe.AudioBitrate.Value * 1000L : OverallBitrate(file)));
        if (average is { } bits)
        {
            sound.Add(many ? $"{BitrateLabel(bits)} average" : BitrateLabel(bits)!);
        }

        sound.AddRange(codecExceptions);
        sound.AddRange(qualityExceptions);
        AddGroup(groups, DetailYourCopyGroupKeys.Audio, sound);

        var length = Sum(files.Select(Duration));
        var added = files.Select(item => item.AddedAt).Where(date => date is not null).DefaultIfEmpty(null).Max();
        var container = Mode(files.Select(Extension));
        if (album)
        {
            var tracks = new List<string>();
            tracks.Add(length is { } span ? $"{Count(files.Count, noun)} · {DurationLabel(span)}" : Count(files.Count, noun));
            var covers = scanned.Count(file => file.Probe!.HasEmbeddedCover);
            if (covers > 0)
            {
                tracks.Add(covers == files.Count ? "Embedded cover art" : $"Cover art embedded in {covers} of {files.Count} tracks");
            }

            AddGroup(groups, DetailYourCopyGroupKeys.Tracks, tracks);

            var disk = new List<string>
            {
                Join(Count(files.Count, container is null ? "file" : $"{container} file"), SizeLabel(Sum(files.Select(item => item.FileSizeBytes))))!,
            };
            if (SharedFolder(files) is { } folder)
            {
                disk.Add("Folder · " + folder);
            }

            if (added is { } date)
            {
                disk.Add("Added · " + DateLabel(date));
            }

            disk.AddRange(Exceptions(files.Select(Extension), container, noun, "in {0}"));
            if (scanned.Count < files.Count)
            {
                disk.Add($"{Count(files.Count - scanned.Count, noun)} still being read");
            }

            AddGroup(groups, DetailYourCopyGroupKeys.Files, disk);
        }
        else
        {
            var disk = new List<string>();
            AddIfKnown(disk, Join(many ? Count(files.Count, "file") : null, container, SizeLabel(many ? Sum(files.Select(item => item.FileSizeBytes)) : files[0].FileSizeBytes)));
            if (length is { } span)
            {
                disk.Add((many ? "Total · " : "Duration · ") + DurationLabel(span));
            }

            if (added is { } date)
            {
                disk.Add("Added · " + DateLabel(date));
            }

            AddGroup(groups, DetailYourCopyGroupKeys.File, disk);
        }

        return (chips, groups);
    }

    // ---- Books, audiobooks and comics --------------------------------------------------------------------------------------------

    private static (IReadOnlyList<string> Chips, IReadOnlyList<DetailYourCopyGroupViewModel> Groups) BookFacts(
        string kind,
        IReadOnlyList<YourCopyAsset> files,
        DetailPageViewModel page)
    {
        var audiobook = kind == DetailYourCopyKinds.Audiobook;
        var formats = files.Select(Extension).Where(extension => extension.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var pages = !audiobook && int.TryParse(page.Facts?.PageCount, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0 ? count : (int?)null;
        var length = Sum(files.Select(Duration));
        var language = string.IsNullOrWhiteSpace(page.Facts?.Language) ? null : page.Facts!.Language!.Trim();

        var chips = new List<string>(formats.Take(2));
        if (pages is { } pageCount)
        {
            chips.Add(Count(pageCount, "page"));
        }
        else if (audiobook && length is { } span)
        {
            chips.Add(DurationLabel(span));
        }

        AddIfKnown(chips, language);

        var groups = new List<DetailYourCopyGroupViewModel>();
        var book = new List<string>();
        if (formats.Count > 0)
        {
            book.Add(string.Join(", ", formats));
        }

        if (pages is { } totalPages)
        {
            book.Add(Count(totalPages, "page"));
        }
        else if (audiobook && length is { } total)
        {
            book.Add("Length · " + DurationLabel(total));
        }

        if (language is not null)
        {
            book.Add("Language · " + language);
        }

        AddGroup(groups, DetailYourCopyGroupKeys.Book, book);

        // Reading: the profile's own progress, shown beside the edition it is for (a reading progress beside an ebook, a listening one beside an audiobook).
        var reading = new List<string>();
        var progress = page.Progress;
        var forThisKind = progress is null
            || progress.Kind == DetailProgressKind.Unknown
            || (audiobook ? progress.Kind == DetailProgressKind.Listening : progress.Kind == DetailProgressKind.Reading);
        if (forThisKind && progress is not null && progress.Percent > 0)
        {
            reading.Add("Progress · " + (string.IsNullOrWhiteSpace(progress.PercentLabel) ? $"{Math.Round(progress.Percent, MidpointRounding.AwayFromZero):0}%" : progress.PercentLabel.Trim()));
            AddIfKnown(reading, progress.RemainingLabel);
        }
        else
        {
            reading.Add("Not started");
        }

        AddGroup(groups, DetailYourCopyGroupKeys.Reading, reading);

        var disk = new List<string>();
        var added = files.Select(item => item.AddedAt).Where(date => date is not null).DefaultIfEmpty(null).Max();
        var size = SizeLabel(Sum(files.Select(item => item.FileSizeBytes)));
        AddIfKnown(disk, files.Count > 1
            ? Join(Count(files.Count, "file"), size)
            : Join(formats.FirstOrDefault(), size));
        if (audiobook && length is { } duration)
        {
            disk.Add("Duration · " + DurationLabel(duration));
        }

        if (added is { } date)
        {
            disk.Add("Added · " + DateLabel(date));
        }

        AddGroup(groups, DetailYourCopyGroupKeys.File, disk);
        return (chips, groups);
    }

    // ---- Facts of one file -------------------------------------------------------------------------------------------------------

    private static string Extension(YourCopyAsset asset) => Path.GetExtension(asset.FilePath).TrimStart('.').ToUpperInvariant();

    private static string? ResolutionLabel(MediaProbeResult? probe)
    {
        if (probe is null || (probe.Height is not > 0 && probe.Width is not > 0))
        {
            return null;
        }

        var height = probe.Height ?? 0;
        var width = probe.Width ?? 0;
        return height >= 2000 || width >= 3600 ? "4K"
            : height >= 1000 || width >= 1800 ? "1080p"
            : height >= 700 || width >= 1200 ? "720p"
            : "SD";
    }

    private static int ResolutionRank(string label) => label switch { "4K" => 4, "1080p" => 3, "720p" => 2, "SD" => 1, _ => 0 };

    private static string? Dimensions(MediaProbeResult? probe) =>
        probe is { Width: > 0, Height: > 0 } ? $"{probe.Width} × {probe.Height}" : null;

    private static string? FrameRateLabel(double? frameRate) =>
        frameRate is > 0 ? frameRate.Value.ToString("0.###", CultureInfo.InvariantCulture) + " fps" : null;

    private static string? VideoCodecLabel(string? codec) => NormalizeCodec(codec) switch
    {
        "" => null,
        "h264" or "avc" or "mpeg4avc" => "H.264",
        "hevc" or "h265" => "HEVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        "vp8" => "VP8",
        "mpeg4" => "MPEG-4",
        "mpeg2video" => "MPEG-2",
        "vc1" => "VC-1",
        var other => other.ToUpperInvariant(),
    };

    // SDR, HDR10 or HLG from the stored transfer characteristic; null when the file does not say.
    private static string? HdrLabel(MediaProbeResult? probe) => probe?.ColorTransfer?.Trim().ToLowerInvariant() switch
    {
        "smpte2084" => "HDR10",
        "arib-std-b67" => "HLG",
        "bt709" or "smpte170m" or "bt470bg" or "bt470m" or "smpte240m" or "bt2020-10" or "bt2020-12" or "iec61966-2-1" or "iec61966-2-4" or "srgb" => "SDR",
        _ => null,
    };

    private static string? AudioCodecLabel(string? codec) => NormalizeCodec(codec) switch
    {
        "" => null,
        "aac" or "aaclc" => "AAC",
        "ac3" => "Dolby Digital",
        "eac3" => "Dolby Digital Plus",
        "truehd" => "Dolby TrueHD",
        "dts" => "DTS",
        "flac" => "FLAC",
        "alac" => "ALAC",
        "mp3" or "mpeg1audiolayer3" => "MP3",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        var pcm when pcm.StartsWith("pcm", StringComparison.Ordinal) => "PCM",
        var other => other.ToUpperInvariant(),
    };

    // "AAC 5.1": the codec and its channel layout read as one word group, not as two facts.
    private static string? AudioChip(MediaProbeResult? probe) => Spaced(AudioCodecLabel(probe?.AudioCodec), ChannelLayout(probe?.Channels));

    private static string? FirstAudioLanguage(MediaProbeResult? probe) =>
        probe?.AudioStreams.FirstOrDefault()?.Language ?? probe?.AudioLanguage;

    private static string? ChannelLayout(int? channels) => channels switch
    {
        null or <= 0 => null,
        1 => "1.0",
        2 => "2.0",
        3 => "2.1",
        4 => "4.0",
        5 => "4.1",
        6 => "5.1",
        7 => "6.1",
        8 => "7.1",
        var other => $"{other} channels",
    };

    private static string? ChannelWords(int? channels) => channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        _ => ChannelLayout(channels),
    };

    private static string? SampleRateLabel(int? hertz) =>
        hertz is > 0 ? (hertz.Value / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + " kHz" : null;

    private static string? DepthAndRate(MediaProbeResult? probe)
    {
        if (probe is null)
        {
            return null;
        }

        var depth = probe.BitDepth is > 0 ? $"{probe.BitDepth}-bit" : null;
        var rate = SampleRateLabel(probe.SampleRate);
        return depth is not null && rate is not null ? $"{depth} / {rate}" : depth ?? rate;
    }

    private static TimeSpan? Duration(YourCopyAsset asset)
    {
        if (asset.DurationSeconds is > 0)
        {
            return TimeSpan.FromSeconds(asset.DurationSeconds.Value);
        }

        return asset.Probe?.Duration is { } probed && probed > TimeSpan.Zero ? probed : null;
    }

    // The file's overall bitrate: a derived rendition's own, otherwise the size over the length.
    private static long? OverallBitrate(YourCopyAsset asset)
    {
        if (asset.RenditionBitrateBps is > 0)
        {
            return asset.RenditionBitrateBps;
        }

        return asset.FileSizeBytes is > 0 && Duration(asset) is { } length && length.TotalSeconds > 0
            ? (long)Math.Round(asset.FileSizeBytes.Value * 8d / length.TotalSeconds)
            : null;
    }

    private static string? SharedFolder(IReadOnlyList<YourCopyAsset> files)
    {
        var folders = files
            .Select(file => Path.GetDirectoryName(file.FilePath.Replace('\\', '/'))?.Replace('\\', '/'))
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folders.Count != 1)
        {
            return null;
        }

        // The last two folders name the album well enough ("Aurora / Night Tape") without showing the whole path.
        var parts = folders[0]!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : string.Join(" / ", parts.TakeLast(2));
    }

    // ---- Summaries and wording ---------------------------------------------------------------------------------------------------

    // The most common known value. A tie goes to the higher-ranked value, then the first in alphabetical order, so the answer never depends on file order.
    private static string? Mode(IEnumerable<string?> values, Func<string, int>? rank = null) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => rank?.Invoke(group.Key) ?? 0)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .FirstOrDefault();

    // "12 episodes in 1080p": the files whose value is not the common one, grouped by that value, biggest group first.
    private static List<string> Exceptions(IEnumerable<string?> values, string? common, string noun, string phrase) =>
        common is null
            ? []
            : values.Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, common, StringComparison.Ordinal))
                .GroupBy(value => value!, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Take(MaxExceptionLines)
                .Select(group => $"{Count(group.Count(), noun)} {string.Format(CultureInfo.InvariantCulture, phrase, group.Key)}")
                .ToList();

    private static string Count(int count, string noun) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}";

    private static long? Average(IEnumerable<long?> values)
    {
        var known = values.Where(value => value is > 0).Select(value => value!.Value).ToList();
        return known.Count == 0 ? null : (long)Math.Round(known.Average());
    }

    private static TimeSpan? Sum(IEnumerable<TimeSpan?> values)
    {
        var known = values.Where(value => value is not null).Select(value => value!.Value).ToList();
        return known.Count == 0 ? null : TimeSpan.FromTicks(known.Sum(value => value.Ticks));
    }

    private static long? Sum(IEnumerable<long?> values)
    {
        var known = values.Where(value => value is > 0).Select(value => value!.Value).ToList();
        return known.Count == 0 ? null : known.Sum();
    }

    private static string? SizeLabel(long? bytes)
    {
        if (bytes is not > 0)
        {
            return null;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes.Value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size.ToString(size >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string? BitrateLabel(long? bitsPerSecond)
    {
        if (bitsPerSecond is not > 0)
        {
            return null;
        }

        return bitsPerSecond >= 1_000_000
            ? $"{(bitsPerSecond.Value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture)} Mb/s"
            : $"{Math.Max(1, (long)Math.Round(bitsPerSecond.Value / 1000d)).ToString(CultureInfo.InvariantCulture)} kb/s";
    }

    private static string DurationLabel(TimeSpan length)
    {
        var minutes = (int)Math.Round(length.TotalMinutes, MidpointRounding.AwayFromZero);
        return minutes >= 60
            ? $"{minutes / 60}h {minutes % 60}m".Replace(" 0m", string.Empty, StringComparison.Ordinal)
            : minutes >= 1 ? $"{minutes} min" : $"{Math.Max(1, (int)length.TotalSeconds)} sec";
    }

    private static string DateLabel(DateTimeOffset? date) =>
        date!.Value.UtcDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    private static string LanguageList(IReadOnlyList<string> names) =>
        names.Count <= MaxLanguages
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(MaxLanguages))} and {names.Count - MaxLanguages} more";

    // ffprobe writes three-letter codes (some bibliographic: fre, ger, chi); "und" and "zxx" name nothing.
    private static string? LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();
        var key = trimmed.Split('-', '_')[0];
        if (key.Equals("und", StringComparison.OrdinalIgnoreCase) || key.Equals("zxx", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Bibliographic.TryGetValue(key, out var named) || Common.TryGetValue(key, out named))
        {
            return named;
        }

        return CultureNames.Value.TryGetValue(key, out var name) ? name : trimmed;
    }

    // The languages most files carry, named without the system's culture data (which a trimmed or invariant host may not have).
    private static readonly Dictionary<string, string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English",
        ["eng"] = "English",
        ["es"] = "Spanish",
        ["spa"] = "Spanish",
        ["fr"] = "French",
        ["fra"] = "French",
        ["de"] = "German",
        ["deu"] = "German",
        ["it"] = "Italian",
        ["ita"] = "Italian",
        ["pt"] = "Portuguese",
        ["por"] = "Portuguese",
        ["ja"] = "Japanese",
        ["jpn"] = "Japanese",
        ["ko"] = "Korean",
        ["kor"] = "Korean",
        ["zh"] = "Chinese",
        ["zho"] = "Chinese",
        ["ru"] = "Russian",
        ["rus"] = "Russian",
        ["nl"] = "Dutch",
        ["nld"] = "Dutch",
        ["sv"] = "Swedish",
        ["swe"] = "Swedish",
        ["pl"] = "Polish",
        ["pol"] = "Polish",
        ["tr"] = "Turkish",
        ["tur"] = "Turkish",
        ["ar"] = "Arabic",
        ["ara"] = "Arabic",
        ["hi"] = "Hindi",
        ["hin"] = "Hindi",
    };

    private static readonly Dictionary<string, string> Bibliographic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alb"] = "Albanian",
        ["arm"] = "Armenian",
        ["baq"] = "Basque",
        ["bur"] = "Burmese",
        ["chi"] = "Chinese",
        ["cze"] = "Czech",
        ["dut"] = "Dutch",
        ["fre"] = "French",
        ["geo"] = "Georgian",
        ["ger"] = "German",
        ["gre"] = "Greek",
        ["ice"] = "Icelandic",
        ["mac"] = "Macedonian",
        ["mao"] = "Maori",
        ["may"] = "Malay",
        ["per"] = "Persian",
        ["rum"] = "Romanian",
        ["slo"] = "Slovak",
        ["tib"] = "Tibetan",
        ["wel"] = "Welsh",
    };

    private static readonly Lazy<Dictionary<string, string>> CultureNames = new(() =>
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (culture.TwoLetterISOLanguageName.Length == 2)
            {
                names.TryAdd(culture.TwoLetterISOLanguageName, culture.EnglishName);
            }

            if (culture.ThreeLetterISOLanguageName.Length == 3)
            {
                names.TryAdd(culture.ThreeLetterISOLanguageName, culture.EnglishName);
            }
        }

        return names;
    });

    private static string NormalizeCodec(string? codec) =>
        string.IsNullOrWhiteSpace(codec) ? string.Empty : codec.Trim().ToLowerInvariant().Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);

    private static string? Spaced(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) ? null : string.IsNullOrWhiteSpace(second) ? first : $"{first} {second}";

    private static string? Join(params string?[] parts)
    {
        var known = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
        return known.Count == 0 ? null : string.Join(" · ", known);
    }

    private static void AddIfKnown(List<string> lines, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add(value);
        }
    }

    private static void AddGroup(List<DetailYourCopyGroupViewModel> groups, string key, List<string> lines)
    {
        if (lines.Count > 0)
        {
            groups.Add(new DetailYourCopyGroupViewModel { Key = key, Lines = lines });
        }
    }
}
