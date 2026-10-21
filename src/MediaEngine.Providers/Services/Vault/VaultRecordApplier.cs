using System.Globalization;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Services.StashBox;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services.Vault;

/// <param name="AppliedVia">"automatic" (the Library matched the file by itself) or "identify" (a person picked the record and ticked fields).</param>
/// <param name="Fields">The fields to apply (<see cref="VaultFieldNames"/>); null means every field the record has.</param>
public sealed record VaultApplyRequest(
    Guid AssetId,
    string LibraryId,
    AdultVideoInfo Info,
    string AppliedVia,
    IReadOnlySet<string>? Fields);

/// <param name="Applied">The fields that were written (<see cref="VaultFieldNames"/>).</param>
/// <param name="Refused">Fields the record had but that failed a check and were not stored (title, cover and so on), by field name.</param>
/// <param name="CoverRefusal">Why the cover was refused, when it was. The generated still frame stays in place.</param>
public sealed record VaultApplyResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Refused, VaultImageRefusal CoverRefusal);

/// <summary>
/// Checks a stash-box record and writes it to a Vault video (Videos spec D30, D31): every field is validated, a person's own edit
/// is never replaced, names and artwork go to the Vault's own tables only, and the source of each field is recorded (which server,
/// which record, how it was matched, when). An automatic match fills fields that are blank or only came from the file itself; a
/// person's Identify choice replaces exactly the fields they ticked.
/// </summary>
public sealed class VaultRecordApplier
{
    private readonly ICanonicalValueRepository _canonical;
    private readonly IMetadataClaimRepository _claims;
    private readonly IVaultNameRepository _names;
    private readonly IVaultArtworkRepository _artwork;
    private readonly IVaultMatchRepository _matches;
    private readonly TuvimaDataPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<VaultRecordApplier> _logger;

    public VaultRecordApplier(
        ICanonicalValueRepository canonical,
        IMetadataClaimRepository claims,
        IVaultNameRepository names,
        IVaultArtworkRepository artwork,
        IVaultMatchRepository matches,
        TuvimaDataPaths paths,
        TimeProvider time,
        ILogger<VaultRecordApplier> logger)
    {
        _canonical = canonical;
        _claims = claims;
        _names = names;
        _artwork = artwork;
        _matches = matches;
        _paths = paths;
        _time = time;
        _logger = logger;
    }

    /// <summary>A value is the person's own when they edited it; those are never replaced, whatever a server says.</summary>
    public static bool IsPersonsOwn(CanonicalValue? value) =>
        value is { WinningProviderId: { } provider } && WellKnownProviders.IsUserSource(provider) && !string.IsNullOrWhiteSpace(value.Value);

    public async Task<VaultApplyResult> ApplyAsync(VaultApplyRequest request, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var info = request.Info;
        var automatic = request.AppliedVia == VaultAppliedVia.Automatic;
        var existing = await _canonical.GetByEntityAsync(request.AssetId, ct).ConfigureAwait(false);

        bool Wanted(string field) => request.Fields is null || request.Fields.Contains(field);
        CanonicalValue? Current(string key) => existing.FirstOrDefault(v => v.Key == key);

        // A person's own edit is never replaced, even when they tick the field: they untick it to keep it, and the preview says so.
        bool MayWrite(string key) => !IsPersonsOwn(Current(key));

        var applied = new List<string>();
        var refused = new List<string>();
        var sources = new List<VaultFieldSource>();
        var claims = new List<MetadataClaim>();
        var canonicals = new List<CanonicalValue>();
        var matchedBy = automatic ? info.MatchedBy : AdultMatchSignals.Identify;

        VaultFieldSource Source(string field, string? language) => new(
            request.AssetId, field, info.ServerId, info.RecordId, matchedBy, request.AppliedVia, language, now);

        void Write(string key, string value)
        {
            claims.Add(new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = request.AssetId,
                ProviderId = WellKnownProviders.StashBox,
                ClaimKey = key,
                ClaimValue = value,
                ClaimedAt = now,
                Confidence = 0.9,
            });
            canonicals.Add(new CanonicalValue
            {
                EntityId = request.AssetId,
                Key = key,
                Value = value,
                LastScoredAt = now,
                WinningProviderId = WellKnownProviders.StashBox,
            });
        }

