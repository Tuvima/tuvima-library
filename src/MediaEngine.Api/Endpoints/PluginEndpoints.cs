using System.Text.Json;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Api.Services.LocalAi;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.AiFeatures;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Contracts.Operations;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Events;
using MediaEngine.Plugins;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

internal static class PluginEndpoints
{
    internal static RouteGroupBuilder MapPluginEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/plugins")
            .WithTags("Plugins");

        group.MapGet("", async (
            PluginCatalog catalog,
            [FromServices] IConfigSecretsStore secrets,
            [FromServices] PluginHostServicesReader hostServices,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            var summaries = new List<PluginSummaryResponse>();
            foreach (var registration in catalog.List())
            {
                summaries.Add(ToDto(
                    registration,
                    await CountConfiguredSecretsAsync(registration, secrets, loggers, ct).ConfigureAwait(false),
                    await hostServices.DescribeAsync(registration, ct).ConfigureAwait(false)));
            }

            return Results.Ok(summaries);
        })
            .WithName("ListPlugins")
            .Produces<IEnumerable<PluginSummaryResponse>>(StatusCodes.Status200OK)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsRead);

        // The plugin directory (Available plugins): the saved copy of Tuvima's list on GitHub. Reading may check GitHub when the
        // last check is over a day old; the actions below change what is remembered or switched, so they are for server
        // administrators only.
        group.MapGet("/directory", async (
            HttpContext http,
            [FromServices] IRequestAuthorityResolver authorityResolver,
            PluginDirectoryService directory,
            PluginCatalog catalog,
            CancellationToken ct) =>
            Results.Ok(await ReadDirectoryAsync(http, authorityResolver, directory, InstalledPluginIds(catalog), ct).ConfigureAwait(false)))
            .WithName("GetPluginDirectory")
            .WithSummary("The saved copy of the plugin directory. For a signed-in administrator it checks GitHub first when the switch is on and the last check is over a day old; any other caller (an outside application) gets the saved copy only.")
            .Produces<PluginDirectoryResponse>(StatusCodes.Status200OK)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsRead);

        group.MapPost("/directory/check", async (PluginDirectoryService directory, PluginCatalog catalog, CancellationToken ct) =>
            Results.Ok(await directory.CheckNowAsync(InstalledPluginIds(catalog), ct).ConfigureAwait(false)))
            .WithName("CheckPluginDirectory")
            .WithSummary("Check now: ask GitHub for the plugin directory. Makes no request when the switch is off.")
            .Produces<PluginDirectoryResponse>(StatusCodes.Status200OK)
            .RequireEffectiveAdministrator();

        group.MapPost("/directory/seen", async (PluginDirectoryService directory, PluginCatalog catalog, CancellationToken ct) =>
            Results.Ok(await directory.MarkSeenAsync(InstalledPluginIds(catalog), ct).ConfigureAwait(false)))
            .WithName("MarkPluginDirectorySeen")
            .WithSummary("Remember that the listed plugins have been seen, so none of them counts as new next time.")
            .Produces<PluginDirectoryResponse>(StatusCodes.Status200OK)
            .RequireEffectiveAdministrator();

        group.MapPut("/directory/settings", async (
            PluginDirectorySettingsRequest request,
            PluginDirectoryService directory,
            PluginCatalog catalog,
            CancellationToken ct) =>
        {
            directory.SetEnabled(request.Enabled);
            return Results.Ok(await directory.GetSavedAsync(InstalledPluginIds(catalog), ct).ConfigureAwait(false));
        })
            .WithName("SavePluginDirectorySettings")
            .WithSummary("Switch the plugin directory on or off. Changes only that one setting.")
            .Produces<PluginDirectoryResponse>(StatusCodes.Status200OK)
            .RequireEffectiveAdministrator();

        group.MapPost("/directory/{pluginId}/approval-preview", async (string pluginId, PluginDirectoryService directory, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await directory.PreviewInstallAsync(pluginId, ct).ConfigureAwait(false));
            }
            catch (KeyNotFoundException ex)
            {
                return ApiErrors.NotFound(ex.Message);
            }
        })
            .WithName("PreviewPluginDirectoryInstall")
            .WithSummary("What a person is shown before installing a listed plugin, built from what the list declares for it.")
            .Produces<ApprovalPreviewResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireEffectiveAdministrator();

        group.MapGet("/{pluginId}", async (
            string pluginId,
            PluginCatalog catalog,
            [FromServices] IConfigSecretsStore secrets,
            [FromServices] PluginHostServicesReader hostServices,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            var plugin = catalog.Get(pluginId);
            return plugin is null
                ? ApiErrors.NotFound($"Plugin '{pluginId}' not found.")
                : Results.Ok(ToDto(
                    plugin,
                    await CountConfiguredSecretsAsync(plugin, secrets, loggers, ct).ConfigureAwait(false),
                    await hostServices.DescribeAsync(plugin, ct).ConfigureAwait(false)));
        })
        .WithName("GetPlugin")
        .Produces<PluginSummaryResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsRead);

        group.MapPost("/{pluginId}/enable", EnablePlugin)
        .WithName("EnablePlugin")
        .WithSummary("Switch a plugin on. Needs the plugin's current approval statement to have been confirmed (409 approval_required otherwise).")
        .Produces<PluginEnabledResponse>(StatusCodes.Status200OK)
        .Produces<ApprovalRequiredResponse>(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsManage);

        group.MapPost("/{pluginId}/disable", async (string pluginId, PluginCatalog catalog, AiFeatureRetractionService retraction, CancellationToken ct) =>
        {
            catalog.SetEnabled(pluginId, false);

            // An AI feature's suggestions go away with its plugin (D-T); provider and person values stay.
            await retraction.RetractForPluginAsync(pluginId, ct);
            return Results.Ok(new PluginEnabledResponse { plugin_id = pluginId, enabled = false });
        })
        .WithName("DisablePlugin")
        .Produces<PluginEnabledResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsManage);

        group.MapPut("/{pluginId}/settings", (
            string pluginId,
            Dictionary<string, JsonElement> settings,
            PluginCatalog catalog) =>
        {
            catalog.SaveSettings(pluginId, settings);
            return Results.Ok(new PluginSavedResponse { plugin_id = pluginId, saved = true });
        })
        .WithName("SavePluginSettings")
        .Produces<PluginSavedResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsManage);

        group.MapGet("/{pluginId}/manifest", (string pluginId, PluginCatalog catalog) =>
        {
            try
            {
                return Results.Ok(new PluginManifestJsonResponse { plugin_id = pluginId, json = catalog.GetManifestJson(pluginId) });
            }
            catch (InvalidOperationException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("GetPluginManifestJson")
        .Produces<PluginManifestJsonResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsRead);

        group.MapPut("/{pluginId}/manifest", (
            string pluginId,
            PluginJsonUpdateRequest request,
            PluginCatalog catalog) =>
        {
            try
            {
                catalog.SaveManifestJson(pluginId, request.Json);
                return Results.Ok(new PluginSavedResponse { plugin_id = pluginId, saved = true });
            }
            catch (JsonException ex)
            {
                return ApiErrors.BadRequest($"Plugin manifest JSON is invalid: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("SavePluginManifestJson")
        .Produces<PluginSavedResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsManage);

        group.MapDelete("/{pluginId}", DeletePluginAsync)
        .WithName("DeletePlugin")
        .Produces<PluginDeletedResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsManage);

        group.MapGet("/{pluginId}/secrets", ListPluginSecretsAsync)
        .WithName("ListPluginSecrets")
        .Produces<IReadOnlyList<PluginSecretStatusResponse>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        group.MapPut("/{pluginId}/secrets/{name}", SetPluginSecretAsync)
        .WithName("SetPluginSecret")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        group.MapDelete("/{pluginId}/secrets/{name}", RemovePluginSecretAsync)
        .WithName("RemovePluginSecret")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        group.MapPost("/{pluginId}/health", async (
            string pluginId,
            PluginCatalog catalog,
            IPluginExecutionContextFactory contexts,
            PluginAiBudget aiBudget,
            [FromServices] PluginHostServicesReader hostServices,
            CancellationToken ct) =>
        {
            var plugin = catalog.Get(pluginId);
            if (plugin is null)
            {
                return ApiErrors.NotFound($"Plugin '{pluginId}' not found.");
            }

            if (!plugin.Enabled || plugin.LoadError is not null)
            {
                return ApiErrors.BadRequest("The plugin is disabled or unavailable.");
            }

            await using var context = contexts.Create(plugin.Manifest.Id, "health");
            var checks = new List<PluginHealthResult>();
            foreach (var check in plugin.Capabilities.OfType<IPluginHealthCheck>())
            {
                checks.Add(await check.GetHealthAsync(context, ct).ConfigureAwait(false));
            }

            return Results.Ok(await PluginHealthComposer
                .ComposeAsync(plugin.Manifest.Id, checks, aiBudget, ct, await hostServices.DescribeAsync(plugin, ct).ConfigureAwait(false))
                .ConfigureAwait(false));
        })
        .WithName("CheckPluginHealth")
        .Produces<PluginHealthResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsRead);

        group.MapGet("/{pluginId}/jobs", async (
            string pluginId,
            IMediaOperationRepository operations,
            CancellationToken ct) =>
        {
            var jobs = await operations.GetByPluginAsync(pluginId, 200, ct).ConfigureAwait(false);
            return Results.Ok(jobs.Select((op, index) => OperationsEndpoints.MapOperation(op, index + 1)).ToList());
        })
            .WithName("GetPluginJobs")
            .Produces<IReadOnlyList<OperationDto>>(StatusCodes.Status200OK)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsJobsRead);

        group.MapPost("/jobs/segment-detection/run", async (
            PluginScheduledSegmentService scheduler,
            IApplicationEventProducer eventProducer,
            CancellationToken ct) =>
        {
            var subject = new ApplicationEventSubject("plugin-job", "segment-detection");
            await eventProducer.PublishAsync(new(
                "plugin.job_started", 1, DateTimeOffset.UtcNow, subject,
                JsonSerializer.SerializeToElement(new { job = "segment-detection", status = "started" })), ct).ConfigureAwait(false);
            var jobs = await scheduler.RunScheduledPassAsync(ct).ConfigureAwait(false);
            await eventProducer.PublishAsync(new(
                "plugin.job_completed", 1, DateTimeOffset.UtcNow, subject,
                JsonSerializer.SerializeToElement(new { job = "segment-detection", status = "completed", item_count = jobs.Count })), ct).ConfigureAwait(false);
            return Results.Ok(jobs);
        })
        .WithName("RunPluginSegmentDetectionJobs")
        .Produces<IReadOnlyList<PluginJobSnapshot>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsJobsRun);

        return group;
    }

    private const int MaximumSecretValueLength = 8192;

    /// <summary>Uninstalls a plugin; its saved settings and its keys and logins go with it.</summary>
    internal static async Task<IResult> DeletePluginAsync(
        string pluginId,
        PluginCatalog catalog,
        [FromServices] IConfigSecretsStore secrets,
        CancellationToken ct)
    {
        try
        {
            catalog.DeletePlugin(pluginId);
        }
        catch (InvalidOperationException ex)
        {
            return ApiErrors.BadRequest(ex.Message);
        }

        await RemoveAllPluginSecretsAsync(pluginId, secrets, ct).ConfigureAwait(false);
        return Results.Ok(new PluginDeletedResponse { plugin_id = pluginId, deleted = true });
    }

    /// <summary>Which declared secrets are set. Names, labels and a yes/no only; never a value.</summary>
    internal static async Task<IResult> ListPluginSecretsAsync(
        string pluginId,
        PluginCatalog catalog,
        [FromServices] IConfigSecretsStore secrets,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var plugin = catalog.Get(pluginId);
        if (plugin is null)
        {
            return ApiErrors.NotFound($"Plugin '{pluginId}' not found.");
        }

        if (!TryPluginSecretOwner(plugin, out var owner, out var badId))
        {
            return badId!;
        }

        try
        {
            var configured = plugin.Manifest.Secrets.Count == 0
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : (await secrets.ListNamesAsync(owner, ct).ConfigureAwait(false))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Results.Ok(plugin.Manifest.Secrets.Select(secret => new PluginSecretStatusResponse
            {
                Name = secret.Name,
                Label = secret.Label,
                Description = secret.Description,
                Configured = configured.Contains(secret.Name),
            }).ToList());
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return PluginSecretsUnavailable(plugin.Manifest.Id, ex, loggers);
        }
    }

    internal static async Task<IResult> SetPluginSecretAsync(
        string pluginId,
        string name,
        PluginSecretWriteRequest request,
        PluginCatalog catalog,
        [FromServices] IConfigSecretsStore secrets,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var plugin = catalog.Get(pluginId);
        if (plugin is null)
        {
            return ApiErrors.NotFound($"Plugin '{pluginId}' not found.");
        }

        var declared = plugin.Manifest.Secrets.FirstOrDefault(secret =>
            string.Equals(secret.Name, name, StringComparison.OrdinalIgnoreCase));
        if (declared is null)
        {
            return ApiErrors.BadRequest($"Plugin '{plugin.Manifest.Id}' does not declare a secret named '{name}'.");
        }

        if (string.IsNullOrWhiteSpace(request?.Value))
        {
            return ApiErrors.BadRequest("Enter a value to save.");
        }

        if (request.Value.Length > MaximumSecretValueLength)
        {
            return ApiErrors.BadRequest($"The value can be at most {MaximumSecretValueLength:N0} characters.");
        }

        if (!TryPluginSecretOwner(plugin, out var owner, out var badId))
        {
            return badId!;
        }

        try
        {
            await secrets.SetAsync(
                owner,
                new Dictionary<string, string> { [declared.Name] = request.Value },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return PluginSecretsUnavailable(plugin.Manifest.Id, ex, loggers);
        }

        // Owner and secret name only; the value is never logged.
        loggers.CreateLogger(typeof(PluginEndpoints).FullName!).LogInformation(
            "Plugin secret {SecretName} was set for plugin {PluginId}", declared.Name, plugin.Manifest.Id);
        return Results.NoContent();
    }

    internal static async Task<IResult> RemovePluginSecretAsync(
        string pluginId,
        string name,
        PluginCatalog catalog,
        [FromServices] IConfigSecretsStore secrets,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var plugin = catalog.Get(pluginId);
        if (plugin is null)
        {
            return ApiErrors.NotFound($"Plugin '{pluginId}' not found.");
        }

        if (!TryPluginSecretOwner(plugin, out var owner, out var badId))
        {
            return badId!;
        }

        try
        {
            await secrets.RemoveAsync(owner, name, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return PluginSecretsUnavailable(plugin.Manifest.Id, ex, loggers);
        }

        loggers.CreateLogger(typeof(PluginEndpoints).FullName!).LogInformation(
            "Plugin secret {SecretName} was removed for plugin {PluginId}", name, plugin.Manifest.Id);
        return Results.NoContent();
    }

    // A plugin id the secrets store cannot turn into a file name (anything outside lowercase letters, digits, '-', '_', '.').
    private static bool TryPluginSecretOwner(PluginRegistration plugin, out SecretOwner owner, out IResult? problem)
    {
        try
        {
            owner = SecretOwner.Plugin(plugin.Manifest.Id);
            problem = null;
            return true;
        }
        catch (ArgumentException)
        {
            owner = default;
            problem = ApiErrors.BadRequest("This plugin's id cannot be used to keep keys and logins. Ask the plugin's author for a fixed version.");
            return false;
        }
    }

    // The secrets file is unreadable or damaged. Plugin id and error type only; never a value.
    private static IResult PluginSecretsUnavailable(string pluginId, Exception ex, ILoggerFactory loggers)
    {
        loggers.CreateLogger(typeof(PluginEndpoints).FullName!).LogWarning(
            "Secrets for plugin {PluginId} could not be read or saved ({ErrorType})", pluginId, ex.GetType().Name);
        return ApiErrors.Conflict("This plugin's keys and logins could not be read or saved. Check that the server's config folder is readable and writable, then try again.");
    }

    private static async Task RemoveAllPluginSecretsAsync(string pluginId, IConfigSecretsStore secrets, CancellationToken ct)
    {
        SecretOwner owner;
        try
        {
            owner = SecretOwner.Plugin(pluginId);
        }
        catch (ArgumentException)
        {
            // An id that cannot name a secrets file never had one.
            return;
        }

        await secrets.RemoveAllAsync(owner, ct).ConfigureAwait(false);
    }

    internal static async Task<int> CountConfiguredSecretsAsync(
        PluginRegistration registration,
        IConfigSecretsStore secrets,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (registration.Manifest.Secrets.Count == 0)
        {
            return 0;
        }

        try
        {
            var configured = (await secrets.ListNamesAsync(SecretOwner.Plugin(registration.Manifest.Id), ct).ConfigureAwait(false))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return registration.Manifest.Secrets.Count(secret => configured.Contains(secret.Name));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            loggers.CreateLogger(typeof(PluginEndpoints).FullName!).LogWarning(
                "Secrets for plugin {PluginId} could not be read ({ErrorType}); showing none as configured",
                registration.Manifest.Id, ex.GetType().Name);
            return 0;
        }
    }

    /// <summary>
    /// Switches a plugin on. Nothing switches on until an administrator has seen what the plugin can do (the approval rule):
    /// with a record for the plugin's current statement it is enabled straight away; otherwise the body must carry that
    /// statement's hash (the person confirmed it), which is recorded first. Anything else is an approval-required 409.
    /// A plugin that cannot work here keeps its own plain-English 409 and is never asked about.
    /// </summary>
    internal static async Task<IResult> EnablePlugin(
        string pluginId,
        [FromBody] PluginEnableRequest? request,
        HttpContext http,
        PluginCatalog catalog,
        [FromServices] ApprovalService approvals,
        [FromServices] IRequestAuthorityResolver authorityResolver,
        CancellationToken ct)
    {
        var registration = catalog.Get(pluginId);
        if (registration is null)
        {
            return ApiErrors.NotFound($"Plugin '{pluginId}' not found.");
        }

        if (registration.Compatibility.IsCompatible)
        {
            var preview = approvals.PreviewPlugin(registration);
            if (!await approvals.IsCoveredAsync(ApprovalKinds.Plugin, registration.Manifest.Id, preview.StatementHash, ct).ConfigureAwait(false))
            {
                if (!ApprovalService.HashMatches(request?.ApprovalHash, preview.StatementHash))
                {
                    return ApiErrors.ApprovalRequired([preview]);
                }

                var authority = await authorityResolver.ResolveAsync(http, ct).ConfigureAwait(false);
                var actor = await approvals.ResolveActorAsync(authority, ct).ConfigureAwait(false);
                await approvals.RecordAsync(preview, ApprovalKinds.Plugin, registration.Manifest.Id, actor, ct).ConfigureAwait(false);
            }
        }

        try
        {
            catalog.SetEnabled(pluginId, true);
            return Results.Ok(new PluginEnabledResponse { plugin_id = pluginId, enabled = true });
        }
        catch (PluginCannotBeEnabledException ex)
        {
            // The plain-English reason (Library version, platform, plugin kind or load problem) is the detail.
            return ApiErrors.Conflict(ex.Message);
        }
    }

    private static string PluginKindName(PluginKind kind) => kind switch
    {
        PluginKind.Official => "official",
        PluginKind.Declarative => "declarative",
        PluginKind.Service => "service",
        PluginKind.Sandboxed => "sandboxed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown plugin kind."),
    };

    private static string PluginTrustName(PluginTrustLevel trust) => trust switch
    {
        PluginTrustLevel.Official => "official",
        PluginTrustLevel.Verified => "verified",
        PluginTrustLevel.Community => "community",
        PluginTrustLevel.Private => "private",
        _ => throw new ArgumentOutOfRangeException(nameof(trust), trust, "Unknown plugin trust level."),
    };

    // The ids of the plugins on this server, so the directory can mark the ones already installed.
    /// <summary>
    /// Only a signed-in server administrator's read may start the once-a-day check of GitHub; an outside application that
    /// holds <c>plugins.read</c> gets the saved copy and never causes a request to leave the machine.
    /// </summary>
    internal static async Task<PluginDirectoryResponse> ReadDirectoryAsync(
        HttpContext http,
        IRequestAuthorityResolver authorityResolver,
        PluginDirectoryService directory,
        IReadOnlyCollection<string> installedIds,
        CancellationToken ct)
    {
        var authority = await authorityResolver.ResolveAsync(http, ct).ConfigureAwait(false);
        return authority.IsEffectiveAdministrator
            ? await directory.GetAsync(installedIds, ct).ConfigureAwait(false)
            : await directory.GetSavedAsync(installedIds, ct).ConfigureAwait(false);
    }

    private static string[] InstalledPluginIds(PluginCatalog catalog) =>
        catalog.List().Select(registration => registration.Manifest.Id).ToArray();

    internal static PluginSummaryResponse ToDto(PluginRegistration registration, int configuredSecrets = 0, PluginHostServicesDto? hostServices = null) => new()
    {
        Id = registration.Manifest.Id,
        Name = registration.Manifest.Name,
        Version = registration.Manifest.Version,
        Description = registration.Manifest.Description,
        Enabled = registration.Enabled,
        IsBuiltIn = registration.IsBuiltIn,
        NeedsApproval = registration.NeedsApproval,
        LoadError = registration.LoadError,
        Kind = PluginKindName(registration.Manifest.Kind),
        TrustLevel = PluginTrustName(registration.Trust),
        MinimumLibraryVersion = registration.Manifest.MinimumLibraryVersion,
        MaximumLibraryVersion = registration.Manifest.MaximumLibraryVersion,
        SupportedPlatforms = registration.Manifest.SupportedPlatforms.ToList(),
        IsCompatible = registration.Compatibility.IsCompatible,
        CompatibilityProblem = registration.Compatibility.Problem,
        Capabilities = registration.Manifest.Capabilities.Select(capability => new PluginCapabilityDto
        {
            Kind = capability.Kind,
            Name = capability.Name,
            Description = capability.Description,
        }).ToList(),
        Permissions = registration.Manifest.Permissions.ToList(),
        ToolRequirements = registration.Manifest.ToolRequirements.Select(requirement => new PluginToolRequirementDto
        {
            Id = requirement.Id,
            Version = requirement.Version,
            ExecutableName = requirement.ExecutableName,
            License = requirement.License,
            SourceUrl = requirement.SourceUrl,
            Platforms = requirement.Platforms.Select(platform => new PluginToolPlatformDto
            {
                Rid = platform.Rid,
                DownloadUrl = platform.DownloadUrl,
                Sha256 = platform.Sha256,
                RelativeExecutablePath = platform.RelativeExecutablePath,
            }).ToList(),
        }).ToList(),
        AiPermissions = registration.Manifest.AiPermissions.Select(permission => new PluginAiPermissionDto
        {
            Role = permission.Role,
            MaxTokens = permission.MaxTokens,
            Schedule = permission.Schedule,
            ResourceClass = permission.ResourceClass,
        }).ToList(),
        AllowedTo = ApprovalStatementBuilder.PluginAllowedTo(registration.Manifest).ToList(),
        SendsDataTo = ApprovalStatementBuilder.PluginSendsDataTo(registration.Manifest),
        NetworkDestinations = registration.Manifest.Network.Select(destination => new PluginNetworkDestinationDto
        {
            Host = destination.Host,
            Sends = destination.Sends,
        }).ToList(),
        DeclaredSecrets = registration.Manifest.Secrets.Count,
        ConfiguredSecrets = configuredSecrets,
        Settings = registration.Settings.ToDictionary(
            setting => setting.Key,
            setting => setting.Value,
            StringComparer.OrdinalIgnoreCase),
        SettingsSchema = registration.SettingsSchema,
        ManifestPath = registration.ManifestPath,
        HostServices = hostServices ?? new PluginHostServicesDto(),
    };
}
