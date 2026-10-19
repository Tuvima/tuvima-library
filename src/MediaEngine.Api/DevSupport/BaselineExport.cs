using System.Globalization;
using Dapper;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.DevSupport;

/// <summary>
/// Offline, read-only export of what a run identified: <c>--export-baseline &lt;config-dir&gt; &lt;out.json&gt;</c>.
/// Reads the run's data store through a read-only connection and never touches the sample folders.
/// </summary>
public static class BaselineExport
{
    // Credits are listed in full by the people section; their per-person provider ids would only add noise.
    private static readonly string[] CreditPrefixes =
        ["cast_member_", "director_", "producer_", "screenwriter_", "composer_", "narrator_", "author_", "artist_"];

    public static Task RunAsync(string configDirectory, string outputPath)
    {
        configDirectory = Path.GetFullPath(configDirectory);
        var runInfo = BaselineRunInfo.Load(configDirectory)
            ?? throw new FileNotFoundException("The run has no run-info.json; prepare the run first.", BaselinePrepare.RunInfoPathFor(configDirectory));
        var databasePath = Path.GetFullPath(TuvimaDataPathResolver.ResolveDatabasePath(configDirectory, null, null));
        var record = Build(databasePath, runInfo, configDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, BaselineRecordJson.Serialize(record));
        Console.WriteLine($"Baseline record: {record.Items.Count} items, {record.ViewItems.Count} View items -> {Path.GetFullPath(outputPath)}");
        return Task.CompletedTask;
    }

    public static BaselineRecord Build(string databasePath, BaselineRunInfo runInfo, string configDirectory)
    {
        DapperConfiguration.Configure();
        var runRoot = Path.GetDirectoryName(Path.GetFullPath(configDirectory))!;
        var normalizer = new BaselineNormalizer(
            runInfo.SampleRoot,
            (BaselinePrepare.DataDirectoryFor(configDirectory), "<data>"),
            (Path.GetFullPath(configDirectory), "<config>"),
            (runRoot, "<run>"));
        using var connection = BaselineWait.OpenReadOnly(databasePath);
        var data = ReadAll(connection);
        normalizer.AddLocalIds(data.LocalIds);
        return Assemble(data, runInfo, normalizer);
    }

    // -- reading -------------------------------------------------------------------------------------------

    private sealed class AssetRow
    {
        public Guid Id { get; set; }
        public Guid EditionId { get; set; }
        public Guid WorkId { get; set; }
        public string Path { get; set; } = string.Empty;
        public string? FormatLabel { get; set; }
        public string? EditionQid { get; set; }
    }

    private sealed class WorkRow
    {
        public Guid Id { get; set; }
        public Guid? CollectionId { get; set; }
        public Guid? ParentWorkId { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public double? OrdinalSort { get; set; }
        public string? Qid { get; set; }
        public string? Ownership { get; set; }
        public string? MatchLevel { get; set; }
        public string? ExternalIdentifiers { get; set; }
    }

    private sealed class ValueRow
    {
        public Guid EntityId { get; set; }
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public bool Conflicted { get; set; }
        public bool NeedsReview { get; set; }
        public Guid? Winner { get; set; }
    }

    private sealed class ArrayRow
    {
        public Guid EntityId { get; set; }
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string? Qid { get; set; }
    }

