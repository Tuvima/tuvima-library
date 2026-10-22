using System.Security.Cryptography;
using System.Text.Json;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using ContractLibraryPreferences = MediaEngine.Contracts.Settings.LibraryPreferencesSettings;
// Explicit alias (not a blanket `using MediaEngine.Contracts.Settings;`) because that
// namespace and MediaEngine.Domain.Configuration (imported above) both declare
// LibraryPreferencesSettings / MissingItemDisplayPolicy / LibraryLaneGroupDisplaySettings —
// a wildcard import would make every existing unqualified use of those names in this file
// ambiguous (CS0104).
using LibraryPreferencesDiagnosticsResponse = MediaEngine.Contracts.Settings.LibraryPreferencesDiagnosticsResponse;
using ResolvedUISettingsDto = MediaEngine.Contracts.Settings.ResolvedUISettingsDto;
using UIDeviceProfileDto = MediaEngine.Contracts.Settings.UIDeviceProfileDto;
using UIGlobalSettingsDto = MediaEngine.Contracts.Settings.UIGlobalSettingsDto;
using UIProfileSettingsDto = MediaEngine.Contracts.Settings.UIProfileSettingsDto;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// UI settings endpoints for the three-tier cascade (Global → Device → Profile).
/// All routes are grouped under <c>/settings/ui</c>.
///
/// <para>
/// Access:
///   Global (read/write) — Administrator only.
///   Device (read) — Authenticated human.
///   Device (write) — Administrator only.
///   Profile (read/write) — Active profile only.
///   Resolved — Authenticated human's active profile.
/// </para>
///
/// <list type="bullet">
///   <item><c>GET    /settings/ui/global</c>                — current global UI settings</item>
///   <item><c>PUT    /settings/ui/global</c>                — save global UI settings</item>
///   <item><c>GET    /settings/ui/device/{deviceClass}</c>  — device profile for class</item>
///   <item><c>PUT    /settings/ui/device/{deviceClass}</c>  — save device profile</item>
///   <item><c>GET    /settings/ui/profile/{profileId}</c>   — profile UI preferences</item>
///   <item><c>PUT    /settings/ui/profile/{profileId}</c>   — save profile UI preferences</item>
///   <item><c>GET    /settings/ui/resolved</c>              — fully cascaded output</item>
/// </list>
/// </summary>
public static class UISettingsEndpoints
{
    /// <summary>
    /// Known device classes. Requests for unrecognised classes are rejected with 400.
    /// </summary>
    private static readonly HashSet<string> ValidDeviceClasses =
        new(StringComparer.OrdinalIgnoreCase) { "web", "mobile", "television", "automotive" };

    public static IEndpointRouteBuilder MapUISettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/settings/ui").WithTags("UI Settings");

        // ── GET /settings/ui/global ──────────────────────────────────────────
        grp.MapGet("/global", (IConfigurationLoader configLoader) =>
        {
            var global = configLoader.LoadConfig<UIGlobalSettings>("ui", "global")
                         ?? new UIGlobalSettings();

            return Results.Ok(SettingsContractMapper.ToContract(global));
        })
        .WithName("GetUIGlobalSettings")
        .WithSummary("Returns the current global UI settings (theme, features, layout defaults).")
        .Produces<UIGlobalSettingsDto>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // ── PUT /settings/ui/global ──────────────────────────────────────────
        grp.MapPut("/global", (
            UIGlobalSettingsDto settings,
            IConfigurationLoader configLoader,
            UISettingsCacheRepository cache) =>
        {
            var storageSettings = SettingsContractMapper.ToStorage(settings);
            configLoader.SaveConfig("ui", "global", storageSettings);
            cache.Upsert("global", JsonSerializer.Serialize(storageSettings));

            return Results.Ok(SettingsContractMapper.ToContract(storageSettings));
        })
        .WithName("UpdateUIGlobalSettings")
        .WithSummary("Saves global UI settings to the configuration file and updates the cache.")
        .Produces<UIGlobalSettingsDto>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // ── GET /settings/ui/device/{deviceClass} ────────────────────────────
        grp.MapGet("/device/{deviceClass}", (
            string deviceClass,
            IConfigurationLoader configLoader) =>
        {
            if (!ValidDeviceClasses.Contains(deviceClass))
            {
                return ApiErrors.BadRequest($"Unknown device class '{deviceClass}'. Valid: web, mobile, television, automotive.");
            }

            var device = configLoader.LoadConfig<UIDeviceProfile>("ui/devices", deviceClass);

            if (device is null)
            {
                return ApiErrors.NotFound($"No device profile found for '{deviceClass}'.");
            }

            return Results.Ok(SettingsContractMapper.ToContract(device));
        })
        .WithName("GetUIDeviceProfile")
        .WithSummary("Returns the device profile and constraints for a specific device class.")
        .Produces<UIDeviceProfileDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireHumanSelfService();

