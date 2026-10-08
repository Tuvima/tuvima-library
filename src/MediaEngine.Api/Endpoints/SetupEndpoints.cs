using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Setup;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

public static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/setup/v1").WithTags("Setup")
            .RequireAuthorization(AuthPolicies.DashboardService);

        group.MapGet("/status", async (SetupSessionService sessions, CancellationToken ct) =>
            Results.Ok(await sessions.GetStatusAsync(ct).ConfigureAwait(false)))
            .Produces<SetupStatusDto>();

        group.MapGet("/locale", async (HttpContext context, ClaimsPrincipal user,
            SetupSessionService sessions, IConfigurationLoader configuration, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct))
            {
                return Results.Unauthorized();
            }
            var core = configuration.LoadCore();
            return Results.Ok(new SetupLocaleDto(core.Language.Display, core.Language.Metadata, core.Country));
        }).Produces<SetupLocaleDto>();
        group.MapPut("/locale", async (SetupLocaleDto request, HttpContext context, ClaimsPrincipal user,
            SetupSessionService sessions, IConfigurationLoader configuration, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct))
            {
                return Results.Unauthorized();
            }
            try
            {
                var display = System.Globalization.CultureInfo.GetCultureInfo(request.DisplayLanguage);
                var metadata = System.Globalization.CultureInfo.GetCultureInfo(request.MetadataLanguage);
                var region = new System.Globalization.RegionInfo(request.Country);
                if (string.IsNullOrEmpty(display.Name) || string.IsNullOrEmpty(metadata.Name))
                {
                    return ApiErrors.BadRequest("Choose a language and country.");
                }
                var normalized = new SetupLocaleDto(display.Name, metadata.Name, region.TwoLetterISORegionName);
#if DEBUG
                var protection = context.RequestServices.GetService<MediaEngine.Api.DevSupport.RealMediaProtectionService>();
                if (protection is not null) await protection.SaveLocaleAsync(normalized, configuration, ct);
                else
#endif
                {
                    var core = configuration.LoadCore();
                    core.Language.Display = normalized.DisplayLanguage;
                    core.Language.Metadata = normalized.MetadataLanguage;
                    core.Country = normalized.Country;
                    configuration.SaveCore(core);
                }
                return Results.Ok(normalized);
            }
            catch (ArgumentException) { return ApiErrors.BadRequest("The language or country is not recognized."); }
        }).Produces<SetupLocaleDto>();

        group.MapPost("/begin", async (SetupSessionService sessions, CancellationToken ct) =>
        {
            var result = await sessions.BeginAsync(ct).ConfigureAwait(false);
            return result is null
                ? ApiErrors.Conflict("Setup has already been secured by an administrator account.")
                : Results.Ok(result);
        }).Produces<SetupStartResponse>()
            .RequireRateLimiting("authentication");

        group.MapPost("/preflight", async (
            HttpContext context, ClaimsPrincipal user, SetupSessionService sessions,
            SetupPreflightService preflight, OnboardingRepository onboarding, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            var result = await preflight.RunAsync(ct).ConfigureAwait(false);
            await onboarding.SetStepAsync(
                "preflight", result.Passed ? "passed" : "blocked",
                result.Passed ? "Required storage and configuration checks passed." : "One or more required paths are blocked.",
                result.Passed ? null : "/setup?step=preflight", null, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }).Produces<SetupPreflightDto>();

        group.MapPost("/administrator", async (
            SetupAdministratorRequest request, HttpContext context, ClaimsPrincipal user,
            SetupSessionService sessions, IFirstPartyIdentityService identity,
            OnboardingRepository onboarding, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            if (await identity.IsAdministratorConfiguredAsync(ct).ConfigureAwait(false))
            {
                await onboarding.SetStepAsync("administrator", "passed", "Administrator account is configured.", null, null, ct).ConfigureAwait(false);
                return Results.Ok(new SetupAdministratorResponse(false, []));
            }
            try
            {
                var issued = await identity.BootstrapAdministratorAsync(
                    request.Email, request.Password, request.DisplayName,
                    request.DeviceId, request.DeviceName, "Tuvima Setup", ct, request.Pin).ConfigureAwait(false);
                await onboarding.SetStepAsync(
                    "administrator", "passed", "Administrator account and initial profile created.",
                    null, issued.Profile.Id, ct).ConfigureAwait(false);
                return Results.Ok(new SetupAdministratorResponse(true, issued.RecoveryCodes));
            }
            catch (ArgumentException ex) { return ApiErrors.BadRequest(ex.Message); }
            catch (InvalidOperationException ex) { return ApiErrors.Conflict(ex.Message); }
        }).Produces<SetupAdministratorResponse>()
            .RequireRateLimiting("authentication");

        group.MapPost("/media-locations/validate", async (
            HttpContext context, ClaimsPrincipal user, SetupSessionService sessions,
            SetupMediaLocationValidationService mediaLocations,
            OnboardingRepository onboarding, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            var result = mediaLocations.Validate();
            await onboarding.SetStepAsync("media-locations", result.Passed ? "passed" : "blocked", result.Detail,
                result.Passed ? null : "/setup?step=media-locations", null, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }).Produces<SetupMediaLocationsDto>();

        group.MapGet("/libraries", async (
            HttpContext context, ClaimsPrincipal user, SetupSessionService sessions,
            IConfigurationLoader configuration, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(SettingsContractMapper.ToContract(configuration.LoadLibraries()));
        }).Produces<LibrariesConfigurationDto>();

        group.MapPut("/libraries", async (
            UpdateLibrariesRequest request, HttpContext context, ClaimsPrincipal user,
            SetupSessionService sessions, IConfigurationLoader configuration, MediaEngine.Api.Services.Settings.ServerFolderBrowserService folders, MediaEngine.Ingestion.Contracts.IFileOrganizer organizer, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            var config = SettingsContractMapper.ToStorage(request);
            var error = SettingsEndpoints.ValidateViewStorage(config)
                ?? SettingsEndpoints.ValidateViewRootChange(configuration.LoadLibraries(), config)
                ?? SettingsEndpoints.ValidateConfiguredPaths(config);
            if (error is not null)
            {
                return ApiErrors.BadRequest(error);
            }

            var validationErrors = JsonConfigValidator.Validate(config, "libraries.json");
            if (validationErrors.Count > 0)
            {
                return ApiErrors.BadRequest(string.Join(" ", validationErrors));
            }

            var sourceError = SettingsEndpoints.ValidateLibrarySources(config, folders, organizer);
            if (sourceError is not null)
            {
                return ApiErrors.BadRequest(sourceError);
            }

            configuration.SaveLibraries(config);
            return Results.Ok(SettingsContractMapper.ToContract(config));
        }).Produces<LibrariesConfigurationDto>()
          .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/server-folders/roots", async (
            HttpContext context, ClaimsPrincipal user, SetupSessionService sessions,
            ServerFolderBrowserService service, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(service.GetStorageLocations());
        }).Produces<IReadOnlyList<ServerStorageLocationDto>>();

        group.MapPost("/server-folders/browse", async (
            BrowseServerFoldersRequest request, HttpContext context, ClaimsPrincipal user,
            SetupSessionService sessions, ServerFolderBrowserService service, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            try { return Results.Ok(service.Browse(request)); }
            catch (ServerFolderAccessException exception) { return ApiErrors.BadRequest(exception.Message); }
        }).Produces<BrowseServerFoldersResultDto>();

        group.MapPost("/server-folders/validate", async (
            ValidateServerFolderRequest request, HttpContext context, ClaimsPrincipal user,
            SetupSessionService sessions, ServerFolderBrowserService service, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            try { return Results.Ok(service.Validate(request)); }
            catch (ServerFolderAccessException exception) { return ApiErrors.BadRequest(exception.Message); }
        }).Produces<ServerFolderValidationResultDto>();

        group.MapPut("/providers/{name}/credentials", async (
            string name, ProviderCredentialWriteRequest request,
            HttpContext context, ClaimsPrincipal user, SetupSessionService sessions,
            ProviderCredentialService credentials, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await credentials.SaveAsync(name, request.Credentials, ct).ConfigureAwait(false));
        }).Produces<ProviderCredentialOperationResultDto>();

        group.MapPost("/providers/{name}/credentials/test", async (
            string name, ProviderCredentialWriteRequest request,
            HttpContext context, ClaimsPrincipal user, SetupSessionService sessions,
            ProviderCredentialService credentials, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await credentials.TestAsync(name, request.Credentials, ct).ConfigureAwait(false));
        }).Produces<ProviderCredentialOperationResultDto>();

        group.MapPost("/steps/{stepKey}", async (
            string stepKey, SetupStepDecisionRequest request, HttpContext context, ClaimsPrincipal user,
            SetupSessionService sessions, OnboardingRepository onboarding,
            CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            if (stepKey != "providers" && !(stepKey == "media-locations" && request.Status == "deferred"))
            {
                return ApiErrors.BadRequest("This setup step has a dedicated server-side validator.");
            }

            if (request.Status is not ("passed" or "deferred"))
            {
                return ApiErrors.BadRequest("Optional setup steps may be passed or deferred.");
            }

            await onboarding.SetStepAsync(stepKey, request.Status,
                request.Detail ?? (request.Status == "deferred" ? "Deferred during first-run setup." : "Configured during first-run setup."),
                request.Status == "deferred" ? $"/setup?step={stepKey}" : null, null, ct).ConfigureAwait(false);
            return Results.Ok(await sessions.GetStatusAsync(ct).ConfigureAwait(false));
        }).Produces<SetupStatusDto>();

        group.MapPost("/restore/upload", async (
            HttpRequest request, HttpContext context, ClaimsPrincipal user, SetupSessionService sessions,
            DatabaseBackupService backups, OnboardingRepository onboarding, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            if (!request.HasFormContentType)
            {
                return ApiErrors.BadRequest("Upload the backup as multipart form data.");
            }

            try
            {
                var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
                var file = form.Files.GetFile("backup");
                if (file is null || file.Length == 0)
                {
                    return ApiErrors.BadRequest("Choose a Tuvima backup ZIP file.");
                }

                await using var stream = file.OpenReadStream();
                return Results.Ok(await backups.UploadAndInspectAsync(stream, file.FileName, onboarding, ct).ConfigureAwait(false));
            }
            catch (InvalidDataException ex) { return ApiErrors.BadRequest(ex.Message); }
            catch (IOException ex) { return ApiErrors.BadRequest($"The backup could not be staged: {ex.Message}"); }
        }).Produces<SetupBackupInspectionDto>()
            .WithMetadata(
                new RequestSizeLimitAttribute(2L * 1024 * 1024 * 1024),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024 })
            .DisableAntiforgery();

        group.MapPost("/restore/{operationId:guid}/confirm", async (
            Guid operationId, HttpContext context, ClaimsPrincipal user, SetupSessionService sessions,
            DatabaseBackupService backups, OnboardingRepository onboarding,
            IHostApplicationLifetime lifetime, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            try
            {
                var result = backups.ConfirmUploadedRestore(operationId, onboarding);
                if (string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                        lifetime.StopApplication();
                    });
                }
                return Results.Ok(new SetupRestoreConfirmationDto(result.Scheduled, result.RestartRequired, result.Message));
            }
            catch (FileNotFoundException ex) { return ApiErrors.NotFound(ex.Message); }
            catch (InvalidDataException ex) { return ApiErrors.BadRequest(ex.Message); }
            catch (InvalidOperationException ex) { return ApiErrors.BadRequest(ex.Message); }
        }).Produces<SetupRestoreConfirmationDto>();

        group.MapGet("/readiness", async (
            HttpContext context, ClaimsPrincipal user, SetupSessionService sessions,
            OnboardingRepository onboarding, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(BuildReadiness(onboarding.Get()));
        }).Produces<SetupReadinessDto>();

        group.MapPost("/complete", async (
            HttpContext context, ClaimsPrincipal user, SetupSessionService sessions,
            OnboardingRepository onboarding, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(context, user, sessions, ct).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            return await onboarding.CompleteAsync(ct).ConfigureAwait(false)
                ? Results.Ok(await sessions.GetStatusAsync(ct).ConfigureAwait(false))
                : ApiErrors.Conflict("Required setup capabilities are still blocked.");
        }).Produces<SetupStatusDto>();

        return app;
    }

    private static async Task<bool> AuthorizedAsync(
        HttpContext context, ClaimsPrincipal user, SetupSessionService sessions, CancellationToken ct)
    {
        _ = user;
        if (await sessions.ValidateSessionAsync(
                context.Request.Headers[SetupSessionService.SessionHeader].ToString(), ct)
                .ConfigureAwait(false))
        {
            return true;
        }

        var resolver = context.RequestServices.GetRequiredService<IRequestAuthorityResolver>();
        var decisions = context.RequestServices.GetRequiredService<IAccountAccessDecisionService>();
        var authority = await resolver.ResolveAsync(context, ct).ConfigureAwait(false);
        return (await decisions.EvaluateAdministratorAsync(authority, true, ct)
            .ConfigureAwait(false)).IsAllowed;
    }

    private static SetupReadinessDto BuildReadiness(OnboardingWorkflowRecord workflow)
    {
        var required = new HashSet<string>(["preflight", "administrator"], StringComparer.Ordinal);
        var capabilities = workflow.Steps.Select(step => new SetupCapabilityDto(
            step.Key,
            step.Key.Replace('-', ' '),
            step.Status,
            required.Contains(step.Key),
            step.Detail ?? "Not checked yet.",
            step.RepairTarget ?? (step.Status is "passed" ? null : $"/setup?step={step.Key}"))).ToList();
        var canComplete = capabilities
            .Where(capability => capability.Key != "readiness")
            .All(capability => capability.Required
                ? capability.Status == "passed"
                : capability.Status is "passed" or "deferred");
        return new SetupReadinessDto(canComplete, capabilities);
    }
}
