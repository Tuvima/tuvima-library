using System.Globalization;

namespace MediaEngine.Contracts.Editing;

/// <summary>
/// How a relationship change asks for a number instead of a move (universal editor spec D14, "Renumber sequentially"): the change's
/// <see cref="EditSessionChange.TargetId"/> names the album the item already sits in, and its <see cref="EditSessionChange.Token"/>
/// is <c>position:N</c>. A relationship change without such a token is a move into <see cref="EditSessionChange.TargetId"/>.
/// </summary>
public static class EditSessionPosition
{
    /// <summary>The token's prefix.</summary>
    public const string Prefix = "position:";

    /// <summary>The highest number a position change may ask for.</summary>
    public const int MaxNumber = 9999;

    /// <summary>The token that asks for track or episode number <paramref name="number"/>.</summary>
    public static string Token(int number) => Prefix + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>True when <paramref name="token"/> is a position token; <paramref name="number"/> is then its number (1 to <see cref="MaxNumber"/>), or 0 when the number is not valid.</summary>
    public static bool IsPosition(string? token, out int number)
    {
        number = 0;
        if (token is null || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (int.TryParse(token.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 1 and <= MaxNumber)
        {
            number = parsed;
        }

        return true;
    }
}
