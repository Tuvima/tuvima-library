using MediaEngine.Api.Services.Collections.ListImport;
using MediaEngine.Api.Services.Collections.ListImport.Sources;
using MediaEngine.Api.Services.Import;
using MediaEngine.Api.Services.Plugins.Trakt;
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

        // The web list sources (Collections and lists spec D7, 4d): each recognises its own addresses and reads a list through its
        // site's documented API, and each is also the refresh job's fetcher for lists that came from it. The sources are metadata
        // sources on the Plugins page (their provider files), so each one's switch and key are read from there on every use. A
        // source does not use the shared retry pipeline: its own failure rules (a site that is down means "will try again") and
        // its provider's rate limit are the whole policy, and a hidden retry would send more requests than that limit counts.
        AddWebListSource<TmdbListSource>(services, "tmdb");
        AddWebListSource<MdbListSource>(services, "mdblist");
        AddWebListSource<OpenLibraryListSource>(services, "openlibrary");
        AddWebListSource<MusicBrainzCollectionSource>(services, "musicbrainz");
        // Trakt is a plugin: its switch and client ID come from the plugin, and a private or own list is read with the token of the
        // profile that signed in to Trakt (Plugin architecture spec C13).
        services.AddSingleton<ITraktSettings, PluginTraktSettings>();
        services.AddSingleton<ITraktAccessTokens, PluginTraktAccessTokens>();
        AddWebListSource<TraktListSource>(services, "trakt");
        services.AddSingleton<WebListResolver>();
        services.AddSingleton<ListInputReader>();

        // A scheduled refresh asks every registered fetcher; a list no fetcher serves is matched against the library again.
        services.AddHostedService<CollectionListRefreshWorker>();
        return services;
    }

    private static void AddWebListSource<TSource>(IServiceCollection services, string providerName)
        where TSource : class, IWebListSource
    {
        services.AddTuvimaHttpClient($"list_source_{providerName}", TimeSpan.FromSeconds(20), addStandardResilience: false);
        services.AddSingleton<TSource>();
        services.AddSingleton<IWebListSource>(sp => sp.GetRequiredService<TSource>());
        services.AddSingleton<IListSourceFetcher>(sp => sp.GetRequiredService<TSource>());
    }
}
