using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Matching;

/// <summary>
/// Turns the evidence the existing match scoring already produced into one short sentence the editor shows under a confidence bar
/// ("Episode number and runtime match; title differs"). It adds no scoring of its own: it only words what the scoring found.
/// </summary>
public static class MatchReasonText
{
    private const int MaxNamed = 4;

    /// <summary>
    /// Builds the sentence for a single-item candidate from its per-field scores. Fields that agree are named first, then fields that
    /// are close, then fields that differ. Fields the provider or the file did not supply are left out. Null when there is nothing to say.
    /// </summary>
    public static string? FromFieldScores(IReadOnlyList<RetailFieldScore>? rows)
    {
        if (rows is null || rows.Count == 0)
        {
            return null;
        }

        var matched = new List<string>();
        var close = new List<string>();
        var differs = new List<string>();

        foreach (var row in rows)
        {
            var name = Plain(row.Label);
            switch (row.Role)
            {
                case "weighted" when row.Verdict == "exact":
                    AddOnce(matched, name);
                    break;
                case "weighted" when row.Verdict == "close":
                    AddOnce(close, name);
                    break;
                case "weighted" when row.Verdict == "mismatch":
                    AddOnce(differs, name);
                    break;
                case "gate" when row.Verdict == "fail":
                    AddOnce(differs, name);
                    break;
                case "bonus" when row.Key == "exact_id" && row.Contribution > 0:
                    matched.Remove(name);
                    matched.Insert(0, name);
                    break;
                case "bonus" when row.Contribution > 0 && !matched.Contains(name, StringComparer.OrdinalIgnoreCase)
                    && !close.Contains(name, StringComparer.OrdinalIgnoreCase):
                    // A bonus names something that agreed (a runtime within range, a cover that looks alike).
                    AddOnce(matched, name);
                    break;
            }
        }

        // A field that agrees must not also be reported as different (a bonus and a mismatching weighted row can share a label).
        differs.RemoveAll(item => matched.Contains(item, StringComparer.OrdinalIgnoreCase));
        return Compose(matched, close, differs);
    }

    /// <summary>
    /// Joins what agreed, what is close and what differs into one sentence: "Title and year match; author is close; runtime differs".
    /// An optional note is added last ("another entry looks similar"). Null when everything is empty.
    /// </summary>
    public static string? Compose(
        IReadOnlyList<string> matched,
        IReadOnlyList<string> close,
        IReadOnlyList<string> differs,
        string? note = null)
    {
        var parts = new List<string>();
        if (matched.Count > 0)
        {
            parts.Add($"{Named(matched)} {(matched.Count == 1 ? "matches" : "match")}");
        }

        if (close.Count > 0)
        {
            parts.Add($"{Named(close)} {(close.Count == 1 ? "is" : "are")} close");
        }

        if (differs.Count > 0)
        {
            parts.Add($"{Named(differs)} {(differs.Count == 1 ? "differs" : "differ")}");
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            parts.Add(note.Trim());
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var sentence = string.Join("; ", parts);
        return char.ToUpperInvariant(sentence[0]) + sentence[1..];
    }

    private static string Named(IReadOnlyList<string> names)
    {
        var shown = names.Take(MaxNamed).ToList();
        return shown.Count switch
        {
            1 => shown[0],
            2 => $"{shown[0]} and {shown[1]}",
            _ => $"{string.Join(", ", shown.Take(shown.Count - 1))} and {shown[^1]}",
        };
    }

    private static void AddOnce(List<string> list, string name)
    {
        if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(name);
        }
    }

    /// <summary>Lowercases a label for the middle of a sentence but keeps short all-capital words (ISBN, ID) as they are.</summary>
    private static string Plain(string label)
    {
        var words = (label ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(word =>
            word.Length >= 2 && word.All(char.IsUpper) ? word : word.ToLowerInvariant()));
    }
}
