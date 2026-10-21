using System.Text.RegularExpressions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A NSFW PIN travels in the body of /api/v1/nsfw/unlock and /api/v1/nsfw/pin, and the profile PIN that opens NSFW access in the body
/// of the access routes. Nothing in the Engine may write a request body to a log (Videos spec D18 and D25): no request-body
/// logging middleware, no code that reads a body outside the one gateway that needs it, the NSFW routes themselves log nothing, and the
/// framework's own request logging stays below the level that would show anything.
/// </summary>
public sealed class NsfwRequestBodyLoggingGuardrailTests
{
    private static readonly string[] BodyLoggingMachinery =
    [
        "AddHttpLogging", "UseHttpLogging", "UseSerilogRequestLogging", "HttpLoggingFields", "EnableBuffering",
    ];

    // The one place that reads a raw request body is the plugin application gateway, which is not a NSFW route and logs no body.
    private static readonly string[] RawBodyReadersAllowed = ["Endpoints/PluginApplicationServiceEndpoints.cs"];

    [Fact]
    public void TheEngine_HasNoRequestBodyLoggingMachinery_AndReadsNoRawBodyOutsideTheAllowedGateway()
    {
        var offenders = new List<string>();
        foreach (var file in EngineSources())
        {
            var relative = Path.GetRelativePath(Path.Combine(FindRepoRoot(), "src/MediaEngine.Api"), file).Replace('\\', '/');
            var source = File.ReadAllText(file);

            foreach (var token in BodyLoggingMachinery.Where(source.Contains))
            {
                offenders.Add($"{relative}: {token}");
            }

            if (Regex.IsMatch(source, @"\b[Rr]equest\.Body\b") && !RawBodyReadersAllowed.Contains(relative))
            {
                offenders.Add($"{relative}: reads Request.Body");
            }
        }

        Assert.True(offenders.Count == 0, "Request bodies must never be logged or read raw: " + string.Join("; ", offenders));
    }

    [Fact]
    public void TheNsfwRoutes_LogNothing()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Api/Endpoints/VaultEndpoints.cs"));

        Assert.DoesNotContain("ILogger", source);
        Assert.DoesNotMatch(@"\b[Ll]og(ger)?\.Log\w*\(", source);
        Assert.DoesNotContain("Console.Write", source);
    }

    [Fact]
    public void TheFrameworkRequestLogging_StaysAtWarningOrAbove_InEveryShippedSetting()
    {
        var folder = Path.Combine(FindRepoRoot(), "src/MediaEngine.Api");
        foreach (var settings in Directory.EnumerateFiles(folder, "appsettings*.json"))
        {
            var text = File.ReadAllText(settings);
            Assert.DoesNotContain("HttpLogging", text);
            Assert.DoesNotMatch("\"Microsoft\\.AspNetCore(\\.[A-Za-z.]+)?\"\\s*:\\s*\"(Trace|Debug|Information)\"", text);
        }
    }

    private static IEnumerable<string> EngineSources() =>
        Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), "src/MediaEngine.Api"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

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
