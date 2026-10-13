using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// The source-policy check for filing out of the import folder. A file may leave the import folder only into the one
/// managed folder marked New media for its type; an as-is folder is refused by the gate even if it were offered.
/// </summary>
public static class ImportFolderGate
{
    public static bool CanFile(
        ISourceMutationPolicyGate gate,
        IImportFolderService importFolders,
        string sourcePath,
        string destinationPath,
        ImportDestination destination,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(importFolders);
        ArgumentNullException.ThrowIfNull(destination);

        reason = null;
        var importPolicy = importFolders.CreateImportPolicy();
        if (!destination.IsReady || importPolicy is null)
        {
            reason = "There is no ready destination folder.";
            return false;
        }

        var leaving = gate.Evaluate(new SourceMutationRequest
        {
            Source = importPolicy,
            Mutation = SourceMutationKind.Move,
            Path = sourcePath,
        });
        if (!leaving.Allowed)
        {
            reason = leaving.Reason;
            return false;
        }

        var arriving = gate.Evaluate(new SourceMutationRequest
        {
            Source = FileSourceMutationPolicyFactory.Create(destination.Library!, destination.Source!),
            Mutation = SourceMutationKind.UseAsDestination,
            Path = destinationPath,
        });
        reason = arriving.Reason;
        return arriving.Allowed;
    }
}
