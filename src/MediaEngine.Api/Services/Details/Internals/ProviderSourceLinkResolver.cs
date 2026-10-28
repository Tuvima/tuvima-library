using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed class ProviderSourceLinkResolver(
    IReadOnlyList<ProviderConfiguration> providers)
{
    public IReadOnlyList<ExternalSourceLinkViewModel> Resolve(
        IReadOnlyDictionary<string, string> identifiers,
        string? mediaType = null)
    {
        if (identifiers.Count == 0)
        {
            return [];
        }

        var links = new List<ExternalSourceLinkViewModel>();
        foreach (var provider in providers)
        {
            if (provider.UiMetadata?.ExternalLinks is not { Count: > 0 } configuredLinks)
            {
                continue;
            }

            foreach (var (identifierKey, linkConfig) in configuredLinks)
            {
                if (!identifiers.TryGetValue(identifierKey, out var identifierValue)
                    || string.IsNullOrWhiteSpace(identifierValue)
                    || string.IsNullOrWhiteSpace(linkConfig.UrlTemplate))
                {
                    continue;
                }

                var url = OutsideLinks.Expand(
                    linkConfig.UrlTemplate,
                    identifierKey,
                    identifierValue,
                    mediaType);
                if (url is null
                    || links.Any(link => string.Equals(link.Url, url, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                links.Add(new ExternalSourceLinkViewModel
                {
                    Key = identifierKey,
                    Label = string.IsNullOrWhiteSpace(linkConfig.Label)
                        ? $"View on {provider.DisplayName ?? provider.Name}"
                        : linkConfig.Label,
                    Url = url,
                    SourceName = provider.DisplayName ?? provider.Name,
                    Tooltip = linkConfig.Tooltip,
                });
            }
        }

        return links;
    }
}
