using Microsoft.Extensions.Http.Resilience;

namespace MediaEngine.Api.DependencyInjection;

internal static class TuvimaHttpClientRegistration
{
    internal const string CanonicalUserAgent =
        "Tuvima Library/1.0 (https://github.com/Tuvima/tuvima-library)";

    internal static IHttpClientBuilder AddTuvimaHttpClient(
        this IServiceCollection services,
        string name,
        TimeSpan timeout,
        string? userAgent = null,
        bool addStandardResilience = true)
    {
        var registration = services.AddHttpClient(name, client =>
        {
            client.Timeout = timeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                string.IsNullOrWhiteSpace(userAgent) ? CanonicalUserAgent : userAgent);
        });

        if (addStandardResilience)
        {
            registration.AddStandardResilienceHandler();
        }

        return registration;
    }
}
