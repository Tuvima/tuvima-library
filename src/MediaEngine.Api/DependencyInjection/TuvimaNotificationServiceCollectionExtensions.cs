using MediaEngine.Api.Services.Notifications;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaNotificationServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaNotifications(this IServiceCollection services)
    {
        services.AddSingleton<NotificationQueue>();
        services.AddSingleton<INotificationPublisher>(provider => provider.GetRequiredService<NotificationQueue>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SignInWatcher>();
        services.AddSingleton<NotificationSettingsAccessor>();
        services.AddSingleton<NotificationSummaryStore>();
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
        services.AddHostedService<NotificationSummaryWorker>();
        return services;
    }
}
