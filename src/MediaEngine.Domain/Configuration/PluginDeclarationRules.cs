using System.Text.RegularExpressions;

namespace MediaEngine.Domain.Configuration;

/// <summary>Who may use what an item provides. Ordered from narrowest to widest, so a larger value is a wider audience.</summary>
public enum PluginAudience
{
    Administrators = 0,
    Adults = 1,
    Everyone = 2,
}

/// <summary>How an item treats Vault items. Worked out from where it sends data, never typed (D4), apart from the two stricter or special words.</summary>
public enum PluginVaultUse
{
    /// <summary>Nothing leaves the machine, so it may work on Vault items under the central access service.</summary>
    LocalOnly = 0,

    /// <summary>Sends titles or details outside, so it may work on a Vault item only when that Vault section has "Look up details online" on.</summary>
    FollowsSectionLookup,

    /// <summary>The Vault's own source (Stash-box); always allowed.</summary>
    OwnSource,

    /// <summary>Never used on Vault items.</summary>
    Never,
}

public enum PluginWeightClass
{
    Light = 0,
    Medium,
    Heavy,
}

/// <summary>An item's declaration with every default filled in. What the Engine enforces and the Plugins page shows.</summary>
public sealed record ResolvedPluginDeclaration(
    PluginAudience Audience,
    bool AudienceStated,
    PluginVaultUse VaultUse,
    PluginWeightClass WeightClass,
    bool RemoveOutputOnRemoval,
    bool TellMeOnFailure,
    bool RetryOnFailure,
    IReadOnlyList<string> MediaTypes,
    string Category);

/// <summary>The one set of rules for a <see cref="PluginDeclaration"/> (PA1): vocabulary, validation and defaults.</summary>
public static partial class PluginDeclarationRules
{
    public const int MaxChangelogLength = 200;
    public const int MaxActionLabelLength = 40;
    public const int MaxActions = 12;
    public const int MaxRequirements = 24;

    public static readonly IReadOnlyList<string> RequirementKinds = ["plugin", "local_ai", "tool", "source", "host_service"];

    /// <summary>The Library surfaces a plugin may contribute data to (C21). A plugin supplies data only; the Library draws it.</summary>
    public static readonly IReadOnlyList<string> DisplaySlots =
    [
        "home_hero", "home_hero_candidates", "item_hero", "item_section", "tile_badge", "player_toolbar", "player_panel", "item_menu", "settings_overview",
    ];

    /// <summary>What an action does. A button appears only when a switched-on item supplies its kind (C4).</summary>
    public static readonly IReadOnlyList<string> ActionKinds = ["find_lyrics", "find_subtitles", "rescan_skip_markers", "refresh_details", "refresh_lore"];

    /// <summary>Kinds the Library itself owns a core player button for. Any other kind goes in the overflow menu.</summary>
    public static readonly IReadOnlyList<string> CoreButtonKinds = ["find_lyrics", "find_subtitles"];

    public static readonly IReadOnlyList<string> ActionSlots = ["item_menu", "player_overflow", "player_core", "panel"];

    public static readonly IReadOnlyList<string> TextTrackKinds = ["lyrics", "subtitles"];

    public static readonly IReadOnlyList<string> CredentialScopeWords = ["household", "personal"];

