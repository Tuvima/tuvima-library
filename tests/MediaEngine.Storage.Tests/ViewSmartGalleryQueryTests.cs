using System.Data;
using Dapper;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class ViewSmartGalleryQueryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-smart-view-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly ViewPersonalSpaceRepository _spaces;
    private readonly LocalAssetRepository _assets;
    private readonly Guid _ownerId;
    private readonly ViewPersonalSpace _space;

    public ViewSmartGalleryQueryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _spaces = new ViewPersonalSpaceRepository(_database);
        _assets = new LocalAssetRepository(_database);
        _ownerId = InsertProfile("Smart Gallery owner");
        _space = _spaces.CreateAsync(_ownerId, Guid.NewGuid()).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task EvaluatesAndOrGroupsWithAuthorizedLibrariesAndKeysetPaging()
    {
        var newest = await AddAsync('1', "favorite.jpg", LocalAssetMediaKinds.Image,
            new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero), tags: ["family"]);
        await _assets.SetFlagsAsync(newest.ItemId, favorite: true, hidden: null);
        var video = await AddAsync('2', "long.mp4", LocalAssetMediaKinds.Video,
            new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero), duration: 95);
        var excluded = await AddAsync('3', "ordinary.jpg", LocalAssetMediaKinds.Image,
            new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero));
        var rule = Definition(
            Group("all", "or", Rule("media_type", "eq", "image"), Rule("favorite", "eq", "true"), Rule("tags", "eq", "family")),
            Group("all", "or", Rule("media_type", "eq", "video"), Rule("duration", "gt", "60")));

        var first = _assets.QueryTimeline(new LocalAssetTimelineQuery(
            [_space.LibraryId], Limit: 1, SmartRule: rule));
        var second = _assets.QueryTimeline(new LocalAssetTimelineQuery(
            [_space.LibraryId], Limit: 1,
            BeforeEffectiveAt: first.NextCursor!.EffectiveAt,
            BeforeItemId: first.NextCursor.ItemId,
            SmartRule: rule));

        Assert.Equal(newest.ItemId, Assert.Single(first.Items).Id);
        Assert.True(first.HasMore);
        Assert.Equal(video.ItemId, Assert.Single(second.Items).Id);
        Assert.False(second.HasMore);
        Assert.DoesNotContain(excluded.ItemId, first.Items.Concat(second.Items).Select(item => item.Id));
    }

    [Fact]
    public async Task MembershipChangesImmediatelyAfterFlagTagAndMetadataChanges()
    {
        var asset = await AddAsync('4', "changing.jpg", LocalAssetMediaKinds.Image,
            DateTimeOffset.UtcNow, width: 200, height: 100, tags: ["family"]);
        var rule = Definition(Group("all", "or",
            Rule("favorite", "eq", "true"),
            Rule("tags", "eq", "family"),
            Rule("orientation", "eq", "landscape")));

        Assert.Empty(Query(rule));
        await _assets.SetFlagsAsync(asset.ItemId, favorite: true, hidden: null);
        Assert.Equal(asset.ItemId, Assert.Single(Query(rule)).Id);

        await _assets.ReplaceTagsAsync(asset.ItemId, ["work"]);
        Assert.Empty(Query(rule));
        await _assets.ReplaceTagsAsync(asset.ItemId, ["family"]);
        Assert.Equal(asset.ItemId, Assert.Single(Query(rule)).Id);

        await UpdateDimensionsAsync(asset, width: 100, height: 200);
        Assert.Empty(Query(rule));
        await UpdateDimensionsAsync(asset, width: 200, height: 100);
        Assert.Equal(asset.ItemId, Assert.Single(Query(rule)).Id);
    }

    [Fact]
    public async Task EveryViewFieldUsesStoredMetadataAndPeopleRequireDiscoveryGradeProvenance()
    {
        var source = await _spaces.UpsertSourceAsync(new ViewSource(
            Guid.Empty, _space.Id, ViewSourceType.Folder, "Camera imports", "folder:camera",
            DateTimeOffset.UtcNow, default, default, ViewSourceStorageMode.Linked,
            ExternalPath: @"C:\camera-imports"));
        var device = await _spaces.UpsertDeviceAsync(new ViewDevice(
            Guid.Empty, _space.Id, source.Id, "phone-1", "Shy's phone", "Example", "Camera 1",
            DateTimeOffset.UtcNow, ViewDeviceBackupState.Complete, default, default));
        var asset = await AddAsync('5', "trip.JPG", LocalAssetMediaKinds.Image,
            new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
            width: 300, height: 200, duration: 3, tags: ["travel"],
            sourceId: source.Id, deviceId: device.Id, location: "Dallas, Texas");
        await _assets.SetFlagsAsync(asset.ItemId, favorite: true, hidden: null);
        await _assets.AddAnnotationAsync(asset.ItemId,
            new LocalAssetAnnotation("object_label", "Sarah", "object-detector"));
        await _assets.AddAnnotationAsync(asset.ItemId,
            new LocalAssetAnnotation("person_identity", "Sarah", "face-cluster"));
        var peopleRule = Definition(Group("all", "or", Rule("people", "eq", "Sarah")));
        Assert.Empty(Query(peopleRule));

        await _assets.AddAnnotationAsync(asset.ItemId,
            new LocalAssetAnnotation("face_identity", "Someone else", "review-workflow",
                ReviewedAt: DateTimeOffset.UtcNow));
        Assert.Empty(Query(peopleRule));
        await _assets.AddAnnotationAsync(asset.ItemId,
            new LocalAssetAnnotation("named_person", "Sarah", "embedded-metadata"));

        var allFields = Definition(Group("all", "or",
            Rule("media_type", "eq", "image"),
            Rule("file_type", "in", values: ["image/jpeg", "png"]),
            Rule("orientation", "neq", "portrait"),
            Rule("duration", "between", values: ["1", "4"]),
            Rule("captured_date", "between", values: ["2026-08-01", "2026-08-31"]),
            Rule("people", "contains", "ara"),
            Rule("place", "contains", "dallas"),
            Rule("device", "eq", device.Id.ToString()),
            Rule("tags", "eq", "TRAVEL"),
            Rule("favorite", "eq", "true"),
            Rule("owner", "eq", _ownerId.ToString()),
            Rule("source", "eq", "Camera imports")));

        Assert.Equal(asset.ItemId, Assert.Single(Query(allFields)).Id);
    }

    [Fact]
    public async Task ValuesAreParameterizedAndCannotInjectSql()
    {
        await AddAsync('6', "safe.jpg", LocalAssetMediaKinds.Image, DateTimeOffset.UtcNow, tags: ["family"]);
        const string attack = "family') OR 1=1; DROP TABLE local_items; --";
        var rule = Definition(Group("all", "or", Rule("tags", "eq", attack)));

        var compiled = LocalAssetSmartRuleSqlCompiler.Compile(rule);
        var results = Query(rule);

        Assert.DoesNotContain(attack, compiled.Predicate, StringComparison.Ordinal);
        Assert.Contains(compiled.Parameters.ParameterNames,
            name => compiled.Parameters.Get<string>(name) == attack.ToLowerInvariant());
        Assert.Empty(results);
        using var connection = _database.CreateConnection();
        Assert.Equal(1, connection.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'local_items';"));
    }

    [Fact]
    public async Task ExactTagRuleUsesTheIndexedRelationshipLookup()
    {
        await AddAsync('7', "indexed.jpg", LocalAssetMediaKinds.Image, DateTimeOffset.UtcNow, tags: ["indexed"]);
        var compiled = LocalAssetSmartRuleSqlCompiler.Compile(
            Definition(Group("all", "or", Rule("tags", "eq", "indexed"))));
        var parameters = new DynamicParameters();
        parameters.Add("LibraryId", GuidSql.ToBlob(_space.LibraryId), DbType.Binary);
        parameters.AddDynamicParams(compiled.Parameters);
        using var connection = _database.CreateConnection();

        var plan = connection.Query<PlanRow>($"""
            EXPLAIN QUERY PLAN
            SELECT li.id
              FROM local_items li
              LEFT JOIN local_item_metadata lm ON lm.item_id = li.id
             WHERE li.library_id = @LibraryId AND ({compiled.Predicate});
            """, parameters).Select(row => row.Detail).ToList();

        Assert.Contains(plan, detail => detail.Contains("local_items", StringComparison.OrdinalIgnoreCase)
            && (detail.Contains("USING INDEX", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("USING COVERING INDEX", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(plan, detail => detail.Contains("local_item_tags", StringComparison.OrdinalIgnoreCase)
            && (detail.Contains("USING INDEX", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("USING COVERING INDEX", StringComparison.OrdinalIgnoreCase)));
    }

    private IReadOnlyList<MediaEngine.Contracts.LocalAssets.LocalAssetDto> Query(CollectionRuleDefinition rule) =>
        _assets.QueryTimeline(new LocalAssetTimelineQuery([_space.LibraryId], SmartRule: rule)).Items;

    private async Task<AssetSeed> AddAsync(char hashCharacter, string fileName, string mediaKind,
        DateTimeOffset capturedAt, int? width = null, int? height = null, double? duration = null,
        IReadOnlyCollection<string>? tags = null, Guid? sourceId = null, Guid? deviceId = null,
        string? location = null)
    {
        var registration = new LocalAssetRegistration(
            _space.LibraryId, _space.Id, _ownerId, mediaKind, Path.GetFileNameWithoutExtension(fileName), capturedAt,
            [new LocalAssetFileRegistration(
                Path.Combine(@"C:\view-smart", fileName), new string(hashCharacter, 64), fileName,
                mediaKind == LocalAssetMediaKinds.Video ? "video/mp4" : "image/jpeg", 1024,
                DateTimeOffset.UtcNow, SourceId: sourceId, DeviceId: deviceId)],
            Width: width, Height: height, DurationSeconds: duration, LocationName: location, Tags: tags);
        var result = await _assets.UpsertAsync(registration);
        return new AssetSeed(result.ItemId, registration);
    }

    private async Task UpdateDimensionsAsync(AssetSeed asset, int width, int height) =>
        _ = await _assets.UpsertAsync(asset.Registration with
        {
            ExistingItemId = asset.ItemId,
            Width = width,
            Height = height,
            Tags = null,
        });

    private Guid InsertProfile(string name)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
            VALUES (@id, @name, '#7C4DFF', 'RestrictedProfile', @now);
            """, new { id, name, now = DateTimeOffset.UtcNow });
        return id;
    }

    private static CollectionRuleDefinition Definition(params CollectionRuleGroup[] groups) =>
        new() { Version = 1, Groups = [.. groups] };

    private static CollectionRuleGroup Group(string mode, string join, params CollectionRulePredicate[] conditions) =>
        new() { MatchMode = mode, JoinWithPrevious = join, Conditions = [.. conditions] };

    private static CollectionRulePredicate Rule(string field, string op, string? value = null, string[]? values = null) =>
        new() { Field = field, Op = op, Value = value, Values = values };

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_path);
    }

    private sealed record AssetSeed(Guid ItemId, LocalAssetRegistration Registration);
    private sealed class PlanRow { public string Detail { get; init; } = string.Empty; }
}
