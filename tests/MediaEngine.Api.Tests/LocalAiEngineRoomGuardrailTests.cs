using System.Text.RegularExpressions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Everything that uses the local text model or the text reader goes through the Local AI engine room, so the queue, the
/// machine's rules, plugin allowances and the usage record can never be skipped.
/// </summary>
public sealed partial class LocalAiEngineRoomGuardrailTests
{
    private static readonly string[] AllowedFolders =
    [
        "src/MediaEngine.Api/Services/LocalAi/",
        "src/MediaEngine.AI/Llama/",
        "src/MediaEngine.AI/Infrastructure/",
    ];

    // The text reader's own service declares and implements the call; it is the engine behind the engine room.
    private static readonly string[] TextReaderServiceFiles =
    [
        "src/MediaEngine.Api/Services/LocalAssets/Ocr/IOcrService.cs",
        "src/MediaEngine.Api/Services/LocalAssets/Ocr/OcrService.cs",
    ];

    [Fact]
    public void OutsideTheEngineRoom_NoSourceFileTouchesTheTextInferenceServices()
    {
        var offenders = SourceFiles()
            .Where(file => !IsAllowed(file.RelativePath))
            .Where(file => TextInferenceName().IsMatch(file.Text))
            .Select(file => file.RelativePath)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Ask the Local AI engine room (IAiEngineRoom) for an ability instead of using the inference services: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void OutsideTheEngineRoom_NoSourceFileCallsTheTextReaderDirectly()
    {
        var offenders = SourceFiles()
            .Where(file => !IsAllowed(file.RelativePath))
            .Where(file => !TextReaderServiceFiles.Contains(file.RelativePath, StringComparer.OrdinalIgnoreCase))
            .Where(file => RecognizeCall().IsMatch(file.Text))
            .Select(file => file.RelativePath)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Read text through IAiEngineRoom.ReadTextFromImageAsync instead of IOcrService.RecognizeAsync: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void TheOldBackgroundAdmissionController_IsGone()
    {
        Assert.DoesNotContain(
            SourceFiles(),
            file => file.Text.Contains("BackgroundAiAdmissionController", StringComparison.Ordinal));
    }

    private static bool IsAllowed(string relativePath) =>
        AllowedFolders.Any(folder => relativePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string RelativePath, string Text)> SourceFiles()
    {
        var root = FindRepositoryRoot();
        var source = Path.Combine(root, "src");
        foreach (var path in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (relative, File.ReadAllText(path));
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root from " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"\b(ITextInferenceService|ILlamaInferenceService|LlamaInferenceService)\b")]
    private static partial Regex TextInferenceName();

    [GeneratedRegex(@"\.RecognizeAsync\(")]
    private static partial Regex RecognizeCall();
}
