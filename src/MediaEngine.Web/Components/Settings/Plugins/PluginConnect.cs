namespace MediaEngine.Web.Components.Settings;

/// <summary>One field of the Connect dialog (Plugins and Integrations screens v6 D62): a key, a secret, a name or an address.</summary>
public sealed record PluginConnectField(string Key, string Label, bool Secret = false, bool Required = true, string? Placeholder = null, string? Help = null);

/// <summary>What a test in the Connect dialog found: <see cref="Ok"/> and one plain sentence ("Connection verified." or the reason).</summary>
public sealed record PluginConnectResult(bool Ok, string Message);
