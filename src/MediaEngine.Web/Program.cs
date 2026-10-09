using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Startup;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Components;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Endpoints;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Branding;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Integration.Clients;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Narration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.WindowsServices;

using var processInstanceLease = ProcessInstanceLease.TryAcquire(ProcessInstanceLease.DashboardLeaseName);
if (!processInstanceLease.IsAcquired)
{
    Console.WriteLine("Tuvima Library Dashboard is already running; the duplicate launch will exit without replacing it.");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpContextAccessor();
SettingsNav.ConfigureEnvironment(builder.Environment.IsProduction());

// ── Windows Service hosting ────────────────────────────────────────────────────
// Integrates with the Windows Service Control Manager when the Dashboard is
// installed as a Windows service via the .exe installer.  No-op on Linux / Docker.
builder.Host.UseWindowsService(options => options.ServiceName = "Tuvima Library Dashboard");
if (!WindowsServiceHelpers.IsWindowsService())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
}

// ── Blazor ────────────────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── Localization ──────────────────────────────────────────────────────────────
// Supported cultures match the curated list in ServerGeneralTab.
var supportedCultures = new[]
{
    "ar", "zh", "zh-TW", "cs", "da", "nl", "en", "fi", "fr", "de",
    "el", "he", "hi", "hu", "id", "it", "ja", "ko", "ms", "no",
    "pl", "pt", "pt-BR", "ro", "ru", "es", "sv", "th", "tr", "uk", "vi",
}.Select(c => new CultureInfo(c)).ToList();

builder.Services.AddLocalization();
builder.Services.Configure<RequestLocalizationOptions>(opts =>
{
    opts.DefaultRequestCulture = new RequestCulture("en");
    opts.SupportedCultures = supportedCultures;
    opts.SupportedUICultures = supportedCultures;
    // Cookie provider first so the Dashboard language selection takes effect.
    opts.RequestCultureProviders.Insert(0, new CookieRequestCultureProvider());
});

// ── First-party UI ─────────────────────────────────────────────────────────────────
builder.Services.AddScoped<AppPopoverService>();
builder.Services.AddScoped<AppDialogService>();
builder.Services.AddScoped<IAppDialogService>(services => services.GetRequiredService<AppDialogService>());
builder.Services.AddScoped<AppToastService>();
builder.Services.AddScoped<IAppToastService>(services => services.GetRequiredService<AppToastService>());
builder.Services.AddMemoryCache();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

// ── Theming ───────────────────────────────────────────────────────────────────
// Singleton: dark-mode-only theme shared across all connections.
builder.Services.AddSingleton<ThemeService>();

// ── Colour Palette ────────────────────────────────────────────────────────────
// Resolve Dashboard config the same way the Engine does so UI palette tokens
// stay aligned with config/ui/palette.json in local, service, and Docker runs.
string configDir = Environment.GetEnvironmentVariable("TUVIMA_CONFIG_DIR")
                ?? builder.Configuration["MediaEngine:ConfigDirectory"]
                ?? "config";
string dataProtectionDir = Environment.GetEnvironmentVariable("TUVIMA_DATA_PROTECTION_DIR")
                        ?? Path.Combine(configDir, ".keys");
Directory.CreateDirectory(dataProtectionDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDir))
    .SetApplicationName("Tuvima.Library");
var dashboardConfig = new DashboardConfigurationReader(configDir);
builder.Services.AddSingleton(dashboardConfig);
var networkSettings = dashboardConfig.LoadNetwork();

