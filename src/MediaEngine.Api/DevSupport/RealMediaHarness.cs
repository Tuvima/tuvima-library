using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Contracts.Startup;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.DevSupport;

/// <summary>Offline, source-preserving reset. Never invokes the legacy source/fixture wipe.</summary>
public static class RealMediaHarness
{
    public const string SettingsFile = "real-media-harness.json";
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static readonly string[] CatalogueFolders = ["books", "audiobooks", "movies", "tv", "music"];
    public static readonly string[] ViewFolders = ["personal_photos", "personal_videos"];

    public static RealMediaRun? Load(string configDirectory) =>
        File.Exists(Path.Combine(configDirectory, SettingsFile))
            ? JsonSerializer.Deserialize<RealMediaRun>(File.ReadAllText(Path.Combine(configDirectory, SettingsFile)), Json)
            : null;

    public static void Save(string path, object value)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public static bool Contains(string root, string path) =>
        string.Equals(Path.GetFullPath(root), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void RequireSeparate(string source, string output)
    {
        if (Contains(source, output) || Contains(output, source))
            throw new InvalidOperationException($"Protected source and output overlap: {source}; {output}");
        RejectReparseAncestors(output);
    }

    public static void RejectReparseAncestors(string path)
    {
        for (var entry = new DirectoryInfo(Path.GetFullPath(path)); entry is not null; entry = entry.Parent)
            if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Reparse-point path is not permitted: {entry.FullName}");
    }

    public static IEnumerable<FileSystemInfo> Enumerate(string root)
    {
        RejectReparseAncestors(root);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"Reparse-point entry is not permitted: {entry.FullName}");
                yield return entry;
                if (entry is DirectoryInfo child) pending.Push(child);
            }
        }
    }

    public static async Task<List<RealMediaFile>> SnapshotAsync(string root, bool hash, CancellationToken ct = default)
    {
        var entries = Enumerate(root).Prepend(new DirectoryInfo(root)).OrderBy(e => e.FullName, StringComparer.Ordinal).ToArray();
        var result = new List<RealMediaFile>();
        var count = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            entry.Refresh();
            var before = Describe(root, entry);
            string? digest = null;
            if (entry is FileInfo file)
            {
                await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
                RejectHardLinks(stream);
                if (hash)
                {
                    digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                    if (++count % 25 == 0) Console.WriteLine($"Source verification: {count} files hashed.");
                }
            }
            entry.Refresh();
            if (before != Describe(root, entry)) throw new IOException($"Source changed during verification: {entry.FullName}");
            result.Add(before with { Sha256 = digest });
        }
        return result;
    }

    private static RealMediaFile Describe(string root, FileSystemInfo entry) => new(
        Path.GetRelativePath(root, entry.FullName), entry is DirectoryInfo,
        entry is FileInfo file ? file.Length : 0, entry.CreationTimeUtc, entry.LastWriteTimeUtc,
        (int)entry.Attributes, null, SecurityDescriptor(entry));

    private static string? SecurityDescriptor(FileSystemInfo entry)
    {
        if (!OperatingSystem.IsWindows()) return null;
        FileSystemSecurity security = entry is FileInfo file ? file.GetAccessControl() : ((DirectoryInfo)entry).GetAccessControl();
        return security.GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
    }

    public static string[] Differences(IReadOnlyList<RealMediaFile> expected, IReadOnlyList<RealMediaFile> actual, bool hashes)
    {
        var left = expected.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var right = actual.ToDictionary(e => e.Path, StringComparer.Ordinal);
        return left.Keys.Union(right.Keys, StringComparer.Ordinal).Where(path =>
            !left.TryGetValue(path, out var a) || !right.TryGetValue(path, out var b)
            || (hashes ? a != b : a with { Sha256 = null } != b with { Sha256 = null })).ToArray();
    }

    public static string ConfigHash(string directory) => Convert.ToHexString(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join("\n", new[] { "libraries.json", "core.json", "writeback.json", "transcoding.json" }
            .Select(name => name + "\n" + File.ReadAllText(Path.Combine(directory, name)))))));

    public static void ValidateConfiguration(string directory, RealMediaRun run)
    {
        if (run.Phase != "Ready") throw new InvalidOperationException("Real-media preparation is incomplete; workers must remain stopped.");
        if (ConfigHash(directory) != run.ConfigurationHash)
            throw new InvalidOperationException("Real-media configuration changed. Revalidate offline before starting workers.");
        var libraries = JsonSerializer.Deserialize<LibrariesConfiguration>(File.ReadAllText(Path.Combine(directory, "libraries.json")))!;
        ValidateSources(libraries, run.SourceRoot);
        var core = JsonSerializer.Deserialize<CoreConfiguration>(File.ReadAllText(Path.Combine(directory, "core.json")))!;
        if (!string.IsNullOrWhiteSpace(core.DataRoot)) RequireSeparate(run.SourceRoot, core.DataRoot);
        var transcoding = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "transcoding.json")))!;
        var variantCache = transcoding["variant_cache_path"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(variantCache)) RequireSeparate(run.SourceRoot, Path.GetFullPath(variantCache));
        foreach (var path in new[] { run.OutputDirectory, run.DatabasePath, run.LibraryRoot, directory, Path.GetTempPath() })
            RequireSeparate(run.SourceRoot, path);
        foreach (var name in new[] { "TUVIMA_DB_PATH", "TUVIMA_LIBRARY_ROOT", "TUVIMA_WATCH_FOLDER" })
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                throw new InvalidOperationException($"Remove {name}: real-media mode uses the validated configuration only.");
    }

    public static void ValidateSources(LibrariesConfiguration libraries, string sourceRoot)
    {
        var errors = MediaEngine.Storage.Configuration.JsonConfigValidator.Validate(libraries, "libraries.json");
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
        var sources = libraries.Libraries.SelectMany(l => l.Sources).ToArray();
        if (sources.Length != CatalogueFolders.Length || libraries.Libraries.Count != CatalogueFolders.Length
            || sources.Any(s => s.ManagementMode != LibrarySourceManagementModes.ExistingLibrary
                || s.AccessMode != LibrarySourceAccessModes.ReadOnly || s.ParticipatesInOrganization || s.WritebackOverride != false)
            || CatalogueFolders.Any(folder => sources.Count(s => string.Equals(Path.GetFullPath(s.Path), Path.Combine(sourceRoot, folder), StringComparison.OrdinalIgnoreCase)) != 1)
            || libraries.PersonalLibraryPolicy.AllowBrowserUpload || libraries.PersonalLibraryPolicy.AllowConnectedDeviceImport
            || libraries.Libraries.Any(l => l.PrimaryDestinationSourceId is not null || l.AcceptedIntakeModes.Count != 0))
            throw new InvalidOperationException("Real-media mode requires exactly the five approved read-only catalogue sources, with uploads and alternate intake disabled.");
    }

    public static async Task PrepareAsync(string configDirectory, string sourceRoot, string outputDirectory, Guid? profileId)
    {
        using var dashboard = ProcessInstanceLease.TryAcquire(ProcessInstanceLease.DashboardLeaseName);
        if (!dashboard.IsAcquired) throw new InvalidOperationException("Stop the Dashboard before preparing real media.");
        configDirectory = Path.GetFullPath(configDirectory);
        sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        outputDirectory = Path.GetFullPath(outputDirectory);
        foreach (var folder in CatalogueFolders.Concat(ViewFolders))
            if (!Directory.Exists(Path.Combine(sourceRoot, folder))) throw new DirectoryNotFoundException(folder);
        var corePath = Path.Combine(configDirectory, "core.json");
        var core = JsonNode.Parse(File.ReadAllText(corePath))!.AsObject();
        var libraryRoot = Path.GetFullPath(core["library_root"]!.GetValue<string>());
        var database = Path.GetFullPath(TuvimaDataPathResolver.ResolveDatabasePath(configDirectory, null, null));
        foreach (var path in new[] { configDirectory, outputDirectory, libraryRoot, database }) RequireSeparate(sourceRoot, path);
        if (Directory.Exists(outputDirectory)) throw new InvalidOperationException("Use a new report directory for each preparation.");
        Directory.CreateDirectory(outputDirectory);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        var profiles = new List<Guid>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM profiles ORDER BY created_at";
            using var reader = command.ExecuteReader();
            while (reader.Read()) profiles.Add(GuidSql.FromDb(reader[0]));
        }
        if (profileId is null && profiles.Count != 1)
            throw new InvalidOperationException("Specify -ProfileId when more than one profile exists. No reset has occurred.");
        var selectedProfile = profileId ?? profiles.Single();
        if (!profiles.Contains(selectedProfile)) throw new InvalidOperationException("The selected profile does not exist.");

        using var observer = new FileSystemWatcher(sourceRoot) { IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes | NotifyFilters.Security,
            InternalBufferSize = 64 * 1024 };
        var changed = 0;
        observer.Changed += (_, _) => Interlocked.Exchange(ref changed, 1);
        observer.Created += (_, _) => Interlocked.Exchange(ref changed, 1);
        observer.Deleted += (_, _) => Interlocked.Exchange(ref changed, 1);
        observer.Renamed += (_, _) => Interlocked.Exchange(ref changed, 1);
        observer.Error += (_, _) => Interlocked.Exchange(ref changed, 1);
        observer.EnableRaisingEvents = true;
        var baseline = await SnapshotAsync(sourceRoot, true);
        Save(Path.Combine(outputDirectory, "source-baseline.json"), baseline);
        if (changed != 0) throw new IOException("Source activity during baseline; reset aborted.");
        Console.WriteLine($"Baseline: {baseline.Count(e => !e.IsDirectory)} files. Preparing source-preserving reset.");

        using (var backup = new SqliteConnection($"Data Source={Path.Combine(outputDirectory, "library-before.db")}"))
        {
            backup.Open();
            connection.BackupDatabase(backup);
        }
        foreach (var name in new[] { "libraries.json", "core.json", "writeback.json", SettingsFile })
            if (File.Exists(Path.Combine(configDirectory, name))) File.Copy(Path.Combine(configDirectory, name), Path.Combine(outputDirectory, name + ".before"));
        var run = new RealMediaRun(sourceRoot, outputDirectory, libraryRoot, database, selectedProfile, "Preparing", "", DateTimeOffset.UtcNow);
        Save(Path.Combine(configDirectory, SettingsFile), run);
        var libraries = JsonSerializer.Deserialize<LibrariesConfiguration>(File.ReadAllText(Path.Combine(configDirectory, "libraries.json")))!;
        libraries.Libraries = libraries.Libraries.Where(l => CatalogueFolders.Contains(l.Category?.ToLowerInvariant())).ToList();
        if (libraries.Libraries.Count != 5) throw new InvalidOperationException("Expected one configured library per real catalogue category.");
        foreach (var library in libraries.Libraries)
        {
            var folder = library.Category!.ToLowerInvariant();
            library.Sources = [new LibrarySourceConfig { Id = library.Sources.FirstOrDefault()?.Id ?? Guid.NewGuid().ToString(), Path = Path.Combine(sourceRoot, folder),
                ManagementMode = LibrarySourceManagementModes.ExistingLibrary, AccessMode = LibrarySourceAccessModes.ReadOnly,
                WritebackOverride = false, IncludeSubdirectories = true }];
            library.PrimaryDestinationSourceId = null;
            library.AcceptedIntakeModes = [];
            library.OrganizationPolicy = new() { Mode = LibraryOrganizationModes.KeepOriginalFolders, PreserveOriginals = true };
        }
        libraries.StorageLocations.RemoveAll(s => Contains(sourceRoot, s.Path) || Contains(s.Path, sourceRoot));
        libraries.StorageLocations.Add(new() { Id = "real-media", Label = "Real media (read only)", Path = sourceRoot, AllowWrite = false });
        libraries.PersonalLibraryPolicy.AllowBrowserUpload = false;
        libraries.PersonalLibraryPolicy.AllowDragAndDrop = false;
        libraries.PersonalLibraryPolicy.AllowConnectedDeviceImport = false;
        libraries.PersonalLibraryPolicy.AllowMobileBackup = false;
        libraries.PersonalLibraryPolicy.AllowExistingFolderAttachment = true;
        core["storage_policy"] = new JsonObject { ["mode"] = "Centralized", ["artwork_export"] = false,
            ["subtitle_export"] = false, ["metadata_sidecar_export"] = false, ["cleanup_managed_local_artwork"] = false,
            ["export_profile"] = new JsonObject { ["name"] = "real-media-read-only", ["artwork"] = false,
                ["preferred_subtitles"] = false, ["metadata_sidecars"] = false } };
        // A fresh derived-data namespace avoids deleting any mixed legacy folder or original.
        core["data_root"] = Path.Combine(libraryRoot, ".data", "real-media", Path.GetFileName(outputDirectory));
        ValidateSources(libraries, sourceRoot);
        var coreErrors = MediaEngine.Storage.Configuration.JsonConfigValidator.Validate(
            JsonSerializer.Deserialize<CoreConfiguration>(core.ToJsonString())!, "core.json");
        if (coreErrors.Count > 0) throw new InvalidOperationException(string.Join("; ", coreErrors));
        Save(Path.Combine(configDirectory, "libraries.json"), libraries);
        File.WriteAllText(corePath, core.ToJsonString(Json));
        ResetDatabase(connection);
        if (changed != 0) throw new IOException("Source activity during preparation. Workers remain blocked.");
        run = run with { Phase = "Ready", ConfigurationHash = ConfigHash(configDirectory) };
        Save(Path.Combine(configDirectory, SettingsFile), run);
        Save(Path.Combine(outputDirectory, "run.json"), run);
        Console.WriteLine("Library-state reset complete. Original files retained. Start the Engine to ingest only the real sources.");
    }

    public static void ResetDatabase(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=OFF";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        try
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM pragma_table_list WHERE schema='main' AND type IN ('table','virtual') AND name NOT LIKE 'sqlite_%'";
            var tables = new List<string>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
            foreach (var table in tables.Where(t => !DevHarnessResetService.PreserveForRealMediaReset(t)))
            {
                command.CommandText = $"DELETE FROM \"{table.Replace("\"", "\"\"")}\"";
                command.ExecuteNonQuery();
            }
            command.CommandText = "PRAGMA foreign_key_check";
            using (var reader = command.ExecuteReader())
                if (reader.Read()) throw new InvalidOperationException($"Reset would leave dangling references in {reader.GetString(0)}.");
            transaction.Commit();
        }
        finally
        {
            command.Transaction = null;
            command.CommandText = "PRAGMA foreign_keys=ON";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Continue monitoring after a documented health-probe defect, without restoring
    /// timestamps or changing any source. The immutable original baseline remains
    /// authoritative for final audits; this is not a successful preservation result.
    /// </summary>
    public static async Task RecordDirectoryProbeRecoveryAsync(string configDirectory)
    {
        using var dashboard = ProcessInstanceLease.TryAcquire(ProcessInstanceLease.DashboardLeaseName);
        if (!dashboard.IsAcquired) throw new InvalidOperationException("Stop the Dashboard before recording recovery.");
        var run = Load(configDirectory) ?? throw new InvalidOperationException("No protected run.");
        ValidateConfiguration(configDirectory, run);
        var original = JsonSerializer.Deserialize<List<RealMediaFile>>(await File.ReadAllTextAsync(Path.Combine(run.OutputDirectory, "source-baseline.json")))!;
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run.OutputDirectory, "protection-failure.json")));
        var probeFolders = evidence.RootElement.GetProperty("events").EnumerateArray()
            .Select(e => e.GetString()!).Where(e => e.StartsWith("Added: ", StringComparison.Ordinal))
            .Select(e => e[7..]).Where(p => Path.GetFileName(p).StartsWith(".tuvima_probe_", StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(run.SourceRoot, Path.GetDirectoryName(p)!)).ToHashSet(StringComparer.Ordinal);
        var actual = await SnapshotAsync(run.SourceRoot, true);
        var differences = Differences(original, actual, true);
        foreach (var path in differences)
        {
            var before = original.SingleOrDefault(e => e.Path == path);
            var after = actual.SingleOrDefault(e => e.Path == path);
            if (before is null || after is null || !before.IsDirectory || !probeFolders.Contains(path)
                || before with { LastWriteUtc = after.LastWriteUtc } != after)
                throw new IOException($"Recovery rejected: change is not an evidenced directory-probe timestamp: {path}");
        }
        Save(Path.Combine(run.OutputDirectory, "directory-probe-recovery.json"), new {
            recorded_at = DateTimeOffset.UtcNow, original_files_preserved = true,
            original_preservation_passed = false, directory_timestamp_changes = differences,
            explanation = "Health probes created/deleted temporary files. The original baseline and failure evidence are retained. No source timestamps were restored. Monitoring resumes against the current directory timestamps only." });
        Save(Path.Combine(run.OutputDirectory, "monitoring-baseline.json"), actual);
        Console.WriteLine($"Original file hashes, names, attributes and security unchanged. {differences.Length} directory timestamp changes recorded; original preservation violation remains in the report.");
    }

    private static void RejectHardLinks(FileStream stream)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            throw new IOException("Cannot verify source file link identity.");
        if (info.NumberOfLinks != 1) throw new IOException("Hard-linked originals require an explicit alias audit before this harness can run.");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out FileInformation info);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
}

public sealed record RealMediaRun(string SourceRoot, string OutputDirectory, string LibraryRoot, string DatabasePath,
    Guid ProfileId, string Phase, string ConfigurationHash, DateTimeOffset StartedAt);
public sealed record RealMediaFile(string Path, bool IsDirectory, long Length, DateTime CreationUtc,
    DateTime LastWriteUtc, int Attributes, string? Sha256, string? Security = null);
