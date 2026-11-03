using System.Reflection;
using System.Text.Json;

namespace MediaEngine.Plugins;

/// <summary>
/// Reads the settings description a compiled-in plugin ships as a JSON file beside its code (Plugins and Integrations screens v5
/// D51, D58). The file is an embedded resource named <c>shipped-plugin-settings.{file}</c>, so it travels inside the plugin's
/// assembly into every publish output, and it is read through the same settings vocabulary as any other plugin's JSON. The C# of
/// a plugin never describes a setting.
/// </summary>
public static class ShippedPluginSettings
{
    /// <summary>The prefix of the embedded resource name of every shipped settings file.</summary>
    public const string ResourcePrefix = "shipped-plugin-settings.";

    /// <summary>
    /// Reads <paramref name="fileName"/> (for example <c>commercial-skip.settings.json</c>) from <paramref name="assembly"/>.
    /// A missing or malformed file is a build mistake, so it throws rather than showing a plugin with no settings.
    /// </summary>
    public static JsonElement Read(Assembly assembly, string fileName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var resource = ResourcePrefix + fileName;
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The shipped settings file '{fileName}' is not embedded in {assembly.GetName().Name}.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return document.RootElement.Clone();
    }

    /// <summary>The names of every shipped settings file embedded in <paramref name="assembly"/>, without the resource prefix.</summary>
    public static IReadOnlyList<string> FilesIn(Assembly assembly) =>
    [
        .. assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(name => name[ResourcePrefix.Length..])
            .Order(StringComparer.Ordinal),
    ];
}