// Installed/service deployments use the user-facing network port from the shared
// configuration. Explicit host configuration (launchSettings, ASPNETCORE_URLS,
// container settings) remains authoritative for development and orchestration.
var explicitUrls = builder.Configuration["urls"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
var tailscaleUrl = Environment.GetEnvironmentVariable("TUVIMA_TAILSCALE_URL");
var proxyPort = ProxyPortConfiguration.Resolve(
    networkSettings,
    Environment.GetEnvironmentVariable("TUVIMA_PROXY_PORT"),
    tailscaleUrl);
var proxyUrl = proxyPort is int proxyPortValue ? ProxyPortConfiguration.BindUrl(networkSettings.Remote, proxyPortValue) : null;
if (string.IsNullOrWhiteSpace(explicitUrls))
{
    builder.WebHost.UseUrls(proxyUrl is null
        ? $"http://0.0.0.0:{networkSettings.Local.Port}"
        : $"http://0.0.0.0:{networkSettings.Local.Port};{proxyUrl}");
}
else if (proxyUrl is not null && !ProxyPortConfiguration.UrlsContainPort(explicitUrls, proxyPort!.Value))
{
    // Explicit hosting settings stay authoritative; the optional proxy port is only added on top.
    builder.WebHost.UseUrls($"{explicitUrls};{proxyUrl}");
}

// One answer to "is this request local?" and one list of Host names the Dashboard answers to.
builder.Services.AddSingleton(new IngressClassifier(
    proxyPort,
    dashboardConfig.LoadCore().Auth.TrustedLocalNetworks,
    networkSettings.Remote.TrustedProxies,
    networkSettings.Remote.TrustedProxyNetworks));
builder.Services.AddSingleton(new HostAllowList(
    networkSettings,
    tailscaleUrl,
    Environment.MachineName));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    ForwardedHeaderConfiguration.Configure(
        options,
        networkSettings.Remote,
        tailscaleUrl);
});

var authSettings = dashboardConfig.LoadCore().Auth;
builder.Services.AddSingleton(authSettings.PasswordReset);
builder.Services.AddSingleton<PasswordResetEmailSender>();
var externalSignInAllowed =
    !string.Equals(authSettings.Mode, "Local", StringComparison.OrdinalIgnoreCase) &&
    !string.Equals(authSettings.Mode, "DisabledLocalOnly", StringComparison.OrdinalIgnoreCase);
var configuredExternalProviders = externalSignInAllowed
    ? authSettings.ExternalProviders.Where(provider => provider.Enabled).ToArray()
    : [];
var ssoEnabled = configuredExternalProviders.Length > 0;
builder.Services.AddSingleton(new DashboardAuthUiOptions(ssoEnabled));
builder.Services.AddSingleton(new RegisteredExternalProvidersSnapshot(configuredExternalProviders));
builder.Services.AddScoped<DashboardCookieEvents>();
var authentication = builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "Tuvima.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/auth/login";
        options.AccessDeniedPath = "/auth/denied";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.EventsType = typeof(DashboardCookieEvents);
    });

var registeredExternalProviders = authentication.AddTuvimaExternalProviders(configuredExternalProviders);
builder.Services.AddSingleton<IReadOnlyList<RegisteredExternalAuthProvider>>(registeredExternalProviders);

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAssertion(context =>
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                return true;
            }

            if (context.Resource is not HttpContext http)
            {
                return false;
            }

            var path = http.Request.Path;
            return path.StartsWithSegments("/setup")
                || path.StartsWithSegments("/_blazor")
                || path.StartsWithSegments("/_framework")
                || path.StartsWithSegments("/_content")
                || path.StartsWithSegments("/assets")
                || path.StartsWithSegments("/js")
                || path.StartsWithSegments("/app.css")
                || path.StartsWithSegments("/MediaEngine.Web.styles.css")
                || path.StartsWithSegments("/favicon");
        })
        .Build();
});
builder.Services.AddCascadingAuthenticationState();
// Open screens re-check their sign-in every minute and can be closed at once after a big access change.
builder.Services.AddSingleton<OpenScreenRegistry>();
builder.Services.AddScoped<AuthenticationStateProvider, SessionRevalidatingAuthenticationStateProvider>();
builder.Services.AddScoped<CircuitHandler, OpenScreenCircuitHandler>();

PaletteProvider.Initialize(dashboardConfig.LoadPalette());
builder.Services.AddHostedService<DashboardPaletteReloadService>();

// ── Narration ─────────────────────────────────────────────────────────────────
// Singleton: config-driven phrase templates for hero subtitles and section headings.
builder.Services.AddSingleton<IPhraseTemplateService, PhraseTemplateService>();
builder.Services.AddSingleton<StreamingServiceLogoResolver>();