        // ── PUT /settings/ui/device/{deviceClass} ────────────────────────────
        grp.MapPut("/device/{deviceClass}", (
            string deviceClass,
            UIDeviceProfileDto profile,
            IConfigurationLoader configLoader,
            UISettingsCacheRepository cache) =>
        {
            if (!ValidDeviceClasses.Contains(deviceClass))
            {
                return ApiErrors.BadRequest($"Unknown device class '{deviceClass}'. Valid: web, mobile, television, automotive.");
            }

            // Ensure the device_class field matches the route parameter.
            profile.DeviceClass = deviceClass;
            var storageProfile = SettingsContractMapper.ToStorage(profile);

            configLoader.SaveConfig("ui/devices", deviceClass, storageProfile);
            cache.Upsert($"device:{deviceClass}", JsonSerializer.Serialize(storageProfile));

            return Results.Ok(SettingsContractMapper.ToContract(storageProfile));
        })
        .WithName("UpdateUIDeviceProfile")
        .WithSummary("Saves a device profile to the configuration file and updates the cache.")
        .Produces<UIDeviceProfileDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireEffectiveAdministrator();

        // ── GET /settings/ui/profile/{profileId} ─────────────────────────────
        grp.MapGet("/profile/{profileId}", (
            string profileId,
            IConfigurationLoader configLoader) =>
        {
            profileId = Guid.Parse(profileId).ToString("D");
            var profile = configLoader.LoadConfig<UIProfileSettings>("ui/profiles", profileId);

            if (profile is null)
            {
                return ApiErrors.NotFound($"No UI profile found for '{profileId}'.");
            }

            return Results.Ok(SettingsContractMapper.ToContract(profile));
        })
        .WithName("GetUIProfileSettings")
        .AddEndpointFilter(new PersonalUiSettingsFilter())
        .WithSummary("Returns the UI preferences for a specific user profile.")
        .Produces<UIProfileSettingsDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireHumanSelfService();

        // ── PUT /settings/ui/profile/{profileId} ─────────────────────────────
        grp.MapPut("/profile/{profileId}", (
            string profileId,
            UIProfileSettingsDto settings,
            IConfigurationLoader configLoader,
            UISettingsCacheRepository cache) =>
        {
            // Ensure the profile_id matches the route parameter.
            profileId = Guid.Parse(profileId).ToString("D");
            settings.ProfileId = profileId;
            var storageSettings = SettingsContractMapper.ToStorage(settings);

            configLoader.SaveConfig("ui/profiles", profileId, storageSettings);
            cache.Upsert($"profile:{profileId}", JsonSerializer.Serialize(storageSettings));

            return Results.Ok(SettingsContractMapper.ToContract(storageSettings));
        })
        .WithName("UpdateUIProfileSettings")
        .AddEndpointFilter(new PersonalUiSettingsFilter())
        .WithSummary("Saves UI preferences for a user profile to the configuration file and updates the cache.")
        .Produces<UIProfileSettingsDto>(StatusCodes.Status200OK)
        .RequireHumanSelfService();

