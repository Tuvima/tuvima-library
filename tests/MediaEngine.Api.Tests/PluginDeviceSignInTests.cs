using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.Trakt;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// "Approve on another device" sign-in for a plugin's personal credential (Plugin architecture spec C13), with the real Trakt
/// plugin's manifest: the code is asked for with the household's client ID only, the token that comes back is kept encrypted for
/// the one profile that signed in, it is renewed when it runs out, and a site that stops accepting it takes the sign-in away.
/// Every answer from Trakt is a hand-made reply from a scripted handler; nothing here opens a network connection.
/// </summary>
public sealed class PluginDeviceSignInTests : IDisposable
{
    private const string Account = TraktPlugin.AccountName;
    private const string DeviceCode = "device-code-secret";
    private const string ClientSecret = "client-secret-value";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-device-sign-in", Guid.NewGuid().ToString("N"));
    private readonly ApprovalTestKit _kit = new();
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly PluginCatalog _catalog;
    private readonly PluginProfileConnectionService _connections;
    private readonly PluginProfileConnectionRepository _repository;
    private readonly Storage.Secrets.ConfigFolderSecretsStore _secrets;
    private readonly ScriptedTrakt _trakt = new();
    private readonly SteppedClock _clock = new();
    private readonly PluginDeviceSignInService _signIn;
    private readonly Guid _ana = Guid.NewGuid();
    private readonly Guid _ben = Guid.NewGuid();

    public PluginDeviceSignInTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(libraryRoot);
        _loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
        _catalog = new PluginCatalog([new TraktPlugin()], new PluginSettingsService(_loader), _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        _repository = new PluginProfileConnectionRepository(_kit.Database);
        _connections = new PluginProfileConnectionService(
            _catalog, _repository, new PluginOutboxRepository(_kit.Database), new DataProtectionSecretStore(new EphemeralDataProtectionProvider()), _clock);
        _secrets = new Storage.Secrets.ConfigFolderSecretsStore(Path.Combine(_root, "config"));
        _signIn = new PluginDeviceSignInService(_catalog, _connections, _secrets, _trakt.Factory, _clock, NullLogger<PluginDeviceSignInService>.Instance);
        AddProfile(_ana, "Ana");
        AddProfile(_ben, "Ben");
        _catalog.SetEnabled(TraktPlugin.PluginId, true);
    }

