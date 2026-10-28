namespace MediaEngine.Domain.Plugins;

/// <summary>
/// Why a plugin or source failed (Plugin architecture spec C17). One fixed list, so a broken plugin can be told apart from everything
/// else and the Activity list, Needs attention wording and the plugin-broken notification all use the same causes.
/// </summary>
public enum PluginFailureReason
{
    /// <summary>The plugin itself crashed or has a bug.</summary>
    PluginCrashed = 0,

    ServiceUnreachable,
    ServiceRefused,
    ServiceRateLimited,
    KeyRejected,
    KeyExpired,

    /// <summary>A tool it needs (FFmpeg, Comskip) is missing or does not run.</summary>
    ToolMissing,

    LocalAiOff,
    LocalAiBusy,
    LocalAiOverAllowance,

    /// <summary>The file or item could not be read.</summary>
    ItemUnreadable,

    /// <summary>The file or item is a kind the plugin does not handle.</summary>
    ItemUnsupported,

    LimitStorage,
    LimitRecords,
    LimitRequests,

    /// <summary>The caller is not in the plugin's audience.</summary>
    NotAllowedAudience,

    /// <summary>The item is in a Vault section the plugin may not work on.</summary>
    NotAllowedVault,

    NotAllowedPermission,

    /// <summary>A plugin or ability it requires is off, missing or the wrong version.</summary>
    DependencyOff,

    /// <summary>The Library's own data store failed.</summary>
    LibraryDataStore,

    /// <summary>The disk is full or cannot be written.</summary>
    LibraryDisk,
}

/// <summary>The cause groups of <see cref="PluginFailureReason"/> and the plain words for each reason.</summary>
public static class PluginFailureReasons
{
    /// <summary>The wire word for a reason (lower-case words with underscores).</summary>
    public static string Word(PluginFailureReason reason) => reason switch
    {
        PluginFailureReason.PluginCrashed => "plugin_crashed",
        PluginFailureReason.ServiceUnreachable => "service_unreachable",
        PluginFailureReason.ServiceRefused => "service_refused",
        PluginFailureReason.ServiceRateLimited => "service_rate_limited",
        PluginFailureReason.KeyRejected => "key_rejected",
        PluginFailureReason.KeyExpired => "key_expired",
        PluginFailureReason.ToolMissing => "tool_missing",
        PluginFailureReason.LocalAiOff => "local_ai_off",
        PluginFailureReason.LocalAiBusy => "local_ai_busy",
        PluginFailureReason.LocalAiOverAllowance => "local_ai_over_allowance",
        PluginFailureReason.ItemUnreadable => "item_unreadable",
        PluginFailureReason.ItemUnsupported => "item_unsupported",
        PluginFailureReason.LimitStorage => "limit_storage",
        PluginFailureReason.LimitRecords => "limit_records",
        PluginFailureReason.LimitRequests => "limit_requests",
        PluginFailureReason.NotAllowedAudience => "not_allowed_audience",
        PluginFailureReason.NotAllowedVault => "not_allowed_vault",
        PluginFailureReason.NotAllowedPermission => "not_allowed_permission",
        PluginFailureReason.DependencyOff => "dependency_off",
        PluginFailureReason.LibraryDataStore => "library_data_store",
        _ => "library_disk",
    };

    public static bool TryParse(string? word, out PluginFailureReason reason)
    {
        foreach (var candidate in Enum.GetValues<PluginFailureReason>())
        {
            if (string.Equals(Word(candidate), word, StringComparison.Ordinal))
            {
                reason = candidate;
                return true;
            }
        }

        reason = PluginFailureReason.PluginCrashed;
        return false;
    }

    /// <summary>The cause group: <c>plugin</c>, <c>outside_service</c>, <c>missing_tool</c>, <c>local_ai</c>, <c>item</c>, <c>limit</c>, <c>not_allowed</c>, <c>dependency_off</c> or <c>library</c>.</summary>
    public static string Group(PluginFailureReason reason) => reason switch
    {
        PluginFailureReason.PluginCrashed => "plugin",
        PluginFailureReason.ServiceUnreachable or PluginFailureReason.ServiceRefused or PluginFailureReason.ServiceRateLimited
            or PluginFailureReason.KeyRejected or PluginFailureReason.KeyExpired => "outside_service",
        PluginFailureReason.ToolMissing => "missing_tool",
        PluginFailureReason.LocalAiOff or PluginFailureReason.LocalAiBusy or PluginFailureReason.LocalAiOverAllowance => "local_ai",
        PluginFailureReason.ItemUnreadable or PluginFailureReason.ItemUnsupported => "item",
        PluginFailureReason.LimitStorage or PluginFailureReason.LimitRecords or PluginFailureReason.LimitRequests => "limit",
        PluginFailureReason.NotAllowedAudience or PluginFailureReason.NotAllowedVault or PluginFailureReason.NotAllowedPermission => "not_allowed",
        PluginFailureReason.DependencyOff => "dependency_off",
        _ => "library",
    };

    /// <summary>The plain sentence for a reason, as shown in the Activity list.</summary>
    public static string Words(PluginFailureReason reason) => reason switch
    {
        PluginFailureReason.PluginCrashed => "The plugin stopped unexpectedly",
        PluginFailureReason.ServiceUnreachable => "The outside service could not be reached",
        PluginFailureReason.ServiceRefused => "The outside service refused the request",
        PluginFailureReason.ServiceRateLimited => "The outside service said to slow down",
        PluginFailureReason.KeyRejected => "The outside service did not accept the key",
        PluginFailureReason.KeyExpired => "The key has expired",
        PluginFailureReason.ToolMissing => "A tool it needs is missing",
        PluginFailureReason.LocalAiOff => "Local AI is switched off",
        PluginFailureReason.LocalAiBusy => "Local AI was busy",
        PluginFailureReason.LocalAiOverAllowance => "Local AI is over its hourly allowance",
        PluginFailureReason.ItemUnreadable => "The file could not be read",
        PluginFailureReason.ItemUnsupported => "The file is a kind it does not handle",
        PluginFailureReason.LimitStorage => "It reached its storage limit",
        PluginFailureReason.LimitRecords => "It reached its record limit",
        PluginFailureReason.LimitRequests => "It reached its request limit",
        PluginFailureReason.NotAllowedAudience => "The person is not allowed to use it",
        PluginFailureReason.NotAllowedVault => "Skipped: the item is in a Vault section it may not work on",
        PluginFailureReason.NotAllowedPermission => "It tried something it was not allowed to do",
        PluginFailureReason.DependencyOff => "Something it needs is switched off",
        PluginFailureReason.LibraryDataStore => "The Library's data store failed",
        _ => "The disk is full or cannot be written",
    };
}
