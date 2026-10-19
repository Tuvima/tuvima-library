using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class SharedEntityEditorRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_shared_editor_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;

    public SharedEntityEditorRepositoryTests()
    {
        _db = new DatabaseConnection(_path);
        _db.InitializeSchema();
    }

    [Fact]
    public async Task VisibleUniverseSearch_IsPagedTypedAndDoesNotLeakOtherWorkLinks()
    {
        var repo = new FictionalEntityRepository(_db);
        var categories = new[] { "Character", "Location", "Organization", "Event", "Object" };
        foreach (var (category, index) in categories.Select((value, index) => (value, index)))
        {
            var entity = new FictionalEntity { WikidataQid = $"Q{index + 1}", Label = $"{category} A", EntitySubType = category, FictionalUniverseQid = "QU" };
            await repo.CreateAsync(entity);
            await repo.LinkToWorkAsync(new FictionalEntityWorkLink(entity.Id, "QVISIBLE", "Visible", "appears_in"));
        }
        var hidden = new FictionalEntity { WikidataQid = "Qhidden", Label = "Hidden", EntitySubType = "Object", FictionalUniverseQid = "QU" };
        await repo.CreateAsync(hidden);
        await repo.LinkToWorkAsync(new FictionalEntityWorkLink(hidden.Id, "QHIDDEN", "Hidden", "appears_in"));

        var page = await repo.SearchVisibleByUniverseAsync("QU", new[] { "QVISIBLE" }, null, null, 1, 2);
        Assert.Equal(5, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.DoesNotContain(page.Items, item => item.Id == hidden.Id);
        Assert.Contains((await repo.SearchVisibleByUniverseAsync("QU", new[] { "QVISIBLE" }, "Event", "Event", 0, 10)).Items, item => item.EntitySubType == "Event");
    }

    [Fact]
    public async Task UserOverride_SurvivesEnrichmentAndAllPrimaryReadsProjectIt()
    {
        var repo = new FictionalEntityRepository(_db);
        var entity = new FictionalEntity { WikidataQid = "Q1", Label = "Provider", Description = "Provider description", EntitySubType = "Object", FictionalUniverseQid = "QU" };
        await repo.CreateAsync(entity);
        await repo.LinkToWorkAsync(new FictionalEntityWorkLink(entity.Id, "QVISIBLE", null, "appears_in"));
        await repo.UpdateUserDetailsAsync(entity.Id, "User label", "User description");
        await repo.UpdateEnrichmentAsync(entity.Id, null, "New provider description", null, DateTimeOffset.UtcNow);

        Assert.Equal("User label", (await repo.FindByIdAsync(entity.Id))!.Label);
        Assert.Equal("User description", (await repo.FindByQidAsync("Q1"))!.Description);
        Assert.Equal("User label", (await repo.FindByQidsAsync(["Q1"])).Single().Label);
        Assert.Equal("User label", (await repo.GetByUniverseAsync("QU")).Single().Label);
        Assert.Equal("User label", (await repo.GetByUniverseAndTypeAsync("QU", "Object")).Single().Label);
        Assert.Equal("User label", (await repo.GetByWorkQidAsync("QVISIBLE")).Single().Label);
    }

    [Fact]
    public async Task NarrativeRootOverride_IsProjectedAndProvenanceReturnsWorkId()
    {
        var roots = new NarrativeRootRepository(_db);
        await roots.UpsertAsync(new NarrativeRoot { Qid = "QU", Label = "Provider", Level = "Universe" });
        await roots.UpdateUserDetailsAsync("QU", "User root", "User root description");
        Assert.Equal("User root", (await roots.FindByQidAsync("QU"))!.Label);
        using var conn = _db.CreateConnection();
        var workId = Guid.NewGuid();
        conn.Execute("INSERT INTO works (id, media_type, work_kind) VALUES (@id, 'Movies', 'standalone');", new { id = GuidSql.ToBlob(workId) });
        conn.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@id, 'narrative_root_qid', 'QU', datetime('now'));", new { id = GuidSql.ToBlob(workId) });
        Assert.Contains(workId, await roots.FindWorkIdsByProvenanceQidAsync("QU"));
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_path);
    }
}
