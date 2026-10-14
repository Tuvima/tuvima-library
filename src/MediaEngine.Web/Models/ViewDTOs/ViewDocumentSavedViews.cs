using System.Globalization;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// Turns the Documents filters into a saved view: a Smart Gallery whose rule says "these are documents" and then lists
/// each filter. The Engine reads the same rule vocabulary for every Smart Gallery, so a saved view needs no table of
/// its own and shows up in Galleries too.
/// </summary>
public static class ViewDocumentSavedViews
{
    /// <summary>The longest name a saved view can be given.</summary>
    public const int MaximumNameLength = 120;

    public static readonly IReadOnlyList<(string Value, string Label)> DatePresets =
    [
        ("last_7_days", "Last 7 days"),
        ("last_30_days", "Last 30 days"),
        ("last_90_days", "Last 90 days"),
        ("this_month", "This month"),
        ("this_year", "This year"),
        ("last_year", "Last year"),
    ];

    public static string DatePresetLabel(string value) =>
        DatePresets.FirstOrDefault(preset => preset.Value == value).Label ?? value;

    /// <summary>True when there is something worth saving: a filter, or words typed into the search box.</summary>
    public static bool CanSave(ViewDocumentFilterState state, string? search) =>
        (state.TagIds.Count > 0 || state.CorrespondentId.HasValue || state.DocumentTypeId.HasValue
            || state.DateFrom.HasValue || state.DateTo.HasValue || state.DatePreset is not null
            || state.Fields.Count > 0 || state.InboxOnly || state.SavedViewId.HasValue
            || !string.IsNullOrWhiteSpace(search))
        && !state.TrashOnly;

    /// <summary>
    /// The rule for the list as it is now. The words in the search box are saved as "text contains", and the Inbox
    /// view as "filing state is inbox". A view the list started from stays part of it only as its own filters
    /// (the saved view is not nested inside the new one).
    /// </summary>
    public static string BuildRuleJson(ViewDocumentFilterState state, string? search, ViewDocumentCatalogueDto? catalogue)
    {
        ArgumentNullException.ThrowIfNull(state);
        var conditions = new List<CollectionRulePredicate>
        {
            new() { Field = "media_type", Op = "eq", Value = "document" },
        };
        if (state.TagIds.Count > 0)
        {
            conditions.Add(new() { Field = "tags", Op = "all", Values = state.TagIds.Select(id => id.ToString("D")).ToArray() });
        }

        if (state.CorrespondentId is { } correspondent)
        {
            conditions.Add(new() { Field = "correspondent", Op = "eq", Value = correspondent.ToString("D") });
        }

        if (state.DocumentTypeId is { } type)
        {
            conditions.Add(new() { Field = "document_type", Op = "eq", Value = type.ToString("D") });
        }

        if (state.DatePreset is { } preset)
        {
            conditions.Add(new() { Field = "document_date", Op = "within", Value = preset });
        }
        else if (state.DateFrom is { } from && state.DateTo is { } to)
        {
            conditions.Add(new() { Field = "document_date", Op = "between", Values = [Iso(from), Iso(to)] });
        }
        else if (state.DateFrom is { } onlyFrom)
        {
            conditions.Add(new() { Field = "document_date", Op = "gte", Value = Iso(onlyFrom) });
        }
        else if (state.DateTo is { } onlyTo)
        {
            conditions.Add(new() { Field = "document_date", Op = "lte", Value = Iso(onlyTo) });
        }

        foreach (var (fieldId, value) in state.Fields)
        {
            var kind = catalogue?.Fields.FirstOrDefault(field => field.Id == fieldId)?.Kind;
            conditions.Add(new()
            {
                Field = ViewSmartGalleryRules.CustomFieldKey(fieldId),
                Op = kind == ViewDocumentFieldKinds.Text ? "contains" : "eq",
                Value = value,
            });
        }

        if (state.InboxOnly)
        {
            conditions.Add(new() { Field = "filing_state", Op = "eq", Value = ViewDocumentFilingStates.Inbox });
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var words = search.Trim();
            conditions.Add(new()
            {
                Field = "text_contains",
                Op = "contains",
                Value = words.Length > ViewSmartGalleryRules.MaximumValueLength ? words[..ViewSmartGalleryRules.MaximumValueLength] : words,
            });
        }

        return ViewSmartGalleryRules.Serialize(new CollectionRuleDefinition
        {
            Version = ViewSmartGalleryRules.CurrentVersion,
            Groups = [new CollectionRuleGroup { MatchMode = "all", JoinWithPrevious = "and", Conditions = conditions }],
        });
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
