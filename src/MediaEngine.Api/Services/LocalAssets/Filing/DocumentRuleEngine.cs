using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.LocalAssets.Filing;

/// <summary>
/// Decides which auto-filing rules match a document and what they ask for together. Plain code with no data store:
/// the same rules and the same document always give the same plan. Matching ignores letter case and never uses
/// patterns, so a rule only ever compares words and names.
/// </summary>
public static class DocumentRuleEngine
{
    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase) { ".pdf" };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".tif", ".tiff", ".bmp", ".avif",
    };

    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".dot", ".dotx", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".pages", ".numbers", ".key",
    };

    /// <summary>pdf, image or office for a file name, or null for any other kind of file.</summary>
    public static string? FileKindOf(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (PdfExtensions.Contains(extension))
        {
            return ViewDocumentFileKinds.Pdf;
        }

        if (ImageExtensions.Contains(extension))
        {
            return ViewDocumentFileKinds.Image;
        }

        return OfficeExtensions.Contains(extension) ? ViewDocumentFileKinds.Office : null;
    }

    /// <summary>True when the rule's conditions hold for the document. A rule with no conditions matches nothing.</summary>
    public static bool Matches(DocumentRule rule, DocumentRuleSubject subject)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(subject);
        if (rule.Conditions.Count == 0)
        {
            return false;
        }

        return string.Equals(rule.MatchMode, ViewDocumentRuleMatchModes.Any, StringComparison.Ordinal)
            ? rule.Conditions.Any(condition => Holds(condition, subject))
            : rule.Conditions.All(condition => Holds(condition, subject));
    }

    private static bool Holds(DocumentRuleCondition condition, DocumentRuleSubject subject)
    {
        var value = condition.Value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        switch (condition.Kind)
        {
            case ViewDocumentRuleConditionKinds.TextContains:
                // A document with no words (a photo, or nothing read yet) never matches a words rule.
                return subject.Text is not null && subject.Text.Contains(value, StringComparison.OrdinalIgnoreCase);
            case ViewDocumentRuleConditionKinds.FilenameContains:
                return subject.FileName.Contains(value, StringComparison.OrdinalIgnoreCase);
            case ViewDocumentRuleConditionKinds.FolderContains:
                return NormaliseSeparators(subject.FolderPath).Contains(NormaliseSeparators(value), StringComparison.OrdinalIgnoreCase);
            case ViewDocumentRuleConditionKinds.Source:
                return Guid.TryParse(value, out var sourceId) && subject.SourceIds.Contains(sourceId);
            case ViewDocumentRuleConditionKinds.FileKind:
                return string.Equals(FileKindOf(subject.FileName), value, StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    private static string NormaliseSeparators(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Runs every enabled rule in the given order. Tags from all matching rules are added together; for the
    /// correspondent, the type and each custom field the first matching rule that sets it wins. A document is filed
    /// when any matching rule says so.
    /// </summary>
    public static DocumentRulePlan Plan(IReadOnlyList<DocumentRule> rules, DocumentRuleSubject subject)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var tags = new List<Guid>();
        Guid? correspondent = null;
        Guid? type = null;
        var fields = new List<DocumentRuleFieldValue>();
        var fileNow = false;
        foreach (var rule in rules)
        {
            if (!rule.Enabled || !Matches(rule, subject))
            {
                continue;
            }

            fileNow |= rule.FilesAutomatically;
            foreach (var action in rule.Actions)
            {
                switch (action.Kind)
                {
                    case ViewDocumentRuleActionKinds.AddTag when action.RefId is { } tagId:
                        if (!tags.Contains(tagId))
                        {
                            tags.Add(tagId);
                        }

                        break;
                    case ViewDocumentRuleActionKinds.SetCorrespondent when action.RefId is { } correspondentId:
                        correspondent ??= correspondentId;
                        break;
                    case ViewDocumentRuleActionKinds.SetDocumentType when action.RefId is { } typeId:
                        type ??= typeId;
                        break;
                    case ViewDocumentRuleActionKinds.SetField when action.RefId is { } fieldId:
                        if (fields.All(field => field.FieldId != fieldId))
                        {
                            fields.Add(new DocumentRuleFieldValue(fieldId, action.TextValue));
                        }

                        break;
                }
            }
        }

        return new DocumentRulePlan(tags, correspondent, type, fields, fileNow);
    }
}
