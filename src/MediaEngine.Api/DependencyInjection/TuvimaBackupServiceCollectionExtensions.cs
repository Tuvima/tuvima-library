using MediaEngine.Api.Services.Backups;
using MediaEngine.Api.Services.ScheduledTasks;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaBackupServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaBackupDestinations(this IServiceCollection services)
    {
        services.AddSingleton<BackupSettingsAccessor>();
        services.AddSingleton<IBackupDestination, LocalFolderBackupDestination>();
        services.AddSingleton<BackupOrchestrator>();
        services.AddSingleton<BackupSettingsManager>();
        services.AddSingleton<BackupScheduleWorker>();
        services.AddSingleton<IScheduledTaskDescriptorSource>(sp => sp.GetRequiredService<BackupScheduleWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<BackupScheduleWorker>());
        return services;
    }
}
