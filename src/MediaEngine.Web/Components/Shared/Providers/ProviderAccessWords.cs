using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Components.Shared.Providers;

/// <summary>Where a source stands on its key: nothing needed, nothing saved yet, or a key saved.</summary>
public enum ProviderAccessState
{
    NoKeyNeeded,
    NoKey,
    KeySaved,
}

/// <summary>
/// The words for a source's key, shared by the plugin page's Access card and the first-run Metadata step so both say the same
/// thing (Plugins and Integrations screens v4 D34): "No key needed", "No key yet", "Key saved"; **Connect** when there is no key,
/// **Replace key** when there is one.
/// </summary>
public static class ProviderAccessWords
{
    public static string StateSentence(IStringLocalizer localizer, ProviderAccessState state) => state switch
    {
        ProviderAccessState.NoKeyNeeded => localizer["ProviderAccess_NoKeyNeeded"].Value,
        ProviderAccessState.KeySaved => localizer["ProviderAccess_KeySaved"].Value,
        _ => localizer["ProviderAccess_NoKey"].Value,
    };

    public static string ConnectLabel(IStringLocalizer localizer) => localizer["ProviderAccess_Connect"].Value;

    public static string ReplaceLabel(IStringLocalizer localizer) => localizer["ProviderAccess_Replace"].Value;

    /// <summary>The one button a source row offers: Connect, or Replace key.</summary>
    public static string ButtonLabel(IStringLocalizer localizer, ProviderAccessState state) =>
        state == ProviderAccessState.KeySaved ? ReplaceLabel(localizer) : ConnectLabel(localizer);
}
