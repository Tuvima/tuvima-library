using System.Text.Json;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Setup;

/// <summary>What the page model needs to know about one plugin, whichever file format it came from.</summary>
internal sealed record PluginPageModelInput
{
    public required string PluginId { get; init; }

    public required string Name { get; init; }

    /// <summary><c>source_file</c>, <c>manifest</c> or <c>compiled</c>.</summary>
    public string Definition { get; init; } = "manifest";

    public string Version { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary>How the plugin runs: official, declarative, service or sandboxed.</summary>
    public string Kind { get; init; } = "official";

    /// <summary>official, verified, community or private.</summary>
    public string TrustLevel { get; init; } = "private";

    /// <summary>The file the plugin is defined by (a file name), or null for a compiled-in plugin.</summary>
    public string? FileName { get; init; }

    public IReadOnlyList<string> Provides { get; init; } = [];

    public IReadOnlyList<string> MediaLabels { get; init; } = [];

    public IReadOnlyList<string> Supplies { get; init; } = [];

    public PluginAccessDto Access { get; init; } = new();

    public PluginCredentialSet Credentials { get; init; } = PluginCredentialSet.Empty;

    public IReadOnlyList<PluginNetworkDestination> Destinations { get; init; } = [];

    public PluginSettingsDescription Settings { get; init; } = PluginSettingsDescription.Empty;

    /// <summary>The saved value of each setting by key (defaults already filled in by the caller where the file has them).</summary>
    public IReadOnlyDictionary<string, JsonElement> Values { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>Household credentials that are set.</summary>
    public IReadOnlySet<string> ConfiguredCredentials { get; init; } = new HashSet<string>();

    /// <summary>Values held outside the plugin's saved settings, by setting key (a list of servers, say), read through <c>value_source</c>.</summary>
    public IReadOnlyDictionary<string, JsonElement> ExternalValues { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>Tests the file itself declares are added to these; a source file that declares none falls back to what its JSON implies.</summary>
    public IReadOnlyList<PluginDeclaredAction> DefaultActions { get; init; } = [];

    /// <summary>A core source (always on, no switch) has no Settings card.</summary>
    public bool NoSettingsCard { get; init; }

    public IReadOnlyList<PluginSettingsNote> ExtraNotes { get; init; } = [];

    /// <summary>The plugin's logging cap (D59). When set, the Settings card ends with the two Activity log controls that change it.</summary>
    public PluginActivityCap? ActivityCap { get; init; }
}

/// <summary>
/// Builds a plugin's page model (Plugins and Integrations screens v5 D46, D48, D51) from facts the plugin's JSON already holds:
/// Access, What it provides, Settings, Privacy and About. Pure: no storage, no network, no clock. The Plugin file card is added by
/// a later packet. A plugin never supplies layout, only content, and a card it has nothing for is left out.
/// </summary>
internal static class PluginPageModelBuilder
{
    public const string AccessCard = "access";
    public const string ProvidesCard = "provides";
    public const string SettingsCard = "settings";
    public const string PrivacyCard = "privacy";
    public const string AboutCard = "about";
    public const string ActivityEntriesKey = "max_entries";
    public const string ActivityDaysKey = "max_days";

    public static PluginSetupPageResponse Build(PluginPageModelInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var page = new PluginSetupPageResponse
        {
            PluginId = input.PluginId,
            PluginName = input.Name,
            Definition = input.Definition,
        };

        var actions = input.Settings.Actions.Count > 0 ? input.Settings.Actions : input.DefaultActions;
        var controls = BuildControls(input);

        page.Cards.Add(BuildAccess(input, actions));
        if (BuildProvides(input) is { } provides)
        {
            page.Cards.Add(provides);
        }

        if (!input.NoSettingsCard && controls.Count > 0)
        {
            page.Cards.Add(new PluginSetupCardDto
            {
                Key = SettingsCard,
                Title = "Settings",
                Controls = controls,
                Actions = [.. actions.Select(ToDto)],
            });
        }

        page.Cards.Add(BuildPrivacy(input));
        page.Cards.Add(BuildAbout(input));
        page.ThingsToKnow = [.. input.Settings.ThingsToKnow.Concat(input.ExtraNotes).Select(note => new PluginSetupNoteDto { Where = note.Where, Word = note.Word, Message = note.Message })];
        return page;
    }

    private static PluginSetupCardDto BuildAccess(PluginPageModelInput input, IReadOnlyList<PluginDeclaredAction> actions)
    {
        var credentials = input.Credentials.Credentials.Select(credential => new PluginSetupCredentialDto
        {
            Name = credential.Name,
            Label = credential.Label,
            Help = credential.Help,
            Required = credential.Required,
            MinimumLength = credential.MinimumLength,
            MaximumLength = credential.MaximumLength,
            Scope = credential.Scope,
            SignInMethod = credential.SignInMethod,
            Configured = credential.Scope == PluginSettingScopes.Household ? input.ConfiguredCredentials.Contains(credential.Name) : null,
        }).ToList();

        var links = new List<PluginSetupLinkDto>();
        AddLink(links, "signup", "Get a key or sign up", input.Credentials.Links.SignupUrl);
        AddLink(links, "help", "Help", input.Credentials.Links.HelpUrl);

        return new PluginSetupCardDto
        {
            Key = AccessCard,
            Title = "Access",
            Access = input.Access,
            Credentials = credentials,
            Links = links,
            Actions = [.. actions.Where(action => action.Kind == PluginDeclaredActions.TestConnection).Select(ToDto)],
            NeedsAttention = credentials.Any(credential => credential is { Required: true, Scope: PluginSettingScopes.Household, Configured: false }),
        };
    }

    private static PluginSetupCardDto? BuildProvides(PluginPageModelInput input)
    {
        var provides = PluginProvides.Resolve(input.Provides).ToList();
        if (provides.Count == 0 && input.Supplies.Count == 0)
        {
            return null;
        }

        return new PluginSetupCardDto
        {
            Key = ProvidesCard,
            Title = "What it provides",
            Provides = provides,
            MediaTypes = [.. input.MediaLabels],
            Supplies = [.. input.Supplies],
        };
    }

    private static PluginSetupCardDto BuildPrivacy(PluginPageModelInput input)
    {
        var links = new List<PluginSetupLinkDto>();
        AddLink(links, "terms", "Terms of use", input.Credentials.Links.TermsUrl);
        AddLink(links, "privacy", "Privacy policy", input.Credentials.Links.PrivacyUrl);
        return new PluginSetupCardDto
        {
            Key = PrivacyCard,
            Title = "Privacy",
            DataLeaving = input.Access.DataLeaving,
            VaultWords = input.Access.VaultWords,
            Links = links,
            Destinations = [.. input.Destinations.Select(destination => new PluginSetupDestinationDto
            {
                Host = destination.Host,
                Sends = destination.Sends,
                MaxRequestsPerMinute = destination.MaxRequestsPerMinute,
            })],
        };
    }

    private static PluginSetupCardDto BuildAbout(PluginPageModelInput input)
    {
        var facts = new List<PluginSetupFactDto>();
        if (!string.IsNullOrWhiteSpace(input.Version))
        {
            facts.Add(new PluginSetupFactDto { Label = "Version", Value = input.Version });
        }

        facts.Add(new PluginSetupFactDto { Label = "From", Value = TrustWords(input.TrustLevel) });
        facts.Add(new PluginSetupFactDto { Label = "Runs as", Value = KindWords(input.Kind) });
        if (!string.IsNullOrWhiteSpace(input.FileName))
        {
            facts.Add(new PluginSetupFactDto { Label = "Defined by", Value = input.FileName });
        }

        if (!string.IsNullOrWhiteSpace(input.Access.Changelog))
        {
            facts.Add(new PluginSetupFactDto { Label = "What changed", Value = input.Access.Changelog });
        }

        return new PluginSetupCardDto
        {
            Key = AboutCard,
            Title = "About",
            Text = input.Description,
            Facts = facts,
        };
    }

    private static List<PluginSetupControlDto> BuildControls(PluginPageModelInput input)
    {
        var controls = new List<PluginSetupControlDto>();
        var declared = input.Settings.Settings;
        foreach (var definition in declared)
        {
            controls.Add(ToControl(definition, input));
        }

        // A credential each person links for themselves is a setting each person makes (D47 scope: profile), so it appears here as
        // well as on the Access card.
        var order = declared.Count == 0 ? 0 : declared.Max(setting => setting.Order) + 1;
        foreach (var credential in input.Credentials.Credentials.Where(credential => credential.Scope == PluginSettingScopes.Profile))
        {
            if (controls.Any(control => string.Equals(control.Key, credential.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            controls.Add(new PluginSetupControlDto
            {
                Key = credential.Name,
                Control = PluginSettingControls.Secret,
                Title = credential.Label,
                Description = credential.Help,
                Scope = PluginSettingScopes.Profile,
                Order = order++,
                SignInMethod = credential.SignInMethod,
            });
        }

        if (input.ActivityCap is { } cap)
        {
            controls.AddRange(ActivityCapControls(cap));
        }

        return controls;
    }

    /// <summary>The two controls every plugin has for its Activity list (D59), kept in the collapsed Advanced section.</summary>
    private static IEnumerable<PluginSetupControlDto> ActivityCapControls(PluginActivityCap cap)
    {
        yield return new PluginSetupControlDto
        {
            Key = ActivityEntriesKey,
            Control = PluginSettingControls.Number,
            Title = "Most entries to keep",
            Description = "The newest entries of this plugin's Activity list that are kept.",
            Unit = "entries",
            Group = "Activity log",
            Order = 2000,
            Advanced = true,
            Minimum = PluginActivityLimits.MinimumEntries,
            Maximum = PluginActivityLimits.MaximumEntries,
            Integer = true,
            Value = JsonSerializer.SerializeToElement(cap.MaxEntries),
            DefaultValue = JsonSerializer.SerializeToElement(PluginActivityLimits.DefaultEntries),
            StoredIn = "activity_cap",
        };
        yield return new PluginSetupControlDto
        {
            Key = ActivityDaysKey,
            Control = PluginSettingControls.Number,
            Title = "Most days to keep",
            Description = "Entries older than this are removed, even when there are fewer than the most entries.",
            Unit = "days",
            Group = "Activity log",
            Order = 2001,
            Advanced = true,
            Minimum = PluginActivityLimits.MinimumDays,
            Maximum = PluginActivityLimits.MaximumDays,
            Integer = true,
            Value = JsonSerializer.SerializeToElement(cap.MaxDays),
            DefaultValue = JsonSerializer.SerializeToElement(PluginActivityLimits.DefaultDays),
            StoredIn = "activity_cap",
        };
    }

    private static PluginSetupControlDto ToControl(PluginSettingDefinition definition, PluginPageModelInput input)
    {
        var secret = definition.Control == PluginSettingControls.Secret;
        var value = definition.ValueSource.Length > 0 && input.ExternalValues.TryGetValue(definition.Key, out var external)
            ? external
            : input.Values.TryGetValue(definition.Key, out var saved) ? saved : definition.Default;
        var control = new PluginSetupControlDto
        {
            Key = definition.Key,
            Control = definition.Control,
            Title = definition.Title,
            Description = definition.Description,
            Placeholder = definition.Placeholder,
            Unit = definition.Unit,
            Group = definition.Group,
            Order = definition.Order,
            Advanced = definition.Advanced,
            ReadOnly = definition.ReadOnly,
            Scope = definition.Scope,
            Minimum = definition.Minimum,
            Maximum = definition.Maximum,
            Integer = definition.IsInteger,
            Choices = [.. definition.Choices.Select(choice => new PluginSetupChoiceDto { Value = choice.Value, Label = choice.Label })],
            DefaultValue = definition.Default,
            StoredIn = definition.ValueSource.Length > 0 ? "value_source" : input.Definition == "source_file" ? "source_config" : "settings",
            ShowWhen = definition.ShowWhen is { } showWhen ? new PluginSetupShowWhenDto { Setting = showWhen.Setting, Value = showWhen.Value } : null,
            Requires = definition.Requires is { } requires ? new PluginSetupRequirementDto { Kind = requires.Kind, Name = requires.Name, Reason = requires.Reason } : null,
        };

        if (secret)
        {
            // A secret is never sent back; the page only learns whether it is set.
            var credential = input.Credentials.Credentials.FirstOrDefault(c => string.Equals(c.Name, definition.Key, StringComparison.OrdinalIgnoreCase));
            control.Configured = definition.Scope == PluginSettingScopes.Household ? input.ConfiguredCredentials.Contains(definition.Key) : null;
            control.SignInMethod = credential?.SignInMethod;
            return control;
        }

        control.Value = value;
        if (definition.GroupList is { } groupList)
        {
            control.GroupList = ToGroupList(definition, groupList, value);
        }

        return control;
    }

    private static PluginSetupGroupListDto ToGroupList(PluginSettingDefinition definition, PluginGroupListDefinition groupList, JsonElement? value)
    {
        var dto = new PluginSetupGroupListDto
        {
            ItemTitle = groupList.ItemTitle,
            AddLabel = groupList.AddLabel,
            RowActions = [.. groupList.RowActions],
            Fields = [.. groupList.Fields.Select(field => new PluginSetupControlDto
            {
                Key = field.Key,
                Control = field.Control,
                Title = field.Title,
                Description = field.Description,
                Placeholder = field.Placeholder,
                Unit = field.Unit,
                Order = field.Order,
                ReadOnly = field.ReadOnly,
                Minimum = field.Minimum,
                Maximum = field.Maximum,
                Integer = field.IsInteger,
                Choices = [.. field.Choices.Select(choice => new PluginSetupChoiceDto { Value = choice.Value, Label = choice.Label })],
                DefaultValue = field.Default,
            })],
        };

        if (value is not { ValueKind: JsonValueKind.Array } items)
        {
            return dto;
        }

        foreach (var item in items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
        {
            var id = ReadString(item, "id") ?? "";
            var row = new PluginSetupGroupItemDto
            {
                Id = id,
                Title = ReadString(item, "title") ?? ReadString(item, "name") ?? id,
                Subtitle = ReadString(item, "subtitle"),
                State = ReadString(item, "state"),
                KeyMissing = item.TryGetProperty("key_missing", out var keyMissing) && keyMissing.ValueKind == JsonValueKind.True,
                Locked = groupList.Locked.Contains(id, StringComparer.Ordinal)
                    || (item.TryGetProperty("locked", out var locked) && locked.ValueKind == JsonValueKind.True),
            };

            foreach (var field in groupList.Fields.Where(field => item.TryGetProperty(field.Key, out _)))
            {
                row.Values[field.Key] = item.GetProperty(field.Key).Clone();
            }

            dto.Items.Add(row);
        }

        return dto;
    }

    private static PluginSetupActionDto ToDto(PluginDeclaredAction action) => new() { Kind = action.Kind, Label = action.Label };

    private static void AddLink(List<PluginSetupLinkDto> links, string role, string label, string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            links.Add(new PluginSetupLinkDto { Role = role, Label = label, Url = url });
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string TrustWords(string trust) => trust switch
    {
        "official" => "Tuvima",
        "verified" => "A plugin Tuvima has checked",
        "community" => "The community, not checked",
        _ => "Added by hand",
    };

    private static string KindWords(string kind) => kind switch
    {
        "declarative" => "A JSON file, no code",
        "service" => "A separate program",
        "sandboxed" => "A sandboxed module",
        _ => "Part of Library",
    };
}
