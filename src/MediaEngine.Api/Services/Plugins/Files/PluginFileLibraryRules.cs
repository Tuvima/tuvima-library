using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Api.Services.Plugins.Files;

/// <summary>
/// The second layer of the plugin file check: Library's own rules, the same ones the Engine applies when a plugin is installed or a
/// provider file is loaded (<see cref="PluginNetworkRules"/>, <see cref="PluginCompatibility"/>, <see cref="PluginDeclarationRules"/>,
/// <see cref="PluginPackageRules"/> and <see cref="JsonConfigValidator"/>), so a file the editor accepts is a file the Engine accepts.
/// Each rule says its message in plain words and the checker finds the line.
/// </summary>
internal static partial class PluginFileLibraryRules
{
    /// <summary>A cache time past this many hours is a thing to know: changes at the source take a month or more to show up.</summary>
    public const int LongCacheHours = 24 * 30;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Word();

    /// <summary>Library's rules for a plugin manifest (<c>plugin.json</c>).</summary>
    public static void CheckManifest(JsonElement root, JsonPositionMap map, bool structureHasErrors, List<PluginFileProblem> problems)
    {
        PluginManifest? manifest;
        try
        {
            manifest = root.Deserialize<PluginManifest>(ReadOptions);
        }
        catch (JsonException ex)
        {
            if (!structureHasErrors)
            {
                Add(problems, map, PathToPointer(root, ex.Path), PluginFileProblemCodes.Rule, $"Library cannot read this part of the file ({SafeDetail(ex)}).");
            }

            return;
        }

        if (manifest is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(manifest.Id) && !PluginPackageRules.IsSafeId(manifest.Id))
        {
            Add(problems, map, "/id", PluginFileProblemCodes.Value, "The id may use only letters, digits, dot, dash and underscore, must start and end with a letter or digit, and cannot be a name Windows keeps for itself.");
        }

        var networkProblems = CheckNetwork(manifest, map, problems);

        var consistency = PluginNetworkRules.Validate(manifest.Permissions, manifest.Network);
        if (consistency is not null && networkProblems == 0)
        {
            var at = map.ValueAt("/network") is not null || manifest.Network.Count > 0 ? "/network" : "/permissions";
            Add(problems, map, at, PluginFileProblemCodes.Rule, Plain(consistency));
        }

        var invalid = PluginCompatibility.ValidateManifest(manifest);
        if (invalid is null || string.Equals(invalid, consistency, StringComparison.Ordinal))
        {
            // Nothing else, or the network message already given line by line above.
        }
        else if (invalid.StartsWith("Plugin manifest declaration is not valid", StringComparison.Ordinal))
        {
            foreach (var declarationProblem in PluginDeclarationRules.Validate(manifest.Declaration))
            {
                Add(problems, map, "/declaration", PluginFileProblemCodes.Rule, $"The declaration is not valid: {declarationProblem}");
            }
        }
        else
        {
            Add(problems, map, PointerFromMessage(root, invalid), PluginFileProblemCodes.Rule, Plain(invalid));
        }

        NotesForSettingsSchema(root, map, problems);
    }

    /// <summary>Library's rules for a provider source file (<c>config/providers/{name}.json</c>).</summary>
    public static void CheckSource(JsonElement root, JsonPositionMap map, string fileName, bool structureHasErrors, List<PluginFileProblem> problems)
    {
        ProviderConfiguration? config;
        try
        {
            config = root.Deserialize<ProviderConfiguration>(ReadOptions);
        }
        catch (JsonException ex)
        {
            if (!structureHasErrors)
            {
                Add(problems, map, PathToPointer(root, ex.Path), PluginFileProblemCodes.Rule, $"Library cannot read this part of the file ({SafeDetail(ex)}).");
            }

            return;
        }

        if (config is null)
        {
            return;
        }

        foreach (var message in JsonConfigValidator.Validate(config, "providers/" + fileName))
        {
            Add(problems, map, PointerFromMessage(root, message), PluginFileProblemCodes.Rule, Plain(message));
        }

        if (config.CacheTtlHours is { } hours && hours > LongCacheHours)
        {
            Add(problems, map, "/cache_ttl_hours", PluginFileProblemCodes.Rule,
                $"A cache time of {hours.ToString(CultureInfo.InvariantCulture)} hours means changes at the source can take more than a month to show up here.",
                PluginFileSeverity.Note);
        }

        NotesForSettingsSchema(root, map, problems);
    }

