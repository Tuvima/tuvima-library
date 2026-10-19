using MediaEngine.AI.Configuration;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.DependencyInjection;

/// <summary>Durable run records for recurring tasks and the registry behind the Scheduled tasks list.</summary>
public static class TuvimaScheduledTaskServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaScheduledTasks(this IServiceCollection services)
    {
        services.AddSingleton<ScheduledTaskRunner>();
        services.AddSingleton<IRecurringTaskRecorder>(sp => sp.GetRequiredService<ScheduledTaskRunner>());
        services.AddSingleton<IScheduledTaskRegistry>(sp =>
        {
            var registry = new ScheduledTaskRegistry();
            ScheduledTaskCatalog.RegisterAll(
                registry,
                sp.GetRequiredService<IConfigurationLoader>(),
                sp.GetRequiredService<AiSettings>(),
                sp.GetRequiredService<ApplicationWebhookRepository>());
            return registry;
        });
        return services;
    }
}
