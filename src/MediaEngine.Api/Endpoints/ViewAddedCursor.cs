using System.Globalization;
using System.Text;
using MediaEngine.Contracts.LocalAssets;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The page position of a "recently added" list: the last item's added time and key, so the next page starts right after it.
/// Items added at the same moment are ordered by key, the same rule the Home recent list uses.
/// </summary>
internal static class ViewAddedCursor
{
    public static string Encode(LocalAssetDto last)
    {
        var raw = $"{last.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|view:{last.Id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>An empty cursor is the first page; anything unreadable is rejected.</summary>
    public static bool TryDecode(string? cursor, out DateTimeOffset? addedBefore, out string? key)
    {
        addedBefore = null;
        key = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|', 2);
            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks > DateTime.MaxValue.Ticks
                || !parts[1].StartsWith("view:", StringComparison.Ordinal))
            {
                return false;
            }

            addedBefore = new DateTimeOffset(ticks, TimeSpan.Zero);
            key = parts[1];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
