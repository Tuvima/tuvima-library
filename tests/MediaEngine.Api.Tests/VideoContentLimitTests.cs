using MediaEngine.Api.Security;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>
/// "Suitable for limited profiles" on the folder (simpler access D7): an item with no rating is let through when its file is in a
/// marked folder; a rated item always follows the profile's limit. Saved videos have no ratings, so they follow the same rule.
/// </summary>
public sealed class VideoContentLimitTests
{
    private const string Library = "77777777-7777-4777-8777-777777777777";
    private const string VaultLibrary = "88888888-8888-4888-8888-888888888888";

    private static SuitableFolder Kids(string library = Library) => new(library, FolderFirstCheckPaths.Prefix("/media/kids"));

    [Fact]
    public void AnUnratedItem_InAMarkedFolder_IsAllowedForALimitedProfile_WhateverAllowUnratedSays()
    {
        var limited = new ContentLimitFilter(new ProfileContentLimit("PG", AllowUnrated: false), [Kids()]);

        Assert.True(limited.AllowsItem(null, "/media/kids/a.mkv"));
        Assert.True(limited.AllowsItem("Not Rated", "/media/kids/sub/a.mkv"));
        Assert.False(limited.AllowsItem(null, "/media/adults/a.mkv"));
        Assert.False(limited.AllowsItem(null, null));
    }

    [Fact]
    public void ARatedItem_FollowsTheLimit_EvenInAMarkedFolder()
    {
        var limited = new ContentLimitFilter(new ProfileContentLimit("PG", AllowUnrated: false), [Kids()]);

        Assert.False(limited.AllowsItem("R", "/media/kids/a.mkv"));
        Assert.True(limited.AllowsItem("G", "/media/adults/a.mkv"));
        Assert.True(limited.AllowsItem("PG", "/media/kids/a.mkv"));
    }

    [Fact]
    public void AnUnratedItem_OutsideAMarkedFolder_FollowsAllowUnrated()
    {
        var strict = new ContentLimitFilter(new ProfileContentLimit("PG", AllowUnrated: false));
        var lenient = new ContentLimitFilter(new ProfileContentLimit("PG", AllowUnrated: true));

        Assert.False(strict.AllowsItem(null, "/media/adults/a.mkv"));
        Assert.True(lenient.AllowsItem(null, "/media/adults/a.mkv"));
    }

    [Fact]
    public void ASiblingFolderSharingAPrefix_IsNotInsideTheMarkedFolder()
    {
        var limited = new ContentLimitFilter(ProfileContentLimit.Strictest, [Kids()]);

        Assert.False(limited.AllowsItem(null, "/media/kids2/a.mkv"));
        Assert.False(limited.AllowsItem(null, "/media/kid/a.mkv"));
        Assert.True(limited.AllowsItem(null, "/media/kids/a.mkv"));
    }

    [Fact]
    public void AMarkedFolderInAVaultLibrary_IsNeverHonoured()
    {
        var vault = new VaultVisibility(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { VaultLibrary }, unlocked: true);
        var limited = new ContentLimitFilter(ProfileContentLimit.Strictest, [Kids(VaultLibrary), Kids()]).WithVault(vault);
        var vaultOnly = new ContentLimitFilter(ProfileContentLimit.Strictest, [Kids(VaultLibrary)]).WithVault(vault);

        Assert.Single(limited.SuitableFolderPrefixes);
        Assert.Empty(vaultOnly.SuitableFolderPrefixes);
        Assert.False(vaultOnly.AllowsItem(null, "/media/kids/a.mkv"));
    }

    [Fact]
    public void AnUnrestrictedProfile_IsNotAffected()
    {
        Assert.True(ContentLimitFilter.Unrestricted.AllowsItem(null, "/media/anywhere/a.mkv"));
        Assert.True(ContentLimitFilter.Unrestricted.AllowsItem("R", null));
        Assert.Empty(ContentLimitFilter.Unrestricted.SuitableFolderPrefixes);
    }
}
