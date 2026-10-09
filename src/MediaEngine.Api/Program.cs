using System.Threading.RateLimiting;
using MediaEngine.Api;
using MediaEngine.Api.DependencyInjection;
#if DEBUG
using MediaEngine.Api.DevSupport;
#endif
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Middleware;
using MediaEngine.Api.Realtime;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.HealthChecks;
using MediaEngine.Contracts.Startup;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.DependencyInjection;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Configuration;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.ResponseCompression;
using Serilog;

#if DEBUG
if (args.Length is 2 or 3 && args[0] == "--repair-contributor-editions")
{
    await ContributorEditionRepair.RunAsync(args[1], args.Length == 3 && args[2] == "--apply");
    return;
}
if (args.Length is 2 or 3 && args[0] == "--repair-real-media")
{
    await RealMediaRepair.RunAsync(args[1], args.Length == 3 && args[2] == "--apply");
    return;
}
// Read-only audit intentionally works while the Engine owns its process lease.
if (args.Length >= 2 && args[0] == "--audit-real-media")
{
    await RealMediaAudit.RunAsync(args[1], args.Contains("--verify"));
    return;
}
#endif
using var processInstanceLease = ProcessInstanceLease.TryAcquire(ProcessInstanceLease.EngineLeaseName);
if (!processInstanceLease.IsAcquired)
{
    Console.WriteLine("Tuvima Library Engine is already running; the duplicate launch will exit without replacing it.");
    Environment.ExitCode = 73;
    return;
}

#if DEBUG
if (args.Length == 2 && args[0] == "--resume-real-media-directory-timestamps")
{
    await RealMediaHarness.RecordDirectoryProbeRecoveryAsync(args[1]);
    return;
}
if (args.Length > 0 && args[0] == "--prepare-real-media")
{
    if (args.Length is < 4 or > 5)
    {
        throw new ArgumentException("Usage: --prepare-real-media <config-dir> <source-root> <report-dir> [profile-id]");
    }
    await RealMediaHarness.PrepareAsync(args[1], args[2], args[3], args.Length == 5 ? Guid.Parse(args[4]) : null);
    return;
}
#endif

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddMemoryCache();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("display-read", policy => policy
        .Expire(TimeSpan.FromSeconds(5))
        .SetVaryByQuery("*")
        .SetVaryByHeader("Authorization", TuvimaAuthDefaults.SessionHeader)
        .Tag("display"));
});
ConfigurationManager config = builder.Configuration;
string configDirectory =
    Environment.GetEnvironmentVariable("TUVIMA_CONFIG_DIR")
    ?? config["MediaEngine:ConfigDirectory"]
    ?? "config";
string backupDirectory =
    Environment.GetEnvironmentVariable("TUVIMA_BACKUP_DIR")
    ?? Path.Combine(configDirectory, "backups");
string dataProtectionDirectory =
    Environment.GetEnvironmentVariable("TUVIMA_DATA_PROTECTION_DIR")
    ?? Path.Combine(configDirectory, ".keys");
string logDirectory =
    Environment.GetEnvironmentVariable("TUVIMA_LOG_DIR")
    ?? "logs";
#if DEBUG
var realMediaRun = RealMediaHarness.Load(configDirectory);
if (realMediaRun is not null)
{
    RealMediaHarness.ValidateConfiguration(configDirectory, realMediaRun);
    foreach (var path in new[] { logDirectory, backupDirectory, dataProtectionDirectory })
    {
        RealMediaHarness.RequireSeparate(realMediaRun.SourceRoot, Path.GetFullPath(path));
    }
    builder.Services.AddSingleton(realMediaRun);
    builder.Services.AddSingleton<RealMediaProtectionService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RealMediaProtectionService>());
}
#else
if (File.Exists(Path.Combine(configDirectory, "real-media-harness.json")))
{
    throw new InvalidOperationException("A protected development corpus must be run with the Debug harness.");
}
#endif
Directory.CreateDirectory(dataProtectionDirectory);
Directory.CreateDirectory(logDirectory);

// -- Serilog ------------------------------------------------------------------
builder.Host.UseSerilog((context, services, loggerConfig) => loggerConfig
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: Path.Combine(logDirectory, "tuvima-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}{NewLine}  {Message:lj}{NewLine}{Exception}"));

builder.Host.UseWindowsService(options => options.ServiceName = "Tuvima Library");

// -- CORS ---------------------------------------------------------------------
string[] allowedOrigins = config
    .GetSection("MediaEngine:Cors:AllowedOrigins")
    .Get<string[]>() ?? [];
