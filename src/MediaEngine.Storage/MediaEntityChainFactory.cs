using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Storage;

/// <summary>
/// Creates the Work-to-Edition chain required before a MediaAsset can be inserted.
/// Work resolution is delegated to <see cref="HierarchyResolver"/>.
/// </summary>
public sealed class MediaEntityChainFactory : IMediaEntityChainFactory
{
    private readonly IDatabaseConnection _db;
    private readonly HierarchyResolver _resolver;
    private readonly ILogger<MediaEntityChainFactory>? _logger;

    public MediaEntityChainFactory(
        IDatabaseConnection db,
        HierarchyResolver resolver,
        ILogger<MediaEntityChainFactory>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(resolver);
        _db = db;
        _resolver = resolver;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<Guid> EnsureEntityChainAsync(
        MediaType mediaType,
        IReadOnlyDictionary<string, string>? metadata,
        string? sourceFilePath = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var resolved = await _resolver.ResolveAsync(mediaType, metadata, sourceFilePath, ct).ConfigureAwait(false);

        _logger?.LogDebug(
            "Chain factory: resolved {MediaType} to Work {WorkId} ({Kind}, parent={Parent}, ordinal={Ordinal}, new={New})",
            mediaType, resolved.WorkId, resolved.WorkKind, resolved.ParentWorkId, resolved.Ordinal, resolved.NewlyCreated);

        string? formatLabel = null;
        metadata?.TryGetValue("format", out formatLabel);

        var editionId = Guid.NewGuid();

        using var conn = _db.CreateConnection();
        using var insertEdition = conn.CreateCommand();
        insertEdition.CommandText = """
            INSERT INTO editions (id, work_id, format_label)
            VALUES (@id, @work_id, @format_label);
            """;
        insertEdition.Parameters.AddWithValue("@id", GuidSql.ToBlob(editionId));
        insertEdition.Parameters.AddWithValue("@work_id", GuidSql.ToBlob(resolved.WorkId));
        insertEdition.Parameters.AddWithValue("@format_label", formatLabel ?? (object)DBNull.Value);
        insertEdition.ExecuteNonQuery();

        return editionId;
    }
}