// ── Engine API HTTP Client ────────────────────────────────────────────────────
// TUVIMA_ENGINE_URL: override the Engine address — essential for Docker where
// the Dashboard and Engine run as separate processes. The Engine only answers to
// loopback host names (its AllowedHosts is localhost;127.0.0.1;[::1]), so use
// "http://127.0.0.1:61495", "http://localhost:61495" or "http://[::1]:61495".
var apiBase = Environment.GetEnvironmentVariable("TUVIMA_ENGINE_URL")
           ?? builder.Configuration["Engine:BaseUrl"]
           ?? "http://localhost:61495";
var configuredMediaGrantKey = builder.Configuration["View:MediaGrantKey"];
var mediaGrantKey = !string.IsNullOrWhiteSpace(configuredMediaGrantKey)
    ? SHA256.HashData(Encoding.UTF8.GetBytes(configuredMediaGrantKey))
    : RandomNumberGenerator.GetBytes(32);
var mediaGrantLifetime = TimeSpan.FromSeconds(Math.Clamp(
    builder.Configuration.GetValue<int?>("View:MediaGrantLifetimeSeconds") ?? 600,
    30,
    900));

// Shared address setup for every HttpClient that talks to the Engine. The service
// credential is loaded at send time so startup/rotation races cannot break DI.
void ConfigureEngineClient(HttpClient client)
{
    client.BaseAddress = new Uri(apiBase);
}

// The circuit-scoped client owns a circuit-scoped active-profile accessor. Its
// server-only handler signs eligible View/Collections requests after the final URI
// is known; the Engine API key is never serialized into browser state.
builder.Services.AddScoped<ActiveProfileAccessor>();
builder.Services.AddScoped<IActiveProfileAccessor>(services => services.GetRequiredService<ActiveProfileAccessor>());
builder.Services.AddSingleton(new DashboardServiceCredentialProviderOptions(configDir));
builder.Services.AddSingleton<DashboardServiceCredentialProvider>();
builder.Services.AddScoped<DashboardSessionAccessor>();
builder.Services.AddTransient<DashboardServiceCredentialHandler>();
builder.Services.AddTransient<DashboardEngineAuthenticationHandler>();
builder.Services.AddTransient<ViewProfileAssertionHandler>(services => new ViewProfileAssertionHandler(
    services.GetRequiredService<IActiveProfileAccessor>()));
builder.Services.AddScoped<DashboardCircuitHttpClientFactory>();
builder.Services.AddScoped<DashboardIdentityClient>(services =>
    ActivatorUtilities.CreateInstance<DashboardIdentityClient>(
        services, services.GetRequiredService<DashboardCircuitHttpClientFactory>()));
builder.Services.AddScoped<AdministratorSurfaceAccessService>();
builder.Services.AddScoped<IAdministratorSurfaceAccessService>(services =>
    services.GetRequiredService<AdministratorSurfaceAccessService>());
builder.Services.AddSingleton(new ViewMediaGrantService(mediaGrantKey, mediaGrantLifetime));
builder.Services.AddScoped<UserProgressChangeNotifier>();
builder.Services.AddScoped<EngineApiClient>(services => new EngineApiClient(
    services.GetRequiredService<DashboardCircuitHttpClientFactory>().CreateClient("EngineApi"),
    services.GetRequiredService<ILogger<EngineApiClient>>(),
    services.GetRequiredService<UserProgressChangeNotifier>(),
    services.GetRequiredService<IActiveProfileAccessor>(),
    services.GetService<StreamingServiceLogoResolver>(),
    services.GetService<ILoggerFactory>(),
    services.GetService<EngineApiFailureState>(),
    services.GetService<OpenScreenRegistry>()));
builder.Services.AddScoped<IEngineApiClient>(services => services.GetRequiredService<EngineApiClient>());
builder.Services.AddScoped<EngineApiFailureState>();
builder.Services.AddHttpClient("EngineViewMedia", ConfigureEngineClient)
    .AddHttpMessageHandler<DashboardEngineAuthenticationHandler>()
    .AddHttpMessageHandler<ViewProfileAssertionHandler>();
builder.Services.AddScoped<ViewMediaEngineClient>(services => new ViewMediaEngineClient(
    services.GetRequiredService<DashboardCircuitHttpClientFactory>().CreateClient("EngineViewMedia")));