string? envCorsOrigins = Environment.GetEnvironmentVariable("TUVIMA_CORS_ORIGINS");
if (!string.IsNullOrWhiteSpace(envCorsOrigins))
{
    string[] extra = envCorsOrigins.Split(
        ",",
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    allowedOrigins = [.. allowedOrigins, .. extra];
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("BlazorWasm", policy =>
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
});

// -- SignalR / API infrastructure ---------------------------------------------
builder.Services.AddSignalR(options =>
{
    options.AddFilter<IntercomAuthFilter>();
});
builder.Services.AddSingleton<IEventPublisher, SignalREventPublisher>();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory))
    .SetApplicationName("Tuvima.Library");
builder.Services.AddSingleton<ISecretStore, DataProtectionSecretStore>();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    };
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() { Title = "Tuvima Library API", Version = "v1" });
    options.CustomSchemaIds(type => (type.FullName ?? type.Name).Replace('+', '.'));
});

// -- Storage bootstrap ---------------------------------------------------------
DapperConfiguration.Configure();
string dbPath = TuvimaDataPathResolver.ResolveDatabasePath(
    configDirectory,
    Environment.GetEnvironmentVariable("TUVIMA_DB_PATH"),
    Environment.GetEnvironmentVariable("TUVIMA_LIBRARY_ROOT"));
{
    var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
    if (!string.IsNullOrEmpty(databaseDirectory))
    {
        Directory.CreateDirectory(databaseDirectory);
    }
}

DatabaseBackupService.ApplyPendingRestore(configDirectory, dbPath, backupDirectory);

builder.Services.AddSingleton<IDatabaseConnection>(_ =>
{
    var database = new DatabaseConnection(dbPath);
    database.InitializeSchema();
    database.RunStartupChecks();
    return database;
});

ConfigurationDirectoryLoader configLoader;
try
{
    configLoader = new ConfigurationDirectoryLoader(configDirectory);
#if DEBUG
    if (realMediaRun is null)
    {
        configLoader.StartWatching();
    }
#else
    configLoader.StartWatching();
#endif
    configLoader.ConfigurationChanged += (_, change) =>
    {
        if (change.Applied)
        {
            Log.Information("Configuration reloaded: {ConfigFile}", change.RelativePath);
        }
        else
        {
            Log.Warning(
                change.Error,
                "Configuration reload rejected for {ConfigFile}; keeping last-known-good values.",
                change.RelativePath);
        }
    };
}
catch (ConfigValidationException ex)
{
    Log.Fatal(
        ex,
        "Invalid Tuvima Library configuration: {ConfigFile} failed {SchemaName}",
        ex.FilePath,
        ex.SchemaName);
    throw;
}

foreach (var warning in LegacyAccessSettingsCheck.Find(configDirectory))
{
    Log.Warning("Access settings: {Detail}", warning);
}

builder.Services.AddSingleton<IConfigurationLoader>(configLoader);
builder.Services.AddSingleton<ProviderCredentialService>();

// -- Rate limiting -------------------------------------------------------------
{
    var rateLimits = configLoader.LoadCore().RateLimiting;
    string[] globalLimiterExemptPaths = ["/system/status", "/health", "/health/live"];
    string[] globalLimiterExemptPrefixes =
    [
        "/swagger",
        "/stream",
        "/read",
        "/playback",
    ];

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = (rejectedContext, _) =>
        {
            if (rejectedContext.Lease.TryGetMetadata(
                    MetadataName.RetryAfter,
                    out var retryAfter))
            {
                rejectedContext.HttpContext.Response.Headers.RetryAfter =
                    Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return ValueTask.CompletedTask;
        };
        options.AddPolicy("key_generation", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rateLimits.KeyGeneration.PermitLimit,
                    Window = TimeSpan.FromMinutes(rateLimits.KeyGeneration.WindowMinutes),
                }));
        options.AddPolicy("authentication", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        options.AddPolicy("intercom", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        options.AddPolicy("streaming", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rateLimits.Streaming.PermitLimit,
                    Window = TimeSpan.FromMinutes(rateLimits.Streaming.WindowMinutes),
                }));
        // HLS uses many short segment/playlist requests. Bound simultaneous work
        // instead of exhausting the ordinary file-stream start quota mid-film.
        options.AddPolicy("adaptive_streaming", context =>
            RateLimitPartition.GetConcurrencyLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = 8,
                    QueueLimit = 64,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                }));
        // Thumbnails are a bounded image-grid workload, not long-lived media streams.
        options.AddPolicy("view_images", context =>
            RateLimitPartition.GetConcurrencyLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = 8,
                    QueueLimit = 128,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                }));
        options.AddPolicy("general", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rateLimits.General.PermitLimit,
                    Window = TimeSpan.FromMinutes(rateLimits.General.WindowMinutes),
                }));
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var isExempt =
                globalLimiterExemptPaths.Any(exempt =>
                    path.Equals(exempt, StringComparison.OrdinalIgnoreCase))
                || globalLimiterExemptPrefixes.Any(prefix =>
                    path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
            if (isExempt)
            {
                return RateLimitPartition.GetNoLimiter("exempt");
            }

            return RateLimitPartition.GetFixedWindowLimiter(
                ViewImageRateLimitPartition.Key(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rateLimits.General.PermitLimit,
                    Window = TimeSpan.FromMinutes(rateLimits.General.WindowMinutes),
                });
        });
    });
}

