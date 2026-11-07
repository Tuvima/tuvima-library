using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.DependencyInjection;

/// <summary>
/// Moving items into another album, season, series, audiobook or hand-picked collection through the Match step. A new media type
/// joins by registering an <see cref="IMoveListProvider"/> for its destinations (the list it can be matched against), or by declaring
/// no list, which makes moves into it free (Details overlay D12 and D14). A Series takes only Add anyway (D20). It needs no screen of its own.
/// </summary>
public static class TuvimaLibraryMoveServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaLibraryMoves(this IServiceCollection services)
    {
        services.AddSingleton<ILibraryMoveRepository, LibraryMoveRepository>();
        services.AddSingleton<IMoveListProvider, AlbumTrackMoveListProvider>();
        services.AddSingleton<IMoveListProvider, SeasonEpisodeMoveListProvider>();
        services.AddSingleton<IMoveListProvider, SeriesAddAnywayOnlyMoveListProvider>();
        services.AddSingleton<IMoveListProvider, AudiobookPartMoveListProvider>();
        services.AddSingleton<MoveListRegistry>();
        services.AddSingleton<MoveFilePlanner>();
        services.AddSingleton<ILibraryMoveService, LibraryMoveService>();

        // The Match tool's parent change and physical-files change save through the same move service and organiser.
        services.AddSingleton<IEditSessionChangeHandler, RelationshipEditSessionChangeHandler>();
        services.AddSingleton<IEditSessionChangeHandler, ChildRematchEditSessionChangeHandler>();
        services.AddSingleton<IEditSessionChangeHandler, FileMoveEditSessionChangeHandler>();

        // Where applying a match would put an item and its file: a read over the same places and previews (universal editor v5, D8).
        services.AddScoped<MediaEngine.Api.Services.Matching.EditorMatchPlacementService>();
        services.AddScoped<MediaEngine.Api.Services.Matching.IEditorMatchPlacement>(sp => sp.GetRequiredService<MediaEngine.Api.Services.Matching.EditorMatchPlacementService>());
        services.AddScoped<MediaEngine.Api.Services.Matching.EditorGroupMatchService>();
        return services;
    }
}
