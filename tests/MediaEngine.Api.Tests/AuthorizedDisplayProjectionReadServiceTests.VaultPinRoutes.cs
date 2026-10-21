using System.Text;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Security;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

// WP17 part C: the per-route PIN scan. Every NSFW and Vault administration route runs for real (its own request delegate, real data
// store, real PIN service) with a body that carries a PIN and a Vault title, locked and unlocked, with the right PIN and a wrong one.
// Nothing the route says (status line body, headers, an exception it throws) and nothing any service logs may contain a PIN or the title.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private const string ScanRightPin = "2468135";
    private const string ScanWrongPin = "5208174";
    private const string ScanNewPin = "9081726";
    private const string ScanVaultTitle = "Zanzibar Secret Title";

    private static readonly string[] ScanPinsAndTitle = [ScanRightPin, ScanWrongPin, ScanNewPin, ScanVaultTitle];

    private static string PinBody(string pin) => $$"""
        {"pin":"{{pin}}","new_pin":"{{ScanNewPin}}","current_pin":"{{pin}}","profile_pin":"{{pin}}","admin_pin":"{{pin}}",
         "mode":"pin","lookup_source":"none","adults_confirmed":true,"folder_path":"/tmp/{{ScanVaultTitle}}","text":"{{ScanVaultTitle}}"}
        """;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task VaultPinScan_EveryNsfwAndVaultAdminRoute_NeverEchoesOrLogsAPinOrAVaultTitle(bool unlocked, bool rightPin)
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var session = NewVaultSession(shape.Account);
        await new FirstPartyIdentityService(
            new IdentityRepository(_database), _accounts, new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(), TimeProvider.System, new ScanPolicy(),
            nsfwAccess: new NsfwAccessRepository(_database)).SetProfilePinAsync(VaultProfile, ScanRightPin);

        if (unlocked)
        {
            await UnlockVaultAsync(session);
        }

        var logs = new ScanLoggerProvider();
        var routes = await MapRoutesAsync(builder => builder.MapVaultEndpoints(), builder =>
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Services.AddSingleton<IProfilePinVerifier>(sp => new FirstPartyIdentityService(
                sp.GetRequiredService<IIdentityRepository>(), _accounts, new ProfileRepository(_database),
                new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(), TimeProvider.System, new ScanPolicy(),
                nsfwAccess: new NsfwAccessRepository(_database)));
            builder.Services.AddSingleton<IIdentityRepository>(new IdentityRepository(_database));
            builder.Services.AddSingleton<RecentSignInGuard>(sp => new RecentSignInGuard(
                (IFirstPartyIdentityService)sp.GetRequiredService<IProfilePinVerifier>()));
            builder.Services.AddSingleton<VaultAdministrationService>(sp => new VaultAdministrationService(
                sp.GetRequiredService<IVaultLibraryRepository>(), new NsfwAccessRepository(_database),
                new NsfwPinService(new NsfwAccessRepository(_database), sp.GetRequiredService<IIdentityRepository>(),
                    new PasswordHasher<ProfileCredential>(), TimeProvider.System),
                new ProfileRepository(_database), sp.GetRequiredService<IIdentityRepository>(),
                sp.GetRequiredService<IProfilePinVerifier>(), TimeProvider.System,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<VaultAdministrationService>()));
        });

        var pin = rightPin ? ScanRightPin : ScanWrongPin;
        var libraryId = shape.VaultLibrary.ToString("D");
        var calls = new (string Name, (string, string)[] Route, string? Body)[]
        {
            ("GetNsfwStatus", [], null),
            ("UnlockNsfw", [], PinBody(pin)),
            ("KeepNsfwAlive", [], null),
            ("SetNsfwPin", [], PinBody(pin)),
            ("SetProfileNsfwAccess", [("profileId", VaultProfile.ToString("D"))], PinBody(pin)),
            ("ResetProfileNsfwPin", [("profileId", VaultProfile.ToString("D"))], PinBody(pin)),
            ("ListVaultLibraries", [], null),
            ("CreateVaultLibrary", [], PinBody(pin)),
            ("UpdateVaultLibrary", [("libraryId", libraryId)], PinBody(pin)),
            ("AttachVaultFolder", [("libraryId", libraryId)], PinBody(pin)),
            ("RemoveVaultLibrary", [("libraryId", libraryId)], PinBody(pin)),
            ("SearchVault", [], PinBody(pin)),
            ("LockNsfw", [], null),
        };

        // Every route in the group must be covered, so a new one cannot slip past the scan.
        var covered = calls.Select(call => call.Name).ToHashSet(StringComparer.Ordinal);
        var nsfwAndAdmin = routes.Keys.Where(name => name.Contains("Nsfw", StringComparison.Ordinal) || name.Contains("VaultLibrar", StringComparison.Ordinal) || name == "AttachVaultFolder").ToList();
        Assert.All(nsfwAndAdmin, name => Assert.Contains(name, covered));

        var observed = new List<string>();
        foreach (var (name, route, body) in calls)
        {
            observed.Add(await InvokeScanAsync(routes[name], shape.Account, session, route, body));
        }

        var answers = observed.ToList();
        observed.AddRange(logs.Lines);
        // The scan is only worth something if the routes really ran: at least one answered with more than a bare "not found".
        Assert.Contains(answers, text => !text.StartsWith("404", StringComparison.Ordinal));
        foreach (var text in observed)
        {
            foreach (var secret in ScanPinsAndTitle)
            {
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>Runs one route and returns everything it showed the caller: status, headers, body and any exception it threw.</summary>
    private async Task<string> InvokeScanAsync(RouteEndpoint endpoint, Guid account, Guid session, (string Name, string Value)[] route, string? body)
    {
        var app = _routeApps[^1];
        await using var scope = app.Services.CreateAsyncScope();
        var context = VaultContext(account, session);
        context.RequestServices = scope.ServiceProvider;
        foreach (var (name, value) in route)
        {
            context.Request.RouteValues[name] = value;
        }

        context.Response.Body = new MemoryStream();
        if (body is not null)
        {
            context.Request.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody());
        }

        var shown = new StringBuilder();
        try
        {
            await endpoint.RequestDelegate!(context);
        }
        catch (Exception exception)
        {
            // The Engine's exception handler would log this object and, in production, answer with a fixed sentence.
            shown.AppendLine(exception.ToString());
        }

        context.Response.Body.Position = 0;
        shown.AppendLine(context.Response.StatusCode.ToString());
        foreach (var header in context.Response.Headers)
        {
            shown.AppendLine($"{header.Key}: {header.Value}");
        }

        shown.AppendLine(await new StreamReader(context.Response.Body).ReadToEndAsync());
        return shown.ToString();
    }

    private sealed class ScanPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }

    private sealed class ScanLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToList();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new ScanLogger(this);

        public void Dispose()
        {
        }

        private sealed class ScanLogger(ScanLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._lines)
                {
                    owner._lines.Add(formatter(state, exception));
                    if (exception is not null)
                    {
                        owner._lines.Add(exception.ToString());
                    }
                }
            }
        }
    }
}