// -- Composition roots ---------------------------------------------------------
builder.Services.AddTuvimaStorage();
builder.Services.AddSingleton<IPermissionRegistry, PermissionRegistry>();
builder.Services.AddScoped<IRequestAuthorityResolver, RequestAuthorityResolver>();
builder.Services.AddScoped<DashboardAuthorityProjector>();
builder.Services.AddScoped<IAccountAccessMutationService, AccountAccessMutationService>();
builder.Services.AddScoped<ApplicationAdministrationService>();
builder.Services.AddScoped<IGrantAdminUnlockService, GrantAdminUnlockService>();
builder.Services.AddScoped<IAccountAccessDecisionService, AccountAccessDecisionService>();
builder.Services.AddScoped<ISelfServiceAuthorizationService>(sp => (AccountAccessDecisionService)sp.GetRequiredService<IAccountAccessDecisionService>());
builder.Services.AddScoped<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, MediaEngine.Api.Security.AuthorizationEvaluator>();
builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
builder.Services.AddSingleton<IAuthorizationInvalidationService, AuthorizationInvalidationService>();
builder.Services.AddSingleton<IAuthorizationAuditWriter, AuthorizationAuditWriter>();
builder.Services.AddSingleton<ClientAuthorizationService>();
builder.Services.AddSingleton(new DashboardServiceCredentialOptions(configDirectory));
builder.Services.AddSingleton<DashboardServiceCredentialBootstrapper>();
builder.Services.AddSingleton<IntercomTokenService>();
builder.Services.AddSingleton<IntercomConnectionLimiter>();
builder.Services.AddAuthentication(TuvimaAuthDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, TuvimaAuthenticationHandler>(TuvimaAuthDefaults.Scheme, _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser());
    options.AddPolicy(AuthPolicies.Administrator, policy =>
        policy.RequireAuthenticatedUser().AddRequirements(new EffectiveAdministratorRequirement(true)));
    options.AddPolicy(AuthPolicies.AdministratorEligibility, policy =>
        policy.RequireAuthenticatedUser().AddRequirements(new EffectiveAdministratorRequirement(false)));
    options.AddPolicy(AuthPolicies.HumanSelfService, policy =>
        policy.RequireAuthenticatedUser().AddRequirements(new HumanSelfServiceRequirement()));
    options.AddPolicy(AuthPolicies.DashboardService, policy =>
        policy.RequireClaim(TuvimaClaimTypes.DashboardService, "true"));
    options.AddPolicy(AuthPolicies.DashboardInteractive, policy =>
        policy.RequireClaim(TuvimaClaimTypes.DashboardService, "true")
            .RequireClaim(TuvimaClaimTypes.ActiveProfileId));
    options.AddPolicy(AuthPolicies.IntercomConnect, policy =>
        policy.RequireClaim(TuvimaClaimTypes.DashboardService, "true"));
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddScoped<IAuthorizationHandler, EffectiveAdministratorHandler>();
builder.Services.AddScoped<IAuthorizationHandler, HumanSelfServiceHandler>();
builder.Services.AddScoped<IAuthorizationHandler, ApplicationPermissionHandler>();
builder.Services.AddScoped<IAuthorizationHandler, AdministratorOrApplicationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, HumanOrApplicationPermissionHandler>();
builder.Services.AddTuvimaPlayback();
builder.Services.AddTuvimaNetworking();
builder.Services.AddMediaEngineIngestion(config, configLoader);
#if DEBUG
builder.Services.AddSingleton<DevHarnessResetService>();
builder.Services.AddSingleton<DevelopmentTestService>();
builder.Services.AddSingleton<ViewPhotoHarnessService>();
#endif
builder.Services.AddSingleton<AssetStoreCleanupService>();
builder.Services.AddSingleton(sp => new DatabaseBackupService(
    sp.GetRequiredService<IDatabaseConnection>(),
    configDirectory,
    backupDirectory));
builder.Services.AddTuvimaDisplay();
builder.Services.AddTuvimaIntelligence();
builder.Services.AddTuvimaProviders(configLoader);
builder.Services.AddTuvimaAi(configLoader);
builder.Services.AddTuvimaPlugins();
builder.Services.AddPluginApplicationServices();
builder.Services.AddApplicationEvents();
builder.Services.AddApplicationWebhooks();
builder.Services.AddTuvimaHostedServices();

builder.Services.AddSingleton<StartupReadinessService>();
builder.Services.AddHealthChecks()
    .AddCheck<SqliteHealthCheck>("database_integrity", tags: ["readiness", "database", "required"])
    .AddCheck<ConfigurationHealthCheck>("configuration_validity", tags: ["readiness", "configuration", "required"])
    .AddCheck<LibraryRootHealthCheck>("library_storage", tags: ["readiness", "storage", "required"])
    .AddCheck<WatchFolderHealthCheck>("watch_folders", tags: ["readiness", "storage"])
    .AddCheck<ModelReadinessHealthCheck>("ai_models", tags: ["readiness", "models"])
    .AddCheck<ProviderReadinessHealthCheck>("metadata_providers", tags: ["readiness", "providers"])
    .AddCheck<MediaRuntimeHealthCheck>("media_runtime", tags: ["readiness", "native", "media"])
    .AddCheck<WorkerReadinessHealthCheck>("background_workers", tags: ["readiness", "workers", "required"]);

WebApplication app = builder.Build();
try
{
    await app.Services.GetRequiredService<DashboardServiceCredentialBootstrapper>()
        .EnsureAsync()
        .ConfigureAwait(false);
}
catch (DatabaseWriteAccessException exception)
{
    Log.Error("{StartupFailure}", exception.Message);
    Environment.ExitCode = 74;
    await app.DisposeAsync().ConfigureAwait(false);
    return;
}

// -- Middleware pipeline -------------------------------------------------------
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        var traceId = context.TraceIdentifier;
        var isDevelopment = app.Environment.IsDevelopment();
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "The Engine hit an unexpected error.",
            Detail = isDevelopment
                ? feature?.Error.Message
                : "The request failed. Check Engine logs with the trace id for details.",
            Instance = context.Request.Path,
        };
        problem.Extensions["traceId"] = traceId;

        app.Logger.LogError(feature?.Error, "Unhandled API exception for {Path} (trace {TraceId})",
            context.Request.Path, traceId);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(problem);
    });
});
app.UseResponseCompression();
app.UseCors("BlazorWasm");
// SECURITY: rate limiting must run before authentication so credential floods are
// rejected before the first-party session or API-key stores are consulted.
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
#if DEBUG
if (realMediaRun is not null)
{
    app.UseRealMediaProtection();
}
#endif
app.UseOutputCache();
app.UseMiddleware<IntercomTokenAuthenticationMiddleware>();
app.UseMiddleware<InteractiveRequestTrackingMiddleware>();

app.MapHealthChecks("/health").RequireAuthorization(AuthPolicies.Administrator);
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("readiness"),
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            checked_at = DateTimeOffset.UtcNow,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString().ToLowerInvariant(),
                description = entry.Value.Description,
                data = entry.Value.Data,
            }),
        });
    },
}).RequireAuthorization(AuthPolicies.Administrator);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Tuvima Library API v1"));
}

// -- Endpoint registration -----------------------------------------------------
app.MapEngineEndpoints();
app.MapDevelopmentEngineEndpoints();

try
{
    app.Run();
}
catch (IOException ex) when (StartupFailureClassifier.IsAddressAlreadyInUse(ex))
{
    Log.Error(ex, "Tuvima Library Engine could not start because one of its configured addresses is already in use");
    Console.Error.WriteLine("Tuvima Library Engine could not start because ports 61494/61495 are already in use. Stop the process using those ports, then start Tuvima again.");
    Environment.ExitCode = 98;
}
catch (Exception exception) when (StartupFailureClassifier.FindPathAccessDenied(exception) is { } denied)
{
    Log.Error("Tuvima Library Engine could not start because a configured path is not accessible: {AccessFailure}", denied.Message);
    Environment.ExitCode = 77;
}
