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
        LocalizedSource.ReadAllText(Path.Combine(RepoRoot, relativePath));

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
            .Where(path => LocalizedSource.ReadAllText(path).Contains("lib/foliate-js", StringComparison.Ordinal))
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
        // Types are compared in one plain form ("application/xhtml+xml; charset=utf-8" is the same type), and only a
        // short list of picture/font/sound/video/style types may ever be handed to the browser as they are.
        Assert.Contains("normalizeType", adapter);
        Assert.Contains("SAFE_TYPES", adapter);
        Assert.Contains("application/octet-stream", adapter);
        Assert.Contains("stripInstructions", adapter);
        Assert.Contains("iframe, frame, frameset, object, embed, applet, base", adapter);
        // Image maps and templates (declarative shadow roots) go, other elements keep only local hrefs, and the click handler
        // that runs on every section agrees with the clean-up on what a link is.
        Assert.Contains("plaintext, map, area, template", adapter);
        Assert.Contains("restrictHrefs(doc);", adapter);
        Assert.Contains("export function guardLinks", adapter);
        Assert.Contains("guardLinks(doc, openExternalLink);", adapter);
        // A page that cannot be parsed is replaced, never passed through as it was.
        Assert.Contains("BLOCKED_PAGE", adapter);
        Assert.DoesNotContain("return text;", adapter);
    }

    [Fact]
    public void BookFrames_AreNotAllowedToRunScripts_ExceptOnAppleWebKitWhereTheAdapterSaysSo()
    {
        foreach (var file in new[] { "paginator.js", "fixed-layout.js" })
        {
            var source = Read($"{Web}/wwwroot/lib/foliate-js/{file}");
            // The value comes from the adapter and can only be one of two exact strings; anything else falls back to no scripts.
            Assert.Contains("globalThis.TUVIMA_BOOK_FRAME_SANDBOX === 'allow-same-origin allow-scripts' ? 'allow-same-origin allow-scripts' : 'allow-same-origin'", source);
            Assert.DoesNotContain("setAttribute('sandbox', 'allow-same-origin allow-scripts')", source);
            Assert.DoesNotContain("setAttribute('sandbox', 'allow-same-origin')", source);
            Assert.Contains("TUVIMA LOCAL PATCH", source);
        }

        // The adapter allows scripts only where needsScriptSandbox says Apple WebKit, and sets the value before it builds a frame.
        var adapter = Read($"{Web}/wwwroot/js/book-reader.js");
        Assert.Contains("export function needsScriptSandbox", adapter);
        Assert.Contains("const SANDBOX_NO_SCRIPTS = 'allow-same-origin';", adapter);
        Assert.Contains("const SANDBOX_WITH_SCRIPTS = 'allow-same-origin allow-scripts';", adapter);
        Assert.Contains("return needsScriptSandbox(env) ? SANDBOX_WITH_SCRIPTS : SANDBOX_NO_SCRIPTS;", adapter);
        Assert.Contains("globalThis.TUVIMA_BOOK_FRAME_SANDBOX = bookFrameSandbox();", adapter);
        Assert.True(adapter.IndexOf("TUVIMA_BOOK_FRAME_SANDBOX = ", StringComparison.Ordinal) < adapter.IndexOf("createElement('foliate-view')", StringComparison.Ordinal));

        // The patch is recorded where the next person upgrading the library will look.
        var pinned = Read($"{Web}/wwwroot/lib/foliate-js/PINNED.md");
        Assert.Contains("Local patch", pinned);
        Assert.Contains("allow-scripts", pinned);
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
    public void EpubReaderRoute_AlwaysOpensTheNewReader_AndTheOldReaderIsGone()
    {
        var page = Read($"{Web}/Components/Pages/EpubReader.razor");

        Assert.Contains("<BookReader ", page);
        // One reader opens every book: no switch on the address, no second code path.
        Assert.DoesNotContain("reader=new", page);
        Assert.DoesNotContain("Query.String(\"reader\")", page);
        Assert.DoesNotContain("_useNewReader", page);
        Assert.False(File.Exists(Path.Combine(RepoRoot, Web, "wwwroot", "js", "epub-reader.js")));
        Assert.False(File.Exists(Path.Combine(RepoRoot, Web, "wwwroot", "css", "epub-reader.css")));
    }

    [Fact]
    public void TheEngine_NoLongerRewritesPictureAddressesToItsOwnRoute()
    {
        // Pictures the browser asked the Engine for one by one carried no sign-in and could not be reached from other
        // devices; the reader now unpacks them from the book file it already holds.
        var endpoints = Read("src/MediaEngine.Api/Endpoints/ReadEndpoints.cs");
        Assert.DoesNotContain("/resource/", endpoints);
        Assert.DoesNotContain("/chapter/", endpoints);
        Assert.DoesNotContain("RewriteResourceUrls", Read("src/MediaEngine.Processors/EpubContentService.cs"));
    }

    [Fact]
    public void ThePictureHelpers_StayInTheirOwnModuleAndNeverTouchTheLibrary()
    {
        var module = Read($"{Web}/wwwroot/js/book-reader-pictures.js");
        Assert.DoesNotContain("foliate", module);
        Assert.Contains("book-reader-pictures.js", Read($"{Web}/wwwroot/js/book-reader.js"));
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
    public void PageAndScript_AgreeOnEveryCallBetweenThem()
    {
        var page = Read($"{Web}/Components/Pages/BookReader.razor");
        var adapter = Read($"{Web}/wwwroot/js/book-reader.js");

        // Every script function the page calls exists in the adapter (a typo would only fail inside a browser).
        var called = Regex.Matches(page, "(?:CallScriptAsync|InvokeAsync<[^>]+(?:>+)|InvokeVoidAsync)\\(\"([A-Za-z]+)\"")
            .Select(match => match.Groups[1].Value)
            .Where(name => name != "import")
            .Distinct()
            .ToArray();
        Assert.Contains("setHighlights", called);
        Assert.Contains("saveSettings", called);
        foreach (var name in called)
        {
            Assert.Matches(new Regex($"export (?:async )?function {name}\\("), adapter);
        }

        // Every notification the script sends to the page has a [JSInvokable] method with the same name.
        var notified = Regex.Matches(adapter, "notify\\(state, '([A-Za-z]+)'")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToArray();
        Assert.Contains("OnSelection", notified);
        Assert.Contains("OnHighlightTapped", notified);
        foreach (var name in notified)
        {
            Assert.Matches(new Regex($"\\[JSInvokable\\]\\s+public (?:async )?Task(?:<[^>]+>)? {name}\\("), page);
        }
    }

    [Fact]
    public void LoadSettings_MergesTheDeviceOnTopOfTheProfileBase()
    {
        var adapter = Read($"{Web}/wwwroot/js/book-reader.js");
        var merge = Read($"{Web}/wwwroot/js/book-reader-settings.js");

        // The script takes the profile's settings as the base and puts what this device saved on top (device wins per key).
        Assert.Matches(new Regex(@"export function loadSettings\(profile\)\s*\{\s*return mergeSettings\(profile, readDeviceSettings\(globalThis\.localStorage\)\);"), adapter);
        Assert.Matches(new Regex(@"return \{ \.\.\.sanitizeSettings\(profile\), \.\.\.sanitizeSettings\(device\) \};"), merge);
        Assert.Contains("'tuvima-reader-settings'", merge);
        // Reset forgets the device choices; saving is validated and clamped.
        Assert.Matches(new Regex(@"export function clearSettings\(\)"), adapter);
        Assert.Contains("FONT_SIZE_RANGE", merge);
        Assert.Contains("LINE_HEIGHT_RANGE", merge);
    }

    [Fact]
    public void BookReaderPage_StartsFromTheProfileDefaultsAndSavesChangesBackDebounced()
    {
        var page = Read($"{Web}/Components/Pages/BookReader.razor");

        Assert.Contains("IUserPlaybackPreferencesAccessor", page);
        Assert.Contains("InvokeAsync<BookReaderOpenOptions>(\"loadSettings\", _profileBase)", page);
        Assert.Contains("ReaderProfileSettings.ToOpenOptions", page);
        Assert.Contains("TimeSpan.FromMilliseconds(600)", page);
        Assert.Contains("Orchestrator.SavePlaybackSettingsAsync", page);
        Assert.Contains("Reset to my defaults", page);
        Assert.Contains("\"clearSettings\"", page);
    }

    [Fact]
    public void ReadingSettingsPage_FollowsTheSettingsPageRules()
    {
        var page = Read($"{Web}/Components/Settings/ReadingSettingsPage.razor");
        // Reading is a section of the Preferences tab on a person's own page in Manage Profiles.
        var settings = Read($"{Web}/Components/Settings/ProfileDetailPage.razor");

        Assert.DoesNotContain("style=\"", page);
        Assert.DoesNotContain("<input", page);
        Assert.DoesNotContain("<button", page);
        Assert.Contains("ApiClient.UpdatePlaybackSettingsAsync(ProfileId!.Value, fresh)", page);
        Assert.Contains("Orchestrator.SavePlaybackSettingsAsync(fresh)", page);
        // Changes are merged into a fresh read of the profile's settings, never saved from the copy loaded when the page opened.
        Assert.Contains("Orchestrator.GetPlaybackSettingsAsync()", page);
        Assert.Contains("<ReadingSettingsPage ", settings);
        Assert.DoesNotContain("PlaybackSettingsMode.Reading", settings);
    }

    [Fact]
    public void Highlights_AreAnchoredByCfiRangeNeverByCharacterOffsets()
    {
        var page = Read($"{Web}/Components/Pages/BookReader.razor");

        Assert.Contains("Api.CreateHighlightAsync(AssetId, selection.Cfi", page);
        // New highlights go through the client call, which writes no offsets; the page never builds the request itself.
        // (ReplaceHighlight copies the retired offsets of an existing row unchanged because the wire type is not a record.)
        Assert.DoesNotContain("CreateReaderHighlightRequestDto", page);
        // Rows from the retired reader (no range) and bookmarks that are not CFIs are never handed to the script.
        Assert.Contains("!string.IsNullOrWhiteSpace(mark.CfiRange)", page);
        Assert.Contains("StartsWith(\"epubcfi(\"", page);
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
