using System.Security.Cryptography;
using System.Text;

namespace MediaEngine.Identity;

/// <summary>
/// The six-digit codes an authenticator app shows (RFC 6238, built on RFC 4226): HMAC-SHA1, 30-second steps, 6 digits.
/// Small on purpose, so there is no extra library to trust and every rule is visible in one place.
/// </summary>
public static class TotpGenerator
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    public const int SecretBytes = 20;

    /// <summary>How many steps either side of now still count (clocks are never perfectly in step).</summary>
    public const int AllowedStepDrift = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>A new random authenticator key.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>The 30-second step number that contains <paramref name="moment"/>.</summary>
    public static long StepAt(DateTimeOffset moment) => moment.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code for one step. <paramref name="digits"/> is 6 for the apps people use; the RFC test vectors use 8.</summary>
    public static string Compute(ReadOnlySpan<byte> secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];
        var modulus = 1;
        for (var i = 0; i < digits; i++)
        {
            modulus *= 10;
        }

        return (binary % modulus).ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step <paramref name="code"/> matches within the allowed drift and after <paramref name="lastUsedStep"/>, or
    /// <c>null</c>. A code for a step that was already used is refused, so a code works once.
    /// </summary>
    public static long? Match(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        var digitsOnly = Normalize(code);
        if (digitsOnly is null)
        {
            return null;
        }

        var current = StepAt(now);
        long? matched = null;
        // Every candidate is compared, in constant time, so timing does not say which step was closest.
        for (var offset = -AllowedStepDrift; offset <= AllowedStepDrift; offset++)
        {
            var step = current + offset;
            var expected = Encoding.ASCII.GetBytes(Compute(secret, step));
            if (CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(digitsOnly)) && step > lastUsedStep)
            {
                matched = matched is null || step > matched ? step : matched;
            }
        }

        return matched;
    }

    /// <summary>The six digits without spaces or dashes, or <c>null</c> when it cannot be a code.</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var digits = new StringBuilder(Digits);
        foreach (var character in code)
        {
            if (character is ' ' or '-')
            {
                continue;
            }

            if (!char.IsAsciiDigit(character))
            {
                return null;
            }

            digits.Append(character);
        }

        return digits.Length == Digits ? digits.ToString() : null;
    }

    public static string ToBase32(ReadOnlySpan<byte> bytes)
    {
        var result = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
                buffer &= (1 << bits) - 1;
            }
        }

        if (bits > 0)
        {
            result.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return result.ToString();
    }

    public static byte[] FromBase32(string text)
    {
        var bytes = new List<byte>(text.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var raw in text)
        {
            if (raw is ' ' or '-' or '=')
            {
                continue;
            }

            var index = Base32Alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (index < 0)
            {
                throw new FormatException("Not a valid authenticator key.");
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
                buffer &= (1 << bits) - 1;
            }
        }

        return [.. bytes];
    }

    /// <summary>The <c>otpauth://</c> link an authenticator app reads from a QR code.</summary>
    public static string BuildUri(string issuer, string accountLabel, string base32Secret)
    {
        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(accountLabel);
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }
}
