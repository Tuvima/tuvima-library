using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Tests;

public sealed class PersonCreationLocksTests
{
    [Fact]
    public void ForQid_SamePerson_GivesTheSameLockWhateverTheCase()
    {
        Assert.Same(PersonCreationLocks.ForQid("Q42"), PersonCreationLocks.ForQid("q42"));
    }

    [Fact]
    public void ForQid_DifferentPeople_GetDifferentLocks()
    {
        Assert.NotSame(PersonCreationLocks.ForQid("Q42"), PersonCreationLocks.ForQid("Q43"));
    }

    [Fact]
    public async Task ForQid_WhileOneCreatorHoldsIt_AnotherWaitsUntilItIsReleased()
    {
        var gate = PersonCreationLocks.ForQid("Q-lock-test");
        await gate.WaitAsync();

        var second = PersonCreationLocks.ForQid("Q-lock-test").WaitAsync();
        Assert.False(second.IsCompleted);

        gate.Release();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release();
    }
}