builder.Services.AddScoped<IViewMediaEngineClient>(services => services.GetRequiredService<ViewMediaEngineClient>());
builder.Services.AddHttpClient("EngineCollectionPersonalMedia", ConfigureEngineClient)
    .AddHttpMessageHandler<DashboardEngineAuthenticationHandler>()
    .AddHttpMessageHandler<ViewProfileAssertionHandler>();
builder.Services.AddScoped<CollectionPersonalMediaClient>(services => new CollectionPersonalMediaClient(
    services.GetRequiredService<DashboardCircuitHttpClientFactory>().CreateClient("EngineCollectionPersonalMedia"),
    services.GetRequiredService<ILogger<CollectionPersonalMediaClient>>()));
builder.Services.AddScoped<ICollectionPersonalMediaClient>(services => services.GetRequiredService<CollectionPersonalMediaClient>());

// Named "EngineApi" client — same base address and API key as the scoped client above.
// Used by ad-hoc pages (e.g. the Enrichment Tester) that need direct HttpClient access
// without routing through IEngineApiClient.
builder.Services.AddHttpClient("EngineApi", ConfigureEngineClient)
    .AddHttpMessageHandler<DashboardEngineAuthenticationHandler>()
    .AddHttpMessageHandler<ViewProfileAssertionHandler>();
// Artwork requests are already authorized at the same-origin Dashboard route.
// Forward only the server-held service credential so a shelf of images does not
// repeat user-session validation and a SQLite lookup for every image.
builder.Services.AddHttpClient("EngineArtwork", ConfigureEngineClient)
    .AddHttpMessageHandler<DashboardEngineAuthenticationHandler>();
builder.Services.AddHttpClient("EngineIdentity", ConfigureEngineClient)
    .AddHttpMessageHandler<DashboardEngineAuthenticationHandler>();
builder.Services.AddSingleton(new ExposureSettingsReader(dashboardConfig, configDir));
// The app door never carries the Dashboard's service credential. No cookies are stored or replayed
// between apps, and Engine redirects are handed back instead of followed.
builder.Services.AddSingleton<INativeAppAccessGate>(new NetworkSettingsNativeAppAccessGate(dashboardConfig, configDir));
builder.Services.AddSingleton<NativeAppPairingThrottle>();
builder.Services.AddSingleton<SignInAttemptLimiter>();
builder.Services.AddClientApiProxyClient(new Uri(apiBase));
builder.Services.AddHealthChecks()
    .AddCheck<DashboardEngineHealthCheck>("engine_liveness", tags: ["readiness"]);

