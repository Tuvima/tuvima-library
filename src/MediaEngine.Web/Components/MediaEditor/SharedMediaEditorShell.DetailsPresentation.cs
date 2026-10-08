using System.Globalization;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Components.MediaEditor;

public partial class SharedMediaEditorShell
{
    protected MediaEditorDetailsPresentation DetailsPresentation => BuildDetailsPresentation();
    protected IReadOnlyList<MediaEditorDetailsFieldPresentation> DetailsPrimaryFacts => DetailsPresentation.Facts;
    protected MediaEditorDetailsFieldPresentation? DetailsHeading => DetailsPresentation.Heading;
    protected IReadOnlyList<MediaEditorDetailsFieldPresentation> DetailsSecondaryFields => DetailsPresentation.Fields;
    protected IReadOnlyList<MediaEditorDetailsFieldPresentation> DetailsAdditionalFields => DetailsPresentation.AdditionalFields;
    protected MediaEditorDetailsFieldPresentation? DetailsSynopsis => DetailsPresentation.Synopsis;
    protected string DetailsImageShape => ResolveDetailsImageShape();
    protected string DetailsImageWidthClass => DetailsImageShape switch
    {
        "landscape" => "sme-details-image--landscape",
        "square" => "sme-details-image--square",
        _ => "sme-details-image--portrait",
    };
    protected IReadOnlyList<MediaEditorDetailsSummaryFact> DetailsLibrarySummary => DetailsPresentation.LibrarySummary;
    protected IReadOnlyList<MediaEditorDetailsFilePresentation> DetailsFiles => DetailsPresentation.Files;
    protected int DetailsFileCount => DetailsFiles.Count;
    protected int? DetailsOwnedArtworkCount => DetailsPresentation.OwnedArtworkCount;
    protected IReadOnlyList<(string Label, string Value)> DetailsTechnicalFacts => BuildDetailsTechnicalFacts();
    protected IReadOnlyList<LibraryItemHistoryDto> DetailsRecentActivity => _history
        .OrderByDescending(entry => entry.OccurredAt)
        .Take(3)
        .ToList();
    protected string DetailsMetadataWritebackState => ResolveDetailsMetadataWritebackState();
    protected bool CanAddDetailsTags => DetailsPresentation.CanAddTags;
    protected string? DetailsImageUrl => MediaTileArtworkUrl.Sized(ResolveDetailsImageVariant()?.ImageUrl, "m");
    protected string? DetailsImageSrcSet => MediaTileArtworkUrl.SrcSet(
        MediaTileArtworkUrl.Sized(ResolveDetailsImageVariant()?.ImageUrl, "s"),
        MediaTileArtworkUrl.Sized(ResolveDetailsImageVariant()?.ImageUrl, "m"),
        MediaTileArtworkUrl.Sized(ResolveDetailsImageVariant()?.ImageUrl, "l"));
    protected string DetailsImageSizes => "(max-width: 760px) calc(100vw - 68px), (max-width: 1450px) max(250px, calc(41.86046512vw - 137.209302px)), 340px";

