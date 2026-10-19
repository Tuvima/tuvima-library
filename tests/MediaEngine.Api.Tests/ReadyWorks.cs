using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Test seam for fixtures that exercise something other than readiness. Display queries show only Ready works,
/// so a fixture that seeds works directly would otherwise see nothing; this marks every leaf work Ready the moment
/// it is inserted. Fixtures that test readiness itself must not use it.
/// </summary>
internal static class ReadyWorks
{
    public static void TreatEveryWorkAsReady(IDatabaseConnection database)
    {
        using var connection = database.CreateConnection();
        connection.Execute("""
            CREATE TRIGGER IF NOT EXISTS test_every_work_is_ready
            AFTER INSERT ON works
            WHEN NEW.work_kind IN ('standalone', 'child') AND NEW.ready_at IS NULL
            BEGIN
                UPDATE works SET ready_at = '2026-01-01T00:00:00.0000000+00:00' WHERE id = NEW.id;
            END;
            """);
    }
}
