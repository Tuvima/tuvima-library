using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.DependencyInjection;

/// <summary>
/// Moving items into another album, season, series, audiobook or hand-picked collection through the Match step. A new media type
/// joins by registering an <see cref="IMoveListProvider"/> for its destinations (the list it can be matched against), or by declaring
/// no list, which makes moves into it free (Details overlay D12 and D14). It needs no screen of its own.
/// </summary>
public static class TuvimaLibraryMoveServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaLibraryMoves(this IServiceCollection services)
    {
        services.AddSingleton<ILibraryMoveRepository, LibraryMoveRepository>();
        services.AddSingleton<IMoveListProvider, AlbumTrackMoveListProvider>();
        services.AddSingleton<IMoveListProvider, SeasonEpisodeMoveListProvider>();
        services.AddSingleton<IMoveListProvider, SeriesPositionMoveListProvider>();
        services.AddSingleton<IMoveListProvider, AudiobookPartMoveListProvider>();
        services.AddSingleton<MoveListRegistry>();
        services.AddSingleton<ILibraryMoveService, LibraryMoveService>();
        return services;
    }
}