        // ── GET /settings/ui/resolved ────────────────────────────────────────
        grp.MapGet("/resolved", async (
            HttpContext httpContext,
            IRequestAuthorityResolver authorities,
            UISettingsCascadeResolver resolver) =>
        {
            var query = httpContext.Request.Query;
            var deviceClass = query["device"].FirstOrDefault() ?? "web";
            var authority = await authorities.ResolveAsync(httpContext, httpContext.RequestAborted);
            var profileId = authority.ActiveProfileId!.Value.ToString("D");

            if (!ValidDeviceClasses.Contains(deviceClass))
            {
                return ApiErrors.BadRequest($"Unknown device class '{deviceClass}'. Valid: web, mobile, television, automotive.");
            }

            var resolved = resolver.Resolve(deviceClass, profileId);

            return Results.Ok(SettingsContractMapper.ToContract(resolved));
        })
        .WithName("GetResolvedUISettings")
        .AddEndpointFilter(new PersonalUiSettingsFilter())
        .WithSummary("Returns the fully cascaded UI settings for a device class and optional profile.")
        .Produces<ResolvedUISettingsDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireHumanSelfService();

        // ── Library Preferences ──────────────────────────────────────────────
        grp.MapGet("/library-preferences", (IConfigurationLoader configLoader) =>
        {
            var prefs = configLoader.LoadLibraryPreferences()
                        ?? throw new InvalidOperationException("config/ui/library-preferences.json or its shipped defaults file is required.");
            return Results.Ok(SettingsContractMapper.ToContract(prefs));
        })
        .WithName("GetLibraryPreferences")
        .WithSummary("Returns the current per-media library display preferences.")
        .Produces<ContractLibraryPreferences>(StatusCodes.Status200OK)
        .RequireHumanSelfService();

        grp.MapGet("/library-preferences/diagnostics", (IConfigurationLoader configLoader) =>
        {
            // The file in effect: this computer's own, or the shipped defaults when it has none (LoadLibraryPreferences).
            var path = Path.Combine(configLoader.ConfigDirectoryPath, "ui", "library-preferences.json");
            if (!File.Exists(path))
            {
                path = Path.Combine(configLoader.ConfigDirectoryPath, "ui", "library-preferences.defaults.json");
            }

            if (!File.Exists(path))
            {
                return ApiErrors.NotFound("Neither config/ui/library-preferences.json nor its shipped defaults file was found.");
            }

            var bytes = File.ReadAllBytes(path);
            return Results.Ok(new LibraryPreferencesDiagnosticsResponse(
                source_path: path,
                sha256: Convert.ToHexStringLower(SHA256.HashData(bytes)),
                last_modified_at: File.GetLastWriteTimeUtc(path),
                settings: SettingsContractMapper.ToContract(
                    configLoader.LoadLibraryPreferences()
                    ?? throw new InvalidOperationException("config/ui/library-preferences.json or its shipped defaults file is required."))));
        })
        .WithName("GetLibraryPreferencesDiagnostics")
        .WithSummary("Returns the tracked source file, content hash, timestamp, and effective per-media library preferences.")
        .Produces<LibraryPreferencesDiagnosticsResponse>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        grp.MapPut("/library-preferences", (
            ContractLibraryPreferences settings,
            IConfigurationLoader configLoader,
            UISettingsCacheRepository cache) =>
        {
            var storageSettings = SettingsContractMapper.ToStorage(settings);
            configLoader.SaveConfig("ui", "library-preferences", storageSettings);
            cache.Upsert("library-preferences", JsonSerializer.Serialize(storageSettings));
            return Results.Ok(SettingsContractMapper.ToContract(storageSettings));
        })
        .WithName("UpdateLibraryPreferences")
        .WithSummary("Validates and atomically saves per-media library display preferences and refreshes the runtime cache.")
        .Produces<ContractLibraryPreferences>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        return app;
    }
}


