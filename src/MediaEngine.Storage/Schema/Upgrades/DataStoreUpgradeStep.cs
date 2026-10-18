using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Schema.Upgrades;

/// <summary>
/// One numbered, forward-only change to the data store. Steps run exactly once, in number order,
/// each inside its own transaction, and are never edited after they have been released.
/// </summary>
internal abstract class DataStoreUpgradeStep
{
    /// <summary>Position in the single numbering shared by SQL and code steps (starts at 1).</summary>
    public abstract int Number { get; }

    /// <summary>Short lowercase name (<c>[a-z0-9]</c> words joined by underscores).</summary>
    public abstract string Name { get; }

    /// <summary>
    /// True when the step must run with SQLite foreign keys off, such as a table rebuild.
    /// The runner switches them off before the step's transaction, checks for orphaned rows
    /// before committing, and always switches them back on.
    /// </summary>
    public virtual bool RequiresForeignKeysOff => false;

    /// <summary>Lowercase hex SHA-256 recorded when the step is applied and compared on every later start.</summary>
    public abstract string Fingerprint { get; }

    /// <summary>The file or type the step came from, used to name an offending step in messages.</summary>
    public abstract string Source { get; }

    /// <summary>
    /// Makes the change on <paramref name="connection"/> inside <paramref name="transaction"/>.
    /// The runner commits or rolls back; a step must not begin, commit or roll back a transaction itself.
    /// </summary>
    public abstract void Apply(SqliteConnection connection, SqliteTransaction transaction);

    internal static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>
/// A plain SQL step. The whole script runs as one command in the runner's transaction.
/// A script declares that it needs foreign keys off with the exact first line
/// <c>-- tuvima:foreign-keys-off</c>.
/// </summary>
internal sealed class SqlDataStoreUpgradeStep : DataStoreUpgradeStep
{
    /// <summary>The first-line marker that declares a step needs foreign keys off.</summary>
    public const string ForeignKeysOffMarker = "-- tuvima:foreign-keys-off";

    private readonly string _script;

    /// <param name="number">Step number.</param>
    /// <param name="name">Step name.</param>
    /// <param name="script">The SQL text. Line endings are normalised to <c>\n</c> so CRLF and LF checkouts agree.</param>
    /// <param name="source">Embedded resource (or other) name shown in messages.</param>
    public SqlDataStoreUpgradeStep(int number, string name, string script, string source)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(source);
        Number = number;
        Name = name;
        Source = source;
        _script = script.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal);
        Fingerprint = Sha256Hex(_script);
        RequiresForeignKeysOff = FirstLine(_script).Equals(ForeignKeysOffMarker, StringComparison.Ordinal);
    }

    public override int Number { get; }

    public override string Name { get; }

    public override bool RequiresForeignKeysOff { get; }

    public override string Fingerprint { get; }

    public override string Source { get; }

    /// <summary>The normalised script text.</summary>
    public string Script => _script;

    public override void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _script;
        command.ExecuteNonQuery();
    }

    private static string FirstLine(string script)
    {
        var end = script.IndexOf('\n', StringComparison.Ordinal);
        return (end < 0 ? script : script[..end]).TrimEnd('\r');
    }
}

/// <summary>
/// A code step, for reshaping existing data that SQL cannot express cleanly. Authors subclass this
/// (internal, parameterless constructor) and the catalog discovers it by reflection.
/// </summary>
/// <remarks>
/// The fingerprint covers the number, name and type name only, so renaming or renumbering an applied
/// step is caught at startup. Edits to the body of <see cref="Apply"/> cannot be seen at run time;
/// the source-edit guardrail added alongside the generated structure snapshot covers that.
/// </remarks>
internal abstract class DataStoreCodeUpgradeStep : DataStoreUpgradeStep
{
    public sealed override string Fingerprint
        => Sha256Hex($"code:{Number}:{Name}:{GetType().FullName}");

    public sealed override string Source => GetType().FullName ?? GetType().Name;
}
