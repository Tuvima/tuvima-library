using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Authorization;
using MediaEngine.Plugin.FandomLore;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.ApplicationServices;

internal sealed partial class FandomLoreDiscoveryOperation(
    PluginCatalog catalog,
    IPluginExecutionContextFactory contexts,
    ILibraryUniverseQidGate libraryQids) : IPluginApplicationOperation
{
    internal const string OperationId = "plugin.tuvima.fandom-lore.universe-lore.discover";
    private const int MaxSources = 100;

    internal static PermissionDefinition Permission { get; } = new(
        new(OperationId),
        "Plugins",
        "Discover Fandom lore sources",
        "Allows the application to discover structured Fandom sources for a Wikidata universe.",
        PermissionRiskLevel.Read,
        [ApplicationType.ServerIntegration, ApplicationType.Automation],
        requiresUserContext: false,
        PermissionProvenance.Plugin,
        FandomLorePlugin.PluginId,
        sortOrder: 1_700,
        PermissionAvailability.Available,
        unavailableReason: null);

    public PluginApplicationOperationDescriptor Descriptor { get; } = new(
        OperationId,
        FandomLorePlugin.PluginId,
        "Discover Fandom lore sources",
        "Discovers structured Fandom sources associated with a Wikidata universe.",
        Permission,
        typeof(DiscoverUniverseLoreSourcesRequest),
        typeof(DiscoverUniverseLoreSourcesResponse),
        MaxRequestBytes: 4 * 1024,
        MaxResponseBytes: 256 * 1024,
        Timeout: TimeSpan.FromSeconds(15));

    public bool IsAvailable(out string? reason)
    {
        var registration = catalog.Get(FandomLorePlugin.PluginId);
        if (registration is null)
        {
            reason = "Fandom Lore is not installed.";
            return false;
        }
        if (!registration.Enabled)
        {
            reason = "Fandom Lore is disabled.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(registration.LoadError))
        {
            reason = "Fandom Lore did not load successfully.";
            return false;
        }
        if (!registration.Capabilities.OfType<IUniverseLoreProvider>().Any())
        {
            reason = "Fandom Lore does not provide universe source discovery.";
            return false;
        }

        reason = null;
        return true;
    }

    public async ValueTask<object> InvokeAsync(JsonElement payload, CancellationToken ct)
    {
        var request = PluginApplicationServiceGateway.ReadRequest<DiscoverUniverseLoreSourcesRequest>(payload);
        var qid = request.UniverseQid?.Trim() ?? string.Empty;
        if (!UniverseQidPattern().IsMatch(qid))
        {
            throw new JsonException("universe_qid must be a canonical Wikidata QID.");
        }

        if (!await libraryQids.HoldsAsync(qid, ct).ConfigureAwait(false))
        {
            // Only Universes this Library holds are looked up; the request never reaches Wikidata or Fandom.
            throw new PluginApplicationGatewayException(
                PluginApplicationGatewayFailure.InvalidPayload,
                "universe_qid is not a Universe in this Library.");
        }

        var registration = catalog.Get(FandomLorePlugin.PluginId);
        if (!IsAvailable(out var reason) || registration is null)
        {
            throw new PluginApplicationGatewayException(PluginApplicationGatewayFailure.Unavailable, reason ?? "Fandom Lore is unavailable.");
        }

        try
        {
            await using var context = contexts.Create(FandomLorePlugin.PluginId, "application-service-universe-lore-discover");
            foreach (var healthCheck in registration.Capabilities.OfType<IPluginHealthCheck>())
            {
                var health = await healthCheck.GetHealthAsync(context, ct).ConfigureAwait(false);
                if (!string.Equals(health.Status, "healthy", StringComparison.OrdinalIgnoreCase))
                {
                    throw new PluginApplicationGatewayException(PluginApplicationGatewayFailure.Unavailable, health.Message ?? "Fandom Lore is not healthy.");
                }
            }

            var provider = registration.Capabilities.OfType<IUniverseLoreProvider>().Single();
            var candidates = await provider.DiscoverSourcesAsync(new PluginUniverseLoreContext(qid, null), context, ct)
                .ConfigureAwait(false);
            var sources = candidates
                .Where(candidate => FandomHostRule.IsFandomAddress(candidate.BaseUrl) && FandomHostRule.IsFandomAddress(candidate.ApiUrl))
                .Take(MaxSources).Select(candidate => new PluginLoreSourceCandidateDto(
                candidate.SourceKey,
                candidate.SourceName,
                candidate.BaseUrl,
                candidate.ApiUrl,
                candidate.License,
                Math.Clamp(candidate.Confidence, 0, 1))).ToArray();
            return new DiscoverUniverseLoreSourcesResponse(qid, sources);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new PluginApplicationGatewayException(
                PluginApplicationGatewayFailure.Unavailable,
                "Fandom Lore capability access was revoked.",
                exception);
        }
    }

    [GeneratedRegex("^Q[1-9][0-9]{0,11}$", RegexOptions.CultureInvariant)]
    private static partial Regex UniverseQidPattern();
}
