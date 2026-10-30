using MediaEngine.Api.Endpoints;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

/// <summary>People outside your household start with View off (D16); an administrator switches it on per person.</summary>
public sealed class OutsideInvitationViewDefaultTests
{
    [Fact]
    public async Task AnOutsidePersonNeverReachesView()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tuvima-view-default-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        DatabaseConnection? database = null;
        try
        {
            database = new DatabaseConnection(Path.Combine(root, "accounts.db"));
            database.InitializeSchema();
            var accounts = new AccountRepository(database);
            var now = DateTimeOffset.UtcNow;
            var profileId = Guid.NewGuid();
            var account = new Account
            {
                Id = Guid.NewGuid(),
                Email = "visitor@example.test",
                NormalizedEmail = "VISITOR@EXAMPLE.TEST",
                IsEnabled = true,
                AuthorizationVersion = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var grant = new TestGrant(account.Id, profileId, IsDefault: true, AdminEnabled: false);
            var profile = new Profile
            {
                Id = profileId,
                DisplayName = "Visitor",
                Role = ProfileRole.StandardUser,
                CreatedAt = now,
            };

            await accounts.CreateAccountAsync(account, grant, newProfile: profile);
            var access = new ProfileAccessRepository(database);
            // Someone outside the server's own household never reaches View (and no switch can give it to them); Read, Watch and Listen are open.
            var areas = await access.GetAreasAsync(profileId);
            Assert.DoesNotContain(AccountFeatureId.View, areas);
            Assert.Contains(AccountFeatureId.Watch, areas);
            TestTemp.ReleaseSqlitePoolsIn(root);
        }
        finally
        {
            database?.Dispose();
            TestTemp.ReleaseSqlitePoolsIn(root);
            Directory.Delete(root, recursive: true);
        }
    }
}
