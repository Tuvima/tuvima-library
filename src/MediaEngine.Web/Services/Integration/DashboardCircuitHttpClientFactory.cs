using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Adds the active Blazor circuit's session identity outside the pooled
/// IHttpClientFactory handler scope. The named client's existing handler chain
/// still owns service credentials and request validation.
/// </summary>
public sealed class DashboardCircuitHttpClientFactory(
    IHttpMessageHandlerFactory handlers,
    IOptionsMonitor<HttpClientFactoryOptions> options,
    DashboardSessionAccessor session) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var client = new HttpClient(new CircuitSessionForwardingHandler(session)
        {
            InnerHandler = handlers.CreateHandler(name),
        }, disposeHandler: true);
        foreach (var configure in options.Get(name).HttpClientActions)
        {
            configure(client);
        }
        return client;
    }

    private sealed class CircuitSessionForwardingHandler(DashboardSessionAccessor session) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!request.Options.TryGetValue(DashboardEngineAuthenticationHandler.SuppressSessionToken, out var suppressed)
                || !suppressed)
            {
                var hasExplicitToken = request.Headers.TryGetValues(
                    DashboardEngineAuthenticationHandler.SessionHeader, out var explicitValues)
                    && explicitValues.Any(value => !string.IsNullOrWhiteSpace(value));
                var forwarding = session.CurrentForwardingState();
                if (!hasExplicitToken && !string.IsNullOrWhiteSpace(forwarding.SessionToken))
                {
                    request.Headers.Remove(DashboardEngineAuthenticationHandler.SessionHeader);
                    request.Headers.TryAddWithoutValidation(
                        DashboardEngineAuthenticationHandler.SessionHeader, forwarding.SessionToken);
                }
                else if (!hasExplicitToken && forwarding.HasEstablishedSessionState)
                {
                    request.Options.Set(DashboardEngineAuthenticationHandler.SuppressSessionToken, true);
                }
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
