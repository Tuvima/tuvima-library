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
        await CreateHumanAsync(limited, EveryFeature, new HashSet<Guid> { library });
        SetContentLimit("R", allowUnrated: true);
        var visibleToLimited = await AssertVisibleIdsMatchFullReadAsync(HumanContext(limited, MediaEngine.Domain.Aggregates.Profile.SeedProfileId));

        Assert.Contains(plain, visibleToLimited);
        Assert.DoesNotContain(marked, visibleToLimited);

        var owner = Guid.NewGuid();
        await CreateHumanAsync(owner, EveryFeature, new HashSet<Guid>(), administrator: true);
        SetContentLimit(null, allowUnrated: false);
        var visibleToOwner = await AssertVisibleIdsMatchFullReadAsync(HumanContext(owner, MediaEngine.Domain.Aggregates.Profile.SeedProfileId));

        Assert.Contains(marked, visibleToOwner);
        Assert.Contains(plain, visibleToOwner);
    }
}
