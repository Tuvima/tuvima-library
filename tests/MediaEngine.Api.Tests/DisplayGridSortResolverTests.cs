using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Tests;

public sealed class DisplayGridSortResolverTests
{
    private static readonly Guid Profile = Guid.NewGuid();

    [Fact]
    public async Task RequestedSort_WinsOverTheRememberedOne()
    {
        var preferences = new PreferenceSpy(DisplayGridValues.RememberWatch, DisplayGridValues.SortTitle);

        var sort = await DisplayGridSortResolver.ResolveAsync("watch", "movies", DisplayGridValues.SortYear, Profile, preferences, CancellationToken.None);

        Assert.Equal(DisplayGridValues.SortYear, sort);
        Assert.Equal(0, preferences.Reads);
    }

    [Fact]
    public async Task NoRequestedSort_OpensWithTheLanesRememberedSort()
    {
        var preferences = new PreferenceSpy(DisplayGridValues.RememberWatch, DisplayGridValues.SortTitle);

        var sort = await DisplayGridSortResolver.ResolveAsync("watch", "movies", null, Profile, preferences, CancellationToken.None);

        Assert.Equal(DisplayGridValues.SortTitle, sort);
        Assert.Equal(Profile, preferences.LastProfile);
    }

    [Fact]
    public async Task NothingRemembered_LeavesTheGridDefault()
    {
        var preferences = new PreferenceSpy(DisplayGridValues.RememberRead, DisplayGridValues.SortTitle);

        var sort = await DisplayGridSortResolver.ResolveAsync("watch", "movies", null, Profile, preferences, CancellationToken.None);

        Assert.Null(sort);
    }

    [Fact]
    public async Task ContinueGrid_NeverUsesARememberedSort()
    {
        var preferences = new PreferenceSpy(DisplayGridValues.RememberWatch, DisplayGridValues.SortTitle);

        var sort = await DisplayGridSortResolver.ResolveAsync("watch", "continue-watching", null, Profile, preferences, CancellationToken.None);

        Assert.Null(sort);
        Assert.Equal(0, preferences.Reads);
    }

    [Fact]
    public async Task NoActiveProfile_DoesNotReadPreferences()
    {
        var preferences = new PreferenceSpy(DisplayGridValues.RememberWatch, DisplayGridValues.SortTitle);

        var sort = await DisplayGridSortResolver.ResolveAsync("watch", "movies", null, null, preferences, CancellationToken.None);

        Assert.Null(sort);
        Assert.Equal(0, preferences.Reads);
    }

    private sealed class PreferenceSpy(string key, string sort) : IProfileGridPreferenceRepository
    {
        public int Reads { get; private set; }

        public Guid? LastProfile { get; private set; }

        public Task<IReadOnlyDictionary<string, string>> GetSortsAsync(Guid profileId, CancellationToken ct)
        {
            Reads++;
            LastProfile = profileId;
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [key] = sort });
        }

        public Task SaveSortAsync(Guid profileId, string gridKey, string sort, CancellationToken ct) => Task.CompletedTask;
    }
}
