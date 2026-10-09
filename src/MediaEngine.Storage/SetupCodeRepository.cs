using System.Security.Cryptography;
using System.Text;
using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>What happened when a setup code was offered.</summary>
public enum SetupCodeCheck
{
    /// <summary>The code matched the active code, which is now used up.</summary>
    Accepted,

    /// <summary>The code did not match the active code.</summary>
    Wrong,

    /// <summary>There is no usable code: none was generated, or it expired, was used, or was replaced.</summary>
    NoActiveCode,

    /// <summary>The code was wrong and that was the last allowed try, so it is now invalid.</summary>
    TooManyAttempts,
}

/// <summary>
/// The short code that proves a person can reach the server itself before first-run setup starts.
/// Only a hash is stored. At most one code is active; generating a new one retires the others.
/// </summary>
public sealed class SetupCodeRepository(IDatabaseConnection database)
{
    /// <summary>No 0/O/1/I/L, so a code read off a screen cannot be mistyped.</summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 8;
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>A new random code, formatted <c>XXXX-XXXX</c>.</summary>
    public static string GenerateCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}";
    }

    /// <summary>Upper-cases and drops separators so <c>abcd efgh</c> and <c>ABCD-EFGH</c> are the same code.</summary>
    public static string Normalize(string? code)
    {
        var builder = new StringBuilder(CodeLength);
        foreach (var c in code ?? string.Empty)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    public static string Hash(string normalizedCode) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode)));

    /// <summary>Stores a new code's hash and retires every older code. Returns when it expires.</summary>
    public async Task<DateTimeOffset> IssueAsync(string code, DateTimeOffset now, CancellationToken ct)
    {
        var expires = now.Add(Lifetime);
        var hash = Hash(Normalize(code));
        await database.ExecuteWriteAsync((connection, transaction, _) =>
        {
            connection.Execute("""
                UPDATE setup_codes SET invalidated_at = @now
                WHERE consumed_at IS NULL AND invalidated_at IS NULL;
                """, new { now = now.ToString("O") }, transaction);
            connection.Execute("""
                INSERT INTO setup_codes (id, code_hash, created_at, expires_at)
                VALUES (@id, @hash, @now, @expires);
                """, new { id = Guid.NewGuid(), hash, now = now.ToString("O"), expires = expires.ToString("O") }, transaction);
        }, ct).ConfigureAwait(false);
        return expires;
    }

    /// <summary>
    /// Checks the offered code against the active one in constant time. A match uses the code up; the
    /// <see cref="MaxFailedAttempts"/>th wrong try retires it so it cannot be guessed further.
    /// </summary>
    public Task<SetupCodeCheck> VerifyAndConsumeAsync(string? offered, DateTimeOffset now, CancellationToken ct) =>
        database.ExecuteWriteAsync(
            (connection, transaction, _) => VerifyAndConsume(connection, transaction, offered, now), ct);

    /// <summary>
    /// The same check inside a transaction the caller already holds, so the code can be used up in the same
    /// commit as whatever it unlocks (starting a setup session).
    /// </summary>
    internal static SetupCodeCheck VerifyAndConsume(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string? offered,
        DateTimeOffset now)
    {
        var offeredHash = Encoding.UTF8.GetBytes(Hash(Normalize(offered)));
        {
            var active = connection.QuerySingleOrDefault<ActiveCodeRow>("""
                SELECT id AS Id, code_hash AS CodeHash, expires_at AS ExpiresAt, failed_attempts AS FailedAttempts
                FROM setup_codes
                WHERE consumed_at IS NULL AND invalidated_at IS NULL
                ORDER BY created_at DESC LIMIT 1;
                """, transaction: transaction);
            if (active is null
                || !DateTimeOffset.TryParse(active.ExpiresAt, out var expiresAt)
                || expiresAt <= now)
            {
                return SetupCodeCheck.NoActiveCode;
            }

            if (CryptographicOperations.FixedTimeEquals(offeredHash, Encoding.UTF8.GetBytes(active.CodeHash)))
            {
                connection.Execute("UPDATE setup_codes SET consumed_at = @now WHERE id = @id;",
                    new { now = now.ToString("O"), id = active.Id }, transaction);
                return SetupCodeCheck.Accepted;
            }

            var failed = active.FailedAttempts + 1;
            var exhausted = failed >= MaxFailedAttempts;
            connection.Execute("""
                UPDATE setup_codes
                SET failed_attempts = @failed,
                    invalidated_at = CASE WHEN @exhausted = 1 THEN @now ELSE invalidated_at END
                WHERE id = @id;
                """, new { failed, exhausted = exhausted ? 1 : 0, now = now.ToString("O"), id = active.Id }, transaction);
            return exhausted ? SetupCodeCheck.TooManyAttempts : SetupCodeCheck.Wrong;
        }
    }

    /// <summary>The stored hash of the newest code, for tests that check nothing readable is kept.</summary>
    public string? NewestStoredHash()
    {
        using var connection = database.CreateConnection();
        return connection.QuerySingleOrDefault<string>(
            "SELECT code_hash FROM setup_codes ORDER BY created_at DESC LIMIT 1;");
    }

    private sealed class ActiveCodeRow
    {
        public Guid Id { get; set; }
        public string CodeHash { get; set; } = string.Empty;
        public string ExpiresAt { get; set; } = string.Empty;
        public int FailedAttempts { get; set; }
    }
}
