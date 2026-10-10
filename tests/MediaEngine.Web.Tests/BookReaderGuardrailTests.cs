using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Source-level guardrails for the foliate-js book reader (packet BR2). The browser behaviour is proven by the
/// rig in scripts/visual-qa/book-reader; these checks keep the shape that makes it safe from drifting.
/// </summary>
public sealed class BookReaderGuardrailTests
{
    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    private const string Web = "src/MediaEngine.Web";

    [Fact]
    public void VendoredFoliate_IsPinnedAndLicensed()
    {
        var pinned = Read($"{Web}/wwwroot/lib/foliate-js/PINNED.md");
        Assert.Matches(new Regex("[0-9a-f]{40}"), pinned);

        Assert.True(File.Exists(Path.Combine(RepoRoot, Web, "wwwroot", "lib", "foliate-js", "LICENSE")));
        Assert.True(File.Exists(Path.Combine(RepoRoot, "licenses", "zip.js-BSD-3-Clause.txt")));
        Assert.Contains("foliate-js", Read("THIRD-PARTY-NOTICES.md"));
    }

    [Fact]
    public void OnlyTheAdapter_ImportsFoliate()
    {
        var wwwroot = Path.Combine(RepoRoot, Web, "wwwroot");
        var vendored = Path.Combine(wwwroot, "lib", "foliate-js") + Path.DirectorySeparatorChar;
        var adapter = Path.Combine(wwwroot, "js", "book-reader.js");

        var offenders = Directory
            .EnumerateFiles(Path.Combine(RepoRoot, Web), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".js", StringComparison.Ordinal)
                           || path.EndsWith(".razor", StringComparison.Ordinal)
                           || path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => !path.StartsWith(vendored, StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !string.Equals(path, adapter, StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("lib/foliate-js", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepoRoot, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Adapter_BlocksScriptsInsideBooks()
    {
        var adapter = Read($"{Web}/wwwroot/js/book-reader.js");

        Assert.Contains("script-src 'none'", adapter);
        Assert.Contains("connect-src 'none'", adapter);
        Assert.Contains("frame-src 'none'", adapter);
        Assert.Contains("if (detail.isScript) detail.allow = false;", adapter);
    }

    [Fact]
    public void Adapter_HardensEveryKindOfDocumentItCanShow()
    {
        var adapter = Read($"{Web}/wwwroot/js/book-reader.js");

        // XHTML/HTML, SVG pages and anything else a browser would open as a document all pass through the hardening step.
        Assert.Contains("hardenPageDetailed", adapter);
        Assert.Contains("hardenSvg", adapter);
        Assert.Contains("hardenSvgAsPage", adapter);
        Assert.Contains("isDocumentType", adapter);
        Assert.Contains("iframe, frame, frameset, object, embed, applet, base", adapter);
        // A page that cannot be parsed is replaced, never passed through as it was.
        Assert.Contains("BLOCKED_PAGE", adapter);
        Assert.DoesNotContain("return text;", adapter);
    }

    [Fact]
    public void Adapter_RejectsABookThatChangesWhileItIsRead()
    {
        var adapter = Read($"{Web}/wwwroot/js/book-reader.js");

        Assert.Contains("'If-Range'", adapter);
        Assert.Contains("'changed'", adapter);
        Assert.Contains("OnFailed", adapter);
    }

    [Fact]
    public void BookReaderPage_UsesTheSharedPatterns()
    {
        var page = Read($"{Web}/Components/Pages/BookReader.razor");

        Assert.Contains("./js/book-reader.js", page);
        Assert.Contains("EngineBookProxyPath.ToBrowserUrl", page);
        Assert.Contains("[\"cfi\"]", page);
        Assert.Contains("IAsyncDisposable", page);
        Assert.Contains("_dotNetRef?.Dispose()", page);
        Assert.Contains("_disposing", page);
        Assert.Contains("public Task OnFailed", page);
        // Stored reader settings are read by the script, so no stored-JSON shape is parsed on the .NET side.
        Assert.DoesNotContain("ReaderSettingsDto", page);
        Assert.DoesNotContain("style=\"", page);
        Assert.DoesNotContain("<button", page);
        Assert.DoesNotContain("<input", page);
    }

    [Fact]
    public void EpubReaderRoute_SwitchesToTheNewReaderOnlyOnRequest()
    {
        var page = Read($"{Web}/Components/Pages/EpubReader.razor");

        Assert.Contains("[SupplyParameterFromQuery(Name = \"reader\")]", page);
        Assert.Contains("\"new\"", page);
        Assert.Contains("<BookReader ", page);
        // The choice is made once when the page opens.
        Assert.Contains("private bool _useNewReader;", page);
        Assert.DoesNotContain("UseNewReader =>", page);
    }

    [Fact]
    public void BookReaderCss_FollowsTheStyleRules()
    {
        var css = Read($"{Web}/wwwroot/css/book-reader.css");

        Assert.DoesNotContain("!important", css);
        Assert.DoesNotContain("::deep", css);
        Assert.DoesNotContain(".reader-", css);
    }

    [Fact]
    public void OpenResult_ReadsTheScriptsCamelCaseAnswer()
    {
        const string json = """
            {"ok":true,"title":"Dune","author":"Frank Herbert","dir":"ltr","layout":"reflowable","fraction":0.25,"restored":true}
            """;

        var result = JsonSerializer.Deserialize<BookReaderOpenResult>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.Equal("Dune", result.Title);
        Assert.Equal(0.25, result.Fraction);
        Assert.True(result.Restored);

        var failure = JsonSerializer.Deserialize<BookReaderOpenResult>(
            """{"ok":false,"code":"notfound","message":"This book is no longer in the library."}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(failure!.Ok);
        Assert.Equal("notfound", failure.Code);
    }
}
