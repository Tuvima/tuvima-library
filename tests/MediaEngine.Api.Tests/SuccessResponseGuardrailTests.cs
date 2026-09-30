using System.Text;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.Tests;

public sealed partial class SuccessResponseGuardrailTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    // These routes intentionally have no JSON response schema. They return no body, a file,
    // a redirect, or bytes written directly to HttpResponse. An entry must be removed when a
    // route starts returning JSON, and new entries require the same explicit review.
    private static readonly HashSet<string> UntypedSuccessMetadataAllowlist =
    [
        "AddCollectionItem",
        "CancelEncodeJob",
        "CancelMediaOperation",
        "CompletePasswordReset",
        "DeleteAudiobookBookmark",
        "DeleteAudiobookChapterTitleOverride",
        "DeleteBookmark",
        "DeleteCollection",
        "DeleteCollectionArtwork",
        "DeleteHighlight",
        "DeleteMediaType",
        "DeletePasskey",
        "DeleteViewProfileSource",
        "DeleteProvider",
        "DownloadOfflineVariant",
        "DownloadBackup",
        "GetArtworkVariant",
        "GetCharacterPortrait",
        "GetViewItemPreview",
        "GetAssetBackground",
        "GetAssetCover",
        "GetAssetCoverThumb",
        "GetAssetLogo",
        "GetAssetLyrics",
        "GetAssetSubtitles",
        "GetAssetTextTrack",
        "GetCollectionArtwork",
        "GetEntityCover",
        "GetEpubResource",
        "GetProfileAvatar",
        "GetTvdbMatchPreview",
        "GetPersonHeadshot",
        "GetViewItemContent",
        "GetViewItemThumbnail",
        "GetProviderIcon",
        "HidePlaybackSegment",
        "RemoveCollectionItem",
        "RemoveCollectionPersonalMediaSource",
        "RevokeAuthSession",
        "RevokeClientDevice",
        "ReorderCollectionItems",
        "RetryMediaOperation",
        "StartAiModelDownload",
        "StreamAsset",
        "SetDetailDefaultSequence",
        "SetProfilePin",
        "DecideDevicePairing",
        "UpdateClientCapabilities",
        "ChangePassword",
        "PutSearchResultsCache",
        "RegisterPasskey",
        "UpdateHighlight",
        "UpdateReadingStatistics",
        "UpdateCollection",
        "UpdateCollectionEnabled",
        "UpdateCollectionFeatured",
        "UpdateCollectionPlacements",
        "UpdateServerGeneral",
    ];

    private static readonly HashSet<string> NoSuccessResponseAllowlist = [];

    [Fact]
    public void EndpointSuccessBodies_DoNotUseAnonymousTypes()
    {
        var offenders = new List<string>();

        foreach (var path in EnumerateEndpointFiles())
        {
            var source = File.ReadAllText(path);
            var scrubbed = ScrubCommentsAndLiterals(source);

            foreach (Match match in SuccessFactoryRegex().Matches(scrubbed))
            {
                var openParenthesis = scrubbed.IndexOf('(', match.Index);
                var closeParenthesis = FindMatchingParenthesis(scrubbed, openParenthesis);
                Assert.True(closeParenthesis >= 0, $"Could not parse success result in {ToRelativePath(path)}.");

                var arguments = scrubbed[(openParenthesis + 1)..closeParenthesis];
                if (AnonymousObjectRegex().IsMatch(arguments))
                {
                    offenders.Add($"{ToRelativePath(path)}:{GetLineNumber(source, match.Index)}");
                }
            }

            // Covers response shapers such as `object ToDto(...) => new { ... }` whose anonymous
            // object is passed to Results.Ok at a different call site.
            foreach (Match match in AnonymousObjectResponseHelperRegex().Matches(scrubbed))
            {
                offenders.Add($"{ToRelativePath(path)}:{GetLineNumber(source, match.Index)}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Anonymous endpoint success bodies are forbidden. Promote each wire shape to a named "
            + "response contract: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryEndpointRoute_DeclaresSuccessMetadata_AndJsonMetadataIsTyped()
    {
        var missingSuccessMetadata = new List<string>();
        var untypedJsonMetadata = new List<string>();
        var matchedUntypedAllowlist = new HashSet<string>(StringComparer.Ordinal);
        var matchedNoSuccessAllowlist = new HashSet<string>(StringComparer.Ordinal);
        var routeCount = 0;

        foreach (var path in EnumerateEndpointFiles())
        {
            var source = File.ReadAllText(path);
            var scrubbed = ScrubCommentsAndLiterals(source);

            foreach (Match match in RouteMapRegex().Matches(scrubbed))
            {
                routeCount++;
                var openParenthesis = scrubbed.IndexOf('(', match.Index);
                var closeParenthesis = FindMatchingParenthesis(scrubbed, openParenthesis);
                Assert.True(closeParenthesis >= 0, $"Could not parse route registration in {ToRelativePath(path)}.");

                var statementEnd = FindStatementEnd(scrubbed, closeParenthesis + 1);
                Assert.True(statementEnd >= 0, $"Could not find route-chain terminator in {ToRelativePath(path)}.");

                var block = source[match.Index..(statementEnd + 1)];
                var routeName = RouteNameRegex().Match(block).Groups["name"].Value;
                var routeLabel = string.IsNullOrWhiteSpace(routeName)
                    ? $"{ToRelativePath(path)}:{GetLineNumber(source, match.Index)}"
                    : routeName;

                var hasTypedSuccess = TypedSuccessMetadataRegex().IsMatch(block);
                var hasUntypedSuccess = UntypedSuccessMetadataRegex().IsMatch(block);
                if (!hasTypedSuccess && !hasUntypedSuccess)
                {
                    if (NoSuccessResponseAllowlist.Contains(routeName))
                    {
                        matchedNoSuccessAllowlist.Add(routeName);
                    }
                    else
                    {
                        missingSuccessMetadata.Add(routeLabel);
                    }

                    continue;
                }

                if (ObjectSuccessMetadataRegex().IsMatch(block))
                {
                    untypedJsonMetadata.Add($"{routeLabel} (.Produces<object>)");
                    continue;
                }

                if (!hasTypedSuccess)
                {
                    if (UntypedSuccessMetadataAllowlist.Contains(routeName))
                    {
                        matchedUntypedAllowlist.Add(routeName);
                    }
                    else if (!Regex.IsMatch(block, @"\.Produces\(\s*StatusCodes\.Status204NoContent\s*\)"))
                    {
                        untypedJsonMetadata.Add(routeLabel);
                    }
                }
            }
        }

        // Keep a floor to detect a broken scanner; new routes are checked below
        // without requiring an unrelated hard-coded count update.
        Assert.True(routeCount >= 500, $"Expected the full route inventory, found {routeCount}.");
        Assert.True(
            missingSuccessMetadata.Count == 0,
            "Routes missing explicit 2xx Produces metadata: " + string.Join(", ", missingSuccessMetadata));
        Assert.True(
            untypedJsonMetadata.Count == 0,
            "JSON success routes must use .Produces<T>(); only reviewed bodyless/binary routes may "
            + "use untyped 2xx metadata: " + string.Join(", ", untypedJsonMetadata));

        var staleAllowlist = UntypedSuccessMetadataAllowlist.Except(matchedUntypedAllowlist).ToList();
        Assert.True(
            staleAllowlist.Count == 0,
            "Stale untyped-success allowlist entries must be removed: " + string.Join(", ", staleAllowlist));

        var staleNoSuccessAllowlist = NoSuccessResponseAllowlist.Except(matchedNoSuccessAllowlist).ToList();
        Assert.True(
            staleNoSuccessAllowlist.Count == 0,
            "Stale no-success allowlist entries must be removed: " + string.Join(", ", staleNoSuccessAllowlist));
    }

    [Fact]
    public void SwaggerSchemaIds_UseQualifiedTypeNames()
    {
        var program = File.ReadAllText(Path.Combine(
            RepoRoot,
            "src",
            "MediaEngine.Api",
            "Program.cs"));

        Assert.Contains("options.CustomSchemaIds(", program, StringComparison.Ordinal);
        Assert.Contains("type.FullName", program, StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateEndpointFiles() =>
        Directory.EnumerateFiles(
            Path.Combine(RepoRoot, "src", "MediaEngine.Api", "Endpoints"),
            "*.cs",
            SearchOption.AllDirectories);

    private static int FindMatchingParenthesis(string source, int openingIndex)
    {
        var depth = 0;
        for (var index = openingIndex; index < source.Length; index++)
        {
            if (source[index] == '(')
            {
                depth++;
            }
            else if (source[index] == ')' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindStatementEnd(string source, int startIndex)
    {
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var index = startIndex; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '(':
                    parenthesisDepth++;
                    break;
                case ')':
                    parenthesisDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    braceDepth--;
                    break;
                case ';' when parenthesisDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Removes comments and string/character literal contents while preserving length and line
    /// breaks. This is intentionally a small lexical pass rather than a regex over arbitrary C#;
    /// route/factory parentheses can then be matched without being confused by raw SQL strings,
    /// interpolated URLs, comments, or semicolons inside literals.
    /// </summary>
    private static string ScrubCommentsAndLiterals(string source)
    {
        var output = new StringBuilder(source);
        var index = 0;

        while (index < source.Length)
        {
            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                BlankUntilLineEnd(source, output, ref index);
                continue;
            }

            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                BlankBlockComment(source, output, ref index);
                continue;
            }

            if (source[index] == '"')
            {
                var quoteCount = CountRun(source, index, '"');
                if (quoteCount >= 3)
                {
                    BlankRawString(source, output, ref index, quoteCount);
                }
                else
                {
                    BlankString(source, output, ref index, index > 0 && source[index - 1] == '@');
                }

                continue;
            }

            if (source[index] == '\'')
            {
                BlankCharacter(source, output, ref index);
                continue;
            }

            index++;
        }

        return output.ToString();
    }

    private static void BlankUntilLineEnd(string source, StringBuilder output, ref int index)
    {
        while (index < source.Length && source[index] is not '\r' and not '\n')
        {
            output[index++] = ' ';
        }
    }

    private static void BlankBlockComment(string source, StringBuilder output, ref int index)
    {
        output[index++] = ' ';
        output[index++] = ' ';
        while (index < source.Length)
        {
            if (source[index] == '*' && index + 1 < source.Length && source[index + 1] == '/')
            {
                output[index++] = ' ';
                output[index++] = ' ';
                return;
            }

            if (source[index] is not '\r' and not '\n')
            {
                output[index] = ' ';
            }

            index++;
        }
    }

    private static void BlankString(
        string source,
        StringBuilder output,
        ref int index,
        bool verbatim)
    {
        output[index++] = ' ';
        while (index < source.Length)
        {
            if (!verbatim && source[index] == '\\' && index + 1 < source.Length)
            {
                output[index++] = ' ';
                output[index++] = ' ';
                continue;
            }

            if (source[index] == '"')
            {
                output[index++] = ' ';
                if (verbatim && index < source.Length && source[index] == '"')
                {
                    output[index++] = ' ';
                    continue;
                }

                return;
            }

            if (source[index] is not '\r' and not '\n')
            {
                output[index] = ' ';
            }

            index++;
        }
    }

    private static void BlankRawString(
        string source,
        StringBuilder output,
        ref int index,
        int delimiterLength)
    {
        for (var count = 0; count < delimiterLength; count++)
        {
            output[index++] = ' ';
        }

        while (index < source.Length)
        {
            if (source[index] == '"' && CountRun(source, index, '"') >= delimiterLength)
            {
                for (var count = 0; count < delimiterLength; count++)
                {
                    output[index++] = ' ';
                }

                return;
            }

            if (source[index] is not '\r' and not '\n')
            {
                output[index] = ' ';
            }

            index++;
        }
    }

    private static void BlankCharacter(string source, StringBuilder output, ref int index)
    {
        output[index++] = ' ';
        while (index < source.Length)
        {
            if (source[index] == '\\' && index + 1 < source.Length)
            {
                output[index++] = ' ';
                output[index++] = ' ';
                continue;
            }

            var current = source[index];
            output[index++] = current is '\r' or '\n' ? current : ' ';
            if (current == '\'')
            {
                return;
            }
        }
    }

    private static int CountRun(string source, int index, char character)
    {
        var count = 0;
        while (index + count < source.Length && source[index + count] == character)
        {
            count++;
        }

        return count;
    }

    private static int GetLineNumber(string source, int index) =>
        source.AsSpan(0, index).Count('\n') + 1;

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static string ToRelativePath(string path) =>
        Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');

    [GeneratedRegex(@"\b(?:Results|TypedResults)\.(?:Ok|Accepted|Created)\s*\(")]
    private static partial Regex SuccessFactoryRegex();

    [GeneratedRegex(@"\bnew\s*\{")]
    private static partial Regex AnonymousObjectRegex();

    [GeneratedRegex(@"\b(?:private|internal|public)\s+static\s+object\s+\w+\s*\([^)]*\)\s*=>\s*new\s*\{")]
    private static partial Regex AnonymousObjectResponseHelperRegex();

    [GeneratedRegex(@"\.\s*Map(?:Get|Post|Put|Delete|Patch)\s*\(")]
    private static partial Regex RouteMapRegex();

    [GeneratedRegex(@"\.WithName\s*\(\s*""(?<name>[^""]+)""\s*\)")]
    private static partial Regex RouteNameRegex();

    [GeneratedRegex(@"\.Produces\s*<\s*(?!object\s*>)[^()]+>\s*\(\s*(?:(?:StatusCodes\.)?Status2\d\d[A-Za-z]*)?")]
    private static partial Regex TypedSuccessMetadataRegex();

    [GeneratedRegex(@"\.Produces\s*\(\s*(?:StatusCodes\.)?Status2\d\d[A-Za-z]*")]
    private static partial Regex UntypedSuccessMetadataRegex();

    [GeneratedRegex(@"\.Produces\s*<\s*object\s*>")]
    private static partial Regex ObjectSuccessMetadataRegex();
}