// ── State + Orchestration (scoped = one per SignalR circuit) ──────────────────
builder.Services.AddScoped<UniverseStateContainer>();
builder.Services.AddScoped<ActiveProfileSessionService>();
builder.Services.AddScoped<ViewWorkspaceService>();
builder.Services.AddScoped<ViewAssetDragService>();
builder.Services.AddScoped<UIOrchestratorService>();
builder.Services.AddScoped<IngestionLiveDashboardState>();
builder.Services.AddScoped<MediaEditorLauncherService>();
builder.Services.AddScoped<CollectionEditorLauncherService>();
builder.Services.AddScoped<GalleryEditorLauncherService>();
builder.Services.AddScoped<MediaTileComposerService>();
builder.Services.AddScoped<RecentViewAssetAdapter>();
builder.Services.AddScoped<SavedItemService>();
builder.Services.AddScoped<MediaReactionService>();
builder.Services.AddSingleton(dashboardConfig.LoadPlaybackClientSettings());
builder.Services.AddScoped<PlaybackSessionController>();
builder.Services.AddScoped<AudiobookBookmarkActionService>(services =>
{
    var nativeOwner = services.GetRequiredService<IAudiobookBookmarkNativeOwner>();
    return new AudiobookBookmarkActionService(
        services.GetRequiredService<IEngineApiClient>(),
        (context, draft, ct) => nativeOwner.PreviewCapturedDraftAsync(context, draft, ct));
});
builder.Services.AddScoped<IAudiobookBookmarkCaptureOwner>(services => services.GetRequiredService<AudiobookBookmarkActionService>());
builder.Services.AddScoped<IAudiobookBookmarkLeaseInvalidator>(services => services.GetRequiredService<AudiobookBookmarkActionService>());
builder.Services.AddScoped<IAudiobookBookmarkAuthoritySource, PlaybackAudiobookBookmarkAuthoritySource>();
builder.Services.AddScoped<IAudiobookBookmarkNativeOwner, PlaybackAudiobookBookmarkNativeOwner>();
builder.Services.AddScoped<AudiobookBookmarkCommandDispatcher>(services =>
{
    var nativeOwner = services.GetRequiredService<IAudiobookBookmarkNativeOwner>();
    var actionService = services.GetRequiredService<AudiobookBookmarkActionService>();
    return new AudiobookBookmarkCommandDispatcher(
        services.GetRequiredService<ListenPlaybackCommandOwner>().RecipientId,
        actionService,
        actionService,
        actionService,
        nativeOwner,
        services.GetRequiredService<IAudiobookBookmarkAuthoritySource>());
});
builder.Services.AddScoped<ListenPlaybackCommandOwner>();
builder.Services.AddScoped<IPlaybackCommandSink, DirectPlaybackCommandSink>();
builder.Services.AddScoped<PlaybackLyricsSelectionOwner>();
builder.Services.AddScoped<PlaybackIdentityNavigationOwner>();
builder.Services.AddScoped<PlaybackLyricsPresenter>();
builder.Services.AddScoped<PlaybackTransientToolCoordinator>();
builder.Services.AddScoped<VideoPresentationResolver>();
builder.Services.AddScoped<IListenPlaybackCommandChannel, BroadcastListenPlaybackCommandChannel>();
builder.Services.AddScoped<IAudiobookBookmarkActions>(services =>
{
    var recipientId = services.GetRequiredService<ListenPlaybackCommandOwner>().RecipientId;
    var dispatcher = services.GetRequiredService<AudiobookBookmarkCommandDispatcher>();
    return new ListenPlaybackCommandActionsClient(recipientId, recipientId,
        new InProcessListenPlaybackCommandChannel(recipientId, dispatcher));
});
builder.Services.AddScoped<ShellActivityState>();
builder.Services.AddScoped<ActivityNotificationQueue>();
builder.Services.AddScoped<ListenAudioDragService>();
builder.Services.AddScoped<ListenPageState>();
builder.Services.AddScoped<IUserPlaybackPreferencesAccessor, UserPlaybackPreferencesAccessor>();
builder.Services.AddScoped<ContextSidebarPreferences>();
builder.Services.AddScoped<IContextSidebarPreferences>(services => services.GetRequiredService<ContextSidebarPreferences>());
builder.Services.AddScoped<ListenContextSidebarState>();
builder.Services.AddScoped<ContextSidebarCoordinator>();

// Provider Catalogue (scoped = one API client per SignalR circuit).
// Caches provider UI metadata from GET /providers/catalogue in IMemoryCache while
// keeping the circuit-scoped Engine API failure state out of the root provider.
builder.Services.AddScoped<ProviderCatalogueService>();
builder.Services.AddScoped<MetadataSettingsStateService>();

// ── Device Context (scoped = per-tab; a TV in television mode won't affect a mobile session) ──
// Generalised device-context model supporting web, mobile, television, and automotive classes.
builder.Services.AddScoped<DeviceContextService>();

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

if (proxyPort is null
    && (networkSettings.Remote.TrustedProxies.Count > 0 || networkSettings.Remote.TrustedProxyNetworks.Count > 0))
{
    app.Logger.LogWarning(
        "Trusted proxies are configured but no proxy port is set (remote.proxy_port). Forwarded headers are ignored " +
        "on the main port, so point the reverse proxy at a proxy port or its visitors will look like one shared remote address " +
        "and sign-in is refused for them.");
}

// Forwarded scheme/client information must be established before HSTS,
// redirection, authentication, and URL generation. Forwarded headers are honoured
// only on the dedicated proxy port (remote.proxy_port), from loopback plus the
// explicitly configured proxy addresses/networks; the main port ignores them.
app.UseForwardedHeadersOnProxyPort(proxyPort);
app.UseHostAllowList();
app.UseExposurePolicy();
app.UseWebSockets();
app.UseResponseCompression();
if (app.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TUVIMA_HOME_MEDIA_QA")))
{
    app.UseMiddleware<MediaEngine.Web.Services.HomeMediaQaTextMiddleware>();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseRequestLocalization();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("readiness"),
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync(report.Status.ToString().ToLowerInvariant());
    },
}).AllowAnonymous();

