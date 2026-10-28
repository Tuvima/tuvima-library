using System.Globalization;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>
/// The answer to "will this fit?" before anything is copied or moved (Libraries spec D8, D26). <paramref name="NeededBytes"/> is the
/// sum of the sources plus the safety margin; <paramref name="FreeBytes"/> is what the destination has (null when it could not be read,
/// in which case <paramref name="Verified"/> is false and the check does not refuse, since nothing was learned).
/// </summary>
public sealed record FreeSpaceResult(bool Fits, bool Verified, long SourceBytes, long MarginBytes, long NeededBytes, long? FreeBytes, string? Message);

/// <summary>
/// Adds up the sizes of what is about to be copied or moved and compares them with the free space on the destination, with a safety
/// margin (10% of the total or 1 GB, whichever is greater) so a copy never fills a drive to the last byte. Add files, phone backup,
/// drag and drop and conversion all call it before they start; if it will not fit nothing starts and the message says how much is
/// needed and how much is free.
/// </summary>
public static class FreeSpaceCheck
{
    /// <summary>The smallest safety margin: 1 GB.</summary>
    public const long MinimumMarginBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>The share of the total kept free as well, when that is more than the minimum.</summary>
    public const double MarginShare = 0.10;

    /// <summary>The safety margin for a total: 10% or 1 GB, whichever is greater.</summary>
    public static long MarginFor(long sourceBytes) => Math.Max(MinimumMarginBytes, (long)Math.Ceiling(Math.Max(0, sourceBytes) * MarginShare));

    /// <summary>Checks <paramref name="sourceSizes"/> against the free space of the drive that holds <paramref name="destination"/>.</summary>
    public static FreeSpaceResult Check(IEnumerable<long> sourceSizes, string destination) =>
        Check(sourceSizes, FreeBytesAt(destination));

    /// <summary>Checks <paramref name="sourceSizes"/> against a known amount of free space; null free space means it could not be read.</summary>
    public static FreeSpaceResult Check(IEnumerable<long> sourceSizes, long? freeBytes)
    {
        ArgumentNullException.ThrowIfNull(sourceSizes);
        var total = 0L;
        foreach (var size in sourceSizes)
        {
            total = checked(total + Math.Max(0, size));
        }

        var margin = MarginFor(total);
        var needed = checked(total + margin);
        if (freeBytes is not { } free)
        {
            return new FreeSpaceResult(true, false, total, margin, needed, null, null);
        }

        return needed <= free
            ? new FreeSpaceResult(true, true, total, margin, needed, free, null)
            : new FreeSpaceResult(false, true, total, margin, needed, free, $"Not enough space: {FormatBytes(needed)} needed, {FormatBytes(free)} free.");
    }

    /// <summary>The free space on the drive that holds <paramref name="destination"/> (the nearest folder that exists), or null when it cannot be read.</summary>
    public static long? FreeBytesAt(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return null;
        }

        try
        {
            var path = Path.GetFullPath(destination);
            while (!Directory.Exists(path))
            {
                var parent = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(parent))
                {
                    return null;
                }

                path = parent;
            }

            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A drive that cannot be read gives no answer rather than a wrong one; the caller treats it as "not verified".
            _ = exception;
            return null;
        }
    }

    /// <summary>A size as people read it: "7.8 GB", "640 MB", "12 KB".</summary>
    public static string FormatBytes(long bytes)
    {
        const double kilo = 1024;
        var value = (double)Math.Max(0, bytes);
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= kilo && unit < units.Length - 1)
        {
            value /= kilo;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes")
            : string.Create(CultureInfo.InvariantCulture, $"{(value >= 100 ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture))} {units[unit]}");
    }
}
