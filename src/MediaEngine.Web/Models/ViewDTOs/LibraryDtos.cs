namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>Library pipeline stage states.</summary>
public enum LibraryStageState { Completed, Warning, Failed, Pending, Running }

/// <summary>Library item display statuses.</summary>
public enum LibraryStatus { Verified, Provisional, NeedsReview, Quarantined, WaitingForProvider, Unowned, RetailMatched }

/// <summary>A single pipeline stage indicator.</summary>
public sealed class LibraryPipelineStage
{
    public LibraryStageState State { get; init; }
    public string Label { get; init; } = "";
}

/// <summary>Wraps LibraryCatalogItemViewModel with library-specific computed properties.</summary>
public sealed class LibraryItemViewModel
{
    public Guid? ArtistPersonId { get; init; }
    public bool IsUpdatingDetails { get; init; }
    public Guid EntityId { get; init; }
    public Guid? AssetId { get; init; }
    public Guid? CollectionId { get; init; }
    public Guid? RootWorkId { get; init; }
    public string Title { get; init; } = "";
    public string? OriginalTitle { get; init; }
    public string? Author { get; init; }
    public string? Director { get; init; }
    public string? Artist { get; init; }
    public string? Series { get; init; }
    public string? SeriesPosition { get; init; }
    public string? Narrator { get; init; }
    public string? Genre { get; init; }
    public string? Runtime { get; init; }
    public string? Rating { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Composer { get; init; }
    public string? Comments { get; init; }
    public string? TrackNumber { get; init; }
    public string? Season { get; init; }
    public string? Episode { get; init; }
    public string? Duration { get; init; }
    public long? DurationSeconds { get; init; }
    public string? DiscNumber { get; init; }
    public string? ShowName { get; init; }
    public string? EpisodeTitle { get; init; }
    public string? Network { get; init; }
    public string? TopCast { get; init; }
    public string? TrackCount { get; init; }
    public string? SeasonCount { get; init; }
    public string? AlbumCount { get; init; }
    public string? Year { get; init; }
    public string? SortTitle { get; init; }
    public string? SortArtist { get; init; }
    public string? SortAlbum { get; init; }
    public string? Kind { get; init; }
    public string MediaType { get; init; } = "";
    public string? CoverUrl { get; init; }
    public string? CoverThumbUrl { get; init; }
    public string? BackgroundUrl { get; init; }
    public string? BannerUrl { get; init; }
    public double Confidence { get; init; }
    public string Status { get; init; } = "";
    public string? ReviewTrigger { get; init; }
    public string? WikidataQid { get; init; }
    public string? WikidataStatus { get; init; }
    public string RetailMatch { get; init; } = "none";
    public string? RetailMatchDetail { get; init; }
    public string WikidataMatch { get; init; } = "none";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DateModified { get; init; }
    public DateTimeOffset? LastPlayedAt { get; init; }
    public DateTimeOffset? ReleaseDate { get; init; }
    public string? FileName { get; init; }
    public string? FilePath { get; init; }
    public long? FileSizeBytes { get; init; }
    public int? PlayCount { get; init; }
    public bool IsFavorite { get; init; }
    public Guid? ReviewItemId { get; init; }
    public bool HasUserLocks { get; init; }
    public string? HeroUrl { get; init; }
    public bool MissingUniverse { get; init; }
    public string PipelineStep { get; init; } = "Retail";
    public string LibraryVisibility { get; init; } = "hidden";
    public bool IsReadyForLibrary { get; init; }
    public string ArtworkState { get; init; } = "pending";
    public string? ArtworkSource { get; init; }
    public DateTimeOffset? ArtworkSettledAt { get; init; }

    /// <summary>Name of the provider that is currently unreachable (if any).</summary>
    public string? FailedProviderName { get; init; }

    /// <summary>Artist headshot URL, set only for Music By Artist container rows.</summary>
    public string? ArtistPhotoUrl { get; init; }

    public LibraryStatus LibraryDisplayStatus => ComputeLibraryStatus();
    public LibraryPipelineStage Stage1 => ComputeRetailStage();
    public LibraryPipelineStage Stage2 => ComputeWikidataStage();
    public LibraryPipelineStage Stage3 => ComputeEnrichmentStage();

    // Legacy compact views still use these combined stages.
    public LibraryPipelineStage IdentifiedStage => ComputeIdentifiedStage();
    public LibraryPipelineStage EnrichedStage => ComputeEnrichedStage();

    public int ConfidenceSegments => (int)Math.Round(Confidence * 5);
    public string? UniverseName { get; init; }
    public int? QuarantineDays { get; init; }
    public string ReadinessLabel => ComputeReadinessLabel();

    /// <summary>
    /// Compact provenance summary for the Resolution column (e.g. "ISBN -> Q83471", "Title search -> Q12345").
    /// Returns null when no resolution has occurred.
    /// </summary>
    public string? ResolutionSummary => ComputeResolutionSummary();

    /// <summary>
    /// Human-readable format specs: "4K REMUX" for video, "FLAC" for audio,
    /// "EPUB" for books, etc. Computed from media type and available metadata.
    /// </summary>
    public string? Specs => ComputeSpecs();

    /// <summary>
    /// Primary creator for display: Director (Movies/TV), Author (Books/Audiobooks/Comics),
    /// Artist (Music). Falls through in priority order.
    /// </summary>
    public string? Creator =>
        !string.IsNullOrWhiteSpace(Director) ? Director :
        !string.IsNullOrWhiteSpace(Author) ? Author :
        !string.IsNullOrWhiteSpace(Artist) ? Artist :
        !string.IsNullOrWhiteSpace(Narrator) ? Narrator :
        null;

    /// <summary>
    /// Contextual grouping label: Show name (TV), Album (Music), Series (Books/Comics/Audiobooks).
    /// Used in the "Context" column of the redesigned media list.
    /// </summary>
    public string? ContextLabel =>
        !string.IsNullOrWhiteSpace(ShowName) ? ShowName :
        !string.IsNullOrWhiteSpace(Album) ? Album :
        !string.IsNullOrWhiteSpace(Series) ? Series :
        null;

    /// <summary>Factory: convert a LibraryCatalogItemViewModel to LibraryItemViewModel.</summary>
    public static LibraryItemViewModel From(LibraryCatalogItemViewModel r) => new()
    {
        EntityId = r.EntityId,
        Title = r.Title,
        OriginalTitle = r.OriginalTitle,
        Author = r.Author,
        Director = r.Director,
        Artist = r.Artist,
        Series = r.Series,
        SeriesPosition = r.SeriesPosition,
        Narrator = r.Narrator,
        Genre = r.Genre,
        Runtime = r.Runtime,
        Rating = r.Rating,
        Album = r.Album,
        TrackNumber = r.TrackNumber,
        Season = r.Season,
        Episode = r.Episode,
        Duration = r.Duration,
        DiscNumber = r.DiscNumber,
        ShowName = r.ShowName,
        EpisodeTitle = r.EpisodeTitle,
        Network = r.Network,
        TopCast = r.TopCast,
        TrackCount = r.TrackCount,
        Year = r.Year,
        MediaType = r.MediaType,
        CoverUrl = r.CoverUrl,
        CoverThumbUrl = !string.IsNullOrEmpty(r.CoverUrl)
            ? r.CoverUrl.Replace("/cover", "/cover-thumb")
            : null,
        BackgroundUrl = r.BackgroundUrl,
        BannerUrl = r.BannerUrl,
        Confidence = r.Confidence,
        Status = r.Status,
        ReviewTrigger = r.ReviewTrigger,
        WikidataQid = r.WikidataQid,
        WikidataStatus = r.WikidataStatus,
        RetailMatch = r.RetailMatch,
        RetailMatchDetail = r.RetailMatchDetail,
        WikidataMatch = r.WikidataMatch,
        CreatedAt = r.CreatedAt,
        FileName = r.FileName,
        FilePath = r.FilePath,
        FileSizeBytes = r.FileSizeBytes,
        ReviewItemId = r.ReviewItemId,
        HasUserLocks = r.HasUserLocks,
        HeroUrl = r.HeroUrl,
        MissingUniverse = r.MissingUniverse,
        PipelineStep = r.PipelineStep,
        LibraryVisibility = r.LibraryVisibility,
        IsReadyForLibrary = r.IsReadyForLibrary,
        ArtworkState = r.ArtworkState,
        ArtworkSource = r.ArtworkSource,
        ArtworkSettledAt = r.ArtworkSettledAt,
        FailedProviderName = r.FailedProviderName,
    };

    private LibraryStatus ComputeLibraryStatus()
    {
        if (string.Equals(Status, "WaitingForProvider", StringComparison.OrdinalIgnoreCase))
        {
            return LibraryStatus.WaitingForProvider;
        }

        if (string.Equals(Status, "Rejected", StringComparison.OrdinalIgnoreCase))
        {
            return LibraryStatus.Quarantined;
        }

        if (NeedsReview())
        {
            return LibraryStatus.NeedsReview;
        }

        if (string.Equals(Status, "RetailMatched", StringComparison.OrdinalIgnoreCase))
        {
            return LibraryStatus.RetailMatched;
        }

        if (string.Equals(Status, "Provisional", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Status, "AwaitingStage2", StringComparison.OrdinalIgnoreCase)
            || string.Equals(LibraryVisibility, "hidden", StringComparison.OrdinalIgnoreCase))
        {
            return LibraryStatus.Provisional;
        }

        if (HasValidWikidataQid() || IsReadyForLibrary || HasUserLocks)
        {
            return LibraryStatus.Verified;
        }

        return LibraryStatus.Provisional;
    }

    private string? ComputeResolutionSummary()
    {
        if (!HasValidWikidataQid())
        {
            return null;
        }

        if (!string.IsNullOrEmpty(RetailMatchDetail) && RetailMatchDetail.Contains("ISBN", StringComparison.OrdinalIgnoreCase))
        {
            return $"ISBN -> {WikidataQid}";
        }

        if (!string.IsNullOrEmpty(RetailMatchDetail) && RetailMatchDetail.Contains("TMDB", StringComparison.OrdinalIgnoreCase))
        {
            return $"TMDB -> {WikidataQid}";
        }

        if (!string.IsNullOrEmpty(RetailMatchDetail) && RetailMatchDetail.Contains("ASIN", StringComparison.OrdinalIgnoreCase))
        {
            return $"ASIN -> {WikidataQid}";
        }

        if (HasRetailMatch())
        {
            return $"{RetailMatch} -> {WikidataQid}";
        }

        return $"Title search -> {WikidataQid}";
    }

    private string? ComputeSpecs()
    {
        var type = MediaType?.ToLowerInvariant();

        switch (type)
        {
            case "movies" or "tv":
                if (!string.IsNullOrEmpty(FileName))
                {
                    var fn = FileName.ToUpperInvariant();
                    var parts = new List<string>();

                    if (fn.Contains("2160P") || fn.Contains("4K") || fn.Contains("UHD"))
                    {
                        parts.Add("4K");
                    }
                    else if (fn.Contains("1080P") || fn.Contains("1080I"))
                    {
                        parts.Add("1080p");
                    }
                    else if (fn.Contains("720P"))
                    {
                        parts.Add("720p");
                    }
                    else if (fn.Contains("480P") || fn.Contains("SD"))
                    {
                        parts.Add("SD");
                    }

                    if (fn.Contains("REMUX"))
                    {
                        parts.Add("REMUX");
                    }
                    else if (fn.Contains("BLURAY") || fn.Contains("BLU-RAY"))
                    {
                        parts.Add("Blu-ray");
                    }
                    else if (fn.Contains("WEBDL") || fn.Contains("WEB-DL"))
                    {
                        parts.Add("WEB-DL");
                    }
                    else if (fn.Contains("WEBRIP") || fn.Contains("WEB-RIP"))
                    {
                        parts.Add("WEBRip");
                    }
                    else if (fn.Contains("HDTV"))
                    {
                        parts.Add("HDTV");
                    }

                    if (fn.Contains("HEVC") || fn.Contains("X265") || fn.Contains("H265") || fn.Contains("H.265"))
                    {
                        parts.Add("HEVC");
                    }
                    else if (fn.Contains("X264") || fn.Contains("H264") || fn.Contains("H.264") || fn.Contains("AVC"))
                    {
                        parts.Add("H.264");
                    }

                    if (parts.Count > 0)
                    {
                        return string.Join(" ", parts);
                    }
                }
                return null;

            case "music":
                if (!string.IsNullOrEmpty(FileName))
                {
                    var ext = Path.GetExtension(FileName).ToUpperInvariant();
                    return ext switch
                    {
                        ".FLAC" => "FLAC",
                        ".ALAC" or ".M4A" => "ALAC",
                        ".MP3" => "MP3",
                        ".WAV" => "WAV",
                        ".OGG" => "OGG",
                        ".AAC" => "AAC",
                        ".OPUS" => "Opus",
                        _ => ext.TrimStart('.'),
                    };
                }
                return null;

            case "audiobooks":
                if (!string.IsNullOrEmpty(FileName))
                {
                    var ext = Path.GetExtension(FileName).ToUpperInvariant();
                    return ext switch
                    {
                        ".M4B" => "M4B",
                        ".MP3" => "MP3",
                        ".M4A" => "M4A",
                        _ => ext.TrimStart('.'),
                    };
                }
                return null;

            case "books":
                if (!string.IsNullOrEmpty(FileName))
                {
                    var ext = Path.GetExtension(FileName).ToUpperInvariant();
                    return ext switch
                    {
                        ".EPUB" => "EPUB",
                        ".PDF" => "PDF",
                        ".MOBI" => "MOBI",
                        ".AZW3" => "AZW3",
                        ".CBZ" => "CBZ",
                        _ => ext.TrimStart('.'),
                    };
                }
                return null;

            case "comics":
                if (!string.IsNullOrEmpty(FileName))
                {
                    var ext = Path.GetExtension(FileName).ToUpperInvariant();
                    return ext switch
                    {
                        ".CBZ" => "CBZ",
                        ".CBR" => "CBR",
                        ".PDF" => "PDF",
                        _ => ext.TrimStart('.'),
                    };
                }
                return null;

            default:
                return null;
        }
    }

    private LibraryPipelineStage ComputeRetailStage()
    {
        if (IsRetailFailure())
        {
            return new LibraryPipelineStage { State = LibraryStageState.Failed, Label = "Retail: No match" };
        }

        if (IsRetailReview())
        {
            return new LibraryPipelineStage { State = LibraryStageState.Warning, Label = "Retail: Needs review" };
        }

        if (HasRetailMatch())
        {
            var label = !string.IsNullOrWhiteSpace(RetailMatchDetail)
                ? $"Retail: {RetailMatchDetail}"
                : "Retail: Matched";
            return new LibraryPipelineStage { State = LibraryStageState.Completed, Label = label };
        }

        if (string.Equals(Status, "WaitingForProvider", StringComparison.OrdinalIgnoreCase)
            || string.Equals(PipelineStep, "Retail", StringComparison.OrdinalIgnoreCase))
        {
            return new LibraryPipelineStage { State = LibraryStageState.Running, Label = "Retail: In progress" };
        }

        return new LibraryPipelineStage { State = LibraryStageState.Pending, Label = "Retail: Pending" };
    }

    private LibraryPipelineStage ComputeWikidataStage()
    {
        if (HasValidWikidataQid())
        {
            return new LibraryPipelineStage { State = LibraryStageState.Completed, Label = $"Wikidata: {WikidataQid}" };
        }

        if (IsQidNoMatch())
        {
            return new LibraryPipelineStage { State = LibraryStageState.Warning, Label = "Wikidata: No match" };
        }

        if (IsWikidataReview())
        {
            return new LibraryPipelineStage { State = LibraryStageState.Warning, Label = "Wikidata: Needs review" };
        }

        if (HasRetailMatch()
            && (string.Equals(PipelineStep, "Wikidata", StringComparison.OrdinalIgnoreCase)
                || string.Equals(PipelineStep, "Enrichment", StringComparison.OrdinalIgnoreCase)
                || string.Equals(PipelineStep, "Complete", StringComparison.OrdinalIgnoreCase)))
        {
            return new LibraryPipelineStage { State = LibraryStageState.Running, Label = "Wikidata: Resolving" };
        }

        return new LibraryPipelineStage { State = LibraryStageState.Pending, Label = "Wikidata: Pending" };
    }

    private LibraryPipelineStage ComputeEnrichmentStage()
    {
        if (string.Equals(ArtworkState, "present", StringComparison.OrdinalIgnoreCase))
        {
            var label = !string.IsNullOrEmpty(UniverseName)
                ? $"Enrichment: {UniverseName}"
                : "Enrichment: Artwork ready";
            return new LibraryPipelineStage { State = LibraryStageState.Completed, Label = label };
        }

        if (string.Equals(ArtworkState, "missing", StringComparison.OrdinalIgnoreCase))
        {
            return new LibraryPipelineStage { State = LibraryStageState.Warning, Label = "Enrichment: No artwork found" };
        }

        if (HasRetailMatch()
            || HasValidWikidataQid()
            || string.Equals(PipelineStep, "Enrichment", StringComparison.OrdinalIgnoreCase)
            || string.Equals(PipelineStep, "Complete", StringComparison.OrdinalIgnoreCase))
        {
            return new LibraryPipelineStage { State = LibraryStageState.Running, Label = "Enrichment: Pending artwork" };
        }

        return new LibraryPipelineStage { State = LibraryStageState.Pending, Label = "Enrichment: Pending" };
    }

    private LibraryPipelineStage ComputeIdentifiedStage()
    {
        var retail = ComputeRetailStage();
        var wikidata = ComputeWikidataStage();

        if (retail.State == LibraryStageState.Failed || wikidata.State == LibraryStageState.Failed)
        {
            var failedLabel = retail.State == LibraryStageState.Failed ? retail.Label : wikidata.Label;
            return new LibraryPipelineStage { State = LibraryStageState.Failed, Label = failedLabel };
        }

        if (wikidata.State == LibraryStageState.Completed)
        {
            return new LibraryPipelineStage { State = LibraryStageState.Completed, Label = wikidata.Label };
        }

        if (retail.State == LibraryStageState.Completed
            && (wikidata.State == LibraryStageState.Pending || wikidata.State == LibraryStageState.Running))
        {
            return new LibraryPipelineStage { State = LibraryStageState.Running, Label = "Retail matched -> awaiting Wikidata" };
        }

        if (retail.State == LibraryStageState.Warning || wikidata.State == LibraryStageState.Warning)
        {
            var warnLabel = retail.State == LibraryStageState.Warning ? retail.Label : wikidata.Label;
            return new LibraryPipelineStage { State = LibraryStageState.Warning, Label = warnLabel };
        }

        return new LibraryPipelineStage { State = LibraryStageState.Pending, Label = "Not yet identified" };
    }

    private LibraryPipelineStage ComputeEnrichedStage()
    {
        var enrichment = ComputeEnrichmentStage();
        return enrichment.State switch
        {
            LibraryStageState.Completed => new LibraryPipelineStage { State = LibraryStageState.Completed, Label = enrichment.Label },
            LibraryStageState.Running => new LibraryPipelineStage { State = LibraryStageState.Running, Label = "Enrichment in progress" },
            LibraryStageState.Warning => new LibraryPipelineStage { State = LibraryStageState.Warning, Label = enrichment.Label },
            LibraryStageState.Failed => new LibraryPipelineStage { State = LibraryStageState.Failed, Label = enrichment.Label },
            _ => new LibraryPipelineStage { State = LibraryStageState.Pending, Label = "Enrichment pending" },
        };
    }

    private bool HasValidWikidataQid() =>
        !string.IsNullOrEmpty(WikidataQid)
        && !WikidataQid.StartsWith("NF", StringComparison.OrdinalIgnoreCase);

    private bool HasRetailMatch() =>
        string.Equals(RetailMatch, "matched", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "RetailMatched", StringComparison.OrdinalIgnoreCase)
        || HasValidWikidataQid()
        || IsQidNoMatch();

    private bool IsRetailFailure() =>
        string.Equals(RetailMatch, "failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ReviewTrigger, "RetailMatchFailed", StringComparison.OrdinalIgnoreCase);

    private bool IsRetailReview() =>
        ReviewItemId.HasValue
        && ReviewTrigger is "RetailMatchFailed";

    private bool IsWikidataReview() =>
        ReviewItemId.HasValue
        && ReviewTrigger is "MissingQid" or "MultipleQidMatches" or "WikidataBridgeFailed";

    private bool IsQidNoMatch() =>
        string.Equals(Status, "QidNoMatch", StringComparison.OrdinalIgnoreCase)
        || string.Equals(WikidataMatch, "failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(WikidataStatus, "missing", StringComparison.OrdinalIgnoreCase);

    private bool NeedsReview() =>
        ReviewItemId.HasValue
        || string.Equals(Status, "InReview", StringComparison.OrdinalIgnoreCase)
        || string.Equals(LibraryVisibility, "review_only", StringComparison.OrdinalIgnoreCase);

    private string ComputeReadinessLabel()
    {
        if (NeedsReview())
        {
            return "Needs review";
        }

        if (string.Equals(ArtworkState, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return "Pending artwork";
        }

        if (IsReadyForLibrary && string.Equals(LibraryVisibility, "visible", StringComparison.OrdinalIgnoreCase))
        {
            return "Ready";
        }

        if (string.Equals(LibraryVisibility, "hidden", StringComparison.OrdinalIgnoreCase))
        {
            return "Hidden";
        }

        return "Pending";
    }
}

/// <summary>Filter state for the library toolbar.</summary>
public sealed class LibraryFilterState
{
    public string? SearchText { get; set; }
    public string SortBy { get; set; } = "newest";
    public string GroupBy { get; set; } = "none";
    public string ViewMode { get; set; } = "list";
    public string? MediaTypeFilter { get; set; }
    public string? StatusFilter { get; set; }
}
