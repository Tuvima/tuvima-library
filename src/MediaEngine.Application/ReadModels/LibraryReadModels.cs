namespace MediaEngine.Application.ReadModels;

public sealed record LibraryOverviewReadModel(
    int Added24h,
    int Added7d,
    int Added30d,
    IReadOnlyDictionary<string, int> PipelineStates,
    double PipelineSuccessRate);
