using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Organise;

/// <summary>Hands one file that is not in the library yet to the normal identification pipeline.</summary>
public interface IFileIntake
{
    Task EnqueueAsync(string path, string libraryId, string sourceId, CancellationToken ct = default);
}

/// <summary>Sends the file through the ingestion engine's direct-intake door, exactly as a folder scan would.</summary>
public sealed class IngestionEngineFileIntake : IFileIntake
{
    private readonly IIngestionEngine _engine;

    public IngestionEngineFileIntake(IIngestionEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    public Task EnqueueAsync(string path, string libraryId, string sourceId, CancellationToken ct = default) =>
        _engine.EnqueueIntakeAsync(new IntakeFileRequest
        {
            Path = path,
            SourceKind = IntakeSourceKinds.DirectLibrary,
            SourceId = sourceId,
            DestinationLibraryId = libraryId,
        }, ct);
}