app.MapGet("/_tuvima/remote-probe", (HttpContext context, string? nonce) => Results.Ok(new
{
    product = "Tuvima Library",
    nonce = nonce ?? string.Empty,
    secure = context.Request.IsHttps,
})).AllowAnonymous();

// ── Culture cookie setter ─────────────────────────────────────────────────────
// Sets the ASP.NET Core culture cookie and redirects back to the requested page.
// Called by ServerGeneralTab via forceLoad navigation after saving a new language.
app.MapGet("/culture/set", (string culture, string redirectUri, HttpContext ctx) =>
{
    if (supportedCultures.Any(c => string.Equals(c.Name, culture, StringComparison.OrdinalIgnoreCase)))
    {
        ctx.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true });
    }
    return Results.Redirect(redirectUri);
}).AllowAnonymous();

app.MapMethods("/engine-stream/{assetId:guid}", [HttpMethods.Get, HttpMethods.Head], ProxyEngineStreamAsync)
    .WithName("ProxyEngineMediaStream")
    .WithSummary("Proxies Engine media bytes through the Dashboard origin for browser media playback.")
    .RequireAuthorization();

app.MapGet("/engine-subtitles/{assetId:guid}", async (Guid assetId, string? language, HttpContext ctx, IHttpClientFactory factory, CancellationToken ct) =>
{
    using var request = new HttpRequestMessage(HttpMethod.Get, $"/stream/{assetId:D}/subtitles?language={Uri.EscapeDataString(language ?? "en")}");
    using var response = await factory.CreateClient("EngineApi").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    ctx.Response.StatusCode = (int)response.StatusCode;
    CopyResponseHeaders(response, ctx.Response);
    await response.Content.CopyToAsync(ctx.Response.Body, ct);
}).RequireAuthorization();

app.MapGet("/engine-text-track/{assetId:guid}/{trackId:guid}", async (Guid assetId, Guid trackId, HttpContext ctx, IHttpClientFactory factory, CancellationToken ct) =>
{
    using var request = new HttpRequestMessage(HttpMethod.Get, $"/stream/{assetId:D}/text-tracks/{trackId:D}");
    using var response = await factory.CreateClient("EngineApi").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    ctx.Response.StatusCode = (int)response.StatusCode;
    CopyResponseHeaders(response, ctx.Response);
    await response.Content.CopyToAsync(ctx.Response.Body, ct);
}).RequireAuthorization();

app.MapMethods(
        "/engine-hls/{grant}/{packageId:guid}/{**resourcePath}",
        [HttpMethods.Get, HttpMethods.Head],
        ProxyEngineHlsAsync)
    .WithName("ProxyEngineAdaptiveHls")
    .WithSummary("Proxies one signed adaptive HLS package through the Dashboard origin.")
    .RequireAuthorization();

app.MapMethods("/engine-image/{**enginePath}", [HttpMethods.Get, HttpMethods.Head], ProxyEngineImageAsync)
    .WithName("ProxyEngineImage")
    .WithSummary("Proxies authenticated Engine artwork through the Dashboard origin.")
    .RequireAuthorization();

app.MapViewMediaProxy();

app.MapDashboardAuthenticationEndpoints(registeredExternalProviders);
app.MapClientApiEdge();

if (registeredExternalProviders.Count > 0)
{
    app.MapGet("/auth/external/{providerId}", (string providerId, string? returnUrl) =>
        {
            var provider = registeredExternalProviders.FirstOrDefault(
                candidate => candidate.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
            {
                return Results.NotFound();
            }

            var safeReturnUrl = !string.IsNullOrWhiteSpace(returnUrl)
                && returnUrl.StartsWith('/')
                && !returnUrl.StartsWith("//", StringComparison.Ordinal)
                    ? returnUrl
                    : "/";
            return Results.Challenge(
                new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = safeReturnUrl },
                [provider.AuthenticationScheme]);
        })
        .AllowAnonymous();
}

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

