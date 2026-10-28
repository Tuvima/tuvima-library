using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Per-profile connections (Plugin architecture spec C13): a plugin asks each profile to link its own account, the value is stored
/// encrypted, a profile reaches only its own, linking needs the person's agreement, and a household-wide value is refused for a
/// credential that is personal.
/// </summary>
public sealed class PluginProfileConnectionTests : IDisposable
{
    private const string PluginId = "tuvima.test.connect";
    private const string Token = "personal-token-that-must-not-leak";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-connections", Guid.NewGuid().ToString("N"));
    private readonly ApprovalTestKit _kit = new();
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly PluginCatalog _catalog;
    private readonly PluginProfileConnectionService _service;
    private readonly PluginProfileConnectionRepository _repository;
    private readonly Guid _ana = Guid.NewGuid();
    private readonly Guid _ben = Guid.NewGuid();

    public PluginProfileConnectionTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(libraryRoot);
        _loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
        _catalog = new PluginCatalog([new ConnectPlugin()], new PluginSettingsService(_loader), _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        _repository = new PluginProfileConnectionRepository(_kit.Database);
        _service = new PluginProfileConnectionService(_catalog, _repository, new PluginOutboxRepository(_kit.Database), new DataProtectionSecretStore(new EphemeralDataProtectionProvider()), TimeProvider.System);
        AddProfile(_ana, "Ana");
        AddProfile(_ben, "Ben");
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

    [Fact]
    public async Task OnlyPersonalCredentialsAreOffered_AndNothingShowsAsLinkedAtFirst()
    {
        var status = await _service.StatusAsync(PluginId, _ana, default);

        var connection = Assert.Single(status!.Connections);
        Assert.Equal("user_token", connection.Name);
        Assert.False(connection.Linked);
        Assert.Contains("your profile only", status.Consent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linking_StoresTheValueEncrypted_AndOnlyThatProfileSeesItLinked()
    {
        var outcome = await Execute(await PluginEndpoints.LinkPluginConnectionAsync(
            PluginId, "user_token", new PluginConnectionLinkRequest { Value = Token, Agreed = true }, _kit.Http(), _service, ResolverFor(_ana), default));

        Assert.Equal(204, outcome.Status);
        var stored = await _repository.GetCiphertextAsync(PluginId, _ana, "user_token");
        Assert.NotNull(stored);
        Assert.DoesNotContain(Token, stored, StringComparison.Ordinal);
        Assert.True((await _service.StatusAsync(PluginId, _ana, default))!.Connections.Single().Linked);
        Assert.False((await _service.StatusAsync(PluginId, _ben, default))!.Connections.Single().Linked);
        Assert.Equal(Token, await _service.ReadForHostAsync(PluginId, _ana, "user_token", default));
        Assert.Null(await _service.ReadForHostAsync(PluginId, _ben, "user_token", default));
    }

    [Fact]
    public async Task TheStatusAnswer_NeverCarriesTheValue()
    {
        await _service.LinkAsync(PluginId, _ana, "user_token", Token, default);

        var outcome = await Execute(await PluginEndpoints.GetPluginConnectionAsync(PluginId, _kit.Http(), _service, ResolverFor(_ana), default));

        Assert.Equal(200, outcome.Status);
        Assert.DoesNotContain(Token, outcome.Body, StringComparison.Ordinal);
        Assert.Contains("\"linked\":true", outcome.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkingWithoutAgreeing_IsRefused_AndNothingIsStored()
    {
        var outcome = await Execute(await PluginEndpoints.LinkPluginConnectionAsync(
            PluginId, "user_token", new PluginConnectionLinkRequest { Value = Token, Agreed = false }, _kit.Http(), _service, ResolverFor(_ana), default));

        Assert.Equal(400, outcome.Status);
        Assert.Null(await _repository.GetCiphertextAsync(PluginId, _ana, "user_token"));
    }

    [Fact]
    public async Task ACredentialTheHouseholdKeeps_CannotBeLinkedPerProfile_AndABlankValueIsRefused()
    {
        var household = await Execute(await PluginEndpoints.LinkPluginConnectionAsync(
            PluginId, "api_key", new PluginConnectionLinkRequest { Value = Token, Agreed = true }, _kit.Http(), _service, ResolverFor(_ana), default));
        var blank = await Execute(await PluginEndpoints.LinkPluginConnectionAsync(
            PluginId, "user_token", new PluginConnectionLinkRequest { Value = "  ", Agreed = true }, _kit.Http(), _service, ResolverFor(_ana), default));

        Assert.Equal(404, household.Status);
        Assert.Equal(400, blank.Status);
    }

    [Fact]
    public async Task Unlinking_RemovesOnlyTheCallersLink()
    {
        await _service.LinkAsync(PluginId, _ana, "user_token", Token, default);
        await _service.LinkAsync(PluginId, _ben, "user_token", "ben-token", default);

        var outcome = await Execute(await PluginEndpoints.UnlinkPluginConnectionAsync(PluginId, "user_token", _kit.Http(), _service, ResolverFor(_ana), default));

        Assert.Equal(204, outcome.Status);
        Assert.False((await _service.StatusAsync(PluginId, _ana, default))!.Connections.Single().Linked);
        Assert.Equal("ben-token", await _service.ReadForHostAsync(PluginId, _ben, "user_token", default));
    }

    [Fact]
    public async Task SomeoneNotInAProfile_CannotLinkAnything()
    {
        var outcome = await Execute(await PluginEndpoints.GetPluginConnectionAsync(PluginId, _kit.Http(), _service, ResolverFor(null), default));

        Assert.Equal(400, outcome.Status);
    }

    [Fact]
    public async Task ACredentialDeclaredPersonal_IsRefusedAsAHouseholdSecret()
    {
        var secrets = new Storage.Secrets.ConfigFolderSecretsStore(Path.Combine(_root, "config"));
        var outcome = await Execute(await PluginEndpoints.SetPluginSecretAsync(
            PluginId, "user_token", new PluginSecretWriteRequest { Value = Token }, _catalog, secrets, NullLoggerFactory.Instance, default));

        Assert.Equal(400, outcome.Status);
        Assert.DoesNotContain(Token, outcome.Body, StringComparison.Ordinal);
        Assert.Empty(await secrets.ListNamesAsync(MediaEngine.Domain.Contracts.SecretOwner.Plugin(PluginId)));
    }

    [Fact]
    public async Task AnUnknownPlugin_IsNotFound()
    {
        var outcome = await Execute(await PluginEndpoints.GetPluginConnectionAsync("tuvima.test.nope", _kit.Http(), _service, ResolverFor(_ana), default));

        Assert.Equal(404, outcome.Status);
    }

    [Fact]
    public async Task ACiphertextCopiedToAnotherProfilesRow_ReadsAsNothing()
    {
        await _service.LinkAsync(PluginId, _ana, "user_token", Token, default);
        var stolen = await _repository.GetCiphertextAsync(PluginId, _ana, "user_token");
        await _repository.UpsertAsync(PluginId, _ben, "user_token", stolen!, DateTimeOffset.UtcNow);

        Assert.Null(await _service.ReadForHostAsync(PluginId, _ben, "user_token", default));
        Assert.Equal(Token, await _service.ReadForHostAsync(PluginId, _ana, "user_token", default));
    }

    [Fact]
    public async Task TheHostReadFindsTheLink_WhateverTheCaseOfTheIdAndName()
    {
        await _service.LinkAsync(PluginId, _ana, "USER_TOKEN", Token, default);

        Assert.Equal(Token, await _service.ReadForHostAsync(PluginId.ToUpperInvariant(), _ana, "User_Token", default));
    }

    [Fact]
    public void ACredentialScopeFromJson_IsPersonalWhateverTheCaseOrSpacing()
    {
        var declaration = JsonSerializer.Deserialize<PluginDeclaration>("""{ "credential_scopes": { " User_Token ": " Personal " } }""")!;

        Assert.True(PluginDeclarationRules.IsPersonalCredential(declaration, "user_token"));
        Assert.False(PluginDeclarationRules.IsPersonalCredential(declaration, "api_key"));
        Assert.False(PluginDeclarationRules.IsPersonalCredential(null, "user_token"));
    }

    [Fact]
    public async Task APluginReadingItsSecrets_NeverGetsAPersonalCredentialFromTheHouseholdFile()
    {
        var secrets = new Storage.Secrets.ConfigFolderSecretsStore(Path.Combine(_root, "config"));
        await secrets.SetAsync(MediaEngine.Domain.Contracts.SecretOwner.Plugin(PluginId), new Dictionary<string, string> { ["user_token"] = "old-shared", ["api_key"] = "house-key" });
        _catalog.SetEnabled(PluginId, true);
        var bound = new BoundPluginSecrets(PluginId, new PluginPermissionGate(_catalog), secrets);

        Assert.Null(await bound.GetAsync("user_token"));
        Assert.Equal("house-key", await bound.GetAsync("api_key"));
    }

    private static IRequestAuthorityResolver ResolverFor(Guid? profileId) => new FixedResolver(new RequestAuthority(PrincipalKind.Human, true, Guid.NewGuid(), profileId));

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

    private sealed class ConnectPlugin : ITuvimaPlugin
    {
        public PluginManifest Manifest { get; } = new()
        {
            Id = PluginId,
            Name = "Connect Test",
            Version = "1.0.0",
            Permissions = [PluginPermissionIds.PluginSecrets],
            Secrets =
            [
                new PluginSecretDeclaration { Name = "user_token", Label = "Your token", Description = "Your own account token." },
                new PluginSecretDeclaration { Name = "api_key", Label = "App key", Description = "The household's key." },
            ],
            Declaration = new PluginDeclaration
            {
                CredentialScopes = new(StringComparer.OrdinalIgnoreCase) { ["user_token"] = "personal", ["api_key"] = "household" },
            },
        };

        public IReadOnlyList<IPluginCapability> CreateCapabilities() => [];
    }
}
