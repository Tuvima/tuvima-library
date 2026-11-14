using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Components.Forms;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// "Set one cover for these 12 songs. It sets the album's cover." (universal editor spec 3c, WP9). Songs of one album share one picture
/// role, the album's cover, so the choice is staged once on the album, never once per song. The pictures offered are the album's own saved
/// ones plus whatever the person uploads. Like the single Artwork tool (universal editor v7 D9) a choice changes the album at once, through
/// the same edit session as Save changes, and nothing waits in Review; there is no Undo, because the earlier covers stay in the list and
/// choosing one again reverses the change.
/// </summary>
public sealed class BulkArtworkPicker
{
    /// <summary>The picture slot a music album's cover is staged under.</summary>
    public const string Slot = "CoverArt";

    private const string EntityKind = "Work";
    private const string MusicMediaType = "Music";
    private const long MaximumUploadBytes = 10 * 1024 * 1024;

    private readonly IEngineApiClient _api;
    private readonly ArtworkChangeSaver _apply;
    private Guid _albumId;
    private int _version;

    public BulkArtworkPicker(IEngineApiClient api, ArtworkChangeSaver apply)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(apply);
        _api = api;
        _apply = apply;
    }

    public event Action? Changed;

    /// <summary>The album whose cover is being chosen; empty until <see cref="LoadAsync"/> has run.</summary>
    public Guid AlbumId => _albumId;

    public bool Loading { get; private set; }

    public bool Busy { get; private set; }

    /// <summary>A plain sentence when the pictures could not be read or stored; null otherwise.</summary>
    public string? Message { get; private set; }

    /// <summary>The album's saved covers, the current one first.</summary>
    public IReadOnlyList<ArtworkPickerPicture> Pictures { get; private set; } = [];

    /// <summary>Reads the album's saved covers. A later call for another album replaces an answer still on its way.</summary>
    public async Task LoadAsync(Guid albumId, CancellationToken ct = default)
    {
        if (albumId == Guid.Empty)
        {
            return;
        }

        var version = ++_version;
        _albumId = albumId;
        Loading = true;
        Message = null;
        Changed?.Invoke();
        try
        {
            var workspace = await _api.GetEntityArtworkAsync(EntityKind, albumId, MusicMediaType, null, [Slot], ct).ConfigureAwait(false);
            if (version != _version)
            {
                return;
            }

            Pictures = [.. ArtworkPickerSession.VersionsOf(workspace, Slot).OrderByDescending(picture => picture.IsCurrent)];
            if (workspace is null)
            {
                Message = "The album's covers could not be loaded.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (version == _version)
            {
                Pictures = [];
                Message = "The album's covers could not be loaded.";
            }
        }
        finally
        {
            if (version == _version)
            {
                Loading = false;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Makes the picture the album's cover at once. The cover the album already has is left as it is; when the change fails the previous cover stays and <see cref="Message"/> says so.</summary>
    public async Task<bool> PickAsync(ArtworkPickerPicture picture, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(picture);
        if (_albumId == Guid.Empty || Busy || picture.IsCurrent)
        {
            return false;
        }

        Busy = true;
        Message = null;
        Changed?.Invoke();
        try
        {
            return await ApplyAsync(_albumId, picture.AssetId, ct).ConfigureAwait(false);
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Stores a picture from the person's computer beside the album and makes it the album's cover at once.</summary>
    public async Task<bool> UploadAsync(IBrowserFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (_albumId == Guid.Empty || Busy)
        {
            return false;
        }

        if (file.ContentType is not ("image/jpeg" or "image/png"))
        {
            return Fail("Choose a JPEG or PNG image.");
        }

        if (file.Size > MaximumUploadBytes)
        {
            return Fail("Artwork must be 10 MB or smaller.");
        }

        Busy = true;
        Message = null;
        Changed?.Invoke();
        try
        {
            await using var stream = file.OpenReadStream(MaximumUploadBytes, ct);
            var stored = await _api.UploadCanonicalArtworkAsync(
                EntityKind, _albumId, "Primary", stream, file.Name, null, MusicMediaType, null, Slot, preferred: false, ct: ct).ConfigureAwait(false);
            if (stored is null)
            {
                return Fail(_api.LastError ?? "That image could not be uploaded.");
            }

            return await ApplyAsync(_albumId, stored.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return Fail("That image could not be uploaded.");
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }

    // The album is fixed before anything is awaited, so a switch to another album while the change goes through cannot send it elsewhere.
    private async Task<bool> ApplyAsync(Guid albumId, Guid assetId, CancellationToken ct)
    {
        var outcome = await _apply([new ArtworkChoice(albumId, Slot, assetId.ToString("D"))]).ConfigureAwait(false);
        if (!outcome.Saved)
        {
            // Whatever was stored stays among the album's covers; the album keeps the cover it had.
            await LoadAsync(albumId, ct).ConfigureAwait(false);
            Message = outcome.Failures.FirstOrDefault()?.Reason is { Length: > 0 } reason ? $"The cover was not changed. {reason}"
                : outcome.Message is { Length: > 0 } message ? $"The cover was not changed. {message}"
                : "The cover was not changed.";
            return false;
        }

        await LoadAsync(albumId, ct).ConfigureAwait(false);
        return true;
    }

    private bool Fail(string message)
    {
        Message = message;
        Busy = false;
        Changed?.Invoke();
        return false;
    }
}
