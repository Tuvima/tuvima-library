using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Watch;

namespace MediaEngine.Web.Tests;

public sealed class OwnedEpisodeQueuePlannerTests
{
    [Fact]
    public void NextUp_ContainsOnlyLaterOwnedEpisodesInSequenceOrder()
    {
        var previous = Guid.NewGuid();
        var current = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var next = Guid.NewGuid();
        var following = Guid.NewGuid();
        var sequence = new SequencePlacementViewModel
        {
            OrderedItems =
            [
                Episode(previous, owned: true),
                Episode(current, owned: true),
                Episode(missing, owned: false),
                Episode(next, owned: true),
                new SequenceItemViewModel { Id = Guid.NewGuid().ToString("D"), EntityType = DetailEntityType.Movie, IsOwned = true },
                Episode(following, owned: true),
            ],
        };

        Assert.Equal([next, following], OwnedEpisodeQueuePlanner.NextPlayableCandidates(sequence, current));
        Assert.Empty(OwnedEpisodeQueuePlanner.NextPlayableCandidates(sequence, Guid.NewGuid()));
    }

    private static SequenceItemViewModel Episode(Guid id, bool owned) => new()
    {
        Id = id.ToString("D"),
        EntityType = DetailEntityType.TvEpisode,
        IsOwned = owned,
    };
}
