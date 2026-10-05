using MediaEngine.Domain.Enums;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

public enum MediaReaction
{
    Neutral,
    Like,
    Dislike,
    Love,
}

public sealed class MediaReactionService(IEngineApiClient apiClient)
{
    public event Action? Changed;
    public static MediaReaction ToggleRating(MediaReaction current, MediaReaction choice) =>
        choice == MediaReaction.Like && current == MediaReaction.Love ? MediaReaction.Love
        : current == choice ? MediaReaction.Neutral : choice;

    public async Task<MediaReaction> GetReactionAsync(
        Guid entityId,
        Guid? profileId,
        ProfileEntityKind entityKind = ProfileEntityKind.Movie,
        CancellationToken ct = default)
    {
        if (!profileId.HasValue)
            return MediaReaction.Neutral;

        var state = await apiClient.GetProfileReactionAsync(entityKind, entityId, ct);
        return state?.Reaction switch
        {
            ProfileReactionKind.Like => MediaReaction.Like,
            ProfileReactionKind.Dislike => MediaReaction.Dislike,
            ProfileReactionKind.Love => MediaReaction.Love,
            _ => MediaReaction.Neutral,
        };
    }

    public async Task<IReadOnlyCollection<Guid>> GetFavoriteWorkIdsAsync(
        Guid? profileId,
        CancellationToken ct = default)
    {
        if (!profileId.HasValue)
            return [];
        var states = await apiClient.GetProfileReactionsAsync(ct);
        return states
            .Where(item => item.EntityKind == ProfileEntityKind.Song
                           && item.Reaction == ProfileReactionKind.Love)
            .Select(item => item.EntityId)
            .ToHashSet();
    }

    public async Task<IReadOnlyCollection<Guid>> GetDislikedWorkIdsAsync(
        Guid? profileId,
        CancellationToken ct = default)
    {
        if (!profileId.HasValue)
            return [];
        var states = await apiClient.GetProfileReactionsAsync(ct);
        return states
            .Where(item => item.EntityKind == ProfileEntityKind.Song
                           && item.Reaction == ProfileReactionKind.Dislike)
            .Select(item => item.EntityId)
            .ToHashSet();
    }

    public async Task SetReactionAsync(
        Guid entityId,
        MediaReaction reaction,
        Guid? profileId,
        ProfileEntityKind entityKind = ProfileEntityKind.Movie,
        CancellationToken ct = default)
    {
        if (!profileId.HasValue)
            return;

        if (reaction == MediaReaction.Neutral)
        {
            await apiClient.RemoveProfileReactionAsync(entityKind, entityId, ct);
            Changed?.Invoke();
            return;
        }

        await apiClient.SetProfileReactionAsync(
            entityKind,
            entityId,
            reaction switch
            {
                MediaReaction.Like => ProfileReactionKind.Like,
                MediaReaction.Dislike => ProfileReactionKind.Dislike,
                MediaReaction.Love => ProfileReactionKind.Love,
                _ => throw new ArgumentOutOfRangeException(nameof(reaction)),
            },
            ct);
        Changed?.Invoke();
    }

    public Task RefreshAsync(Guid? profileId, CancellationToken ct = default) => Task.CompletedTask;
}
