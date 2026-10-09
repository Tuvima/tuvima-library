using MediaEngine.Domain.Authorization;

namespace MediaEngine.Domain.Tests;

public sealed class AuthorizationFoundationTests
{
    private static readonly string[] ExpectedPermissionIds =
    [
        "ai.infer", "ai.manage", "ai.status.read",
        "analytics.devices.read", "analytics.library.read", "analytics.playback.read", "analytics.users.read",
        "artwork.read", "backup.read", "backup.restore", "backup.run",
        "collections.read", "collections.write", "downloads.read", "downloads.write", "events.subscribe",
        "identity.applications.write", "identity.sessions.read", "identity.users.read", "identity.users.write",
        "ingestion.cancel", "ingestion.history.read", "ingestion.retry", "ingestion.run", "ingestion.status.read",
        "library.changes.read", "library.files.read", "library.read",
        "metadata.enrichment.read", "metadata.enrichment.run", "metadata.match", "metadata.read", "metadata.write",
        "network.config.write", "network.status.read",
        "playback.history.read", "playback.read", "playback.sessions.control", "playback.sessions.read", "playback.write",
        "plugins.jobs.read", "plugins.jobs.run", "plugins.manage", "plugins.read",
        "progress.read", "progress.write", "providers.config.read", "providers.config.write", "providers.status.read",
        "queue.read", "queue.write", "review.read", "review.resolve",
        "storage.config.write", "storage.status.read",
        "system.activity.read", "system.audit.read", "system.logs.read", "system.metrics.read", "system.status.read",
        "view.galleries.read", "view.galleries.write", "view.originals.read", "view.personal.read", "view.shared.read", "view.upload",
    ];

    private static readonly string[] NativePermissionIds =
    [
        "library.read", "artwork.read", "library.changes.read", "progress.read", "progress.write", "queue.read",
        "queue.write", "playback.read", "playback.write", "downloads.read", "downloads.write", "events.subscribe",
    ];

    [Fact]
    public void RegistryContainsTheReviewedPermissionInventoryWithTruthfulAvailability()
    {
        var registry = new PermissionRegistry();
        var definitions = registry.GetAll();

        Assert.Equal(ExpectedPermissionIds, definitions.Select(definition => definition.Id.Value).Order(StringComparer.Ordinal));
        Assert.Equal(definitions.Count, definitions.Select(definition => definition.Id).Distinct().Count());
        Assert.All(definitions.Where(definition => definition.IsAvailable),
            definition => Assert.Null(definition.UnavailableReason));
        Assert.All(definitions.Where(definition => !definition.IsAvailable),
            definition => Assert.False(string.IsNullOrWhiteSpace(definition.UnavailableReason)));
        Assert.All(definitions, definition =>
        {
            Assert.Equal(PermissionProvenance.BuiltIn, definition.Provenance);
            Assert.Null(definition.PluginId);
        });

        var unavailable = definitions.Where(definition => !definition.IsAvailable)
            .Select(definition => definition.Id.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[]
        {
            "ai.infer", "identity.sessions.read",
            "playback.sessions.control", "system.audit.read",
            "system.logs.read", "system.metrics.read",
        }, unavailable);
    }

    [Fact]
    public void NativeClientPermissionIdsRemainExactAndOrdered()
    {
        Assert.Equal(NativePermissionIds, ApplicationPermissionIds.NativeClient.Select(id => id.Value));
        var registry = new PermissionRegistry();
        Assert.All(ApplicationPermissionIds.NativeClient, id =>
            Assert.True(registry.TryGet(id, out var definition) && definition.IsAvailable));
        var nativeList = Assert.IsAssignableFrom<IList<ApplicationPermissionId>>(ApplicationPermissionIds.NativeClient);
        Assert.Throws<NotSupportedException>(() => nativeList.Add(ApplicationPermissionIds.SystemStatusRead));
    }

    [Fact]
    public void MutatingAndInferenceOperationsHaveTruthfulRiskLevels()
    {
        var definitions = new PermissionRegistry().GetAll().ToDictionary(value => value.Id);

        Assert.Equal(PermissionRiskLevel.Write, definitions[ApplicationPermissionIds.MetadataMatch].RiskLevel);
        Assert.Equal(PermissionRiskLevel.Write, definitions[ApplicationPermissionIds.IngestionRetry].RiskLevel);
        Assert.Equal(PermissionRiskLevel.Write, definitions[ApplicationPermissionIds.ReviewResolve].RiskLevel);
        Assert.Equal(PermissionRiskLevel.Sensitive, definitions[ApplicationPermissionIds.AiInfer].RiskLevel);
    }

