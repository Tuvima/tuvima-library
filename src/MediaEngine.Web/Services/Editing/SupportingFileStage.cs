using MediaEngine.Contracts.Details;

namespace MediaEngine.Web.Services.Editing;

/// <summary>The fields of a supporting file the editor can change, as the keys a staged change and a Review row share.</summary>
public static class SupportingFileFields
{
    public const string Title = "title";
    public const string Description = "description";
    public const string Group = "group";
    public const string Role = "role";
    public const string Scope = "scope";
    public const string Alongside = "alongside";
    public const string Tags = "tags";

    /// <summary>The Review row that says a group is made when the change is saved; it goes with the group field.</summary>
    public const string CreateGroup = "group:create";

    /// <summary>The Review row for the file move a changed Belongs to makes in a Managed folder; it goes with the scope field.</summary>
    public const string Move = "scope:move";

    /// <summary>The field a row key belongs to ("group:create" is part of "group").</summary>
    public static string FieldOf(string rowKey) => rowKey switch
    {
        CreateGroup => Group,
        Move => Scope,
        _ => rowKey,
    };
}

/// <summary>One changed field of a supporting file as Review shows it: the words for the field and the before and after.</summary>
public sealed record SupportingFileStagedRow(string Key, string Label, string Before, string After);

/// <summary>
/// What the editor has staged for one supporting file (universal editor v5 WP7): the typed values that become one
/// <see cref="SupportingFileBulkChangesDto"/> at Save, and the words Review and the footer show for them. One per file; staging the file
/// again replaces it, so the list always says what Save would do. Nothing here is sent anywhere until Save changes.
/// </summary>
/// <param name="TitleId">The title (a work id) the file belongs to; the saved change names it as its entity.</param>
/// <param name="FileId">The supporting file.</param>
/// <param name="ItemName">What Review calls the file (its title when it was staged).</param>
public sealed record SupportingFileStage(Guid TitleId, Guid FileId, string ItemName)
{
    public string? Title { get; init; }

    public bool ClearTitle { get; init; }

    public string? Description { get; init; }

    public bool ClearDescription { get; init; }

    public string? GroupName { get; init; }

    public bool ClearGroup { get; init; }

    /// <summary>True when the group named is new to the title, so Save creates it (Review lists "Create group").</summary>
    public bool CreatesGroup { get; init; }

    public string? Role { get; init; }

    public string? ScopeKey { get; init; }

    public bool UseFolderLocation { get; init; }

    public string? AlongsideKey { get; init; }

    public bool NoAlongside { get; init; }

    public bool UseGuessedAlongside { get; init; }

    public IReadOnlyList<string> AddTags { get; init; } = [];

    public IReadOnlyList<string> RemoveTags { get; init; } = [];

    /// <summary>Where the Engine's check says the file is now and where it moves to on Save; both null when nothing moves (a Read-only folder, or Belongs to unchanged).</summary>
    public string? MoveFrom { get; init; }

    public string? MoveTo { get; init; }

    /// <summary>The changed fields in the order the form shows them.</summary>
    public IReadOnlyList<SupportingFileStagedRow> Rows { get; init; } = [];

    public bool HasMove => !string.IsNullOrWhiteSpace(MoveFrom) && !string.IsNullOrWhiteSpace(MoveTo);

    /// <summary>True when nothing is staged.</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>How many things Save would do for this file: one per changed field, plus the group it creates and the file it moves.</summary>
    public int ChangeCount => Rows.Count + (CreatesGroup ? 1 : 0) + (HasMove ? 1 : 0);

    public SupportingFileStagedRow? Row(string key) => Rows.FirstOrDefault(row => row.Key == key);

    /// <summary>The change the Engine saves for this file.</summary>
    public SupportingFileBulkUpdateRequest ToRequest() => new()
    {
        Ids = [FileId.ToString("D")],
        Changes = new SupportingFileBulkChangesDto
        {
            Title = ClearTitle ? null : Title,
            ClearTitle = ClearTitle,
            Description = ClearDescription ? null : Description,
            ClearDescription = ClearDescription,
            GroupName = ClearGroup ? null : GroupName,
            ClearGroup = ClearGroup,
            Role = Role,
            ScopeKey = UseFolderLocation ? null : ScopeKey,
            UseFolderLocation = UseFolderLocation,
            AlongsideKey = NoAlongside || UseGuessedAlongside ? null : AlongsideKey,
            NoAlongside = NoAlongside,
            UseGuessedAlongside = UseGuessedAlongside,
            AddTags = AddTags,
            RemoveTags = RemoveTags,
        },
    };

    /// <summary>The stage without one field (Review's remove on a row, or the form putting a field back to what it was). The move goes with Belongs to.</summary>
    public SupportingFileStage Without(string rowKey)
    {
        var field = SupportingFileFields.FieldOf(rowKey);
        var kept = Rows.Where(row => row.Key != field).ToList();
        return field switch
        {
            SupportingFileFields.Title => this with { Title = null, ClearTitle = false, Rows = kept },
            SupportingFileFields.Description => this with { Description = null, ClearDescription = false, Rows = kept },
            SupportingFileFields.Group => this with { GroupName = null, ClearGroup = false, CreatesGroup = false, Rows = kept },
            SupportingFileFields.Role => this with { Role = null, Rows = kept },
            SupportingFileFields.Scope => this with { ScopeKey = null, UseFolderLocation = false, MoveFrom = null, MoveTo = null, Rows = kept },
            SupportingFileFields.Alongside => this with { AlongsideKey = null, NoAlongside = false, UseGuessedAlongside = false, Rows = kept },
            SupportingFileFields.Tags => this with { AddTags = [], RemoveTags = [], Rows = kept },
            _ => this,
        };
    }
}
