using MediaEngine.Api.Http;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity.Contracts;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Providers;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage.Configuration;
using MediaEngine.Storage.Contracts;
using AuthSettingsDto = MediaEngine.Contracts.Settings.AuthSettingsDto;
using ContractPipelineConfiguration = MediaEngine.Contracts.Settings.PipelineConfiguration;
using ContractTranscodingSettings = MediaEngine.Contracts.Settings.TranscodingSettings;
using FieldMappingResponse = MediaEngine.Contracts.Settings.FieldMappingDto;
using HydrationSettingsDto = MediaEngine.Contracts.Settings.HydrationSettingsDto;
using LibrariesConfigurationSettingsDto = MediaEngine.Contracts.Settings.LibrariesConfigurationDto;
using MediaTypeConfigurationDto = MediaEngine.Contracts.Settings.MediaTypeConfigurationDto;
using MediaTypeDefinitionDto = MediaEngine.Contracts.Settings.MediaTypeDefinitionDto;
using OrganizationTemplateResponse = MediaEngine.Contracts.Settings.OrganizationTemplateDto;
using ProviderConfigUpdateRequest = MediaEngine.Contracts.Settings.ProviderConfigUpdateDto;
using ProviderCredentialOperationResultDto = MediaEngine.Contracts.Settings.ProviderCredentialOperationResultDto;
using ProviderCredentialWriteRequest = MediaEngine.Contracts.Settings.ProviderCredentialWriteRequest;
using ProviderHealthRecord = MediaEngine.Domain.Entities.ProviderHealthRecord;
// Explicit aliases (not a blanket `using MediaEngine.Contracts.Settings;`) because that
// namespace and MediaEngine.Domain.Configuration (imported above) both declare
// TranscodingSettings / PipelineConfiguration / LibraryPreferencesSettings — a wildcard
// import would make every unqualified use of those pre-existing names ambiguous (CS0104).
using ProviderHealthStatusResponse = MediaEngine.Contracts.Settings.ProviderHealthStatusResponse;
using ProviderIconPathResponse = MediaEngine.Contracts.Settings.ProviderIconPathResponse;
using ProviderSampleClaim = MediaEngine.Contracts.Settings.ProviderSampleClaimDto;
using ProviderSampleRequest = MediaEngine.Contracts.Settings.ProviderSampleRequest;
using ProviderSampleResponse = MediaEngine.Contracts.Settings.ProviderSampleResultDto;
using ProviderStatusResponse = MediaEngine.Contracts.Settings.ProviderStatusDto;
using ProviderTestResponse = MediaEngine.Contracts.Settings.ProviderTestResultDto;
using ServerGeneralRequest = MediaEngine.Contracts.Settings.ServerGeneralSettingsDto;
using ServerGeneralResponse = MediaEngine.Contracts.Settings.ServerGeneralSettingsDto;
using SettingsCatalogEntryResponse = MediaEngine.Contracts.Settings.SettingsCatalogEntryResponse;
using SettingsSavedResponse = MediaEngine.Contracts.Settings.SettingsSavedResponse;
using TestPathRequest = MediaEngine.Contracts.Settings.TestPathRequest;
using TestPathResponse = MediaEngine.Contracts.Settings.PathTestResultDto;
using UpdateAuthSettingsRequest = MediaEngine.Contracts.Settings.UpdateAuthSettingsRequest;
using UpdateExternalAuthProviderRequest = MediaEngine.Contracts.Settings.UpdateExternalAuthProviderRequest;
using UpdateLibrariesRequest = MediaEngine.Contracts.Settings.UpdateLibrariesRequest;
using UpdateOrganizationTemplateRequest = MediaEngine.Contracts.Settings.UpdateOrganizationTemplateRequest;
using UpdateProviderRequest = MediaEngine.Contracts.Settings.UpdateProviderRequest;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Settings endpoints for library/source configuration, path testing, and provider status.
/// All routes are grouped under <c>/settings</c>.
///
/// Access:
///   Libraries, test-path, organization-template — Administrator only.
///   Providers (read) — Administrator or Curator.
///   Providers (write) — Administrator only.
///
/// <list type="bullet">
///   <item><c>GET/PUT /settings/libraries</c> — complete library and approved server-root configuration</item>
///   <item><c>POST   /settings/test-path</c> — probe a path for existence / read / write access</item>
///   <item><c>GET    /settings/providers</c> — enabled state + async reachability for each provider</item>
/// </list>
/// </summary>
public static class SettingsEndpoints
{
    // Fallback display names when a provider config file has no display_name field.
    // In practice all provider configs have display_name set explicitly; this map
    // is kept as a safety net and is derived from the same data in the config files.
    private static readonly IReadOnlyDictionary<string, string> _displayNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["apple_api"] = "Apple API",
            // audnexus removed - config file deleted as part of SPARQL cleanup
            ["wikidata"] = "Wikidata",
            ["wikidata_reconciliation"] = "Wikidata",
            ["local_filesystem"] = "Local Filesystem",
            ["tmdb"] = "TMDB",
            ["musicbrainz"] = "MusicBrainz",
        };

    // Maps provider name → key in manifest.ProviderEndpoints for the reachability probe.
    private static readonly IReadOnlyDictionary<string, string> _endpointKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["apple_api"] = "apple_api",
            // audnexus removed - config file deleted as part of SPARQL cleanup
            ["wikidata"] = "wikidata_api",
            ["tmdb"] = "tmdb",
            ["musicbrainz"] = "musicbrainz",
        };

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/settings").WithTags("Settings");
        grp.MapLibraryMutations();

        grp.MapGet("/security/auth", (AuthenticationProviderConfigurationService providerConfiguration, IConfigurationLoader configLoader) =>
        {
            var auth = providerConfiguration.LoadWithSecrets();
            return Results.Ok(ToAuthSettingsDto(auth, configLoader.LoadNetwork(), restartRequired: false));
        })
        .WithName("GetAuthSettings")
        .WithSummary("Returns user sign-in and external provider configuration without secrets.")
        .Produces<AuthSettingsDto>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        grp.MapPut("/security/auth", async (UpdateAuthSettingsRequest request,
            IConfigurationLoader configLoader,
            IUsableAdministratorService usableAdministrators,
            AuthenticationProviderConfigurationService providerConfiguration,
            AuthenticationPolicyMutationGate mutationGate,
            CancellationToken ct) =>
        {
            using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
            if (request.Mode is not ("Local" or "DisabledLocalOnly" or "Optional" or "Required"))
            {
                return ApiErrors.BadRequest("Authentication mode is invalid.");
            }

            if (request.InvitationLifetimeHours is < 1 or > 720)
            {
                return ApiErrors.BadRequest("Invitation lifetime must be between 1 and 720 hours.");
            }

            if (request.SessionLifetimeHours is < 1 or > 8760)
            {
                return ApiErrors.BadRequest("Session lifetime must be between 1 and 8760 hours.");
            }

            if (request.MaximumActiveSessions is < 1 or > 100)
            {
                return ApiErrors.BadRequest("Maximum active sessions must be between 1 and 100.");
            }

            if (request.TrustedLocalNetworks.Count > 32 || request.TrustedLocalNetworks.Any(value =>
                    !System.Net.IPNetwork.TryParse(value, out _)))
            {
                return ApiErrors.BadRequest("Trusted local networks must contain at most 32 valid CIDR ranges.");
            }

            if (!request.PasswordSignInEnabled && !request.PasskeySignInEnabled &&
                !request.ExternalSignInEnabled && !request.AllowLocalOnlyAccounts)
            {
                return ApiErrors.BadRequest("At least one account sign-in method must remain enabled.");
            }

            var core = configLoader.LoadCore();
            var auth = core.Auth;
            auth.Mode = request.Mode;
            auth.LocalhostBypass = request.LocalhostBypass;
            auth.PasswordSignInEnabled = request.PasswordSignInEnabled;
            auth.PasskeySignInEnabled = request.PasskeySignInEnabled;
            auth.ExternalSignInEnabled = request.ExternalSignInEnabled;
            auth.AllowLocalOnlyAccounts = request.AllowLocalOnlyAccounts;
            auth.TrustedLocalNetworks = request.TrustedLocalNetworks
                .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            auth.InvitationLifetimeHours = request.InvitationLifetimeHours;
            auth.SessionLifetimeHours = request.SessionLifetimeHours;
            auth.MaximumActiveSessions = request.MaximumActiveSessions;
            var prospective = providerConfiguration.LoadWithSecrets();
            prospective.Mode = auth.Mode;
            prospective.LocalhostBypass = auth.LocalhostBypass;
            prospective.PasswordSignInEnabled = auth.PasswordSignInEnabled;
            prospective.PasskeySignInEnabled = auth.PasskeySignInEnabled;
            prospective.ExternalSignInEnabled = auth.ExternalSignInEnabled;
            prospective.AllowLocalOnlyAccounts = auth.AllowLocalOnlyAccounts;
            prospective.TrustedLocalNetworks = auth.TrustedLocalNetworks;
            prospective.InvitationLifetimeHours = auth.InvitationLifetimeHours;
            prospective.SessionLifetimeHours = auth.SessionLifetimeHours;
            prospective.MaximumActiveSessions = auth.MaximumActiveSessions;
            if (!await usableAdministrators.HasUsableAdministratorSignInAsync(prospective, ct).ConfigureAwait(false))
            {
                return ApiErrors.Conflict("The policy must leave at least one enabled administrator with a usable sign-in method.");
            }

            configLoader.SaveCore(core);
            return Results.Ok(ToAuthSettingsDto(prospective, configLoader.LoadNetwork(), restartRequired: true));
        })
        .WithName("UpdateAuthSettings")
        .Produces<AuthSettingsDto>()
        .RequireEffectiveAdministrator();

        grp.MapPut("/security/auth/providers/{providerId}", async (
            string providerId,
            UpdateExternalAuthProviderRequest request,
            IConfigurationLoader configLoader,
            AuthenticationProviderConfigurationService providerConfiguration,
            AuthenticationPolicyMutationGate mutationGate,
            IUsableAdministratorService usableAdministrators,
            CancellationToken ct) =>
        {
            var error = ValidateExternalProvider(providerId, request);
            if (error is not null)
            {
                return ApiErrors.BadRequest(error);
            }

            using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
            var core = configLoader.LoadCore();
            var authWithSecrets = providerConfiguration.LoadWithSecrets();
            var provider = ToExternalProvider(providerId, request);
            var previous = authWithSecrets.ExternalProviders.FirstOrDefault(candidate =>
                candidate.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            provider.ClientSecret = request.ClearClientSecret
                ? string.Empty
                : request.ClientSecret?.Trim() ?? previous?.ClientSecret ?? string.Empty;
            authWithSecrets.ExternalProviders.RemoveAll(candidate =>
                candidate.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            authWithSecrets.ExternalProviders.Add(provider);
            if (!await usableAdministrators.HasUsableAdministratorSignInAsync(authWithSecrets, ct).ConfigureAwait(false))
            {
                return ApiErrors.Conflict("The provider change would remove the last usable administrator sign-in method.");
            }

            core.Auth.ExternalProviders.RemoveAll(candidate =>
                candidate.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            core.Auth.ExternalProviders.Add(provider);
            configLoader.SaveCore(core);
            await providerConfiguration.UpdateSecretAsync(
                providerId, request.ClientSecret, request.ClearClientSecret, ct).ConfigureAwait(false);
            return Results.Ok(ToAuthSettingsDto(providerConfiguration.LoadWithSecrets(), configLoader.LoadNetwork(), restartRequired: true));
        }).WithName("UpdateExternalAuthProvider")
          .Produces<AuthSettingsDto>()
          .RequireEffectiveAdministrator();

        grp.MapDelete("/security/auth/providers/{providerId}", async (
            string providerId,
            IConfigurationLoader configLoader,
            AuthenticationProviderConfigurationService providerConfiguration,
            AuthenticationPolicyMutationGate mutationGate,
            IUsableAdministratorService usableAdministrators,
            CancellationToken ct) =>
        {
            using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
            var core = configLoader.LoadCore();
            var authWithSecrets = providerConfiguration.LoadWithSecrets();
            var removed = authWithSecrets.ExternalProviders.RemoveAll(candidate =>
                candidate.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return ApiErrors.NotFound("Authentication provider not found.");
            }

            if (!await usableAdministrators.HasUsableAdministratorSignInAsync(authWithSecrets, ct).ConfigureAwait(false))
            {
                return ApiErrors.Conflict("The provider change would remove the last usable administrator sign-in method.");
            }

            core.Auth.ExternalProviders.RemoveAll(candidate =>
                candidate.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            configLoader.SaveCore(core);
            await providerConfiguration.RemoveSecretAsync(providerId, ct).ConfigureAwait(false);
            return Results.NoContent();
        }).WithName("DeleteExternalAuthProvider")
          .Produces(StatusCodes.Status204NoContent)
          .RequireEffectiveAdministrator();

        grp.MapGet("/transcoding", (IConfigurationLoader configLoader) =>
        {
            return Results.Ok(SettingsContractMapper.ToContract(configLoader.LoadTranscoding()));
        })
        .WithName("GetTranscodingSettings")
        .WithSummary("Returns playback encode, FFmpeg, and offline variant policy.")
        .Produces<ContractTranscodingSettings>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        grp.MapPut("/transcoding", (
            ContractTranscodingSettings request,
            IConfigurationLoader configLoader) =>
        {
            var settings = SettingsContractMapper.ToStorage(request);
            settings.MaxConcurrentTranscodes = Math.Clamp(settings.MaxConcurrentTranscodes, 1, 8);
            settings.ShadowStorageLimitGb = Math.Clamp(settings.ShadowStorageLimitGb, 1, 10_000);
            settings.VariantRetentionDays = Math.Clamp(settings.VariantRetentionDays, 1, 3650);
            settings.HardwareAcceleration = string.IsNullOrWhiteSpace(settings.HardwareAcceleration)
                ? "auto"
                : settings.HardwareAcceleration.Trim().ToLowerInvariant();
            settings.MaintenanceWindow = string.IsNullOrWhiteSpace(settings.MaintenanceWindow)
                ? "01:00-05:00"
                : settings.MaintenanceWindow.Trim();
            settings.VariantCachePath = string.IsNullOrWhiteSpace(settings.VariantCachePath)
                ? ".data/variants"
                : settings.VariantCachePath.Trim();
            settings.DefaultMobileProfile = string.IsNullOrWhiteSpace(settings.DefaultMobileProfile)
                ? settings.QualityProfiles.FirstOrDefault()?.Name ?? "mobile-small"
                : settings.DefaultMobileProfile.Trim();
            settings.AdaptiveHls.ProfileName = string.IsNullOrWhiteSpace(settings.AdaptiveHls.ProfileName)
                ? "tv-adaptive-hls"
                : settings.AdaptiveHls.ProfileName.Trim().ToLowerInvariant();
            settings.AdaptiveHls.SegmentSeconds = Math.Clamp(settings.AdaptiveHls.SegmentSeconds, 2, 12);
            settings.AdaptiveHls.AccessLifetimeMinutes = Math.Clamp(settings.AdaptiveHls.AccessLifetimeMinutes, 15, 720);
            settings.AdaptiveHls.PreparationWaitSeconds = Math.Clamp(settings.AdaptiveHls.PreparationWaitSeconds, 1, 60);
            settings.AdaptiveHls.CleanupIntervalMinutes = Math.Clamp(settings.AdaptiveHls.CleanupIntervalMinutes, 1, 1440);
            settings.AdaptiveHls.Renditions = settings.AdaptiveHls.Renditions
                .Where(rendition => rendition.Height is >= 144 and <= 4320
                    && rendition.VideoBitrateKbps is >= 100 and <= 100_000)
                .OrderByDescending(rendition => rendition.Height)
                .GroupBy(rendition => rendition.Height)
                .Select(group => group.First())
                .Take(8)
                .ToList();
            foreach (var rendition in settings.AdaptiveHls.Renditions)
            {
                rendition.Name = string.IsNullOrWhiteSpace(rendition.Name) ? $"{rendition.Height}p" : rendition.Name.Trim();
                rendition.MaxRateKbps = Math.Max(rendition.VideoBitrateKbps, rendition.MaxRateKbps);
                rendition.BufferSizeKbps = Math.Max(rendition.MaxRateKbps, rendition.BufferSizeKbps);
            }

            configLoader.SaveTranscoding(settings);
            return Results.Ok(SettingsContractMapper.ToContract(settings));
        })
        .WithName("UpdateTranscodingSettings")
        .WithSummary("Saves playback encode, FFmpeg, and offline variant policy.")
        .Produces<ContractTranscodingSettings>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // ── GET/PUT /settings/libraries ───────────────────────────────────────
        grp.MapGet("/libraries", (IConfigurationLoader configLoader) =>
        {
            return Results.Ok(SettingsContractMapper.ToContract(configLoader.LoadLibraries()));
        })
        .WithName("GetLibraries")
        .WithSummary("Returns schema 6 catalogued libraries, the single View root, and approved storage.")
        .Produces<LibrariesConfigurationSettingsDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.StorageStatusRead);

        grp.MapPut("/libraries", async (UpdateLibrariesRequest request, IConfigurationLoader configLoader, MediaEngine.Api.Services.LocalAssets.ViewStorageService viewStorage, IProfileRepository profiles, IViewProfileRepository viewProfiles, MediaEngine.Api.Services.Settings.ServerFolderBrowserService folders, MediaEngine.Ingestion.Contracts.IFileOrganizer organizer, CancellationToken ct) =>
        {
            await LibraryMutationEndpoints.WriteGate.WaitAsync(ct);
            try
            {
                var config = SettingsContractMapper.ToStorage(request);
                var viewError = ValidateViewStorage(config) ?? ValidateViewRootChange(configLoader.LoadLibraries(), config);
                if (viewError is not null)
                {
                    return ApiErrors.BadRequest(viewError);
                }
                var pathError = ValidateConfiguredPaths(config);
                if (pathError is not null)
                {
                    return ApiErrors.BadRequest(pathError);
                }

                var validationErrors = JsonConfigValidator.Validate(config, "libraries.json");
                if (validationErrors.Count > 0)
                {
                    return ApiErrors.BadRequest(string.Join(" ", validationErrors));
                }

                var sourceError = ValidateLibrarySources(config, folders, organizer);
                if (sourceError is not null)
                {
                    return ApiErrors.BadRequest(sourceError);
                }

                configLoader.SaveLibraries(config);
                foreach (var profile in await profiles.GetAllAsync(ct))
                {
                    if ((await viewProfiles.GetPolicyAsync(profile.Id, ct)).ViewEnabled)
                    {
                        await viewStorage.EnsurePersonalSpaceAsync(profile.Id, ct);
                    }
                }

                return Results.Ok(SettingsContractMapper.ToContract(config));
            }
            finally { LibraryMutationEndpoints.WriteGate.Release(); }
        })
        .WithName("UpdateLibraries")
        .WithSummary("Replaces schema 6 catalogued libraries, the single View root, and approved storage.")
        .Produces<LibrariesConfigurationSettingsDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.StorageConfigWrite);

        grp.MapGet("/writeback/artwork", (IConfigurationLoader configLoader) =>
        {
            var settings = configLoader.LoadConfig<WriteBackConfiguration>(string.Empty, "writeback")
                ?? new WriteBackConfiguration();
            return Results.Ok(new ArtworkWritebackSettingsDto(settings.Enabled, settings.ArtworkEnabled));
        })
        .WithName("GetArtworkWritebackSettings")
        .Produces<ArtworkWritebackSettingsDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.StorageStatusRead);

        grp.MapPut("/writeback/artwork", (UpdateArtworkWritebackSettingsDto request,
            IConfigurationLoader configLoader) =>
        {
            var settings = configLoader.LoadConfig<WriteBackConfiguration>(string.Empty, "writeback")
                ?? new WriteBackConfiguration();
            settings.ArtworkEnabled = request.ArtworkEnabled;
            configLoader.SaveConfig(string.Empty, "writeback", settings);
            return Results.Ok(new ArtworkWritebackSettingsDto(settings.Enabled, settings.ArtworkEnabled));
        })
        .WithName("UpdateArtworkWritebackSettings")
        .Produces<ArtworkWritebackSettingsDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.StorageConfigWrite);

        // ── POST /settings/test-path ────────────────────────────────────────────

        grp.MapPost("/test-path", (TestPathRequest request, MediaEngine.Ingestion.Contracts.ILibraryFolderResolver resolver) =>
        {
            var path = request.Path ?? string.Empty;

            // Path traversal validation.
            var pathError = PathValidator.Validate(path);
            if (pathError is not null)
            {
                return ApiErrors.BadRequest(pathError);
            }

            var exists = Directory.Exists(path);
            bool hasRead = false;
            bool hasWrite = false;

            if (exists)
            {
                // Read probe: attempt to enumerate at least one entry.
                try
                {
                    // ReSharper disable once ReturnValueOfPureMethodIsNotUsed — intentional probe.
                    Directory.EnumerateFileSystemEntries(path).Any();
                    hasRead = true;
                }
                catch { /* access denied or I/O error */ }

                // Write probe: create and immediately delete a sentinel file.
                var sourcePolicy = resolver.ResolveSourceForPath(path);
                if (sourcePolicy?.Source.AllowsFileMutation == true)
                {
                    try
                    {
                        var probe = Path.Combine(path, $".tuvima_probe_{Guid.NewGuid():N}");
                        File.WriteAllText(probe, string.Empty);
                        File.Delete(probe);
                        hasWrite = true;
                    }
                    catch { /* read-only file system or access denied */ }
                }
            }

            return Results.Ok(new TestPathResponse
            {
                Path = path,
                Exists = exists,
                HasRead = hasRead,
                HasWrite = hasWrite,
            });
        })
        .WithName("TestPath")
        .WithSummary("Probes a directory path for existence, read access, and write access.")
        .Produces<TestPathResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.StorageConfigWrite);

        // ── GET /settings/providers/health ────────────────────────────────────────
        // Must be mapped BEFORE /providers/{name} so the literal "health" segment
        // is matched before the route parameter catches it.

        grp.MapGet("/providers/health", async (
            IProviderHealthRepository healthRepo,
            CancellationToken ct) =>
        {
            var records = await healthRepo.GetAllAsync(ct);
            return Results.Ok(records.Select(r => new ProviderHealthStatusResponse(
                r.ProviderId,
                r.Status.ToString(),
                r.ConsecutiveFailures,
                r.LastCheckAt?.ToString("o"),
                r.LastSuccessAt?.ToString("o"),
                r.LastFailureAt?.ToString("o"),
                r.LastFailureReason,
                r.NextCheckAt?.ToString("o"),
                r.DownSince?.ToString("o"))));
        })
        .WithName("GetProviderHealth")
        .WithSummary("Returns health status for all tracked providers.")
        .Produces<IEnumerable<ProviderHealthStatusResponse>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersStatusRead);

        // ── PUT /settings/providers/{name} ───────────────────────────────────────

        grp.MapPut("/providers/{name}", (
            string name,
            UpdateProviderRequest request,
            IConfigurationLoader configLoader,
            IProviderConnectionCheckRepository connectionChecks) =>
        {
            var provider = configLoader.LoadProvider(name);

            if (provider is null)
            {
                return ApiErrors.NotFound($"Provider '{name}' not found.");
            }

            provider.Enabled = request.Enabled;
            SaveProviderManifest(configLoader, provider, connectionChecks);

            var displayName = ResolveDisplayName(provider);

            return Results.Ok(BuildProviderStatusResponse(provider, displayName));
        })
        .WithName("UpdateProvider")
        .WithSummary("Toggles a provider's enabled state and saves to the manifest.")
        .Produces<ProviderStatusResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        // ── GET /settings/providers ─────────────────────────────────────────────

        grp.MapGet("/providers", async (
            IConfigurationLoader configLoader,
            IProviderHealthRepository healthRepo,
            IProviderConnectionCheckRepository connectionChecks,
            CancellationToken ct) =>
        {
            var providers = configLoader.LoadAllProviders();

            var healthRecords = await healthRepo.GetAllAsync(ct);
            var healthMap = healthRecords.ToDictionary(
                r => r.ProviderId, StringComparer.OrdinalIgnoreCase);
            var checks = await connectionChecks.GetAllAsync(ct);
            var checkMap = checks.ToDictionary(check => check.ProviderName, StringComparer.OrdinalIgnoreCase);

            var statuses = providers.Select(provider =>
            {
                var displayName = ResolveDisplayName(provider);

                if (healthMap.TryGetValue(provider.Name, out var healthRecord))
                {
                    // Use persisted health data — no live probe needed.
                    bool isReachable = healthRecord.Status != ProviderHealthStatus.Down;
                    return BuildProviderStatusResponse(
                        provider, displayName, isReachable, healthRecord,
                        checkMap.GetValueOrDefault(provider.Name));
                }

                return BuildProviderStatusResponse(provider, displayName,
                    connectionCheck: checkMap.GetValueOrDefault(provider.Name));
            });

            return Results.Ok(statuses.ToArray());
        })
        .WithName("GetProviderStatus")
        .WithSummary("Returns enabled/reachability status for all registered metadata providers.")
        .Produces<ProviderStatusResponse[]>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigRead);

        // ── GET /settings/organization-template ───────────────────────────────────

        grp.MapGet("/organization-template", (
            IConfigurationLoader configLoader,
            Microsoft.Extensions.Options.IOptions<MediaEngine.Ingestion.Models.IngestionOptions> ingestionOpts,
            IFileOrganizer organizer) =>
        {
            var core = configLoader.LoadCore();
            string template = !string.IsNullOrWhiteSpace(core.OrganizationTemplate)
                ? core.OrganizationTemplate
                : ingestionOpts.Value.OrganizationTemplate;

            string? preview = organizer.ValidateTemplate(template, out _);

            return Results.Ok(new OrganizationTemplateResponse
            {
                Template = template,
                Preview = preview,
                Templates = core.OrganizationTemplates,
            });
        })
        .WithName("GetOrganizationTemplate")
        .WithSummary("Returns the current file organization templates (default + per-media-type) and a sample preview.")
        .Produces<OrganizationTemplateResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.StorageStatusRead);

        // ── PUT /settings/organization-template ───────────────────────────────────

        grp.MapPost("/organization-template/preview", (
            UpdateOrganizationTemplateRequest request,
            IFileOrganizer organizer) =>
        {
            if (string.IsNullOrWhiteSpace(request.Template))
            {
                return ApiErrors.BadRequest("Template cannot be empty.");
            }

            string? preview = organizer.ValidateTemplate(request.Template, out var error);
            if (preview is null)
            {
                return ApiErrors.BadRequest(error ?? "Invalid template.");
            }

            if (request.Templates is not null)
            {
                foreach (var (key, tmpl) in request.Templates)
                {
                    if (string.IsNullOrWhiteSpace(tmpl))
                    {
                        continue;
                    }

                    string? typePreview = organizer.ValidateTemplate(tmpl, out var typeError);
                    if (typePreview is null)
                    {
                        return ApiErrors.BadRequest($"Invalid template for '{key}': {typeError}");
                    }
                }
            }

            return Results.Ok(new OrganizationTemplateResponse
            {
                Template = request.Template,
                Preview = preview,
                Templates = request.Templates is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(request.Templates, StringComparer.OrdinalIgnoreCase),
            });
        })
        .WithName("PreviewOrganizationTemplate")
        .WithSummary("Validates file organization templates and returns a sample preview without saving.")
        .Produces<OrganizationTemplateResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.StorageConfigWrite);

        grp.MapPut("/organization-template", (
            UpdateOrganizationTemplateRequest request,
            IConfigurationLoader configLoader,
            IFileOrganizer organizer) =>
        {
            if (string.IsNullOrWhiteSpace(request.Template))
            {
                return ApiErrors.BadRequest("Template cannot be empty.");
            }

            string? preview = organizer.ValidateTemplate(request.Template, out var error);
            if (preview is null)
            {
                return ApiErrors.BadRequest(error ?? "Invalid template.");
            }

            // Validate per-media-type templates if provided.
            if (request.Templates is not null)
            {
                foreach (var (key, tmpl) in request.Templates)
                {
                    if (string.IsNullOrWhiteSpace(tmpl))
                    {
                        continue;
                    }

                    string? typePreview = organizer.ValidateTemplate(tmpl, out var typeError);
                    if (typePreview is null)
                    {
                        return ApiErrors.BadRequest($"Invalid template for '{key}': {typeError}");
                    }
                }
            }

            var core = configLoader.LoadCore();
            core.OrganizationTemplate = request.Template;
            if (request.Templates is not null)
            {
                core.OrganizationTemplates = new Dictionary<string, string>(request.Templates, StringComparer.OrdinalIgnoreCase);
            }

            configLoader.SaveCore(core);

            return Results.Ok(new OrganizationTemplateResponse
            {
                Template = request.Template,
                Preview = preview,
                Templates = core.OrganizationTemplates,
            });
        })
        .WithName("UpdateOrganizationTemplate")
        .WithSummary("Validates and saves file organization templates (default + per-media-type).")
        .Produces<OrganizationTemplateResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.StorageConfigWrite);

        // ── Provider credential onboarding ────────────────────────────────────

        grp.MapPost("/providers/{name}/credentials/test", async (
            string name,
            ProviderCredentialWriteRequest request,
            ProviderCredentialService credentials,
            CancellationToken ct) =>
        {
            return Results.Ok(await credentials.TestAsync(name, request.Credentials, ct));
        })
        .WithName("TestProviderCredentials")
        .WithSummary("Validates provider credential format and performs a non-mutating authentication check.")
        .Produces<ProviderCredentialOperationResultDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        grp.MapPut("/providers/{name}/credentials", async (
            string name,
            ProviderCredentialWriteRequest request,
            ProviderCredentialService credentials,
            CancellationToken ct) =>
        {
            return Results.Ok(await credentials.SaveAsync(name, request.Credentials, ct));
        })
        .WithName("SaveProviderCredentials")
        .WithSummary("Verifies and atomically saves or rotates a provider credential set.")
        .Produces<ProviderCredentialOperationResultDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        grp.MapDelete("/providers/{name}/credentials", (
            string name,
            ProviderCredentialService credentials) =>
        {
            return Results.Ok(credentials.Remove(name));
        })
        .WithName("RemoveProviderCredentials")
        .WithSummary("Removes every stored credential for a provider.")
        .Produces<ProviderCredentialOperationResultDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        // ── POST /settings/providers/{name}/test ────────────────────────────────
        // Uses the same read-only connection probe as setup and credential save.

        grp.MapPost("/providers/{name}/test", async (
            string name,
            ProviderCredentialService credentials,
            CancellationToken ct) =>
        {
            var result = await credentials.TestConfiguredAsync(name, ct).ConfigureAwait(false);
            return Results.Ok(new ProviderTestResponse(
                result.Success,
                result.ResponseTimeMs,
                [],
                result.Message)
            {
                Status = result.Status,
            });
        })
        .WithName("TestProvider")
        .WithSummary("Tests the configured provider connection without relying on a sample title match.")
        .Produces<ProviderTestResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        // ── POST /settings/providers/{name}/sample ──────────────────────────────
        // Fetches sample claims from a provider for a given title.
        // Returns the full claim list for property picker UI.

        grp.MapPost("/providers/{name}/sample", async (
            string name,
            ProviderSampleRequest request,
            IConfigurationLoader configLoader,
            IEnumerable<IExternalMetadataProvider> providers,
            CancellationToken ct) =>
        {
            var providerConfig = configLoader.LoadProvider(name);
            if (providerConfig is null)
            {
                return ApiErrors.NotFound($"Provider '{name}' not found.");
            }

            if (!ProviderExecutionFilter.IsEnabled(name, [providerConfig]))
            {
                return Results.Ok(new ProviderSampleResponse
                {
                    ProviderName = name,
                    Message = "Provider disabled. Enable it before fetching sample claims.",
                    Claims = [],
                });
            }

            // Provider instances are composed once at Engine startup.
            var adapter = providers.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            if (adapter is null)
            {
                return ApiErrors.NotFound($"Provider '{name}' is configured but not registered. Restart the Engine after changing provider configuration.");
            }

            var baseUrl = GetBaseUrlForProvider(providerConfig);
            var sparqlUrl = providerConfig.Endpoints.TryGetValue("wikidata_sparql", out var sp) ? sp : null;

            var mediaType = MediaType.Books; // Default.
            if (!string.IsNullOrWhiteSpace(request.MediaType)
                && Enum.TryParse<MediaType>(request.MediaType, true, out var parsed))
            {
                mediaType = parsed;
            }
            var core = configLoader.LoadCore();
            var language = ResolveMetadataLanguage(core);
            var country = ResolveProviderCountry(core);

            var lookup = new ProviderLookupRequest
            {
                EntityId = Guid.NewGuid(),
                EntityType = EntityType.Work,
                MediaType = mediaType,
                Title = request.Title ?? "The Fellowship of the Ring",
                Author = request.Author,
                Isbn = request.Isbn,
                Asin = request.Asin,
                BaseUrl = baseUrl ?? string.Empty,
                SparqlBaseUrl = sparqlUrl,
                Language = language,
                Country = country,
            };

            var claims = await adapter.FetchAsync(lookup, ct);

            return Results.Ok(new ProviderSampleResponse
            {
                ProviderName = name,
                Claims = claims.Select(c => new ProviderSampleClaim
                {
                    Key = c.Key,
                    Value = c.Value.Length > 500 ? c.Value[..500] + "…" : c.Value,
                    Confidence = c.Confidence,
                }).ToList(),
            });
        })
        .WithName("SampleProvider")
        .WithSummary("Fetches sample claims from a provider for a given title, for the property picker UI.")
        .Produces<ProviderSampleResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        // ── PUT /settings/providers/{name}/config ───────────────────────────────
        // Saves the full provider configuration (endpoints, weights, throttle, etc.)

        grp.MapPut("/providers/{name}/config", (
            string name,
            ProviderConfigUpdateRequest request,
            IConfigurationLoader configLoader,
            IProviderConnectionCheckRepository connectionChecks) =>
        {
            var existing = configLoader.LoadProvider(name);
            if (existing is null)
            {
                return ApiErrors.NotFound($"Provider '{name}' not found.");
            }

            string? normalizedLanguageStrategy = null;
            if (request.LanguageStrategy is not null
                && !TryNormalizeLanguageStrategy(request.LanguageStrategy, out normalizedLanguageStrategy))
            {
                return ApiErrors.BadRequest("language_strategy must be one of: source, localized, both.");
            }

            // Update mutable fields.
            if (request.Enabled.HasValue)
            {
                existing.Enabled = request.Enabled.Value;
            }

            if (request.Weight.HasValue)
            {
                existing.Weight = Math.Clamp(request.Weight.Value, 0.0, 1.0);
            }

            if (request.FieldWeights is not null)
            {
                existing.FieldWeights = request.FieldWeights;
            }

            if (request.ThrottleMs.HasValue)
            {
                existing.ThrottleMs = Math.Max(0, request.ThrottleMs.Value);
            }

            if (request.MaxConcurrency.HasValue)
            {
                existing.MaxConcurrency = Math.Max(1, request.MaxConcurrency.Value);
            }

            if (request.Endpoints is not null)
            {
                foreach (var (key, url) in request.Endpoints)
                {
                    existing.Endpoints[key] = url;
                }
            }
            if (request.CapabilityTags is not null)
            {
                existing.CapabilityTags = request.CapabilityTags;
            }

            if (request.LanguageStrategy is not null)
            {
                existing.LanguageStrategyRaw = normalizedLanguageStrategy!;
            }

            // Config-driven field mappings: replace the entire list if provided.
            if (request.FieldMappings is not null)
            {
                existing.FieldMappings = request.FieldMappings
                    .Select(fm => new MediaEngine.Domain.Configuration.FieldMappingConfig
                    {
                        ClaimKey = fm.ClaimKey,
                        JsonPath = fm.JsonPath,
                        Confidence = fm.Confidence,
                        Transform = fm.Transform,
                        TransformArgs = fm.TransformArgs,
                    })
                    .ToList();
            }

            // HTTP client settings. Credentials use the dedicated atomic lifecycle endpoints.
            if (request.TimeoutSeconds.HasValue)
            {
                existing.HttpClient ??= new MediaEngine.Domain.Configuration.HttpClientConfig();
                existing.HttpClient.TimeoutSeconds = Math.Clamp(request.TimeoutSeconds.Value, 1, 120);
            }
            if (request.CustomIconName is not null)
            {
                existing.CustomIconName = string.IsNullOrWhiteSpace(request.CustomIconName) ? null : request.CustomIconName;
            }

            SaveProviderManifest(configLoader, existing, connectionChecks);

            var displayName = ResolveDisplayName(existing);

            return Results.Ok(BuildProviderStatusResponse(existing, displayName));
        })
        .WithName("UpdateProviderConfig")
        .WithSummary("Saves full provider configuration including endpoints, weights, throttle, and capabilities.")
        .Produces<ProviderStatusResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        // ── DELETE /settings/providers/{name} ───────────────────────────────────
        // Deletes a provider config file. Wikidata and local_filesystem cannot be deleted.

        grp.MapDelete("/providers/{name}", (
            string name,
            IConfigurationLoader configLoader,
            IProviderConnectionCheckRepository connectionChecks) =>
        {
            // Protect universe and filesystem providers.
            if (string.Equals(name, "wikidata", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(
                    detail: "The Universe provider (Wikidata) cannot be removed. In a future version, this may be configurable.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.Equals(name, "local_filesystem", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(
                    detail: "The Local Filesystem provider cannot be removed.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var existing = configLoader.LoadProvider(name);
            if (existing is null)
            {
                return ApiErrors.NotFound($"Provider '{name}' not found.");
            }

            // Disable rather than physically deleting the file — preserves history.
            existing.Enabled = false;
            SaveProviderManifest(configLoader, existing, connectionChecks);

            return Results.NoContent();
        })
        .WithName("DeleteProvider")
        .WithSummary("Removes a metadata provider (disables its configuration).")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        // ── GET /settings/hydration ──────────────────────────────────────────
        grp.MapGet("/hydration", (IConfigurationLoader configLoader) =>
        {
            var settings = configLoader.LoadHydration();
            return Results.Ok(SettingsContractMapper.ToContract(settings));
        })
        .WithName("GetHydrationSettings")
        .WithSummary("Load hydration pipeline configuration.")
        .Produces<HydrationSettingsDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRead);

        // ── PUT /settings/hydration ──────────────────────────────────────────
        grp.MapPut("/hydration", (
            HydrationSettingsDto settings,
            IConfigurationLoader configLoader) =>
        {
            configLoader.SaveHydration(SettingsContractMapper.ToStorage(settings));
            return Results.Ok(new SettingsSavedResponse(true));
        })
        .WithName("SaveHydrationSettings")
        .WithSummary("Save hydration pipeline configuration.")
        .Produces<SettingsSavedResponse>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // ── GET /settings/pipelines ──────────────────────────────────────
        grp.MapGet("/pipelines", (IConfigurationLoader configLoader) =>
        {
            var pipelines = configLoader.LoadPipelines();
            return Results.Ok(SettingsContractMapper.ToContract(pipelines));
        })
        .WithName("GetPipelines")
        .WithDescription("Current pipeline configuration per media type")
        .Produces<ContractPipelineConfiguration>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        grp.MapGet("/pipelines/defaults", (IConfigurationLoader configLoader) =>
        {
            var defaults = configLoader.LoadConfig<Dictionary<string, MediaEngine.Domain.Configuration.MediaTypePipeline>>(
                string.Empty,
                "pipeline-priority-defaults") ?? new(StringComparer.OrdinalIgnoreCase);
            var configuration = new MediaEngine.Domain.Configuration.PipelineConfiguration
            {
                Pipelines = new Dictionary<string, MediaEngine.Domain.Configuration.MediaTypePipeline>(
                    defaults,
                    StringComparer.OrdinalIgnoreCase),
            };
            return Results.Ok(SettingsContractMapper.ToContract(configuration));
        })
        .WithName("GetDefaultPipelines")
        .WithSummary("Returns shipped provider-order defaults for each media type.")
        .Produces<ContractPipelineConfiguration>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // ── PUT /settings/pipelines ──────────────────────────────────────
        grp.MapPut("/pipelines", (
            ContractPipelineConfiguration pipelines,
            IConfigurationLoader configLoader) =>
        {
            configLoader.SavePipelines(SettingsContractMapper.ToStorage(pipelines));
            return Results.Ok(new SettingsSavedResponse(true));
        })
        .WithName("SavePipelines")
        .WithDescription("Save pipeline configuration")
        .Produces<SettingsSavedResponse>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // ── GET /settings/media-types ──────────────────────────────────────────
        grp.MapGet("/media-types", (IConfigurationLoader configLoader) =>
        {
            var config = configLoader.LoadMediaTypes();
            return Results.Ok(SettingsContractMapper.ToContract(config));

        })
        .WithName("GetMediaTypes")
        .WithSummary("Load media type definitions including icons, extensions, and category folders.")
        .Produces<MediaTypeConfigurationDto>(StatusCodes.Status200OK)
        .RequireHumanSelfService();

        // ── PUT /settings/media-types ──────────────────────────────────────────
        grp.MapPut("/media-types", (
            MediaTypeConfigurationDto config,
            IConfigurationLoader configLoader) =>
        {
            if (config?.Types is null || config.Types.Count == 0)
            {
                return ApiErrors.BadRequest("At least one media type is required.");
            }

            var dupKeys = config.Types
                .GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1)?.Key;
            if (dupKeys is not null)
            {
                return ApiErrors.BadRequest($"Duplicate media type key: '{dupKeys}'.");
            }

            var dupNames = config.Types
                .GroupBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1)?.Key;
            if (dupNames is not null)
            {
                return ApiErrors.BadRequest($"Duplicate media type display name: '{dupNames}'.");
            }

            configLoader.SaveMediaTypes(SettingsContractMapper.ToStorage(config));
            return Results.Ok(new SettingsSavedResponse(true));
        })
        .WithName("SaveMediaTypes")
        .WithSummary("Save media type definitions including custom types.")
        .Produces<SettingsSavedResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireEffectiveAdministrator();

        // ── POST /settings/media-types/add ────────────────────────────────────
        grp.MapPost("/media-types/add", (
            MediaTypeDefinitionDto newType,
            IConfigurationLoader configLoader) =>
        {
            if (string.IsNullOrWhiteSpace(newType.Key) || string.IsNullOrWhiteSpace(newType.DisplayName))
            {
                return ApiErrors.BadRequest("Key and display name are required.");
            }

            var config = configLoader.LoadMediaTypes();

            if (config.Types.Any(t => string.Equals(t.Key, newType.Key, StringComparison.OrdinalIgnoreCase)))
            {
                return ApiErrors.BadRequest($"Media type key '{newType.Key}' already exists.");
            }

            if (config.Types.Any(t => string.Equals(t.DisplayName, newType.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                return ApiErrors.BadRequest($"Media type '{newType.DisplayName}' already exists.");
            }

            newType.BuiltIn = false;
            config.Types.Add(SettingsContractMapper.ToStorage(newType));
            configLoader.SaveMediaTypes(config);

            return Results.Ok(SettingsContractMapper.ToContract(config));

        })
        .WithName("AddMediaType")
        .WithSummary("Add a custom media type definition.")
        .Produces<MediaTypeConfigurationDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireEffectiveAdministrator();

        // ── DELETE /settings/media-types/{key} ────────────────────────────────
        grp.MapDelete("/media-types/{key}", (
            string key,
            IConfigurationLoader configLoader) =>
        {
            var config = configLoader.LoadMediaTypes();
            var existing = config.Types.FirstOrDefault(
                t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                return ApiErrors.NotFound($"Media type '{key}' not found.");
            }

            if (existing.BuiltIn)
            {
                return ApiErrors.BadRequest("Built-in media types cannot be deleted.");
            }

            config.Types.Remove(existing);
            configLoader.SaveMediaTypes(config);

            return Results.NoContent();
        })
        .WithName("DeleteMediaType")
        .WithSummary("Remove a custom media type definition.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        // ── Provider Icon Upload ──────────────────────────────────────────────

        grp.MapPost("/providers/{name}/icon", async (
            string name,
            HttpRequest request,
            IConfiguration config) =>
        {
            if (!request.HasFormContentType)
            {
                return ApiErrors.BadRequest("Expected multipart form data.");
            }

            var form = await request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return ApiErrors.BadRequest("No file uploaded.");
            }

            if (file.Length > 256 * 1024)
            {
                return ApiErrors.BadRequest("Icon must be 256 KB or smaller.");
            }

            var ext = Path.GetExtension(file.FileName)?.ToLowerInvariant();
            if (ext is not ".svg" and not ".png" and not ".jpg" and not ".jpeg")
            {
                return ApiErrors.BadRequest("Allowed formats: SVG, PNG, JPG.");
            }

            var configDir = config["MediaEngine:ConfigDirectory"] ?? "config";
            var iconsDir = Path.Combine(configDir, "icons");
            Directory.CreateDirectory(iconsDir);

            // Remove any existing icon for this provider.
            foreach (var existing in Directory.EnumerateFiles(iconsDir, $"{name}.*"))
            {
                File.Delete(existing);
            }

            var filePath = Path.Combine(iconsDir, $"{name}{ext}");
            await using var stream = File.Create(filePath);
            await file.CopyToAsync(stream);

            return Results.Ok(new ProviderIconPathResponse($"/settings/providers/{name}/icon"));
        })
        .WithName("UploadProviderIcon")
        .WithSummary("Upload an icon (SVG/PNG/JPG, max 256KB) for a provider.")
        .Accepts<IFormFile>("multipart/form-data")
        .Produces<ProviderIconPathResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .DisableAntiforgery()
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigWrite);

        grp.MapGet("/providers/{name}/icon", (
            string name,
            IConfiguration config) =>
        {
            var configDir = config["MediaEngine:ConfigDirectory"] ?? "config";
            var iconsDir = Path.Combine(configDir, "icons");

            if (!Directory.Exists(iconsDir))
            {
                return ApiErrors.NotFound($"No icon has been uploaded for provider '{name}'.");
            }

            var match = Directory.EnumerateFiles(iconsDir, $"{name}.*").FirstOrDefault();
            if (match is null)
            {
                return ApiErrors.NotFound($"No icon has been uploaded for provider '{name}'.");
            }

            var ext = Path.GetExtension(match).ToLowerInvariant();
            var contentType = ext switch
            {
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                _ => "application/octet-stream",
            };

            return Results.File(match, contentType);
        })
        .WithName("GetProviderIcon")
        .WithSummary("Serve the uploaded icon for a provider.")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersStatusRead);

        // ── GET /settings/server-general ──────────────────────────────────────

        grp.MapGet("/server-general", (IConfigurationLoader configLoader) =>
        {
            var core = configLoader.LoadCore();
            return Results.Ok(new ServerGeneralResponse
            {
                ServerName = core.ServerName,
                Language = core.Language.Metadata,
                DisplayLanguage = core.Language.Display,
                MetadataLanguage = core.Language.Metadata,
                AdditionalLanguages = core.Language.Additional,
                AcceptAnyLanguage = core.Language.AcceptAny,
                Country = core.Country,
                DateFormat = core.DateFormat,
                TimeFormat = core.TimeFormat,
            });
        })
        .WithName("GetServerGeneral")
        .WithSummary("Returns server identity and regional settings.")
        .Produces<ServerGeneralResponse>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // ── PUT /settings/server-general ──────────────────────────────────────

        grp.MapPut("/server-general", (
            ServerGeneralRequest request,
            IConfigurationLoader configLoader) =>
        {
            if (string.IsNullOrWhiteSpace(request.ServerName))
            {
                return ApiErrors.BadRequest("server_name cannot be empty");
            }

            var core = configLoader.LoadCore();
            core.ServerName = request.ServerName.Trim();
            core.Language = new LanguagePreferences
            {
                Display = !string.IsNullOrWhiteSpace(request.DisplayLanguage) ? request.DisplayLanguage : request.Language,
                Metadata = !string.IsNullOrWhiteSpace(request.MetadataLanguage) ? request.MetadataLanguage : request.Language,
                Additional = request.AdditionalLanguages ?? [],
                AcceptAny = request.AcceptAnyLanguage,
            };
            core.Country = request.Country;
            core.DateFormat = request.DateFormat;
            core.TimeFormat = request.TimeFormat;
            configLoader.SaveCore(core);
            return Results.Ok();
        })
        .WithName("UpdateServerGeneral")
        .WithSummary("Saves server identity and regional settings.")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireEffectiveAdministrator();

        grp.MapGet("/catalog", () => Results.Ok(new[]
        {
            new SettingsCatalogEntryResponse(
                key: "core",
                label: "Core runtime",
                source: "config/core.json",
                owner: "json",
                editable: true,
                role: "Administrator",
                restart_required: false,
                deprecated: false),
            new SettingsCatalogEntryResponse(
                key: "libraries",
                label: "Libraries",
                source: "config/libraries.json",
                owner: "json",
                editable: true,
                role: "Administrator",
                restart_required: false,
                deprecated: false),
            new SettingsCatalogEntryResponse(
                key: "providers",
                label: "Providers",
                source: "config/providers/*.json + config/secrets/*.json",
                owner: "json",
                editable: true,
                role: "Administrator",
                restart_required: false,
                deprecated: false),
            new SettingsCatalogEntryResponse(
                key: "metadata",
                label: "Metadata behavior",
                source: "config/media_types.json, config/scoring.json, config/hydration.json, config/pipelines.json, config/field_priorities.json",
                owner: "json",
                editable: true,
                role: "Administrator",
                restart_required: false,
                deprecated: false),
            new SettingsCatalogEntryResponse(
                key: "operational-state",
                label: "Operational state",
                source: "SQLite: profiles, api_keys, provider_health, review_queue, system_activity, encode_jobs",
                owner: "sqlite",
                editable: false,
                role: "Administrator",
                restart_required: false,
                deprecated: false),
            new SettingsCatalogEntryResponse(
                key: "ui-palette",
                label: "UI palette and accent customization",
                source: "config/ui/palette.json and accent_color fields",
                owner: "internal",
                editable: false,
                role: "None",
                restart_required: false,
                deprecated: true),
        }))
        .WithName("GetSettingsCatalog")
        .WithSummary("Returns the canonical settings source-of-truth catalog.")
        .Produces<SettingsCatalogEntryResponse[]>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        return app;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Gets the primary base URL for a provider from its config endpoints.</summary>
    private static string? GetBaseUrlForProvider(ProviderConfiguration config)
    {
        // Convention: config-driven adapters use "api" as the primary endpoint key.
        if (config.Endpoints.TryGetValue("api", out var apiUrl) && !string.IsNullOrWhiteSpace(apiUrl))
        {
            return apiUrl;
        }

        // Try the endpoint key matching the provider name (legacy convention).
        if (config.Endpoints.TryGetValue(config.Name, out var url) && !string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        // Try well-known endpoint keys from the legacy mapping.
        if (_endpointKeys.TryGetValue(config.Name, out var epKey)
            && config.Endpoints.TryGetValue(epKey, out var ep)
            && !string.IsNullOrWhiteSpace(ep))
        {
            return ep;
        }

        // Fallback: return the first endpoint URL.
        return config.Endpoints.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    /// <summary>Resolve display name: prefer config's DisplayName, then fallback map, then raw name.</summary>
    private static string ResolveDisplayName(ProviderConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.DisplayName))
        {
            return config.DisplayName;
        }

        return _displayNames.TryGetValue(config.Name, out var dn) ? dn : config.Name;
    }

    private static void SaveProviderManifest(
        IConfigurationLoader configLoader,
        ProviderConfiguration provider,
        IProviderConnectionCheckRepository connectionChecks)
    {
        var http = provider.HttpClient;
        if (http is null)
        {
            configLoader.SaveProvider(provider);
            connectionChecks.DeleteAsync(provider.Name).GetAwaiter().GetResult();
            return;
        }

        var apiKey = http.ApiKey;
        var apiKeyOverride = http.ApiKeyOverride;
        var clientKey = http.ClientKey;
        var accessToken = http.AccessToken;
        var pin = http.Pin;
        var username = http.Username;
        var password = http.Password;
        try
        {
            http.ApiKey = null;
            http.ApiKeyOverride = null;
            http.ClientKey = null;
            http.AccessToken = null;
            http.Pin = null;
            http.Username = null;
            http.Password = null;
            configLoader.SaveProvider(provider);
            connectionChecks.DeleteAsync(provider.Name).GetAwaiter().GetResult();
        }
        finally
        {
            http.ApiKey = apiKey;
            http.ApiKeyOverride = apiKeyOverride;
            http.ClientKey = clientKey;
            http.AccessToken = accessToken;
            http.Pin = pin;
            http.Username = username;
            http.Password = password;
        }
    }

    /// <summary>Builds a <see cref="ProviderStatusResponse"/> from a provider config.</summary>
    private static ProviderStatusResponse BuildProviderStatusResponse(
        ProviderConfiguration provider,
        string displayName,
        bool isReachable = false,
        ProviderHealthRecord? healthRecord = null,
        ProviderConnectionCheck? connectionCheck = null)
    {
        // Prefer explicit can_handle.media_types; fall back to domain-derived media types.
        var mediaTypes = provider.CanHandle?.MediaTypes;
        if (mediaTypes is null || mediaTypes.Count == 0)
        {
            mediaTypes = DeriveMediaTypesFromDomain(provider.Domain);
        }

        return new ProviderStatusResponse
        {
            Name = provider.Name,
            DisplayName = displayName,
            Enabled = provider.Enabled,
            IsZeroKey = !provider.RequiresApiKey,
            IsReachable = isReachable,
            Domain = provider.Domain.ToString(),
            CapabilityTags = provider.CapabilityTags,
            DefaultWeight = provider.Weight,
            FieldWeights = provider.FieldWeights,
            HydrationStages = provider.HydrationStages,
            Endpoints = provider.Endpoints,
            ThrottleMs = provider.ThrottleMs,
            MaxConcurrency = provider.MaxConcurrency,
            LanguageStrategy = provider.LanguageStrategyRaw,
            AvailableFields = provider.AvailableFields,
            MediaTypes = mediaTypes,
            RequiresApiKey = provider.RequiresApiKey,
            HasApiKey = !string.IsNullOrWhiteSpace(provider.HttpClient?.ApiKeyOverride)
                        || !string.IsNullOrWhiteSpace(provider.HttpClient?.ApiKey)
                               || (string.Equals(provider.HttpClient?.ApiKeyDelivery, "basic", StringComparison.OrdinalIgnoreCase)
                                   && !string.IsNullOrWhiteSpace(provider.HttpClient?.Username)
                                   && !string.IsNullOrWhiteSpace(provider.HttpClient?.Password)),
            ApiKeyDelivery = provider.HttpClient?.ApiKeyDelivery,
            ApiKeyParamName = provider.HttpClient?.ApiKeyParamName,
            TimeoutSeconds = provider.HttpClient?.TimeoutSeconds ?? 10,
            CustomIconName = provider.CustomIconName,
            FieldMappings = provider.FieldMappings?.Select(fm => new FieldMappingResponse
            {
                ClaimKey = fm.ClaimKey,
                JsonPath = fm.JsonPath,
                Confidence = fm.Confidence,
                Transform = fm.Transform,
                TransformArgs = fm.TransformArgs,
            }).ToList(),
            HealthStatus = healthRecord?.Status.ToString(),
            ConsecutiveFailures = healthRecord?.ConsecutiveFailures ?? 0,
            LastSuccessAt = healthRecord?.LastSuccessAt?.ToString("o"),
            LastFailureAt = healthRecord?.LastFailureAt?.ToString("o"),
            LastFailureReason = healthRecord?.LastFailureReason,
            DownSince = healthRecord?.DownSince?.ToString("o"),
            ConnectionStatus = connectionCheck?.Status,
            ConnectionCheckedAt = connectionCheck?.CheckedAt.ToString("o"),
            ConnectionMessage = connectionCheck?.Message,
        };
    }

    internal static string? ValidateConfiguredPaths(LibrariesConfiguration config)
    {
        foreach (var path in config.Libraries.SelectMany(library => library.Sources).Select(source => source.Path))
        {
            var error = PathValidator.Validate(path);
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    internal static string? ValidateLibrarySources(LibrariesConfiguration config,
        MediaEngine.Api.Services.Settings.ServerFolderBrowserService folders,
        MediaEngine.Ingestion.Contracts.IFileOrganizer organizer)
    {
        try
        {
            foreach (var library in config.Libraries)
            {
                if (library.OrganizationPolicy.Mode == LibraryOrganizationModes.Custom
                    && organizer.ValidateTemplate(library.OrganizationPolicy.CustomTemplate ?? "", out var templateError) is null)
                {
                    return $"{library.Name}: {templateError}";
                }

                foreach (var source in library.Sources)
                {
                    var validation = folders.Validate(new MediaEngine.Contracts.Settings.ValidateServerFolderRequest
                    {
                        ManualPath = source.Path,
                        CurrentSourceId = source.Id,
                        SelectionMode = source.IsManaged ? MediaEngine.Contracts.Settings.ServerFolderSelectionModes.ManagedLibrary : MediaEngine.Contracts.Settings.ServerFolderSelectionModes.ExistingLibrary,
                    }, config);
                    if (!validation.CanSelect)
                    {
                        return validation.Issues.First(issue => issue.Severity == "error").Message;
                    }
                }
            }
            var rootValidation = folders.Validate(new MediaEngine.Contracts.Settings.ValidateServerFolderRequest
            {
                StorageLocationId = config.ViewStorage.StorageLocationId,
                RelativePath = config.ViewStorage.RelativeRoot,
                SelectionMode = MediaEngine.Contracts.Settings.ServerFolderSelectionModes.PersonalSpaceManaged,
            }, config);
            return rootValidation.CanSelect ? null : rootValidation.Issues.First(issue => issue.Severity == "error").Message;
        }
        catch (Exception exception) when (exception is MediaEngine.Api.Services.Settings.ServerFolderAccessException or ArgumentException or IOException)
        {
            return exception.Message;
        }
    }

    internal static string? ValidateViewRootChange(LibrariesConfiguration current, LibrariesConfiguration proposed)
    {
        try
        {
            static string Root(LibrariesConfiguration value) => Path.GetFullPath(Path.Combine(
                value.StorageLocations.First(location => location.Id == value.ViewStorage.StorageLocationId).Path,
                value.ViewStorage.RelativeRoot));
            var before = Root(current);
            var after = Root(proposed);
            if (!string.Equals(before, after, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && Directory.Exists(before) && Directory.EnumerateFileSystemEntries(before).Any())
            {
                return "The current View root contains Personal Space folders. Changing roots would leave those files behind. Keep the current root; relocation is not available.";
            }

            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return $"View storage could not be checked: {exception.Message}";
        }
    }

    internal static string? ValidateViewStorage(LibrariesConfiguration config)
    {
        if (!string.Equals(config.SchemaVersion, "6.0", StringComparison.Ordinal))
        {
            return "libraries.json must use schema_version 6.0.";
        }

        if (config.Libraries.Any(library =>
                string.Equals(library.Kind, LibraryKinds.Personal, StringComparison.OrdinalIgnoreCase)))
        {
            return "Personal Spaces are profile-owned and must not be configured as libraries.";
        }

        var storage = config.StorageLocations.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, config.ViewStorage.StorageLocationId, StringComparison.OrdinalIgnoreCase));
        if (storage is null)
        {
            return "View storage must reference a configured storage location.";
        }

        if (!storage.AllowWrite)
        {
            return "View storage must reference a writable storage location.";
        }

        var relative = config.ViewStorage.RelativeRoot?.Trim();
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            return "View storage relative_root must be a non-empty relative path.";
        }

        var resolved = Path.GetFullPath(Path.Combine(Path.GetFullPath(storage.Path), relative));
        var parent = Path.GetRelativePath(Path.GetFullPath(storage.Path), resolved);
        if (parent == ".." || parent.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                           || Path.IsPathRooted(parent))
        {
            return "View storage relative_root must remain within its storage location.";
        }

        return null;
    }

    internal static AuthSettingsDto ToAuthSettingsDto(AuthSettings auth, NetworkSettings network, bool restartRequired)
    {
        var canonicalOriginReady = AuthenticationEndpoints.IsCanonicalOriginReady(network);
        var smtpConfigured = auth.PasswordReset.Mode.Equals("Smtp", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(auth.PasswordReset.SmtpHost)
            && auth.PasswordReset.SmtpPort is > 0 and <= 65535
            && !string.IsNullOrWhiteSpace(auth.PasswordReset.FromAddress);
        var recoveryReady = smtpConfigured && canonicalOriginReady;
        return new AuthSettingsDto
        {
            Mode = auth.Mode,
            LocalhostBypass = auth.LocalhostBypass,
            PasswordSignInEnabled = auth.PasswordSignInEnabled,
            PasskeySignInEnabled = auth.PasskeySignInEnabled,
            ExternalSignInEnabled = auth.ExternalSignInEnabled,
            AllowLocalOnlyAccounts = auth.AllowLocalOnlyAccounts,
            TrustedLocalNetworks = auth.TrustedLocalNetworks,
            InvitationLifetimeHours = auth.InvitationLifetimeHours,
            SessionLifetimeHours = auth.SessionLifetimeHours,
            MaximumActiveSessions = auth.MaximumActiveSessions,
            PublicAddress = network.Remote.PublicHostname ?? string.Empty,
            CanonicalOriginReady = canonicalOriginReady,
            PasskeyReady = auth.PasskeySignInEnabled
                && !auth.Mode.Equals("DisabledLocalOnly", StringComparison.OrdinalIgnoreCase)
                && canonicalOriginReady,
            RecoveryDeliveryReady = recoveryReady,
            RestartRequired = restartRequired,
            PasswordReset = new MediaEngine.Contracts.Settings.PasswordResetDeliveryDto
            {
                Mode = auth.PasswordReset.Mode,
                SmtpHost = auth.PasswordReset.SmtpHost,
                SmtpPort = auth.PasswordReset.SmtpPort,
                FromAddress = auth.PasswordReset.FromAddress,
                Configured = smtpConfigured,
                Ready = recoveryReady,
            },
            ExternalProviders = auth.ExternalProviders.Select(provider =>
            {
                var configured = !string.IsNullOrWhiteSpace(provider.Id)
                    && !string.IsNullOrWhiteSpace(provider.ClientId)
                    && (provider.Kind.Equals(ExternalAuthProviderKinds.OpenIdConnect, StringComparison.OrdinalIgnoreCase)
                        ? Uri.TryCreate(provider.Authority, UriKind.Absolute, out var authority)
                          && authority.Scheme == Uri.UriSchemeHttps
                        : !string.IsNullOrWhiteSpace(provider.ClientSecret)
                          && Uri.TryCreate(provider.Issuer, UriKind.Absolute, out var issuer)
                          && issuer.Scheme == Uri.UriSchemeHttps
                          && Uri.TryCreate(provider.AuthorizationEndpoint, UriKind.Absolute, out var authorization)
                          && authorization.Scheme == Uri.UriSchemeHttps
                          && Uri.TryCreate(provider.TokenEndpoint, UriKind.Absolute, out var token)
                          && token.Scheme == Uri.UriSchemeHttps
                          && Uri.TryCreate(provider.UserInformationEndpoint, UriKind.Absolute, out var userInfo)
                          && userInfo.Scheme == Uri.UriSchemeHttps);
                return new MediaEngine.Contracts.Settings.ExternalAuthProviderDto
                {
                    Id = provider.Id,
                    Kind = provider.Kind,
                    Enabled = provider.Enabled,
                    DisplayName = provider.DisplayName,
                    Issuer = provider.Issuer,
                    Authority = provider.Authority,
                    ClientId = provider.ClientId,
                    Scopes = provider.Scopes,
                    UsePkce = provider.UsePkce,
                    AuthorizationEndpoint = provider.AuthorizationEndpoint,
                    TokenEndpoint = provider.TokenEndpoint,
                    UserInformationEndpoint = provider.UserInformationEndpoint,
                    IdClaim = provider.IdClaim,
                    NameClaim = provider.NameClaim,
                    EmailClaim = provider.EmailClaim,
                    CallbackPath = $"/signin-tuvima-{provider.Id}",
                    Configured = configured,
                    Ready = AuthenticationEndpoints.IsExternalSignInEnabled(auth)
                        && provider.Enabled && configured && canonicalOriginReady,
                    RestartRequired = restartRequired,
                };
            }).ToList(),
        };
    }

    private static string? ValidateExternalProvider(string providerId, UpdateExternalAuthProviderRequest request)
    {
        if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 64 ||
            providerId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return "Provider id must contain only letters, numbers, hyphens, and underscores.";
        }

        if (request.Kind is not (ExternalAuthProviderKinds.OpenIdConnect or ExternalAuthProviderKinds.OAuth))
        {
            return "Provider kind must be oidc or oauth.";
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.ClientId))
        {
            return "Provider display name and client id are required.";
        }

        if (request.ClientSecret is { Length: > 4096 } || request.ClientSecret is not null && string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            return "Client secret is invalid.";
        }

        if (request.ClientSecret is not null && request.ClearClientSecret)
        {
            return "Choose either a replacement client secret or clear the existing secret.";
        }

        if (request.Scopes is null || request.Scopes.Count is 0 or > 20 || request.Scopes.Any(scope => string.IsNullOrWhiteSpace(scope) || scope.Length > 100))
        {
            return "Provider scopes must contain between one and twenty valid values.";
        }

        if (request.Kind == ExternalAuthProviderKinds.OpenIdConnect)
        {
            if (!IsHttps(request.Authority))
            {
                return "OIDC authority must be an absolute HTTPS URL.";
            }

            var tenantError = ExternalIssuerMatcher.GetMicrosoftTenantError(request.Authority);
            if (tenantError is not null)
            {
                return tenantError;
            }

            if (!request.Scopes.Contains("openid", StringComparer.Ordinal))
            {
                return "OIDC providers must request the openid scope.";
            }
        }
        else if (!IsHttps(request.Issuer) || !IsHttps(request.AuthorizationEndpoint) ||
                 !IsHttps(request.TokenEndpoint) || !IsHttps(request.UserInformationEndpoint))
        {
            return "OAuth issuer, authorization, token, and user information endpoints must use HTTPS.";
        }
        return null;
    }

    private static ExternalAuthProviderSettings ToExternalProvider(
        string providerId,
        UpdateExternalAuthProviderRequest request) => new()
        {
            Id = providerId.Trim(),
            Kind = request.Kind,
            Enabled = request.Enabled,
            DisplayName = request.DisplayName.Trim(),
            Issuer = (request.Issuer ?? string.Empty).Trim(),
            Authority = (request.Authority ?? string.Empty).Trim(),
            ClientId = request.ClientId.Trim(),
            Scopes = request.Scopes.Select(scope => scope.Trim()).Distinct(StringComparer.Ordinal).ToList(),
            UsePkce = request.UsePkce,
            AuthorizationEndpoint = (request.AuthorizationEndpoint ?? string.Empty).Trim(),
            TokenEndpoint = (request.TokenEndpoint ?? string.Empty).Trim(),
            UserInformationEndpoint = (request.UserInformationEndpoint ?? string.Empty).Trim(),
            IdClaim = (request.IdClaim ?? "id").Trim(),
            NameClaim = (request.NameClaim ?? "name").Trim(),
            EmailClaim = (request.EmailClaim ?? "email").Trim(),
        };

    private static bool IsHttps(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static string ResolveMetadataLanguage(CoreConfiguration core) =>
        string.IsNullOrWhiteSpace(core.Language.Metadata) ? "en" : core.Language.Metadata.Trim();

    private static string ResolveProviderCountry(CoreConfiguration core) =>
        string.IsNullOrWhiteSpace(core.Country) ? "US" : core.Country.Trim().ToUpperInvariant();

    private static bool TryNormalizeLanguageStrategy(string? value, out string? normalized)
    {
        normalized = null;

        if (value is null)
        {
            return false;
        }

        var candidate = value.Trim().ToLowerInvariant();
        switch (candidate)
        {
            case "source":
            case "localized":
            case "both":
                normalized = candidate;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Derives media type capabilities from the provider's domain when
    /// <c>can_handle.media_types</c> is missing or empty in the config file.
    /// </summary>
    private static List<string> DeriveMediaTypesFromDomain(ProviderDomain domain) => domain switch
    {
        ProviderDomain.Ebook => ["Books"],
        ProviderDomain.Audiobook => ["Audiobooks"],
        ProviderDomain.Comic => ["Comic"],
        ProviderDomain.Video => ["Movies", "TV"],
        ProviderDomain.Universal => ["Books", "Audiobooks", "Comic", "Movies", "TV"],
        _ => [],
    };
}
