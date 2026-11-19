using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.Faces;

/// <summary>
/// The one swap point for face recognition (spec view-people-search D13): given a photo, find its faces and describe how each looks as numbers.
/// Finding and describing sit behind this single role so the models can change without redoing the pages.
/// </summary>
public interface IFaceEngine
{
    /// <summary>Names the models in use; saved with every scanned photo so a model change can rescan.</summary>
    string ModelId { get; }

    /// <summary>True when the models are downloaded and the runtime is in place, so a photo can be looked at now.</summary>
    bool IsAvailable { get; }

    /// <summary>Finds the faces in one photo. Returns an empty list for a photo with none; throws when the file cannot be read.</summary>
    Task<IReadOnlyList<ViewDetectedFace>> AnalyseAsync(string imagePath, CancellationToken ct);
}

/// <summary>Stands in until the face runtime is in place: nothing is looked at, and People says it is getting the face tools.</summary>
public sealed class UnavailableFaceEngine : IFaceEngine
{
    public string ModelId => "none";

    public bool IsAvailable => false;

    public Task<IReadOnlyList<ViewDetectedFace>> AnalyseAsync(string imagePath, CancellationToken ct) =>
        throw new InvalidOperationException("The face tools are not in place yet.");
}
