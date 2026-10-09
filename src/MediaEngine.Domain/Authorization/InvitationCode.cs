using System.Security.Cryptography;
using System.Text;

namespace MediaEngine.Domain.Authorization;

/// <summary>
/// The short code an administrator hands to someone so they can set up their sign-in: ten characters from an
/// alphabet without look-alikes (no 0, O, 1, I or L), shown as <c>XXXXX-XXXXX</c>. Only a hash of the normalized
/// code is stored. About 50 bits, which is enough because wrong guesses are limited for callers outside the home and
/// every invitation expires.
/// </summary>
public static class InvitationCode
{
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int Length = 10;

    /// <summary>A new random code in its normalized form (no dash).</summary>
    public static string Generate()
    {
        var characters = new char[Length];
        for (var i = 0; i < characters.Length; i++)
        {
            characters[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(characters);
    }

    /// <summary>The display form, <c>XXXXX-XXXXX</c>, of a normalized code.</summary>
    public static string Format(string normalized) =>
        normalized.Length == Length ? string.Concat(normalized.AsSpan(0, 5), "-", normalized.AsSpan(5)) : normalized;

    /// <summary>
    /// Cleans up what a person typed or pasted (case, spaces, dashes) and returns the normalized code, or
    /// <see langword="null"/> when it cannot be a code.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var builder = new StringBuilder(Length);
        foreach (var character in input)
        {
            if (character == '-' || char.IsWhiteSpace(character))
            {
                continue;
            }

            var upper = char.ToUpperInvariant(character);
            if (Alphabet.IndexOf(upper) < 0 || builder.Length == Length)
            {
                return null;
            }

            builder.Append(upper);
        }

        return builder.Length == Length ? builder.ToString() : null;
    }

    /// <summary>The stored form of a normalized code.</summary>
    public static string Hash(string normalized) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
}
