namespace MediaEngine.Domain.Services;

/// <summary>
/// Where code that cannot be handed the rules (a static reader that runs at scan time) finds them (universal editor v5, D33). The Engine
/// installs the reloading provider once at start; until then, and in any code that never installs one, the shipped rules apply.
/// </summary>
public static class FilenameCleaner
{
    private static volatile FilenameCleaningConfigurationProvider? _source;

    /// <summary>The rules in force.</summary>
    public static FilenameCleaningRules Rules => _source?.Current ?? FilenameCleaningConfigurationProvider.BuiltIn;

    /// <summary>Installs the provider that supplies the rules from now on (null puts the shipped rules back).</summary>
    public static void Use(FilenameCleaningConfigurationProvider? provider) => _source = provider;
}
