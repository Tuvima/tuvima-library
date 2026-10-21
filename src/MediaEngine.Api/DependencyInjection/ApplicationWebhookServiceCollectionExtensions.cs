using MediaEngine.Api.Services.Webhooks;
using MediaEngine.Storage;

namespace MediaEngine.Api.DependencyInjection;

public static class ApplicationWebhookServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationWebhooks(this IServiceCollection services)
    {
        services.AddSingleton<ApplicationWebhookRepository>();
        services.AddSingleton<IWebhookDnsResolver, WebhookDnsResolver>();
        services.AddSingleton<WebhookDestinationPolicy>();
        services.AddSingleton<IWebhookTransport, WebhookTransport>();
        services.AddScoped<ApplicationWebhookService>();
        services.AddScoped<ApplicationWebhookTestSender>();
        services.AddScoped<ApplicationWebhookDispatcher>();
        services.AddHostedService<ApplicationWebhookWorker>();
        return services;
    }
}
