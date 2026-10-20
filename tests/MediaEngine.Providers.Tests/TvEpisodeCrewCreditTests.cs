using System.Text.Json.Nodes;
using MediaEngine.Domain;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Workers;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// TMDB lists an episode's crew member once per job. A writer credited for "Writer" and "Story" is one screenwriter
/// credit, so the same person is not stored twice for one role.
/// </summary>
public sealed class TvEpisodeCrewCreditTests
{
    [Fact]
    public void EpisodeCrew_PersonListedForTwoJobsOfOneRole_IsOneCredit()
    {
        var episode = JsonNode.Parse("""
            {
              "crew": [
                { "id": 4500, "name": "Vince Gilligan", "job": "Writer", "department": "Writing" },
                { "id": 66633, "name": "Adam Bernstein", "job": "Director", "department": "Directing" },
                { "id": 4500, "name": "Vince Gilligan", "job": "Story", "department": "Writing" }
              ]
            }
            """)!;
        var claims = new List<ProviderClaim>();

        RetailMatchWorker.AddTvEpisodeCrewClaims(claims, episode);

        Assert.Equal(["Vince Gilligan"], claims.Where(c => c.Key == MetadataFieldConstants.Screenwriter).Select(c => c.Value));
        Assert.Equal(["4500"], claims.Where(c => c.Key == "screenwriter_tmdb_id").Select(c => c.Value));
        Assert.Equal(["Adam Bernstein"], claims.Where(c => c.Key == MetadataFieldConstants.Director).Select(c => c.Value));
    }
}
