using System.Reflection;
using System.Text.Json;


namespace MediaEngine.Api.Services.Plugins.Sources;

/// <summary>
/// Puts back a provider file that ships with the Library when it is missing or cannot be read (Plugins and Integrations screens v4
/// D37). The person's keys live in <c>config/secrets</c> and the data store and their choices in the data store, so putting the
/// shipped file back loses nothing they typed; a file that reads fine is never touched. One line is logged per file put back.
/// </summary>
public static class ShippedProviderFiles
{
    private const string ResourcePrefix = "MediaEngine.Api.shipped-providers.";

    /// <summary>Repairs the providers folder of an existing config folder. A folder that does not exist yet is left to the first-run setup.</summary>
    /// <returns>The file names that were put back.</returns>
    public static IReadOnlyList<string> Repair(string configDirectory, Serilog.ILogger logger) =>
        Repair(configDirectory, logger, typeof(ShippedProviderFiles).Assembly);

    internal static IReadOnlyList<string> Repair(string configDirectory, Serilog.ILogger logger, Assembly assembly)
    {
        var providersDirectory = Path.Combine(configDirectory, "providers");
        if (!Directory.Exists(providersDirectory))
        {
            return [];
        }

        var restored = new List<string>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            var fileName = resource[ResourcePrefix.Length..];
            var path = Path.Combine(providersDirectory, fileName);
            if (IsReadable(path))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            using var file = File.Create(path);
            stream.CopyTo(file);
            restored.Add(fileName);
        }

        if (restored.Count > 0)
        {
            logger.Information("Put back {Count} provider file(s) that were missing or unreadable: {Files}", restored.Count, string.Join(", ", restored));
        }

        return restored;
    }

    private static bool IsReadable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
