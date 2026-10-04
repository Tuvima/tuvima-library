using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

public sealed record PlaybackLyricLine(string Text, double? StartSeconds);

public static partial class PlaybackLyricsParser
{
    [GeneratedRegex(@"\[(\d+):([0-5]\d)(?:\.(\d{1,3}))?\]", RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();
    [GeneratedRegex(@"^\[(?:ar|al|ti|by|re|ve|offset):.*\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Metadata();

    [GeneratedRegex(@"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Offset();

    public static IReadOnlyList<PlaybackLyricLine> Parse(string? text)
    {
        var offsetMatch = Offset().Matches(text ?? string.Empty).LastOrDefault();
        var offset = offsetMatch is not null && double.TryParse(offsetMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var milliseconds)
            ? milliseconds / 1000 : 0;
        var lines = new List<PlaybackLyricLine>();
        foreach (var raw in (text ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
        {
            var value = raw.Trim();
            if (value.Length == 0 || Metadata().IsMatch(value)) continue;
            var matches = Timestamp().Matches(value);
            // Only leading timestamp tags provide timing; malformed tags stay ordinary text.
            if (matches.Count == 0 || matches[0].Index != 0) { lines.Add(new(value, null)); continue; }
            var end = 0;
            var stamps = new List<double>();
            foreach (Match match in matches)
            {
                if (match.Index != end) break;
                end += match.Length;
                if (!double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var minutes)) continue;
                var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = match.Groups[3].Success ? double.Parse("0." + match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                var time = minutes * 60 + seconds + fraction;
                if (double.IsFinite(time) && time >= 0) stamps.Add(time);
            }
            var content = value[end..].Trim();
            if (content.Length == 0) continue;
            if (stamps.Count == 0) lines.Add(new(value, null));
            else lines.AddRange(stamps.Select(time => new PlaybackLyricLine(content, time - offset)));
        }
        // Preserve ordinary text order; timed files may contain repeated/multiple timestamps.
        return lines.All(line => line.StartSeconds.HasValue)
            ? lines.OrderBy(line => line.StartSeconds).ToArray() : lines;
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
        if (_identity is not { } identity) return;
        var generation = _generation;
        if (!_tracksLoaded)
        {
            if (Loading) return;
            Loading = true; Changed?.Invoke();
            try
            {
                var tracks = await api.GetTextTracksAsync(identity.AssetId, _reads.Token);
                if (!Current(identity, generation)) return;
                Tracks = tracks.Where(track => track.Id != Guid.Empty && track.Kind.Equals("Lyrics", StringComparison.OrdinalIgnoreCase))
                    .DistinctBy(track => track.Id).ToArray();
                _tracksLoaded = true;
            }
            catch (Exception) { if (Current(identity, generation)) { Status = "Lyrics could not be loaded."; _tracksLoaded = true; } }
            finally { if (Current(identity, generation)) Loading = false; }
        }
        if (!Current(identity, generation)) return;
        var projected = _projectedChoice;
        var choice = projected is Guid p && Tracks.Any(track => track.Id == p) ? p
            : SelectedTrackId is Guid previous && Tracks.Any(track => track.Id == previous) ? previous
            : Tracks.FirstOrDefault(track => track.IsPreferred)?.Id ?? Tracks.FirstOrDefault()?.Id;
        if (choice is not Guid selected)
        {
            Lines = []; SelectedTrackId = null; _contentLoaded = true;
            if (snapshot.LyricsSelection is { } old && commands is IPlaybackLyricsSelectionSink selectionSink)
                await selectionSink.SelectLyricsAsync(snapshot, old.TrackId, _reads.Token);
            if (Current(identity, generation)) Changed?.Invoke();
            return;
        }
        if (SelectedTrackId == selected && (_contentLoaded || Loading)) return;
        if (projected != selected && commands is IPlaybackLyricsSelectionSink owner)
        {
            SelectedTrackId = selected; Loading = true;
            var selection = ++_selectionGeneration;
            try
            {
                var reply = await owner.SelectLyricsAsync(snapshot, selected, _reads.Token);
                if (!Current(identity, generation) || selection != _selectionGeneration) return;
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
        if (!Current(identity, generation)) return;
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
            if (!Fresh()) return;
            Lines = PlaybackLyricsParser.Parse(content);
            if (content is null) Status = "That lyrics version is unavailable.";
            _contentLoaded = true;
        }
        catch (Exception) { if (Fresh()) Status = "That lyrics version is unavailable."; }
        finally { if (Fresh()) { Loading = false; Changed?.Invoke(); } }
    }

    public async Task SelectAsync(Guid trackId)
    {
        if (_identity is not { } identity || _commands is not IPlaybackLyricsSelectionSink owner
            || !Tracks.Any(track => track.Id == trackId)) return;
        var generation = _generation; var selection = ++_selectionGeneration;
        _projectedChoice = trackId;
        SelectedTrackId = trackId; Lines = []; Loading = true; _contentLoaded = false;
        Changed?.Invoke();
        try
        {
            var reply = await owner.SelectLyricsAsync(_snapshot, trackId, _reads.Token);
            if (!Current(identity, generation) || selection != _selectionGeneration) return;
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
        if (_identity is not { } identity || SelectedTrackId is not Guid track || !Tracks.Any(item => item.Id == track)) return;
        var generation = _generation; var selection = _selectionGeneration;
        bool Fresh() => Current(identity, generation) && selection == _selectionGeneration && SelectedTrackId == track;
        try
        {
            var saved = await api.SetPreferredTextTrackAsync(identity.AssetId, track, _reads.Token);
            if (!Fresh()) return;
            if (saved)
            {
                var tracks = await api.GetTextTracksAsync(identity.AssetId, _reads.Token);
                if (!Fresh()) return;
                Tracks = tracks.Where(item => item.Id != Guid.Empty && item.Kind.Equals("Lyrics", StringComparison.OrdinalIgnoreCase)).DistinctBy(item => item.Id).ToArray();
                if (!Tracks.Any(item => item.Id == track))
                {
                    Lines = []; SelectedTrackId = null; _contentLoaded = false;
                    if (_commands is not null) await EnsureAsync(_snapshot, _commands);
                    return;
                }
            }
            Status = saved ? "Preferred lyrics saved." : "Could not save preferred lyrics.";
        }
        catch (Exception) { if (Fresh()) Status = "Could not save preferred lyrics."; }
        finally { if (Fresh()) Changed?.Invoke(); }
    }

    public async Task RefreshAsync()
    {
        if (Refreshing || _identity is not { } identity || _commands is null) return;
        var generation = _generation; var selection = _selectionGeneration;
        Refreshing = true; Status = null; Changed?.Invoke();
        bool Fresh() => Current(identity, generation) && selection == _selectionGeneration;
        try
        {
            var result = await api.RefreshTextTracksAsync(identity.AssetId, "lyrics", _reads.Token);
            if (!Fresh()) return;
            var message = result?.message ?? "Lyrics search could not start.";
            _tracksLoaded = false; _contentLoaded = false;
            var selectedBeforeReload = SelectedTrackId;
            await EnsureAsync(_snapshot, _commands);
            if (Current(identity, generation) && SelectedTrackId == selectedBeforeReload && _contentLoaded) Status = message;
        }
        catch (Exception) { if (Fresh()) Status = "Lyrics search could not start."; }
        finally { if (Current(identity, generation)) { Refreshing = false; Changed?.Invoke(); } }
    }

    public void Dispose() { _disposed = true; _reads.Cancel(); _reads.Dispose(); }
}
