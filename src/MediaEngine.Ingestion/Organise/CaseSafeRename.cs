using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Organise;

/// <summary>
/// Renames a file whose new name differs from the old one only in letter case (<c>dune.mkv</c> to <c>Dune.mkv</c>). On a
/// drive that ignores case (Windows, SMB and CIFS shares, exFAT, macOS) the new name already "exists" because it is the same
/// file, so the rename goes through a temporary name in the same folder. That is one safe path on every system, and the file
/// is never mistaken for a duplicate of itself.
/// </summary>
public static class CaseSafeRename
{
    /// <summary>The ending of the short-lived name used in the middle of a letter-case rename.</summary>
    public const string TemporarySuffix = ".case-rename";

    /// <summary>
    /// The short-lived name a file has between the two steps. It is derived from the file's own name, so a restart can find a
    /// file a stopped rename left under it. A leftover with that name makes the first step fail rather than overwrite anything.
    /// </summary>
    public static string TemporaryName(string from) =>
        Path.Combine(Path.GetDirectoryName(from)!, $".{Path.GetFileName(from)}{TemporarySuffix}");

    /// <summary>
    /// Why a name that differs only in letter case is left as it is when nothing proves the two names are one file. Recorded for
    /// the media file and for each of its sidecars.
    /// </summary>
    public const string UnprovenReason =
        "Library could not confirm the name differs only in letter case (the file may be in use, or this drive does not support the check), so the name was left as it is.";

    /// <summary>True when the two paths are the same except for letter case.</summary>
    public static bool IsCaseOnlyChange(string from, string to) =>
        !string.Equals(from, to, StringComparison.Ordinal)
        && string.Equals(from, to, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Moves <paramref name="from"/> to <paramref name="to"/>. A letter-case-only change goes through a temporary name, and is
    /// put back under the old name if the second step fails, so the file is never left under a name nobody knows.
    /// </summary>
    public static void Move(IOrganiseFileMover mover, string from, string to, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(mover);
        ArgumentNullException.ThrowIfNull(logger);

        if (!IsCaseOnlyChange(from, to))
        {
            mover.Move(from, to);
            return;
        }

        var temporary = TemporaryName(from);
        mover.Move(from, temporary);
        try
        {
            mover.Move(temporary, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The second step of renaming {From} to {To} failed; putting the file back", from, to);
            try
            {
                mover.Move(temporary, from);
            }
            catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
            {
                // Nothing more can be done here; the file sits under the temporary name and the log says where.
                logger.LogError(restoreEx, "Could not put {From} back; it is at {Temporary}", from, temporary);
            }

            throw;
        }
    }
}
