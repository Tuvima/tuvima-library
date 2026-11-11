using System.Text.Json;

namespace MediaEngine.Api.Services.Plugins.Files;

/// <summary>
/// Checks the text of a plugin's file (Plugins and Integrations screens v5 D55). Two layers: the structure against the published
/// schema file for the plugin's kind, then Library's own rules (hosts, permissions, ranges, the same ones the Engine applies on
/// install). Locked fields are compared with a baseline copy. Every finding has a real line, a plain message and a severity: an
/// error blocks saving, a note does not. Pure: reads nothing and writes nothing.
/// </summary>
public static class PluginFileChecker
{
    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>The largest file the checker looks at.</summary>
    public const int MaxCharacters = 512 * 1024;

    /// <param name="text">The text to check.</param>
    /// <param name="format">Which kind of file it is meant to be.</param>
    /// <param name="fileName">The file's name (a source file's name must match its <c>name</c>).</param>
    /// <param name="baselineText">The copy that locked fields are compared with: Tuvima's version, or for a private plugin the file as installed. Null skips the comparison.</param>
    /// <param name="baselineWords">How the baseline is named in messages ("Tuvima's version", "the installed file").</param>
    public static PluginFileCheckResult Check(string text, PluginFileFormat format, string fileName, string? baselineText, string baselineWords = "Tuvima's version")
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return Result([new PluginFileProblem(1, 1, PluginFileSeverity.Error, PluginFileProblemCodes.Syntax, "The file is empty. A plugin file is one JSON object, starting with { and ending with }.")]);
        }

        if (text.Length > MaxCharacters)
        {
            return Result([new PluginFileProblem(1, 1, PluginFileSeverity.Error, PluginFileProblemCodes.Rule, "The file is too large to check (over 512 KB). A plugin file is a small description, not data.")]);
        }

        var map = JsonPositionMap.TryBuild(text, out var syntax);
        if (map is null)
        {
            return Result([syntax!]);
        }

        using var document = JsonDocument.Parse(text, ReadOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            var (line, column) = map.Best(string.Empty);
            return Result([new PluginFileProblem(line, column, PluginFileSeverity.Error, PluginFileProblemCodes.Type, "The file must be one JSON object, starting with { and ending with }.")]);
        }

        var problems = new List<PluginFileProblem>();
        var schema = format switch
        {
            PluginFileFormat.SourceFile => PluginSchemaValidator.SourceSchema,
            PluginFileFormat.SettingsDescription => PluginSchemaValidator.SettingsDescriptionSchema,
            _ => PluginSchemaValidator.ManifestSchema(root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : null),
        };
        foreach (var finding in OnePerPlace(PluginSchemaValidator.Validate(root, schema)))
        {
            var (line, column) = finding.Code == PluginFileProblemCodes.UnknownWord
                ? map.Best(finding.Pointer)
                : map.ValueAt(finding.Pointer) ?? map.Best(finding.Pointer);
            problems.Add(new PluginFileProblem(line, column, finding.Severity, finding.Code, finding.Message));
        }

        var structureHasErrors = problems.Any(problem => problem.Severity == PluginFileSeverity.Error);
        var structureLines = problems.Where(problem => problem.Severity == PluginFileSeverity.Error).Select(problem => problem.Line).ToHashSet();
        var rules = new List<PluginFileProblem>();
        if (format == PluginFileFormat.SourceFile)
        {
            PluginFileLibraryRules.CheckSource(root, map, fileName, structureHasErrors, rules);
        }
        else if (format == PluginFileFormat.Manifest)
        {
            PluginFileLibraryRules.CheckManifest(root, map, structureHasErrors, rules);
        }

        // A rule that complains about a line the structure layer already complained about is the same finding said twice.
        problems.AddRange(rules.Where(rule => rule.Severity == PluginFileSeverity.Note || !structureLines.Contains(rule.Line)));

        if (format != PluginFileFormat.SettingsDescription && baselineText is not null && TryParse(baselineText) is { } baseline)
        {
            using (baseline)
            {
                PluginLockedFields.Check(root, baseline.RootElement, format, map, baselineWords, problems);
            }
        }

        return Result(problems);
    }

    // The same place can fail two layers of the same schema (an enum and a const, say); one finding per place and kind is enough.
    private static IEnumerable<SchemaFinding> OnePerPlace(IReadOnlyList<SchemaFinding> findings) =>
        findings.GroupBy(finding => (finding.Pointer, finding.Code)).Select(group => group.First());

    private static JsonDocument? TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text, ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PluginFileCheckResult Result(IEnumerable<PluginFileProblem> problems) =>
        new([.. problems
            .DistinctBy(problem => (problem.Line, problem.Code, problem.Message))
            .OrderBy(problem => problem.Line)
            .ThenBy(problem => problem.Column)
            .ThenBy(problem => problem.Severity)]);
}
