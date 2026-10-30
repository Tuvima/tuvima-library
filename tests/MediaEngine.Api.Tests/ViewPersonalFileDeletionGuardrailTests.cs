using System.Text.RegularExpressions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Safer deleting in View D1: no code under Services/View or Services/LocalAssets removes a personal file from disk unless the shared
/// source-file safety gate was asked first. Every file that calls File.Delete or Directory.Delete there is listed with the reason it is
/// allowed to; a new one must either use the gate or be added with a reason a reviewer accepts.
/// </summary>
public sealed class ViewPersonalFileDeletionGuardrailTests
{
    private static readonly Dictionary<string, string> Reviewed = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Api/Services/View/ViewSharedTransferService.cs"] = "staging copies; the original is removed only after EvaluateOriginalDeleteAsync (the gate)",
        ["src/MediaEngine.Api/Services/View/ViewTrashStaging.cs"] = "the hidden Empty Trash staging folder; used only by the gated Empty Trash service",
        ["src/MediaEngine.Api/Services/View/ViewTrashDeletionService.cs"] = "Empty Trash, which asks the gate for every file",
        ["src/MediaEngine.Api/Services/View/ProfilePhotoDisposer.cs"] = "a removed person's files, each gate-approved first, plus their own profile picture",
        ["src/MediaEngine.Api/Services/View/ViewThumbnailService.cs"] = "Library's own temporary thumbnail files, never a person's original",
        ["src/MediaEngine.Api/Services/LocalAssets/ViewLibraryService.cs"] = "the half-written temporary file of a failed upload",
        ["src/MediaEngine.Api/Services/LocalAssets/Intake/ViewIntakeAdminService.cs"] = "files in the intake inbox the person is discarding",
        ["src/MediaEngine.Api/Services/LocalAssets/Intake/ViewTidyService.cs"] = "empty folders only",
        ["src/MediaEngine.Api/Services/LocalAssets/Intake/ViewIntakeFiler.cs"] = "intake leftovers after a verified copy",
        ["src/MediaEngine.Api/Services/LocalAssets/Ocr/OcrService.cs"] = "temporary OCR files",
        ["src/MediaEngine.Api/Services/LocalAssets/Ocr/OcrLanguageDownloader.cs"] = "partial language downloads",
        ["src/MediaEngine.Api/Services/LocalAssets/Ocr/DocumentOcrService.cs"] = "temporary OCR folders",
        ["src/MediaEngine.Api/Services/LocalAssets/FileWrites/ViewItemFileWriter.cs"] = "its own temporary write copies",
        ["src/MediaEngine.Api/Services/LocalAssets/FileWrites/StrandedCopyRecord.cs"] = "its own temporary write copies",
    };

    private static readonly Regex RawDelete = new(@"\b(File|Directory)\.Delete\(", RegexOptions.Compiled);

    [Fact]
    public void OnlyReviewedFilesCanDeleteFromDiskInTheViewServices()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var folder in new[] { "src/MediaEngine.Api/Services/View", "src/MediaEngine.Api/Services/LocalAssets" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
                {
                    continue;
                }

                if (RawDelete.IsMatch(File.ReadAllText(file)) && !Reviewed.ContainsKey(relative))
                {
                    offenders.Add(relative);
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These files delete from disk but are not on the reviewed list. A personal file may only be removed after the shared "
            + "source-file safety gate (ISourceMutationPolicyGate) said yes; use it, or add the file here with a reason: "
            + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("src/MediaEngine.Api/Services/View/ViewSharedTransferService.cs", "EvaluateOriginalDeleteAsync")]
    [InlineData("src/MediaEngine.Api/Services/View/ProfilePhotoDisposer.cs", "GateAllowsDelete")]
    [InlineData("src/MediaEngine.Api/Services/View/ViewTrashDeletionService.cs", "gate.Evaluate")]
    public void ServicesThatRemovePersonalOriginalsAskTheGate(string file, string marker)
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), file));
        Assert.Contains(marker, source, StringComparison.Ordinal);
        Assert.Contains("SourceMutationKind.Delete", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
