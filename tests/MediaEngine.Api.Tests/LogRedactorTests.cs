using MediaEngine.Api.Services.EngineLogs;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Private details are hidden before a log line leaves the server: keys and tokens, sign-in details, and the paths of
/// files and folders. Each pattern has its own case, and so does what must be left alone.
/// </summary>
public sealed class LogRedactorTests
{
    // ── Keys and tokens ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Calling provider with Authorization: Bearer abc123.def456-ghi789 now", "abc123.def456-ghi789")]
    [InlineData("header was bearer SGVsbG8gV29ybGQ=", "SGVsbG8gV29ybGQ=")]
    public void BearerValues_AreHidden(string line, string secret)
    {
        var result = LogRedactor.Redact(line);

        Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        Assert.Contains("[hidden]", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET https://api.example.org/v3/search?api_key=9f8e7d6c5b4a&query=dune", "9f8e7d6c5b4a")]
    [InlineData("request used apikey=SECRETVALUE1 for lookup", "SECRETVALUE1")]
    [InlineData("url ?token=tok_live_12345&page=2", "tok_live_12345")]
    [InlineData("settings key=abcd1234 loaded", "abcd1234")]
    [InlineData("client secret=shhh-it-is-private ok", "shhh-it-is-private")]
    [InlineData("connect failed password=hunter2 retrying", "hunter2")]
    [InlineData("body {\"password\":\"hunter2\",\"user\":\"x\"}", "hunter2")]
    [InlineData("tried 'api_key': 'quoted-secret-value'", "quoted-secret-value")]
    [InlineData("Authorization: topsecretscheme", "topsecretscheme")]
    public void KeyedValues_AreHidden_WhateverTheSeparatorOrQuoting(string line, string secret)
    {
        var result = LogRedactor.Redact(line);

        Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        Assert.Contains("[hidden]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNameOfAKeyedValue_StaysSoTheLineStillReads()
    {
        Assert.Equal("lookup failed for api_key=[hidden]&query=dune", LogRedactor.Redact("lookup failed for api_key=abc123&query=dune"));
    }

    [Fact]
    public void JsonWebTokens_AreHidden()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r";

        var result = LogRedactor.Redact($"session cookie was {jwt} at sign");

        Assert.DoesNotContain("eyJhbGci", result, StringComparison.Ordinal);
        Assert.Contains("[hidden]", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("digest 3f786850e387550fdab836ed7e6dc881de23001b3f786850e387550fdab836ed done")]
    [InlineData("secret blob QWxhZGRpbjpvcGVuIHNlc2FtZVRoaXNJc0FMb25nQmFzZTY0U3RyaW5n12 end")]
    [InlineData("hash d41d8cd98f00b204e9800998ecf8427e computed")]
    public void LongHexAndBase64Runs_AreHidden(string line)
    {
        var result = LogRedactor.Redact(line);

        Assert.Contains("[hidden]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("d41d8cd98f00b204e9800998ecf8427e", result, StringComparison.Ordinal);
        Assert.DoesNotContain("3f786850e387550fdab836ed7e6dc881", result, StringComparison.Ordinal);
        Assert.DoesNotContain("QWxhZGRpbjpvcGVu", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RunAndItemIds_AreNotMistakenForSecrets()
    {
        var id = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

        Assert.Equal($"Ingestion run {id} finished", LogRedactor.Redact($"Ingestion run {id} finished"));
    }

    [Fact]
    public void LongWordsAndShortValues_AreLeftAlone()
    {
        const string line = "The internationalizationconfigurationmanagement step took 12 ms for key count 4";

        Assert.Equal(line, LogRedactor.Redact(line));
    }

    // ── Sign-in details ──────────────────────────────────────────────────────

    [Fact]
    public void EmailAddresses_AreHidden()
    {
        var result = LogRedactor.Redact("Invitation sent to Shy.Faruqi+tuvima@example.co.uk successfully");

        Assert.Equal("Invitation sent to [email] successfully", result);
    }

    [Theory]
    [InlineData("Sign-in failed for alice from the home network", "alice")]
    [InlineData("Signed in as alice", "alice")]
    [InlineData("Login attempt by alice was refused", "alice")]
    [InlineData("Sign-in rejected: user alice not found", "alice")]
    [InlineData("Sign-in failed, username=alice", "alice")]
    [InlineData("login failed account 'alice'", "alice")]
    public void TheUserNamedInASignInMessage_IsHidden(string line, string user)
    {
        var result = LogRedactor.Redact(line);

        Assert.DoesNotContain(user, result, StringComparison.Ordinal);
        Assert.Contains("[user]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void WordsLikeUserOutsideASignInMessage_AreLeftAlone()
    {
        const string line = "Scan finished for user library folder after 3 passes";

        Assert.Equal(line, LogRedactor.Redact(line));
    }

    // ── Paths ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"Could not read C:\Media\Movies\Dune (2021)\Dune.mkv today", @"C:\Media")]
    [InlineData(@"Could not read C:\Media\Movies\Dune (2021)\Dune.mkv today", "Dune")]
    [InlineData(@"Folder 'D:\My Music\Pink Floyd' is missing", "Pink Floyd")]
    [InlineData("Could not read C:/Media/Movies/Arrival.mkv today", "Arrival")]
    [InlineData(@"moving \\unused C:\Users\shy\Downloads\New Folder\Some Very Long Name.epub failed", "Some Very Long")]
    [InlineData("Could not read /mnt/media/Movies/Dune (2021)/Dune.mkv today", "/mnt")]
    [InlineData("Could not read /mnt/media/Movies/Dune (2021)/Dune.mkv today", "Dune")]
    [InlineData("Watching '/home/shy/My Library/books' for changes", "shy")]
    [InlineData("copy /var/lib/tuvima/library.db now", "tuvima")]
    public void FileAndFolderPaths_AreHidden_WithTheirNames(string line, string mustNotAppear)
    {
        var result = LogRedactor.Redact(line);

        Assert.DoesNotContain(mustNotAppear, result, StringComparison.Ordinal);
        Assert.Contains("[path]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePathIsReplacedAndTheRestOfTheSentenceStays()
    {
        Assert.Equal("Could not read [path] today", LogRedactor.Redact(@"Could not read C:\Media\Movies\Dune.mkv today"));
        Assert.Equal("Could not read [path] today", LogRedactor.Redact("Could not read /mnt/media/Movies/Dune.mkv today"));
    }

    [Theory]
    [InlineData(@"Could not read C:\Media\Movies\Dune (2021)\Dune.mkv today", "Could not read [path]/Dune.mkv today")]
    [InlineData("Could not read /mnt/media/Movies/Dune (2021)/Dune.mkv today", "Could not read [path]/Dune.mkv today")]
    [InlineData("Could not read C:/Media/Arrival.mkv today", "Could not read [path]/Arrival.mkv today")]
    public void WithFileNamesOn_OnlyTheLastSegmentRemains_NeverTheFolders(string line, string expected)
    {
        var result = LogRedactor.Redact(line, showFileNames: true);

        Assert.Equal(expected, result);
        Assert.DoesNotContain("Media", result.Replace("Could not read", string.Empty), StringComparison.Ordinal);
        Assert.DoesNotContain("Movies", result, StringComparison.Ordinal);
    }

    [Fact]
    public void WithFileNamesOn_AFileNameThatLooksLikeAKey_IsStillShownAsAName()
    {
        const string name = "Dune_Part_Two_2024_2160p_UHD_BluRay_x265_HDR.mkv";

        var result = LogRedactor.Redact($@"Could not read C:\Media\Movies\{name} today", showFileNames: true);

        Assert.Equal($"Could not read [path]/{name} today", result);
    }

    [Fact]
    public void WithFileNamesOn_AFolderOnlyPath_ShowsItsLastFolderName()
    {
        Assert.Equal("Watching [path]/books now", LogRedactor.Redact("Watching /home/shy/library/books now", showFileNames: true));
    }

    [Theory]
    [InlineData("Request finished HTTP/1.1 GET https://example.org/api/v1/logs/technical - 200")]
    [InlineData("Posted to http://localhost:61495/system/scheduled-tasks")]
    [InlineData("Finished 10/19/2026 and/or later at 3/4 speed")]
    [InlineData("route /api")]
    public void WebAddressesDatesAndSingleWords_AreNotTakenForPaths(string line)
    {
        Assert.Equal(line, LogRedactor.Redact(line));
    }

    [Fact]
    public void ASecretInsideAPath_AndAPathNextToASecret_AreBothHidden()
    {
        var result = LogRedactor.Redact(@"Failed C:\Media\a.mkv with token=abc123XYZ");

        Assert.DoesNotContain("abc123XYZ", result, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\", result, StringComparison.Ordinal);
    }

    // ── Network paths, folders with spaces, Authorization headers ───────────

    [Theory]
    [InlineData(@"Could not read \\NAS\Shy\Movies\Dune (2021)\Dune.mkv today")]
    [InlineData(@"Could not read \\NAS\Shy\Movies\Dune Part Two (2024)\Dune Part Two.mkv today")]
    [InlineData("Could not read //NAS/Shy/Movies/Dune (2021)/Dune.mkv today")]
    public void NetworkPaths_AreHidden_IncludingSpacesAndParentheses(string line)
    {
        var result = LogRedactor.Redact(line);

        Assert.Equal("Could not read [path] today", result);
        Assert.DoesNotContain("NAS", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Shy", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Dune", result, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkPaths_WithFileNamesOn_KeepOnlyTheLastSegment()
    {
        var result = LogRedactor.Redact(@"Could not read \\NAS\Shy\Movies\Dune (2021)\Dune.mkv today", showFileNames: true);

        Assert.Equal("Could not read [path]/Dune.mkv today", result);
    }

    [Fact]
    public void ANetworkShareWithNoFile_IsHiddenToo()
    {
        Assert.Equal("Mount of [path] failed", LogRedactor.Redact(@"Mount of \\NAS\Shy failed"));
    }

    [Theory]
    [InlineData(@"Library folder D:\Media\My Folder", "Library folder [path]")]
    [InlineData(@"Library folder D:\Media\My Folder is missing", "Library folder [path] is missing")]
    [InlineData(@"Library folder D:\Media\My Folder, skipping", "Library folder [path], skipping")]
    [InlineData("Library folder /mnt/media/My Folder is missing", "Library folder [path] is missing")]
    public void AWindowsOrPosixFolderWithSpacesAndNoTrailingSlash_IsFullyHidden(string line, string expected)
    {
        var result = LogRedactor.Redact(line);

        Assert.Equal(expected, result);
        Assert.DoesNotContain("Folder", result.Replace("Library folder", string.Empty), StringComparison.Ordinal);
    }

    // ── Names with an apostrophe and no extension: over-hide ────────────────

    [Theory]
    [InlineData(@"D:\Media\Schindler's List", "[path]")]
    [InlineData(@"Could not open D:\Media\Schindler's List", "Could not open [path]")]
    [InlineData(@"Could not open D:\Media\Schindler's List, skipping", "Could not open [path], skipping")]
    [InlineData(@"Could not open D:\Media\Schindler's List; skipping", "Could not open [path]; skipping")]
    [InlineData(@"Could not open (D:\Media\Schindler's List) today", "Could not open ([path]) today")]
    [InlineData(@"Could not open D:\Media\Schindler's List. Next step ran", "Could not open [path]. Next step ran")]
    [InlineData(@"Could not open D:\Media\Schindler's List is missing", "Could not open [path] is missing")]
    [InlineData("Could not open /mnt/media/Schindler's List, skipping", "Could not open [path], skipping")]
    [InlineData("Could not open /mnt/media/Schindler's List", "Could not open [path]")]
    public void ANameWithAnApostropheAndNoExtension_IsHiddenWholeUpToTheEndOfThePath(string line, string expected)
    {
        var result = LogRedactor.Redact(line);

        Assert.Equal(expected, result);
        Assert.DoesNotContain("Schindler", result, StringComparison.Ordinal);
        Assert.DoesNotContain("List", result.Replace("[path]", string.Empty), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"Could not open 'D:\Media\Schindler's List' today", "Could not open '[path]' today")]
    [InlineData(@"Could not open ""D:\Media\Schindler's List"" today", @"Could not open ""[path]"" today")]
    [InlineData(@"Folder 'D:\My Music\Pink Floyd' is missing", "Folder '[path]' is missing")]
    [InlineData("Watching '/home/shy/My Library/Schindler's List' for changes", "Watching '[path]' for changes")]
    [InlineData(@"Could not open '\\NAS\Shy\Schindler's List' now", "Could not open '[path]' now")]
    [InlineData(@"Could not open 'D:\Media\Schindler's List\a's file.mkv'", "Could not open '[path]'")]
    public void AQuotedPath_IsHiddenBetweenItsQuotes_AnApostropheInsideDoesNotCloseThem(string line, string expected)
    {
        var result = LogRedactor.Redact(line);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void AQuotedPath_WithFileNamesOn_KeepsTheLastSegmentInsideTheQuotes()
    {
        var result = LogRedactor.Redact(@"Could not open 'D:\Media\Movies\Schindler's List\Schindler's List.mkv' now", showFileNames: true);

        Assert.Equal("Could not open '[path]/Schindler's List.mkv' now", result);
        Assert.DoesNotContain("Movies", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuoteThatIsNotAPath_IsLeftAlone()
    {
        const string line = "It's 'fine' and the user's setting is 'on' at /api";

        Assert.Equal(line, LogRedactor.Redact(line));
    }

    [Theory]
    [InlineData("Authorization: Basic dXNlcjpwYXNz", "Authorization: [hidden]")]
    [InlineData("Authorization: Bearer abc.def.ghi", "Authorization: [hidden]")]
    [InlineData("authorization: Digest username=\"shy\", realm=\"tuvima\", response=\"abc\"", "authorization: [hidden]")]
    [InlineData("Authorization: Negotiate YIIGhgYGKwYBBQUCoIIGejCCBnagMDAu", "Authorization: [hidden]")]
    [InlineData("Authorization=Custom some secret words", "Authorization=[hidden]")]
    [InlineData("Proxy-Authorization: Basic dXNlcjpwYXNz", "Proxy-Authorization: [hidden]")]
    public void AuthorizationHeaders_AreHiddenWholeAfterTheName_WhateverTheScheme(string line, string expected)
    {
        var result = LogRedactor.Redact(line);

        Assert.Equal(expected, result);
        Assert.DoesNotContain("Basic", result, StringComparison.Ordinal);
        Assert.DoesNotContain("dXNlcjpwYXNz", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAuthorizationJsonField_IsHiddenUpToItsClosingQuote_NotTheRestOfTheLine()
    {
        var result = LogRedactor.Redact("{\"authorization\":\"Basic dXNlcjpwYXNz\",\"status\":401}");

        Assert.Equal("{\"authorization\":[hidden],\"status\":401}", result);
    }

    [Fact]
    public void LineWithNothingPrivate_IsReturnedUnchanged()
    {
        const string line = "Scan finished in 4.2 s: 12 added, 0 failed";

        Assert.Same(line, LogRedactor.Redact(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyText_IsReturnedAsItIs(string? text)
    {
        Assert.Equal(text, LogRedactor.Redact(text!));
    }
}
