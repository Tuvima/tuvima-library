using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Every question the Dashboard asks is asked in its own dialog style (details overlay spec D17: "No browser pop-ups"), so no
/// code in <c>src/MediaEngine.Web</c> calls the browser's <c>confirm</c>, <c>alert</c> or <c>prompt</c>, from C# or Razor through
/// JS interop or from the Dashboard's own scripts. Ask with <c>AppConfirmDialog.ShowAsync</c> (or a dialog of its own) instead.
/// </summary>
public sealed partial class NoBrowserPopupGuardrailTests
{
    private static readonly string WebRoot = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");

    // Vendored third-party code is not the Dashboard's own.
    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", "lib", "vendor"];

    /// <summary>
    /// The only browser call that looks like a dialog and is not one: the PWA install flow shows the browser's own install sheet
    /// through the <c>beforeinstallprompt</c> event's <c>prompt()</c> method. It is not a pop-up dialog the page can style.
    /// </summary>
    private static readonly (string RelativePath, string Text)[] Allowed =
    [
        ("wwwroot/app.js", "deferredPrompt.prompt("),
    ];

    [Fact]
    public void NoCSharpOrRazorCodeCallsConfirmAlertOrPromptThroughJsInterop()
    {
        var offenders = Files(".cs", ".razor")
            .Where(file => JsInteropPopup().IsMatch(file.Source))
            .Select(file => file.RelativePath)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A browser pop-up is called through JS interop; use AppConfirmDialog.ShowAsync (spec D17): " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoDashboardScriptCallsConfirmAlertOrPromptOutsideTheAllowedPwaInstallPrompt()
    {
        var offenders = new List<string>();
        foreach (var file in Files(".js", ".mjs", ".html", ".cshtml"))
        {
            foreach (Match match in ScriptPopup().Matches(StripComments(file.Source)))
            {
                var text = match.Value.Trim();
                if (!Allowed.Any(allowed => allowed.RelativePath == file.RelativePath && allowed.Text == text))
                {
                    offenders.Add($"{file.RelativePath}: {text}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A script calls a browser pop-up; use AppConfirmDialog (spec D17): " + string.Join("; ", offenders));
    }

    [Fact]
    public void EveryAllowedCallStillExistsSoTheAllowlistCannotGoStale()
    {
        foreach (var (relativePath, text) in Allowed)
        {
            var source = File.ReadAllText(Path.Combine(WebRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            Assert.True(StripComments(source).Contains(text, StringComparison.Ordinal),
                $"{relativePath} no longer contains {text}; remove it from the allowlist.");
        }
    }

    [Theory]
    [InlineData("await JS.InvokeAsync<bool>(\"confirm\", \"Sure?\")")]
    [InlineData("await js.InvokeVoidAsync(\"alert\", message)")]
    [InlineData("await JS.InvokeAsync<string?>(\n    \"prompt\",\n    \"Name?\")")]
    [InlineData("await JS.InvokeVoidAsync(\"window.confirm\", text)")]
    public void TheInteropRuleCatchesEachWayOfCallingAPopup(string source)
    {
        Assert.Matches(JsInteropPopup(), source);
    }

    [Theory]
    [InlineData("if (confirm('Sure?')) go();")]
    [InlineData("window.alert('Saved');")]
    [InlineData("const name = prompt('Name?');")]
    [InlineData("await installEvent.prompt();")]
    public void TheScriptRuleCatchesEachWayOfCallingAPopup(string source)
    {
        Assert.Matches(ScriptPopup(), source);
    }

    [Theory]
    [InlineData("role=\"alert\"")]
    [InlineData("var LiveRegionRole = \"alert\";")]
    [InlineData("await JS.InvokeVoidAsync(\"detailOrigin.back\", fallback)")]
    [InlineData("el.classList.add('confirm-button'); showConfirmation(x);")]
    public void TheRulesLeaveOrdinaryUsesOfThoseWordsAlone(string source)
    {
        Assert.DoesNotMatch(JsInteropPopup(), source);
        Assert.DoesNotMatch(ScriptPopup(), source);
    }

    private static string StripComments(string source) => ScriptComment().Replace(source, string.Empty);

    private static IEnumerable<(string RelativePath, string Source)> Files(params string[] extensions) =>
        Directory.EnumerateFiles(WebRoot, "*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !Path.GetRelativePath(WebRoot, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => SkippedFolders.Contains(part, StringComparer.OrdinalIgnoreCase)))
            .Where(path => !path.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path.GetRelativePath(WebRoot, path).Replace('\\', '/'), File.ReadAllText(path)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [GeneratedRegex(@"Invoke(?:Void)?Async(?:<[^>]*>)?\(\s*""(?:window\.)?(?:confirm|alert|prompt)""")]
    private static partial Regex JsInteropPopup();

    [GeneratedRegex(@"(?<![\w$.])(?:window\.)?(?:confirm|alert|prompt)\s*\(|[\w$]+\.prompt\s*\(")]
    private static partial Regex ScriptPopup();

    [GeneratedRegex(@"/\*.*?\*/|(?<![:\w])//[^\r\n]*", RegexOptions.Singleline)]
    private static partial Regex ScriptComment();
}
