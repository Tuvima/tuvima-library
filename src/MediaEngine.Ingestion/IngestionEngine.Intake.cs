using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion;

public sealed partial class IngestionEngine
{
    /// <inheritdoc />
    public Task EnqueueIntakeAsync(IntakeFileRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (!IntakeSourceKinds.IsValid(request.SourceKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), request.SourceKind, "Unsupported intake source kind.");
        }

        var fullPath = Path.GetFullPath(request.Path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The intake file does not exist.", fullPath);
        }

        var destination = string.IsNullOrWhiteSpace(request.DestinationLibraryId)
            ? null
            : _libraryFolderResolver?.ResolveById(request.DestinationLibraryId);
        if (!string.IsNullOrWhiteSpace(request.DestinationLibraryId) && destination is null)
        {
            throw new InvalidOperationException(
                $"Direct intake destination library '{request.DestinationLibraryId}' is not configured.");
        }

        if (destination is not null
            && (string.Equals(destination.Kind, LibraryKinds.Personal, StringComparison.OrdinalIgnoreCase)
                || string.Equals(destination.Area, LibraryAreas.View, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Personal View library '{destination.Name}' must use the local-asset intake path; "
                + "catalogue ingestion is not permitted for this destination.");
        }

        _debounce.Enqueue(new FileEvent
        {
            Path = fullPath,
            EventType = FileEventType.Created,
            OccurredAt = DateTimeOffset.UtcNow,
            Intake = new IntakeContext
            {
                SourceKind = request.SourceKind,
                SourceId = request.SourceId,
                DestinationLibraryId = request.DestinationLibraryId,
                ActorProfileId = request.ActorProfileId,
            },
        });

        return Task.CompletedTask;
    }
}