    private static int CheckNetwork(PluginManifest manifest, JsonPositionMap map, List<PluginFileProblem> problems)
    {
        var added = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < manifest.Network.Count; index++)
        {
            var destination = manifest.Network[index];
            var at = "/network/" + index.ToString(CultureInfo.InvariantCulture);
            if (destination is null)
            {
                Add(problems, map, at, PluginFileProblemCodes.Rule, "This network entry is empty.");
                added++;
                continue;
            }

            if (PluginNetworkRules.ValidateHost(destination.Host) is { } hostProblem)
            {
                Add(problems, map, at + "/host", PluginFileProblemCodes.Rule, $"The host \"{destination.Host}\" {hostProblem}.");
                added++;
            }
            else if (!seen.Add(destination.Host))
            {
                Add(problems, map, at + "/host", PluginFileProblemCodes.Rule, $"The host \"{destination.Host}\" is listed more than once.");
                added++;
            }

            var sends = destination.Sends?.Trim();
            if (string.IsNullOrEmpty(sends) || sends.Length > PluginNetworkRules.MaxSendsLength || sends.Any(char.IsControl))
            {
                Add(problems, map, at + "/sends", PluginFileProblemCodes.Rule,
                    $"Say what is sent to \"{destination.Host}\" in 1 to {PluginNetworkRules.MaxSendsLength} characters, in plain words.");
                added++;
            }

            // The requests-per-minute range is in the published schema (a test keeps it equal to PluginNetworkRules' limits), so the
            // structure layer reports it with its line; here it only stops the install-time message below from saying it a second time.
            if (destination.MaxRequestsPerMinute is { } rate && (rate < PluginNetworkRules.MinRequestsPerMinute || rate > PluginNetworkRules.MaxRequestsPerMinute))
            {
                added++;
            }
        }

        return added;
    }

    // The unknown words the settings description reader collects are things to know, with the line of the word.
    private static void NotesForSettingsSchema(JsonElement root, JsonPositionMap map, List<PluginFileProblem> problems)
    {
        if (!root.TryGetProperty("settings_schema", out var schema))
        {
            return;
        }

        foreach (var note in PluginSettingsDescriptionReader.Read(schema).ThingsToKnow)
        {
            var pointer = note.Where.Length == 0
                ? "/settings_schema/" + JsonPositionMap.Escape(note.Word)
                : "/settings_schema/properties/" + JsonPositionMap.Escape(note.Where) + "/" + JsonPositionMap.Escape(note.Word);
            Add(problems, map, pointer, PluginFileProblemCodes.UnknownWord, note.Message, PluginFileSeverity.Note);
        }
    }

    private static void Add(List<PluginFileProblem> problems, JsonPositionMap map, string pointer, string code, string message, PluginFileSeverity severity = PluginFileSeverity.Error)
    {
        var (line, column) = map.ValueAt(pointer) is { } value && code != PluginFileProblemCodes.UnknownWord ? value : map.Best(pointer);
        problems.Add(new PluginFileProblem(line, column, severity, code, message));
    }

    /// <summary>
    /// Finds the place a rule's message talks about. The rules name what they check at the start of the message
    /// (<c>weight must be ...</c>, <c>http_client.timeout_seconds ...</c>) or inside it (<c>invalid minimum_library_version</c>), so the
    /// first message word that is a word in the file, deepest first, is the place.
    /// </summary>
    private static string PointerFromMessage(JsonElement root, string message)
    {
        var leading = message.Split(' ', 2)[0];
        var walked = Walk(root, Word().Matches(leading).Select(match => match.Value));
        if (walked.Length > 0)
        {
            return walked;
        }

        foreach (Match match in Word().Matches(message))
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(match.Value, out _))
            {
                return "/" + JsonPositionMap.Escape(match.Value);
            }
        }

        return string.Empty;
    }

    private static string Walk(JsonElement root, IEnumerable<string> words)
    {
        var pointer = string.Empty;
        var current = root;
        foreach (var word in words)
        {
            if (current.ValueKind == JsonValueKind.Array && current.GetArrayLength() > 0)
            {
                pointer += "/0";
                current = current[0];
            }

            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(word, out var next))
            {
                pointer += "/" + JsonPositionMap.Escape(word);
                current = next;
            }
            else
            {
                break;
            }
        }

        return pointer;
    }

    // A System.Text.Json path such as $.network[0].host becomes the pointer /network/0/host.
    private static string PathToPointer(JsonElement root, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var pointer = string.Empty;
        foreach (Match match in Regex.Matches(path, @"\.([A-Za-z0-9_\-]+)|\[(\d+)\]", RegexOptions.None, TimeSpan.FromMilliseconds(250)))
        {
            pointer += "/" + (match.Groups[1].Success ? JsonPositionMap.Escape(match.Groups[1].Value) : match.Groups[2].Value);
        }

        return pointer;
    }

    private static string SafeDetail(JsonException ex)
    {
        var detail = ex.Message;
        var cut = detail.IndexOf(" Path:", StringComparison.Ordinal);
        return (cut > 0 ? detail[..cut] : detail).TrimEnd('.');
    }

    // The install-time messages talk about "Plugin manifest"; in the editor the reader is looking at the file itself.
    private static string Plain(string message) =>
        message.Replace("Plugin manifest", "This file", StringComparison.Ordinal);
}