    private sealed class ClaimRow
    {
        public Guid EntityId { get; set; }
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private sealed class JobRow
    {
        public Guid EntityId { get; set; }
        public string State { get; set; } = string.Empty;
        public string? ResolvedQid { get; set; }
        public string? Outcome { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }

    private sealed class ReviewRow
    {
        public Guid EntityId { get; set; }
        public string EntityType { get; set; } = string.Empty;
        public string Trigger { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    private sealed class ArtworkRow
    {
        public Guid EntityId { get; set; }
        public string Role { get; set; } = string.Empty;
        public int Preferred { get; set; }
        public int SortOrder { get; set; }
        public string? Provider { get; set; }
        public string? SourceUrl { get; set; }
        public string? ContentHash { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
    }

    private sealed class PersonRow
    {
        public Guid AssetId { get; set; }
        public string Role { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Qid { get; set; }
    }

    private sealed class TrackRow
    {
        public Guid AssetId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string SourceFormat { get; set; } = string.Empty;
        public string NormalizedFormat { get; set; } = string.Empty;
    }

    private sealed class CollectionRow
    {
        public Guid Id { get; set; }
        public Guid? ParentId { get; set; }
        public string Type { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Qid { get; set; }
        public string? Status { get; set; }
    }

    private sealed class RelationshipRow
    {
        public Guid CollectionId { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Qid { get; set; } = string.Empty;
        public string? Label { get; set; }
    }

    private sealed class ViewRow
    {
        public Guid Id { get; set; }
        public string MediaKind { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? MimeType { get; set; }
        public string? CapturedAt { get; set; }
        public string? ContentHash { get; set; }
        public long ByteSize { get; set; }
        public string? FilePath { get; set; }
    }

    private sealed class ExportData
    {
        public List<AssetRow> Assets { get; set; } = [];
        public Dictionary<Guid, WorkRow> Works { get; set; } = [];
        public Dictionary<Guid, string> Providers { get; set; } = [];
        public ILookup<Guid, ValueRow> Values { get; set; } = null!;
        public ILookup<Guid, ArrayRow> Arrays { get; set; } = null!;
        public ILookup<Guid, ClaimRow> Claims { get; set; } = null!;
        public Dictionary<Guid, JobRow> Jobs { get; set; } = [];
        public List<ReviewRow> Reviews { get; set; } = [];
        public ILookup<Guid, ArtworkRow> Artwork { get; set; } = null!;
        public ILookup<Guid, PersonRow> People { get; set; } = null!;
        public ILookup<Guid, TrackRow> Tracks { get; set; } = null!;
        public Dictionary<Guid, CollectionRow> Collections { get; set; } = [];
        public ILookup<Guid, RelationshipRow> Relationships { get; set; } = null!;
        public List<BaselineSeriesMember> SeriesMembers { get; set; } = [];
        public Dictionary<string, int> ProviderClaims { get; set; } = new(StringComparer.Ordinal);
        public List<ViewRow> ViewRows { get; set; } = [];
        public Dictionary<Guid, IDictionary<string, string>> ViewDetails { get; set; } = [];
        public ILookup<Guid, string> ViewTags { get; set; } = null!;
        public ILookup<Guid, string> ViewAnnotations { get; set; } = null!;
        public List<Guid> LocalIds { get; set; } = [];
    }

    private static ExportData ReadAll(SqliteConnection connection)
    {
        var data = new ExportData
        {
            Assets = connection.Query<AssetRow>("""
                SELECT ma.id AS Id, ma.edition_id AS EditionId, e.work_id AS WorkId, ma.file_path_root AS Path,
                       e.format_label AS FormatLabel, e.wikidata_qid AS EditionQid
                FROM media_assets ma JOIN editions e ON e.id = ma.edition_id
                """).ToList(),
            Works = connection.Query<WorkRow>("""
                SELECT id AS Id, collection_id AS CollectionId, parent_work_id AS ParentWorkId, media_type AS MediaType,
                       work_kind AS Kind, ordinal_sort AS OrdinalSort, wikidata_qid AS Qid, ownership AS Ownership,
                       match_level AS MatchLevel, external_identifiers AS ExternalIdentifiers
                FROM works
                """).ToDictionary(work => work.Id),
            Providers = connection.Query<(Guid Id, string Name)>("SELECT id AS Id, name AS Name FROM metadata_providers")
                .ToDictionary(provider => provider.Id, provider => provider.Name),
        };
        data.Values = connection.Query<ValueRow>("""
            SELECT entity_id AS EntityId, key AS Key, value AS Value, is_conflicted AS Conflicted,
                   needs_review AS NeedsReview, winning_provider_id AS Winner
            FROM canonical_values
            """).ToLookup(row => row.EntityId);
        data.Arrays = connection.Query<ArrayRow>("""
            SELECT entity_id AS EntityId, key AS Key, value AS Value, value_qid AS Qid
            FROM canonical_value_arrays
            """).ToLookup(row => row.EntityId);
        data.Claims = connection.Query<ClaimRow>("""
            SELECT entity_id AS EntityId, claim_key AS Key, claim_value AS Value
            FROM metadata_claims WHERE is_current = 1
            """).ToLookup(row => row.EntityId);
        data.Jobs = connection.Query<JobRow>("""
            SELECT entity_id AS EntityId, state AS State, resolved_qid AS ResolvedQid, last_outcome_category AS Outcome,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM identity_jobs
            """).GroupBy(job => job.EntityId).ToDictionary(group => group.Key, group => group.OrderBy(job => job.UpdatedAt, StringComparer.Ordinal).Last());
        data.Reviews = connection.Query<ReviewRow>("""
            SELECT entity_id AS EntityId, entity_type AS EntityType, trigger AS Trigger, status AS Status FROM review_queue
            """).ToList();
        data.Artwork = connection.Query<ArtworkRow>("""
            SELECT l.entity_id AS EntityId, l.role AS Role, l.is_preferred AS Preferred, l.sort_order AS SortOrder,
                   a.source_provider AS Provider, a.source_url AS SourceUrl, a.content_hash AS ContentHash,
                   a.width_px AS Width, a.height_px AS Height
            FROM entity_artwork_links l JOIN artwork_assets a ON a.id = l.artwork_asset_id
            """).ToLookup(row => row.EntityId);
        data.People = connection.Query<PersonRow>("""
            SELECT l.media_asset_id AS AssetId, l.role AS Role, p.name AS Name, p.wikidata_qid AS Qid
            FROM person_media_links l JOIN persons p ON p.id = l.person_id
            """).ToLookup(row => row.AssetId);
        data.Tracks = connection.Query<TrackRow>("""
            SELECT asset_id AS AssetId, kind AS Kind, language AS Language, provider AS Provider,
                   source_format AS SourceFormat, normalized_format AS NormalizedFormat
            FROM text_tracks
            """).ToLookup(row => row.AssetId);
        data.Collections = connection.Query<CollectionRow>("""
            SELECT id AS Id, parent_collection_id AS ParentId, collection_type AS Type, display_name AS Name,
                   wikidata_qid AS Qid, universe_status AS Status
            FROM collections
            """).ToDictionary(collection => collection.Id);
        data.Relationships = connection.Query<RelationshipRow>("""
            SELECT collection_id AS CollectionId, rel_type AS Type, rel_qid AS Qid, rel_label AS Label
            FROM collection_relationships
            """).ToLookup(row => row.CollectionId);
        var seriesMembers = connection.Query<(string SeriesQid, string WorkQid, string? Label, string? Position, int Owned)>("""
            SELECT series_qid AS SeriesQid, work_qid AS WorkQid, work_label AS Label, position AS Position, owned AS Owned
            FROM series_members
            """);
        data.SeriesMembers = seriesMembers.Select(member => new BaselineSeriesMember
        {
            SeriesQid = member.SeriesQid,
            WorkQid = member.WorkQid,
            Label = member.Label,
            Position = member.Position,
            Owned = member.Owned != 0,
        }).ToList();
        data.ProviderClaims = connection.Query<(string Name, int Count)>("""
            SELECT p.name AS Name, COUNT(*) AS Count
            FROM metadata_claims c JOIN metadata_providers p ON p.id = c.provider_id GROUP BY p.name
            """).ToDictionary(row => row.Name, row => row.Count, StringComparer.Ordinal);
        data.ViewRows = connection.Query<ViewRow>("""
            SELECT li.id AS Id, li.media_kind AS MediaKind, li.title AS Title, li.primary_mime_type AS MimeType,
                   li.captured_at AS CapturedAt, lf.content_hash AS ContentHash, lf.byte_size AS ByteSize,
                   (SELECT MIN(lfs.file_path) FROM local_file_sources lfs WHERE lfs.file_id = lf.id) AS FilePath
            FROM local_items li
            JOIN local_item_files lif ON lif.item_id = li.id AND lif.role = 'primary'
            JOIN local_files lf ON lf.id = lif.file_id
            WHERE li.trashed_at IS NULL
            """).ToList();
        data.ViewDetails = ReadViewDetails(connection);
        data.ViewTags = connection.Query<(Guid ItemId, string Name)>("""
            SELECT l.item_id AS ItemId, t.name AS Name FROM local_item_tag_links l JOIN tags t ON t.id = l.tag_id
            """).ToLookup(row => row.ItemId, row => row.Name);
        data.ViewAnnotations = connection.Query<(Guid ItemId, string Kind, string Value, string Source)>("""
            SELECT item_id AS ItemId, annotation_kind AS Kind, annotation_value AS Value, source AS Source
            FROM local_item_annotations
            """).ToLookup(row => row.ItemId, row => $"{row.Kind}={row.Value} ({row.Source})");
        foreach (var table in new[]
                 {
                     "media_assets", "editions", "works", "artwork_assets", "collections", "persons", "local_items",
                     "local_files", "review_queue", "identity_jobs", "text_tracks",
                 })
        {
            data.LocalIds.AddRange(connection.Query<Guid>($"SELECT id FROM {table}"));
        }

        return data;
    }

    private static Dictionary<Guid, IDictionary<string, string>> ReadViewDetails(SqliteConnection connection)
    {
        var details = new Dictionary<Guid, IDictionary<string, string>>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM local_item_metadata";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = GuidSql.FromDb(reader["item_id"]);
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                if (name is "item_id" or "updated_at" || reader.IsDBNull(i))
                {
                    continue;
                }

                var text = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
                // Document text can be huge; its length is what matters for a comparison.
                values[name] = name == "document_text" ? $"{text.Length} characters" : text;
            }

            details[id] = values;
        }

        return details;
    }

    // -- assembling ----------------------------------------------------------------------------------------

    private static BaselineRecord Assemble(ExportData data, BaselineRunInfo runInfo, BaselineNormalizer normalizer)
    {
        var record = new BaselineRecord();
        var pathOf = data.Assets.ToDictionary(asset => asset.Id, asset => normalizer.RelativePath(asset.Path));
        var assetsByWork = data.Assets.GroupBy(asset => asset.WorkId)
            .ToDictionary(group => group.Key, group => group.Select(asset => pathOf[asset.Id]).Order(StringComparer.Ordinal).ToList());
        var assetsByEdition = data.Assets.GroupBy(asset => asset.EditionId)
            .ToDictionary(group => group.Key, group => group.Select(asset => pathOf[asset.Id]).Order(StringComparer.Ordinal).ToList());

        foreach (var asset in data.Assets.OrderBy(asset => pathOf[asset.Id], StringComparer.Ordinal))
        {
            record.Items.Add(BuildItem(data, asset, pathOf, assetsByWork, assetsByEdition, normalizer));
        }

        AddReviews(record, data, pathOf, normalizer);
        AddCollections(record, data, pathOf);
        AddViewItems(record, data, normalizer);
        record.SeriesMembers = data.SeriesMembers
            .OrderBy(member => member.SeriesQid, StringComparer.Ordinal)
            .ThenBy(member => member.WorkQid, StringComparer.Ordinal)
            .ToList();
        AddCounts(record, data);
        AddMetadataAndTimings(record, data, runInfo, pathOf);
        return record;
    }

    private static BaselineItem BuildItem(
        ExportData data,
        AssetRow asset,
        Dictionary<Guid, string> pathOf,
        Dictionary<Guid, List<string>> assetsByWork,
        Dictionary<Guid, List<string>> assetsByEdition,
        BaselineNormalizer normalizer)
    {
        var work = data.Works[asset.WorkId];
        var parent = work.ParentWorkId is { } parentId && data.Works.TryGetValue(parentId, out var parentRow) ? parentRow : null;
        var scopes = new List<(string Scope, Guid Id)> { ("asset", asset.Id), ("work", work.Id) };
        if (parent is not null)
        {
            scopes.Add(("parent", parent.Id));
        }

        var item = new BaselineItem
        {
            Path = pathOf[asset.Id],
            MediaType = work.MediaType,
            Work = DescribeWork(data, work, assetsByWork, pathOf, asset.Id, normalizer),
            ParentWork = parent is null ? null : DescribeWork(data, parent, assetsByWork, pathOf, null, normalizer),
            Edition = new BaselineEdition
            {
                FormatLabel = asset.FormatLabel,
                Qid = asset.EditionQid,
                Assets = assetsByEdition[asset.EditionId],
            },
        };

        data.Jobs.TryGetValue(asset.Id, out var job);
        item.Identity = new BaselineIdentity
        {
            JobState = job?.State,
            ResolvedQid = job?.ResolvedQid,
            Outcome = job?.Outcome,
            Title = ScalarValue(data, asset.Id, "title", normalizer),
            WikidataQid = QidOf(data, work, asset.Id),
        };

        foreach (var (scope, id) in scopes)
        {
            item.Values[scope] = BuildValues(data, id, normalizer);
            foreach (var artwork in PreferredArtwork(data, id, normalizer))
            {
                item.Artwork[$"{scope}/{artwork.Key}"] = artwork.Value;
            }
        }

        // Ids come from current claims on the file, its work and the parent work.
        var ids = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (_, id) in scopes)
        {
            foreach (var claim in data.Claims[id].Where(claim => IsExternalIdKey(claim.Key)))
            {
                if (!ids.TryGetValue(claim.Key, out var set))
                {
                    ids[claim.Key] = set = new SortedSet<string>(StringComparer.Ordinal);
                }

                set.Add(normalizer.Text(claim.Value));
            }
        }

        foreach (var (key, set) in ids)
        {
            item.ExternalIds[key] = set.ToList();
        }

        // Series, then Universe: nearest group first.
        var collectionId = work.CollectionId ?? parent?.CollectionId;
        for (var guard = 0; collectionId is { } current && data.Collections.TryGetValue(current, out var collection) && guard < 8; guard++)
        {
            item.Groups.Add(new BaselineGroupRef { Type = collection.Type, Name = collection.Name, Qid = collection.Qid });
            collectionId = collection.ParentId;
        }

        item.People = data.People[asset.Id]
            .Select(person => new BaselinePerson { Role = person.Role, Name = person.Name, Qid = person.Qid })
            .OrderBy(person => person.Role, StringComparer.Ordinal).ThenBy(person => person.Name, StringComparer.Ordinal)
            .ThenBy(person => person.Qid, StringComparer.Ordinal)
            .ToList();
        item.TextTracks = data.Tracks[asset.Id]
            .Select(track => new BaselineTextTrack
            {
                Kind = track.Kind,
                Language = track.Language,
                Provider = track.Provider,
                SourceFormat = track.SourceFormat,
                NormalizedFormat = track.NormalizedFormat,
            })
            .OrderBy(track => track.Kind, StringComparer.Ordinal).ThenBy(track => track.Language, StringComparer.Ordinal)
            .ThenBy(track => track.Provider, StringComparer.Ordinal)
            .ToList();
        return item;
    }

    private static BaselineWork DescribeWork(
        ExportData data, WorkRow work, Dictionary<Guid, List<string>> assetsByWork,
        Dictionary<Guid, string> pathOf, Guid? ownAsset, BaselineNormalizer normalizer)
    {
        // A parent work (an album, a show) has no file of its own: its assets are those of its children.
        List<string> assets = assetsByWork.TryGetValue(work.Id, out var own)
            ? own
            : data.Works.Values.Where(child => child.ParentWorkId == work.Id)
                .SelectMany(child => assetsByWork.TryGetValue(child.Id, out var list) ? list : [])
                .Order(StringComparer.Ordinal).ToList();
        return new BaselineWork
        {
            Kind = work.Kind,
            Title = ScalarValue(data, work.Id, "title", normalizer) ?? (ownAsset is { } asset ? ScalarValue(data, asset, "title", normalizer) : null),
            Qid = QidOf(data, work, ownAsset),
            Ordinal = work.OrdinalSort,
            Ownership = work.Ownership,
            MatchLevel = work.MatchLevel,
            ExternalIdentifiers = work.ExternalIdentifiers is { Length: > 0 } json ? normalizer.Text(json) : null,
            Assets = assets,
        };
    }

    // The works table keeps no QID in practice: the Wikidata match is stored as a canonical value on the file and its work.
    private static string? QidOf(ExportData data, WorkRow work, Guid? assetId) =>
        work.Qid is { Length: > 0 } column ? column
        : data.Values[work.Id].Where(row => row.Key == "wikidata_qid").Select(row => row.Value).FirstOrDefault()
            ?? (assetId is { } id ? data.Values[id].Where(row => row.Key == "wikidata_qid").Select(row => row.Value).FirstOrDefault() : null);

    private static string? ScalarValue(ExportData data, Guid entityId, string key, BaselineNormalizer normalizer) =>
        data.Values[entityId].Where(row => row.Key == key).Select(row => normalizer.Value(key, row.Value)).FirstOrDefault();

    private static BaselineValueSet BuildValues(ExportData data, Guid entityId, BaselineNormalizer normalizer)
    {
        var set = new BaselineValueSet();
        foreach (var row in data.Values[entityId].OrderBy(row => row.Key, StringComparer.Ordinal))
        {
            set.Scalars[row.Key] = new BaselineValue
            {
                Value = normalizer.Value(row.Key, row.Value),
                Provider = row.Winner is { } winner && data.Providers.TryGetValue(winner, out var name) ? name : null,
                Conflicted = row.Conflicted,
                NeedsReview = row.NeedsReview,
            };
        }

        foreach (var group in data.Arrays[entityId].GroupBy(row => row.Key).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            set.Arrays[group.Key] = group
                .Select(row => row.Qid is { Length: > 0 } ? $"{normalizer.Text(row.Value)} [{row.Qid}]" : normalizer.Text(row.Value))
                .Order(StringComparer.Ordinal)
                .ToList();
        }

        return set;
    }

    private static IEnumerable<KeyValuePair<string, BaselineArtwork>> PreferredArtwork(ExportData data, Guid entityId, BaselineNormalizer normalizer)
    {
        foreach (var group in data.Artwork[entityId].GroupBy(link => link.Role))
        {
            var chosen = group.Where(link => link.Preferred != 0).OrderBy(link => link.SortOrder).FirstOrDefault();
            if (chosen is null)
            {
                continue;
            }

            yield return new(group.Key, new BaselineArtwork
            {
                Provider = chosen.Provider,
                SourceUrl = chosen.SourceUrl is null ? null : normalizer.Text(chosen.SourceUrl),
                ContentHash = chosen.ContentHash,
                Width = chosen.Width,
                Height = chosen.Height,
            });
        }
    }

    private static bool IsExternalIdKey(string key)
    {
        if (CreditPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return false;
        }

        return key.EndsWith("_id", StringComparison.Ordinal) || key.EndsWith("_qid", StringComparison.Ordinal)
            || key is "isbn" or "asin" or "isrc" or "isbn_10" or "isbn_13" or "wikidata_qid";
    }

    private static void AddReviews(BaselineRecord record, ExportData data, Dictionary<Guid, string> pathOf, BaselineNormalizer normalizer)
    {
        var itemsByPath = record.Items.ToDictionary(item => item.Path, StringComparer.Ordinal);
        foreach (var row in data.Reviews)
        {
            if (pathOf.TryGetValue(row.EntityId, out var path))
            {
                itemsByPath[path].Review.Add(new BaselineReview { Subject = path, Trigger = row.Trigger, Status = row.Status });
                continue;
            }

            var title = data.Works.ContainsKey(row.EntityId) ? ScalarValue(data, row.EntityId, "title", normalizer) : null;
            record.UnattachedReview.Add(new BaselineReview
            {
                Subject = title is null ? $"{row.EntityType.ToLowerInvariant()}:unknown" : $"work:{title}",
                Trigger = row.Trigger,
                Status = row.Status,
            });
        }

        foreach (var item in record.Items)
        {
            item.Review = item.Review.OrderBy(review => review.Trigger, StringComparer.Ordinal).ThenBy(review => review.Status, StringComparer.Ordinal).ToList();
        }

        record.UnattachedReview = record.UnattachedReview
            .OrderBy(review => review.Subject, StringComparer.Ordinal).ThenBy(review => review.Trigger, StringComparer.Ordinal)
            .ThenBy(review => review.Status, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddCollections(BaselineRecord record, ExportData data, Dictionary<Guid, string> pathOf)
    {
        var assetsByCollection = new Dictionary<Guid, SortedSet<string>>();
        foreach (var asset in data.Assets)
        {
            var work = data.Works[asset.WorkId];
            var parent = work.ParentWorkId is { } parentId && data.Works.TryGetValue(parentId, out var parentRow) ? parentRow : null;
            var visited = new HashSet<Guid>();
            for (var collectionId = work.CollectionId ?? parent?.CollectionId;
                 collectionId is { } id && visited.Add(id) && data.Collections.TryGetValue(id, out var collection);
                 collectionId = collection.ParentId)
            {
                if (!assetsByCollection.TryGetValue(id, out var set))
                {
                    assetsByCollection[id] = set = new SortedSet<string>(StringComparer.Ordinal);
                }

                set.Add(pathOf[asset.Id]);
            }
        }

        record.Collections = data.Collections.Values
            .Select(collection => new BaselineCollection
            {
                Type = collection.Type,
                Name = collection.Name,
                Qid = collection.Qid,
                Parent = collection.ParentId is { } id && data.Collections.TryGetValue(id, out var parent) ? parent.Name ?? parent.Qid : null,
                Status = collection.Status,
                Assets = assetsByCollection.TryGetValue(collection.Id, out var set) ? set.ToList() : [],
                Relationships = data.Relationships[collection.Id]
                    .Select(rel => rel.Label is { Length: > 0 } ? $"{rel.Type}:{rel.Qid} ({rel.Label})" : $"{rel.Type}:{rel.Qid}")
                    .Order(StringComparer.Ordinal).ToList(),
            })
            .OrderBy(collection => collection.Type, StringComparer.Ordinal).ThenBy(collection => collection.Name, StringComparer.Ordinal)
            .ThenBy(collection => collection.Qid, StringComparer.Ordinal).ThenBy(collection => collection.Assets.FirstOrDefault(), StringComparer.Ordinal)
            .ToList();
    }

    private static void AddViewItems(BaselineRecord record, ExportData data, BaselineNormalizer normalizer)
    {
        foreach (var row in data.ViewRows.Where(row => row.FilePath is not null).OrderBy(row => row.FilePath, StringComparer.Ordinal))
        {
            var item = new BaselineViewItem
            {
                Path = normalizer.RelativePath(row.FilePath!),
                MediaKind = row.MediaKind,
                Title = row.Title,
                MimeType = row.MimeType,
                ByteSize = row.ByteSize,
                ContentHash = row.ContentHash,
                CapturedAt = row.CapturedAt,
                Tags = data.ViewTags[row.Id].Order(StringComparer.Ordinal).ToList(),
                Annotations = data.ViewAnnotations[row.Id].Select(normalizer.Text).Order(StringComparer.Ordinal).ToList(),
            };
            if (data.ViewDetails.TryGetValue(row.Id, out var details))
            {
                foreach (var (key, value) in details)
                {
                    item.Details[key] = normalizer.Value(key, value);
                }
            }

            record.ViewItems.Add(item);
        }

        record.ViewItems = record.ViewItems.OrderBy(item => item.Path, StringComparer.Ordinal).ToList();
    }

    private static void AddCounts(BaselineRecord record, ExportData data)
    {
        BaselineTypeCounts For(string type)
        {
            if (!record.Counts.ByMediaType.TryGetValue(type, out var counts))
            {
                record.Counts.ByMediaType[type] = counts = new BaselineTypeCounts();
            }

            return counts;
        }

        foreach (var item in record.Items)
        {
            var counts = For(item.MediaType);
            counts.Assets++;
            if (item.Identity.WikidataQid is { Length: > 0 })
            {
                counts.WithWikidataQid++;
            }

            if (item.Artwork.Count > 0)
            {
                counts.WithArtwork++;
            }

            counts.ReviewPending += item.Review.Count(review => review.Status == "Pending");
            counts.ReviewClosed += item.Review.Count(review => review.Status != "Pending");
            counts.TextTracks += item.TextTracks.Count;
        }

        foreach (var work in data.Works.Values)
        {
            For(work.MediaType).Works++;
        }

        foreach (var state in data.Jobs.Values.GroupBy(job => job.State))
        {
            record.Counts.IdentityJobStates[state.Key] = state.Count();
        }

        foreach (var kind in record.ViewItems.GroupBy(item => item.MediaKind))
        {
            record.Counts.ViewItemsByKind[kind.Key] = kind.Count();
        }

        record.Counts.Works = data.Works.Count;
        record.Counts.Collections = data.Collections.Count;
    }

    private static void AddMetadataAndTimings(BaselineRecord record, ExportData data, BaselineRunInfo runInfo, Dictionary<Guid, string> pathOf)
    {
        record.Metadata = new BaselineMetadata
        {
            Label = runInfo.Label,
            BuildCommit = runInfo.BuildCommit,
            SampleRoot = runInfo.SampleRoot,
            StartedAt = runInfo.StartedAt,
            EndedAt = runInfo.EndedAt,
            ExportedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            CatalogueFolders = runInfo.CatalogueFolders.ToList(),
            ViewFolders = runInfo.ViewFolders.ToList(),
            ProviderClaims = new SortedDictionary<string, int>(data.ProviderClaims, StringComparer.Ordinal),
            HttpHosts = new SortedDictionary<string, int>(runInfo.HttpHosts.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal),
            ProviderAnswers = runInfo.ProviderAnswers,
        };
        if (DateTimeOffset.TryParse(runInfo.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var started)
            && DateTimeOffset.TryParse(runInfo.EndedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ended))
        {
            record.Metadata.TotalDurationSeconds = Math.Round((ended - started).TotalSeconds, 1);
            record.Timings.TotalSeconds = record.Metadata.TotalDurationSeconds;
        }

        foreach (var asset in data.Assets)
        {
            if (data.Jobs.TryGetValue(asset.Id, out var job)
                && DateTime.TryParse(job.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created)
                && DateTime.TryParse(job.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var updated))
            {
                record.Timings.RegisteredToIdentified[pathOf[asset.Id]] = Math.Round(Math.Max(0, (updated - created).TotalSeconds), 1);
            }
        }
    }
}
