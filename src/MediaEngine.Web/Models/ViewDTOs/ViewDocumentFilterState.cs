using MediaEngine.Contracts.LocalAssets;

namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// What the Documents page has narrowed the list to: tags (all of them), a correspondent, a type, a date range and
/// custom field values. Dashboard-only; <see cref="ToDto"/> maps it to the wire filter explicitly.
/// </summary>
public sealed class ViewDocumentFilterState
{
    public HashSet<Guid> TagIds { get; } = [];
    public Guid? CorrespondentId { get; set; }
    public Guid? DocumentTypeId { get; set; }
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }

    /// <summary>A value per custom field; fields with no value are not filtered on.</summary>
    public Dictionary<Guid, string> Fields { get; } = [];

    public int ActiveCount =>
        TagIds.Count + (CorrespondentId.HasValue ? 1 : 0) + (DocumentTypeId.HasValue ? 1 : 0)
        + (DateFrom.HasValue || DateTo.HasValue ? 1 : 0) + Fields.Count;

    public bool IsActive => ActiveCount > 0;

    public void Clear()
    {
        TagIds.Clear();
        CorrespondentId = null;
        DocumentTypeId = null;
        DateFrom = null;
        DateTo = null;
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

    public ViewDocumentFilterDto? ToDto() => !IsActive
        ? null
        : new ViewDocumentFilterDto(
            TagIds.Count == 0 ? null : TagIds.ToList(),
            CorrespondentId,
            DocumentTypeId,
            DateFrom,
            DateTo,
            Fields: Fields.Count == 0
                ? null
                : Fields.Select(field => new ViewDocumentFieldFilterDto(field.Key, field.Value)).ToList());
}
