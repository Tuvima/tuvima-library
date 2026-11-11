namespace MediaEngine.Web.Services.Ui;

/// <summary>
/// Holds the one row, card or tile a person just changed, so a page can glow it once (design system, Feedback after an action).
/// The page calls <see cref="Mark"/> when the change worked and puts <see cref="Class"/> on the changed element; the global
/// <c>tl-just-changed</c> animation plays when the class first appears. Marking another id moves the glow to it. Use
/// <see cref="Version"/> in the element's <c>@key</c> to play the glow again for the same id.
/// </summary>
public sealed class JustChanged
{
    /// <summary>The global class that plays the glow (wwwroot/app.css).</summary>
    public const string GlowClass = "tl-just-changed";

    private string? _id;
    private int _version;

    /// <summary>Glows this id; any earlier id stops glowing.</summary>
    public void Mark(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _id = id;
        _version++;
    }

    /// <summary>Stops the glow.</summary>
    public void Clear() => _id = null;

    /// <summary>True when this id is the one just changed.</summary>
    public bool Is(string id) => _id is not null && string.Equals(_id, id, StringComparison.Ordinal);

    /// <summary>The class to put on the element for this id, or null when it did not just change.</summary>
    public string? Class(string id) => Is(id) ? GlowClass : null;

    /// <summary>Counts every <see cref="Mark"/>; part of an element's key so the same row glows again after a second change.</summary>
    public int Version(string id) => Is(id) ? _version : 0;
}
