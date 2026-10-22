using System.Globalization;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.View;

/// <summary>
/// What is left to clean up on disk once the person has been removed: their personal files and their profile picture
/// (<paramref name="AvatarPath"/>, deleted only when it lives in the Library's own avatar folder).
/// </summary>
public sealed record ProfilePhotoPlan(IReadOnlyList<ProfilePersonalFile> FilesToDelete, string? AvatarPath = null);

/// <summary>Looks after a removed person's personal photos: keeps them in the Shared Library or deletes them.</summary>
public interface IProfilePhotoDisposer
{
    /// <summary>
    /// Runs before the person is removed. When photos are kept, each one moves into the household's Shared Library folder
    /// "From &lt;name&gt; &lt;date&gt;" (linked originals are copied, never moved). Throws if any photo cannot be kept,
    /// so the person is not removed and nothing is lost.
    /// </summary>
    Task<ProfilePhotoPlan> PrepareAsync(
        Profile profile, ProfilePhotoDisposition photos, Guid actorProfileId, CancellationToken ct = default);

    /// <summary>Runs after the person is removed: deletes the files they no longer have anything pointing at, and their profile picture.</summary>
    Task CompleteAsync(ProfilePhotoPlan plan, CancellationToken ct = default);
}

public sealed class ProfilePhotoDisposer(
    IProfilePersonalMediaRepository media,
    ViewSharedTransferService transfers,
    ViewStorageService storage,
    TuvimaDataPaths dataPaths,
    TimeProvider clock,
    ILogger<ProfilePhotoDisposer> logger) : IProfilePhotoDisposer
{
    public async Task<ProfilePhotoPlan> PrepareAsync(
        Profile profile, ProfilePhotoDisposition photos, Guid actorProfileId, CancellationToken ct = default)
    {
        if (photos == ProfilePhotoDisposition.MoveToShared)
        {
            var items = await media.GetPersonalItemIdsAsync(profile.Id, ct).ConfigureAwait(false);
            if (items.Count > 0)
            {
                var now = clock.GetUtcNow();
                var label = SharedLabel(profile.DisplayName, now);
                await media.AddTagAsync(profile.Id, label, now, ct).ConfigureAwait(false);
                foreach (var item in items)
                {
                    await transfers.ExecuteAsync(item, actorProfileId, "folder", label, null, ct).ConfigureAwait(false);
                }
            }
        }

        // Whatever is still on disk now (trashed items, or originals the person chose to delete) is removed after the person is.
        // The share records and file locations are released inside the profile delete itself, so a failed delete leaves them intact.
        var remaining = (await media.GetPersonalFilesAsync(profile.Id, ct).ConfigureAwait(false))
            .Where(file => file.IsManaged)
            .ToList();
        return new ProfilePhotoPlan(remaining, profile.AvatarImagePath);
    }

    public async Task CompleteAsync(ProfilePhotoPlan plan, CancellationToken ct = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storage.GetRootPath())) + Path.DirectorySeparatorChar;
        foreach (var file in plan.FilesToDelete)
        {
            var path = Path.GetFullPath(file.FilePath);
            // Only Tuvima's own managed copies are ever deleted; anything outside its folder is left alone.
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The person is already gone; a file that cannot be deleted right now is left for the next library cleanup.
                logger.LogWarning(exception, "Could not delete a removed profile's photo file {Path}.", path);
            }
        }

        await media.DeleteUnusedFilesAsync(plan.FilesToDelete.Select(file => file.FileId).Distinct().ToList(), ct)
            .ConfigureAwait(false);
        DeleteAvatar(plan.AvatarPath, AvatarFolder(dataPaths), logger);
    }

    /// <summary>The Library's own avatar folder: every uploaded profile picture lives in <c>{data folder}/profiles/{profile id}/</c>.</summary>
    internal static string AvatarFolder(TuvimaDataPaths dataPaths) => Path.Combine(dataPaths.Root, "profiles");

    /// <summary>
    /// Deletes a removed person's uploaded profile picture, and its per-person folder once empty. Only a file inside
    /// <paramref name="avatarFolder"/> is ever deleted; a path anywhere else is left alone. A picture that cannot be deleted is
    /// logged as a warning: the person is already gone.
    /// </summary>
    internal static void DeleteAvatar(string? avatarPath, string avatarFolder, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(avatarPath))
        {
            return;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(avatarFolder)) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(avatarPath);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            File.Delete(path);
            var folder = Path.GetDirectoryName(path);
            if (folder is not null && !string.Equals(folder + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(exception, "Could not delete a removed profile's picture {Path}.", avatarPath);
        }
    }

    /// <summary>"From Alex 2026-10-10": the Shared Library folder and tag the kept photos carry.</summary>
    internal static string SharedLabel(string displayName, DateTimeOffset now)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(displayName.Where(c => Array.IndexOf(invalid, c) < 0).ToArray()).Trim().TrimEnd('.');
        var date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(name) ? $"From a removed profile {date}" : $"From {name} {date}";
    }
}
