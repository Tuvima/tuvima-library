using MediaEngine.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MediaEngine.Storage.Benchmarks.Ef;

/// <summary>
/// Guid stored as a 16-byte BLOB in RFC4122/network byte order. Calls the same <see cref="GuidSql"/> helpers as the
/// production Dapper type handlers (<c>DapperConfiguration.GuidTypeHandler</c>), so both stacks read and write
/// identical bytes and reject invalid blobs identically.
/// </summary>
internal sealed class GuidBlobConverter()
    : ValueConverter<Guid, byte[]>(value => GuidSql.ToBlob(value), blob => GuidSql.FromDb(blob));

/// <summary>
/// DateTimeOffset stored as ISO-8601 round-trip text (<c>"o"</c>), parsed with <c>DateTimeOffset.Parse</c> exactly as
/// the production Dapper handler does.
/// </summary>
internal sealed class DateTimeOffsetTextConverter()
    : ValueConverter<DateTimeOffset, string>(value => value.ToString("o"), text => DateTimeOffset.Parse(text));