    private MediaEditorDetailsPresentation BuildDetailsPresentation()
    {
        if (ActiveScope is not { } activeScope)
        {
            return new();
        }

        var availableKeys = activeScope.FieldSnapshot.CanonicalFields.Select(field => field.Key)
            .Concat(activeScope.FieldSnapshot.DisplayOverrides.Keys)
            .Concat(activeScope.FieldSnapshot.AllowedOverrideKeys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var draft = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in availableKeys)
        {
            if (_editedValues.TryGetValue(BuildScopedFieldKey(key), out var value))
            {
                draft[key] = value;
            }
        }

        IReadOnlyDictionary<string, string>? profileOverrides = null;
        if (_profilePreferencesByWork.TryGetValue(activeScope.FieldEntityId, out var preferences))
        {
            var values = preferences.DisplayOverrides
                .Where(pair => !string.Equals(pair.Key, "custom_tags", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            profileOverrides = values;
        }

        var scopes = (_editorContext?.Scopes ?? [])
            .ToDictionary(scope => scope.ScopeId, StringComparer.OrdinalIgnoreCase);
        var (summary, artworkCount) = BuildOwnedSummary(activeScope);
        Guid? preferredOwnerId = _workVersions is { } versions
                               && versions.WorkId == activeScope.FieldEntityId
                               && !string.Equals(versions.SelectedEntityType, "Work", StringComparison.OrdinalIgnoreCase)
            ? versions.SelectedEntityId
            : null;
        var presentation = MediaEditorDetailsPresenter.Build(activeScope, scopes, EditorMediaType, draft, profileOverrides, preferredOwnerId);
        var observedCountFacts = summary.Select(fact => new MediaEditorDetailsFieldPresentation(
            fact.Label == "Owned seasons" ? "owned_seasons" : "owned_episodes",
            fact.Label == "Owned seasons" ? "owned_seasons" : "owned_episodes",
            fact.Label,
            fact.Value,
            fact.Label == "Owned seasons" ? "ViewAgenda" : "LiveTv",
            "text",
            "Observed",
            "Owned library",
            null,
            false,
            false,
            false,
            1,
            fact.Value,
            fact.Value,
            false)).ToList();
        return presentation with
        {
            Facts = presentation.Facts.Concat(observedCountFacts).ToList(),
            LibrarySummary = summary,
            OwnedArtworkCount = artworkCount,
            Files = BuildDetailsFiles(),
        };
    }

    private (IReadOnlyList<MediaEditorDetailsSummaryFact> Summary, int? ArtworkCount) BuildOwnedSummary(MediaEditorScopeDto activeScope)
    {
        if (!string.Equals(EditorMediaType, "TV", StringComparison.OrdinalIgnoreCase)
            || activeScope.ScopeId is not ("series" or "season")
            || _navigator?.Nodes is not { Count: > 0 } nodes)
        {
            return ([], null);
        }

        var owned = nodes.Where(node => node.IsOwned).ToList();
        var summary = new List<MediaEditorDetailsSummaryFact>();
        var seasons = string.Equals(activeScope.ScopeId, "series", StringComparison.OrdinalIgnoreCase)
            ? owned.Count(node => string.Equals(node.ScopeId, "season", StringComparison.OrdinalIgnoreCase))
            : 0;
        var episodes = string.Equals(activeScope.ScopeId, "series", StringComparison.OrdinalIgnoreCase)
            ? owned.Count(node => string.Equals(node.ScopeId, "episode", StringComparison.OrdinalIgnoreCase))
            : owned.Count(node => string.Equals(node.ScopeId, "episode", StringComparison.OrdinalIgnoreCase)
                                  && node.ParentNodeId == SelectedNavigatorNode?.NodeId);
        if (seasons > 0)
        {
            summary.Add(new("Owned seasons", FormatOwnedCount(seasons, "season")));
        }
        if (episodes > 0)
        {
            summary.Add(new("Owned episodes", FormatOwnedCount(episodes, "episode")));
        }
        var artworkCount = owned.Count(node => !string.IsNullOrWhiteSpace(node.ArtworkUrl));
        return (summary, artworkCount);
    }

    private static string FormatOwnedCount(int count, string singular) =>
        $"{count.ToString(CultureInfo.CurrentCulture)} {singular}{(count == 1 ? string.Empty : "s")}";

    private IReadOnlyList<MediaEditorDetailsFilePresentation> BuildDetailsFiles()
    {
        var scope = ActiveScope;
        if (scope is null || _workVersions is null || _workVersions.WorkId != scope.FieldEntityId
            || scope.ScopeId is "series" or "season")
        {
            return [];
        }

        var editions = _workVersions.Editions.AsEnumerable();
        if (string.Equals(_workVersions.SelectedEntityType, "Edition", StringComparison.OrdinalIgnoreCase))
        {
            editions = editions.Where(edition => edition.EditionId == _workVersions.SelectedEntityId);
        }

        var files = editions
            .SelectMany(edition => edition.Assets.Select(asset => new
            {
                edition.EditionId,
                asset.AssetId,
                EditionLabel = string.IsNullOrWhiteSpace(edition.Label) ? "Edition" : edition.Label!,
                asset.FileName,
                asset.TechnicalLabel,
            }))
            .Where(file => !string.IsNullOrWhiteSpace(file.FileName))
            .ToList();

        if (string.Equals(_workVersions.SelectedEntityType, "Asset", StringComparison.OrdinalIgnoreCase))
        {
            return files.Where(file => file.AssetId == _workVersions.SelectedEntityId)
                .Select(file => new MediaEditorDetailsFilePresentation(file.EditionLabel, file.FileName, file.TechnicalLabel))
                .ToList();
        }

        return files.Select(file => new MediaEditorDetailsFilePresentation(file.EditionLabel, file.FileName, file.TechnicalLabel)).ToList();
    }

    private IReadOnlyList<(string Label, string Value)> BuildDetailsTechnicalFacts()
    {
        if (_detail is null)
        {
            return [];
        }
        var facts = new List<(string Label, string Value)>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                facts.Add((label, value.Trim()));
            }
        }

        var selectedVersion = _workVersions is { } versions
                              && !string.Equals(versions.SelectedEntityType, "Work", StringComparison.OrdinalIgnoreCase);
        if (selectedVersion)
        {
            foreach (var file in DetailsFiles)
            {
                Add("File", file.FileName);
                Add("Technical", file.TechnicalLabel);
            }
            return facts;
        }

        if (SelectedNavigatorNode?.IsLeaf != true)
        {
            return [];
        }

        Add("File", _detail.FileName);
        if (_detail.FileSizeBytes is > 0)
        {
            Add("Size", FormatDetailsFileSize(_detail.FileSizeBytes.Value));
        }

        var technical = _detail.PlaybackSummary;
        Add("Video", PlaybackTechnicalSummaryDisplay.VideoChip(technical));
        Add("Audio", PlaybackTechnicalSummaryDisplay.AudioChip(technical));
        Add("Subtitles", PlaybackTechnicalSummaryDisplay.SubtitleChip(technical));
        return facts;
    }

