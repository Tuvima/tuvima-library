using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Components.Forms;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// "Set one cover for these 12 songs. It sets the album's cover." (universal editor spec 3c, WP9). Songs of one album share one picture
/// role, the album's cover, so the choice is staged once on the album, never once per song. The pictures offered are the album's own saved
/// ones plus whatever the person uploads (stored beside the album, unchosen, like the single Artwork tool); nothing becomes the cover
/// until Save changes.
/// </summary>
public sealed class BulkArtworkPicker
{
    /// <summary>The picture slot a music album's cover is staged under.</summary>
    public const string Slot = "CoverArt";

    private const string EntityKind = "Work";
    private const string MusicMediaType = "Music";
    private const long MaximumUploadBytes = 10 * 1024 * 1024;

    private readonly IEngineApiClient _api;
    private readonly PendingEditList _pending;
    private Guid _albumId;
    private int _version;

    public BulkArtworkPicker(IEngineApiClient api, PendingEditList pending)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pending);
        _api = api;
        _pending = pending;
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

    /// <summary>The picture staged to become the album's cover, or null when none is staged.</summary>
    public Guid? StagedAssetId => StagedChange() is { TargetId: { } target } && Guid.TryParse(target, out var id) ? id : null;

    public bool HasStaged => StagedChange() is not null;

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

    /// <summary>Stages the picture as the album's cover. Choosing the cover the album already has takes any staged choice back out.</summary>
    public void Pick(ArtworkPickerPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        if (_albumId == Guid.Empty)
        {
            return;
        }

        if (picture.IsCurrent)
        {
            Undo();
            return;
        }

        var current = Pictures.FirstOrDefault(candidate => candidate.IsCurrent);
        _pending.SetArtwork(
            _albumId,
            Slot,
            picture.AssetId.ToString("D"),
            label: string.IsNullOrWhiteSpace(picture.Caption) ? "Album cover" : picture.Caption,
            before: current is null ? EditReviewRows.NotSet : string.IsNullOrWhiteSpace(current.Caption) ? "Current cover" : current.Caption);
        Changed?.Invoke();
    }

    /// <summary>Takes the staged cover back out; the album keeps the cover it has.</summary>
    public void Undo()
    {
        if (StagedChange() is { } change && _pending.Remove(change.Id))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Stores a picture from the person's computer beside the album (unchosen) and stages it as the cover.</summary>
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

            await LoadAsync(_albumId, ct).ConfigureAwait(false);
            var added = Pictures.FirstOrDefault(picture => picture.AssetId == stored.Id);
            if (added is not null)
            {
                Pick(added);
            }

            return true;
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

    private EditSessionChange? StagedChange() =>
        _albumId == Guid.Empty
            ? null
            : _pending.Changes.FirstOrDefault(change =>
                change.Kind == EditSessionChangeKinds.Artwork
                && change.EntityId == _albumId
                && string.Equals(change.Slot, Slot, StringComparison.OrdinalIgnoreCase));

    private bool Fail(string message)
    {
        Message = message;
        Busy = false;
        Changed?.Invoke();
        return false;
    }
}