    /// <summary>True when the declaration lists the credential as <c>personal</c> (each profile links its own), whatever the case or spacing.</summary>
    public static bool IsPersonalCredential(PluginDeclaration? declaration, string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && declaration?.CredentialScopes.Any(pair =>
            string.Equals(pair.Key?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.Value?.Trim(), "personal", StringComparison.OrdinalIgnoreCase)) == true;

    private static readonly string[] KnownMediaTypes = ["books", "comics", "audiobooks", "movies", "tv", "music", "photos"];

    /// <summary>The plain words for an audience, as plugin pages already use them.</summary>
    public static string AudienceWords(PluginAudience audience) => audience switch
    {
        PluginAudience.Everyone => "everyone, including child profiles",
        PluginAudience.Adults => "adults",
        _ => "administrators",
    };

    /// <summary>The wire word for an audience.</summary>
    public static string AudienceWord(PluginAudience audience) => audience switch
    {
        PluginAudience.Everyone => "everyone",
        PluginAudience.Adults => "adults",
        _ => "administrators",
    };

    public static bool TryParseAudience(string? word, out PluginAudience audience)
    {
        switch (word?.Trim().ToLowerInvariant())
        {
            case "administrators": audience = PluginAudience.Administrators; return true;
            case "adults": audience = PluginAudience.Adults; return true;
            case "everyone": audience = PluginAudience.Everyone; return true;
            default: audience = PluginAudience.Administrators; return false;
        }
    }

    /// <summary>The wire word for how an item treats Vault items.</summary>
    public static string VaultWord(PluginVaultUse use) => use switch
    {
        PluginVaultUse.LocalOnly => "local_only",
        PluginVaultUse.FollowsSectionLookup => "follows_section_lookup",
        PluginVaultUse.OwnSource => "own_source",
        _ => "never",
    };

    /// <summary>The plain sentence for the Vault line of the Access section.</summary>
    public static string VaultWords(PluginVaultUse use) => use switch
    {
        PluginVaultUse.LocalOnly => "Allowed (local only)",
        PluginVaultUse.FollowsSectionLookup => "Only if the section's online lookup is on",
        PluginVaultUse.OwnSource => "Allowed (the Vault's own source)",
        _ => "Not used",
    };

    public static string WeightWord(PluginWeightClass weight) => weight switch
    {
        PluginWeightClass.Heavy => "heavy",
        PluginWeightClass.Medium => "medium",
        _ => "light",
    };

    /// <summary>
    /// Checks a declaration. Returns every problem as a plain sentence; an empty list means it is fine.
    /// </summary>
    public static IReadOnlyList<string> Validate(PluginDeclaration? declaration)
    {
        var problems = new List<string>();
        if (declaration is null)
        {
            return problems;
        }

        if (declaration.Audience is not null && !TryParseAudience(declaration.Audience, out _))
        {
            problems.Add("'audience' must be administrators, adults or everyone.");
        }

        if (declaration.Vault is not null && declaration.Vault.Trim().ToLowerInvariant() is not ("never" or "own_source"))
        {
            problems.Add("'vault' must be 'never' or 'own_source' when stated. Where the item sends data decides the rest.");
        }

        if (declaration.WeightClass is not null && declaration.WeightClass.Trim().ToLowerInvariant() is not ("light" or "medium" or "heavy"))
        {
            problems.Add("'weight_class' must be light, medium or heavy.");
        }

        if (declaration.OnFailure is not null && declaration.OnFailure.Trim().ToLowerInvariant() is not ("skip_and_retry" or "skip"))
        {
            problems.Add("'on_failure' must be skip_and_retry or skip.");
        }

        if (declaration.Notify is not null && declaration.Notify.Trim().ToLowerInvariant() is not ("quiet" or "tell_me"))
        {
            problems.Add("'notify' must be quiet or tell_me.");
        }

        if (declaration.Stores?.OnRemoval is { } onRemoval && onRemoval.Trim().ToLowerInvariant() is not ("keep" or "remove"))
        {
            problems.Add("'stores.on_removal' must be keep or remove.");
        }

        if (declaration.Changelog is { } changelog
            && (string.IsNullOrWhiteSpace(changelog) || changelog.Length > MaxChangelogLength || changelog.Any(char.IsControl)))
        {
            problems.Add($"'changelog' must be one line of 1 to {MaxChangelogLength} characters.");
        }

        if (declaration.TextTrack is not null && !TextTrackKinds.Contains(declaration.TextTrack.Trim().ToLowerInvariant()))
        {
            problems.Add("'text_track' must be lyrics or subtitles.");
        }

        foreach (var language in declaration.Languages)
        {
            if (string.IsNullOrWhiteSpace(language) || !LanguageCode().IsMatch(language))
            {
                problems.Add($"Language '{language}' is not a language code such as en or pt-BR.");
            }
        }

        foreach (var slot in declaration.DisplaySlots)
        {
            if (!DisplaySlots.Contains(slot?.Trim().ToLowerInvariant() ?? ""))
            {
                problems.Add($"Display slot '{slot}' is not one of: {string.Join(", ", DisplaySlots)}.");
            }
        }

        foreach (var (name, scope) in declaration.CredentialScopes)
        {
            if (!CredentialScopeWords.Contains(scope?.Trim().ToLowerInvariant() ?? ""))
            {
                problems.Add($"Credential '{name}' must be household or personal.");
            }
        }

        if (declaration.Requires.Count > MaxRequirements)
        {
            problems.Add($"'requires' lists more than {MaxRequirements} things.");
        }

        var seenRequirements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in declaration.Requires)
        {
            requirement.Kind ??= "";
            requirement.Id ??= "";
            if (!RequirementKinds.Contains(requirement.Kind.Trim().ToLowerInvariant()))
            {
                problems.Add($"A requirement has kind '{requirement.Kind}'; use one of: {string.Join(", ", RequirementKinds)}.");
            }
            else if (string.IsNullOrWhiteSpace(requirement.Id))
            {
                problems.Add($"A '{requirement.Kind}' requirement has no id.");
            }
            else if (!seenRequirements.Add($"{requirement.Kind.Trim().ToLowerInvariant()}:{requirement.Id.Trim()}"))
            {
                problems.Add($"Requirement {requirement.Kind} '{requirement.Id}' is listed more than once.");
            }

            if (requirement.VersionRange is { } range && !VersionRangeSyntax().IsMatch(range))
            {
                problems.Add($"Requirement '{requirement.Id}' has a version_range that is not like 1.0.0 or 1.0.0-2.0.0.");
            }
        }

        if (declaration.Actions.Count > MaxActions)
        {
            problems.Add($"'actions' lists more than {MaxActions} actions.");
        }

        var seenActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in declaration.Actions)
        {
            if (!ActionId().IsMatch(action.Id ?? ""))
            {
                problems.Add($"Action id '{action.Id}' must be lower-case words and hyphens.");
            }
            else if (!seenActions.Add(action.Id!))
            {
                problems.Add($"Action '{action.Id}' is listed more than once.");
            }

            var kind = action.Kind?.Trim().ToLowerInvariant() ?? "";
            if (!ActionKinds.Contains(kind))
            {
                problems.Add($"Action '{action.Id}' has kind '{action.Kind}'; use one of: {string.Join(", ", ActionKinds)}.");
            }

            if (string.IsNullOrWhiteSpace(action.Label) || action.Label.Length > MaxActionLabelLength || action.Label.Any(char.IsControl))
            {
                problems.Add($"Action '{action.Id}' needs a label of 1 to {MaxActionLabelLength} characters.");
            }

            var slot = (action.Slot ?? "item_menu").Trim().ToLowerInvariant();
            if (!ActionSlots.Contains(slot))
            {
                problems.Add($"Action '{action.Id}' has slot '{action.Slot}'; use one of: {string.Join(", ", ActionSlots)}.");
            }
            else if (slot == "player_core" && !CoreButtonKinds.Contains(kind))
            {
                // A plugin never makes a loose button: only a Library-defined action type may ask for a core position.
                problems.Add($"Action '{action.Id}' asks for a core player button, which only {string.Join(" and ", CoreButtonKinds)} actions may.");
            }

            if (action.Audience is not null && !TryParseAudience(action.Audience, out _))
            {
                problems.Add($"Action '{action.Id}' has an audience that is not administrators, adults or everyone.");
            }

            if (action.Priority is < 0 or > 100)
            {
                problems.Add($"Action '{action.Id}' priority must be 0 to 100.");
            }
        }

