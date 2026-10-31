using System.Collections.Concurrent;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Extras;

/// <inheritdoc cref="IItemExtrasScanner"/>
public sealed class ItemExtrasScanner : IItemExtrasScanner
{
    private readonly ILibraryFolderResolver _libraries;
    private readonly IMediaTypeExtensionCatalog _extensions;
    private readonly IItemExtraRepository _extras;
    private readonly IVaultIngestionHook _vault;
    private readonly IOptionsMonitor<IngestionOptions> _options;
    private readonly ILogger<ItemExtrasScanner> _logger;
    private readonly IItemExtraFactsReader? _facts;
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);

    public ItemExtrasScanner(
        ILibraryFolderResolver libraries,
        IMediaTypeExtensionCatalog extensions,
        IItemExtraRepository extras,
        IVaultIngestionHook vault,
        IOptionsMonitor<IngestionOptions> options,
        ILogger<ItemExtrasScanner> logger,
        IItemExtraFactsReader? facts = null)
    {
        _libraries = libraries;
        _extensions = extensions;
        _extras = extras;
        _vault = vault;
        _options = options;
        _logger = logger;
        _facts = facts;
    }

    public async Task<bool> ClaimsFileAsync(string filePath, LibraryFolderEntry? library, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !ExtrasRule.IsExtraKindExtension(Path.GetExtension(filePath)))
        {
            return false;
        }

        var context = await ResolveContextAsync(filePath, library, ct).ConfigureAwait(false);
        if (context is null)
        {
            return false;
        }

        // A person's earlier decision stands: moved to Extras stays an Extra, moved to an episode stays an episode.
        var decided = await _extras.GetByPathAsync(Path.GetFullPath(filePath), ct).ConfigureAwait(false);
        if (decided is { Origin: ItemExtraOrigins.UserExtra })
        {
            Queue(decided.ItemFolder);
            return true;
        }

        if (decided is { Origin: ItemExtraOrigins.UserEpisode })
        {
            return false;
        }

        var candidate = ExtrasRule.Classify(context, filePath);
        if (candidate is null)
        {
            return false;
        }

        Queue(candidate.ItemFolder);
        return true;
    }

    public void NoteFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !ExtrasRule.IsExtraKindExtension(Path.GetExtension(filePath)))
        {
            return;
        }

        var resolved = _libraries.ResolveSourceForPath(filePath);
        if (resolved is null)
        {
            return;
        }

        var context = BuildContext(resolved.Library, resolved.Source.Path);
        var folder = ExtrasRule.ResolveItemFolder(context, filePath);
        if (folder is not null)
        {
            Queue(folder);
        }
    }

    public async Task<int> SyncQueuedAsync(CancellationToken ct = default)
    {
        var folders = _queued.Keys.ToList();
        var checkedCount = 0;
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            _queued.TryRemove(folder, out _);
            if (await SyncItemFolderAsync(folder, ct).ConfigureAwait(false))
            {
                checkedCount++;
            }
        }

        return checkedCount;
    }

    public async Task<int> SyncAllAsync(CancellationToken ct = default)
    {
        var checkedCount = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in _options.CurrentValue.LibraryFolders)
        {
            if (library.MediaTypes.Count == 0
                || await _vault.IsVaultLibraryAsync(library.Id, ct).ConfigureAwait(false))
            {
                continue;
            }

            foreach (var sourcePath in library.EffectiveSourcePaths)
            {
                var context = BuildContext(library, sourcePath);
                foreach (var folder in ExtrasRule.EnumerateItemFolders(context))
                {
                    ct.ThrowIfCancellationRequested();
                    seen.Add(Path.GetFullPath(folder));
                    await SyncFolderAsync(context, folder, ct).ConfigureAwait(false);
                    checkedCount++;
                }
            }
        }

        // A title folder that has gone away takes its Extras records with it; a source folder that cannot be reached marks nothing.
        foreach (var stored in await _extras.ListItemFoldersAsync(ct).ConfigureAwait(false))
        {
            if (seen.Contains(stored) || Directory.Exists(stored))
            {
                continue;
            }

            var resolved = _libraries.ResolveSourceForPath(stored);
            if (resolved is not null && Directory.Exists(resolved.Source.Path))
            {
                await _extras.RemoveUnderFolderAsync(stored, ct).ConfigureAwait(false);
            }
        }

        return checkedCount;
    }

    public async Task<bool> SyncItemFolderAsync(string itemFolder, CancellationToken ct = default)
    {
        var resolved = _libraries.ResolveSourceForPath(itemFolder);
        if (resolved is null
            || resolved.Library.MediaTypes.Count == 0
            || await _vault.IsVaultLibraryAsync(resolved.Library.Id, ct).ConfigureAwait(false))
        {
            return false;
        }

        await SyncFolderAsync(BuildContext(resolved.Library, resolved.Source.Path), itemFolder, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<ItemExtra?> DescribeAsync(
        string filePath, string origin, int? targetSeason, int? targetEpisode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        var extension = Path.GetExtension(filePath);
        if (!ExtrasRule.IsExtraKindExtension(extension))
        {
            return null;
        }

        var context = await ResolveContextAsync(filePath, null, ct).ConfigureAwait(false);
        var itemFolder = context is null ? null : ExtrasRule.ResolveItemFolder(context, filePath);
        if (context is null || itemFolder is null)
        {
            return null;
        }

        var full = Path.GetFullPath(filePath);
        var kind = ExtrasRule.KindOf(extension);
        var existing = await _extras.GetByPathAsync(full, ct).ConfigureAwait(false);
        var size = new FileInfo(full).Length;
        var facts = existing is not null && existing.SizeBytes == size
            ? new ItemExtraFacts(existing.DurationSeconds, existing.PageCount)
            : _facts is null || kind == ItemExtraKinds.Picture
                ? new ItemExtraFacts(null, null)
                : await _facts.ReadAsync(full, kind, ct).ConfigureAwait(false);
        return new ItemExtra(
            existing?.Id ?? Guid.NewGuid(),
            context.LibraryId,
            Path.GetFullPath(itemFolder),
            full,
            kind,
            origin,
            ExtrasRule.TitleFor(Path.GetFileNameWithoutExtension(full)),
            null,
            existing?.SortOrder ?? 0,
            size,
            facts.DurationSeconds,
            facts.PageCount,
            existing?.FoundAt ?? DateTimeOffset.UtcNow,
            targetSeason,
            targetEpisode);
    }

    public async Task<ExtraEpisodePlacement?> GetEpisodePlacementAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var row = await _extras.GetByPathAsync(Path.GetFullPath(filePath), ct).ConfigureAwait(false);
        if (row is not { Origin: ItemExtraOrigins.UserEpisode, TargetSeason: >= 0, TargetEpisode: > 0 })
        {
            return null;
        }

        // The show is named by its folder: "Shogun (2024)" is the show "Shogun" first aired in 2024.
        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(row.ItemFolder));
        var match = System.Text.RegularExpressions.Regex.Match(
            folder, @"^(?<name>.*?)\s*\((?<year>\d{4})\)\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success
            ? new ExtraEpisodePlacement(match.Groups["name"].Value.Trim(), match.Groups["year"].Value, row.TargetSeason.Value, row.TargetEpisode.Value)
            : new ExtraEpisodePlacement(folder, null, row.TargetSeason.Value, row.TargetEpisode.Value);
    }

    private async Task SyncFolderAsync(ExtrasLibraryContext context, string itemFolder, CancellationToken ct)
    {
        var full = Path.GetFullPath(itemFolder);
        try
        {
            if (!Directory.Exists(full))
            {
                if (Directory.Exists(context.SourceRoot))
                {
                    await _extras.RemoveUnderFolderAsync(full, ct).ConfigureAwait(false);
                }

                return;
            }

            var existing = (await _extras.ListAllInItemFolderAsync(full, ct).ConfigureAwait(false))
                .ToDictionary(row => row.FilePath, StringComparer.OrdinalIgnoreCase);
            var candidates = ExtrasRule.HoldsTitle(context, full) ? ExtrasRule.ScanItemFolder(context, full) : [];
            var found = new List<ItemExtra>(candidates.Count);
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                existing.TryGetValue(candidate.FilePath, out var known);
                var facts = known is not null && known.SizeBytes == candidate.SizeBytes
                    ? new ItemExtraFacts(known.DurationSeconds, known.PageCount)
                    : await ReadFactsAsync(candidate, ct).ConfigureAwait(false);
                found.Add(new ItemExtra(
                    known?.Id ?? Guid.NewGuid(),
                    context.LibraryId,
                    full,
                    candidate.FilePath,
                    candidate.Kind,
                    ItemExtraOrigins.Rule,
                    candidate.Title,
                    candidate.GroupKey,
                    index,
                    candidate.SizeBytes,
                    facts.DurationSeconds,
                    facts.PageCount,
                    known?.FoundAt ?? DateTimeOffset.UtcNow));
            }

            var gone = existing.Values
                .Where(row => row.Origin != ItemExtraOrigins.Rule && !File.Exists(row.FilePath))
                .Select(row => row.FilePath)
                .ToList();
            await _extras.SyncItemFolderAsync(full, found, gone, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One unreadable folder never stops the pass; its Extras are left as they were and the next pass tries again.
            _logger.LogWarning(ex, "Could not check the Extras of \"{Folder}\"", Path.GetFileName(full));
        }
    }

    private async Task<ItemExtraFacts> ReadFactsAsync(ExtraCandidate candidate, CancellationToken ct)
    {
        if (_facts is null || candidate.Kind == ItemExtraKinds.Picture)
        {
            return new ItemExtraFacts(null, null);
        }

        return await _facts.ReadAsync(candidate.FilePath, candidate.Kind, ct).ConfigureAwait(false);
    }

    private async Task<ExtrasLibraryContext?> ResolveContextAsync(string filePath, LibraryFolderEntry? library, CancellationToken ct)
    {
        var resolved = _libraries.ResolveSourceForPath(filePath);
        var owner = resolved?.Library ?? library;
        var sourcePath = resolved?.Source.Path ?? _libraries.ResolveSourcePath(filePath);
        if (owner is null || string.IsNullOrWhiteSpace(sourcePath) || owner.MediaTypes.Count == 0)
        {
            return null;
        }

        if (await _vault.IsVaultLibraryAsync(owner.Id, ct).ConfigureAwait(false))
        {
            return null;
        }

        return BuildContext(owner, sourcePath);
    }

    private ExtrasLibraryContext BuildContext(LibraryFolderEntry library, string sourcePath)
    {
        var main = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in library.MediaTypes)
        {
            main.UnionWith(_extensions.GetExtensionsFor(type));
        }

        return new ExtrasLibraryContext(library.Id, library.MediaTypes, sourcePath, main);
    }

    private void Queue(string itemFolder) => _queued.TryAdd(Path.GetFullPath(itemFolder), 0);
}
