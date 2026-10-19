using MediaEngine.Contracts.Settings;

namespace MediaEngine.Api.Services.Tools;

/// <summary>
/// The one catalogue of outside tools and on-demand downloads. Setup, the Settings page, the start-up log and the
/// health checks all read it, so they can never disagree.
/// </summary>
public interface IToolRequirementsService
{
    /// <summary>The last result; runs a check first when none has run yet.</summary>
    Task<ToolRequirementsDto> GetAsync(CancellationToken ct);

    /// <summary>Looks again (searching for tools afresh), tells the media tool listeners, and returns the new result.</summary>
    Task<ToolRequirementsDto> CheckAsync(CancellationToken ct);
}

/// <summary>
/// The parts of a tools check that touch native runtimes or AI model files, kept behind one seam so the catalogue
/// logic can be tested without them.
/// </summary>
public interface IToolRuntimeProbe
{
    /// <summary>True when the image engine can create a small picture.</summary>
    bool ImageEngineWorks();

    /// <summary>True when the local AI runtime loads.</summary>
    bool AiRuntimeLoads();

    /// <summary>Re-reads which AI model files are on disk.</summary>
    void RefreshModelFiles();
}
