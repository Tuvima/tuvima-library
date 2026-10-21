using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MediaEngine.Api.Services.Plugins.Declarative;

/// <summary>One channel from a channel list or guide.</summary>
internal sealed record ChannelEntry(string Id, string Name, string? LogoUrl, string? Group, string? StreamUrl);

/// <summary>One programme from an XMLTV guide.</summary>
internal sealed record GuideProgramme(string ChannelId, string Title, DateTimeOffset Start, DateTimeOffset Stop, string? Description);

/// <summary>Reads the two plain-text channel formats. Both are tolerant of extra lines and unknown attributes.</summary>
internal static partial class ChannelListParsers
{
    /// <summary>Entries of an M3U playlist: each <c>#EXTINF</c> line with the address on the line after it. Returns null with a plain reason when the text is not an M3U list.</summary>
    public static IReadOnlyList<ChannelEntry>? ParseM3u(string text, out string? error)
    {
        error = null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.TrimStart('﻿').StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) != true)
        {
            error = "The channel list does not start with #EXTM3U, so it is not an M3U list.";
            return null;
        }

        var entries = new List<ChannelEntry>();
        string? pendingInfo = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                pendingInfo = line;
            }
            else if (line.Length > 0 && !line.StartsWith('#') && pendingInfo is not null)
            {
                entries.Add(ToEntry(pendingInfo, line, entries.Count));
                pendingInfo = null;
            }
        }

        return entries;
    }

    /// <summary>Channels and programmes of an XMLTV guide. Returns false with a plain reason when the text is not XMLTV.</summary>
    public static bool TryParseXmltv(string text, out IReadOnlyList<ChannelEntry> channels, out IReadOnlyList<GuideProgramme> programmes, out string? error)
    {
        channels = [];
        programmes = [];
        error = null;
        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            error = $"The guide is not valid XML: {ex.Message}";
            return false;
        }

        if (document.Root is not { Name.LocalName: "tv" } root)
        {
            error = "The guide's top element is not <tv>, so it is not XMLTV.";
            return false;
        }

        channels = root.Elements("channel")
            .Select(element => new ChannelEntry(
                (string?)element.Attribute("id") ?? "",
                (string?)element.Element("display-name") ?? (string?)element.Attribute("id") ?? "",
                (string?)element.Element("icon")?.Attribute("src"),
                null,
                null))
            .Where(entry => entry.Id.Length > 0)
            .ToList();

        var guide = new List<GuideProgramme>();
        foreach (var element in root.Elements("programme"))
        {
            var channelId = (string?)element.Attribute("channel");
            if (channelId is null
                || !TryXmltvTime((string?)element.Attribute("start"), out var start)
                || !TryXmltvTime((string?)element.Attribute("stop"), out var stop))
            {
                continue;
            }

            guide.Add(new GuideProgramme(channelId, (string?)element.Element("title") ?? "", start, stop, (string?)element.Element("desc")));
        }

        programmes = guide;
        return true;
    }

    /// <summary>XMLTV times look like <c>20261021183000 +0100</c>; the zone may be missing, which means UTC.</summary>
    internal static bool TryXmltvTime(string? text, out DateTimeOffset time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = XmltvTime().Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        var local = DateTime.ParseExact(match.Groups["t"].Value, "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var offset = TimeSpan.Zero;
        if (match.Groups["z"].Success)
        {
            var zone = match.Groups["z"].Value;
            var sign = zone[0] == '-' ? -1 : 1;
            offset = new TimeSpan(sign * int.Parse(zone.AsSpan(1, 2)), sign * int.Parse(zone.AsSpan(3, 2)), 0);
        }

        time = new DateTimeOffset(local, offset);
        return true;
    }

    private static ChannelEntry ToEntry(string info, string url, int index)
    {
        var attributes = ExtinfAttribute().Matches(info).ToDictionary(
            match => match.Groups["k"].Value.ToLowerInvariant(),
            match => match.Groups["v"].Value,
            StringComparer.Ordinal);
        var comma = info.LastIndexOf(',');
        var name = comma >= 0 ? info[(comma + 1)..].Trim() : "";
        if (name.Length == 0)
        {
            name = attributes.GetValueOrDefault("tvg-name") ?? $"Channel {index + 1}";
        }

        var id = attributes.GetValueOrDefault("tvg-id");
        return new ChannelEntry(
            string.IsNullOrWhiteSpace(id) ? name : id,
            name,
            attributes.GetValueOrDefault("tvg-logo"),
            attributes.GetValueOrDefault("group-title"),
            url);
    }

    [GeneratedRegex("(?<k>[A-Za-z0-9_-]+)=\"(?<v>[^\"]*)\"")]
    private static partial Regex ExtinfAttribute();

    [GeneratedRegex(@"^(?<t>\d{14})(?:\s*(?<z>[+-]\d{4}))?$")]
    private static partial Regex XmltvTime();
}
