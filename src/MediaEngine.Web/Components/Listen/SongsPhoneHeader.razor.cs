using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Components.Listen;

/// <summary>
/// The phone Songs page's header (spec music-table-and-reader-frame D21): search, Play, Shuffle, and one Sort and filter button that
/// opens a bottom sheet. It draws and remembers the choices; the page applies them, so the sort and filter rules are the ones the
/// desktop uses.
/// </summary>
public partial class SongsPhoneHeader
{
    private static readonly AppDialogOptions SheetOptions = new() { NoHeader = true, MaxWidth = AppMaxWidth.Small, FullWidth = true, CloseOnEscapeKey = true, BackdropClick = true, Sheet = AppDialogSheetMode.Phone };

    private bool _open;
    private string? _text;
    private string? _textFromPage;
    private Guid? _loadedFor;

    [Inject] private IServiceProvider Services { get; set; } = default!;

    /// <summary>The signed-in profile; with it the chosen sort is remembered for the profile.</summary>
    [Parameter] public Guid? ProfileId { get; set; }

    /// <summary>The page's search text. Typing raises <see cref="SearchTextChanged"/> once the person pauses.</summary>
    [Parameter] public string? SearchText { get; set; }

    [Parameter] public EventCallback<string> SearchTextChanged { get; set; }

    /// <summary>The sort the list is in. A remembered sort is raised through <see cref="SortChanged"/> when the profile's choice loads.</summary>
    [Parameter] public SongSortState? Sort { get; set; }

    [Parameter] public EventCallback<SongSortState> SortChanged { get; set; }

    /// <summary>The Favorites filter, offered only when <see cref="FavoritesOnlyChanged"/> is wired.</summary>
    [Parameter] public bool FavoritesOnly { get; set; }

    [Parameter] public EventCallback<bool> FavoritesOnlyChanged { get; set; }

    /// <summary>The genres to choose from, and the ones chosen. The genre filter is offered only when there are genres.</summary>
    [Parameter] public IReadOnlyList<string> Genres { get; set; } = [];

    [Parameter] public IReadOnlyList<string> SelectedGenres { get; set; } = [];

    [Parameter] public EventCallback<IReadOnlyList<string>> SelectedGenresChanged { get; set; }

    /// <summary>True while there is nothing to play (an empty or filtered-out list).</summary>
    [Parameter] public bool PlayDisabled { get; set; }

    [Parameter] public EventCallback OnPlay { get; set; }

    [Parameter] public EventCallback OnShuffle { get; set; }

    private int ActiveFilterCount => (FavoritesOnly ? 1 : 0) + SelectedGenres.Count;

    private string SortButtonLabel => ActiveFilterCount > 0
        ? L["SongsHeader_SortAndFilterActive", ActiveFilterCount].Value
        : L["SongsHeader_SortAndFilter"].Value;

    protected override void OnParametersSet()
    {
        if (!string.Equals(SearchText, _textFromPage, StringComparison.Ordinal))
        {
            // The page changed the text (Clear filters, a new address): show it.
            _textFromPage = SearchText;
            _text = SearchText;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (ProfileId is not { } profile || _loadedFor == profile || Services.GetService(typeof(ISongSortPreferenceStore)) is not ISongSortPreferenceStore store)
        {
            return;
        }

        _loadedFor = profile;
        if (await store.LoadAsync(profile) is { } remembered && remembered != Sort)
        {
            await SortChanged.InvokeAsync(remembered);
        }
    }

    private string ColumnLabel(SongColumn column) => column switch
    {
        SongColumn.Song => L["Common_Title"].Value,
        SongColumn.Artist => L["ListenPage_Artist"].Value,
        SongColumn.Album => L["ListenPage_Album"].Value,
        SongColumn.DateAdded => L["SongTable_DateAdded"].Value,
        SongColumn.Plays => L["SongTable_Plays"].Value,
        SongColumn.Time => L["Common_Time"].Value,
        _ => string.Empty,
    };

    private void OnTyped(string? value) => _text = value;

    private async Task OnSearchElapsed(string value)
    {
        _text = value;
        _textFromPage = value;
        await SearchTextChanged.InvokeAsync(value);
    }

    private Task OpenAsync()
    {
        _open = true;
        return Task.CompletedTask;
    }

    private Task CloseAsync()
    {
        _open = false;
        return Task.CompletedTask;
    }

    // The first click on a column goes the way the desktop header does (Date added, Plays and Time high to low); the direction buttons flip it.
    private Task ChooseColumnAsync(SongColumn column)
        => ApplySortAsync(Sort?.Column == column ? Sort : new SongSortState(column, SongTableSort.StartsDescending(column)));

    private const string AscendingValue = "ascending";
    private const string DescendingValue = "descending";

    private IReadOnlyList<AppSelectOption> DirectionOptions =>
    [
        new(AscendingValue, L["SongsHeader_Ascending"].Value, AppMaterialIcons.Outlined.ArrowUpward),
        new(DescendingValue, L["SongsHeader_Descending"].Value, AppMaterialIcons.Outlined.ArrowDownward),
    ];

    private Task ChooseDirectionAsync(string direction)
    {
        var descending = string.Equals(direction, DescendingValue, StringComparison.Ordinal);
        var column = Sort?.Column is { } current && SongSortPreferences.PhoneSortColumns.Contains(current) ? current : SongColumn.DateAdded;
        return ApplySortAsync(new SongSortState(column, descending));
    }

    private async Task ApplySortAsync(SongSortState? state)
    {
        if (state is null)
        {
            return;
        }

        await SortChanged.InvokeAsync(state);
        if (ProfileId is { } profile && Services.GetService(typeof(ISongSortPreferenceStore)) is ISongSortPreferenceStore store)
        {
            await store.SaveAsync(profile, state);
        }
    }

    private bool IsGenreSelected(string genre) => SelectedGenres.Contains(genre, StringComparer.OrdinalIgnoreCase);

    private Task ToggleGenreAsync(string genre, bool on)
    {
        var next = SelectedGenres.Where(value => !string.Equals(value, genre, StringComparison.OrdinalIgnoreCase)).ToList();
        if (on)
        {
            next.Add(genre);
        }

        return SelectedGenresChanged.InvokeAsync(next);
    }
}
