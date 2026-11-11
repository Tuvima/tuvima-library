using System.Text.Json;

namespace MediaEngine.Providers.Adapters;

/// <summary>
/// Tells an answer that holds nothing (no results, an empty list, an empty object) from one that holds a record. A provider that answers
/// "nothing found" for a title is remembered for seven days if its answer is cached, so a title added to the provider the next day stays
/// invisible; an empty answer is therefore never cached. A record is never empty: it carries at least one piece of text.
/// </summary>
internal static class EmptyProviderAnswer
{
    public static bool IsEmpty(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return !HasText(document.RootElement);
        }
        catch (JsonException)
        {
            // Not JSON: the caller's own parse decides what to do with it; it is not "empty" and is not judged here.
            return false;
        }
    }

    // Any non-blank string anywhere in the answer is content. Numbers (a page, a total of 0) and empty lists are not.
    private static bool HasText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
        JsonValueKind.Array => element.EnumerateArray().Any(HasText),
        JsonValueKind.Object => element.EnumerateObject().Any(property => HasText(property.Value)),
        _ => false,
    };
}
