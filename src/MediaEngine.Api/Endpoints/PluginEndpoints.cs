using System.Text.Json;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Api.Services.LocalAi;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.AiFeatures;
using MediaEngine.Api.Services.Plugins.Declarative;
using MediaEngine.Api.Services.Plugins.Service;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Contracts.Operations;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Events;
using MediaEngine.Plugins;
using Microsoft.AspNetCore.Http.Features;
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
            foreach (var registration in catalog.List().Where(registration => !registration.Manifest.Hidden))
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

        group.MapPost("/directory/{pluginId}/install", async ([FromServices] PluginPackageInstaller installer, string pluginId, CancellationToken ct) =>
        {
            if (!PluginDirectoryService.InstallSupported)
            {
                return ApiErrors.BadRequest("Installing plugins from the directory is not switched on yet.");
            }

            try
            {
                return Results.Ok(await installer.InstallAsync(pluginId, ct).ConfigureAwait(false));
            }
            catch (KeyNotFoundException ex)
            {
                return ApiErrors.NotFound(ex.Message);
            }
            catch (PluginInstallException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
            .WithName("InstallDirectoryPlugin")
            .WithSummary("Install, or update to the listed version, a plugin from the plugin directory. The package is downloaded from GitHub, checked against the directory's checksum (and, for verified plugins, the publisher's signature) and unpacked safely. The plugin is left switched off; an update that asks for more than was approved leaves it off until approved again.")
            .Produces<PluginInstallResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireEffectiveAdministrator();

        // Upload plugin (spec D5/D11): a JSON-only plugin from a file on the administrator's own computer. Never delegated to an
        // outside application or paired app, because it puts a new file in the plugins folder.
        group.MapPost("/upload", async (
            HttpContext http,
            IFormFile file,
            [FromServices] PluginPackageInstaller installer,
            [FromServices] IRequestAuthorityResolver authorityResolver,
            [FromServices] ISystemActivityRepository activity,
            CancellationToken ct) =>
        {
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
            {
                bodyLimit.MaxRequestBodySize = PluginPackageRules.MaxPackageBytes + (1024 * 1024);
            }

            if (file.Length is <= 0 or > PluginPackageRules.MaxPackageBytes)
            {
                return ApiErrors.BadRequest("The file is empty or larger than allowed.");
            }

            try
            {
                byte[] package;
                await using (var input = file.OpenReadStream())
                using (var buffer = new MemoryStream())
                {
                    await input.CopyToAsync(buffer, ct).ConfigureAwait(false);
                    package = buffer.ToArray();
                }

                var result = await installer.UploadAsync(package, ct).ConfigureAwait(false);
                var authority = await authorityResolver.ResolveAsync(http, ct).ConfigureAwait(false);
                await activity.LogAsync(new SystemActivityEntry
                {
                    OccurredAt = DateTimeOffset.UtcNow,
                    ActionType = SystemActionType.PluginUploaded,
                    EntityType = "Plugin",
                    ProfileId = authority.ActiveProfileId,
                    Detail = $"Uploaded plugin {result.Name} {result.Version} ({result.PluginId}) as a Private plugin, switched off.",
                }, ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (PluginInstallException ex)
            {
                // A refused upload is the entry worth reading later, so it is logged too.
                var refusedBy = await authorityResolver.ResolveAsync(http, ct).ConfigureAwait(false);
                await activity.LogAsync(new SystemActivityEntry
                {
                    OccurredAt = DateTimeOffset.UtcNow,
                    ActionType = SystemActionType.PluginUploaded,
                    EntityType = "Plugin",
                    ProfileId = refusedBy.ActiveProfileId,
                    Detail = $"A plugin upload was refused: {ex.Message}",
                }, ct).ConfigureAwait(false);
                return ApiErrors.BadRequest(ex.Message);
            }
        })
            .WithName("UploadPlugin")
            .WithSummary("Add a JSON-only plugin from a package file. The package is checked with the install rules (size, safe unpacking, a valid description, only plugin.json and plain notes or pictures); it is stored as a Private plugin, switched off, and must still be approved before it can be switched on. Plugins that run code are refused.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<PluginInstallResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .DisableAntiforgery()
            .RequireEffectiveAdministratorInPerson();

        group.MapGet("/{pluginId}/activity", async (
            string pluginId,
            [FromServices] PluginActivityService activity,
            [FromServices] ProviderCapabilityCatalogue sources,
            PluginCatalog catalog,
            int? limit,
            bool? failuresOnly,
            CancellationToken ct) =>
        {
            if (!IsPluginOrSource(pluginId, catalog, sources))
            {
                return ApiErrors.NotFound($"Plugin '{pluginId}' not found.");
            }

            return Results.Ok(await activity.GetAsync(pluginId, PagedRequest.From(null, limit, defaultLimit: 50).Limit, failuresOnly ?? false, ct).ConfigureAwait(false));
        })
            .WithName("GetPluginActivity")
            .WithSummary("What a plugin or source did, when, how many items changed, and every failure with its cause. Newest first.")
            .Produces<PluginActivityResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsRead);

        group.MapGet("/people-actions", async (
            [FromServices] PluginPeopleActionLog peopleActions,
            string? plugin,
            Guid? profileId,
            string? action,
            int? limit,
            CancellationToken ct) =>
            Results.Ok(await peopleActions.GetAsync(plugin, profileId, action, PagedRequest.From(null, limit, defaultLimit: 100).Limit, ct).ConfigureAwait(false)))
            .WithName("GetPluginPeopleActions")
            .WithSummary("What people did with plugins and sources that changed shared Library data, newest first. Administrators only.")
            .Produces<PluginPeopleActionsResponse>(StatusCodes.Status200OK)
            .RequireEffectiveAdministrator();

        group.MapGet("/{pluginId}/dependencies", (string pluginId, PluginCatalog catalog) =>
        {
            var registration = catalog.Get(pluginId);
            if (registration is null)
            {
                return ApiErrors.NotFound($"Plugin '{pluginId}' not found.");
            }

            var all = catalog.List();
            return Results.Ok(new PluginDependenciesResponse
            {
                PluginId = registration.Manifest.Id,
                WaitingFor = PluginDependencyGraph.WaitingFor(registration, all),
                Needs = PluginDependencyGraph.Needs(registration, all)
                    .Select(need => new PluginNeedDto
                    {
                        Id = need.Id,
                        Name = need.Name,
                        Optional = need.Optional,
                        VersionRange = need.VersionRange,
                        State = DependencyStateWord(need.State),
                    })
                    .ToList(),
                NeededBy = PluginDependencyGraph.Dependants(registration.Manifest.Id, all)
                    .Select(plugin => new PluginNeededByDto { Id = plugin.Manifest.Id, Name = plugin.Manifest.Name })
                    .ToList(),
            });
        })
            .WithName("GetPluginDependencies")
            .WithSummary("What a plugin needs from other plugins (and whether each is ready), who needs it, and a plain \"Waiting for X\" when a required plugin is off, missing or at the wrong version.")
            .Produces<PluginDependenciesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsRead);

        group.MapGet("/{pluginId}/libraries", async (
            string pluginId,
            [FromServices] PluginLibraryLimitService limits,
            [FromServices] ProviderCapabilityCatalogue sources,
            PluginCatalog catalog,
            CancellationToken ct) =>
        {
            if (!IsPluginOrSource(pluginId, catalog, sources))
            {
                return ApiErrors.NotFound($"Plugin '{pluginId}' not found.");
            }

            return Results.Ok(await limits.GetAsync(pluginId, ct).ConfigureAwait(false));
        })
            .WithName("GetPluginLibraryLimit")
            .WithSummary("Which libraries a plugin or source works on: every library (the default) or only the ones an administrator chose. Vault libraries are never listed.")
            .Produces<PluginLibraryLimitResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsRead);

        group.MapPut("/{pluginId}/libraries", async (
            string pluginId,
            PluginLibraryLimitRequest request,
            HttpContext http,
            [FromServices] PluginLibraryLimitService limits,
            [FromServices] ProviderCapabilityCatalogue sources,
            [FromServices] PluginPeopleActionLog peopleActions,
            [FromServices] IRequestAuthorityResolver authorityResolver,
            PluginCatalog catalog,
            CancellationToken ct) =>
        {
            if (!IsPluginOrSource(pluginId, catalog, sources))
            {
                return ApiErrors.NotFound($"Plugin '{pluginId}' not found.");
            }

            var before = await limits.GetAsync(pluginId, ct).ConfigureAwait(false);
            var refused = await limits.SetAsync(pluginId, request.LibraryIds ?? [], ct).ConfigureAwait(false);
            if (refused is not null)
            {
                return ApiErrors.BadRequest(refused);
            }

            var after = await limits.GetAsync(pluginId, ct).ConfigureAwait(false);
            var authority = await authorityResolver.ResolveAsync(http, ct).ConfigureAwait(false);
            await peopleActions.RecordAsync(new PluginPeopleAction(
                authority.ActiveProfileId, pluginId, "limited-plugin-libraries", $"Changed which libraries {pluginId} works on",
                Before: DescribeLimit(before), After: DescribeLimit(after)), ct).ConfigureAwait(false);
            return Results.Ok(after);
        })
            .WithName("SetPluginLibraryLimit")
            .WithSummary("Limit a plugin or source to chosen libraries. An empty list removes the limit. A Vault library or an unknown one is refused.")
            .Produces<PluginLibraryLimitResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsManage);

        group.MapGet("/{pluginId}/removal-impact", (string pluginId, PluginCatalog catalog) =>
        {
            if (catalog.Get(pluginId) is null)
            {
                return ApiErrors.NotFound($"Plugin '{pluginId}' not found.");
            }

            return Results.Ok(new PluginRemovalImpactResponse
            {
                PluginId = pluginId,
                WouldLose = catalog.DescribeRemovalImpact(pluginId).ToList(),
                Keeps = "Anything this plugin already saved to your Library stays.",
            });
        })
            .WithName("GetPluginRemovalImpact")
            .WithSummary("What would have no source if the plugin were removed. Saved metadata is always kept.")
            .Produces<PluginRemovalImpactResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsRead);

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

        group.MapPost("/{pluginId}/disable", DisablePlugin)
        .WithName("DisablePlugin")
        .Produces<PluginEnabledResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsManage);

        group.MapPost("/{pluginId}/ai-steps/{stepKey}/run", async (
            string pluginId,
            string stepKey,
            PluginAiStepRunRequest request,
            [FromServices] DeclarativeAiStepRunner runner,
            CancellationToken ct) =>
        {
            var result = await runner.RunAsync(pluginId, stepKey, request.inputs, ct);
            return result is null
                ? ApiErrors.NotFound($"Plugin '{pluginId}' has no AI step '{stepKey}'.")
                : Results.Ok(new PluginAiStepRunResponse
                {
                    plugin_id = pluginId,
                    step = stepKey,
                    status = AiStatusWord(result.Status),
                    message = result.Message,
                    text = result.Text,
                });
        })
        .WithName("RunPluginAiStep")
        .WithSummary("Run an AI step a JSON-only plugin declared. The plugin must be switched on and approved; Local AI answers as that plugin.")
        .Produces<PluginAiStepRunResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsManage);

        group.MapPost("/{pluginId}/service/lookup", async (
            string pluginId,
            HttpContext http,
            PluginCatalog catalog,
            [FromServices] PluginServiceClient service,
            CancellationToken ct) =>
        {
            var registration = catalog.Get(pluginId);
            if (registration is null)
            {
                return ApiErrors.NotFound($"There is no plugin '{pluginId}'.");
            }

            if (registration.Capabilities.OfType<ServiceLookupCapability>().FirstOrDefault() is null)
            {
                return ApiErrors.Conflict("This plugin is not a separate program.");
            }

            if (!registration.Enabled)
            {
                return ApiErrors.Conflict("Switch the plugin on first.");
            }

            var query = http.Request.Query
                .Where(pair => pair.Key.Length <= 64)
                .Take(10)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
            var result = await service.LookupAsync(pluginId, query, ct).ConfigureAwait(false);
            return Results.Ok(new PluginServiceLookupResponse
            {
                plugin_id = pluginId,
                status = result.Status switch
                {
                    ServiceCallStatus.Succeeded => "succeeded",
                    ServiceCallStatus.NotRunning => "not_running",
                    _ => "failed",
                },
                message = result.Message,
                result = result.Json is null ? null : JsonDocument.Parse(result.Json).RootElement.Clone(),
            });
        })
        .WithName("RunPluginServiceLookup")
        .WithSummary("Ask a plugin that is a separate program to look something up (query values are passed on), to check that it works.")
        .Produces<PluginServiceLookupResponse>(StatusCodes.Status200OK)
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

        group.MapGet("/{pluginId}/removal-preview", async (string pluginId, [FromServices] PluginRemovalService removal, CancellationToken ct) =>
        {
            var preview = await removal.PreviewAsync(pluginId, ct).ConfigureAwait(false);
            return preview is null ? ApiErrors.NotFound($"Plugin '{pluginId}' not found.") : Results.Ok(preview);
        })
        .WithName("GetPluginRemovalPreview")
        .WithSummary("Everything removing the plugin would do, in plain words, before anything is removed.")
        .Produces<PluginRemovalPreviewResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsManage);

        group.MapDelete("/{pluginId}", DeletePluginAsync)
        .WithName("DeletePlugin")
        .Produces<PluginDeletedResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsManage);

        group.MapGet("/{pluginId}/connection", GetPluginConnectionAsync)
        .WithName("GetPluginConnection")
        .WithSummary("What the signed-in profile has linked for this plugin: its own credentials, never a value.")
        .Produces<PluginConnectionsResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireHumanSelfService();

        group.MapPut("/{pluginId}/connection/{name}", LinkPluginConnectionAsync)
        .WithName("LinkPluginConnection")
        .WithSummary("Links the signed-in profile's own account for a plugin. The value is encrypted on this machine and only that profile can unlink it.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireHumanSelfService();

        group.MapDelete("/{pluginId}/connection/{name}", UnlinkPluginConnectionAsync)
        .WithName("UnlinkPluginConnection")
        .WithSummary("Unlinks the signed-in profile's account for a plugin.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireHumanSelfService();

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
            [FromServices] PluginActivityService activity,
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
                .ComposeAsync(plugin.Manifest.Id, checks, aiBudget, ct, await hostServices.DescribeAsync(plugin, ct).ConfigureAwait(false), activity.PausedUntil(plugin.Manifest.Id), catalog.WaitingFor(plugin.Manifest.Id))
                .ConfigureAwait(false));
        })
        .WithName("CheckPluginHealth")
        .Produces<PluginHealthResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsRead);

        group.MapPost("/{pluginId}/check-now", async (string pluginId, [FromServices] PluginCheckNowService check, CancellationToken ct) =>
        {
            var result = await check.CheckAsync(pluginId, ct).ConfigureAwait(false);
            return result is null ? ApiErrors.NotFound($"Plugin '{pluginId}' not found.") : Results.Ok(result);
        })
        .WithName("CheckPluginNow")
        .WithSummary("Runs the plugin's health check, or reaches the metadata source once, and says in plain words whether it is working.")
        .Produces<PluginCheckNowResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsRead);

        group.MapGet("/{pluginId}/jobs", async (
            string pluginId,
            IMediaOperationRepository operations,
            [Microsoft.AspNetCore.Mvc.FromServices] VaultOperationGate vault,
            CancellationToken ct) =>
        {
            var jobs = await vault.VisibleAsync(await operations.GetByPluginAsync(pluginId, 200, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
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

    private const string NoProfileMessage = "Choose a profile first. Accounts are linked to a profile.";

    internal static async Task<IResult> GetPluginConnectionAsync(
        string pluginId,
        HttpContext http,
        [FromServices] PluginProfileConnectionService connections,
        [FromServices] IRequestAuthorityResolver authorityResolver,
        CancellationToken ct)
    {
        var authority = await authorityResolver.ResolveAsync(http, ct).ConfigureAwait(false);
        if (authority.ActiveProfileId is not { } profileId)
        {
            return ApiErrors.BadRequest(NoProfileMessage);
        }

        var status = await connections.StatusAsync(pluginId, profileId, ct).ConfigureAwait(false);
        return status is null ? ApiErrors.NotFound($"Plugin '{pluginId}' not found.") : Results.Ok(status);
    }

    internal static async Task<IResult> LinkPluginConnectionAsync(
        string pluginId,
        string name,
        PluginConnectionLinkRequest request,
        HttpContext http,
        [FromServices] PluginProfileConnectionService connections,
        [FromServices] IRequestAuthorityResolver authorityResolver,
        CancellationToken ct)
    {
        if (request?.Agreed != true)
        {
            return ApiErrors.BadRequest("Tick the box to agree before linking.");
        }

        var authority = await authorityResolver.ResolveAsync(http, ct).ConfigureAwait(false);
        if (authority.ActiveProfileId is not { } profileId)
        {
            return ApiErrors.BadRequest(NoProfileMessage);
        }

        return await connections.LinkAsync(pluginId, profileId, name, request.Value, ct).ConfigureAwait(false) switch
        {
            ConnectionOutcome.Done => Results.NoContent(),
            ConnectionOutcome.BadValue => ApiErrors.BadRequest($"Enter a value of up to {PluginProfileConnectionService.MaximumValueLength:N0} characters."),
            _ => ApiErrors.NotFound("This plugin does not ask profiles to link that."),
        };
    }

    internal static async Task<IResult> UnlinkPluginConnectionAsync(
        string pluginId,
        string name,
        HttpContext http,
        [FromServices] PluginProfileConnectionService connections,
        [FromServices] IRequestAuthorityResolver authorityResolver,
        CancellationToken ct)
    {
        var authority = await authorityResolver.ResolveAsync(http, ct).ConfigureAwait(false);
        if (authority.ActiveProfileId is not { } profileId)
        {
            return ApiErrors.BadRequest(NoProfileMessage);
        }

        return await connections.UnlinkAsync(pluginId, profileId, name, ct).ConfigureAwait(false) == ConnectionOutcome.Done
            ? Results.NoContent()
            : ApiErrors.NotFound("This plugin does not ask profiles to link that.");
    }

    /// <summary>Uninstalls a plugin; its settings, records, keys and logins go with it, and its saved output goes if the person chose that.</summary>
    internal static async Task<IResult> DeletePluginAsync(
        string pluginId,
        [FromBody] PluginRemoveRequest? request,
        PluginCatalog catalog,
        [FromServices] PluginRemovalService removal,
        CancellationToken ct)
    {
        var preview = await removal.PreviewAsync(pluginId, ct).ConfigureAwait(false);
        if (preview is null)
        {
            return ApiErrors.NotFound($"Plugin '{pluginId}' not found.");
        }

        if (request?.Output is { } choice && choice is not ("keep" or "remove"))
        {
            return ApiErrors.BadRequest("Choose to keep or remove what the plugin saved.");
        }

        var dependants = PluginDependencyGraph.Dependants(pluginId, catalog.List());
        if (dependants.Count > 0 && request?.AlsoSwitchOffDependants != true)
        {
            return ApiErrors.Conflict(
                "has_dependants",
                $"{string.Join(" and ", dependants.Select(plugin => plugin.Manifest.Name))} needs this plugin. Switch {(dependants.Count == 1 ? "it" : "them")} off too, or cancel.");
        }

        foreach (var dependant in dependants)
        {
            catalog.SetEnabled(dependant.Manifest.Id, false);
        }

        try
        {
            await removal.RemoveAsync(pluginId, (request?.Output ?? preview.DefaultOutput) == "remove", ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return ApiErrors.BadRequest(ex.Message);
        }

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
            return Results.Ok(plugin.Manifest.Secrets.Where(secret => !PluginDeclarationRules.IsPersonalCredential(plugin.Manifest.Declaration, secret.Name)).Select(secret => new PluginSecretStatusResponse
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

        if (PluginDeclarationRules.IsPersonalCredential(plugin.Manifest.Declaration, declared.Name))
        {
            return ApiErrors.BadRequest($"'{declared.Name}' is linked by each person on their own profile, not for the whole household.");
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

    /// <summary>A plugin's id or a metadata source's name; anything else has no Activity list and no library limit.</summary>
    private static bool IsPluginOrSource(string id, PluginCatalog catalog, ProviderCapabilityCatalogue sources) =>
        catalog.Get(id) is not null || sources.Sources().Any(source => string.Equals(source.Name, id, StringComparison.OrdinalIgnoreCase));

    private static string DependencyStateWord(PluginDependencyState state) => state switch
    {
        PluginDependencyState.Ready => "ready",
        PluginDependencyState.Off => "off",
        PluginDependencyState.Missing => "missing",
        PluginDependencyState.WrongVersion => "wrong_version",
        PluginDependencyState.Broken => "broken",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown dependency state."),
    };

    private static string DescribeLimit(PluginLibraryLimitResponse limit) =>
        limit.Limited ? string.Join(", ", limit.Libraries.Where(library => library.Selected).Select(library => library.Name)) : "every library";

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
    internal static string AiStatusWord(PluginAiStatus status) => status switch
    {
        PluginAiStatus.Succeeded => "succeeded",
        PluginAiStatus.Refused => "refused",
        PluginAiStatus.AiUnavailable => "ai_unavailable",
        PluginAiStatus.Busy => "busy",
        PluginAiStatus.OverAllowance => "over_allowance",
        PluginAiStatus.TimedOut => "timed_out",
        PluginAiStatus.InvalidAnswer => "invalid_answer",
        _ => "failed",
    };

    internal static async Task<IResult> DisablePlugin(
        string pluginId,
        [FromBody] PluginDisableRequest? request,
        HttpContext http,
        PluginCatalog catalog,
        [FromServices] AiFeatureRetractionService retraction,
        [FromServices] PluginActivityService activity,
        [FromServices] PluginPeopleActionLog peopleActions,
        [FromServices] IRequestAuthorityResolver authorityResolver,
        CancellationToken ct)
    {
        // Plugins that need this one go off first (C22); never silently broken, so without the flag nothing changes.
        var dependants = PluginDependencyGraph.Dependants(pluginId, catalog.List());
        if (dependants.Count > 0 && request?.AlsoSwitchOffDependants != true)
        {
            return ApiErrors.Conflict(
                "has_dependants",
                $"{string.Join(" and ", dependants.Select(plugin => plugin.Manifest.Name))} needs this plugin. Switch {(dependants.Count == 1 ? "it" : "them")} off too, or cancel.");
        }

        var farthestFirst = PluginDependencyGraph.StartOrder(catalog.List())
            .Reverse()
            .Where(plugin => dependants.Any(dependant => string.Equals(dependant.Manifest.Id, plugin.Manifest.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(plugin => plugin.Manifest.Id)
            .ToList();
        foreach (var dependantId in farthestFirst)
        {
            await SwitchOffAsync(dependantId).ConfigureAwait(false);
        }

        await SwitchOffAsync(pluginId).ConfigureAwait(false);
        return Results.Ok(new PluginEnabledResponse { plugin_id = pluginId, enabled = false, also_changed = farthestFirst });

        async Task SwitchOffAsync(string id)
        {
            var registration = catalog.Get(id);
            catalog.SetEnabled(id, false);
            if (registration is not null)
            {
                var context = new PluginActivityContext(registration.Manifest.Id, registration.Manifest.Version);
                await activity.ForgetAsync(id, deleteHistory: false, ct).ConfigureAwait(false);
                await activity.RecordNoteAsync(context, "Switched off.", ct).ConfigureAwait(false);
                var authority = await authorityResolver.ResolveAsync(http, ct).ConfigureAwait(false);
                await peopleActions.RecordAsync(new PluginPeopleAction(
                    authority.ActiveProfileId, registration.Manifest.Id, "switched-plugin-off", $"Switched off {registration.Manifest.Name}"), ct).ConfigureAwait(false);
            }

            // An AI feature's suggestions go away with its plugin (D-T); provider and person values stay.
            await retraction.RetractForPluginAsync(id, ct);
        }
    }

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

        // Plugins this one needs go on first (C22), under one approval that lists every plugin being switched on.
        var order = PluginDependencyGraph.EnableOrder(pluginId, catalog.List(), out var blocked);
        if (blocked.Count > 0)
        {
            return ApiErrors.Conflict("needs_unavailable", string.Join(" ", blocked));
        }

        var needed = order.Where(plugin => !string.Equals(plugin.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (needed.Count > 0 && request?.AlsoSwitchOnNeeded != true)
        {
            return ApiErrors.Conflict(
                "needs_off",
                $"{registration.Manifest.Name} needs {string.Join(" and ", needed.Select(plugin => plugin.Manifest.Name))}. Switch {(needed.Count == 1 ? "it" : "them")} on too, or cancel.");
        }

        var targets = order.Count == 0 ? [registration] : order;
        var unfit = targets.FirstOrDefault(plugin => !plugin.Compatibility.IsCompatible);
        if (unfit is not null)
        {
            // The plain-English reason (Library version, platform, plugin kind or load problem) is the detail.
            return ApiErrors.Conflict(unfit.Compatibility.Problem ?? "This plugin cannot be switched on.");
        }

        var pending = new List<(PluginRegistration Plugin, ApprovalPreviewResponse Preview)>();
        foreach (var target in targets)
        {
            var preview = approvals.PreviewPlugin(target);
            if (!await approvals.IsCoveredAsync(ApprovalKinds.Plugin, target.Manifest.Id, preview.StatementHash, ct).ConfigureAwait(false))
            {
                pending.Add((target, preview));
            }
        }

        if (pending.Count > 0)
        {
            var supplied = (request?.ApprovalHashes ?? []).Append(request?.ApprovalHash).ToList();
            if (pending.Any(entry => !supplied.Any(hash => ApprovalService.HashMatches(hash, entry.Preview.StatementHash))))
            {
                return ApiErrors.ApprovalRequired(pending.Select(entry => entry.Preview));
            }

            var authority = await authorityResolver.ResolveAsync(http, ct).ConfigureAwait(false);
            var actor = await approvals.ResolveActorAsync(authority, ct).ConfigureAwait(false);
            foreach (var (plugin, preview) in pending)
            {
                await approvals.RecordAsync(preview, ApprovalKinds.Plugin, plugin.Manifest.Id, actor, ct).ConfigureAwait(false);
            }
        }

        try
        {
            foreach (var target in targets)
            {
                catalog.SetEnabled(target.Manifest.Id, true);
            }

            return Results.Ok(new PluginEnabledResponse
            {
                plugin_id = pluginId,
                enabled = true,
                also_changed = needed.Select(plugin => plugin.Manifest.Id).ToList(),
            });
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
        Category = PluginCategories.Resolve(registration.Manifest.Category),
        MediaTypes = PluginCategories.ResolveMediaTypes(registration.Manifest.MediaTypes).ToList(),
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
        Access = PluginAccessMapper.Map(
            registration.Manifest.Declaration,
            registration.Manifest.Network.Select(destination => (destination.Host, (string?)destination.Sends)).ToList(),
            registration.Manifest.Secrets.Count > 0,
            PluginAccessMapper.MediaLabelsFor(PluginCategories.ResolveMediaTypes(registration.Manifest.MediaTypes)),
            registration.Manifest.MediaTypes,
            registration.Manifest.Category),
        Settings = registration.Settings.ToDictionary(
            setting => setting.Key,
            setting => setting.Value,
            StringComparer.OrdinalIgnoreCase),
        SettingsSchema = registration.SettingsSchema,
        ManifestPath = registration.ManifestPath,
        HostServices = hostServices ?? new PluginHostServicesDto(),
    };
}
