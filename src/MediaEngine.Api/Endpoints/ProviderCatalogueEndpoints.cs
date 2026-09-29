using MediaEngine.Api.Security;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Provider catalogue endpoint — serves consolidated provider UI metadata
/// from each provider's config JSON file.
///
/// <para>
/// This centralises display names, accent colours, material icons, external URL
/// templates, category labels, auth types, and per-media-type search/ranking
/// chip labels that were previously hardcoded across ~15 Dashboard files.
/// The Dashboard reads this endpoint once on load and caches the result.
/// </para>
///
/// Access: administrator settings or an Application with provider configuration read permission.
/// Route:  <c>GET /providers/catalogue</c>
/// </summary>
public static class ProviderCatalogueEndpoints
{
    public static IEndpointRouteBuilder MapProviderCatalogueEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/providers").WithTags("Providers");

        // ── GET /providers/catalogue ─────────────────────────────────────────────

        grp.MapGet("/catalogue", (IConfigurationLoader configLoader) =>
        {
            var providers = configLoader.LoadAllProviders();
            var catalogue = providers
                .Where(p => p.ProviderId is not null)   // coded adapters without GUIDs are internal only
                .Select(p => MapToEntry(p))
                .ToList();

            return Results.Ok(catalogue);
        })
        .WithName("GetProviderCatalogue")
        .WithSummary("Returns consolidated UI metadata for all configured providers.")
        .Produces<IReadOnlyList<ProviderCatalogueDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigRead);

        return app;
    }

    // ── Private helpers ──────────────────────────────────────────────────────────

    internal static ProviderCatalogueDto MapToEntry(MediaEngine.Domain.Configuration.ProviderConfiguration p)
    {
        var ui = p.UiMetadata;

        // Build per-media-type search/ranking chip dictionaries
        var searchChips = BuildChips(ui?.SearchChips);
        var rankingChips = BuildChips(ui?.RankingChips);

        // Fall back to display_name → formatted name string
        var displayName = p.DisplayName ?? FormatProviderName(p.Name);

        return new ProviderCatalogueDto
        {
            ProviderId = p.ProviderId!,
            Name = p.Name,
            DisplayName = displayName,
            Enabled = p.Enabled,
            Domain = p.Domain.ToString(),
            MediaTypes = p.CanHandle?.MediaTypes ?? [],
            AccentColor = ui?.AccentColor ?? "#90A4AE",
            MaterialIcon = ui?.MaterialIcon ?? p.CustomIconName ?? "Cloud",
            ExternalUrlTemplate = string.IsNullOrEmpty(ui?.ExternalUrlTemplate) ? null : ui.ExternalUrlTemplate,
            ExternalLinks = ui?.ExternalLinks.ToDictionary(
                pair => pair.Key,
                pair => new ProviderExternalLinkDto
                {
                    Label = pair.Value.Label,
                    UrlTemplate = pair.Value.UrlTemplate,
                    Tooltip = pair.Value.Tooltip,
                },
                StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase),
            Category = ui?.Category ?? "Open",
            RequiresKey = ui?.RequiresKey ?? p.RequiresApiKey,
            AuthType = ui?.AuthType ?? ResolveAuthType(p),
            SearchChips = searchChips,
            RankingChips = rankingChips,
            IconPath = p.Icon,
            HydrationStages = [.. p.HydrationStages],
            Capabilities = [.. p.ProviderCapabilities],
            SystemRole = ui?.SystemRole,
            RequiredSystemProvider = ui?.RequiredSystemProvider ?? false,
            LanguageStrategy = p.LanguageStrategyRaw,
            Onboarding = MapOnboarding(p),
        };
    }

    private static ProviderOnboardingDto? MapOnboarding(
        MediaEngine.Domain.Configuration.ProviderConfiguration provider)
    {
        var onboarding = provider.Onboarding;
        if (onboarding is null)
        {
            return null;
        }

        return new ProviderOnboardingDto
        {
            Classification = onboarding.Classification,
            SignupUrl = onboarding.SignupUrl,
            HelpUrl = onboarding.HelpUrl,
            TermsUrl = onboarding.TermsUrl,
            PrivacyUrl = onboarding.PrivacyUrl,
            SupportedLanes = [.. onboarding.SupportedLanes],
            RequiredScopes = [.. onboarding.RequiredScopes],
            Intro = onboarding.Intro is null
                ? null
                : new ProviderOnboardingIntroDto
                {
                    Title = onboarding.Intro.Title,
                    Summary = onboarding.Intro.Summary,
                },
            Steps = onboarding.Steps.Select(step => new ProviderOnboardingStepDto
            {
                Id = step.Id,
                Title = step.Title,
                Description = step.Description,
                Instructions = [.. step.Instructions],
                CredentialKeys = [.. step.CredentialKeys],
                Action = step.Action is null
                    ? null
                    : new ProviderOnboardingActionDto
                    {
                        Kind = step.Action.Kind,
                        Label = step.Action.Label,
                        Url = step.Action.Url,
                        UrlRole = step.Action.UrlRole,
                    },
            }).ToList(),
            Troubleshooting = onboarding.Troubleshooting.Select(item =>
                new ProviderOnboardingTroubleshootingDto
                {
                    Status = item.Status,
                    Title = item.Title,
                    Message = item.Message,
                    Instructions = [.. item.Instructions],
                    HelpUrl = item.HelpUrl,
                }).ToList(),
            SkipConsequences = onboarding.SkipConsequences.Select(consequence =>
                new ProviderSkipConsequenceDto
                {
                    Lane = consequence.Lane,
                    Summary = consequence.Summary,
                    UnavailableCapabilities = [.. consequence.UnavailableCapabilities],
                }).ToList(),
            Credentials = onboarding.Credentials.Select(field =>
                new ProviderCredentialFieldDto
                {
                    Key = field.Key,
                    Label = field.Label,
                    InputType = field.InputType,
                    Required = field.Required,
                    Ownership = NormalizeOwnership(field.Ownership),
                    Purpose = ResolvePurpose(field),
                    FormatHint = field.FormatHint,
                    MinimumLength = field.MinimumLength,
                    MaximumLength = field.MaximumLength,
                    Configured = IsCredentialConfigured(provider, field.Key),
                }).ToList(),
        };
    }

    private static bool IsCredentialConfigured(
        MediaEngine.Domain.Configuration.ProviderConfiguration provider,
        string key) => key.ToLowerInvariant() switch
        {
            "api_key" => !string.IsNullOrWhiteSpace(provider.HttpClient?.ApiKey)
                || (string.Equals(provider.Name, "tmdb", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(provider.HttpClient?.ApiKeyOverride)),
            "api_key_override" => !string.IsNullOrWhiteSpace(provider.HttpClient?.ApiKeyOverride),
            "client_key" => !string.IsNullOrWhiteSpace(provider.HttpClient?.ClientKey),
            "username" => !string.IsNullOrWhiteSpace(provider.HttpClient?.Username),
            "password" => !string.IsNullOrWhiteSpace(provider.HttpClient?.Password),
            "access_token" => !string.IsNullOrWhiteSpace(provider.HttpClient?.AccessToken),
            "pin" => !string.IsNullOrWhiteSpace(provider.HttpClient?.Pin),
            _ => false,
        };

    private static string NormalizeOwnership(string? ownership) =>
        string.Equals(ownership, "application_managed", StringComparison.OrdinalIgnoreCase)
            ? "application_managed"
            : "user_supplied";

    private static string ResolvePurpose(
        MediaEngine.Domain.Configuration.ProviderCredentialFieldConfiguration field)
    {
        if (!string.IsNullOrWhiteSpace(field.Purpose))
        {
            return field.Purpose.ToLowerInvariant();
        }

        return field.Key.ToLowerInvariant() switch
        {
            "api_key" => "api_key",
            "client_key" => "client_key",
            "username" => "username",
            "password" => "password",
            "access_token" => "access_token",
            _ => "other",
        };
    }

    private static Dictionary<string, List<string>> BuildChips(
        Dictionary<string, List<string>>? source)
    {
        if (source is null or { Count: 0 })
        {
            return [];
        }

        return source.ToDictionary(
            kv => kv.Key,
            kv => kv.Value);
    }

    private static string ResolveAuthType(MediaEngine.Domain.Configuration.ProviderConfiguration p)
    {
        var delivery = p.HttpClient?.ApiKeyDelivery?.ToLowerInvariant();
        return delivery switch
        {
            "bearer" => "bearer",
            "basic" => "basic",
            "query" => "api_key",
            "header" => "api_key",
            _ => "none",
        };
    }

    private static string FormatProviderName(string key) =>
        string.Join(' ', key.Split('_')
            .Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w));
}
