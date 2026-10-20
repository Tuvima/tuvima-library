namespace MediaEngine.Application.ReadModels;

public sealed record LibraryOverviewReadModel(
    int Added24h,
    int Added7d,
    int Added30d,
    IReadOnlyDictionary<string, int> PipelineStates,
    double PipelineSuccessRate);

/// <summary>
/// What a batch edit needs to know about one selected item: the work it edits, where it sits in its show or album,
/// the file a Needs Review entry would point at, and how each requested field currently stands.
/// </summary>
/// <param name="RequestedEntityId">The id the caller selected (a work or a media asset).</param>
/// <param name="WorkId">The work whose presentation overrides a batch edit writes.</param>
/// <param name="RepresentativeAssetId">An owned file of the work or of its children, or <c>null</c> when it has none.</param>
/// <param name="Fields">Field key (as requested, lower case) to its current state.</param>
public sealed record BatchEditItemRead(
    Guid RequestedEntityId,
    Guid WorkId,
    string MediaType,
    string WorkKind,
    bool HasParentWork,
    Guid? RepresentativeAssetId,
    string? Title,
    IReadOnlyDictionary<string, BatchEditFieldRead> Fields);

/// <summary>
/// One field of one item. <see cref="OverrideValue"/> is the stored presentation override (<c>null</c> when none;
/// an empty string is an explicit empty list). <see cref="OwnValues"/> is what the item's own provider data says and
/// <see cref="InheritedValues"/> is what its nearest parent says, both as lists (a scalar is a list of one).
/// </summary>
public sealed record BatchEditFieldRead(
    string? OverrideValue,
    IReadOnlyList<string> OwnValues,
    IReadOnlyList<string> InheritedValues);
