using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using DayMonth = MediaEngine.Domain.Models.DayMonth;

namespace MediaEngine.Web.Components.Collections;

public partial class CollectionEditorShell
{
    // Display options (Collections and lists spec D11 and D12): minimum items, "at most N", daily random order and a
    // seasonal window. Order and "at most" are drawn by the rules builder for a rule-driven collection and by the Display
    // options panel (Details, and the last step of the New collection flow) otherwise.
    private int _minItems;
    private int _maxItems;
    private bool _randomOrder;
    private bool _seasonal;
    private int _seasonFromMonth = 10;
    private int _seasonFromDay = 1;
    private int _seasonToMonth = 11;
    private int _seasonToDay = 1;
    private readonly HashSet<string> _showOn = new(StringComparer.Ordinal);
    private bool _showOnLoaded;
    private static readonly (string Key, string Label)[] ShowOnPlaces =
        [("home", "Home"), ("watch", "Watch"), ("read", "Read"), ("listen", "Listen")];

    // Show on (Collections and lists spec D9): the places this collection is a lane. A new collection starts shown nowhere.
    // When an existing collection's places cannot be read, nothing is sent on save, so a failed read never clears them.
    private async Task LoadShowOnAsync()
    {
        if (EditingCollection is null)
        {
            _showOnLoaded = true;
            return;
        }

        if (await ApiClient.GetCollectionShowOnAsync(EditingCollection.Id) is { } places)
        {
            _showOn.UnionWith(places);
            _showOnLoaded = true;
        }
    }

    private Task OnShowOnChanged(string place, bool selected)
    {
        if (selected)
        {
            _showOn.Add(place);
        }
        else
        {
            _showOn.Remove(place);
        }

        MarkDirty();
        return Task.CompletedTask;
    }

    private Task SetAudienceProfileAsync(Guid profileId, bool selected)
    {
        if (selected)
        {
            _selectedProfileIds.Add(profileId);
        }
        else
        {
            _selectedProfileIds.Remove(profileId);
        }

        MarkDirty();
        return Task.CompletedTask;
    }

    private static readonly string[] MonthNames =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    private static string SeasonText(int month, int day) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{month:00}-{day:00}");

    private static string? MonthLabel(string? month) =>
        int.TryParse(month, out var number) && number is >= 1 and <= 12 ? MonthNames[number - 1] : month;

    private void LoadSeasonWindow(string? from, string? to)
    {
        if (!DayMonth.TryParse(from, out var start) || !DayMonth.TryParse(to, out var end))
        {
            return;
        }

        _seasonal = true;
        (_seasonFromMonth, _seasonFromDay) = (start.Month, start.Day);
        (_seasonToMonth, _seasonToDay) = (end.Month, end.Day);
    }

    /// <summary>"Hidden until 1 October" while the saved collection is outside its window, else null.</summary>
    private string? HiddenUntilNote =>
        DateOnly.TryParseExact(EditingCollection?.HiddenUntil, "yyyy-MM-dd", out var until)
            ? $"Hidden until {until.Day} {MonthNames[until.Month - 1]}. It stays here so you can edit it, but lanes and the Collections page skip it until then."
            : null;

    private Task OnMinItemsChanged(int value)
    {
        _minItems = Math.Max(0, value);
        MarkDirty();
        return Task.CompletedTask;
    }

    private Task OnMaxItemsChanged(int value)
    {
        _maxItems = Math.Max(0, value);
        MarkDirty();
        return Task.CompletedTask;
    }

    private Task OnRandomOrderChanged(string? value)
    {
        _randomOrder = string.Equals(value, "random", StringComparison.Ordinal);
        MarkDirty();
        return Task.CompletedTask;
    }

    private Task OnSeasonalChanged(bool value)
    {
        _seasonal = value;
        MarkDirty();
        return Task.CompletedTask;
    }

    private Task OnSeasonEdgeChanged(Action apply)
    {
        apply();
        MarkDirty();
        return Task.CompletedTask;
    }

    private Task OnNameChanged(string value)
    {
        _name = value;
        _suggestedName = null;
        _nameMissing = false;
        MarkDirty();
        return Task.CompletedTask;
    }

    private Task OnDescriptionChanged(string? value)
    {
        _description = value;
        MarkDirty();
        return Task.CompletedTask;
    }

    private Task OnIconChanged(string value)
    {
        _iconName = value;
        MarkDirty();
        return Task.CompletedTask;
    }

    private static string DefaultIconForCollectionType(string collectionType) => collectionType switch
    {
        "Smart" => AppMaterialIcons.Outlined.AutoAwesomeMotion,
        "Playlist" => AppMaterialIcons.Outlined.QueueMusic,
        "Mix" => AppMaterialIcons.Outlined.GraphicEq,
        "System" => AppMaterialIcons.Outlined.SettingsSuggest,
        _ => AppMaterialIcons.Outlined.Collections,
    };

    private static string? IconLabelFor(string? icon) =>
        IconChoices.FirstOrDefault(choice => string.Equals(choice.Icon, icon, StringComparison.Ordinal)).Label
        ?? "Collection";

    // Library ownership ("Share with everyone") and Selected profiles are administrator choices (Collections and lists D19). Anyone else
    // gets no choice to make: what they create is theirs alone. The Engine refuses the sharing choices from a non-administrator anyway.
    private bool CanShare => ServiceProvider.GetService<DashboardSessionAccessor>() is { } session && CollectionAuthoringRule.CanShare(session);

    private bool OwnerOnlyPanel => !CanShare && _ownerKind == "Profile" && _audience == "Private";
}
