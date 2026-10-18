using System.Text.Json;

namespace MediaEngine.Storage.Benchmarks.Seeding;

/// <summary>
/// Shape of a seeded file. Stored next to the database as <c>&lt;db&gt;.bench.json</c> so <c>verify</c>, <c>bench</c> and
/// <c>concurrency</c> derive their fixed parameter sets (ids, offsets) without scanning the data.
/// </summary>
internal sealed record DatasetInfo(
    string Scale,
    int Works,
    int ParentWorks,
    int Persons,
    int Collections,
    int Claims,
    int ArtworkAssets,
    IReadOnlyDictionary<string, long> TableCounts)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SidecarPath(string databasePath) => databasePath + ".bench.json";

    public static DatasetInfo Load(string databasePath)
    {
        var path = SidecarPath(databasePath);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"No dataset description at '{path}'. Run 'seed --db <path>' first; the file is written next to the database.");
        }

        return JsonSerializer.Deserialize<DatasetInfo>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"'{path}' is empty or not a dataset description.");
    }

    public void Save(string databasePath) =>
        File.WriteAllText(SidecarPath(databasePath), JsonSerializer.Serialize(this, JsonOptions));
}
