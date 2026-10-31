namespace MediaEngine.Contracts.Details;

/// <summary>What an Extras card opens: a document in the reader, a folder of pictures in the photo viewer, a clip in the video player.</summary>
public static class DetailExtraCardKinds
{
    public const string Document = "document";
    public const string Pictures = "pictures";
    public const string Clip = "clip";
}

/// <summary>One card in a title's Extras row. A picture folder is one card however many pictures it holds.</summary>
public sealed class DetailExtraCardViewModel
{
    /// <summary>The Extra the card opens (for a picture folder, its first picture). Open it with the Extras viewer.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>One of <see cref="DetailExtraCardKinds"/>.</summary>
    public string Kind { get; init; } = DetailExtraCardKinds.Document;

    public string Title { get; init; } = string.Empty;

    /// <summary>The line under the title: "PDF, 42 pages", "24 pictures", "12:03".</summary>
    public string? Subtitle { get; init; }

    /// <summary>How many files the card holds (more than one only for a picture folder).</summary>
    public int ItemCount { get; init; } = 1;

    /// <summary>Up to four pictures for the card's mosaic; empty for a document or a clip.</summary>
    public IReadOnlyList<string> PreviewIds { get; init; } = [];

    /// <summary>True when the file is no longer where Library found it; the card stays with a warning and "File not found".</summary>
    public bool IsMissing { get; init; }
}

/// <summary>What the Extras viewer needs to open one card: the files behind it, in order.</summary>
public sealed class ExtraViewDto
{
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = DetailExtraCardKinds.Document;
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public IReadOnlyList<ExtraViewItemDto> Items { get; init; } = [];
}

/// <summary>One file in the Extras viewer.</summary>
public sealed class ExtraViewItemDto
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>The file name with its extension, used for downloads.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>The file's media type, for example <c>application/pdf</c> or <c>video/mp4</c>.</summary>
    public string MimeType { get; init; } = "application/octet-stream";

    public bool IsMissing { get; init; }
}
