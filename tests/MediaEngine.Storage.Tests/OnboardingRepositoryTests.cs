using MediaEngine.Storage;

namespace MediaEngine.Storage.Tests;

public sealed class OnboardingRepositoryTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"tuvima-onboarding-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly OnboardingRepository _repository;

    public OnboardingRepositoryTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new OnboardingRepository(_database);
    }

    [Fact]
    public async Task SetupStartAndStepWritesAreDurableAndIdempotent()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(1);

        Assert.True(await _repository.TryBeginAsync("session-hash", Guid.NewGuid(), expires, CancellationToken.None));
        Assert.True(await _repository.TryBeginAsync("other-hash", Guid.NewGuid(), expires, CancellationToken.None));

        await _repository.SetStepAsync("welcome", "passed", "Named.", null, null, CancellationToken.None);
        await _repository.SetStepAsync("tools", "passed", "Ready.", null, null, CancellationToken.None);
        await _repository.SetStepAsync("tools", "passed", "Ready.", null, null, CancellationToken.None);

        var workflow = _repository.Get();
        Assert.Equal(1, workflow.WorkflowVersion);
        Assert.Equal("profile", workflow.CurrentStep);
        Assert.Equal("passed", workflow.Steps.Single(step => step.Key == "tools").Status);
        Assert.False(await _repository.ValidateSessionAsync("session-hash", CancellationToken.None));
        Assert.True(await _repository.ValidateSessionAsync("other-hash", CancellationToken.None));
    }

    [Fact]
    public async Task CompletionRequiresCoreSetupAndAllowsProviderDeferral()
    {
        Assert.True(await _repository.TryBeginAsync(
            "session-hash", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None));
        await _repository.SetStepAsync("welcome", "passed", null, null, null, CancellationToken.None);
        await _repository.SetStepAsync("tools", "passed", null, null, null, CancellationToken.None);
        await _repository.SetStepAsync("profile", "passed", null, null, null, CancellationToken.None);
        await _repository.SetStepAsync("metadata", "deferred", null, "/setup?step=metadata", null, CancellationToken.None);
        Assert.False(await _repository.CompleteAsync(CancellationToken.None));

        await _repository.SetStepAsync("libraries", "passed", null, null, null, CancellationToken.None);
        Assert.True(await _repository.CompleteAsync(CancellationToken.None));
        Assert.Equal("complete", _repository.Get().State);
        Assert.False(await _repository.ValidateSessionAsync("session-hash", CancellationToken.None));
    }

    [Fact]
    public void TheWorkflowListsExactlyTheSevenSetupStepsInOrder()
    {
        var keys = _repository.Get().Steps.Select(step => step.Key).ToArray();
        Assert.Equal(["welcome", "profile", "tools", "libraries", "vault", "metadata", "done"], keys);
        Assert.Equal("welcome", _repository.Get().CurrentStep);
    }

    [Fact]
    public async Task AnOpenToolsStepKeepsSetupOnToolsUntilItIsPassed_AndTheVaultNeverBlocksCompletion()
    {
        await _repository.TryBeginAsync("session", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        await _repository.SetStepAsync("welcome", "passed", null, null, null, CancellationToken.None);
        await _repository.SetStepAsync("profile", "passed", null, null, null, CancellationToken.None);
        await _repository.SetStepAsync("tools", "in_progress", null, null, null, CancellationToken.None);
        Assert.Equal("tools", _repository.Get().CurrentStep);
        Assert.False(await _repository.CompleteAsync(CancellationToken.None));

        await _repository.SetStepAsync("tools", "passed", null, null, null, CancellationToken.None);
        await _repository.SetStepAsync("libraries", "deferred", null, null, null, CancellationToken.None);
        await _repository.SetStepAsync("metadata", "deferred", null, null, null, CancellationToken.None);
        Assert.True(await _repository.CompleteAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MediaDeferralSurvivesReloadAndAllowsCompletion()
    {
        await _repository.TryBeginAsync("session", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        foreach (var key in new[] { "welcome", "tools", "profile" })
        {
            await _repository.SetStepAsync(key, "passed", null, null, null, CancellationToken.None);
        }

        foreach (var key in new[] { "libraries", "metadata" })
        {
            await _repository.SetStepAsync(key, "deferred", "Later", null, null, CancellationToken.None);
        }

        var reloaded = new OnboardingRepository(_database);
        Assert.Equal("deferred", reloaded.Get().Steps.Single(step => step.Key == "libraries").Status);
        Assert.True(await reloaded.CompleteAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
