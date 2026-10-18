using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Schema.Upgrades;

/// <summary>
/// Writes a consistent copy of the data store before the first pending upgrade step runs, checks that the copy is a
/// valid data store at the step it was taken from, and keeps only the newest few copies.
/// Copies live in a <c>safety-copies</c> subfolder of the backups folder so the user-facing backup list
/// (<c>tuvima-backup-*.zip</c> in the backups folder itself) never shows them.
/// </summary>
internal sealed class DataStoreSafetyCopy
{
    /// <summary>Subfolder of the backups folder that holds safety copies.</summary>
    public const string FolderName = "safety-copies";

    /// <summary>Every safety copy file name starts with this and ends in <c>.db</c>.</summary>
    public const string FilePrefix = "tuvima-safety-copy-";

    /// <summary>
    /// A copy is written under its final name plus this extension and renamed only after it has been checked,
    /// so a file with the final name is always a checked copy.
    /// </summary>
    public const string PartialExtension = ".partial";

    /// <summary>How many safety copies are kept (D9).</summary>
    public const int CopiesToKeep = 2;

    private const double SpaceMargin = 1.10;

    // The only names that count as safety copies when pruning; the stamp (UTC) is what orders them.
    private static readonly Regex FinalCopyName = new(
        "^" + FilePrefix + "(?<stamp>[0-9]{8}-[0-9]{6})-step-[0-9]{4}-to-[0-9]{4}(?:-[0-9]+)?\\.db$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _databasePath;
    private readonly string _folder;
    private readonly Func<string, long> _freeSpaceLookup;
    private readonly List<string> _warnings = [];

    /// <param name="databasePath">The data store file being upgraded.</param>
    /// <param name="backupDirectory">The backups folder; copies go in its <see cref="FolderName"/> subfolder.</param>
    /// <param name="freeSpaceLookup">Free bytes on the drive holding a folder. Null uses the real drive.</param>
    public DataStoreSafetyCopy(
        string databasePath,
        string backupDirectory,
        Func<string, long>? freeSpaceLookup = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        _databasePath = Path.GetFullPath(databasePath);
        _folder = FolderFor(backupDirectory);
        _freeSpaceLookup = freeSpaceLookup ?? DefaultFreeSpace;
    }

    /// <summary>The folder safety copies are written to for a backups folder.</summary>
    public static string FolderFor(string backupDirectory)
        => Path.Combine(Path.GetFullPath(backupDirectory), FolderName);

    /// <summary>Problems that did not stop the copy, such as an old copy that could not be deleted.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// The runner's <see cref="DataStoreUpgradeOptions.BeforeFirstPendingStep"/> hook, bound to the data store connection.
    /// </summary>
    public Func<DataStoreUpgradePlan, string?> AsHook(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return plan => Create(connection, plan);
    }

    /// <summary>Writes, checks and prunes a safety copy, returning its path.</summary>
    /// <exception cref="DataStoreUpgradeRefusedException">There is not enough free disk space, or the copy did not check out.</exception>
    public string Create(SqliteConnection connection, DataStoreUpgradePlan plan)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(plan);

        CheckFreeSpace();
        Directory.CreateDirectory(_folder);

        var toNumber = plan.PendingSteps[^1].Number;
        var stamp = plan.Now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = string.Create(
            CultureInfo.InvariantCulture,
            $"{FilePrefix}{stamp}-step-{plan.FromNumber:0000}-to-{toNumber:0000}");
        var path = UniquePath(baseName);
        var partial = path + PartialExtension;

        try
        {
            WriteCopy(connection, partial);
            VerifyCopy(partial, plan.FromNumber);
            File.Move(partial, path);
        }
        catch (Exception exception)
        {
            // Never leave a half-written or unchecked copy behind. If one cannot be removed, say so in the refusal.
            var leftovers = DeleteFailedCopy(partial);
            if (leftovers.Count == 0)
            {
                throw;
            }

            throw new DataStoreUpgradeRefusedException(
                $"{exception.Message} {string.Join(" ", leftovers)}",
                exception);
        }

        Prune(path);
        return path;
    }

    private void CheckFreeSpace()
    {
        var needed = RequiredBytes();
        var available = _freeSpaceLookup(_folder);
        if (available >= needed)
        {
            return;
        }

        throw new DataStoreUpgradeRefusedException(
            $"Not enough free disk space to make a safety copy before upgrading the data store '{_databasePath}'. "
            + $"The copy goes in '{_folder}' and needs about {needed:N0} bytes, but only {available:N0} bytes are free. "
            + "Free up space or set TUVIMA_BACKUP_DIR to a drive with more room. Nothing was changed.");
    }

    private long RequiredBytes()
    {
        var size = FileLength(_databasePath) + FileLength(_databasePath + "-wal");
        return (long)Math.Ceiling(size * SpaceMargin);
    }

    private static long FileLength(string path)
        => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static long DefaultFreeSpace(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(folder);
            return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Network shares and some mounts cannot report free space. The copy itself is still the backstop:
            // if the drive is full, writing it fails and the upgrade is refused before anything changes.
            return long.MaxValue;
        }
    }

    private string UniquePath(string baseName)
    {
        var candidate = Path.Combine(_folder, baseName + ".db");
        for (var suffix = 2; File.Exists(candidate) || File.Exists(candidate + PartialExtension); suffix++)
        {
            candidate = Path.Combine(_folder, string.Create(CultureInfo.InvariantCulture, $"{baseName}-{suffix}.db"));
        }

        return candidate;
    }

    private static void WriteCopy(SqliteConnection connection, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $path;";
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    private static void VerifyCopy(string path, int expectedStep)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var copy = new SqliteConnection(connectionString);
        copy.Open();

        var check = ScalarText(copy, "PRAGMA quick_check;");
        if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new DataStoreUpgradeRefusedException(
                $"The safety copy of the data store could not be checked ({check}). Nothing was changed.");
        }

        var recorded = ScalarText(copy, "SELECT COALESCE(MAX(number), 0) FROM data_store_upgrades;");
        if (!string.Equals(recorded, expectedStep.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new DataStoreUpgradeRefusedException(
                $"The safety copy of the data store is at step {recorded} but should be at step {expectedStep}. Nothing was changed.");
        }
    }

    private static string? ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private List<string> DeleteFailedCopy(string partialPath)
    {
        // The original failure is what the person needs to see; a leftover file is reported beside it.
        var leftovers = new List<string>();
        foreach (var file in new[] { partialPath, partialPath + "-wal", partialPath + "-shm", partialPath + "-journal" })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var warning = $"Could not delete the unfinished safety copy '{file}': {exception.Message}";
                leftovers.Add(warning);
                _warnings.Add(warning);
            }
        }

        return leftovers;
    }

    private void Prune(string newest)
    {
        // Only final names are considered (never .partial files, never other files), newest first by the
        // timestamp in the name; the copy just made is always kept.
        var older = new DirectoryInfo(_folder)
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(file => FinalCopyName.IsMatch(file.Name))
            .Where(file => !string.Equals(file.FullName, newest, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => FinalCopyName.Match(file.Name).Groups["stamp"].Value, StringComparer.Ordinal)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(CopiesToKeep - 1)
            .ToList();

        foreach (var file in older)
        {
            try
            {
                file.Delete();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _warnings.Add($"Could not delete the old safety copy '{file.FullName}': {exception.Message}");
            }
        }
    }
}
