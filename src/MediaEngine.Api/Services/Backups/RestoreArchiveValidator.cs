using System.IO.Compression;
using System.Text.Json;
using MediaEngine.Storage.Backup;

namespace MediaEngine.Api.Services.Backups;

/// <summary>What a checked backup file says about itself. It names no title and no profile.</summary>
/// <param name="ManifestVersion">The manifest's <c>schema_version</c>.</param>
/// <param name="DatabaseEpoch">The manifest's <c>database_epoch</c>, shown to the person. A backup from another data structure is refused before this is built.</param>
/// <param name="CreatedAt">When the backup was made, when the manifest says.</param>
/// <param name="Profiles">Profiles in the backup.</param>
/// <param name="ItemsOfPersonalState">Rows of progress, bookmarks, favourites, choices and corrections.</param>
/// <param name="EntryCount">Entries in the zip file.</param>
/// <param name="ConfigurationFileCount">Configuration files in the zip file.</param>
/// <param name="UncompressedBytes">Size of everything once unpacked.</param>
/// <param name="ConfigurationRows">Settings rows in the backup.</param>
/// <param name="Titles">Titles the personal state is about.</param>
/// <param name="FileOnlyTitles">Titles a restore can match by exact file only.</param>
internal sealed record RestoreArchiveInfo(
    string ManifestVersion,
    string DatabaseEpoch,
    DateTimeOffset? CreatedAt,
    int Profiles,
    int ItemsOfPersonalState,
    int EntryCount,
    int ConfigurationFileCount,
    long UncompressedBytes,
    int ConfigurationRows = 0,
    int Titles = 0,
    int FileOnlyTitles = 0);

/// <summary>
/// The one check every route into a restore uses (scheduling it, the monthly restore check, the test restore, a setup upload and a
/// restore from a destination): the zip's own safety rules first, then the manifest, then the whole data part is read once. A backup that
/// passes can be imported; one that does not is refused with one plain sentence.
/// </summary>
internal static class RestoreArchiveValidator
{
    public const string OlderVersionMessage = "This backup is from an older version of the Library and can't be restored. Make a new backup with this version.";
    public const string DamagedMessage = "This backup file is damaged and can't be restored.";

    /// <summary>The data structure a backup must have been made with (a different one is refused, users spec D11).</summary>
    private static readonly string CurrentEpoch = BackupManifestWriter.DatabaseEpoch;

    public const string CurrentManifestVersion = "3.0";

    private const long MaxUncompressedBytes = 16L * 1024 * 1024 * 1024;
    private const int MaxArchiveEntries = 10_000;
    private const long MaxManifestBytes = 1024 * 1024;

