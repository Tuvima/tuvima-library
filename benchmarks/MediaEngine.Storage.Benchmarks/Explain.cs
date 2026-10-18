using MediaEngine.Storage.Benchmarks.Queries;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Benchmarks;

/// <summary>
/// Diagnostic: prints SQLite's EXPLAIN QUERY PLAN for the production SQL of Q4 (count statement, no filter) and Q5, on a
/// production connection. Used to see why the heavy reads scale the way they do; not part of any measurement.
/// </summary>
internal static class Explain
{
    public static void Run(BenchContext context, string query)
    {
        var sql = query switch
        {
            "q4" => Q4LibraryItems.ProjectionSql.Value
                + "SELECT COUNT(*) FROM full_data fd WHERE fd.library_visibility = 'visible'",
            "q5" => Q5DisplayProjection.Sql.Value,
            _ => throw new ArgumentException("--query must be q4 or q5."),
        };

        using var connection = context.Database.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddWithValue("@limit", Workload.HomeLimit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            Console.WriteLine($"{reader.GetInt32(0),5} {reader.GetInt32(1),5}  {reader.GetString(3)}");
        }
    }
}