    [Fact]
    public void PresetIdentifiersRemainExactUiSelectionsRatherThanRoles()
    {
        Assert.Equal(
            ["media-player", "monitoring-analytics", "home-automation", "metadata-integration", "read-only", "administrator", "custom"],
            ApplicationPermissionPresets.All.Select(preset => preset.Id));
        var administrator = Assert.Single(ApplicationPermissionPresets.All, preset => preset.IsAdministrator);
        Assert.Equal("administrator", administrator.Id);
    }

    [Fact]
    public void RegistryAndDefinitionsDoNotExposeMutableCallerCollections()
    {
        var applicationTypes = new HashSet<ApplicationType> { ApplicationType.Automation };
        var plugin = PluginDefinition("plugin.example.status.read", applicationTypes);
        var registry = new PermissionRegistry([plugin]);

        applicationTypes.Add(ApplicationType.Other);
        Assert.DoesNotContain(ApplicationType.Other, plugin.ApplicationTypes);

        var list = Assert.IsAssignableFrom<IList<PermissionDefinition>>(registry.GetAll());
        Assert.Throws<NotSupportedException>(() => list.Add(plugin));
    }

    [Fact]
    public void PluginPermissionsCannotOverrideBuiltInsOrEscapeTheirNamespace()
    {
        Assert.Throws<ArgumentException>(() => new PermissionRegistry([
            PluginDefinition("library.read", [ApplicationType.Automation])
        ]));
        Assert.Throws<ArgumentException>(() => new PermissionRegistry([
            PluginDefinition("plugin.other.status.read", [ApplicationType.Automation])
        ]));
    }

    [Fact]
    public void PluginPermissionNamespacesPreserveDottedBundledIdentifiers()
    {
        var definition = PluginDefinition(
            "plugin.tuvima.fandom-lore.universe-lore.discover",
            [ApplicationType.ServerIntegration],
            "tuvima.fandom-lore");

        var registry = new PermissionRegistry([definition]);

        Assert.True(registry.TryGet(definition.Id, out var registered));
        Assert.Equal("tuvima.fandom-lore", registered.PluginId);
    }

    [Theory]
    [InlineData("tuvima..fandom-lore")]
    [InlineData(".tuvima.fandom-lore")]
    [InlineData("tuvima.fandom-lore.")]
    [InlineData("../tuvima.fandom-lore")]
    [InlineData("tuvima/fandom-lore")]
    public void PluginPermissionNamespacesRejectAmbiguousOrTraversalPluginIdentifiers(string pluginId)
    {
        Assert.Throws<ArgumentException>(() => new PermissionRegistry([
            PluginDefinition("plugin.tuvima.fandom-lore.universe-lore.discover", [ApplicationType.Automation], pluginId)
        ]));
    }

    [Fact]
    public void PluginPermissionDefinitionsRejectOtherProvenanceAndGlobalCollisions()
    {
        var wrongProvenance = new PermissionDefinition(
            new("plugin.tuvima.fandom-lore.universe-lore.discover"), "Plugins", "Discover lore", "Discovers lore sources.",
            PermissionRiskLevel.Read, [ApplicationType.Automation], false, PermissionProvenance.BuiltIn,
            "tuvima.fandom-lore", 1_000, PermissionAvailability.Available, null);
        Assert.Throws<ArgumentException>(() => new PermissionRegistry([wrongProvenance]));

        var duplicate = PluginDefinition("plugin.example.status.read", [ApplicationType.Automation]);
        Assert.Throws<InvalidOperationException>(() => new PermissionRegistry([duplicate, duplicate]));
    }

    [Fact]
    public void AdministratorPresetTracksOnlyCurrentlyAvailableDefinitions()
    {
        var availablePlugin = PluginDefinition("plugin.example.status.read", [ApplicationType.Automation]);
        var unavailablePlugin = new PermissionDefinition(
            new("plugin.example.jobs.run"), "Plugins", "Run jobs", "Runs a plugin job.",
            PermissionRiskLevel.Write, [ApplicationType.Automation], false, PermissionProvenance.Plugin,
            "example", 1_001, PermissionAvailability.Unavailable, "Plugin is disabled.");
        var registry = new PermissionRegistry([availablePlugin, unavailablePlugin]);

        var resolved = ApplicationPermissionPresets.Administrator.ResolveAvailablePermissions(
            registry, ApplicationType.Automation);

        Assert.Contains(availablePlugin.Id, resolved);
        Assert.DoesNotContain(unavailablePlugin.Id, resolved);
        Assert.Contains(ApplicationPermissionIds.EventsSubscribe, resolved);
        Assert.DoesNotContain(ApplicationPermissionIds.PlaybackSessionsControl, resolved);
        Assert.Equal(registry.GetAvailableFor(ApplicationType.Automation).Count, resolved.Count);
    }