try
{
    app.Run();
}
catch (IOException ex) when (StartupFailureClassifier.IsAddressAlreadyInUse(ex))
{
    app.Logger.LogError(ex, "Tuvima Library Dashboard could not start because one of its configured addresses is already in use");
    Console.Error.WriteLine("Tuvima Library Dashboard could not start because port 5016 is already in use. Stop the process using that port, then start Tuvima Library again.");
    Environment.ExitCode = 98;
}

static async Task ProxyEngineStreamAsync(
    Guid assetId,
    HttpContext ctx,
    IHttpClientFactory httpFactory,
    CancellationToken ct)
{
    var client = httpFactory.CreateClient("EngineApi");
    using var request = new HttpRequestMessage(HttpMethod.Get, $"/stream/{assetId:D}");
    CopyRequestHeader(ctx, request, "Range");
    CopyRequestHeader(ctx, request, "If-Range");

    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    ctx.Response.StatusCode = (int)response.StatusCode;
    CopyResponseHeaders(response, ctx.Response);

    if (HttpMethods.IsHead(ctx.Request.Method))
    {
        return;
    }

    await response.Content.CopyToAsync(ctx.Response.Body, ct);
}

static async Task ProxyEngineHlsAsync(
    string grant,
    Guid packageId,
    string? resourcePath,
    HttpContext ctx,
    IHttpClientFactory httpFactory,
    CancellationToken ct)
{
    var normalizedPath = (resourcePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
    if (string.IsNullOrWhiteSpace(normalizedPath)
        || normalizedPath.Split('/').Any(segment => segment is ".." or "." || string.IsNullOrWhiteSpace(segment)))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var client = httpFactory.CreateClient("EngineApi");
    using var request = new HttpRequestMessage(
        HttpMethods.IsHead(ctx.Request.Method) ? HttpMethod.Head : HttpMethod.Get,
        $"/stream/hls/{Uri.EscapeDataString(grant)}/{packageId:D}/{normalizedPath}");
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    ctx.Response.StatusCode = (int)response.StatusCode;
    CopyResponseHeaders(response, ctx.Response);
    if (!HttpMethods.IsHead(ctx.Request.Method))
    {
        await response.Content.CopyToAsync(ctx.Response.Body, ct);
    }
}

static async Task ProxyEngineImageAsync(
    string enginePath,
    HttpContext ctx,
    IHttpClientFactory httpFactory,
    CancellationToken ct)
{
    var upstreamPath = $"/{enginePath.TrimStart('/')}";
    if (!EngineImageProxyPath.IsAllowedEnginePath(upstreamPath))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var client = httpFactory.CreateClient("EngineArtwork");
    using var request = new HttpRequestMessage(
        HttpMethods.IsHead(ctx.Request.Method) ? HttpMethod.Head : HttpMethod.Get,
        $"{upstreamPath}{ctx.Request.QueryString}");
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    ctx.Response.StatusCode = (int)response.StatusCode;
    CopyResponseHeaders(response, ctx.Response);

    if (!HttpMethods.IsHead(ctx.Request.Method))
    {
        await response.Content.CopyToAsync(ctx.Response.Body, ct);
    }
}

static void CopyRequestHeader(HttpContext ctx, HttpRequestMessage request, string headerName)
{
    if (ctx.Request.Headers.TryGetValue(headerName, out var values))
    {
        request.Headers.TryAddWithoutValidation(headerName, values.ToArray());
    }
}

static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse target)
{
    foreach (var header in source.Headers)
    {
        target.Headers[header.Key] = header.Value.ToArray();
    }

    foreach (var header in source.Content.Headers)
    {
        target.Headers[header.Key] = header.Value.ToArray();
    }

    target.Headers.Remove("transfer-encoding");
}

sealed class DashboardEngineHealthCheck(IHttpClientFactory httpClientFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient("EngineApi");
            using var response = await client.GetAsync("/health/live", cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("The Dashboard can reach the Engine liveness endpoint.")
                : HealthCheckResult.Unhealthy($"The Engine liveness endpoint returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The Dashboard cannot reach the Engine liveness endpoint.", ex);
        }
    }
}