    public void Dispose()
    {
        _kit.Dispose();
        _loader.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is left for the OS to clean up.
        }
    }

    // ── The manifest ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTraktPlugin_IsAValidManifestPlugin_WithOneSignInToTraktOnly()
    {
        var registration = _catalog.Get(TraktPlugin.PluginId)!;

        Assert.Null(registration.LoadError);
        Assert.True(registration.Compatibility.IsCompatible);
        Assert.Equal(["api.trakt.tv"], registration.Manifest.Network.Select(destination => destination.Host));
        var personal = _connections.PersonalCredentials(TraktPlugin.PluginId);
        var account = Assert.Single(personal);
        Assert.Equal(Account, account.Name);
        Assert.Equal("api.trakt.tv", account.SignIn!.Host);
        Assert.False(_connections.IsPersonalCredential(TraktPlugin.PluginId, TraktPlugin.ClientIdName));
        Assert.False(_connections.IsPersonalCredential(TraktPlugin.PluginId, TraktPlugin.ClientSecretName));
    }

    [Fact]
    public void ASignInThatContactsAHostTheManifestDoesNotList_IsRefused()
    {
        var manifest = TraktPlugin_With(signIn => signIn with { Host = "evil.example" });

        Assert.Contains("not one of its network hosts", PluginCompatibility.ValidateManifest(manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void ASignInOnAHouseholdCredential_IsRefused()
    {
        var original = new TraktPlugin().Manifest;
        original.Declaration!.CredentialScopes[TraktPlugin.AccountName] = "household";
        var manifest = original;

        Assert.Contains("only a personal credential", PluginCompatibility.ValidateManifest(manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void ASignInWhoseClientIdIsPersonal_OrNotDeclared_IsRefused()
    {
        Assert.Contains("household secret", PluginCompatibility.ValidateManifest(TraktPlugin_With(signIn => signIn with { ClientIdSecret = "nope" })), StringComparison.Ordinal);
        Assert.Contains("household secret", PluginCompatibility.ValidateManifest(TraktPlugin_With(signIn => signIn with { ClientIdSecret = Account })), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("oauth/device/code")]
    [InlineData("/oauth/../code")]
    [InlineData("//evil.example/code")]
    [InlineData("/code?x=1")]
    public void ASignInPathThatIsNotAPlainPathOnTheHost_IsRefused(string path)
    {
        var manifest = TraktPlugin_With(signIn => signIn with { CodePath = path });

        Assert.Contains("invalid code_path", PluginCompatibility.ValidateManifest(manifest), StringComparison.Ordinal);
    }

    // ── Starting ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Starting_AsksTraktForACodeWithTheClientIdOnly_AndGivesTheCodeAndAddressBack()
    {
        await SetUpAppAsync();
        _trakt.Answer("/oauth/device/code", HttpStatusCode.OK, CodeBody());

        var started = await _signIn.StartAsync(TraktPlugin.PluginId, _ana, Account, default);

        Assert.Equal(DeviceSignInOutcome.Started, started.Outcome);
        Assert.Equal("ABCD1234", started.Code!.UserCode);
        Assert.Equal("https://trakt.tv/activate", started.Code.VerificationUrl);
        Assert.Equal(600, started.Code.ExpiresInSeconds);
        Assert.Equal(5, started.Code.IntervalSeconds);
        var request = Assert.Single(_trakt.Requests);
        Assert.Equal("https://api.trakt.tv/oauth/device/code", request.Url);
        Assert.Equal("""{"client_id":"client-id-1"}""", request.Body);
        Assert.DoesNotContain(ClientSecret, request.Body, StringComparison.Ordinal);
        Assert.Equal("2", request.Headers["trakt-api-version"]);
        Assert.Equal("client-id-1", request.Headers["trakt-api-key"]);
        Assert.Empty(request.Authorization);
    }

    [Fact]
    public async Task Starting_WithNoClientId_ExplainsAnAdministratorMustSetItUp_AndAsksTraktNothing()
    {
        var started = await _signIn.StartAsync(TraktPlugin.PluginId, _ana, Account, default);

        Assert.Equal(DeviceSignInOutcome.NeedsSetup, started.Outcome);
        Assert.Empty(_trakt.Requests);
        Assert.True(await _signIn.NeedsSetupAsync(TraktPlugin.PluginId, Account, default));
        await SetUpAppAsync();
        Assert.False(await _signIn.NeedsSetupAsync(TraktPlugin.PluginId, Account, default));
    }

    [Theory]
    [InlineData("client_id")]
    [InlineData("nope")]
    public async Task Starting_ACredentialThatHasNoSignIn_IsNotAsked(string name)
    {
        await SetUpAppAsync();

        var started = await _signIn.StartAsync(TraktPlugin.PluginId, _ana, name, default);

        Assert.Equal(DeviceSignInOutcome.NotAsked, started.Outcome);
        Assert.Empty(_trakt.Requests);
    }

    [Fact]
    public async Task Starting_WhenTraktGivesNoCode_SaysItIsUnavailable()
    {
        await SetUpAppAsync();
        _trakt.Answer("/oauth/device/code", HttpStatusCode.ServiceUnavailable, "{}");

        var started = await _signIn.StartAsync(TraktPlugin.PluginId, _ana, Account, default);

        Assert.Equal(DeviceSignInOutcome.Unavailable, started.Outcome);
    }

    [Fact]
    public async Task Starting_WithAVerificationAddressThatIsNotHttps_IsNotShown()
    {
        await SetUpAppAsync();
        _trakt.Answer("/oauth/device/code", HttpStatusCode.OK, CodeBody(url: "javascript:alert(1)"));

        var started = await _signIn.StartAsync(TraktPlugin.PluginId, _ana, Account, default);

        Assert.Equal(DeviceSignInOutcome.Unavailable, started.Outcome);
    }

    // ── Checking ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhileTheCodeIsNotApproved_TheCheckSaysWaiting_AndNeverAsksBeforeTheIntervalIsUp()
    {
        await StartAsync(_ana);
        _trakt.Answer("/oauth/device/token", HttpStatusCode.BadRequest, "{}");

        var tooSoon = await _signIn.CheckAsync(TraktPlugin.PluginId, _ana, Account, default);
        _clock.Advance(TimeSpan.FromSeconds(5));
        var waiting = await _signIn.CheckAsync(TraktPlugin.PluginId, _ana, Account, default);
        var tooSoonAgain = await _signIn.CheckAsync(TraktPlugin.PluginId, _ana, Account, default);

        Assert.Equal(PluginDeviceSignInStatuses.Waiting, tooSoon.Status);
        Assert.Equal(PluginDeviceSignInStatuses.Waiting, waiting.Status);
        Assert.Equal(PluginDeviceSignInStatuses.Waiting, tooSoonAgain.Status);
        Assert.Single(_trakt.Requests, request => request.Url.EndsWith("/oauth/device/token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnceApproved_TheTokenIsKeptEncrypted_ForThatProfileOnly()
    {
        await StartAsync(_ana);
        _clock.Advance(TimeSpan.FromSeconds(5));
        _trakt.Answer("/oauth/device/token", HttpStatusCode.OK, TokenBody("access-1", "refresh-1", expiresIn: 7_776_000));

        var check = await _signIn.CheckAsync(TraktPlugin.PluginId, _ana, Account, default);

        Assert.Equal(PluginDeviceSignInStatuses.SignedIn, check.Status);
        var token = Assert.Single(_trakt.Requests, request => request.Url.EndsWith("/oauth/device/token", StringComparison.Ordinal));
        var body = JsonDocument.Parse(token.Body).RootElement;
        Assert.Equal(DeviceCode, body.GetProperty("code").GetString());
        Assert.Equal("client-id-1", body.GetProperty("client_id").GetString());
        Assert.Equal(ClientSecret, body.GetProperty("client_secret").GetString());
        var stored = await _repository.GetCiphertextAsync(TraktPlugin.PluginId, _ana, Account);
        Assert.NotNull(stored);
        Assert.DoesNotContain("access-1", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-1", stored, StringComparison.Ordinal);
        Assert.True(await _signIn.HasTokenAsync(TraktPlugin.PluginId, _ana, Account, default));
        Assert.False(await _signIn.HasTokenAsync(TraktPlugin.PluginId, _ben, Account, default));
        Assert.Equal("access-1", await _signIn.AccessTokenAsync(TraktPlugin.PluginId, _ana, Account, default));
        Assert.Null(await _signIn.AccessTokenAsync(TraktPlugin.PluginId, _ben, Account, default));
        Assert.False((await _connections.StatusAsync(TraktPlugin.PluginId, _ben, default))!.Connections.Single().Linked);
    }

    [Fact]
    public async Task ACodeStartedByOneProfile_CannotBeCheckedByAnother()
    {
        await StartAsync(_ana);
        _clock.Advance(TimeSpan.FromSeconds(5));
        _trakt.Answer("/oauth/device/token", HttpStatusCode.OK, TokenBody("access-1", "refresh-1", 7_776_000));

        var check = await _signIn.CheckAsync(TraktPlugin.PluginId, _ben, Account, default);

        Assert.Equal(PluginDeviceSignInStatuses.Expired, check.Status);
        Assert.DoesNotContain(_trakt.Requests, request => request.Url.EndsWith("/oauth/device/token", StringComparison.Ordinal));
        Assert.False(await _signIn.HasTokenAsync(TraktPlugin.PluginId, _ben, Account, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, PluginDeviceSignInStatuses.Expired)]
    [InlineData((HttpStatusCode)418, PluginDeviceSignInStatuses.Denied)]
    [InlineData(HttpStatusCode.NotFound, PluginDeviceSignInStatuses.Failed)]
    [InlineData(HttpStatusCode.Conflict, PluginDeviceSignInStatuses.Failed)]
    public async Task ACodeTraktTurnsDown_EndsTheSignInInPlainWords(HttpStatusCode status, string expected)
    {
        await StartAsync(_ana);
        _clock.Advance(TimeSpan.FromSeconds(5));
        _trakt.Answer("/oauth/device/token", status, "{}");

        var check = await _signIn.CheckAsync(TraktPlugin.PluginId, _ana, Account, default);
        var again = await _signIn.CheckAsync(TraktPlugin.PluginId, _ana, Account, default);

        Assert.Equal(expected, check.Status);
        Assert.False(string.IsNullOrWhiteSpace(check.Message));
        Assert.Equal(PluginDeviceSignInStatuses.Expired, again.Status);
        Assert.False(await _signIn.HasTokenAsync(TraktPlugin.PluginId, _ana, Account, default));
    }

    [Fact]
    public async Task WhenTraktAsksToSlowDown_TheNextCheckWaitsLonger()
    {
        await StartAsync(_ana);
        _clock.Advance(TimeSpan.FromSeconds(5));
        _trakt.Answer("/oauth/device/token", HttpStatusCode.TooManyRequests, "{}");

        var slowed = await _signIn.CheckAsync(TraktPlugin.PluginId, _ana, Account, default);

        Assert.Equal(PluginDeviceSignInStatuses.Waiting, slowed.Status);
        Assert.Equal(6, slowed.IntervalSeconds);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _signIn.CheckAsync(TraktPlugin.PluginId, _ana, Account, default);
        Assert.Single(_trakt.Requests, request => request.Url.EndsWith("/oauth/device/token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACodeLeftUnapprovedPastItsLife_Expires()
    {
        await StartAsync(_ana);
        _clock.Advance(TimeSpan.FromMinutes(11));

        var check = await _signIn.CheckAsync(TraktPlugin.PluginId, _ana, Account, default);

        Assert.Equal(PluginDeviceSignInStatuses.Expired, check.Status);
        Assert.DoesNotContain(_trakt.Requests, request => request.Url.EndsWith("/oauth/device/token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTraktCannotBeReachedWhileWaiting_TheCodeIsKept()
    {
        await StartAsync(_ana);
        _clock.Advance(TimeSpan.FromSeconds(5));
        _trakt.Fail("/oauth/device/token");

        var check = await _signIn.CheckAsync(TraktPlugin.PluginId, _ana, Account, default);

        Assert.Equal(PluginDeviceSignInStatuses.Waiting, check.Status);
        _clock.Advance(TimeSpan.FromSeconds(5));
        _trakt.Answer("/oauth/device/token", HttpStatusCode.OK, TokenBody("access-1", "refresh-1", 7_776_000));
        Assert.Equal(PluginDeviceSignInStatuses.SignedIn, (await _signIn.CheckAsync(TraktPlugin.PluginId, _ana, Account, default)).Status);
    }

    // ── Using the token ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ATokenThatIsStillGood_IsUsedWithoutAskingTrakt()
    {
        await SignInAsync(_ana, "access-1", "refresh-1", expiresIn: 7_776_000);
        _trakt.Requests.Clear();

        var token = await _signIn.AccessTokenAsync(TraktPlugin.PluginId, _ana, Account, default);

        Assert.Equal("access-1", token);
        Assert.Empty(_trakt.Requests);
    }

    [Fact]
    public async Task ATokenThatRanOut_IsRenewedOnce_AndTheNewOnesAreKept()
    {
        await SignInAsync(_ana, "access-1", "refresh-1", expiresIn: 3600);
        _clock.Advance(TimeSpan.FromHours(2));
        _trakt.Requests.Clear();
        _trakt.Answer("/oauth/token", HttpStatusCode.OK, TokenBody("access-2", "refresh-2", 7_776_000));

        var first = await _signIn.AccessTokenAsync(TraktPlugin.PluginId, _ana, Account, default);
        var second = await _signIn.AccessTokenAsync(TraktPlugin.PluginId, _ana, Account, default);

        Assert.Equal("access-2", first);
        Assert.Equal("access-2", second);
        var renewal = Assert.Single(_trakt.Requests);
        var body = JsonDocument.Parse(renewal.Body).RootElement;
        Assert.Equal("refresh-1", body.GetProperty("refresh_token").GetString());
        Assert.Equal("refresh_token", body.GetProperty("grant_type").GetString());
        Assert.Equal(ClientSecret, body.GetProperty("client_secret").GetString());
        Assert.Equal("client-id-1", body.GetProperty("client_id").GetString());
    }

    [Fact]
    public async Task TwoCallsAtOnceForARunOutToken_RenewItOnlyOnce()
    {
        await SignInAsync(_ana, "access-1", "refresh-1", expiresIn: 3600);
        _clock.Advance(TimeSpan.FromHours(2));
        _trakt.Requests.Clear();
        _trakt.Answer("/oauth/token", HttpStatusCode.OK, TokenBody("access-2", "refresh-2", 7_776_000), delay: TimeSpan.FromMilliseconds(100));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _signIn.AccessTokenAsync(TraktPlugin.PluginId, _ana, Account, default)));

        Assert.All(tokens, token => Assert.Equal("access-2", token));
        Assert.Single(_trakt.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ATokenTraktNoLongerHonours_TakesTheSignInAway_SoThePersonIsAskedToSignInAgain(HttpStatusCode status)
    {
        await SignInAsync(_ana, "access-1", "refresh-1", expiresIn: 3600);
        _clock.Advance(TimeSpan.FromHours(2));
        _trakt.Answer("/oauth/token", status, "{}");

        var token = await _signIn.AccessTokenAsync(TraktPlugin.PluginId, _ana, Account, default);

        Assert.Null(token);
        Assert.False(await _signIn.HasTokenAsync(TraktPlugin.PluginId, _ana, Account, default));
    }

    [Fact]
    public async Task WhenTraktCannotBeReachedToRenew_ATokenThatStillWorksIsUsed_AndOneThatDoesNotIsNotThrownAway()
    {
        await SignInAsync(_ana, "access-1", "refresh-1", expiresIn: 3600);
        _clock.Advance(TimeSpan.FromMinutes(58));
        _trakt.Fail("/oauth/token");

        var stillGood = await _signIn.AccessTokenAsync(TraktPlugin.PluginId, _ana, Account, default);
        _clock.Advance(TimeSpan.FromHours(2));
        var ranOut = await _signIn.AccessTokenAsync(TraktPlugin.PluginId, _ana, Account, default);

        Assert.Equal("access-1", stillGood);
        Assert.Null(ranOut);
        Assert.True(await _signIn.HasTokenAsync(TraktPlugin.PluginId, _ana, Account, default));
    }

    [Fact]
    public async Task EachProfilesTokenIsItsOwn()
    {
        await SignInAsync(_ana, "ana-access", "ana-refresh", 7_776_000);
        await SignInAsync(_ben, "ben-access", "ben-refresh", 7_776_000);

        Assert.Equal("ana-access", await _signIn.AccessTokenAsync(TraktPlugin.PluginId, _ana, Account, default));
        Assert.Equal("ben-access", await _signIn.AccessTokenAsync(TraktPlugin.PluginId, _ben, Account, default));
        await _connections.UnlinkAsync(TraktPlugin.PluginId, _ana, Account, default);
        Assert.Null(await _signIn.AccessTokenAsync(TraktPlugin.PluginId, _ana, Account, default));
        Assert.Equal("ben-access", await _signIn.AccessTokenAsync(TraktPlugin.PluginId, _ben, Account, default));
    }

    [Fact]
    public async Task TheTraktAccessTokens_AreTheSignedInProfilesAlone_AndNoneWhileThePluginIsOff()
    {
        await SignInAsync(_ana, "ana-access", "ana-refresh", 7_776_000);
        var tokens = new PluginTraktAccessTokens(_signIn, _catalog);

        Assert.Equal("ana-access", await tokens.TokenAsync(_ana, default));
        Assert.Null(await tokens.TokenAsync(_ben, default));
        Assert.Null(await tokens.TokenAsync(null, default));
        Assert.True(await tokens.HasAccountAsync(_ana, default));
        Assert.False(await tokens.HasAccountAsync(_ben, default));

        _catalog.SetEnabled(TraktPlugin.PluginId, false);
        Assert.Null(await tokens.TokenAsync(_ana, default));
    }

    [Fact]
    public async Task TheTraktSettings_FollowThePluginsSwitchAndTheHouseholdsClientId()
    {
        var settings = new PluginTraktSettings(_catalog, _secrets);

        Assert.Equal(new Services.Collections.ListImport.Sources.TraktSetup(true, null), await settings.LoadAsync(default));
        await SetUpAppAsync();
        Assert.Equal("client-id-1", (await settings.LoadAsync(default)).ClientId);
        _catalog.SetEnabled(TraktPlugin.PluginId, false);
        Assert.Equal(new Services.Collections.ListImport.Sources.TraktSetup(false, null), await settings.LoadAsync(default));
    }

    // ── The Engine actions ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheStartAction_NeedsTheAgreementAndAProfile()
    {
        await SetUpAppAsync();

        var notAgreed = await Execute(await PluginEndpoints.StartPluginSignInAsync(
            TraktPlugin.PluginId, Account, new PluginDeviceSignInStartRequest { Agreed = false }, _kit.Http(), _signIn, ResolverFor(_ana), default));
        var noProfile = await Execute(await PluginEndpoints.StartPluginSignInAsync(
            TraktPlugin.PluginId, Account, new PluginDeviceSignInStartRequest { Agreed = true }, _kit.Http(), _signIn, ResolverFor(null), default));

        Assert.Equal(400, notAgreed.Status);
        Assert.Equal(400, noProfile.Status);
        Assert.Empty(_trakt.Requests);
    }

    [Fact]
    public async Task TheStartAction_GivesTheCode_AndSaysWhenSetUpIsMissingOrTheCredentialIsUnknown()
    {
        var missing = await Execute(await PluginEndpoints.StartPluginSignInAsync(
            TraktPlugin.PluginId, Account, new PluginDeviceSignInStartRequest { Agreed = true }, _kit.Http(), _signIn, ResolverFor(_ana), default));
        await SetUpAppAsync();
        _trakt.Answer("/oauth/device/code", HttpStatusCode.OK, CodeBody());
        var started = await Execute(await PluginEndpoints.StartPluginSignInAsync(
            TraktPlugin.PluginId, Account, new PluginDeviceSignInStartRequest { Agreed = true }, _kit.Http(), _signIn, ResolverFor(_ana), default));
        var unknown = await Execute(await PluginEndpoints.StartPluginSignInAsync(
            TraktPlugin.PluginId, "nope", new PluginDeviceSignInStartRequest { Agreed = true }, _kit.Http(), _signIn, ResolverFor(_ana), default));

        Assert.Equal(400, missing.Status);
        Assert.Contains("administrator", missing.Body, StringComparison.Ordinal);
        Assert.Equal(200, started.Status);
        Assert.Contains("ABCD1234", started.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(DeviceCode, started.Body, StringComparison.Ordinal);
        Assert.Equal(404, unknown.Status);
    }

    [Fact]
    public async Task TheCheckAction_LinksTheSignedInProfile_AndTheConnectionsListShowsIt()
    {
        await SetUpAppAsync();
        _trakt.Answer("/oauth/device/code", HttpStatusCode.OK, CodeBody());
        await Execute(await PluginEndpoints.StartPluginSignInAsync(
            TraktPlugin.PluginId, Account, new PluginDeviceSignInStartRequest { Agreed = true }, _kit.Http(), _signIn, ResolverFor(_ana), default));
        _clock.Advance(TimeSpan.FromSeconds(5));
        _trakt.Answer("/oauth/device/token", HttpStatusCode.OK, TokenBody("access-1", "refresh-1", 7_776_000));

        var check = await Execute(await PluginEndpoints.CheckPluginSignInAsync(TraktPlugin.PluginId, Account, _kit.Http(), _signIn, ResolverFor(_ana), default));
        var forAna = await Execute(await PluginEndpoints.ListPluginConnectionsAsync(_kit.Http(), _catalog, _connections, _signIn, ResolverFor(_ana), default));
        var forBen = await Execute(await PluginEndpoints.ListPluginConnectionsAsync(_kit.Http(), _catalog, _connections, _signIn, ResolverFor(_ben), default));

        Assert.Equal(200, check.Status);
        Assert.Contains("signed_in", check.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("access-1", check.Body + forAna.Body + forBen.Body, StringComparison.Ordinal);
        var anaConnection = JsonDocument.Parse(forAna.Body).RootElement.GetProperty("plugins")[0].GetProperty("connections")[0];
        Assert.True(anaConnection.GetProperty("linked").GetBoolean());
        Assert.True(anaConnection.GetProperty("can_sign_in").GetBoolean());
        Assert.False(anaConnection.GetProperty("sign_in_needs_setup").GetBoolean());
        var benConnection = JsonDocument.Parse(forBen.Body).RootElement.GetProperty("plugins")[0].GetProperty("connections")[0];
        Assert.False(benConnection.GetProperty("linked").GetBoolean());
    }

    [Fact]
    public async Task TheConnectionsList_LeavesOutPluginsThatAreOff_AndNeedsAProfile()
    {
        _catalog.SetEnabled(TraktPlugin.PluginId, false);

        var off = await Execute(await PluginEndpoints.ListPluginConnectionsAsync(_kit.Http(), _catalog, _connections, _signIn, ResolverFor(_ana), default));
        var noProfile = await Execute(await PluginEndpoints.ListPluginConnectionsAsync(_kit.Http(), _catalog, _connections, _signIn, ResolverFor(null), default));

        Assert.Empty(JsonDocument.Parse(off.Body).RootElement.GetProperty("plugins").EnumerateArray());
        Assert.Equal(400, noProfile.Status);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static PluginManifest TraktPlugin_With(Func<PluginDeviceSignIn, PluginDeviceSignIn> change)
    {
        var manifest = new TraktPlugin().Manifest;
        return manifest with
        {
            Secrets = manifest.Secrets
                .Select(secret => secret.SignIn is { } signIn ? secret with { SignIn = change(signIn) } : secret)
                .ToList(),
        };
    }

    private Task SetUpAppAsync() => _secrets.SetAsync(
        SecretOwner.Plugin(TraktPlugin.PluginId),
        new Dictionary<string, string> { [TraktPlugin.ClientIdName] = "client-id-1", [TraktPlugin.ClientSecretName] = ClientSecret });

    private async Task StartAsync(Guid profile)
    {
        await SetUpAppAsync();
        _trakt.Answer("/oauth/device/code", HttpStatusCode.OK, CodeBody());
        var started = await _signIn.StartAsync(TraktPlugin.PluginId, profile, Account, default);
        Assert.Equal(DeviceSignInOutcome.Started, started.Outcome);
    }

    private async Task SignInAsync(Guid profile, string access, string refresh, int expiresIn)
    {
        await StartAsync(profile);
        _clock.Advance(TimeSpan.FromSeconds(5));
        _trakt.Answer("/oauth/device/token", HttpStatusCode.OK, TokenBody(access, refresh, expiresIn));
        Assert.Equal(PluginDeviceSignInStatuses.SignedIn, (await _signIn.CheckAsync(TraktPlugin.PluginId, profile, Account, default)).Status);
    }

    private static string CodeBody(string url = "https://trakt.tv/activate") =>
        $$"""{ "device_code": "{{DeviceCode}}", "user_code": "ABCD1234", "verification_url": "{{url}}", "expires_in": 600, "interval": 5 }""";

    private static string TokenBody(string access, string refresh, int expiresIn) =>
        $$"""{ "access_token": "{{access}}", "token_type": "bearer", "expires_in": {{expiresIn}}, "refresh_token": "{{refresh}}", "scope": "public", "created_at": 1 }""";

    private static IRequestAuthorityResolver ResolverFor(Guid? profileId) =>
        new FixedResolver(new RequestAuthority(PrincipalKind.Human, true, Guid.NewGuid(), profileId));

    private void AddProfile(Guid id, string name)
    {
        using var connection = _kit.Database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@id,@name,'#7C4DFF','StandardUser',@now);",
            new { id = GuidSql.ToBlob(id), name, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    private static async Task<(int Status, string Body)> Execute(IResult result)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private sealed class FixedResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) => ValueTask.FromResult(authority);
    }

    /// <summary>A clock a test moves by hand.</summary>
    private sealed class SteppedClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>What was sent to the stand-in for Trakt.</summary>
    private sealed record SentRequest(string Url, string Body, Dictionary<string, string> Headers, string Authorization);

    /// <summary>Answers each request from a table of path endings, newest first, and remembers what was sent.</summary>
    private sealed class ScriptedTrakt : HttpMessageHandler
    {
        private readonly List<(string Path, Func<HttpResponseMessage> Answer, TimeSpan Delay, bool Fails)> _routes = [];
        private readonly object _gate = new();

        public List<SentRequest> Requests { get; } = [];

        public IHttpClientFactory Factory => new FactoryOf(this);

        public void Answer(string path, HttpStatusCode status, string body, TimeSpan? delay = null)
        {
            lock (_gate)
            {
                _routes.Insert(0, (path, () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }, delay ?? TimeSpan.Zero, false));
            }
        }

        public void Fail(string path)
        {
            lock (_gate)
            {
                _routes.Insert(0, (path, () => throw new HttpRequestException("no route to host"), TimeSpan.Zero, true));
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            (string Path, Func<HttpResponseMessage> Answer, TimeSpan Delay, bool Fails) route;
            lock (_gate)
            {
                Requests.Add(new SentRequest(
                    request.RequestUri!.ToString(),
                    body,
                    request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
                    request.Headers.Authorization?.ToString() ?? string.Empty));
                route = _routes.FirstOrDefault(candidate => request.RequestUri.AbsolutePath.EndsWith(candidate.Path, StringComparison.Ordinal));
            }

            if (route.Answer is null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (route.Delay > TimeSpan.Zero)
            {
                await Task.Delay(route.Delay, cancellationToken);
            }

            return route.Answer();
        }

        private sealed class FactoryOf(HttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        }
    }
}
