namespace MediaEngine.Api.Services.Playback;

/// <summary>Who a session belongs to, in words: the person (the account's own profile) and the profile being used.</summary>
public interface IPlayingNowNames
{
    Task<(string? Name, bool Kids)> ProfileAsync(Guid profileId, CancellationToken ct);

    Task<string?> PersonAsync(Guid accountId, CancellationToken ct);
}
