using Dapper;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class ApplicationRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly ApplicationRepository _repository;
    private readonly DateTimeOffset _now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    public ApplicationRepositoryTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_applications_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _repository = new ApplicationRepository(_database);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }

    [Fact]
    public async Task CrudAndPermissions_UseGuidBlobsAndIncrementLiveAuthorizationVersion()
    {
        var application = NewApplication("Home bridge");
        var permissions = Set(
            ApplicationPermissionIds.LibraryRead,
            ApplicationPermissionIds.SystemStatusRead);

        await _repository.InsertApplicationAsync(application, permissions);

        using (var connection = _database.CreateConnection())
        {
            var storage = connection.QuerySingle<(string IdType, int IdLength)>(
                "SELECT typeof(id) AS IdType, length(id) AS IdLength FROM applications WHERE id = @id;",
                new { id = GuidSql.ToBlob(application.Id) });
            Assert.Equal("blob", storage.IdType);
            Assert.Equal(16, storage.IdLength);
        }

        var inserted = Assert.IsType<Application>(await _repository.GetApplicationAsync(application.Id));
        Assert.Equal(application.Name, inserted.Name);
        Assert.Equal(permissions, await _repository.GetApplicationPermissionsAsync(application.Id));

        inserted.Name = "Renamed bridge";
        inserted.IsAdministrator = true;
        inserted.UpdatedAt = _now.AddMinutes(1);
        await _repository.UpdateApplicationAsync(inserted);
        var updated = Assert.IsType<Application>(await _repository.GetApplicationAsync(application.Id));
        Assert.Equal("Renamed bridge", updated.Name);
        Assert.True(updated.IsAdministrator);
        Assert.Equal(2, updated.AuthorizationVersion);

        var replacementPermissions = Set(ApplicationPermissionIds.MetadataRead);
        await _repository.ReplacePermissionsAsync(application.Id, replacementPermissions, _now.AddMinutes(2));
        var afterPermissions = Assert.IsType<Application>(await _repository.GetApplicationAsync(application.Id));
        Assert.Equal(3, afterPermissions.AuthorizationVersion);
        Assert.Equal(replacementPermissions, await _repository.GetApplicationPermissionsAsync(application.Id));

        Assert.True(await _repository.DeleteApplicationAsync(application.Id, _now.AddMinutes(3)));
        Assert.Null(await _repository.GetApplicationAsync(application.Id));
        Assert.False(await _repository.DeleteApplicationAsync(application.Id, _now.AddMinutes(4)));
    }

    [Fact]
    public async Task CredentialLookup_DeniesExpiredRevokedAndDisabledApplicationsAndTracksOnlyActiveUsage()
    {
        var application = NewApplication("Reader");
        await _repository.InsertApplicationAsync(application, Set(ApplicationPermissionIds.LibraryRead));
        var active = NewCredential(application.Id, "active", "active-hash", _now.AddHours(1));
        var expired = NewCredential(application.Id, "expired", "expired-hash", _now);
        var disabledCandidate = NewCredential(application.Id, "disabled candidate", "disabled-hash", _now.AddHours(1));
        await _repository.InsertCredentialAsync(active);
        await _repository.InsertCredentialAsync(expired);
        await _repository.InsertCredentialAsync(disabledCandidate);

        var identity = Assert.IsType<ApplicationCredentialIdentity>(
            await _repository.FindApplicationCredentialAsync("active-hash", _now));
        Assert.Equal(application.Id, identity.Application.Id);
        Assert.Equal(active.Id, identity.Credential.Id);
        Assert.Contains(ApplicationPermissionIds.LibraryRead, identity.Permissions);
        Assert.Null(await _repository.FindApplicationCredentialAsync("expired-hash", _now));

        var usedAt = _now.AddMinutes(5);
        await _repository.TouchCredentialUsageAsync(application.Id, active.Id, usedAt);
        await _repository.TouchCredentialUsageAsync(application.Id, expired.Id, usedAt);
        var credentials = await _repository.GetApplicationCredentialsAsync(application.Id);
        Assert.Equal(usedAt, Assert.Single(credentials, value => value.Id == active.Id).LastUsedAt);
        Assert.Null(Assert.Single(credentials, value => value.Id == expired.Id).LastUsedAt);
        Assert.Equal(usedAt, (await _repository.GetApplicationAsync(application.Id))!.LastUsedAt);

        Assert.True(await _repository.RevokeCredentialAsync(application.Id, active.Id, _now.AddMinutes(6)));
        Assert.Null(await _repository.FindApplicationCredentialAsync("active-hash", _now.AddMinutes(7)));
        Assert.False(await _repository.RevokeCredentialAsync(application.Id, active.Id, _now.AddMinutes(8)));

        application.IsEnabled = false;
        application.UpdatedAt = _now.AddMinutes(9);
        await _repository.UpdateApplicationAsync(application);
        Assert.Null(await _repository.FindApplicationCredentialAsync("disabled-hash", _now.AddMinutes(10)));
        var another = NewCredential(application.Id, "another", "another-hash", _now.AddHours(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.InsertCredentialAsync(another));
        Assert.Null(await _repository.FindApplicationCredentialAsync("another-hash", _now.AddMinutes(10)));
    }

    [Fact]
    public async Task MultipleCredentials_RemainIndependentAndDeleteCascadesLifecycleRows()
    {
        var application = NewApplication("Multiple clients");
        var permissions = Set(ApplicationPermissionIds.LibraryRead);
        await _repository.InsertApplicationAsync(application, permissions);
        var first = NewCredential(application.Id, "living room", "first-hash", null);
        var second = NewCredential(application.Id, "mobile", "second-hash", null);
        await _repository.InsertCredentialAsync(first);
        await _repository.InsertCredentialAsync(second);

        var credentials = await _repository.GetApplicationCredentialsAsync(application.Id);
        Assert.Equal(2, credentials.Count);
        Assert.NotNull(await _repository.FindApplicationCredentialAsync("first-hash", _now));
        Assert.NotNull(await _repository.FindApplicationCredentialAsync("second-hash", _now));

        Assert.True(await _repository.RevokeCredentialAsync(application.Id, first.Id, _now.AddMinutes(1)));
        Assert.Null(await _repository.FindApplicationCredentialAsync("first-hash", _now.AddMinutes(2)));
        Assert.NotNull(await _repository.FindApplicationCredentialAsync("second-hash", _now.AddMinutes(2)));

        Assert.True(await _repository.DeleteApplicationAsync(application.Id, _now.AddMinutes(3)));
        Assert.Empty(await _repository.GetApplicationCredentialsAsync(application.Id));
        Assert.Empty(await _repository.GetApplicationPermissionsAsync(application.Id));
    }

    [Fact]
    public async Task ConcurrentRotation_HasOneWinnerAndDoesNotChangePermissions()
    {
        var application = NewApplication("Atomic rotation");
        var permissions = Set(
            ApplicationPermissionIds.LibraryRead,
            ApplicationPermissionIds.MetadataRead);
        await _repository.InsertApplicationAsync(application, permissions);
        var prior = NewCredential(application.Id, "primary", "prior-hash", null);
        await _repository.InsertCredentialAsync(prior);
        var versionBeforeRotation = (await _repository.GetApplicationAsync(application.Id))!.AuthorizationVersion;

        var replacements = Enumerable.Range(0, 8)
            .Select(index => NewCredential(application.Id, $"replacement-{index}", $"replacement-hash-{index}", null))
            .ToArray();
        var outcomes = await Task.WhenAll(replacements.Select(replacement =>
            _repository.RotateCredentialAsync(
                application.Id,
                prior.Id,
                replacement,
                _now.AddMinutes(1))));

        Assert.Single(outcomes, value => value);
        var stored = await _repository.GetApplicationCredentialsAsync(application.Id);
        Assert.Equal(2, stored.Count);
        Assert.NotNull(Assert.Single(stored, value => value.Id == prior.Id).RevokedAt);
        Assert.Single(stored, value => value.Id != prior.Id);
        Assert.Equal(permissions, await _repository.GetApplicationPermissionsAsync(application.Id));
        Assert.Equal(
            versionBeforeRotation + 1,
            (await _repository.GetApplicationAsync(application.Id))!.AuthorizationVersion);
    }

    [Fact]
    public async Task ClientBindings_ResolveOnlyEnabledUserClientsAndReplaceAtomically()
    {
        var first = NewApplication("Native client");
        first.ApplicationType = ApplicationType.UserClient;
        var second = NewApplication("Other native client");
        second.ApplicationType = ApplicationType.UserClient;
        await _repository.InsertApplicationAsync(first, Set(ApplicationPermissionIds.LibraryRead));
        await _repository.InsertApplicationAsync(second, Set(ApplicationPermissionIds.LibraryRead));

        await _repository.ReplaceClientBindingsAsync(
            first.Id,
            new HashSet<string>(StringComparer.Ordinal) { "living-room", "tablet" },
            _now.AddMinutes(1));
        Assert.True((await _repository.GetApplicationClientBindingsAsync(first.Id))
            .SetEquals(["living-room", "tablet"]));
        Assert.Equal(first.Id, (await _repository.GetApplicationByClientIdAsync("living-room"))!.Id);
        Assert.Null(await _repository.GetApplicationByClientIdAsync("Living-Room"));
        Assert.Null(await _repository.GetApplicationByClientIdAsync(""));
        await _repository.ReplaceClientBindingsAsync(
            second.Id,
            new HashSet<string>(StringComparer.Ordinal) { "second-original" },
            _now.AddMinutes(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReplaceClientBindingsAsync(
                second.Id,
                new HashSet<string>(StringComparer.Ordinal) { "living-room" },
                _now.AddMinutes(3)));
        Assert.Equal(first.Id, (await _repository.GetApplicationByClientIdAsync("tablet"))!.Id);
        Assert.Equal(second.Id, (await _repository.GetApplicationByClientIdAsync("second-original"))!.Id);

        await _repository.ReplaceClientBindingsAsync(
            first.Id,
            new HashSet<string>(StringComparer.Ordinal) { "television" },
            _now.AddMinutes(4));
        Assert.Null(await _repository.GetApplicationByClientIdAsync("living-room"));
        Assert.Equal(first.Id, (await _repository.GetApplicationByClientIdAsync("television"))!.Id);

        first.IsEnabled = false;
        first.UpdatedAt = _now.AddMinutes(5);
        await _repository.UpdateApplicationAsync(first);
        Assert.Null(await _repository.GetApplicationByClientIdAsync("television"));

        var integration = NewApplication("Server integration");
        await _repository.InsertApplicationAsync(integration, Set(ApplicationPermissionIds.LibraryRead));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReplaceClientBindingsAsync(
                integration.Id,
                new HashSet<string>(StringComparer.Ordinal) { "not-a-user-client" },
                _now.AddMinutes(6)));
    }

    private Application NewApplication(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ApplicationType = ApplicationType.ServerIntegration,
        IsEnabled = true,
        AuthorizationVersion = 1,
        CreatedAt = _now,
        UpdatedAt = _now,
    };

    private ApplicationCredential NewCredential(
        Guid applicationId,
        string name,
        string hash,
        DateTimeOffset? expiresAt) => new()
        {
            Id = Guid.NewGuid(),
            ApplicationId = applicationId,
            Name = name,
            CredentialHash = hash,
            HashScheme = "sha256",
            CreatedAt = _now,
            ExpiresAt = expiresAt,
        };

    private static IReadOnlySet<ApplicationPermissionId> Set(params ApplicationPermissionId[] permissions) =>
        permissions.ToHashSet();
}
