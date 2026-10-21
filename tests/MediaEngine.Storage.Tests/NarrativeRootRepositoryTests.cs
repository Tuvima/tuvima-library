using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Models;

namespace MediaEngine.Storage.Tests;

public sealed class NarrativeRootRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly NarrativeRootRepository _repo;

    public NarrativeRootRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_narrative_roots_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repo = new NarrativeRootRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task UpsertAsync_WithNoParent_KeepsTheParentRecordedEarlier()
    {
        await _repo.UpsertAsync(Root("Q10", "Middle-earth", NarrativeLevel.Universe, parentQid: null));
        await _repo.UpsertAsync(Root("Q20", "Lord of the Rings", NarrativeLevel.Franchise, parentQid: "Q10"));

        await _repo.UpsertAsync(Root("Q20", "Lord of the Rings", NarrativeLevel.Franchise, parentQid: null));

        var saved = await _repo.FindByQidAsync("Q20");
        Assert.Equal("Q10", saved!.ParentQid);
    }

    [Fact]
    public async Task UpsertAsync_WithAParent_ReplacesTheParent()
    {
        await _repo.UpsertAsync(Root("Q20", "Lord of the Rings", NarrativeLevel.Franchise, parentQid: "Q10"));

        await _repo.UpsertAsync(Root("Q20", "Lord of the Rings", NarrativeLevel.Franchise, parentQid: "Q11"));

        Assert.Equal("Q11", (await _repo.FindByQidAsync("Q20"))!.ParentQid);
    }

    [Fact]
    public async Task EnsureAsync_InsertsAMissingRecordAndNeverChangesAnExistingOne()
    {
        await _repo.EnsureAsync(Root("Q10", "Middle-earth", NarrativeLevel.Universe, parentQid: null));
        await _repo.EnsureAsync(Root("Q10", "Something else", NarrativeLevel.Series, parentQid: "Q99"));

        var saved = await _repo.FindByQidAsync("Q10");
        Assert.Equal("Middle-earth", saved!.Label);
        Assert.Equal(NarrativeLevel.Universe, saved.Level);
        Assert.Null(saved.ParentQid);
        Assert.Equal(1, await _repo.CountAsync());
    }

    private static NarrativeRoot Root(string qid, string label, string level, string? parentQid) => new()
    {
        Qid = qid,
        Label = label,
        Level = level,
        ParentQid = parentQid,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