        // Text fields: checked, then written when they are wanted and not the person's own.
        if (Wanted(VaultFieldNames.Title))
        {
            var title = VaultFieldValidator.Title(info.Title);
            if (title is null)
            {
                if (!string.IsNullOrWhiteSpace(info.Title))
                {
                    refused.Add(VaultFieldNames.Title);
                }
            }
            else if (MayWrite(MetadataFieldConstants.Title))
            {
                Write(MetadataFieldConstants.Title, title);
                sources.Add(Source(VaultFieldNames.Title, VaultFieldValidator.NoteLanguage(title)));
                applied.Add(VaultFieldNames.Title);
            }
        }

        if (Wanted(VaultFieldNames.Description))
        {
            var description = VaultFieldValidator.Description(info.Description);
            if (description is null)
            {
                if (!string.IsNullOrWhiteSpace(info.Description))
                {
                    refused.Add(VaultFieldNames.Description);
                }
            }
            else if (MayWrite(MetadataFieldConstants.Description))
            {
                Write(MetadataFieldConstants.Description, description);
                sources.Add(Source(VaultFieldNames.Description, VaultFieldValidator.NoteLanguage(description)));
                applied.Add(VaultFieldNames.Description);
            }
        }

        if (Wanted(VaultFieldNames.ReleaseDate) && info.Date is { } date)
        {
            if (!VaultFieldValidator.IsSensibleDate(date))
            {
                refused.Add(VaultFieldNames.ReleaseDate);
            }
            else if (MayWrite(MetadataFieldConstants.OriginalReleaseDate))
            {
                Write(MetadataFieldConstants.OriginalReleaseDate, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                if (MayWrite(MetadataFieldConstants.Year))
                {
                    Write(MetadataFieldConstants.Year, date.Year.ToString(CultureInfo.InvariantCulture));
                }

                sources.Add(Source(VaultFieldNames.ReleaseDate, null));
                applied.Add(VaultFieldNames.ReleaseDate);
            }
        }

        if (applied.Count > 0)
        {
            Write(MetadataFieldConstants.VideoLookupSource, VaultLookupSources.StashBox);
            await _claims.InsertBatchAsync(claims, ct).ConfigureAwait(false);
            await _canonical.UpsertBatchAsync(canonicals, ct).ConfigureAwait(false);
        }

        // The Vault's own names (Videos spec D27, D28): created or found in the Vault tables only. An automatic match adds to what
        // is there; Identify replaces the kinds the person ticked.
        if (Wanted(VaultFieldNames.Performers))
        {
            var performers = VaultFieldValidator.Names(info.Performers);
            if (performers.Count > 0)
            {
                if (!automatic)
                {
                    await _names.UnlinkPerformersAsync(request.AssetId, ct).ConfigureAwait(false);
                }

                foreach (var performer in performers)
                {
                    var id = await _names.EnsurePerformerAsync(request.LibraryId, performer, null, StashBoxSettings.ProviderName, now, ct).ConfigureAwait(false);
                    await _names.LinkPerformerAsync(request.AssetId, id, ct).ConfigureAwait(false);
                }

                sources.Add(Source(VaultFieldNames.Performers, null));
                applied.Add(VaultFieldNames.Performers);
            }
        }

        if (Wanted(VaultFieldNames.Studio) && VaultFieldValidator.Name(info.Studio) is { } studio)
        {
            if (!automatic)
            {
                await _names.UnlinkStudiosAsync(request.AssetId, ct).ConfigureAwait(false);
            }

            var id = await _names.EnsureStudioAsync(request.LibraryId, studio, StashBoxSettings.ProviderName, now, ct).ConfigureAwait(false);
            await _names.LinkStudioAsync(request.AssetId, id, ct).ConfigureAwait(false);
            sources.Add(Source(VaultFieldNames.Studio, null));
            applied.Add(VaultFieldNames.Studio);
        }

        if (Wanted(VaultFieldNames.Tags))
        {
            var tags = VaultFieldValidator.Names(info.Tags, VaultFieldValidator.MaxTagLength);
            if (tags.Count > 0)
            {
                if (!automatic)
                {
                    await _names.UnlinkTagsAsync(request.AssetId, ct).ConfigureAwait(false);
                }

                foreach (var tag in tags)
                {
                    var id = await _names.EnsureTagAsync(request.LibraryId, tag, now, ct).ConfigureAwait(false);
                    await _names.LinkTagAsync(request.AssetId, id, ct).ConfigureAwait(false);
                }

                sources.Add(Source(VaultFieldNames.Tags, null));
                applied.Add(VaultFieldNames.Tags);
            }
        }

        var coverRefusal = VaultImageRefusal.None;
        if (Wanted(VaultFieldNames.Cover) && info.Cover is not null)
        {
            (var stored, coverRefusal) = await StoreCoverAsync(request, info, automatic, now, ct).ConfigureAwait(false);
            if (stored)
            {
                sources.Add(Source(VaultFieldNames.Cover, null));
                applied.Add(VaultFieldNames.Cover);
            }
            else if (coverRefusal != VaultImageRefusal.None)
            {
                refused.Add(VaultFieldNames.Cover);
            }
        }

        // The match itself is recorded even when nothing new was written: the video is Matched.
        await _matches.SaveMatchAsync(
            new VaultMatch(request.AssetId, info.ServerId, info.RecordId, matchedBy, request.AppliedVia, now), sources, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "A Vault video was matched by {Signal} and applied {Via}; {Count} fields were written",
            matchedBy, request.AppliedVia, applied.Count);
        return new VaultApplyResult(applied, refused, coverRefusal);
    }

