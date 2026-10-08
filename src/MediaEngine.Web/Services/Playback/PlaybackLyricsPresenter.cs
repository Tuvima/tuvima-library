using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

public sealed record PlaybackLyricWord(string Text, double StartSeconds, double? EndSeconds, bool ExplicitEnd = false);
public sealed record PlaybackLyricLine(string Text, double? StartSeconds, IReadOnlyList<PlaybackLyricWord>? Words = null,
    bool IsInstrumental = false, double? EndSeconds = null);

public static partial class PlaybackLyricsParser
{
    [GeneratedRegex(@"\[(\d+):([0-5]\d)(?:\.(\d{1,3}))?\]", RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();
    [GeneratedRegex(@"<(\d+):([0-5]\d)(?:\.(\d{1,3}))?>", RegexOptions.CultureInvariant)]
    private static partial Regex WordTimestamp();
    [GeneratedRegex(@"<[^>]*(?:>|$)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineMarkup();
    [GeneratedRegex(@"^\[(?:ar|al|ti|by|re|ve|offset):.*\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Metadata();
    [GeneratedRegex(@"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Offset();

    private static double Time(Match match) => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60
        + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
        + (match.Groups[3].Success ? double.Parse("0." + match.Groups[3].Value, CultureInfo.InvariantCulture) : 0);

    public static IReadOnlyList<PlaybackLyricLine> Parse(string? text)
    {
        var offsetMatch = Offset().Matches(text ?? string.Empty).LastOrDefault();
        var offset = offsetMatch is not null && double.TryParse(offsetMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var milliseconds)
            ? milliseconds / 1000 : 0;
        var lines = new List<PlaybackLyricLine>();
        foreach (var raw in (text ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
        {
            var value = raw.Trim();
            if (value.Length == 0 || Metadata().IsMatch(value))
            {
                continue;
            }
            var matches = Timestamp().Matches(value);
            if (matches.Count == 0 || matches[0].Index != 0) { lines.Add(new(InlineMarkup().Replace(value, ""), null)); continue; }
            var end = 0; var stamps = new List<double>();
            foreach (Match match in matches)
            {
                if (match.Index != end)
                {
                    break;
                }
                end += match.Length; stamps.Add(Time(match) - offset);
            }
            var content = value[end..].Trim();
            var wordTags = WordTimestamp().Matches(content).Cast<Match>().ToArray();
            var stripped = InlineMarkup().Replace(content, "").Trim();
            IReadOnlyList<PlaybackLyricWord>? words = null;
            // Invalid, repeated or reversed inline times degrade to ordinary timed text.
            if (wordTags.Length > 0 && string.IsNullOrWhiteSpace(content[..wordTags[0].Index]) && InlineMarkup().Matches(content).Count == wordTags.Length
                && wordTags.Select(Time).Zip(wordTags.Skip(1).Select(Time)).All(pair => pair.First < pair.Second))
            {
                var parsed = new List<PlaybackLyricWord>();
                for (var i = 0; i < wordTags.Length; i++)
                {
                    var tag = wordTags[i]; var next = i + 1 < wordTags.Length ? wordTags[i + 1] : null;
                    var segment = content[(tag.Index + tag.Length)..(next?.Index ?? content.Length)];
                    if (segment.Length == 0)
                    {
                        continue;
                    }
                    parsed.Add(new(segment, Time(tag) - offset, next is null ? null : Time(next) - offset,
                        next is not null && string.IsNullOrWhiteSpace(content[(next.Index + next.Length)..])));
                }
                if (parsed.Count > 0)
                {
                    words = parsed;
                }
            }
            foreach (var stamp in stamps)
            {
                var shift = stamp - stamps[0];
                var shifted = words?.Select(word => word with { StartSeconds = word.StartSeconds + shift, EndSeconds = word.EndSeconds + shift }).ToArray();
                lines.Add(new(stripped, stamp, shifted, stripped.Length == 0));
            }
        }
        var ordered = lines.All(line => line.StartSeconds.HasValue) ? lines.OrderBy(line => line.StartSeconds).ToList() : lines;
        for (var i = 0; i < ordered.Count; i++)
        {
            var line = ordered[i];
            if (line.Words is not { Count: > 0 } words || words[^1].EndSeconds is not null)
            {
                continue;
            }
            var next = ordered.Skip(i + 1).FirstOrDefault(other => other.StartSeconds > words[^1].StartSeconds)?.StartSeconds;
            var updated = words.ToArray(); updated[^1] = updated[^1] with { EndSeconds = next };
            ordered[i] = line with { Words = updated };
        }
        return ordered;
    }
}

public static class PlaybackLyricsTimeline
{
    public static IReadOnlyList<PlaybackLyricLine> Build(IReadOnlyList<PlaybackLyricLine> lines, double duration, ListenPlaybackClientSettings settings)
    {
        var sung = lines.Where(line => !line.IsInstrumental && line.StartSeconds is not null).OrderBy(line => line.StartSeconds).ToArray();
        if (sung.Length == 0)
        {
            return lines.Where(line => !line.IsInstrumental).ToArray();
        }
        var gaps = new List<(double Start, double End)>();
        var first = sung[0].StartSeconds!.Value;
        if (first >= settings.LyricsIntroMinSeconds)
        {
            gaps.Add((0, first));
        }
        foreach (var marker in lines.Where(line => line.IsInstrumental && line.StartSeconds is not null))
        {
            var start = marker.StartSeconds!.Value;
            var next = sung.FirstOrDefault(line => line.StartSeconds > start)?.StartSeconds ?? (duration > start ? duration : (double?)null);
            if (next is double end && end - start >= settings.LyricsInstrumentalMinSeconds)
            {
                gaps.Add((start, end));
            }
        }
        for (var i = 0; i < sung.Length - 1; i++)
        {
            if (sung[i].Words?.LastOrDefault() is { ExplicitEnd: true, EndSeconds: double end }
                    && sung[i + 1].StartSeconds is double next && next - end >= settings.LyricsWordGapMinSeconds)
            {
                gaps.Add((end, next));
            }
        }
        var merged = new List<(double Start, double End)>();
        foreach (var gap in gaps.OrderBy(gap => gap.Start))
        {
            if (merged.Count > 0 && gap.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, gap.End));
            }
            else
            {
                merged.Add(gap);
            }
        }
        return lines.Where(line => !line.IsInstrumental).Concat(merged.Select(gap => new PlaybackLyricLine("", gap.Start, IsInstrumental: true, EndSeconds: gap.End)))
            .OrderBy(line => line.StartSeconds ?? double.MaxValue).ToArray();
    }
}

/// <summary>Authorized text presentation shared by every audio host, with no playback ownership.</summary>
public sealed class PlaybackLyricsPresenter(IEngineApiClient api) : IDisposable
{
    private PlaybackLyricsIdentity? _identity;
    private Guid? _observedProjection, _projectedChoice;
    private ListenPlaybackSnapshot _snapshot = new();
    private IPlaybackCommandSink? _commands;
    private CancellationTokenSource _reads = new();
    private long _generation, _selectionGeneration;
    private bool _tracksLoaded, _contentLoaded, _disposed;
    public IReadOnlyList<TextTrackDto> Tracks { get; private set; } = [];
    public IReadOnlyList<PlaybackLyricLine> Lines { get; private set; } = [];
    public Guid? SelectedTrackId { get; private set; }
    public bool Loading { get; private set; }
    public bool Refreshing { get; private set; }
    public string? Status { get; private set; }
    public event Action? Changed;

    public void Observe(ListenPlaybackSnapshot snapshot, IPlaybackCommandSink commands)
    {
        _snapshot = snapshot; _commands = commands;
        var identity = PlaybackLyricsIdentity.From(snapshot);
        var projected = snapshot.LyricsSelection is { } projection && projection.Identity == identity ? projection.TrackId : (Guid?)null;
        if (_identity == identity)
        {
            if (_observedProjection != projected) { _observedProjection = projected; _projectedChoice = projected; }
            return;
        }
        _observedProjection = projected; _projectedChoice = projected;
        _identity = identity; _generation++; _selectionGeneration++;
        _reads.Cancel(); _reads.Dispose(); _reads = new();
        Tracks = []; Lines = []; SelectedTrackId = null; Loading = false; Refreshing = false;
        Status = null; _tracksLoaded = false; _contentLoaded = false;
    }

    private bool Current(PlaybackLyricsIdentity identity, long generation) =>
        !_disposed && _identity == identity && _generation == generation;

    public async Task EnsureAsync(ListenPlaybackSnapshot snapshot, IPlaybackCommandSink commands)
    {
        Observe(snapshot, commands);
        if (_identity is not { } identity)
        {
            return;
        }
        var generation = _generation;
        if (!_tracksLoaded)
        {
            if (Loading)
            {
                return;
            }
            Loading = true; Changed?.Invoke();
            try
            {
                var tracks = await api.GetTextTracksAsync(identity.AssetId, _reads.Token);
                if (!Current(identity, generation))
                {
                    return;
                }
                Tracks = tracks.Where(track => track.Id != Guid.Empty && track.Kind.Equals("Lyrics", StringComparison.OrdinalIgnoreCase))
                    .DistinctBy(track => track.Id).ToArray();
                _tracksLoaded = true;
            }
            catch (Exception) { if (Current(identity, generation)) { Status = "Lyrics could not be loaded."; _tracksLoaded = true; } }
            finally { if (Current(identity, generation))
            {
                Loading = false;
            } }
        }
        if (!Current(identity, generation))
        {
            return;
        }
        var projected = _projectedChoice;
        var choice = projected is Guid p && Tracks.Any(track => track.Id == p) ? p
            : SelectedTrackId is Guid previous && Tracks.Any(track => track.Id == previous) ? previous
            : Tracks.FirstOrDefault(track => track.IsPreferred)?.Id ?? Tracks.FirstOrDefault()?.Id;
        if (choice is not Guid selected)
        {
            Lines = []; SelectedTrackId = null; _contentLoaded = true;
            if (snapshot.LyricsSelection is { } old && commands is IPlaybackLyricsSelectionSink selectionSink)
            {
                await selectionSink.SelectLyricsAsync(snapshot, old.TrackId, _reads.Token);
            }
            if (Current(identity, generation))
            {
                Changed?.Invoke();
            }
            return;
        }
        if (SelectedTrackId == selected && (_contentLoaded || Loading))
        {
            return;
        }
        if (projected != selected && commands is IPlaybackLyricsSelectionSink owner)
        {
            SelectedTrackId = selected; Loading = true;
            var selection = ++_selectionGeneration;
            try
            {
                var reply = await owner.SelectLyricsAsync(snapshot, selected, _reads.Token);
                if (!Current(identity, generation) || selection != _selectionGeneration)
                {
                    return;
                }
                if (reply?.BooleanResult != true)
                {
                    Lines = []; Loading = false; Status = "That lyrics version is unavailable.";
                    Changed?.Invoke(); return;
                }
            }
            catch (Exception)
            {
                if (Current(identity, generation) && selection == _selectionGeneration)
                { Loading = false; Status = "Lyrics could not be loaded."; Changed?.Invoke(); }
                return;
            }
        }
        if (!Current(identity, generation))
        {
            return;
        }
        await LoadContentAsync(identity, generation, selected);
    }

    private async Task LoadContentAsync(PlaybackLyricsIdentity identity, long generation, Guid selected)
    {
        SelectedTrackId = selected; var selection = ++_selectionGeneration;
        Lines = []; Loading = true; _contentLoaded = false; Status = null; Changed?.Invoke();
        bool Fresh() => Current(identity, generation) && selection == _selectionGeneration && SelectedTrackId == selected;
        try
        {
            var content = await api.GetTextTrackContentAsync(identity.AssetId, selected, _reads.Token);
            if (!Fresh())
            {
                return;
            }
            Lines = PlaybackLyricsParser.Parse(content);
            if (content is null)
            {
                Status = "That lyrics version is unavailable.";
            }
            _contentLoaded = true;
        }
        catch (Exception) { if (Fresh())
        {
            Status = "That lyrics version is unavailable.";
        } }
        finally { if (Fresh()) { Loading = false; Changed?.Invoke(); } }
    }

    public async Task SelectAsync(Guid trackId)
    {
        if (_identity is not { } identity || _commands is not IPlaybackLyricsSelectionSink owner
            || !Tracks.Any(track => track.Id == trackId))
        {
            return;
        }
        var generation = _generation; var selection = ++_selectionGeneration;
        _projectedChoice = trackId;
        SelectedTrackId = trackId; Lines = []; Loading = true; _contentLoaded = false;
        Changed?.Invoke();
        try
        {
            var reply = await owner.SelectLyricsAsync(_snapshot, trackId, _reads.Token);
            if (!Current(identity, generation) || selection != _selectionGeneration)
            {
                return;
            }
            if (reply?.BooleanResult != true)
            { Loading = false; Status = "Playback changed before the lyrics choice arrived."; Changed?.Invoke(); return; }
            await LoadContentAsync(identity, generation, trackId);
        }
        catch (Exception)
        {
            if (Current(identity, generation) && selection == _selectionGeneration)
            { Loading = false; Status = "That lyrics version is unavailable."; Changed?.Invoke(); }
        }
    }

    public async Task PreferAsync()
    {
        if (_identity is not { } identity || SelectedTrackId is not Guid track || !Tracks.Any(item => item.Id == track))
        {
            return;
        }
        var generation = _generation; var selection = _selectionGeneration;
        bool Fresh() => Current(identity, generation) && selection == _selectionGeneration && SelectedTrackId == track;
        try
        {
            var saved = await api.SetPreferredTextTrackAsync(identity.AssetId, track, _reads.Token);
            if (!Fresh())
            {
                return;
            }
            if (saved)
            {
                var tracks = await api.GetTextTracksAsync(identity.AssetId, _reads.Token);
                if (!Fresh())
                {
                    return;
                }
                Tracks = tracks.Where(item => item.Id != Guid.Empty && item.Kind.Equals("Lyrics", StringComparison.OrdinalIgnoreCase)).DistinctBy(item => item.Id).ToArray();
                if (!Tracks.Any(item => item.Id == track))
                {
                    Lines = []; SelectedTrackId = null; _contentLoaded = false;
                    if (_commands is not null)
                    {
                        await EnsureAsync(_snapshot, _commands);
                    }
                    return;
                }
            }
            Status = saved ? "Preferred lyrics saved." : "Could not save preferred lyrics.";
        }
        catch (Exception) { if (Fresh())
        {
            Status = "Could not save preferred lyrics.";
        } }
        finally { if (Fresh())
        {
            Changed?.Invoke();
        } }
    }

    public async Task RefreshAsync()
    {
        if (Refreshing || _identity is not { } identity || _commands is null)
        {
            return;
        }
        var generation = _generation; var selection = _selectionGeneration;
        Refreshing = true; Status = null; Changed?.Invoke();
        bool Fresh() => Current(identity, generation) && selection == _selectionGeneration;
        try
        {
            var result = await api.RefreshTextTracksAsync(identity.AssetId, "lyrics", _reads.Token);
            if (!Fresh())
            {
                return;
            }
            var message = result?.message ?? "Lyrics search could not start.";
            _tracksLoaded = false; _contentLoaded = false;
            var selectedBeforeReload = SelectedTrackId;
            await EnsureAsync(_snapshot, _commands);
            if (Current(identity, generation) && SelectedTrackId == selectedBeforeReload && _contentLoaded)
            {
                Status = message;
            }
        }
        catch (Exception) { if (Fresh())
        {
            Status = "Lyrics search could not start.";
        } }
        finally { if (Current(identity, generation)) { Refreshing = false; Changed?.Invoke(); } }
    }

    public void Dispose() { _disposed = true; _reads.Cancel(); _reads.Dispose(); }
}
