using System.IO.Compression;
using System.Text.Json;
using MediaEngine.Storage.Backup;

namespace MediaEngine.Api.Services.Backups;

/// <summary>What a checked backup file says about itself. It names no title and no profile.</summary>
/// <param name="ManifestVersion">The manifest's <c>schema_version</c>.</param>
/// <param name="DatabaseEpoch">The manifest's <c>database_epoch</c>, shown to the person; it is not used to decide anything.</param>
/// <param name="CreatedAt">When the backup was made, when the manifest says.</param>
/// <param name="Profiles">Profiles in the backup.</param>
/// <param name="ItemsOfPersonalState">Rows of progress, bookmarks, favourites, choices and corrections.</param>
/// <param name="EntryCount">Entries in the zip file.</param>
/// <param name="ConfigurationFileCount">Configuration files in the zip file.</param>
/// <param name="UncompressedBytes">Size of everything once unpacked.</param>
internal sealed record RestoreArchiveInfo(
    string ManifestVersion,
    string DatabaseEpoch,
    DateTimeOffset? CreatedAt,
    int Profiles,
    int ItemsOfPersonalState,
    int EntryCount,
    int ConfigurationFileCount,
    long UncompressedBytes);

/// <summary>
/// The one check every route into a restore uses (scheduling it, the monthly restore check, the test restore, a setup upload and a
/// restore from a destination): the zip's own safety rules first, then the manifest, then the whole data part is read once. A backup that
/// passes can be imported; one that does not is refused with one plain sentence.
/// </summary>
internal static class RestoreArchiveValidator
{
    public const string OlderVersionMessage = "This backup is from an older version of Tuvima Library and can't be restored. Make a new backup with this version.";
    public const string DamagedMessage = "This backup file is damaged and can't be restored.";
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
    private static long CheckEntries(ZipArchive archive)
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
                throw new InvalidDataException("This backup was made by a different version of Tuvima Library and can't be restored.");
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
                0);
        }
    }

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int OptionalCount(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0 ? number : 0;
}
