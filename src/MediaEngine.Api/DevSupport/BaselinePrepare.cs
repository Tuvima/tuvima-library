using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;

namespace MediaEngine.Api.DevSupport;

/// <summary>
/// Offline preparation of one isolated ingestion-baseline run. Works only inside the run's own config folder
/// and data folder; the sample root is only ever read. Layout: <c>&lt;run&gt;/config</c>, <c>&lt;run&gt;/data</c>.
/// </summary>
public static class BaselinePrepare
{
    public const string RunInfoFile = "run-info.json";

    /// <summary>Sample folder to library category. A library is created only for folders that exist.</summary>
    public static readonly (string Folder, string Category)[] CatalogueFolders =
    [
        ("books", "Books"),
        ("audiobooks", "Audiobooks"),
        ("movies", "Movies"),
        ("tv", "TV"),
        ("music", "Music"),
        ("comics", "Comics"),
        ("videos", "Videos"),
    ];

    public static readonly string[] ViewFolders = ["personal_photos", "personal_videos"];

    /// <summary>Written by <c>--run-baseline</c> into its run folder; prepare refuses any folder without it.</summary>
    public const string RunMarkerFile = ".baseline-run";

    public static string DataDirectoryFor(string configDirectory) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configDirectory))!, "data");

    public static string RunInfoPathFor(string configDirectory) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configDirectory))!, RunInfoFile);

    /// <summary>Offline command: <c>--prepare-baseline &lt;config-dir&gt; &lt;sample-root&gt;</c>.</summary>
    public static async Task RunAsync(string configDirectory, string sampleRoot)
    {
        configDirectory = Path.GetFullPath(configDirectory);
        sampleRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sampleRoot));
        var dataDirectory = DataDirectoryFor(configDirectory);
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(configDirectory)!, RunMarkerFile)))
        {
            throw new InvalidOperationException(
                $"'{configDirectory}' is not inside a baseline run folder (no {RunMarkerFile} beside it). Prepare only rewrites a copied config made by --run-baseline, never a real config folder.");
        }

        if (!File.Exists(Path.Combine(configDirectory, "core.json")) || !File.Exists(Path.Combine(configDirectory, "libraries.json")))
        {
            throw new FileNotFoundException("The baseline config folder must be a copy of the Engine config folder (core.json and libraries.json are required).", configDirectory);
        }

        if (!Directory.Exists(sampleRoot))
        {
            throw new DirectoryNotFoundException($"Sample root '{sampleRoot}' does not exist.");
        }

        // Config tokens resolve against this folder, never the workstation's real data folder.
        Environment.SetEnvironmentVariable("TUVIMA_DATA_DIR", dataDirectory);
        foreach (var path in new[] { configDirectory, dataDirectory })
        {
            RealMediaHarness.RequireSeparate(sampleRoot, path);
        }

        var catalogue = CatalogueFolders.Where(entry => Directory.Exists(Path.Combine(sampleRoot, entry.Folder))).ToArray();
        if (catalogue.Length == 0)
        {
            throw new InvalidOperationException($"Sample root '{sampleRoot}' has none of the folders {string.Join(", ", CatalogueFolders.Select(e => e.Folder))}.");
        }

        var libraryRoot = Path.Combine(dataDirectory, "library");
        Directory.CreateDirectory(libraryRoot);

        // -- core.json: absolute data location, nothing written next to the samples.
        var corePath = Path.Combine(configDirectory, "core.json");
        var core = JsonNode.Parse(TuvimaDataPaths.ExpandConfigTokens(File.ReadAllText(corePath)))!.AsObject();
        core["library_root"] = libraryRoot;
        core["data_root"] = "";
        core["storage_policy"] = new JsonObject
        {
            ["mode"] = "Centralized",
            ["artwork_export"] = false,
            ["subtitle_export"] = false,
            ["metadata_sidecar_export"] = false,
            ["cleanup_managed_local_artwork"] = false,
            ["export_profile"] = new JsonObject
            {
                ["name"] = "baseline-read-only",
                ["artwork"] = false,
                ["preferred_subtitles"] = false,
                ["metadata_sidecars"] = false,
            },
        };
        var coreErrors = MediaEngine.Storage.Configuration.JsonConfigValidator.Validate(
            JsonSerializer.Deserialize<CoreConfiguration>(core.ToJsonString())!, "core.json");
        if (coreErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", coreErrors));
        }

        // -- libraries.json: one read-only existing-library source per catalogue sample folder.
        var librariesPath = Path.Combine(configDirectory, "libraries.json");
        var libraries = JsonSerializer.Deserialize<LibrariesConfiguration>(TuvimaDataPaths.ExpandConfigTokens(File.ReadAllText(librariesPath)))!;
        var kept = new List<LibraryFolderConfig>();
        foreach (var (folder, category) in catalogue)
        {
            var library = libraries.Libraries.FirstOrDefault(l => string.Equals(l.Category, category, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"The copied libraries.json has no {category} library.");
            library.Sources =
            [
                new LibrarySourceConfig
                {
                    Id = library.Sources.FirstOrDefault()?.Id ?? Guid.NewGuid().ToString(),
                    Path = Path.Combine(sampleRoot, folder),
                    ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
                    AccessMode = LibrarySourceAccessModes.ReadOnly,
                    IncludeSubdirectories = true,
                },
            ];
            library.PrimaryDestinationSourceId = null;
            library.AcceptedIntakeModes = [];
            library.OrganizationPolicy = new() { Mode = LibraryOrganizationModes.KeepOriginalFolders, WriteTags = false };
            kept.Add(library);
        }

        libraries.Libraries = kept;
        libraries.StorageLocations.RemoveAll(s => RealMediaHarness.Contains(sampleRoot, s.Path) || RealMediaHarness.Contains(s.Path, sampleRoot)
            || string.Equals(s.Id, "media", StringComparison.OrdinalIgnoreCase));
        libraries.StorageLocations.Insert(0, new() { Id = "media", Label = "Baseline data", Path = libraryRoot, AllowWrite = true });
        libraries.StorageLocations.Add(new() { Id = "baseline-samples", Label = "Baseline samples (read only)", Path = sampleRoot, AllowWrite = false });
        libraries.PersonalLibraryPolicy.AllowBrowserUpload = false;
        libraries.PersonalLibraryPolicy.AllowDragAndDrop = false;
        libraries.PersonalLibraryPolicy.AllowConnectedDeviceImport = false;
        libraries.PersonalLibraryPolicy.AllowMobileBackup = false;
        libraries.PersonalLibraryPolicy.AllowExistingFolderAttachment = true;
        var libraryErrors = MediaEngine.Storage.Configuration.JsonConfigValidator.Validate(libraries, "libraries.json");
        if (libraryErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", libraryErrors));
        }

        RealMediaHarness.Save(librariesPath, libraries);
        File.WriteAllText(corePath, core.ToJsonString(RealMediaHarness.Json));
        HoldClockSchedules(configDirectory);

        // -- Empty data store, first-run setup already accepted, View folders linked to the owner profile.
        var databasePath = Path.GetFullPath(TuvimaDataPathResolver.ResolveDatabasePath(configDirectory, null, null));
        if (File.Exists(databasePath))
        {
            throw new InvalidOperationException($"A data store already exists at '{databasePath}'; baseline runs start from an empty one.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using var database = new DatabaseConnection(databasePath, new DataStoreStartupOptions { BackupDirectory = Path.Combine(configDirectory, "backups") });
        database.InitializeSchema();
        database.RunStartupChecks();
        var ownerProfile = Guid.Parse("00000000-0000-0000-0000-000000000001");
        AcceptFirstRunSetup(database, ownerProfile);

        var linkedView = new List<string>();
        var viewFolders = ViewFolders.Where(folder => Directory.Exists(Path.Combine(sampleRoot, folder))).ToArray();
        if (viewFolders.Length > 0)
        {
            var storage = new ViewStorageService(
                new ConfigurationDirectoryLoader(configDirectory),
                new ViewPersonalSpaceRepository(database),
                new ViewSharedLibraryRepository(database));
            var space = await storage.EnsurePersonalSpaceAsync(ownerProfile).ConfigureAwait(false);
            foreach (var folder in viewFolders)
            {
                var source = await storage.AddLinkedSourceAsync(space, folder, Path.Combine(sampleRoot, folder), true).ConfigureAwait(false);
                if (!source.IncludeInTimeline)
                {
                    await storage.UpdateSourceAsync(space, source with { IncludeInTimeline = true }).ConfigureAwait(false);
                }

                linkedView.Add(folder);
            }
        }

        var runInfo = new JsonObject
        {
            ["sample_root"] = sampleRoot,
            ["catalogue_folders"] = new JsonArray(catalogue.Select(entry => (JsonNode?)entry.Folder).ToArray()),
            ["view_folders"] = new JsonArray(linkedView.Select(folder => (JsonNode?)folder).ToArray()),
            ["prepared_at"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        File.WriteAllText(RunInfoPathFor(configDirectory), runInfo.ToJsonString(RealMediaHarness.Json));
        Console.WriteLine($"Baseline prepared: {catalogue.Length} catalogue libraries, {linkedView.Count} View folders, data store {databasePath}.");
    }

    private static void AcceptFirstRunSetup(DatabaseConnection database, Guid administratorProfileId)
    {
        using var connection = database.CreateConnection();
        var now = DateTimeOffset.UtcNow.ToString("O");
        connection.Execute("""
            UPDATE onboarding_steps SET status = 'passed', detail = 'Ingestion baseline run.',
                completed_at = @now, updated_at = @now WHERE workflow_version = 1;
            UPDATE onboarding_workflows SET state = 'complete', current_step = 'readiness',
                administrator_profile_id = @administrator, completed_at = @now, updated_at = @now,
                revision = revision + 1 WHERE workflow_version = 1;
            """, new { now, administrator = GuidSql.ToBlob(administratorProfileId) });
    }

    /// <summary>How far after the run starts the held schedules next fire: well past any run, and inside the longest
    /// wait a scheduled task can sleep for (Task.Delay tops out at about 49.7 days; a later time stops the Engine).</summary>
    public static readonly TimeSpan HeldScheduleOffset = TimeSpan.FromDays(30);

    /// <summary>A one-off cron time <see cref="HeldScheduleOffset"/> after <paramref name="now"/>, in local time.</summary>
    public static string HeldScheduleFrom(DateTimeOffset now)
    {
        var at = now.ToLocalTime().Add(HeldScheduleOffset);
        return $"{at.Minute} {at.Hour} {at.Day} {at.Month} *";
    }

    /// <summary>The daily Local AI schedules in <c>ai.json</c> (<c>scheduling</c>) that fire at night. The every-15-minutes
    /// description schedule does not depend on the time of day, so it stays as it is.</summary>
    private static readonly string[] HeldAiSchedules = ["series_check_cron", "vibe_batch_cron"];

    /// <summary>
    /// Holds the clock-scheduled tasks for the run (in the copied <c>maintenance.json</c> and <c>ai.json</c> only): every
    /// schedule moves to one time 30 days after the run starts (<see cref="HeldScheduleFrom"/>) and the off-hours retag and storage sweeps are switched off, so a
    /// run that crosses the night (02:00 to 06:00) gives the same result as one that does not. The series-order and
    /// vibe-tag services still make one pass a few minutes after the Engine starts; that pass does not depend on the time of day.
    /// </summary>
    public static void HoldClockSchedules(string configDirectory, DateTimeOffset? now = null)
    {
        var held = HeldScheduleFrom(now ?? DateTimeOffset.Now);
        HoldMaintenanceSchedules(configDirectory, held);
        HoldAiSchedules(configDirectory, held);
    }

    private static void HoldAiSchedules(string configDirectory, string held)
    {
        var path = Path.Combine(configDirectory, "ai.json");
        if (!File.Exists(path))
        {
            return;
        }

        // A missing section or key means the Engine falls back to its built-in 03:00 and 04:00 defaults, so the
        // held times are always written, and a new section carries the description schedule's default so it still validates.
        var ai = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (ai["scheduling"] is not JsonObject scheduling)
        {
            scheduling = new JsonObject { ["description_intelligence_cron"] = "*/15 * * * *" };
            ai["scheduling"] = scheduling;
        }

        foreach (var key in HeldAiSchedules)
        {
            scheduling[key] = held;
        }

        File.WriteAllText(path, ai.ToJsonString(RealMediaHarness.Json));
    }

    private static void HoldMaintenanceSchedules(string configDirectory, string held)
    {
        var path = Path.Combine(configDirectory, "maintenance.json");
        if (!File.Exists(path))
        {
            return;
        }

        var maintenance = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (maintenance["schedules"] is JsonObject schedules)
        {
            foreach (var key in schedules.Select(pair => pair.Key).ToList())
            {
                schedules[key] = held;
            }
        }

        foreach (var sweep in new[] { "retag_sweep", "storage_maintenance" })
        {
            if (maintenance[sweep] is JsonObject section)
            {
                section["enabled"] = false;
            }
        }

        File.WriteAllText(path, maintenance.ToJsonString(RealMediaHarness.Json));
    }
}
