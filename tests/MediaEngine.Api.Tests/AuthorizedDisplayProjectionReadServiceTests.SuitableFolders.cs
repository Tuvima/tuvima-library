using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

// "Suitable for limited profiles" on the folder (simpler access D7): an item with no rating is let through for a profile with a
// rating limit when its file sits in a marked folder; a rated item always follows the limit.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private const string KidsFolder = "C:/library/kids";
    private const string OtherFolder = "C:/library/other";

    private static LibrariesOnlyBrowseLoader FolderConfig(Guid library, bool kidsTicked = true, bool otherTicked = false) => new(new LibraryFolderConfig
    {
        Id = library.ToString("D"),
        Name = "Shared",
        KindKey = "audiobooks",
        Sources =
        [
            new LibrarySourceConfig { Id = "kids", Path = KidsFolder, OkForLimitedProfiles = kidsTicked },
            new LibrarySourceConfig { Id = "other", Path = OtherFolder, OkForLimitedProfiles = otherTicked },
        ],
    });

    // One work with one file at <folder>/<name>, rated or not.
    private async Task<(Guid WorkId, Guid AssetId)> InsertFolderWorkAsync(Guid library, string folder, string mediaType, string? rating)
    {
        var owned = await InsertOwnedWorkWithIdAsync(library, $"{mediaType} in {folder}", mediaType);
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE media_assets SET file_path_root=@path WHERE id=@asset;",
            new { path = $"{folder}/{owned.AssetId:N}.m4b", asset = owned.AssetId });
        if (rating is not null)
        {
            await connection.ExecuteAsync(
                "INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES(@work,'content_rating',@rating,CURRENT_TIMESTAMP);",
                new { work = owned.WorkId, rating });
        }

        return owned;
    }

    private async Task<DefaultHttpContext> LimitedContextAsync(string? limit, bool allowUnrated)
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>());
        SetContentLimit(limit, allowUnrated);
        return HumanContext(account, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
    }

    private async Task<IReadOnlySet<Guid>> VisibleWorksAsync(DefaultHttpContext context, LibrariesOnlyBrowseLoader config)
    {
        var service = CreateService(
            context,
            new ReaderBackedProjection(new DisplayWorkProjectionReader(_database), new DisplayVisibleWorkReader(_database)),
            config);
        var listed = (await service.LoadWorksAsync(CancellationToken.None)).Select(row => row.WorkId).ToHashSet();
        var counted = await service.LoadVisibleWorkIdsAsync(CancellationToken.None);
        // Counts are taken from the same rule as the list, so the two never disagree.
        Assert.Equal(listed.Order(), counted.Order());
        return listed;
    }

    [Fact]
    public async Task APgProfileWithAllowUnratedOff_SeesAnUnratedAudiobookFromATickedFolder_AndNotFromAnUntickedOneInTheSameLibrary()
    {
        var library = Guid.NewGuid();
        var ticked = await InsertFolderWorkAsync(library, KidsFolder, "Audiobooks", null);
        var unticked = await InsertFolderWorkAsync(library, OtherFolder, "Audiobooks", null);
        var context = await LimitedContextAsync("PG", allowUnrated: false);

        var visible = await VisibleWorksAsync(context, FolderConfig(library));

        Assert.Contains(ticked.WorkId, visible);
        Assert.DoesNotContain(unticked.WorkId, visible);
    }

    [Fact]
    public async Task AnRFilmInATickedFolder_StaysHiddenFromAPgProfile_WhileAPgFilmThereShows()
    {
        var library = Guid.NewGuid();
        var adult = await InsertFolderWorkAsync(library, KidsFolder, "Movies", "R");
        var family = await InsertFolderWorkAsync(library, KidsFolder, "Movies", "PG");
        var context = await LimitedContextAsync("PG", allowUnrated: false);

        var visible = await VisibleWorksAsync(context, FolderConfig(library));

        Assert.DoesNotContain(adult.WorkId, visible);
        Assert.Contains(family.WorkId, visible);
    }

    [Fact]
    public async Task UnratedSavedVideosFromATickedVideosFolderShowForALimitedProfile_AndFromAnUntickedOneOnlyWhenAllowUnratedIsOn()
    {
        var library = Guid.NewGuid();
        var ticked = await InsertFolderWorkAsync(library, KidsFolder, "Videos", null);
        var unticked = await InsertFolderWorkAsync(library, OtherFolder, "Videos", null);

        var context = await LimitedContextAsync("PG", allowUnrated: false);

        var strict = await VisibleWorksAsync(context, FolderConfig(library));
        Assert.Contains(ticked.WorkId, strict);
        Assert.DoesNotContain(unticked.WorkId, strict);

        SetContentLimit("PG", allowUnrated: true);
        var lenient = await VisibleWorksAsync(context, FolderConfig(library));
        Assert.Contains(ticked.WorkId, lenient);
        Assert.Contains(unticked.WorkId, lenient);
    }

    [Fact]
    public async Task ListsCountsAndAuthorizedAssets_AgreeForALimitedProfileWithOneTickedFolder()
    {
        var library = Guid.NewGuid();
        var tickedUnrated = await InsertFolderWorkAsync(library, KidsFolder, "Audiobooks", null);
        var tickedRated = await InsertFolderWorkAsync(library, KidsFolder, "Audiobooks", "G");
        var untickedUnrated = await InsertFolderWorkAsync(library, OtherFolder, "Audiobooks", null);
        var tickedAdult = await InsertFolderWorkAsync(library, KidsFolder, "Audiobooks", "R");
        var context = await LimitedContextAsync("PG", allowUnrated: false);
        var config = FolderConfig(library);

        var visible = await VisibleWorksAsync(context, config);
        var service = CreateService(context, new StubRawProjection([], []), config);
        var assets = (await service.LoadAuthorizedAssetsAsync(CancellationToken.None)).Select(row => row.AssetId).ToHashSet();

        Assert.Equivalent(new[] { tickedUnrated.WorkId, tickedRated.WorkId }, visible);
        Assert.Equivalent(new[] { tickedUnrated.AssetId, tickedRated.AssetId }, assets);
        Assert.DoesNotContain(untickedUnrated.AssetId, assets);
        Assert.DoesNotContain(tickedAdult.AssetId, assets);
    }

    [Fact]
    public async Task StreamAndDetailAuthorization_FollowTheSameFolderRule()
    {
        var library = Guid.NewGuid();
        var ticked = await InsertFolderWorkAsync(library, KidsFolder, "Audiobooks", null);
        var unticked = await InsertFolderWorkAsync(library, OtherFolder, "Audiobooks", null);
        var adult = await InsertFolderWorkAsync(library, KidsFolder, "Audiobooks", "R");
        var context = await LimitedContextAsync("PG", allowUnrated: false);
        var resources = CreateResourceService(context, FolderConfig(library));

        Assert.Equal(CatalogueResourceAccess.Allowed, await resources.EvaluateAssetAsync(context, ticked.AssetId, ApplicationPermissionIds.PlaybackRead));
        Assert.Equal(CatalogueResourceAccess.Denied, await resources.EvaluateAssetAsync(context, unticked.AssetId, ApplicationPermissionIds.PlaybackRead));
        Assert.Equal(CatalogueResourceAccess.Denied, await resources.EvaluateAssetAsync(context, adult.AssetId, ApplicationPermissionIds.PlaybackRead));
        Assert.Equal([ticked.AssetId], await resources.GetAuthorizedAssetIdsForWorkAsync(
            context, ticked.WorkId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId, ApplicationPermissionIds.LibraryRead));
        Assert.Empty(await resources.GetAuthorizedAssetIdsForWorkAsync(
            context, unticked.WorkId, MediaEngine.Domain.Aggregates.Profile.SeedProfileId, ApplicationPermissionIds.LibraryRead));
    }

    [Fact]
    public async Task AnUnrestrictedProfile_SeesEverythingWhetherOrNotAnyFolderIsTicked()
    {
        var library = Guid.NewGuid();
        var ticked = await InsertFolderWorkAsync(library, KidsFolder, "Audiobooks", null);
        var unticked = await InsertFolderWorkAsync(library, OtherFolder, "Audiobooks", null);
        var adult = await InsertFolderWorkAsync(library, OtherFolder, "Audiobooks", "R");
        var context = await LimitedContextAsync(null, allowUnrated: false);

        var withTick = await VisibleWorksAsync(context, FolderConfig(library));
        var withoutTick = await VisibleWorksAsync(context, FolderConfig(library, kidsTicked: false));

        Assert.Equivalent(new[] { ticked.WorkId, unticked.WorkId, adult.WorkId }, withTick);
        Assert.Equivalent(new[] { ticked.WorkId, unticked.WorkId, adult.WorkId }, withoutTick);
    }

    [Fact]
    public async Task AFileInASiblingFolderThatSharesAPrefix_IsNotTreatedAsInsideTheTickedFolder()
    {
        var library = Guid.NewGuid();
        var sibling = await InsertFolderWorkAsync(library, KidsFolder + "2", "Audiobooks", null);
        var inside = await InsertFolderWorkAsync(library, KidsFolder + "/nested", "Audiobooks", null);
        var context = await LimitedContextAsync("PG", allowUnrated: false);

        var visible = await VisibleWorksAsync(context, FolderConfig(library));

        Assert.DoesNotContain(sibling.WorkId, visible);
        Assert.Contains(inside.WorkId, visible);
    }
}
