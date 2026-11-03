namespace MediaEngine.Web.Components.Settings;

/// <summary>The forms of the "what it provides" display (Plugins and Integrations screens v5 D53).</summary>
public enum PluginProvidesMode
{
    /// <summary>Icons with names on one line (list view).</summary>
    Named,

    /// <summary>Coloured icons only (tiles).</summary>
    Icons,

    /// <summary>Structured cards with a sentence and media-type chips (the plugin page).</summary>
    Card,
}