    private static string FormatDetailsFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }

    private string ResolveDetailsMetadataWritebackState()
    {
        if (_writebackSettings is null)
        {
            return "limited";
        }
        if (!_writebackSettings.MetadataWritebackEnabled)
        {
            return "disabled";
        }
        if (SelectedNavigatorNode?.PrimaryAssetId is null)
        {
            return "limited";
        }

        // The settings flag reports policy only. Keep the UI conservative about
        // containers whose current writer/read-back behavior is not established.
        var files = DetailsFiles;
        if (files.Count == 0)
        {
            return "limited";
        }
        return files.All(file => HasBoundedMetadataWriter(file.FileName)) ? "enabled" : "limited";
    }

    private static bool HasBoundedMetadataWriter(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".epub", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".cbz", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveDetailsImageShape()
    {
        var variant = ResolveDetailsImageVariant();
        if (variant is { WidthPx: > 0, HeightPx: > 0 })
        {
            var ratio = (double)variant.WidthPx.Value / variant.HeightPx.Value;
            if (ratio > 1.25)
            {
                return "landscape";
            }
            if (ratio >= 0.88)
            {
                return "square";
            }
            return "portrait";
        }
        return DetailsPresentation.ImageShape;
    }

    private ArtworkVariantDto? ResolveDetailsImageVariant()
    {
        var scope = ActiveScope;
        if (scope is null)
        {
            return null;
        }
        if (EditorMediaType == "TV" && scope.ScopeId == "episode")
        {
            return GetExactScopeArtworkVariant(scope, "EpisodeStill");
        }

        if (EditorMediaType == "TV" && scope.ScopeId == "series")
        {
            return GetExactScopeArtworkVariant(scope, "Background")
                   ?? GetExactScopeArtworkVariant(scope, "Poster")
                   ?? GetExactScopeArtworkVariant(scope, "CoverArt");
        }

        if (EditorMediaType == "TV" && scope.ScopeId == "season")
        {
            return GetExactScopeArtworkVariant(scope, "SeasonPoster")
                   ?? GetExactScopeArtworkVariant(GetScopeById("series"), "Poster")
                   ?? GetExactScopeArtworkVariant(GetScopeById("series"), "CoverArt");
        }

        if (EditorMediaType == "Music" && scope.ScopeId == "track")
        {
            return GetExactScopeArtworkVariant(GetScopeById(scope.ArtworkOwnerScopeId), "CoverArt");
        }

        return EditorMediaType == "Music"
            ? GetExactScopeArtworkVariant(scope, "CoverArt")
            : GetExactScopeArtworkVariant(scope, "Poster") ?? GetExactScopeArtworkVariant(scope, "CoverArt");
    }

    private ArtworkVariantDto? GetExactScopeArtworkVariant(MediaEditorScopeDto? scope, string assetType)
    {
        if (scope is null || !_artworkStates.TryGetValue(BuildScopeStateKey(scope.FieldEntityId, scope.ScopeId), out var artwork))
        {
            return null;
        }

        var variant = artwork.Slots.FirstOrDefault(slot =>
                string.Equals(slot.AssetType, assetType, StringComparison.OrdinalIgnoreCase))?.Variants
            .OrderByDescending(item => item.IsPreferred)
            .ThenByDescending(item => item.CreatedAt)
            .FirstOrDefault();
        return variant;
    }
}
