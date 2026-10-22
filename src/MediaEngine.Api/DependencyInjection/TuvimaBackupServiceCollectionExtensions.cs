using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Storage.Backup;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaBackupServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaBackupDestinations(this IServiceCollection services)
    {
        // What a backup keeps of the data store, and the codec that makes the two-step secret readable on any computer.
        services.AddSingleton<IBackupPortableValueCodec, DataProtectionBackupPortableValueCodec>();
        services.AddSingleton<PersonalStateBackupRepository>();

        // Restore: settings go back at once, and a person's own state waits for its title and is put back as the title arrives.
        services.AddSingleton<BackupRestoreRepository>();
        services.AddSingleton<WaitingPersonalStateRepository>();
        services.AddHostedService<WaitingStateReattachHostedService>();
        services.AddSingleton<BackupSettingsAccessor>();
        services.AddSingleton<IBackupDestination, LocalFolderBackupDestination>();
        services.AddSingleton<IBackupHttp, BackupHttp>();
        services.AddSingleton<ConfiguredBackupDestinations>();
        services.AddSingleton<BackupOrchestrator>();
        services.AddSingleton<BackupSettingsManager>();
        services.AddSingleton<BackupScheduleWorker>();
        services.AddSingleton<IScheduledTaskDescriptorSource>(sp => sp.GetRequiredService<BackupScheduleWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<BackupScheduleWorker>());
        return services;
    }
}
