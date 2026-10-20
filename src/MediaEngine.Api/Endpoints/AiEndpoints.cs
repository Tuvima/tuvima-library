using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Contracts.Ai;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// AI subsystem API endpoints — model lifecycle, download management, and configuration.
///
/// All endpoints require Administrator role.
/// </summary>
internal static class AiEndpoints
{
    internal static RouteGroupBuilder MapAiEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/ai")
                          .WithTags("AI");

        // ── GET /ai/status ───────────────────────────────────────────────────
        group.MapGet("/status", (
            IModelLifecycleManager lifecycle) =>
        {
            var status = lifecycle.GetHealthStatus();
            return Results.Ok(new AiHealthStatusDto
            {
                Models = status.Models.Select(ToModelStatusResponse).ToList(),
                MemoryUsedMB = status.MemoryUsedMB,
                MemoryLimitMB = status.MemoryLimitMB,
                GpuAvailable = status.GpuAvailable,
                MemoryProfile = status.MemoryProfile,
                IsReady = status.IsReady,
            });
        })
        .WithName("GetAiStatus")
        .WithSummary("Returns overall AI subsystem health status.")
        .Produces<AiHealthStatusDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.AiStatusRead);

        // ── GET /ai/models ───────────────────────────────────────────────────
        group.MapGet("/models", (
            IModelDownloadManager downloadManager,
            IModelLifecycleManager lifecycle,
            AiSettings settings,
            ModelInventory inventory) =>
        {
            var statuses = downloadManager.GetAllStatuses()
                .Select(status =>
                {
                    return ToModelStatusResponse(status, settings, lifecycle.CurrentlyLoadedRole, inventory);
                })
                .ToList();
            return Results.Ok(statuses);
        })
        .WithName("GetAiModelStatuses")
        .WithSummary("Returns download and lifecycle status for all AI model roles.")
        .Produces<IReadOnlyList<AiModelStatusDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.AiStatusRead);

        // ── POST /ai/models/{role}/download ──────────────────────────────────
        group.MapPost("/models/{role}/download", async (
            string role,
            IModelDownloadManager downloadManager,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!TryParseModelRole(role, out var modelRole))
            {
                return UnknownRoleProblem(role);
            }

            try
            {
                await downloadManager.StartDownloadAsync(modelRole, ct);
                return Results.Accepted();
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("AiModelCommands").LogError(ex, "Could not start model download for {Role}", modelRole);
                return ModelCommandProblem("download_start_failed", "The model download could not be started.");
            }
        })
        .WithName("StartAiModelDownload")
        .WithSummary("Starts downloading the model for the specified role. Returns 202 Accepted immediately; progress is reported via SignalR.")
        .Produces(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.AiManage);

        // ── DELETE /ai/models/{role}/download ────────────────────────────────
        group.MapDelete("/models/{role}/download", async (
            string role,
            IModelDownloadManager downloadManager,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!TryParseModelRole(role, out var modelRole))
            {
                return UnknownRoleProblem(role);
            }

            try
            {
                await downloadManager.CancelDownloadAsync(modelRole, ct);
                return Results.Ok(new AiDownloadCancelledResponse(true, ToRoleKey(modelRole)));
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("AiModelCommands").LogError(ex, "Could not cancel model download for {Role}", modelRole);
                return ModelCommandProblem("download_cancel_failed", "The model download could not be cancelled.");
            }
        })
        .WithName("CancelAiModelDownload")
        .WithSummary("Cancels an in-progress model download for the specified role.")
        .Produces<AiDownloadCancelledResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.AiManage);

        // ── POST /ai/models/{role}/load ──────────────────────────────────────
        group.MapPost("/models/{role}/load", async (
            string role,
            IModelLifecycleManager lifecycle,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!TryParseModelRole(role, out var modelRole))
            {
                return UnknownRoleProblem(role);
            }

            try
            {
                await lifecycle.LoadModelAsync(modelRole, ct);
                return Results.Ok(new AiModelLoadedResponse(true, ToRoleKey(modelRole)));
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("AiModelCommands").LogError(ex, "Could not load model {Role}", modelRole);
                return ModelCommandProblem("model_load_failed", "The model could not be loaded.");
            }
        })
        .WithName("LoadAiModel")
        .WithSummary("Loads the model for the specified role into memory. Unloads any currently loaded model first.")
        .Produces<AiModelLoadedResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.AiManage);

        // ── POST /ai/models/{role}/unload ────────────────────────────────────
        group.MapPost("/models/{role}/unload", async (
            string role,
            IModelLifecycleManager lifecycle,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!TryParseModelRole(role, out var modelRole))
            {
                return UnknownRoleProblem(role);
            }

            try
            {
                // Only unload if the requested role is currently loaded.
                if (lifecycle.CurrentlyLoadedRole == modelRole)
                {
                    await lifecycle.UnloadCurrentAsync(ct);
                }

                return Results.Ok(new AiModelUnloadedResponse(true, ToRoleKey(modelRole)));
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("AiModelCommands").LogError(ex, "Could not unload model {Role}", modelRole);
                return ModelCommandProblem("model_unload_failed", "The model could not be unloaded.");
            }
        })
        .WithName("UnloadAiModel")
        .WithSummary("Unloads the model for the specified role from memory, freeing resources.")
        .Produces<AiModelUnloadedResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.AiManage);

        // ── GET /ai/config ───────────────────────────────────────────────────
        group.MapGet("/config", GetAiConfig)
        .WithName("GetAiConfig")
        .WithSummary("Returns the current AI configuration (config/ai.json).")
        .Produces<AiConfigDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.AiManage);

        // ── PUT /ai/config ───────────────────────────────────────────────────
        group.MapPut("/config", SaveAiConfigAsync)
        .WithName("SaveAiConfig")
        .WithSummary("Saves updated AI configuration to config/ai.json. A feature switched from off to on needs its approval hash in 'approvals' (409 approval_required otherwise, nothing saved).")
        .Produces<AiSettingsSavedResponse>(StatusCodes.Status200OK)
        .Produces<ApprovalRequiredResponse>(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.AiManage);

        // ── GET /ai/profile ──────────────────────────────────────────────────
        group.MapGet("/profile", (HardwareBenchmarkService benchmark) =>
        {
            return Results.Ok(AiContractMapper.ToContract(benchmark.Current));
        })
        .WithName("GetAiHardwareProfile")
        .WithSummary("Returns the cached hardware profile and performance tier.")
        .Produces<HardwareProfileDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.AiStatusRead);

        // ── POST /ai/benchmark ───────────────────────────────────────────────
        group.MapPost("/benchmark", async (
            MediaEngine.AI.Infrastructure.HardwareBenchmarkService benchmark,
            CancellationToken ct) =>
        {
            var profile = await benchmark.BenchmarkAsync(force: true, ct: ct);
            return Results.Ok(AiContractMapper.ToContract(profile));
        })
        .WithName("RunAiHardwareBenchmark")
        .WithSummary("Re-runs the hardware benchmark and returns the updated profile.")
        .Produces<HardwareProfileDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.AiManage);

        group.MapDelete("/benchmark", async (
            HardwareBenchmarkService benchmark,
            IModelLifecycleManager lifecycle,
            CancellationToken ct) =>
        {
            await lifecycle.UnloadCurrentAsync(ct);
            return Results.Ok(AiContractMapper.ToContract(benchmark.Invalidate()));
        })
        .WithName("InvalidateAiHardwareBenchmark")
        .WithSummary("Invalidates the machine-local hardware benchmark.")
        .Produces<HardwareProfileDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.AiManage);

        // ── GET /ai/resources ────────────────────────────────────────────────
        group.MapGet("/resources", (ResourceMonitorService monitor) =>
        {
            var snapshot = monitor.GetSnapshot();
            return Results.Ok(new ResourceSnapshotDto
            {
                TotalRamMb = snapshot.TotalRamMb,
                FreeRamMb = snapshot.FreeRamMb,
                EngineRamMb = snapshot.EngineRamMb,
                CpuPressure = snapshot.CpuPressure,
                TranscodingActive = snapshot.TranscodingActive,
            });
        })
        .WithName("GetAiResourceSnapshot")
        .WithSummary("Returns current system resource usage (RAM, CPU pressure, transcoding status).")
        .Produces<ResourceSnapshotDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.AiStatusRead);

        // ── GET /ai/enrichment/progress ──────────────────────────────────────
        group.MapGet("/enrichment/progress", async (
            ICanonicalValueRepository canonicals,
            CancellationToken ct) =>
        {
            // Items that have a description but not yet themes → pending enrichment.
            var pending = await canonicals.GetEntitiesNeedingEnrichmentAsync("description", "themes", 10000, ct);
            // Items that already have themes → completed enrichment.
            var completed = await canonicals.GetEntitiesNeedingEnrichmentAsync("themes", "__nonexistent__", 10000, ct);
            int pendingCount = pending.Count;
            int completedCount = completed.Count;
            return Results.Ok(new EnrichmentProgressDto
            {
                PendingCount = pendingCount,
                CompletedCount = completedCount,
                Total = pendingCount + completedCount,
            });
        })
        .WithName("GetAiEnrichmentProgress")
        .WithSummary("Returns pending and completed AI enrichment counts.")
        .Produces<EnrichmentProgressDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRead);

        return group;
    }

    /// <summary>The saved configuration, with the Engine's plain words for what each feature may do.</summary>
    internal static IResult GetAiConfig(AiConfigurationService configurationStore)
    {
        var config = AiContractMapper.ToContract(configurationStore.Current);
        foreach (var feature in config.Features.Keys)
        {
            config.FeatureInfo[feature] = new AiFeatureInfoDto
            {
                AllowedTo = ApprovalStatementBuilder.AiFeatureAllowedTo(feature).ToList(),
                SendsDataTo = ApprovalStatementBuilder.AiFeatureSendsDataTo(feature),
            };
        }

        return Results.Ok(config);
    }

    /// <summary>
    /// Saves the AI configuration. The approval rule: a feature switched from off to on needs its current statement
    /// confirmed (the hash in <see cref="AiConfigDto.Approvals"/>) or already recorded. Every such feature is listed in one
    /// 409 and nothing is saved until all of them are covered; confirmed approvals are recorded before the save.
    /// </summary>
    internal static async Task<IResult> SaveAiConfigAsync(
        AiConfigDto request,
        HttpContext http,
        AiConfigurationService configurationStore,
        IModelDownloadManager downloads,
        [FromServices] ApprovalService approvals,
        [FromServices] IRequestAuthorityResolver authorityResolver,
        CancellationToken ct)
    {
        // The same feature written in two letter cases is ambiguous (which one wins depends on the order), so it is refused.
        if (request.Features.Keys.GroupBy(key => key, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            return ApiErrors.BadRequest("A feature appears more than once with different letter case.");
        }

        // "Switched on" is judged on the canonical feature keys, using the same reading (case-insensitive) that the save
        // itself applies, so changing the letter case of a key cannot switch a feature on without its approval.
        var settings = AiContractMapper.ToSettings(request);
        var wasOn = AiContractMapper.ToContract(configurationStore.Current).Features;
        var willBeOn = AiContractMapper.ToContract(settings).Features;
        var confirmed = new List<ApprovalPreviewResponse>();
        var missing = new List<ApprovalPreviewResponse>();
        var switchedOn = wasOn.Keys
            .Where(key => willBeOn.TryGetValue(key, out var after) && after && !wasOn[key])
            .Order(StringComparer.Ordinal);
        foreach (var feature in switchedOn)
        {
            var preview = approvals.PreviewAiFeature(feature, ApprovalService.IsFeatureModelReady(downloads));
            if (await approvals.IsCoveredAsync(ApprovalKinds.AiFeature, feature, preview.StatementHash, ct).ConfigureAwait(false))
            {
                continue;
            }

            if (request.Approvals?.Any(pair => string.Equals(pair.Key, feature, StringComparison.OrdinalIgnoreCase)
                    && ApprovalService.HashMatches(pair.Value, preview.StatementHash)) == true)
            {
                confirmed.Add(preview);
            }
            else
            {
                missing.Add(preview);
            }
        }

        if (missing.Count > 0)
        {
            return ApiErrors.ApprovalRequired(missing);
        }

        if (confirmed.Count > 0)
        {
            var authority = await authorityResolver.ResolveAsync(http, ct).ConfigureAwait(false);
            var actor = await approvals.ResolveActorAsync(authority, ct).ConfigureAwait(false);
            foreach (var preview in confirmed)
            {
                await approvals.RecordAsync(preview, ApprovalKinds.AiFeature, preview.ItemId, actor, ct).ConfigureAwait(false);
            }
        }

        var errors = configurationStore.Save(settings);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors
                .GroupBy(error => error.Path)
                .ToDictionary(group => group.Key, group => group.Select(error => error.Message).ToArray()));
        }

        return Results.Ok(new AiSettingsSavedResponse(true));
    }

    private static bool TryParseModelRole(string value, out AiModelRole role)
    {
        var normalized = value.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return Enum.TryParse(normalized, ignoreCase: true, out role);
    }

    private static string UnknownRoleMessage(string role) =>
        $"Unknown model role: '{role}'. Valid values: {string.Join(", ", Enum.GetValues<AiModelRole>().Select(ToRoleKey))}.";

    private static IResult UnknownRoleProblem(string role) => Results.Problem(
        detail: UnknownRoleMessage(role),
        type: "https://tuvima.local/problems/ai/unknown-model-role",
        title: "Unknown AI model role",
        statusCode: StatusCodes.Status400BadRequest);

    private static IResult ModelCommandProblem(string code, string detail) => Results.Problem(
        detail: detail,
        type: $"https://tuvima.local/problems/ai/{code}",
        title: "AI model operation failed",
        statusCode: StatusCodes.Status500InternalServerError);

    private static string ToRoleKey(AiModelRole role) => AiModelDefinitions.ToRoleKey(role);

    private static string GetRequiredHardwareTier(AiModelRole role) => role switch
    {
        AiModelRole.TextScholar => "high",
        AiModelRole.TextQuality or AiModelRole.TextCjk or AiModelRole.Audio => "medium",
        _ => "low",
    };

    private static string? TryGetUriHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private static AiModelStatusDto ToModelStatusResponse(AiModelStatus status) => new()
    {
        Role = ToRoleKey(status.Role),
        RoleName = status.Role.ToString(),
        Supported = true,
        ModelType = status.ModelType.ToString(),
        State = status.State.ToString(),
        ModelFile = status.ModelFile,
        SizeMB = status.SizeMB,
        DownloadProgressPercent = status.DownloadProgressPercent,
        BytesDownloaded = status.BytesDownloaded,
        TotalBytes = status.TotalBytes,
        Loaded = status.State == AiModelState.Loaded,
        Active = status.State == AiModelState.Loaded,
        MemoryFootprintMB = status.State == AiModelState.Loaded ? status.SizeMB : 0,
        RequiredHardwareTier = GetRequiredHardwareTier(status.Role),
        ErrorMessage = status.ErrorMessage,
        DiskStatus = "unknown",
        ChecksumStatus = "unknown",
        CanOperate = false,
    };

    private static AiModelStatusDto ToModelStatusResponse(AiModelStatus status, AiSettings settings, AiModelRole? currentRole, ModelInventory inventory)
    {
        var definition = settings.Models.GetByRole(status.Role);
        var advisor = new AiModelSelectionAdvisor(settings);
        var decision = advisor.GetDecision(status.Role);
        var catalog = settings.GetCatalogEntryForRole(status.Role);
        var isLoaded = currentRole == status.Role || status.State == AiModelState.Loaded;
        return new AiModelStatusDto
        {
            Role = ToRoleKey(status.Role),
            RoleName = status.Role.ToString(),
            Supported = true,
            ModelType = status.ModelType.ToString(),
            State = status.State.ToString(),
            Description = definition.Description,
            ModelFile = status.ModelFile,
            SizeMB = status.SizeMB,
            DownloadUrlHost = TryGetUriHost(definition.DownloadUrl),
            DownloadProgressPercent = status.DownloadProgressPercent,
            BytesDownloaded = status.BytesDownloaded,
            TotalBytes = status.TotalBytes,
            Loaded = isLoaded,
            Active = currentRole == status.Role,
            MemoryFootprintMB = isLoaded ? definition.SizeMB : 0,
            RequiredHardwareTier = GetRequiredHardwareTier(status.Role),
            ErrorMessage = status.ErrorMessage,
            CatalogKey = definition.CatalogKey,
            DisplayName = decision.DisplayName,
            Family = decision.Family,
            Provider = decision.Provider,
            License = decision.License,
            Runtime = decision.Runtime,
            SelectionTier = decision.SelectionTier,
            SelectionStatus = decision.Status,
            SelectionRationale = decision.Rationale,
            RoleRequirement = decision.Requirement,
            BenchmarkSuite = decision.BenchmarkSuite,
            ValidationWarnings = decision.Warnings.ToList(),
            Capabilities = FormatCapabilities(catalog?.Capabilities).ToList(),
            DiskStatus = GetDiskStatus(inventory.GetModelPath(status.Role)),
            DiskSizeMB = GetDiskSizeMB(inventory.GetModelPath(status.Role)),
            MemoryEnvelopeMB = decision.MemoryEnvelopeMB,
            Quantization = decision.Quantization,
            SourceUrl = decision.SourceUrl,
            ChecksumStatus = decision.ChecksumConfigured ? "configured" : "missing",
            ConfigurationReady = decision.ConfigurationReady,
            RuntimeReady = decision.RuntimeReady,
            Validated = decision.Validated,
            CanOperate = decision.CanEnable,
            Experimental = decision.Experimental,
            BlockingReasons = decision.BlockingReasons.ToList(),
        };
    }

    private static string GetDiskStatus(string path) => File.Exists(path) ? "present" : "missing";

    private static long GetDiskSizeMB(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (long)Math.Ceiling(info.Length / 1024d / 1024d) : 0;
    }

    private static IReadOnlyList<string> FormatCapabilities(AiModelCapabilities? capabilities)
    {
        if (capabilities is null)
        {
            return [];
        }

        var values = new List<string>();
        if (capabilities.TextInput)
        {
            values.Add("text input");
        }

        if (capabilities.AudioInput)
        {
            values.Add("audio input");
        }

        if (capabilities.ImageInput)
        {
            values.Add("image input");
        }

        if (capabilities.TextOutput)
        {
            values.Add("text output");
        }

        if (capabilities.StructuredJson)
        {
            values.Add("structured JSON");
        }

        if (capabilities.Gbnf)
        {
            values.Add("GBNF");
        }

        if (capabilities.TimestampSegments)
        {
            values.Add("segment timestamps");
        }

        if (capabilities.WordTimestamps)
        {
            values.Add("word timestamps");
        }

        if (capabilities.SyncGrade)
        {
            values.Add("sync-grade");
        }

        if (capabilities.Multilingual)
        {
            values.Add("multilingual");
        }

        if (capabilities.Cjk)
        {
            values.Add("CJK");
        }

        if (capabilities.ExperimentalMultimodal)
        {
            values.Add("experimental multimodal");
        }

        if (capabilities.EmbeddingOutput)
        {
            values.Add("embeddings");
        }

        if (capabilities.FunctionCalling)
        {
            values.Add("function calling");
        }

        if (capabilities.ToolCalling)
        {
            values.Add("tool calling");
        }

        return values;
    }

}
