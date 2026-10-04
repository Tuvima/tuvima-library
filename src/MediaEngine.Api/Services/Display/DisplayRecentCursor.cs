using System.Text.Json;
using MediaEngine.Contracts.Display;

namespace MediaEngine.Api.Services.Display;

internal sealed record DisplayRecentBoundary(DateTimeOffset AddedAt, string Key);

internal static class DisplayRecentCursor
{
    private sealed record Payload(int Version, string Type, Guid ProfileId, DateTimeOffset AddedAt, string Key);
    public static string NormalizeType(string? type)
    {
        var value = type?.Trim().ToLowerInvariant() ?? "all";
        return value is "all" or "watch" or "read" or "listen" or "view" ? value
            : throw new ArgumentException("Recent type must be all, watch, read, listen, or view.");
    }
    public static string Encode(string type, Guid profileId, DisplayRecentBoundary boundary) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Payload(1, type, profileId, boundary.AddedAt, boundary.Key)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static DisplayRecentBoundary? Decode(string? cursor, string type, Guid profileId)
    {
        if (cursor is null) return null;
        try
        {
            if (cursor.Length is 0 or > 1024) throw new FormatException();
            var value = cursor.Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
            var payload = JsonSerializer.Deserialize<Payload>(Convert.FromBase64String(value));
            if (payload is null || payload.Version != 1 || payload.Type != type || payload.ProfileId != profileId
                || payload.ProfileId == Guid.Empty || payload.AddedAt == default || !ValidKey(payload.Key, type)) throw new FormatException();
            return new(payload.AddedAt, payload.Key);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        { throw new ArgumentException("The recent cursor is invalid for this profile and filter.", nameof(cursor)); }
    }
    private static bool ValidKey(string? key, string type)
    {
        if (key is null) return false;
        var prefix = key.StartsWith("catalogue:", StringComparison.Ordinal) ? "catalogue:" : key.StartsWith("view:", StringComparison.Ordinal) ? "view:" : null;
        return prefix is not null && key.Length == prefix.Length + 32 && Guid.TryParseExact(key[prefix.Length..], "N", out var id) && id != Guid.Empty
            && key[prefix.Length..].All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')
            && (type == "all" || (type == "view" ? prefix == "view:" : prefix == "catalogue:"));
    }
    public static bool IsAfter(DateTimeOffset addedAt, string key, DisplayRecentBoundary? boundary) =>
        boundary is null || addedAt < boundary.AddedAt || (addedAt == boundary.AddedAt && string.CompareOrdinal(key, boundary.Key) > 0);
    public static string ViewKey(Guid id) => "view:" + id.ToString("N");
    public static DisplayRecentPageDto Page(string type, Guid profileId, IEnumerable<DisplayRecentItemDto> items, int limit)
    {
        var ordered = items.OrderByDescending(i => i.AddedAt).ThenBy(i => i.Key, StringComparer.Ordinal).Take(limit + 1).ToList();
        var hasMore = ordered.Count > limit;
        if (hasMore) ordered.RemoveAt(limit);
        var last = ordered.LastOrDefault();
        return new(type, ordered, hasMore && last is not null ? Encode(type, profileId, new(last.AddedAt, last.Key)) : null, hasMore);
    }
}
