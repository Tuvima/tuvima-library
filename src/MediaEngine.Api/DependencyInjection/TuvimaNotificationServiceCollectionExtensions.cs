using MediaEngine.Api.Services.Notifications;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaNotificationServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaNotifications(this IServiceCollection services)
    {
        services.AddSingleton<NotificationQueue>();
        services.AddSingleton<INotificationPublisher>(provider => provider.GetRequiredService<NotificationQueue>());
        services.AddSingleton<NotificationSettingsAccessor>();
        services.AddSingleton<INotificationChannel, EmailNotificationChannel>();
        services.AddSingleton<INotificationChannelSource, PluginNotificationChannelSource>();
        services.AddSingleton<NotificationService>();
        services.AddHostedService<NotificationDispatchWorker>();
        return services;
    }
}
