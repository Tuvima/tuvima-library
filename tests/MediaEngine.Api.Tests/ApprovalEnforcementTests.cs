using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Api.Services.Events;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Webhooks;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Events;
using MediaEngine.Domain.Models;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The approval rule (Developer tools spec D-C) on every action it guards: plugins (the Local AI features are plugins too), application
/// credentials (in <see cref="ApplicationAdministrationServiceTests"/>'s fixture) and webhooks.
/// </summary>
public sealed class ApprovalEnforcementTests : IDisposable
{
    private const string PluginId = "tuvima.test.approval";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-approval-tests", Guid.NewGuid().ToString("N"));
    private readonly ApprovalTestKit _kit = new();
    private readonly ConfigurationDirectoryLoader _loader;

    public ApprovalEnforcementTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(libraryRoot);
        _loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
    }

    public void Dispose()
    {
        _kit.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is left for the OS to clean up.
        }
    }

    // ── Plugins ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnablingAPlugin_WithoutAHash_IsRefusedWithThePreview_AndStaysOff()
    {
        var catalog = Catalog(["media.read", "network.http"]);

        var (status, body) = await Execute(await Enable(catalog, null));

        Assert.Equal(409, status);
        var required = JsonSerializer.Deserialize<ApprovalRequiredResponse>(body)!;
        Assert.Equal("approval_required", required.Error);
        var preview = Assert.Single(required.Approvals);
        Assert.Equal("plugin", preview.Kind);
        Assert.Equal(PluginId, preview.ItemId);
        Assert.Contains("Reads your media files", preview.CanDo);
        Assert.True(preview.SendsDataOut);
        Assert.Equal("api.example.com", preview.SendsDataTo);
        Assert.Contains("Contacts api.example.com: Looks up titles by name", preview.CanDo);
        Assert.False(catalog.Get(PluginId)!.Enabled);
        Assert.Empty(await _kit.Service.ListAsync("plugin", PluginId, default));
    }

    [Fact]
    public async Task EnablingAPlugin_WithTheRightHash_TurnsItOnAndRecordsWhoWhenAndWhatWasShown()
    {
        var catalog = Catalog(["media.read"]);
        var preview = _kit.Service.PreviewPlugin(catalog.Get(PluginId)!);

        var (status, _) = await Execute(await Enable(catalog, preview.StatementHash));

        Assert.Equal(200, status);
        Assert.True(catalog.Get(PluginId)!.Enabled);
        var record = Assert.Single(await _kit.Service.ListAsync("plugin", PluginId, default));
        Assert.Equal(ApprovalTestKit.ApproverName, record.ApprovedByName);
        Assert.Equal(_kit.Clock.GetUtcNow(), record.ApprovedAt);
        Assert.Equal(preview.StatementHash, record.StatementHash);
        Assert.Equal(preview.CanDo, record.Statement.CanDo);
        Assert.Equal(preview.Notes, record.Statement.Notes);
        var stored = Assert.Single(await _kit.Records.ListAsync("plugin", PluginId));
        Assert.Equal(ApprovalTestKit.AccountId, stored.ApprovedByAccountId);
        Assert.Equal(ApprovalTestKit.ProfileId, stored.ApprovedByProfileId);
        Assert.Null(stored.ApprovedByApplicationId);
    }

    [Fact]
    public async Task EnablingAPluginAgainAfterTurningItOff_DoesNotAskAgain()
    {
        var catalog = Catalog(["media.read"]);
        var hash = _kit.Service.PreviewPlugin(catalog.Get(PluginId)!).StatementHash;
        await Enable(catalog, hash);
        catalog.SetEnabled(PluginId, false);

        var (status, _) = await Execute(await Enable(catalog, null));

        Assert.Equal(200, status);
        Assert.True(catalog.Get(PluginId)!.Enabled);
        Assert.Single(await _kit.Records.ListAsync("plugin", PluginId));
    }

    [Fact]
    public async Task APluginThatNowAsksForAnotherPermission_IsAskedAboutAgain()
    {
        var before = Catalog(["media.read"]);
        await Enable(before, _kit.Service.PreviewPlugin(before.Get(PluginId)!).StatementHash);
        before.SetEnabled(PluginId, false);
        var after = Catalog(["media.read", "network.http"]);

        var (status, body) = await Execute(await Enable(after, null));

        Assert.Equal(409, status);
        Assert.Contains("Contacts api.example.com", body, StringComparison.Ordinal);
        Assert.False(after.Get(PluginId)!.Enabled);
    }

    [Fact]
    public async Task APluginThatIsOn_WhoseDestinationChanges_IsNotRunningUntilItIsApprovedAgain()
    {
        PluginCatalog WithDestination(string host) => Catalog(new PluginManifest
        {
            Id = PluginId,
            Name = "Approval test plugin",
            Permissions = ["network.http"],
            Network = [new PluginNetworkDestination { Host = host, Sends = "Looks up titles by name" }],
        });

        var before = WithDestination("api.example.com");
        await Enable(before, _kit.Service.PreviewPlugin(before.Get(PluginId)!).StatementHash);
        Assert.True(before.Get(PluginId)!.Enabled);

        // The same plugin, still switched on in its saved settings, now says it contacts a different host.
        var moved = WithDestination("api.elsewhere.example");

        var registration = moved.Get(PluginId)!;
        Assert.False(registration.Enabled);
        Assert.True(registration.NeedsApproval);
        var (status, body) = await Execute(await Enable(moved, null));
        Assert.Equal(409, status);
        Assert.Contains("Contacts api.elsewhere.example", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnablingAPlugin_WithAWrongHash_IsRefused()
    {
        var catalog = Catalog(["media.read"]);

        var (status, _) = await Execute(await Enable(catalog, new string('a', 64)));

        Assert.Equal(409, status);
        Assert.False(catalog.Get(PluginId)!.Enabled);
        Assert.Empty(await _kit.Records.ListAsync("plugin", PluginId));
    }

    [Fact]
    public async Task APluginApprovedByAnApplication_IsRecordedUnderTheApplicationsName()
    {
        var applicationId = Guid.NewGuid();
        await new ApplicationRepository(_kit.Database).InsertApplicationAsync(new()
        {
            Id = applicationId,
            Name = "Home Assistant",
            ApplicationType = ApplicationType.ServerIntegration,
            CreatedAt = _kit.Clock.GetUtcNow(),
            UpdatedAt = _kit.Clock.GetUtcNow(),
        }, new HashSet<ApplicationPermissionId>());
        var catalog = Catalog(["media.read"]);
        var hash = _kit.Service.PreviewPlugin(catalog.Get(PluginId)!).StatementHash;
        var resolver = new FixedAuthorityResolver(new RequestAuthority(PrincipalKind.ServiceApplication, true, ApplicationId: applicationId));

        await PluginEndpoints.EnablePlugin(PluginId, new PluginEnableRequest { ApprovalHash = hash }, _kit.Http(), catalog, _kit.Service, resolver, default);

        var stored = Assert.Single(await _kit.Records.ListAsync("plugin", PluginId));
        Assert.Equal("Home Assistant", stored.ApprovedByName);
        Assert.Equal(applicationId, stored.ApprovedByApplicationId);
        Assert.Null(stored.ApprovedByAccountId);
    }

    [Fact]
    public async Task DisablingAPlugin_NeedsNoApproval()
    {
        var catalog = Catalog(["media.read"]);
        await Enable(catalog, _kit.Service.PreviewPlugin(catalog.Get(PluginId)!).StatementHash);

        catalog.SetEnabled(PluginId, false);

        Assert.False(catalog.Get(PluginId)!.Enabled);
    }

    [Fact]
    public async Task APluginThatIsOn_WhoseManifestGainsAPermission_IsNotRunningUntilItIsApprovedAgain()
    {
        var catalog = Catalog(["media.read"]);
        var original = _kit.Service.PreviewPlugin(catalog.Get(PluginId)!);
        await Enable(catalog, original.StatementHash);
        Assert.True(catalog.Get(PluginId)!.Enabled);

        // The same plugin, still switched on in its saved settings, now asks for the internet as well.
        var widened = Catalog(["media.read", "network.http"]);

        var registration = widened.Get(PluginId)!;
        Assert.False(registration.Enabled);
        Assert.True(registration.NeedsApproval);
        Assert.Empty(widened.GetEnabledSegmentDetectors());
        var dto = PluginEndpoints.ToDto(registration);
        Assert.False(dto.Enabled);
        Assert.True(dto.NeedsApproval);

        var (status, body) = await Execute(await Enable(widened, null));
        Assert.Equal(409, status);
        Assert.Contains("Contacts api.example.com", body, StringComparison.Ordinal);

        var newHash = _kit.Service.PreviewPlugin(widened.Get(PluginId)!).StatementHash;
        Assert.Equal(200, (await Execute(await Enable(widened, newHash))).Status);
        Assert.True(widened.Get(PluginId)!.Enabled);
        Assert.False(widened.Get(PluginId)!.NeedsApproval);
        Assert.Equal(2, (await _kit.Records.ListAsync("plugin", PluginId)).Count);
    }

    [Fact]
    public async Task APluginThatIsOn_WhoseManifestGainsALocalAiAbility_IsNotRunningUntilItIsApprovedAgain()
    {
        PluginManifest Manifest(params string[] roles) => new()
        {
            Id = PluginId,
            Name = "Approval test plugin",
            Permissions = ["media.read", "ai.infer"],
            AiPermissions = roles.Select(role => new PluginAiPermission { Role = role }).ToList(),
        };

        var before = Catalog(Manifest("text_fast"));
        await Enable(before, _kit.Service.PreviewPlugin(before.Get(PluginId)!).StatementHash);
        Assert.True(before.Get(PluginId)!.Enabled);

        // The same plugin, still switched on in its saved settings, now also asks to write careful text.
        var widened = Catalog(Manifest("text_fast", "text_quality"));

        var registration = widened.Get(PluginId)!;
        Assert.False(registration.Enabled);
        Assert.True(registration.NeedsApproval);
        var (status, body) = await Execute(await Enable(widened, null));
        Assert.Equal(409, status);
        Assert.Contains("Uses Local AI: write text (careful)", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APluginApprovedToRunOneWay_IsNotCoveredWhenItsManifestSaysItRunsAnotherWay()
    {
        var catalog = Catalog(["media.read"]);
        await Enable(catalog, _kit.Service.PreviewPlugin(catalog.Get(PluginId)!).StatementHash);
        var check = new PluginApprovalCheck(_kit.Records, NullLogger<PluginApprovalCheck>.Instance);
        var official = new PluginManifest { Id = PluginId, Name = "Approval test plugin", Permissions = ["media.read"] };

        Assert.True(check.IsCovered(official, PluginTrustLevel.Official));
        Assert.False(check.IsCovered(official with { Kind = PluginKind.Service }, PluginTrustLevel.Official));
        Assert.False(check.IsCovered(official with { Kind = PluginKind.Sandboxed }, PluginTrustLevel.Official));
    }

    [Fact]
    public async Task AnApprovedPlugin_WhoseManifestIsSavedUnchanged_KeepsRunning()
    {
        var pluginRoot = Path.Combine(_root, "library", ".data", "plugins", "sample-plugin");
        var catalog = FilePluginCatalog(pluginRoot, ["media.read"]);
        var hash = _kit.Service.PreviewPlugin(catalog.Get(FilePluginId)!).StatementHash;
        Assert.Equal(200, (await Execute(await PluginEndpoints.EnablePlugin(
            FilePluginId, new PluginEnableRequest { ApprovalHash = hash }, _kit.Http(), catalog, _kit.Service, _kit.Resolver, default))).Status);

        catalog.SaveManifestJson(FilePluginId, catalog.GetManifestJson(FilePluginId));

        Assert.True(catalog.Get(FilePluginId)!.Enabled);
        Assert.False(catalog.Get(FilePluginId)!.NeedsApproval);
    }

    [Fact]
    public async Task SavingAManifestThatAddsAPermission_StopsTheRunningPlugin_UntilItIsApprovedAgain()
    {
        var pluginRoot = Path.Combine(_root, "library", ".data", "plugins", "sample-plugin");
        var catalog = FilePluginCatalog(pluginRoot, ["media.read"]);
        var hash = _kit.Service.PreviewPlugin(catalog.Get(FilePluginId)!).StatementHash;
        await PluginEndpoints.EnablePlugin(FilePluginId, new PluginEnableRequest { ApprovalHash = hash }, _kit.Http(), catalog, _kit.Service, _kit.Resolver, default);
        Assert.True(catalog.Get(FilePluginId)!.Enabled);

        var manifest = JsonSerializer.Deserialize<PluginManifest>(catalog.GetManifestJson(FilePluginId))!;
        catalog.SaveManifestJson(FilePluginId, JsonSerializer.Serialize(manifest with { Permissions = ["media.read", "network.http"], Network = [TestDestination] }));

        var registration = catalog.Get(FilePluginId)!;
        Assert.False(registration.Enabled);
        Assert.True(registration.NeedsApproval);

        var newHash = _kit.Service.PreviewPlugin(registration).StatementHash;
        await PluginEndpoints.EnablePlugin(FilePluginId, new PluginEnableRequest { ApprovalHash = newHash }, _kit.Http(), catalog, _kit.Service, _kit.Resolver, default);
        Assert.True(catalog.Get(FilePluginId)!.Enabled);
    }

    [Fact]
    public async Task AManifestEditedOnDisk_ThenReloaded_IsNotRunningUntilItIsApprovedAgain()
    {
        var pluginRoot = Path.Combine(_root, "library", ".data", "plugins", "sample-plugin");
        var catalog = FilePluginCatalog(pluginRoot, ["media.read"]);
        var hash = _kit.Service.PreviewPlugin(catalog.Get(FilePluginId)!).StatementHash;
        await PluginEndpoints.EnablePlugin(FilePluginId, new PluginEnableRequest { ApprovalHash = hash }, _kit.Http(), catalog, _kit.Service, _kit.Resolver, default);
        Assert.True(catalog.Get(FilePluginId)!.Enabled);

        File.WriteAllText(Path.Combine(pluginRoot, "plugin.json"), PluginJson(["media.read", "process.execute"]));
        catalog.Reload();

        Assert.False(catalog.Get(FilePluginId)!.Enabled);
        Assert.True(catalog.Get(FilePluginId)!.NeedsApproval);
    }

    // ── Webhooks ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatingAnEnabledWebhook_WithoutAHash_IsRefusedWithTheReceiversHost_AndNothingIsCreated()
    {
        var webhooks = WebhookFixture();

        var refused = await Assert.ThrowsAsync<ApprovalRequiredException>(() =>
            webhooks.Service.SaveAsync(webhooks.ApplicationId, null, Request("https://hooks.example.com/a/b"), _kit.Authority, default));

        var preview = Assert.Single(refused.Approvals);
        Assert.Equal("hooks.example.com", preview.SendsDataTo);
        Assert.Empty(await webhooks.Repository.ListAsync(webhooks.ApplicationId));
    }

    [Fact]
    public async Task CreatingAnEnabledWebhook_WithTheHash_SavesItAndRecordsTheApprovalUnderTheNewId_WithoutTheSigningSecret()
    {
        var webhooks = WebhookFixture();
        var request = Request("https://hooks.example.com/a/b", hash: Hash("https://hooks.example.com/a/b"));

        var saved = await webhooks.Service.SaveAsync(webhooks.ApplicationId, null, request, _kit.Authority, default);

        var record = Assert.Single(await _kit.Records.ListAsync("webhook", saved.Webhook.Id.ToString("D")));
        Assert.Equal(ApprovalTestKit.ApproverName, record.ApprovedByName);
        Assert.NotNull(saved.SigningSecret);
        Assert.DoesNotContain(saved.SigningSecret!, record.StatementJson, StringComparison.Ordinal);
        Assert.DoesNotContain("/a/b", record.StatementJson, StringComparison.Ordinal);
        Assert.Equal(saved.Webhook.Id.ToString("D"), JsonSerializer.Deserialize<ApprovalPreviewResponse>(record.StatementJson)!.ItemId);
    }

    [Fact]
    public async Task CreatingADisabledWebhook_NeedsNoApproval_ButEnablingItLaterDoes()
    {
        var webhooks = WebhookFixture();
        var created = await webhooks.Service.SaveAsync(
            webhooks.ApplicationId, null, Request("https://hooks.example.com/a", enabled: false), _kit.Authority, default);

        await Assert.ThrowsAsync<ApprovalRequiredException>(() => webhooks.Service.SaveAsync(
            webhooks.ApplicationId, created.Webhook.Id, Request("https://hooks.example.com/a", expectedVersion: created.Webhook.Version), _kit.Authority, default));

        Assert.Empty(await _kit.Records.ListAsync("webhook", created.Webhook.Id.ToString("D")));
    }

    [Fact]
    public async Task ChangingAnEnabledWebhooksAddress_EventsOrPrivateNetworkSetting_NeedsApprovalButAnUnchangedSaveDoesNot()
    {
        var webhooks = WebhookFixture();
        var url = "https://hooks.example.com/a";
        var created = await webhooks.Service.SaveAsync(webhooks.ApplicationId, null, Request(url, hash: Hash(url)), _kit.Authority, default);
        var version = created.Webhook.Version;

        var unchanged = await webhooks.Service.SaveAsync(
            webhooks.ApplicationId, created.Webhook.Id, Request(url, expectedVersion: version), _kit.Authority, default);
        Assert.Equal(version + 1, unchanged.Webhook.Version);
        version = unchanged.Webhook.Version;

        await Assert.ThrowsAsync<ApprovalRequiredException>(() => webhooks.Service.SaveAsync(
            webhooks.ApplicationId, created.Webhook.Id, Request("https://other.example.com/a", expectedVersion: version), _kit.Authority, default));
        await Assert.ThrowsAsync<ApprovalRequiredException>(() => webhooks.Service.SaveAsync(
            webhooks.ApplicationId, created.Webhook.Id, Request(url, events: ["ingestion.completed", "ingestion.started"], expectedVersion: version), _kit.Authority, default));
        await Assert.ThrowsAsync<ApprovalRequiredException>(() => webhooks.Service.SaveAsync(
            webhooks.ApplicationId, created.Webhook.Id, Request(url, allowLocalNetwork: true, expectedVersion: version), _kit.Authority, default));
    }

    [Fact]
    public async Task ChangingAWebhooksAddress_WithTheNewHash_SavesAndRecordsTheNewStatement()
    {
        var webhooks = WebhookFixture();
        var created = await webhooks.Service.SaveAsync(
            webhooks.ApplicationId, null, Request("https://hooks.example.com/a", hash: Hash("https://hooks.example.com/a")), _kit.Authority, default);

        await webhooks.Service.SaveAsync(
            webhooks.ApplicationId, created.Webhook.Id,
            Request("https://other.example.com/a", hash: Hash("https://other.example.com/a"), expectedVersion: created.Webhook.Version),
            _kit.Authority, default);

        var records = await _kit.Records.ListAsync("webhook", created.Webhook.Id.ToString("D"));
        Assert.Equal(2, records.Count);
        Assert.Contains("other.example.com", records[0].StatementJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisablingAnEnabledWebhook_NeedsNoApproval()
    {
        var webhooks = WebhookFixture();
        var url = "https://hooks.example.com/a";
        var created = await webhooks.Service.SaveAsync(webhooks.ApplicationId, null, Request(url, hash: Hash(url)), _kit.Authority, default);

        var disabled = await webhooks.Service.SaveAsync(
            webhooks.ApplicationId, created.Webhook.Id, Request(url, enabled: false, expectedVersion: created.Webhook.Version), _kit.Authority, default);

        Assert.False(disabled.Webhook.IsEnabled);
    }

    [Fact]
    public async Task TheWebhookFilter_TurnsAnApprovalRequirementIntoTheSharedApprovalRequired409()
    {
        var result = ApiErrors409(new ApprovalRequiredException([_kit.Service.PreviewWebhook(new Uri("https://hooks.example.com/x"), ["a"], false, "")]));

        var (status, body) = await Execute(result);

        Assert.Equal(409, status);
        Assert.Contains("\"error\":\"approval_required\"", body, StringComparison.Ordinal);
    }

    // ── Preview and list ─────────────────────────────────────────────────────

    [Fact]
    public async Task PreviewEndpoint_DescribesAPluginAndRefusesUnknownKindsAndItems()
    {
        var catalog = Catalog(["media.read"]);
        // Only a credential preview reads an application; the plugin and webhook previews here never touch this service.
        ApplicationAdministrationService applications = null!;

        async Task<(int Status, string Body)> Preview(ApprovalPreviewRequest request) => await Execute(
            await ApprovalEndpoints.PreviewAsync(request, _kit.Http(), _kit.Service, _kit.Resolver, applications, catalog, default));

        var ok = await Preview(new ApprovalPreviewRequest { Kind = "plugin", ItemId = PluginId });
        Assert.Equal(200, ok.Status);
        Assert.Contains("Reads your media files", ok.Body, StringComparison.Ordinal);
        Assert.Equal(400, (await Preview(new ApprovalPreviewRequest { Kind = "nonsense" })).Status);
        Assert.Equal(400, (await Preview(new ApprovalPreviewRequest { Kind = "plugin" })).Status);
        Assert.Equal(404, (await Preview(new ApprovalPreviewRequest { Kind = "plugin", ItemId = "no.such.plugin" })).Status);
        var webhook = await Preview(new ApprovalPreviewRequest
        {
            Kind = "webhook",
            Webhook = new ApprovalWebhookInput { Url = "https://hooks.example.com/secret-path", EventTypes = ["a"] },
        });
        Assert.Equal(200, webhook.Status);
        Assert.DoesNotContain("secret-path", webhook.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListEndpoint_ReturnsApprovalsNewestFirst_AndNeedsAKnownKindAndAnItem()
    {
        var catalog = Catalog(["media.read"]);
        var preview = _kit.Service.PreviewPlugin(catalog.Get(PluginId)!);
        await Enable(catalog, preview.StatementHash);
        _kit.Clock.Advance(TimeSpan.FromHours(1));
        await _kit.Service.RecordAsync(preview, "plugin", PluginId, new ApprovalActor(null, null, Guid.NewGuid(), "Second approver"), default);

        var (status, body) = await Execute(await ApprovalEndpoints.ListAsync("plugin", PluginId.ToUpperInvariant(), _kit.Service, catalog, default));

        Assert.Equal(200, status);
        var list = JsonSerializer.Deserialize<List<ApprovalRecordResponse>>(body)!;
        Assert.Equal(["Second approver", ApprovalTestKit.ApproverName], list.Select(r => r.ApprovedByName).ToArray());
        Assert.Equal(400, (await Execute(await ApprovalEndpoints.ListAsync("plugin", null, _kit.Service, catalog, default))).Status);
        Assert.Equal(400, (await Execute(await ApprovalEndpoints.ListAsync("nonsense", "x", _kit.Service, catalog, default))).Status);
    }

    [Fact]
    public async Task ApprovalRoutes_AreForServerAdministratorsOnly()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.MapApprovalEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        Assert.Equal(2, endpoints.Length);
        Assert.All(endpoints, endpoint => Assert.Contains(
            endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>(),
            data => data.Policy == MediaEngine.Api.Security.AuthPolicies.Administrator));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private PluginCatalog Catalog(IReadOnlyList<string> permissions) =>
        Catalog(new PluginManifest { Id = PluginId, Name = "Approval test plugin", Permissions = permissions, Network = NetworkFor(permissions) });

    // A plugin that holds the internet permission must say where it connects.
    private static readonly PluginNetworkDestination TestDestination = new() { Host = "api.example.com", Sends = "Looks up titles by name" };

    private static IReadOnlyList<PluginNetworkDestination> NetworkFor(IReadOnlyList<string> permissions) =>
        permissions.Contains("network.http") ? [TestDestination] : [];

    private PluginCatalog Catalog(PluginManifest manifest)
    {
        var catalog = new PluginCatalog(
            [new TestPlugin(manifest)],
            new PluginSettingsService(_loader),
            _loader,
            new PluginApprovalCheck(_kit.Records, NullLogger<PluginApprovalCheck>.Instance),
            NullLogger<PluginCatalog>.Instance);
        return catalog;
    }

    private const string FilePluginId = "tuvima.test.approval-file";

    // A plugin loaded from a plugin.json under the library's plugin folder, so its manifest can be edited and saved.
    private PluginCatalog FilePluginCatalog(string pluginRoot, IReadOnlyList<string> permissions)
    {
        Directory.CreateDirectory(pluginRoot);
        var testAssemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        File.Copy(testAssemblyPath, Path.Combine(pluginRoot, Path.GetFileName(testAssemblyPath)), overwrite: true);
        File.WriteAllText(Path.Combine(pluginRoot, "plugin.json"), PluginJson(permissions));
        return new PluginCatalog(
            [],
            new PluginSettingsService(_loader),
            _loader,
            new PluginApprovalCheck(_kit.Records, NullLogger<PluginApprovalCheck>.Instance),
            NullLogger<PluginCatalog>.Instance);
    }

    private static string PluginJson(IReadOnlyList<string> permissions) => $$"""
        {
          "id": "{{FilePluginId}}",
          "name": "File approval test",
          "version": "1.0.0",
          "description": "Loaded from plugin.json.",
          "entry_assembly": "MediaEngine.Api.Tests.dll",
          "entry_type": "MediaEngine.Api.Tests.PluginCatalogTests+DynamicTestPlugin",
          "permissions": [{{string.Join(", ", permissions.Select(permission => $"\"{permission}\""))}}]{{(permissions.Contains("network.http") ? ", \"network\": [{ \"host\": \"api.example.com\", \"sends\": \"Looks up titles by name\" }]" : "")}}
        }
        """;

    private async Task<IResult> Enable(PluginCatalog catalog, string? hash) => await PluginEndpoints.EnablePlugin(
        PluginId, hash is null ? null : new PluginEnableRequest { ApprovalHash = hash }, _kit.Http(), catalog, _kit.Service, _kit.Resolver, default);

    private (ApplicationWebhookService Service, ApplicationWebhookRepository Repository, Guid ApplicationId) WebhookFixture()
    {
        var applications = new ApplicationRepository(_kit.Database);
        var applicationId = Guid.NewGuid();
        applications.InsertApplicationAsync(new()
        {
            Id = applicationId,
            Name = "Webhook approval test",
            ApplicationType = ApplicationType.ServerIntegration,
            CreatedAt = _kit.Clock.GetUtcNow(),
            UpdatedAt = _kit.Clock.GetUtcNow(),
        }, new HashSet<ApplicationPermissionId>()).GetAwaiter().GetResult();
        var repository = new ApplicationWebhookRepository(_kit.Database);
        var service = new ApplicationWebhookService(
            repository, applications, new ApplicationEventRepository(_kit.Database), new AllowAllSubscriptions(),
            new ApplicationEventRegistry(new PermissionRegistry()), new WebhookDestinationPolicy(new PublicDns()),
            new EphemeralDataProtectionProvider(), _kit.Clock, _kit.Service);
        return (service, repository, applicationId);
    }

    private string Hash(string url, bool allowLocalNetwork = false) =>
        _kit.Service.PreviewWebhook(new Uri(url), ["ingestion.completed"], allowLocalNetwork, "").StatementHash;

    private static SaveApplicationWebhookRequest Request(
        string url,
        bool enabled = true,
        string? hash = null,
        long? expectedVersion = null,
        bool allowLocalNetwork = false,
        string[]? events = null) =>
        new(url, events ?? ["ingestion.completed"], allowLocalNetwork, enabled, expectedVersion, hash);

    private static IResult ApiErrors409(ApprovalRequiredException exception) => MediaEngine.Api.Http.ApiErrors.ApprovalRequired(exception.Approvals);

    private static async Task<(int Status, string Body)> Execute(IResult result)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private sealed class TestPlugin(PluginManifest manifest) : ITuvimaPlugin
    {
        public PluginManifest Manifest { get; } = manifest;

        public IReadOnlyList<IPluginCapability> CreateCapabilities() => [];
    }

    private sealed class FixedAuthorityResolver(RequestAuthority authority) : MediaEngine.Api.Security.IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) => ValueTask.FromResult(authority);
    }

    private sealed class AllowAllSubscriptions : IApplicationEventDeliveryAuthorizer
    {
        public ValueTask<bool> CanSubscribeAsync(Guid id, IReadOnlyList<string> types, CancellationToken ct = default) => ValueTask.FromResult(true);

        public ValueTask<bool> CanDeliverAsync(Guid id, StoredApplicationEvent value, CancellationToken ct = default) => ValueTask.FromResult(true);
    }

    private sealed class PublicDns : IWebhookDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
    }
}
