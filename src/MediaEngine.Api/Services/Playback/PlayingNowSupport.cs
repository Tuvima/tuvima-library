using System.ComponentModel;
using System.Diagnostics;
using MediaEngine.Contracts.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>Turns an encode's share of the processor into the word the list shows (playing-now spec D9).</summary>
public static class EncodeHeaviness
{
    /// <summary>Below this share of the whole processor an encode is Light.</summary>
    public const double MediumFrom = 0.15;

    /// <summary>From this share of the whole processor an encode is Heavy: most of what the Engine is allowed to use.</summary>
    public const double HeavyFrom = 0.45;

    public static string Classify(double share) =>
        share >= HeavyFrom ? PlayingNowHeaviness.Heavy
        : share >= MediumFrom ? PlayingNowHeaviness.Medium
        : PlayingNowHeaviness.Light;

    /// <summary>The step label for a ladder rung (the four steps of the playback path).</summary>
    public static string StepFor(string rung) => rung switch
    {
        PlaybackRungs.Direct => PlayingNowSteps.PlayingDirectly,
        PlaybackRungs.Repackage => PlayingNowSteps.Repackaging,
        PlaybackRungs.ConvertSound => PlayingNowSteps.ConvertingAudio,
        _ => PlayingNowSteps.EncodingPicture,
    };

    /// <summary>"Processor" or "Graphics card" for an encode place, or null when nothing is encoded.</summary>
    public static string? PlaceWords(string ranOn) => ranOn switch
    {
        PlaybackEncodePlaces.GraphicsCard => "Graphics card",
        PlaybackEncodePlaces.Processor => "Processor",
        _ => null,
    };
}

/// <summary>Measures how much of the processor the encoders use, as a share of the whole machine (0 to 1).</summary>
public interface IEncodeLoadSampler
{
    /// <summary>The encoders' combined share since the last call, or null when it cannot be measured.</summary>
    double? Sample();
}

/// <inheritdoc cref="IEncodeLoadSampler"/>
public sealed class FfmpegProcessLoadSampler(TimeProvider clock) : IEncodeLoadSampler
{
    private readonly Dictionary<int, TimeSpan> _previous = [];
    private DateTimeOffset _lastAt;

    public double? Sample()
    {
        var now = clock.GetUtcNow();
        var elapsed = _lastAt == default ? TimeSpan.Zero : now - _lastAt;
        _lastAt = now;
        var current = new Dictionary<int, TimeSpan>();
        var used = TimeSpan.Zero;
        foreach (var process in Process.GetProcessesByName("ffmpeg"))
        {
            try
            {
                var total = process.TotalProcessorTime;
                current[process.Id] = total;
                used += _previous.TryGetValue(process.Id, out var before) ? total - before : TimeSpan.Zero;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // The encoder ended between listing and reading it: it simply adds nothing to this sample.
            }
            finally
            {
                process.Dispose();
            }
        }

        _previous.Clear();
        foreach (var pair in current)
        {
            _previous[pair.Key] = pair.Value;
        }

        return elapsed <= TimeSpan.Zero
            ? null
            : Math.Clamp(used.TotalSeconds / (elapsed.TotalSeconds * Environment.ProcessorCount), 0d, 1d);
    }
}
