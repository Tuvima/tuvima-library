using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Models;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Reads subtitle text (WebVTT, SRT, and ASS/SSA through <see cref="SubtitleNormalizer"/>) into plain, time-stamped lines.
/// Hand-written and deterministic: no library, no network, no AI. Markup, styling tags and cue settings are dropped; the
/// words and their timing are kept.
/// </summary>
public static class SubtitleLineParser
{
    private static readonly Regex TimingPattern = new(
        @"^\s*(?<start>(?:\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})\s*-->\s*(?<end>(?:\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex Tags = new(@"<[^>]*>|\{\\[^}]*\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <param name="content">The subtitle file's text.</param>
    /// <param name="sourceFormat">The format name when known (<c>vtt</c>, <c>srt</c>, <c>ass</c>); otherwise it is detected.</param>
    /// <returns>Lines in time order with their position as <see cref="SubtitleLine.Index"/>; empty cues are left out.</returns>
    public static IReadOnlyList<SubtitleLine> Parse(string content, string? sourceFormat = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var text = content.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (LooksLikeAss(text, sourceFormat))
        {
            text = SubtitleNormalizer.NormalizeToWebVtt(text, "ass").Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        var cues = new List<(long Start, long End, string Text)>();
        foreach (var block in Regex.Split(text, @"\n{2,}", RegexOptions.None, TimeSpan.FromSeconds(5)))
        {
            var rows = block.Split('\n');
            var timingRow = -1;
            Match timing = Match.Empty;
            for (var i = 0; i < rows.Length && i < 2; i++)
            {
                timing = TimingPattern.Match(rows[i]);
                if (timing.Success)
                {
                    timingRow = i;
                    break;
                }
            }

            if (timingRow < 0
                || !TryReadTimestamp(timing.Groups["start"].Value, out var start)
                || !TryReadTimestamp(timing.Groups["end"].Value, out var end))
            {
                continue;
            }

            var lineText = Clean(string.Join(' ', rows.Skip(timingRow + 1)));
            if (lineText.Length == 0)
            {
                continue;
            }

            cues.Add((start, Math.Max(start, end), lineText));
        }

        return cues
            .OrderBy(cue => cue.Start)
            .ThenBy(cue => cue.End)
            .Select((cue, index) => new SubtitleLine(index, cue.Start, cue.End, cue.Text))
            .ToList();
    }

    /// <summary>Reads a timestamp such as <c>01:02:03,456</c>, <c>02:03.456</c> or <c>1:02:03.4</c> into milliseconds.</summary>
    public static bool TryReadTimestamp(string value, out long milliseconds)
    {
        milliseconds = 0;
        var separator = value.LastIndexOfAny(['.', ',']);
        if (separator < 0)
        {
            return false;
        }

        var fraction = value[(separator + 1)..];
        if (fraction.Length is < 1 or > 3 || !int.TryParse(fraction, NumberStyles.None, CultureInfo.InvariantCulture, out var fractionValue))
        {
            return false;
        }

        // ".4" means 400 ms, ".45" means 450 ms.
        var millis = fractionValue * (fraction.Length == 1 ? 100 : fraction.Length == 2 ? 10 : 1);

        var parts = value[..separator].Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var numbers = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        var hours = parts.Length == 3 ? numbers[0] : 0;
        var minutes = parts.Length == 3 ? numbers[1] : numbers[0];
        var seconds = parts.Length == 3 ? numbers[2] : numbers[1];
        milliseconds = (((hours * 60) + minutes) * 60 + seconds) * 1000 + millis;
        return true;
    }

    private static bool LooksLikeAss(string text, string? sourceFormat) =>
        (sourceFormat is not null && (sourceFormat.Equals("ass", StringComparison.OrdinalIgnoreCase)
            || sourceFormat.Equals("ssa", StringComparison.OrdinalIgnoreCase)))
        || text.Contains("[Events]", StringComparison.OrdinalIgnoreCase);

    private static string Clean(string value)
    {
        var withoutTags = Tags.Replace(value, string.Empty);
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return Spaces.Replace(decoded, " ").Trim();
    }
}