    /// <summary>Checks the backup file at <paramref name="archivePath"/>; throws <see cref="InvalidDataException"/> with a plain sentence when it cannot be restored.</summary>
    public static RestoreArchiveInfo Validate(string archivePath)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(archivePath);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(DamagedMessage, ex);
        }

        using (archive)
        {
            return Validate(archive);
        }
    }

    private static RestoreArchiveInfo Validate(ZipArchive archive)
    {
        var uncompressed = CheckEntries(archive);
        var manifest = ReadManifest(archive);

        try
        {
            BackupArchiveFormat.ReadBackupFile(archive);
        }
        catch (InvalidDataException ex)
        {
            // The reader's own reasons name entries; the person gets one sentence.
            throw new InvalidDataException(DamagedMessage, ex);
        }
        catch (IOException ex)
        {
            throw new InvalidDataException(DamagedMessage, ex);
        }

        return manifest with
        {
            EntryCount = archive.Entries.Count,
            ConfigurationFileCount = archive.Entries.Count(entry => entry.FullName.StartsWith("config/", StringComparison.Ordinal) && !string.IsNullOrEmpty(entry.Name)),
            UncompressedBytes = uncompressed,
        };
    }

    /// <summary>Unsafe or duplicate paths, host secret material, an implausible compression ratio, and too many or too large entries.</summary>
    internal static long CheckEntries(ZipArchive archive)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxArchiveEntries)
        {
            throw new InvalidDataException("The backup contains an unsupported number of entries.");
        }

        long uncompressed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal)
                || normalized.Contains("../", StringComparison.Ordinal)
                || !seen.Add(normalized))
            {
                throw new InvalidDataException("The backup contains an unsafe or duplicate entry path.");
            }

            if (IsForbiddenEntry(normalized))
            {
                throw new InvalidDataException($"The backup contains forbidden host secret material: {normalized}");
            }

            checked { uncompressed += entry.Length; }
            if (uncompressed > MaxUncompressedBytes)
            {
                throw new InvalidDataException("The expanded backup exceeds the 16 GB safety limit.");
            }

            if (entry.CompressedLength > 0 && entry.Length / Math.Max(1, entry.CompressedLength) > 250)
            {
                throw new InvalidDataException("The backup contains an unsafe compression ratio.");
            }
        }

        return uncompressed;
    }

    private static bool IsForbiddenEntry(string relative) =>
        relative.StartsWith("config/secrets/", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("config/.secrets/", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("config/.keys/", StringComparison.OrdinalIgnoreCase)
        || relative.EndsWith(".restore-pending.json", StringComparison.OrdinalIgnoreCase)
        || relative.EndsWith(".restore-import.json", StringComparison.OrdinalIgnoreCase)
        || relative.EndsWith(".restore-import-failed.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>When the backup was made, from its manifest; null when the manifest does not say.</summary>
    public static DateTimeOffset? ReadCreatedAt(ZipArchive archive) => ReadManifest(archive).CreatedAt;

    private static RestoreArchiveInfo ReadManifest(ZipArchive archive)
    {
        // A backup of the earlier layout held the whole data store file; it is refused whatever its manifest says.
        if (archive.GetEntry("database/library.db") is not null)
        {
            throw new InvalidDataException(OlderVersionMessage);
        }

        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("The backup does not contain manifest.json.");
        if (manifestEntry.Length > MaxManifestBytes)
        {
            throw new InvalidDataException("The backup manifest is unexpectedly large.");
        }

        JsonDocument document;
        try
        {
            document = ZipArchiveJson.Parse(manifestEntry);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(DamagedMessage, ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(DamagedMessage);
            }

            var version = OptionalString(root, "schema_version");
            var kind = OptionalString(root, "kind");

            if (version is null or "1.0" or "2.0" || kind is null)
            {
                throw new InvalidDataException(OlderVersionMessage);
            }

            if (!string.Equals(version, CurrentManifestVersion, StringComparison.Ordinal)
                || !string.Equals(kind, BackupArchiveFormat.ContentsKind, StringComparison.Ordinal))
            {
                throw new InvalidDataException("This backup was made by a different version of the Library and can't be restored.");
            }

            // People moved onto their profiles in the current data structure (users spec D11): a backup from an earlier one is refused.
            var epochOrder = CompareEpoch(OptionalString(root, "database_epoch"));
            if (epochOrder < 0)
            {
                throw new InvalidDataException(OlderVersionMessage);
            }

            if (epochOrder > 0)
            {
                throw new InvalidDataException("This backup was made by a different version of the Library and can't be restored.");
            }

            if (root.TryGetProperty("includes_secrets", out var includesSecrets) && includesSecrets.ValueKind == JsonValueKind.True)
            {
                throw new InvalidDataException("Backups containing secrets cannot be restored.");
            }

            DateTimeOffset? createdAt = null;
            if (root.TryGetProperty("created_at", out var created) && created.ValueKind == JsonValueKind.String && created.TryGetDateTimeOffset(out var parsed))
            {
                createdAt = parsed;
            }

            return new RestoreArchiveInfo(
                version,
                OptionalString(root, "database_epoch") ?? "unknown",
                createdAt,
                OptionalCount(root, "profiles"),
                OptionalCount(root, "personal_rows") + OptionalCount(root, "choice_rows") + OptionalCount(root, "correction_rows"),
                0,
                0,
                0,
                OptionalCount(root, "configuration_rows"),
                OptionalCount(root, "titles"),
                OptionalCount(root, "titles_recognised_by_file_only"));
        }
    }

    /// <summary>
    /// How a file's data structure compares with this build's: negative for an earlier one (or one that does not say), zero for the same,
    /// positive for a later one. Epochs read <c>guid-blob-vNN-...</c>; anything else counts as earlier.
    /// </summary>
    private static int CompareEpoch(string? epoch)
    {
        if (string.Equals(epoch, CurrentEpoch, StringComparison.Ordinal))
        {
            return 0;
        }

        return EpochNumber(epoch) is { } found && EpochNumber(CurrentEpoch) is { } current && found > current ? 1 : -1;
    }

    private static int? EpochNumber(string? epoch)
    {
        const string prefix = "guid-blob-v";
        if (epoch is null || !epoch.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var digits = new string(epoch.Skip(prefix.Length).TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int OptionalCount(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0 ? number : 0;
}
