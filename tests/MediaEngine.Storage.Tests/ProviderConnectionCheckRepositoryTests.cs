using MediaEngine.Domain.Contracts;

namespace MediaEngine.Storage.Tests;

public sealed class ProviderConnectionCheckRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(),
        $"tuvima_provider_checks_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public ProviderConnectionCheckRepositoryTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    [Fact]
    public async Task LastConfiguredCheck_IsCachedAndCanBeRemoved()
    {
        var repository = new ProviderConnectionCheckRepository(_database);
        var checkedAt = DateTimeOffset.UtcNow;
        await repository.UpsertAsync(new ProviderConnectionCheck(
            "subdl", "valid", "SubDL accepted the key.", checkedAt, 123));
        await repository.UpsertAsync(new ProviderConnectionCheck(
            "subdl", "connectivity_failure", "The provider could not be reached.",
            checkedAt.AddMinutes(1), 25));

        var stored = Assert.Single(await repository.GetAllAsync());
        Assert.Equal("subdl", stored.ProviderName);
        Assert.Equal("connectivity_failure", stored.Status);
        Assert.Equal(25, stored.ResponseTimeMs);

        await repository.DeleteAsync("subdl");
        Assert.Empty(await repository.GetAllAsync());
    }

    public void Dispose()
    {
        _database.Dispose();
        try { File.Delete(_path); } catch (IOException) { }
    }
}
