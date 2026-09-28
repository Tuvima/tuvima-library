namespace MediaEngine.Domain.Services;

public static class IsbnValidation
{
    public static string? NormalizeValid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        if (value.StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase)) value = value[9..];
        else if (value.StartsWith("isbn:", StringComparison.OrdinalIgnoreCase)) value = value[5..];
        value = value.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (value.Length == 13 && (value.StartsWith("978") || value.StartsWith("979"))
            && value.All(c => c is >= '0' and <= '9')
            && value.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum() % 10 == 0)
            return value;
        if (value.Length == 10 && value[..9].All(c => c is >= '0' and <= '9')
            && (value[9] is >= '0' and <= '9' or 'X')
            && value.Select((c, i) => (c == 'X' ? 10 : c - '0') * (10 - i)).Sum() % 11 == 0)
            return value;
        return null;
    }
}
