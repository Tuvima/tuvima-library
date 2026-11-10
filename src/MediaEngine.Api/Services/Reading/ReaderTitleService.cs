using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Reading;

/// <summary>The words the page reader shows in its top line: the title and one line under it.</summary>
public sealed record ReaderTitle(string? Title, string? Subtitle);

public interface IReaderTitleService
{
    Task<ReaderTitle> GetAsync(Guid assetId, bool isComic, CancellationToken ct);
}

/// <summary>
/// Reads the title, author and Series of a title from the catalogue for the reader's top line (PDF and comic reader spec section 4).
/// Presentation only: nothing here is used to decide access.
/// </summary>
public sealed class ReaderTitleService(IDatabaseConnection database) : IReaderTitleService
{
    public async Task<ReaderTitle> GetAsync(Guid assetId, bool isComic, CancellationToken ct)
    {
        using var connection = database.CreateConnection();
        var rows = await connection.QueryAsync<(string Key, string Value)>(
            new CommandDefinition(
                """
                SELECT cv.key AS Key, cv.value AS Value
                FROM media_assets a
                JOIN editions e ON e.id = a.edition_id
                JOIN canonical_values cv ON cv.entity_id IN (a.id, e.work_id)
                WHERE a.id = @assetId
                  AND cv.key IN ('title', 'author', 'series', 'series_position')
                ORDER BY CASE WHEN cv.entity_id = a.id THEN 0 ELSE 1 END
                """,
                new { assetId },
                cancellationToken: ct));

        string? Pick(string key) => rows.FirstOrDefault(r => r.Key == key && !string.IsNullOrWhiteSpace(r.Value)).Value?.Trim();

        var title = Pick(MetadataFieldConstants.Title);
        string? subtitle;
        if (isComic)
        {
            var series = Pick(MetadataFieldConstants.Series);
            var position = Pick(MetadataFieldConstants.SeriesPosition);
            subtitle = series is null ? Pick(MetadataFieldConstants.Author) : position is null ? series : $"{series} #{position}";
        }
        else
        {
            subtitle = Pick(MetadataFieldConstants.Author);
        }

        return new ReaderTitle(title, subtitle);
    }
}
