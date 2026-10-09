using MediaEngine.Api.Realtime;
using MediaEngine.Api.Services.Events;
using MediaEngine.Domain.Events;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.DependencyInjection;

public static class ApplicationEventServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationEvents(this IServiceCollection services)
    {
        services.AddSingleton<ApplicationEventRegistry>();
        services.AddSingleton<IntercomAudienceRegistry>();
        services.AddSingleton<IIntercomAudienceAuthorizer, IntercomAudienceAuthorizer>();
        services.AddSingleton<IApplicationEventRepository, ApplicationEventRepository>();
        services.AddSingleton<IApplicationEventOutboxWriter, ApplicationEventOutboxWriter>();
        services.AddSingleton<IApplicationEventDeliveryAuthorizer, ApplicationEventDeliveryAuthorizer>();
        services.AddScoped<ApplicationEventSubscriptionAuthorizer>();
        services.AddSingleton<ApplicationEventDispatcher>();
        services.AddSingleton<MediaEngine.Api.Security.IDeviceRevocationNotifier>(
            provider => provider.GetRequiredService<ApplicationEventDispatcher>());
        services.AddSingleton<IApplicationEventProducer, ApplicationEventProducer>();
        services.AddSingleton<IApplicationEventResourceResolver, ApplicationEventResourceResolver>();
        services.AddSingleton<ApplicationEventProjectionPublisher>();
        services.AddHostedService<ApplicationEventDispatchWorker>();
        return services;
    }
}