    private async Task<(bool Stored, VaultImageRefusal Refusal)> StoreCoverAsync(
        VaultApplyRequest request, AdultVideoInfo info, bool automatic, DateTimeOffset now, CancellationToken ct)
    {
        var current = (await _artwork.GetForOwnerAsync(VaultArtworkOwnerKinds.Asset, request.AssetId, ct).ConfigureAwait(false))
            .Where(a => a.Kind == VaultArtworkKinds.Cover)
            .ToList();
        if (automatic && current.Count > 0)
        {
            return (false, VaultImageRefusal.None);
        }

        var (image, refusal) = VaultArtworkValidator.Validate(info.Cover, VaultImagePurpose.Cover);
        if (image is null)
        {
            _logger.LogInformation("A Vault cover was refused ({Reason}); the still frame stays", refusal);
            return (false, refusal);
        }

        // An identical image is dropped rather than stored twice.
        if (current.Any(c => string.Equals(c.ContentHash, image.Hash, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, VaultImageRefusal.None);
        }

        var folder = Path.Combine(_paths.Root, "vault-artwork", request.AssetId.ToString("N"));
        var path = Path.Combine(folder, $"cover-{image.Hash[..12].ToLowerInvariant()}{image.Extension}");
        try
        {
            Directory.CreateDirectory(folder);
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, image.Bytes, ct).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning("A Vault cover could not be written ({Type}); the still frame stays", ex.GetType().Name);
            return (false, VaultImageRefusal.CouldNotRead);
        }

        await _artwork.AddAsync(
            VaultArtworkOwnerKinds.Asset, request.AssetId, VaultArtworkKinds.Cover, path, image.Hash, image.Width, image.Height,
            StashBoxSettings.ProviderName, now, ct).ConfigureAwait(false);

        // A cover a person replaced in Identify is removed with its file.
        foreach (var old in current)
        {
            await _artwork.DeleteAsync(old.Id, ct).ConfigureAwait(false);
            TryDelete(old.FilePath, path);
        }

        return (true, VaultImageRefusal.None);
    }

    private void TryDelete(string oldPath, string keepPath)
    {
        if (string.Equals(oldPath, keepPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(oldPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation("A replaced Vault cover file could not be removed ({Type})", ex.GetType().Name);
        }
    }
}
