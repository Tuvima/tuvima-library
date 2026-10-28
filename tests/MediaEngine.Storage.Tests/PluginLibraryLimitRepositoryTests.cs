namespace MediaEngine.Storage.Tests;

/// <summary>The libraries a plugin is limited to, against a real temporary data store.</summary>
public sealed class PluginLibraryLimitRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly PluginLibraryLimitRepository _repo;

    public PluginLibraryLimitRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_plugin_library_limits_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repo = new PluginLibraryLimitRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* temp data store cleanup is best effort */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* temp data store cleanup is best effort */ }
    }

    [Fact]
    public async Task APluginWithNothingSaved_IsNotLimited()
    {
        Assert.Empty(await _repo.GetAsync("tuvima.skip-markers"));
    }

    [Fact]
    public async Task Replacing_KeepsOnlyTheNewChoice_AndIgnoresDuplicatesAndBlanks()
    {
        await _repo.ReplaceAsync("tuvima.skip-markers", ["lib-a", "lib-b"]);

        await _repo.ReplaceAsync("tuvima.skip-markers", ["lib-c", "lib-c", " "]);

        Assert.Equal(["lib-c"], await _repo.GetAsync("tuvima.skip-markers"));
    }

    [Fact]
    public async Task OnePluginsLimit_NeverTouchesAnothers()
    {
        await _repo.ReplaceAsync("plugin.one", ["lib-a"]);
        await _repo.ReplaceAsync("plugin.two", ["lib-b"]);

        await _repo.DeleteForPluginAsync("plugin.one");

        Assert.Empty(await _repo.GetAsync("plugin.one"));
        Assert.Equal(["lib-b"], await _repo.GetAsync("plugin.two"));
    }
}
