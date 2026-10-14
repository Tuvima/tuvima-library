using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// What the Documents page has narrowed the list to: a saved view, tags (all of them), a correspondent, a type, a date
/// range (fixed days or a relative range such as "last 30 days") and custom field values, plus how the list is
/// ordered. Dashboard-only; <see cref="ToDto"/> maps it to the wire filter explicitly.
/// </summary>
public sealed class ViewDocumentFilterState
{
    public HashSet<Guid> TagIds { get; } = [];
    public Guid? CorrespondentId { get; set; }
    public Guid? DocumentTypeId { get; set; }
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }

    /// <summary>
    /// A relative range from <see cref="ViewSmartGalleryRules.RelativeDateRanges"/>. It is worked out again every time the
    /// list is loaded, and a saved view keeps it as a range, not as the days it happened to cover.
    /// </summary>
    public string? DatePreset { get; set; }

    /// <summary>The saved view the list starts from; every other filter narrows it further.</summary>
    public Guid? SavedViewId { get; set; }

    public string? SavedViewName { get; set; }

    /// <summary>One of <see cref="ViewDocumentSorts"/>; null is the usual order (newest date first).</summary>
    public string? Sort { get; set; }

    /// <summary>
    /// True while the page shows only the documents waiting to be filed. It is a way of looking at the list, not a
    /// filter: it is not counted in <see cref="ActiveCount"/> and <see cref="Clear"/> leaves it as it is.
    /// </summary>
    public bool InboxOnly { get; set; }

    /// <summary>True while the page shows the trash. Like the Inbox, a way of looking at the list rather than a filter.</summary>
    public bool TrashOnly { get; set; }

    /// <summary>A value per custom field; fields with no value are not filtered on.</summary>
    public Dictionary<Guid, string> Fields { get; } = [];

    public int ActiveCount =>
        TagIds.Count + (CorrespondentId.HasValue ? 1 : 0) + (DocumentTypeId.HasValue ? 1 : 0)
        + (DateFrom.HasValue || DateTo.HasValue || DatePreset is not null ? 1 : 0) + Fields.Count
        + (SavedViewId.HasValue ? 1 : 0);

    public bool IsActive => ActiveCount > 0;

    public void Clear()
    {
        TagIds.Clear();
        CorrespondentId = null;
        DocumentTypeId = null;
        DateFrom = null;
        DateTo = null;
        DatePreset = null;
        SavedViewId = null;
        SavedViewName = null;
        Fields.Clear();
    }

    public void SetField(Guid fieldId, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Fields.Remove(fieldId);
        }
        else
        {
            Fields[fieldId] = value.Trim();
        }
    }

    /// <summary>
    /// The filter the Engine reads. A relative date range becomes real days as of <paramref name="today"/> (the current
    /// day when left out).
    /// </summary>
    public ViewDocumentFilterDto? ToDto(DateOnly? today = null)
    {
        if (!IsActive && !InboxOnly && string.IsNullOrWhiteSpace(Sort))
        {
            return null;
        }

        var from = DateFrom;
        var to = DateTo;
        if (DatePreset is not null)
        {
            (from, to) = ViewSmartGalleryRules.ResolveRelativeRange(
                DatePreset, today ?? DateOnly.FromDateTime(DateTime.Now));
        }

        return new ViewDocumentFilterDto(
            TagIds.Count == 0 ? null : TagIds.ToList(),
            CorrespondentId,
            DocumentTypeId,
            from,
            to,
            InboxOnly ? ViewDocumentFilingStates.Inbox : null,
            Fields: Fields.Count == 0
                ? null
                : Fields.Select(field => new ViewDocumentFieldFilterDto(field.Key, field.Value)).ToList(),
            Sort: string.IsNullOrWhiteSpace(Sort) ? null : Sort);
    }
}
