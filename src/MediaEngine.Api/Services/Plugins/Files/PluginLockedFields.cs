using System.Text.Json;

namespace MediaEngine.Api.Services.Plugins.Files;

/// <summary>Which of the two plugin file formats a text is, so the checker picks the right schema and the right rules.</summary>
public enum PluginFileFormat
{
    /// <summary>A provider source file (<c>config/providers/{name}.json</c>).</summary>
    SourceFile,

    /// <summary>A plugin manifest (<c>plugin.json</c>) or the shipped JSON that describes a compiled-in plugin.</summary>
    Manifest,
}

/// <summary>
/// The fields an administrator cannot change in the file editor (Plugins and Integrations screens v5 D55): the plugin's id, its
/// kind, its provider id, the places it may contact and its permissions. Those need the install-time approval flow, so an edit
/// cannot quietly widen what a plugin may send out. A locked field that differs from the baseline copy is reported as
/// <c>changed_locked</c>.
/// </summary>
internal static class PluginLockedFields
{
    public static void Check(JsonElement proposed, JsonElement baseline, PluginFileFormat format, JsonPositionMap map, string baselineWords, List<PluginFileProblem> problems)
    {
        if (proposed.ValueKind != JsonValueKind.Object || baseline.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (format == PluginFileFormat.Manifest)
        {
            Scalar(proposed, baseline, "id", null, map, baselineWords, problems);
            Scalar(proposed, baseline, "kind", "official", map, baselineWords, problems);
            Set("permissions", Strings(proposed, "permissions"), Strings(baseline, "permissions"), "The permissions it asks for", map, baselineWords, problems);
            Set("network", NetworkHosts(proposed), NetworkHosts(baseline), "The places it may contact", map, baselineWords, problems);
        }
        else
        {
            Scalar(proposed, baseline, "name", null, map, baselineWords, problems);
            Scalar(proposed, baseline, "provider_id", null, map, baselineWords, problems);
            Scalar(proposed, baseline, "plugin_id", null, map, baselineWords, problems);
            Set("endpoints", EndpointHosts(proposed), EndpointHosts(baseline), "The places it may contact", map, baselineWords, problems);
        }
    }

    private static void Scalar(JsonElement proposed, JsonElement baseline, string name, string? defaultValue, JsonPositionMap map, string baselineWords, List<PluginFileProblem> problems)
    {
        var was = Text(baseline, name) ?? defaultValue;
        var now = Text(proposed, name) ?? defaultValue;
        if (string.Equals(was, now, StringComparison.Ordinal))
        {
            return;
        }

        var (line, column) = map.Best("/" + JsonPositionMap.Escape(name));
        var said = now is null ? "the file leaves it out" : $"this file says \"{now}\"";
        var original = was is null ? "it is not set" : $"it is \"{was}\"";
        problems.Add(new PluginFileProblem(line, column, PluginFileSeverity.Error, PluginFileProblemCodes.ChangedLocked,
            $"\"{name}\" is locked and cannot be changed here: in {baselineWords} {original}, and {said}. Put it back, or reset the file to {baselineWords}."));
    }

    private static void Set(string property, IReadOnlySet<string> now, IReadOnlySet<string> was, string meaning, JsonPositionMap map, string baselineWords, List<PluginFileProblem> problems)
    {
        var added = now.Except(was, StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = was.Except(now, StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
        if (added.Count == 0 && removed.Count == 0)
        {
            return;
        }

        var changes = new List<string>();
        if (added.Count > 0)
        {
            changes.Add("added " + string.Join(", ", added.Select(item => $"\"{item}\"")));
        }

        if (removed.Count > 0)
        {
            changes.Add("removed " + string.Join(", ", removed.Select(item => $"\"{item}\"")));
        }

        var (line, column) = map.Best("/" + JsonPositionMap.Escape(property));
        problems.Add(new PluginFileProblem(line, column, PluginFileSeverity.Error, PluginFileProblemCodes.ChangedLocked,
            $"{meaning} are locked and cannot be changed here: compared with {baselineWords} this file has {string.Join(" and ", changes)}. Changing them needs the approval step used when a plugin is installed."));
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static HashSet<string> Strings(JsonElement element, string name)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String))
            {
                set.Add(item.GetString() ?? string.Empty);
            }
        }

        return set;
    }

    private static HashSet<string> NetworkHosts(JsonElement element)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("network", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
            {
                if (Text(item, "host") is { Length: > 0 } host)
                {
                    set.Add(host);
                }
            }
        }

        return set;
    }

    private static HashSet<string> EndpointHosts(JsonElement element)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("endpoints", out var list) && list.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in list.EnumerateObject().Where(item => item.Value.ValueKind == JsonValueKind.String))
            {
                var address = item.Value.GetString() ?? string.Empty;
                set.Add(Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : address);
            }
        }

        return set;
    }
}