        return problems;
    }

    /// <summary>
    /// Fills every default. <paramref name="sendsDataOut"/> is true when the item declares network destinations; it decides the Vault rule
    /// (D4). <paramref name="mediaTypes"/> and <paramref name="category"/> come from where each format keeps them.
    /// </summary>
    public static ResolvedPluginDeclaration Resolve(PluginDeclaration? declaration, bool sendsDataOut, IEnumerable<string>? mediaTypes, string? category)
    {
        var stated = declaration?.Audience is not null && TryParseAudience(declaration.Audience, out _);
        var audience = PluginAudience.Administrators;
        if (stated)
        {
            TryParseAudience(declaration!.Audience, out audience);
        }

        var vaultWord = declaration?.Vault?.Trim().ToLowerInvariant();
        var vault = vaultWord switch
        {
            "never" => PluginVaultUse.Never,
            "own_source" => PluginVaultUse.OwnSource,
            _ => sendsDataOut ? PluginVaultUse.FollowsSectionLookup : PluginVaultUse.LocalOnly,
        };

        var weight = declaration?.WeightClass?.Trim().ToLowerInvariant() switch
        {
            "heavy" => PluginWeightClass.Heavy,
            "medium" => PluginWeightClass.Medium,
            _ => PluginWeightClass.Light,
        };

        var types = (mediaTypes ?? [])
            .Select(value => value?.Trim().ToLowerInvariant())
            .Where(value => value is not null && KnownMediaTypes.Contains(value))
            .Select(value => value!)
            .Distinct()
            .ToList();

        return new ResolvedPluginDeclaration(
            audience,
            stated,
            vault,
            weight,
            string.Equals(declaration?.Stores?.OnRemoval, "remove", StringComparison.OrdinalIgnoreCase),
            string.Equals(declaration?.Notify, "tell_me", StringComparison.OrdinalIgnoreCase),
            !string.Equals(declaration?.OnFailure, "skip", StringComparison.OrdinalIgnoreCase),
            types,
            category ?? "utilities");
    }

    /// <summary>The audience of one action: its own, or the item's.</summary>
    public static PluginAudience ActionAudience(PluginActionDeclaration action, PluginAudience itemAudience) =>
        TryParseAudience(action.Audience, out var own) && own <= itemAudience ? own : itemAudience;

    /// <summary>True when the action changes metadata or files, which makes it desktop-only.</summary>
    public static bool ActionEdits(PluginActionDeclaration action) =>
        action.Edits ?? !(action.Kind is "find_lyrics" or "find_subtitles" or "rescan_skip_markers");

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LanguageCode();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-\d+\.\d+\.\d+)?$")]
    private static partial Regex VersionRangeSyntax();

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$")]
    private static partial Regex ActionId();
}
