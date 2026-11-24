using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Secrets;

namespace MediaEngine.Api.DevSupport;

/// <summary>One kind of media the run cannot identify, why, and the files it therefore never registered.</summary>
public sealed record BaselineHeldKind(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sources")] IReadOnlyList<string> Sources,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("files")] IReadOnlyList<string> Files);

/// <summary>
/// The provider keys of a baseline run. A real install seals its saved keys with its own root key, which a run in its own
/// folder does not have, so the copied key files cannot be opened and the Engine quietly holds back every file whose
/// only identification sources need a key (films, TV and comics). This class points the run at the install's root key
/// (read only), checks every copied key file, and says plainly which kinds of media the run cannot identify.
/// </summary>
public static class BaselineKeys
{
    public const string Readable = "readable";
    public const string CannotBeRead = "cannot be read with this run's root key";
    public const string NoneSaved = "no key saved";

    /// <summary>Sample folder to the library kind key the Engine's source rules know it by.</summary>
    private static readonly (string Folder, string Kind)[] HeldCandidates = [("movies", "movies"), ("tv", "tv"), ("comics", "comics")];

    /// <summary>
    /// When no root key is named in the environment and this computer's own install has one, names it for this process
    /// (and so for the Engine the run starts). The file is only ever read. Call before the run changes its data folder.
    /// Returns a plain sentence saying which root key the run uses.
    /// </summary>
    public static string PointAtInstallKey() =>
        PointAtInstallKey(
            Environment.GetEnvironmentVariable(FileSecretsRootKey.KeyEnvironmentVariable),
            Environment.GetEnvironmentVariable(FileSecretsRootKey.KeyFileEnvironmentVariable),
            FileSecretsRootKey.DefaultPath(),
            value => Environment.SetEnvironmentVariable(FileSecretsRootKey.KeyFileEnvironmentVariable, value));

    public static string PointAtInstallKey(string? keyFromEnvironment, string? keyFileFromEnvironment, string installKeyPath, Action<string> setKeyFile)
    {
        if (!string.IsNullOrWhiteSpace(keyFromEnvironment))
        {
            return $"Root key: taken from {FileSecretsRootKey.KeyEnvironmentVariable}.";
        }

        if (!string.IsNullOrWhiteSpace(keyFileFromEnvironment))
        {
            return File.Exists(keyFileFromEnvironment)
                ? $"Root key: the file named by {FileSecretsRootKey.KeyFileEnvironmentVariable} (read only)."
                : $"Root key: {FileSecretsRootKey.KeyFileEnvironmentVariable} names a file that does not exist, so saved provider keys will not open.";
        }

        if (File.Exists(installKeyPath))
        {
            setKeyFile(installKeyPath);
            return "Root key: this computer's own install key (read only), so its saved provider keys open in the run.";
        }

        return "Root key: none found on this computer, so saved provider keys cannot open in the run.";
    }

    /// <summary>The state of every provider key file in the run's copied config folder, by provider name. Names only, never values.</summary>
    public static SortedDictionary<string, string> Inspect(string configDirectory, ISecretsRootKey? rootKey = null)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var store = new ConfigFolderSecretsStore(configDirectory, rootKey);
        foreach (var owner in store.ListOwners().Where(o => o.Kind == SecretOwnerKind.Provider))
        {
            result[owner.Name] = store.GetState(owner) switch
            {
                SecretFileState.Readable => Readable,
                SecretFileState.CannotBeRead => CannotBeRead,
                _ => NoneSaved,
            };
        }

        return result;
    }

    /// <summary>
    /// The library kinds whose files the Engine will hold back for lack of a working identification source (the same rule
    /// the Engine and Dashboard use, <see cref="IdentificationSourceRules"/>), with the files of the sample folder that are
    /// therefore never registered.
    /// </summary>
    public static List<BaselineHeldKind> FindHeldKinds(string configDirectory, string sampleRoot, IReadOnlyCollection<string> catalogueFolders, IReadOnlyDictionary<string, string> keyStates)
    {
        var held = new List<BaselineHeldKind>();
        foreach (var (folder, kind) in HeldCandidates)
        {
            var sources = IdentificationSourceRules.RequiredFor(kind);
            if (sources.Count == 0 || !catalogueFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var reasons = new List<string>();
            var anyWorks = false;
            foreach (var source in sources)
            {
                var reason = WhyNotWorking(configDirectory, source, keyStates);
                anyWorks |= reason is null;
                if (reason is not null)
                {
                    reasons.Add(reason);
                }
            }

            if (!anyWorks)
            {
                held.Add(new BaselineHeldKind(kind, sources, string.Join("; ", reasons), ListFiles(sampleRoot, folder)));
            }
        }

        return held;
    }

    /// <summary>Null when the provider works; otherwise a plain reason.</summary>
    private static string? WhyNotWorking(string configDirectory, string provider, IReadOnlyDictionary<string, string> keyStates)
    {
        var path = Path.Combine(configDirectory, "providers", provider + ".json");
        if (!File.Exists(path))
        {
            return $"{provider} has no provider file";
        }

        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (node["enabled"]?.GetValue<bool>() == false)
        {
            return $"{provider} is switched off";
        }

        if (node["requires_api_key"]?.GetValue<bool>() != true
            || !string.IsNullOrWhiteSpace(node["http_client"]?["api_key"]?.GetValue<string>())
            || keyStates.GetValueOrDefault(provider) == Readable)
        {
            return null;
        }

        return keyStates.TryGetValue(provider, out var state) ? $"the saved {provider} key {state}" : $"{provider} has {NoneSaved}";
    }

    private static List<string> ListFiles(string sampleRoot, string folder)
    {
        var path = Path.Combine(sampleRoot, folder);
        return !Directory.Exists(path)
            ? []
            : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(sampleRoot, file).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToList();
    }

    /// <summary>Plain-English lines for the console; empty when nothing is held.</summary>
    public static IEnumerable<string> Explain(IReadOnlyList<BaselineHeldKind> held) =>
        held.Select(kind =>
            $"WARNING: this run cannot identify {kind.Kind}: {kind.Reason}. Its {kind.Files.Count} file(s) will not be registered at all; the record lists them under held_kinds.");
}
