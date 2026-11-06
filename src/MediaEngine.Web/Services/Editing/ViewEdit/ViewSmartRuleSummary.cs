using System.Globalization;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Says a Smart Gallery's saved rules in plain English ("Kind is Photo and Tag is Lisbon") for its page, instead of printing the stored rule text
/// (spec view-section WP5).
/// </summary>
public static class ViewSmartRuleSummary
{
    public const string Unreadable = "These rules could not be read.";
    public const string NoRules = "No rules yet.";

    public static string Describe(string? ruleJson)
    {
        if (string.IsNullOrWhiteSpace(ruleJson))
        {
            return NoRules;
        }

        CollectionRuleDefinition definition;
        try
        {
            definition = ViewSmartGalleryRules.Parse(ruleJson);
        }
        catch (ArgumentException)
        {
            // Said in words on the page; the stored text is never shown to the person.
            return Unreadable;
        }

        var groups = new List<string>();
        foreach (var group in definition.Groups.Where(group => group.Conditions.Count > 0))
        {
            var joiner = string.Equals(group.MatchMode, "any", StringComparison.OrdinalIgnoreCase) ? " or " : " and ";
            groups.Add(string.Join(joiner, group.Conditions.Select(Condition)));
        }

        return groups.Count == 0 ? NoRules : string.Join(". Or: ", groups) + ".";
    }

    private static string Condition(CollectionRulePredicate condition)
    {
        var field = Field(condition.Field);
        var values = condition.DisplayValues is { Length: > 0 } ? condition.DisplayValues
            : condition.DisplayValue is not null ? [condition.DisplayValue]
            : condition.GetEffectiveValues();
        var first = values.FirstOrDefault() ?? string.Empty;
        return condition.Op.ToLowerInvariant() switch
        {
            "eq" => $"{field} is {first}",
            "neq" => $"{field} is not {first}",
            "contains" or "like" => $"{field} contains {first}",
            "gt" => $"{field} is after {first}",
            "gte" => $"{field} is on or after {first}",
            "lt" => $"{field} is before {first}",
            "lte" => $"{field} is on or before {first}",
            "in" => $"{field} is {string.Join(" or ", values)}",
            "between" when values.Length >= 2 => $"{field} is between {values[0]} and {values[1]}",
            _ => $"{field} {condition.Op} {first}".TrimEnd(),
        };
    }

    private static string Field(string field)
    {
        if (ViewSmartGalleryRules.TryParseCustomField(field, out _))
        {
            return "A custom field";
        }

        var words = field.Replace('_', ' ').Trim();
        return words.Length == 0 ? "A field" : char.ToUpper(words[0], CultureInfo.InvariantCulture) + words[1..];
    }
}
