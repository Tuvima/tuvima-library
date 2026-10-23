using Dapper;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Tests;

// An item or series a person marked Explicit is hidden from every content-limited profile, whatever its rating says.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    [Fact]
    public async Task ExplicitItems_AreHiddenFromLimitedProfiles_AndSeenByUnrestrictedOnes()
    {
        var library = Guid.NewGuid();
        var (marked, _) = await InsertOwnedWorkWithIdAsync(library, "Marked", "Movies");
        var (plain, _) = await InsertOwnedWorkWithIdAsync(library, "Plain", "Movies");
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                "INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES(@marked,'content_rating','G',CURRENT_TIMESTAMP),(@plain,'content_rating','G',CURRENT_TIMESTAMP),(@marked,'explicit','true',CURRENT_TIMESTAMP);",
                new { marked, plain });
        }

        var limited = Guid.NewGuid();
        await CreateHumanAsync(limited, EveryFeature, new HashSet<Guid>());
        SetContentLimit("R", allowUnrated: true);
        var visibleToLimited = await AssertVisibleIdsMatchFullReadAsync(HumanContext(limited, ProfileOf(limited)));

        Assert.Contains(plain, visibleToLimited);
        Assert.DoesNotContain(marked, visibleToLimited);

        var owner = Guid.NewGuid();
        // A profile has at most one user, so the owner signs in as a person of their own.
        var ownerProfile = new MediaEngine.Domain.Aggregates.Profile
        {
            Id = Guid.NewGuid(),
            DisplayName = "Owner",
            Role = MediaEngine.Domain.Enums.ProfileRole.StandardUser,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await CreateHumanAsync(owner, EveryFeature, new HashSet<Guid>(), administrator: true, ownProfile: ownerProfile);
        SetContentLimit(null, allowUnrated: false);
        var visibleToOwner = await AssertVisibleIdsMatchFullReadAsync(HumanContext(owner, ownerProfile.Id));

        Assert.Contains(marked, visibleToOwner);
        Assert.Contains(plain, visibleToOwner);
    }
}
