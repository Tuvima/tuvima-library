namespace MediaEngine.Storage.Benchmarks.Seeding;

/// <summary>
/// Deterministic, random-looking 16-byte identifiers. The SQL seeder and the C# workload builder derive the same
/// bytes from the same (kind, value) pair, so the benchmark can pick "its" ids without reading the database and
/// every run of <c>seed</c> produces a byte-identical dataset. The leading bytes are scrambled so b-tree inserts
/// land in random order, as they do with the product's random UUIDs.
/// </summary>
internal static class SeedIds
{
    public const int Work = 1;
    public const int Edition = 2;
    public const int Asset = 3;
    public const int Parent = 4;
    public const int Claim = 5;
    public const int Person = 6;
    public const int Collection = 7;
    public const int ArtworkAsset = 8;
    public const int ArtworkLink = 9;
    public const int Review = 10;
    public const int Job = 11;
    public const int Profile = 12;

    /// <summary>The profile that owns the seeded play history (added 2026-10-20). <c>user_states.user_id</c> is this id.</summary>
    public static readonly Guid PlayHistoryProfile = Id(Profile, 1);

    /// <summary>Offset that keeps music-album parent ids apart from TV-show parent ids.</summary>
    public const long MusicParentBase = 10_000_000;

    private const long MixA = 2654435761;
    private const long MixB = 40503;
    private const long Mod32 = 4294967296;

    public static Guid Id(int kind, long value)
    {
        var first = (value * MixA + (long)kind * MixB) % Mod32;
        var second = (value * MixB + (long)kind * MixA) % Mod32;
        var last = ((long)kind << 48) + value;

        var bytes = new byte[16];
        WriteBigEndian(bytes, 0, first, 4);
        WriteBigEndian(bytes, 4, second, 4);
        WriteBigEndian(bytes, 8, last, 8);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>SQL expression producing the same 16-byte BLOB as <see cref="Id"/> for an integer SQL expression.</summary>
    public static string Sql(int kind, string valueExpression) =>
        $"unhex(printf('%08X%08X%016X', (({valueExpression}) * {MixA} + {kind * MixB}) % {Mod32}, " +
        $"(({valueExpression}) * {MixB} + {kind * MixA}) % {Mod32}, {((long)kind << 48)} + ({valueExpression})))";

    private static void WriteBigEndian(byte[] target, int offset, long value, int width)
    {
        for (var i = 0; i < width; i++)
        {
            target[offset + width - 1 - i] = (byte)(value & 0xFF);
            value >>= 8;
        }
    }
}
