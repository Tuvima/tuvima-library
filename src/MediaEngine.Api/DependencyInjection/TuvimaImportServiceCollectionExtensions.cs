using MediaEngine.Api.Services.Collections.ListImport;
using MediaEngine.Api.Services.Import;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MediaEngine.Api.DependencyInjection;

/// <summary>
/// The shared Import (Collections and lists spec D17) and imported lists: the recogniser with one handler per kind of file,
/// the list matcher and refresh service, and the refresh job.
/// </summary>
public static class TuvimaImportServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaImport(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ListImportService>();

        // One handler per kind of file. The recogniser asks them in Priority order and a later registration of the same kind
        // replaces an earlier one, so a packet that owns a kind registers its handler after the placeholder here:
        //   - the pack file kind (PackFileImportKindHandler) reads through the packs packet's IPackFileImporter;
        //   - a Goodreads library import that also sets reading status would add its own kind.
        services.AddSingleton<IImportKindHandler, BookmarkPackImportKindHandler>();
        services.AddSingleton<IImportKindHandler, PackFileImportKindHandler>();
        services.AddSingleton<IImportKindHandler, PluginSettingsImportKindHandler>();
        services.AddSingleton<IImportKindHandler, CollectionListImportKindHandler>();
        services.AddSingleton<ImportRecogniser>();

        // The web list sources (a later packet) add an IListSourceFetcher each; with none, a scheduled refresh matches the
        // stored entries against the library again.
        services.AddHostedService<CollectionListRefreshWorker>();
        return services;
    }
}
