using MediaEngine.Domain.Capabilities;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;

namespace MediaEngine.Ingestion.Pipeline;

public sealed record HashDedupeStageDependencies(
    IFileHashCacheRepository? FileHashCache);

public sealed record ScoreIdentifyStageDependencies(
    CapabilityPlanner? CapabilityPlanner);

public sealed record OrganizeStageDependencies(
    StageOutcomeFactory? StageOutcomeFactory);

public sealed record WriteBackStageDependencies(
    IEntityAssetRepository? EntityAssetRepository,
    AssetPathService? AssetPathService,
    IWorkRepository? WorkRepository,
    IAssetExportService? AssetExportService,
    IFileWriteQueue? FileWriteQueue = null);

public sealed record IdentityJobStageDependencies(
    IIdentityPipelineSignal? Signal);
