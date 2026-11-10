namespace MediaEngine.Domain.Entities;

/// <summary>A bookmark on a page of a comic or PDF (PDF and comic reader spec D14).</summary>
public sealed class PageBookmark
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public Guid AssetId { get; set; }

    /// <summary>The page, starting at 1.</summary>
    public int Page { get; set; }

    public string? Label { get; set; }

    /// <summary>True when the bookmark is in Everyone's: everyone who can read the title sees it, and only an administrator changes it.</summary>
    public bool IsGlobal { get; set; }

    /// <summary>The owner's display name, set on Everyone's bookmarks that belong to someone else.</summary>
    public string? OwnerName { get; set; }

    public DateTime CreatedAt { get; set; }
}
