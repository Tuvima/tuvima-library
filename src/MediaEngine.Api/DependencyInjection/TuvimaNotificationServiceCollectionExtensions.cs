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
        services.AddSingleton<MailServerSettingsService>();
        services.AddSingleton<INotificationChannel, EmailNotificationChannel>();
        services.AddSingleton<INotificationChannelSource, PluginNotificationChannelSource>();
        services.AddSingleton<ChannelTypeCatalogue>();
        services.AddSingleton<NotificationChannelStore>();
        services.AddSingleton<IAsyncNotificationChannelSource, AddedChannelSource>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<NotificationChannelService>();
        services.AddHttpClient(TemplatedNotificationChannel.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHostedService<NotificationDispatchWorker>();
        return services;
    }
}
