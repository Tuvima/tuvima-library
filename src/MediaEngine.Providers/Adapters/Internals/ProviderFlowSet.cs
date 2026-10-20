using MediaEngine.Domain.Configuration;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Adapters;

/// <summary>
/// The parsed <c>flows</c> of one provider file, ready to answer fetch and search requests.
/// A flow that fails answers "nothing found" and logs a warning, as the providers that used to
/// code these lookups did; provider health is not touched.
/// </summary>
internal sealed class ProviderFlowSet
{
    private readonly ProviderConfiguration _config;
    private readonly ProviderApiClient _api;
    private readonly ILogger _logger;
    private readonly FlowProgram? _fetch;
    private readonly FlowProgram? _search;

    public ProviderFlowSet(ProviderConfiguration config, ProviderApiClient api, ILogger logger)
    {
        _config = config;
        _api = api;
        _logger = logger;
        _fetch = Compile(config.Flows?.Fetch, "flows.fetch.steps");
        _search = Compile(config.Flows?.Search, "flows.search.steps");
    }

    public bool HasFetch => _fetch is not null;

    public bool HasSearch => _search is not null;

    private static FlowProgram? Compile(ProviderFlowConfiguration? flow, string path)
    {
        if (flow is null)
        {
            return null;
        }

        var errors = new List<string>();
        var program = FlowParser.Parse(flow.Steps, path, errors);
        return errors.Count == 0
            ? program
            : throw new InvalidOperationException($"The flow at {path} is not valid: {string.Join(" ", errors)}");
    }

    public async Task<IReadOnlyList<ProviderClaim>> FetchAsync(ProviderLookupRequest request, CancellationToken ct) =>
        (await RunAsync(_config.Flows!.Fetch!, _fetch!, request, 0, ct).ConfigureAwait(false)).Claims;

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(ProviderLookupRequest request, int limit, CancellationToken ct) =>
        (await RunAsync(_config.Flows!.Search!, _search!, request, limit, ct).ConfigureAwait(false)).Items;

    private async Task<FlowResult> RunAsync(
        ProviderFlowConfiguration flow,
        FlowProgram program,
        ProviderLookupRequest request,
        int limit,
        CancellationToken ct)
    {
        if (!flow.AppliesTo(request.MediaType, request.EntityType)
            || (flow.RequiresCredentials && !_api.IsConfigured(_config.Name)))
        {
            return FlowResult.Empty;
        }

        try
        {
            var runner = new ProviderFlowRunner(_api, _config.Name, request, name => RequestField(request, name), limit, _logger);
            return await runner.RunAsync(program.Steps, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Provider}: lookup failed for {MediaType}", _config.Name, request.MediaType);
            return FlowResult.Empty;
        }
    }

    private static string? RequestField(ProviderLookupRequest request, string name) => name switch
    {
        "show_name" => request.ShowName,
        "series" => request.Series,
        "title" => request.Title,
        "season_number" => request.SeasonNumber,
        "episode_number" => request.EpisodeNumber,
        "year" => request.Year,
        "language" => request.Language,
        _ => null,
    };
}
