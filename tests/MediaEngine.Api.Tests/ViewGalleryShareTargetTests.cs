using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Tests;

public sealed class ViewGalleryShareTargetTests
{
    [Fact]
    public async Task DiscoveryRequiresCallerGallerySharingPolicy()
    {
        var callerId = Guid.NewGuid();
        var target = Entry(Guid.NewGuid(), "Sarah", viewEnabled: true, hasPersonalSpace: true);
        var policies = new ProfileRepository(ViewProfilePolicy.Default(callerId) with
        {
            ViewEnabled = true,
            ShareGalleries = false,
        });

        var result = await ViewEndpoints.GetGalleryShareTargetsAsync(
            callerId, policies, new ScopeStore(target));

        Assert.Null(result);
    }

    [Fact]
    public async Task DiscoveryReturnsOnlyEnabledRecipientsWithoutMediaFacts()
    {
        var callerId = Guid.NewGuid();
        var enabledId = Guid.NewGuid();
        var policies = new ProfileRepository(ViewProfilePolicy.Default(callerId) with
        {
            ViewEnabled = true,
            ShareGalleries = true,
        });
        var scopes = new ScopeStore(
            Entry(callerId, "Owner", viewEnabled: true, hasPersonalSpace: true),
            Entry(enabledId, "Sarah", viewEnabled: true, hasPersonalSpace: true),
            Entry(Guid.NewGuid(), "Disabled", viewEnabled: false, hasPersonalSpace: true),
            Entry(Guid.NewGuid(), "No space", viewEnabled: true, hasPersonalSpace: false));

        var targets = Assert.IsAssignableFrom<IReadOnlyList<ViewGalleryShareTargetDto>>(
            await ViewEndpoints.GetGalleryShareTargetsAsync(callerId, policies, scopes));

        var target = Assert.Single(targets);
        Assert.Equal(enabledId, target.ProfileId);
        Assert.Equal("Sarah", target.DisplayName);
        Assert.Equal(
            ["AvatarColor", "AvatarUrl", "DisplayName", "ProfileId"],
            typeof(ViewGalleryShareTargetDto).GetProperties().Select(property => property.Name).Order().ToArray());
    }

    [Fact]
    public void ReplacementAcceptsEligibleRecipientsAndRejectsForgedOrDuplicateProfiles()
    {
        var callerId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        ViewGalleryShareTargetDto[] targets = [new(targetId, "Sarah", "#7457D9", null)];

        Assert.True(ViewEndpoints.TryValidateGalleryShares(
            [new(targetId, ViewGallerySharePermission.Contribute)], callerId, targets, out var shares));
        Assert.Equal((targetId, ViewGallerySharePermission.Contribute), Assert.Single(shares));

        Assert.False(ViewEndpoints.TryValidateGalleryShares(
            [new(callerId, ViewGallerySharePermission.View)], callerId, targets, out _));
        Assert.False(ViewEndpoints.TryValidateGalleryShares(
            [new(Guid.NewGuid(), ViewGallerySharePermission.View)], callerId, targets, out _));
        Assert.False(ViewEndpoints.TryValidateGalleryShares(
            [new(targetId, ViewGallerySharePermission.View), new(targetId, ViewGallerySharePermission.Contribute)],
            callerId, targets, out _));
        Assert.False(ViewEndpoints.TryValidateGalleryShares(
            [new(targetId, (ViewGallerySharePermission)99)], callerId, targets, out _));
    }

    private static ViewScopeStoreEntry Entry(
        Guid profileId,
        string displayName,
        bool viewEnabled,
        bool hasPersonalSpace)
    {
        var now = DateTimeOffset.UtcNow;
        return new ViewScopeStoreEntry(
            new ViewProfilePolicy(profileId, viewEnabled, false, false, false, now),
            hasPersonalSpace ? new ViewPersonalSpace(Guid.NewGuid(), profileId, Guid.NewGuid(), now, now) : null,
            displayName,
            "#7457D9");
    }

    private sealed class ScopeStore(params ViewScopeStoreEntry[] entries) : IViewScopeStore
    {
        public Task<ViewScopeStoreEntry?> FindProfileAsync(Guid profileId, CancellationToken ct = default) =>
            Task.FromResult(entries.FirstOrDefault(entry => entry.Policy.ProfileId == profileId));

        public Task<IReadOnlyList<ViewScopeStoreEntry>> GetProfilesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ViewScopeStoreEntry>>(entries);
    }

    private sealed class ProfileRepository(ViewProfilePolicy policy) : IViewProfileRepository
    {
        public Task<ViewProfilePolicy> GetPolicyAsync(Guid profileId, CancellationToken ct = default) =>
            Task.FromResult(policy);

        public Task<bool> SavePolicyAsync(ViewProfilePolicy value, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ViewProfilePreferences> GetPreferencesAsync(Guid profileId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> SavePreferencesAsync(ViewProfilePreferences preferences, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
