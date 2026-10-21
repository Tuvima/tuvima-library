using System.Security.Cryptography;

namespace MediaEngine.Api.Services.Plugins.Service;

/// <summary>
/// The keys the Library has handed to plugin programs. A key is random, made fresh each time a program is started, kept only
/// in memory (as a hash) and good for one plugin: it opens the plugin door as that plugin and nothing else. Stopping the
/// program, switching the plugin off or restarting the Library ends the key.
/// </summary>
internal sealed class PluginKeyRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _hashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes a new key for the plugin, replacing any earlier one, and returns it.</summary>
    public string Issue(string pluginId)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            _hashes[pluginId] = Hash(key);
        }

        return key;
    }

    public void Revoke(string pluginId)
    {
        lock (_gate)
        {
            _hashes.Remove(pluginId);
        }
    }

    /// <summary>The plugin a key belongs to, or null when the key is unknown, wrong or revoked.</summary>
    public string? Resolve(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 256)
        {
            return null;
        }

        var hash = Hash(key);
        lock (_gate)
        {
            string? found = null;
            foreach (var (pluginId, stored) in _hashes)
            {
                // Every entry is compared, so how long this takes does not reveal which plugin was close.
                if (CryptographicOperations.FixedTimeEquals(hash, stored))
                {
                    found = pluginId;
                }
            }

            return found;
        }
    }

    private static byte[] Hash(string key) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
}
