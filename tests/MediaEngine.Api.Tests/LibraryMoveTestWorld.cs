using Dapper;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A real temp data store wired to the real move repository, list providers and service (with a real series manifest repository),
/// plus seeders for albums, seasons, series, audiobooks and hand-picked collections. Files are only recorded, never written: a move
/// changes the library, not the disk.
/// </summary>
internal sealed class LibraryMoveTestWorld : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_moves_{Guid.NewGuid():N}.db");

    /// <param name="withFiles">
    /// True to wire the real organiser over real temp folders (a Managed, an As-is and a read-only folder) so a move also renames and
    /// moves files. Without it, files are only recorded and the move changes the library, not the disk.
    /// </param>
    public LibraryMoveTestWorld(bool withFiles = false)
    {
        DapperConfiguration.Configure();
        Database = new DatabaseConnection(_databasePath);
        Database.InitializeSchema();
        Database.RunStartupChecks();
        Repository = new LibraryMoveRepository(Database);
        var manifests = new SeriesManifestRepository(Database);
        Cache = new MemoryCache(new MemoryCacheOptions());
        Root = Path.Combine(Path.GetTempPath(), $"tuvima_moves_{Guid.NewGuid():N}");
        DataRoot = Path.Combine(Root, "data");
        AssetPaths = new AssetPathService(Path.Combine(Root, "library"), dataRoot: DataRoot);
        Files = withFiles ? new MoveFileWorld(Database, Path.Combine(Root, "files")) : null;
        Service = new LibraryMoveService(
            Repository,
            new MoveListRegistry(
            [
                new AlbumTrackMoveListProvider(Repository),
                new SeasonEpisodeMoveListProvider(Repository, manifests),
                new SeriesAddAnywayOnlyMoveListProvider(),
                new AudiobookPartMoveListProvider(Repository),
            ]),
            new MoveOwnedFileCleaner(AssetPaths, NullLogger<MoveOwnedFileCleaner>.Instance),
            new MoveFilePlanner((IAutoOrganizeService?)Files?.Organizer ?? new MoveFileWorld.NoFilesOrganizer()),
            Cache);
    }

    /// <summary>The real organiser, folders and file queue. Null in a world that only records files.</summary>
    public MoveFileWorld? Files { get; }

    public string Root { get; }
    public string DataRoot { get; }
    public AssetPathService AssetPaths { get; }
    public DatabaseConnection Database { get; }
    public LibraryMoveRepository Repository { get; }
    public MemoryCache Cache { get; }
    public LibraryMoveService Service { get; }

    /// <summary>The person confirming moves in these tests.</summary>
    public static LibraryMoveActor Actor { get; } = new(
        new RequestAuthority(PrincipalKind.Human, true, AccountId: Guid.NewGuid(), ActiveProfileId: Guid.NewGuid(), SessionId: Guid.NewGuid(),
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ActiveProfileIsServerAdministrator: true),
        null,
        "Shy");

    public static LibraryMoveAccess Everything { get; } = (_, _, _) => ValueTask.FromResult(true);

    public static LibraryMoveAccess Nothing { get; } = (_, _, _) => ValueTask.FromResult(false);

    /// <summary>An application that may write metadata but not match: the right to write only.</summary>
    public static LibraryMoveAccess WriteOnly { get; } = (_, right, _) => ValueTask.FromResult(right == LibraryMoveRight.Write);

    // ----- seeding ---------------------------------------------------------------------------------------------------------

    public Guid AddParent(string mediaType, string title, Guid? grandparent = null, int? ordinal = null, string? manifestJson = null, Guid? collection = null)
    {
        var id = Guid.NewGuid();
        using var connection = Database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, collection_id)
            VALUES (@id, @mediaType, 'parent', @grandparent, @ordinal, @collection);
            """,
            new { id, mediaType, grandparent, ordinal, collection });
        if (title.Length > 0)
        {
            var key = mediaType switch { "Music" => "album", "TV" => "show_name", _ => "series" };
            Put(id, key, title);
            Put(id, "title", title);
        }

        if (manifestJson is not null)
        {
            Put(id, "child_entities_json", manifestJson);
        }

        return id;
    }

    public (Guid WorkId, Guid EditionId, Guid? AssetId) AddChild(
        Guid parent, string mediaType, string title, int? ordinal, bool hasFile = true,
        double? ordinalSort = -1, string? duration = null, bool catalogOnly = false, Guid? collection = null, string? filePath = null)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        Guid? asset = hasFile ? Guid.NewGuid() : null;
        var sort = ordinalSort is -1 ? ordinal : ordinalSort;
        using var connection = Database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ordinal_sort, is_catalog_only, ownership, collection_id)
            VALUES (@work, @mediaType, @kind, @parent, @ordinal, @sort, @catalog, @ownership, @collection);
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            """,
            new
            {
                work,
                edition,
                mediaType,
                parent,
                ordinal,
                sort,
                kind = catalogOnly ? "catalog" : "child",
                catalog = catalogOnly ? 1 : 0,
                ownership = catalogOnly ? "Unowned" : "Owned",
                collection,
            });
        Put(work, "title", title);
        if (duration is not null)
        {
            Put(work, "duration_seconds", duration);
        }

        if (asset is { } assetId)
        {
            connection.Execute(
                """
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
                VALUES (@assetId, @edition, @hash, @path, CURRENT_TIMESTAMP);
                """,
                new { assetId, edition, hash = Guid.NewGuid().ToString("N"), path = filePath ?? $"C:/library/{assetId:N}.mp3" });
        }

        return (work, edition, asset);
    }

    /// <summary>An audiobook recording Work with one part per (number, title); each part is a file under its own Edition.</summary>
    public (Guid WorkId, List<(Guid EditionId, Guid AssetId)> Parts) AddAudiobook(string title, IReadOnlyList<(int Number, string Title)> parts, int? partCount = null)
    {
        var work = Guid.NewGuid();
        using (var connection = Database.CreateConnection())
        {
            connection.Execute("INSERT INTO works (id, media_type, work_kind) VALUES (@work, 'Audiobooks', 'standalone');", new { work });
        }

        Put(work, "title", title);
        var made = new List<(Guid, Guid)>();
        foreach (var (number, partTitle) in parts)
        {
            made.Add(AddPart(work, number, partTitle, partCount));
        }

        return (work, made);
    }

    public (Guid EditionId, Guid AssetId) AddPart(Guid work, int? number, string partTitle, int? partCount = null, string? filePath = null)
    {
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using (var connection = Database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO editions (id, work_id) VALUES (@edition, @work);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
                VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP);
                """,
                new { edition, work, asset, hash = Guid.NewGuid().ToString("N"), path = filePath ?? $"C:/library/{asset:N}.mp3" });
        }

        Put(asset, "track_title", partTitle);
        if (number is { } n)
        {
            Put(asset, "audiobook_part_number", n.ToString());
        }

        if (partCount is { } count)
        {
            Put(asset, "audiobook_part_count", count.ToString());
        }

        return (edition, asset);
    }

    public Guid AddCollection(string name, params Guid[] works)
    {
        var id = Guid.NewGuid();
        using var connection = Database.CreateConnection();
        connection.Execute(
            "INSERT INTO collections (id, display_name, collection_type, membership_mode, resolution) VALUES (@id, @name, 'Custom', 'Manual', 'materialized');",
            new { id, name });
        var order = 0;
        foreach (var work in works)
        {
            connection.Execute(
                "INSERT INTO collection_items (id, collection_id, work_id, sort_order) VALUES (@item, @id, @work, @order);",
                new { item = Guid.NewGuid(), id, work, order = order++ });
        }

        return id;
    }

    /// <summary>A Series collection with a provider manifest; the items are (id, series id, number, label, linked work).</summary>
    public Guid AddSeriesCollection(string seriesQid, IReadOnlyList<(string ItemQid, string SeriesQid, double? Number, string Label, Guid? LinkedWork)> items)
    {
        var id = Guid.NewGuid();
        using var connection = Database.CreateConnection();
        connection.Execute(
            "INSERT INTO collections (id, display_name, collection_type) VALUES (@id, @name, 'Series');",
            new { id, name = seriesQid });
        connection.Execute(
            "INSERT INTO series_manifest_hydrations (series_qid, collection_id, series_label, last_hydrated_at) VALUES (@seriesQid, @id, @seriesQid, @now);",
            new { id, seriesQid, now = DateTimeOffset.UtcNow.ToString("O") });
        foreach (var item in items)
        {
            connection.Execute(
                """
                INSERT INTO series_manifest_items
                    (id, collection_id, series_qid, item_qid, item_label, parsed_ordinal, raw_ordinal, sort_order, order_source,
                     ownership_state, linked_work_id, last_hydrated_at)
                VALUES (@itemId, @id, @series, @qid, @label, @number, @raw, @number, 'test', @state, @linked, @now);
                """,
                new
                {
                    itemId = Guid.NewGuid(),
                    id,
                    series = item.SeriesQid,
                    qid = item.ItemQid,
                    label = item.Label,
                    number = item.Number,
                    raw = item.Number?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                    state = item.LinkedWork is null ? "Missing" : "Owned",
                    linked = item.LinkedWork,
                    now = DateTimeOffset.UtcNow.ToString("O"),
                });
        }

        return id;
    }

    public void Put(Guid entity, string key, string value)
    {
        using var connection = Database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@entity, @key, @value, @now)
            ON CONFLICT(entity_id, key) DO UPDATE SET value = excluded.value;
            """,
            new { entity, key, value, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    public void MarkProcessing(Guid asset)
    {
        using var connection = Database.CreateConnection();
        connection.Execute(
            "INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, state, pass) VALUES (@id, @asset, 'MediaAsset', 'Music', 'Hydrating', 'Quick');",
            new { id = Guid.NewGuid(), asset });
    }

    // ----- reading ---------------------------------------------------------------------------------------------------------

    public void Execute(string sql, object? args = null)
    {
        using var connection = Database.CreateConnection();
        connection.Execute(sql, args);
    }

    public T Scalar<T>(string sql, object? args = null)
    {
        using var connection = Database.CreateConnection();
        return connection.ExecuteScalar<T>(sql, args)!;
    }

    public WorkState Work(Guid id)
    {
        using var connection = Database.CreateConnection();
        return connection.QuerySingle<WorkState>(
            "SELECT parent_work_id AS Parent, ordinal AS Ordinal, ordinal_sort AS OrdinalSort, placement_unmatched AS Unmatched, collection_id AS Collection FROM works WHERE id = @id;",
            new { id });
    }

    public bool WorkExists(Guid id) => Scalar<long>("SELECT COUNT(*) FROM works WHERE id = @id;", new { id }) > 0;

    public Guid WorkOfAsset(Guid asset) =>
        Scalar<Guid>("SELECT e.work_id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE ma.id = @asset;", new { asset });

    public int EditionCount(Guid work) => (int)Scalar<long>("SELECT COUNT(*) FROM editions WHERE work_id = @work;", new { work });

    public string? Canonical(Guid entity, string key)
    {
        using var connection = Database.CreateConnection();
        return connection.QueryFirstOrDefault<string>("SELECT value FROM canonical_values WHERE entity_id = @entity AND key = @key;", new { entity, key });
    }

    public sealed class WorkState
    {
        public Guid? Parent { get; init; }
        public int? Ordinal { get; init; }
        public double? OrdinalSort { get; init; }
        public long Unmatched { get; init; }
        public Guid? Collection { get; init; }
    }

    public void Dispose()
    {
        Files?.Dispose();
        Cache.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { Database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }
}
