using MediaEngine.Contracts.Ingestion;

namespace MediaEngine.Api.Services;

/// <summary>The Engine's one source for the live ingestion numbers.</summary>
public interface IIngestionQueueProgressService
{
    /// <summary>The newest snapshot.</summary>
    IngestionQueueProgressDto Current { get; }
}