    [Fact]
    public void RestrictedProfileNeverHasAdministratorAuthorityEvenWithAnAdminEnabledGrant()
    {
        var authority = new RequestAuthority(
            PrincipalKind.Human, true, Guid.NewGuid(), Guid.NewGuid(),
            AccountEnabled: true, GrantEnabled: true,
            AccountIsAdministrator: true, GrantAdminEnabled: true);

        Assert.True(authority.IsEffectiveAdministrator);
        Assert.False((authority with { ActiveProfileIsRestricted = true }).IsEffectiveAdministrator);
    }

    [Theory]
    [InlineData("this_computer", "this_computer", true)]
    [InlineData("home_network", "home_network", true)]
    [InlineData("home_network", "this_computer", true)]
    [InlineData("this_computer", "home_network", true)]
    [InlineData("home_network", "remote", false)]
    [InlineData("this_computer", "remote", false)]
    [InlineData("remote", "remote", true)]
    [InlineData("remote", "home_network", true)]
    [InlineData("home_network", null, false)]
    [InlineData("home_network", "anything-else", false)]
    [InlineData(null, "remote", true)]
    public void SessionsMadeAtHomeOnlyContinueAtHome(string? issued, string? current, bool expected)
    {
        Assert.Equal(expected, ClientIngress.SessionMayContinue(issued, current));
    }

    [Fact]
    public void EffectiveAdministratorRequiresACompleteEnabledHumanBinding()
    {
        var accountId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var valid = new RequestAuthority(
            PrincipalKind.Human, true, accountId, profileId,
            AccountEnabled: true, GrantEnabled: true,
            AccountIsAdministrator: true, GrantAdminEnabled: true);

        Assert.True(valid.IsEffectiveAdministrator);
        Assert.True((valid with
        {
            PrincipalKind = PrincipalKind.DelegatedUserClient,
            ApplicationId = Guid.NewGuid(),
            ApplicationEnabled = true,
        }).IsEffectiveAdministrator);
        Assert.False((valid with { PrincipalKind = PrincipalKind.DelegatedUserClient }).IsEffectiveAdministrator);
        Assert.False((valid with
        {
            PrincipalKind = PrincipalKind.DelegatedUserClient,
            ApplicationId = Guid.NewGuid(),
            ApplicationEnabled = false,
        }).IsEffectiveAdministrator);
        Assert.False((valid with { PrincipalKind = PrincipalKind.ServiceApplication, ApplicationId = Guid.NewGuid() }).IsEffectiveAdministrator);
        Assert.False((valid with { PrincipalKind = PrincipalKind.DashboardTransport }).IsEffectiveAdministrator);
        Assert.False((valid with { IsAuthenticated = false }).IsEffectiveAdministrator);
        Assert.False((valid with { AccountId = null }).IsEffectiveAdministrator);
        Assert.False((valid with { AccountId = Guid.Empty }).IsEffectiveAdministrator);
        Assert.False((valid with { ActiveProfileId = null }).IsEffectiveAdministrator);
        Assert.False((valid with { AccountEnabled = false }).IsEffectiveAdministrator);
        Assert.False((valid with { GrantEnabled = false }).IsEffectiveAdministrator);
    }

    [Fact]
    public void ServiceAdministratorRequiresItsOwnLiveApplicationIdentity()
    {
        var valid = new RequestAuthority(
            PrincipalKind.ServiceApplication, true,
            ApplicationId: Guid.NewGuid(), ApplicationEnabled: true, ApplicationIsAdministrator: true);

        Assert.True(valid.IsAdministratorApplication);
        Assert.False((valid with { ApplicationId = Guid.Empty }).IsAdministratorApplication);
        Assert.False((valid with { ApplicationEnabled = false }).IsAdministratorApplication);
        Assert.False((valid with { PrincipalKind = PrincipalKind.Human }).IsAdministratorApplication);
    }

    [Fact]
    public void IdentifiersAndDecisionsRejectAmbiguousValues()
    {
        Assert.Throws<ArgumentException>(() => new AccountFeatureId("curate"));
        Assert.Throws<ArgumentException>(() => new ApplicationPermissionId("Library.Read"));
        Assert.Throws<ArgumentException>(() => AuthorizationDecision.Deny(AuthorizationDenialReason.None));
        Assert.Equal(AuthorizationDenialReason.None, AuthorizationDecision.Allow().DenialReason);
    }

    private static PermissionDefinition PluginDefinition(
        string permissionId,
        IEnumerable<ApplicationType> applicationTypes,
        string pluginId = "example") => new(
            new(permissionId), "Plugins", "Example status", "Reads example plugin status.",
            PermissionRiskLevel.Read, applicationTypes, false, PermissionProvenance.Plugin,
            pluginId, 1_000, PermissionAvailability.Available, null);
}
