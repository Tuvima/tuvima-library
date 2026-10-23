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
    private static readonly string[] Everything = ["read", "watch", "listen", "view"];

    [Fact]
    public void ANewHouseholdStartsWithViewOff_ExistingProfilesKeepWhatWasChosen()
    {
        var outside = AccountEndpoints.FeaturesForNewAccount(Everything, startsNewHousehold: true);
        Assert.DoesNotContain(AccountFeatureId.View, outside);
        Assert.Contains(AccountFeatureId.Read, outside);
        Assert.Contains(AccountFeatureId.Watch, outside);
        Assert.Contains(AccountFeatureId.Listen, outside);

        var inside = AccountEndpoints.FeaturesForNewAccount(Everything, startsNewHousehold: false);
        Assert.Contains(AccountFeatureId.View, inside);
    }

    [Fact]
    public async Task OutsidePersonCannotUseViewUntilAnAdministratorSwitchesItOn()
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
            var grant = new AccountProfileGrant
            {
                AccountId = account.Id,
                ProfileId = profileId,
                IsDefault = true,
                IsEnabled = true,
                AuthorizationVersion = 1,
                GrantedAt = now,
            };
            var profile = new Profile
            {
                Id = profileId,
                DisplayName = "Visitor",
                Role = ProfileRole.StandardUser,
                CreatedAt = now,
            };

            await accounts.CreateAccountAsync(account, grant,
                AccountEndpoints.FeaturesForNewAccount(Everything, startsNewHousehold: true),
                new HashSet<Guid>(), newProfile: profile);
            var access = new ProfileAccessRepository(database);
            Assert.DoesNotContain(AccountFeatureId.View, await access.GetFeatureGrantsAsync(profileId));

            var features = (await access.GetFeatureGrantsAsync(profileId)).Append(AccountFeatureId.View).ToHashSet();
            await access.ReplaceAccessAsync(profileId, features, new HashSet<Guid>(), DateTimeOffset.UtcNow);

            Assert.Contains(AccountFeatureId.View, await access.GetFeatureGrantsAsync(profileId));
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
