using MediaEngine.Domain.Models;
using MediaEngine.Providers.Helpers;

namespace MediaEngine.Providers.Tests;

public sealed class PersonPassPlannerTests
{
    private static List<PersonReference> Actors(int count, string? qid = null) =>
        Enumerable.Range(1, count).Select(n => new PersonReference("Actor", $"Actor {n}", qid is null ? null : $"{qid}{n}")).ToList();

    [Fact]
    public void FirstPass_KeepsTheFirstBilledActors_AndDefersTheRest()
    {
        var linked = Actors(6, "Q");
        var unlinked = Actors(9);

        var plan = PersonPassPlanner.Plan(linked, unlinked, PersonPass.FirstBilled, actorLimit: 10);

        Assert.Equal(6, plan.Linked.Count);
        Assert.Equal(4, plan.Unlinked.Count);
        Assert.Equal(5, plan.DeferredCount);
    }

    [Fact]
    public void LaterPass_TakesExactlyWhatTheFirstPassLeft()
    {
        var linked = Actors(6, "Q");
        var unlinked = Actors(9);

        var first = PersonPassPlanner.Plan(linked, unlinked, PersonPass.FirstBilled, actorLimit: 10);
        var rest = PersonPassPlanner.Plan(linked, unlinked, PersonPass.Remainder, actorLimit: 10);

        Assert.Equal(15, first.Linked.Count + first.Unlinked.Count + rest.Linked.Count + rest.Unlinked.Count);
        Assert.Empty(first.Linked.Intersect(rest.Linked));
        Assert.Empty(first.Unlinked.Intersect(rest.Unlinked));
        Assert.Equal(0, rest.DeferredCount);
    }

    [Fact]
    public void NonActorCredits_AreNeverDeferred_AndNeverRepeatedInTheLaterPass()
    {
        var linked = Actors(12, "Q");
        linked.Add(new PersonReference("Director", "A Director", "Q900"));
        var unlinked = new List<PersonReference> { new("Writer", "A Writer"), new("Narrator", "A Narrator") };

        var first = PersonPassPlanner.Plan(linked, unlinked, PersonPass.FirstBilled, actorLimit: 10);
        var rest = PersonPassPlanner.Plan(linked, unlinked, PersonPass.Remainder, actorLimit: 10);

        Assert.Contains(first.Linked, r => r.Role == "Director");
        Assert.Equal(2, first.Unlinked.Count);
        Assert.All(rest.Linked, r => Assert.Equal("Actor", r.Role));
        Assert.Empty(rest.Unlinked);
        Assert.Equal(2, rest.Linked.Count);
    }

    [Fact]
    public void AllPass_AndSmallCasts_ChangeNothing()
    {
        var linked = Actors(20, "Q");
        var all = PersonPassPlanner.Plan(linked, [], PersonPass.All);
        Assert.Equal(20, all.Linked.Count);
        Assert.Equal(0, all.DeferredCount);

        var small = PersonPassPlanner.Plan(Actors(10, "Q"), [], PersonPass.FirstBilled, actorLimit: 10);
        Assert.Equal(10, small.Linked.Count);
        Assert.Equal(0, small.DeferredCount);
    }
}
