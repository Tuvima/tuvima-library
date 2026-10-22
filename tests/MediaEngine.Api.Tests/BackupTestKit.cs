using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>Builds the real backup and restore pieces over a real temporary data store, with the real Data Protection codec.</summary>
internal static class BackupTestKit
{
    public static DataProtectionBackupPortableValueCodec Codec(IDataProtectionProvider? provider = null)
    {
        provider ??= new EphemeralDataProtectionProvider();
        return new DataProtectionBackupPortableValueCodec(new DataProtectionTwoStepSecretProtector(provider), provider);
    }

    public static DatabaseConnection OpenStore(string path)
    {
        DapperConfiguration.Configure();
        var database = new DatabaseConnection(path);
        database.InitializeSchema();
        database.RunStartupChecks();
        return database;
    }

    public static DatabaseBackupService Service(
        DatabaseConnection database, string config, string? backup = null, IBackupPortableValueCodec? codec = null) =>
        new(new PersonalStateBackupRepository(database, codec ?? Codec()), config, backup);

    public static PendingRestoreImportService Importer(
        DatabaseConnection database, string config, IBackupPortableValueCodec? codec = null, ILogger<PendingRestoreImportService>? logger = null) =>
        new(new BackupRestoreRepository(database), codec ?? Codec(), config, TimeProvider.System, logger ?? NullLogger<PendingRestoreImportService>.Instance);

    /// <summary>Keeps what is logged, so a test can say what a log line named and what it left out.</summary>
    public sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }
}
