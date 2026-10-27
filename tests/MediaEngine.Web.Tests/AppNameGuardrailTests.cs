using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The app is called Library; Tuvima is the company and brand. Every string a person can see (page titles, Settings and
/// Setup copy, error messages, snackbars, the PWA manifest, the native apps) names the app "Library". "Tuvima" stays only
/// where it is the company or brand, or a technical name that must not change; each of those is listed in
/// <see cref="Allowed"/> with its reason. Code identifiers, namespaces, URLs and comments are not checked.
/// </summary>
public sealed partial class AppNameGuardrailTests
{
    private static readonly string[] ScannedRoots = ["src", "clients", "config"];

    private static readonly HashSet<string> ScannedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".razor", ".cs", ".resx", ".js", ".webmanifest", ".html", ".json", ".kt", ".swift", ".xml", ".brs", ".plist", ".strings",
    };

    private static readonly string[] SkippedFolders =
    [
        "/bin/", "/obj/", "/build/", "/node_modules/", "/.gradle/",
        "/wwwroot/lib/", "/wwwroot/vendor/",
        // Local credentials, gitignored and never shipped.
        "config/secrets/", "config/.secrets/",
        // JSON Schema titles for editors and validation tooling, read by developers rather than shown in the app.
        "config/schemas/",
        // Development-only seed and test harness output; never shown in a release build.
        "src/MediaEngine.Api/DevSupport/",
    ];

    /// <summary>The places "Tuvima" is kept on purpose: (file, text the line contains, why).</summary>
    private static readonly AllowedUse[] Allowed =
    [
        new("src/MediaEngine.AI/Infrastructure/SharedAiRuntime.cs", "\"Tuvima\", \"AI Runtimes\"", "Folder name on disk; renaming it would orphan installed AI runtimes."),
        new("src/MediaEngine.Domain/Services/TuvimaDataPaths.cs", "\"Tuvima\", \"data\"", "Folder name on disk for the data store and managed library."),
        new("src/MediaEngine.Api/Program.cs", "ServiceName = \"Tuvima Library\"", "Windows service identity used by the installer and service manager."),
        new("src/MediaEngine.Web/Program.cs", "ServiceName = \"Tuvima Library Dashboard\"", "Windows service identity used by the installer and service manager."),
        new("src/MediaEngine.Api/Security/TuvimaAuthentication.cs", "Scheme = \"Tuvima\"", "Authentication scheme identifier, never shown."),
        new("src/MediaEngine.Api/Services/Networking/LocalDiscoveryHostedService.cs", "\"Tuvima Library._tuvima._tcp.local\"", "Local network discovery service name the apps look for."),
        new("src/MediaEngine.Api/Services/Networking/LocalDiscoveryHostedService.cs", "\"product=Tuvima Library\"", "Discovery product identifier the apps check."),
        new("src/MediaEngine.Api/Services/Networking/TailscaleRemoteConnectivityProvider.cs", "\"Tuvima Library\", StringComparison.Ordinal", "Matches the server's product identifier."),
        new("src/MediaEngine.Contracts/Authentication/ClientAuthorizationContracts.cs", "Product { get; init; } = \"Tuvima Library\"", "Wire product identifier the apps check when pairing."),
        new("src/MediaEngine.Web/Program.cs", "product = \"Tuvima Library\"", "Wire product identifier the apps check when pairing."),
        new("src/MediaEngine.Contracts/Startup/ProcessInstanceLease.cs", "Tuvima lease owner", "Internal process lease name, never shown."),
        new("src/MediaEngine.Identity/FirstPartyIdentityService.cs", "TwoStepIssuer = \"Tuvima Library\"", "Brand shown in authenticator apps beside the code, so the account is recognisable among others."),
        new("src/MediaEngine.Ingestion/AudioMetadataTagger.cs", "Tuvima identifier tag", "The name of the tag family Tuvima writes into audio files."),
        new("src/MediaEngine.Web/Components/Profiles/ProfileAvatarPalette.cs", "\"Tuvima purple\"", "Brand colour name."),
        new("src/MediaEngine.Api/Services/LocalAssets/FileWrites/ViewProfileNameFormats.cs", "\"Tuvima Added By\"", "Name of the custom property Library writes into Word, Excel and PowerPoint files (batched write-back D7); a technical name that other tools read, so it stays as the spec fixes it."),
        new("src/MediaEngine.Api/DependencyInjection/TuvimaHttpClientRegistration.cs", "\"Tuvima Library/1.0", "User-Agent sent to metadata providers, who ask for a stable product name."),
        new("src/MediaEngine.Web/Services/Integration/ExternalAuthenticationRegistration.cs", "\"Tuvima-Library/1.0\"", "User-Agent sent to sign-in providers."),
        new("src/MediaEngine.Api/Services/Webhooks/WebhookTransport.cs", "request.Headers.Add(\"Tuvima-", "Webhook header names that receivers verify."),
        new("clients/tv-web/platforms/webos/appinfo.json", "\"vendor\": \"Tuvima\"", "The company that publishes the TV app."),
        new("config/providers/", "\"user_agent\": \"Tuvima Library/1.0", "User-Agent sent to metadata providers, who ask for a stable product name."),
        new("src/MediaEngine.Api/Services/Approvals/ApprovalStatementBuilder.cs", "\"Official plugin from Tuvima\"", "The company that publishes Official plugins (developer-tools spec D-H trust levels)."),
        new("src/MediaEngine.Api/Services/Approvals/ApprovalStatementBuilder.cs", "\"Community, not checked by Tuvima\"", "The company that verifies plugins (developer-tools spec D-H trust levels)."),
        new("src/MediaEngine.Api/Services/Approvals/ApprovalStatementBuilder.cs", "\"Added by hand, not checked by Tuvima\"", "The company that verifies plugins (developer-tools spec D-H trust levels)."),
        new("src/MediaEngine.Web/Components/Settings/PluginSettingsTab.razor", "Official Tuvima", "The company that publishes Official plugins: the badge and the filter switch on the Plugins page (Plugins and Integrations screens spec D7)."),
        new("src/MediaEngine.Api/Services/Approvals/ApprovalStatementBuilder.cs", "\"Tuvima hasn't checked this plugin.", "The company that verifies plugins; the not-verified warning (developer-tools spec D-H trust levels)."),
    ];

    [Fact]
    public void UserFacingStrings_DoNotNameTheAppTuvima()
    {
        var root = FindRepoRoot();
        var violations = new List<string>();

        foreach (var (relativePath, lines) in ScannedFiles(root))
        {
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (!AppNamePattern().IsMatch(line) || IsAllowed(relativePath, line))
                {
                    continue;
                }

                violations.Add($"{relativePath}:{index + 1}: {line.Trim()}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "These strings call the app \"Tuvima\". The app is called Library; use \"Library\", or add the line to the "
            + "allowlist with a reason when it is the company, the brand or a technical name:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void EveryAllowedUse_StillExists()
    {
        var root = FindRepoRoot();
        var files = ScannedFiles(root).ToList();

        var stale = Allowed
            .Where(allowed => !files.Any(file => file.RelativePath.StartsWith(allowed.Path, StringComparison.Ordinal)
                && file.Lines.Any(line => line.Contains(allowed.LineContains, StringComparison.Ordinal))))
            .Select(allowed => $"{allowed.Path}: {allowed.LineContains}")
            .ToList();

        Assert.True(stale.Count == 0, "Remove these allowlist entries; the text they allow is gone:\n" + string.Join('\n', stale));
    }

    private static bool IsAllowed(string relativePath, string line) =>
        Allowed.Any(allowed => relativePath.StartsWith(allowed.Path, StringComparison.Ordinal)
            && line.Contains(allowed.LineContains, StringComparison.Ordinal));

    private static IEnumerable<(string RelativePath, string[] Lines)> ScannedFiles(string root)
    {
        foreach (var scannedRoot in ScannedRoots)
        {
            var directory = Path.Combine(root, scannedRoot.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relativePath = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                var fileName = Path.GetFileName(path);
                var isRokuManifest = fileName == "manifest" && relativePath.StartsWith("clients/roku/", StringComparison.Ordinal);
                if ((!ScannedExtensions.Contains(Path.GetExtension(path)) && !isRokuManifest)
                    || SkippedFolders.Any(folder => ("/" + relativePath).Contains(folder, StringComparison.Ordinal)))
                {
                    continue;
                }

                var text = File.ReadAllText(path);
                if (!text.Contains("Tuvima", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return (relativePath, WithoutComments(text, Path.GetExtension(path), isRokuManifest));
            }
        }
    }

    /// <summary>Blanks out comments, keeping line numbers, so only code, markup and strings are checked.</summary>
    private static string[] WithoutComments(string text, string extension, bool isRokuManifest)
    {
        var withoutBlocks = BlockCommentPattern().Replace(text, match => new string('\n', match.Value.Count(character => character == '\n')));
        var lineComment = extension is ".brs" || isRokuManifest ? "'" : "//";
        return withoutBlocks
            .Split('\n')
            .Select(line => line.TrimStart().StartsWith(lineComment, StringComparison.Ordinal) ? string.Empty : line.TrimEnd('\r'))
            .ToArray();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    /// <summary>"Tuvima" as a word: not part of an identifier (TuvimaClient), a namespace (Tuvima.Library), a path or a URL.</summary>
    [GeneratedRegex(@"(?<![\w./\\-])Tuvima(?![\w/\\]|\.\w)")]
    private static partial Regex AppNamePattern();

    [GeneratedRegex(@"/\*.*?\*/|@\*.*?\*@|<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex BlockCommentPattern();

    private sealed record AllowedUse(string Path, string LineContains, string Reason);
}
