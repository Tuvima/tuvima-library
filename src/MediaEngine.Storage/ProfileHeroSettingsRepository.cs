using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class ProfileHeroSettingsRepository(IDatabaseConnection db) : IProfileHeroSettingsRepository
{
    public Task<ProfileHeroSettings> GetAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var row = connection.QuerySingleOrDefault<(int Enabled, int Slides, int Watch, int Read, int Listen, int Continued)?>(new CommandDefinition(
            """
            SELECT hero_enabled AS Enabled, slide_count AS Slides, allow_watch AS Watch, allow_read AS Read,
                   allow_listen AS Listen, continued_only AS Continued
              FROM profile_hero_settings
             WHERE profile_id = @profileId;
            """,
            new { profileId }, cancellationToken: ct));
        return Task.FromResult(row is { } r
            ? new ProfileHeroSettings(r.Enabled == 1, r.Slides, r.Watch == 1, r.Read == 1, r.Listen == 1, r.Continued == 1).Clamped()
            : ProfileHeroSettings.Default);
    }

    public Task SaveAsync(Guid profileId, ProfileHeroSettings settings, CancellationToken ct = default)
    {
        var clamped = settings.Clamped();
        return db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (connection.ExecuteScalar<long>(new CommandDefinition(
                    "SELECT COUNT(1) FROM profiles WHERE id = @profileId;",
                    new { profileId }, transaction, cancellationToken: token)) == 0)
            {
                throw new KeyNotFoundException($"Profile '{profileId:D}' was not found.");
            }

            connection.Execute(new CommandDefinition(
                """
                INSERT INTO profile_hero_settings
                    (profile_id, hero_enabled, slide_count, allow_watch, allow_read, allow_listen, continued_only, updated_at)
                VALUES (@profileId, @enabled, @slides, @watch, @read, @listen, @continued, @updatedAt)
                ON CONFLICT(profile_id) DO UPDATE SET
                    hero_enabled = excluded.hero_enabled,
                    slide_count = excluded.slide_count,
                    allow_watch = excluded.allow_watch,
                    allow_read = excluded.allow_read,
                    allow_listen = excluded.allow_listen,
                    continued_only = excluded.continued_only,
                    updated_at = excluded.updated_at;
                """,
                new
                {
                    profileId,
                    enabled = clamped.Enabled ? 1 : 0,
                    slides = clamped.SlideCount,
                    watch = clamped.AllowWatch ? 1 : 0,
                    read = clamped.AllowRead ? 1 : 0,
                    listen = clamped.AllowListen ? 1 : 0,
                    continued = clamped.ContinuedOnly ? 1 : 0,
                    updatedAt = DateTimeOffset.UtcNow.ToString("O"),
                },
                transaction, cancellationToken: token));
        }, ct);
    }
}
