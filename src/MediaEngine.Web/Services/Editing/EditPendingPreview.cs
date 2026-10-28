using System.Text.RegularExpressions;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What the page shows while changes are only staged (spec D4): the hero title, year and genres and the description read the staged
/// value when there is one, so the person sees the result as they type and nothing is written until Save changes. A staged "reset to the
/// provider value" is empty, which this overlay ignores (the page keeps what it has until the save brings the provider value back).
/// </summary>
public sealed partial class EditPendingPreview
{
    private static readonly string[] TitleKeys = ["title", "show_name", "episode_title", "season_title", "issue_title", "album", "series"];
    private static readonly string[] YearKeys = ["release_date", "first_air_date", "air_date", "original_publication_date", "publication_date", "year"];
    private static readonly string[] DescriptionKeys = ["description", "episode_description", "issue_description"];

    private readonly EditSessionState _session;
    private readonly ItemFieldEditSession _fields;

    public EditPendingPreview(EditSessionState session, ItemFieldEditSession fields)
    {
        _session = session;
        _fields = fields;
        Artwork = new ArtworkEditPreview(session, fields);
    }

    /// <summary>The pictures picked or staged in the Artwork tool, so the hero and the temporary view show them before Save changes.</summary>
    public ArtworkEditPreview Artwork { get; }

    /// <summary>The title as staged, else <paramref name="current"/>.</summary>
    public string? Title(string? current)
    {
        var heading = _fields.Presentation.Heading;
        string[] keys = heading is null ? TitleKeys : [heading.OverrideKey, heading.Key, .. TitleKeys];
        return Staged(keys) ?? current;
    }

    /// <summary>The description as staged, else <paramref name="current"/>.</summary>
    public string? Description(string? current)
    {
        var synopsis = _fields.Presentation.Synopsis;
        string[] keys = synopsis is null ? DescriptionKeys : [synopsis.OverrideKey, synopsis.Key, .. DescriptionKeys];
        return Staged(keys) ?? current;
    }

    /// <summary>The tagline as staged, else <paramref name="current"/>.</summary>
    public string? Tagline(string? current) => Staged("tagline") ?? current;

    /// <summary>The year as staged (taken from a staged date too), else <paramref name="current"/>.</summary>
    public string? Year(string? current)
    {
        var staged = Staged(YearKeys);
        return staged is not null && YearPattern().Match(staged) is { Success: true } match ? match.Value : current;
    }

    /// <summary>The genres as staged, else null (the page keeps its own list).</summary>
    public IReadOnlyList<string>? Genres()
    {
        var staged = Staged("genre");
        return staged is null
            ? null
            : staged.Split([',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    private string? Staged(params string[] keys)
    {
        var entityId = _fields.EntityId;
        if (!_session.IsActive || entityId == Guid.Empty)
        {
            return null;
        }

        foreach (var key in keys.Where(key => !string.IsNullOrWhiteSpace(key)))
        {
            if (_session.Pending.TryGetField(entityId, key, out var value))
            {
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }

        return null;
    }

    [GeneratedRegex(@"\d{4}")]
    private static partial Regex YearPattern();
}
