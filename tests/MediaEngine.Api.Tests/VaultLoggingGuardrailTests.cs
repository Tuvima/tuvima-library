using System.Text.RegularExpressions;

namespace MediaEngine.Api.Tests;

// What the Vault must never write to a log (Videos spec WP12; engineering/validation/vault-surface-inventory.md, hard gate 4): no
// request or response bodies (a PIN, a typed search), and no title, file name, path or exception text (which carries the path) from
// the Vault lookup code.
public sealed class VaultLoggingGuardrailTests
{
    private static readonly string Root = FindRepoRoot();

    [Fact]
    public void TheEngine_NeverTurnsOnRequestOrResponseBodyLogging()
    {
        var offenders = SourceFiles("src/MediaEngine.Api")
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"UseHttpLogging|AddHttpLogging|HttpLoggingFields\.(Request|Response)Body|EnableBuffering\s*\("))
            .Select(Relative)
            .ToList();

        Assert.True(offenders.Count == 0, "Body logging would record PINs and Vault searches: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheVaultRoutes_NeverWriteToALog()
    {
        foreach (var file in new[] { "src/MediaEngine.Api/Endpoints/VaultIdentifyEndpoints.cs", "src/MediaEngine.Api/Endpoints/VaultEndpoints.cs" })
        {
            var source = File.ReadAllText(Path.Combine(Root, file));
            Assert.DoesNotMatch(@"ILogger|\.Log(Trace|Debug|Information|Warning|Error|Critical)\s*\(", source);
        }
    }

    [Fact]
    public void TheVaultLookupCode_LogsOnlyFixedWordsAndKindsOfError_NeverATitleAPathOrAnExceptionObject()
    {
        var files = SourceFiles("src/MediaEngine.Providers/Services/Vault")
            .Concat(
            [
                Path.Combine(Root, "src/MediaEngine.Providers/Services/StashBox/VaultVideoDetailsService.cs"),
                Path.Combine(Root, "src/MediaEngine.Providers/Services/StashBox/StashBoxVideoLookup.cs"),
            ]);
        var call = new Regex(@"_logger\s*\.\s*Log\w+\s*\((?<args>[^;]*)\);", RegexOptions.Singleline);
        var offenders = new List<string>();
        foreach (var file in files)
        {
            foreach (Match match in call.Matches(File.ReadAllText(file)))
            {
                var args = match.Groups["args"].Value;
                // An exception object carries its message, and a file error's message carries the path.
                var passesException = Regex.IsMatch(args.TrimStart(), @"^(ex|exception|e)\s*,");
                var namesSensitiveData = Regex.IsMatch(args, @"\b(Title|FileName|FilePath|Path|Description|Text|Query)\b");
                if (passesException || namesSensitiveData)
                {
                    offenders.Add($"{Relative(file)}: {match.Value.Split('\n')[0].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Vault lookup code logged something sensitive:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static IEnumerable<string> SourceFiles(string folder) =>
        Directory.EnumerateFiles(Path.Combine(Root, folder), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string file) => Path.GetRelativePath(Root, file).Replace('\\', '/');

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
