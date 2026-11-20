using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MediaEngine.Web.Tests;

/// <summary>
/// One save rule across every Settings page (settings-alignment D2 and D10): switches and choices save the moment they change, typed
/// fields save when you leave them, and a page keeps a Save button only where several fields must apply together, as the one shared
/// "Save changes" button (<c>SettingsSaveBar</c>). Times are pickers that follow the region's clock, and headings and buttons are in
/// sentence case. These tests keep the eight differently named Save buttons from coming back.
/// </summary>
public sealed partial class SettingsSaveRuleGuardrailTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string WebRoot = Path.Combine(RepoRoot, "src", "MediaEngine.Web");
    private static readonly string SettingsRoot = Path.Combine(WebRoot, "Components", "Settings");

    // Save buttons that are not a page's save: each is a form with its own Cancel (a dialog, drawer or create form) or an action
    // that does more than save. Key is the file name and the exact label.
    private static readonly Dictionary<string, string> AllowedSaveLabels = new(StringComparer.Ordinal)
    {
        ["ManagedAccessUsers.razor|Save email"] = "A drawer form with Cancel: changing a sign-in's email is one deliberate step, not a Settings page value.",
        ["ManagedAccessApplications.razor|Save application"] = "The create and edit form of an application, with Cancel.",
        ["ManagedApplicationWebhooks.razor|Save webhook"] = "The create and edit form of a webhook (a URL and its events together), with Cancel.",
        ["RemoteAccessSettingsPanel.razor|Save and verify"] = "An action, not a plain save: it saves the public address and then checks that the outside world can reach it.",
        ["DocumentRuleEditorDialog.razor|Save rule"] = "A dialog: the rule editor, with Cancel.",
    };

    // Resource keys whose "Save ..." value is allowed, for the same reasons as above.
    private static readonly HashSet<string> AllowedSaveKeys = new(StringComparer.Ordinal)
    {
        "ManagedAccessUsers_SaveEmail",
        "ManagedApplicationWebhooks_SaveWebhook",
        "RemoteAccessSettingsPanel_SaveAndVerify",
        "DocumentRuleEditorDialog_SaveRule",
    };

    // Words that keep their capital in a sentence-case heading or button: product, page and mode names and acronyms.
    private static readonly HashSet<string> ProperNames = new(StringComparer.Ordinal)
    {
        "Library", "Libraries", "Vault", "Needs", "Review", "Tools", "Activity", "Matching", "Profiles", "Profile", "Local", "AI",
        "Plugins", "Integrations", "Backups", "System", "General", "Network", "Managed", "Shared", "Tailscale", "OpenID", "Connect",
        "Qwen3", "Advanced", "Essential", "Standard", "Engine", "Dashboard", "Models", "Hardware", "Accounts", "Devices", "Security",
        "Users", "Households", "Home", "Remote", "Access", "Settings", "Wikidata", "Universe", "Series", "Edition", "Work",
    };

    [GeneratedRegex(@"""Save (?!changes"")[a-z][^""]*""")]
    private static partial Regex SaveLabel();

    [GeneratedRegex(@"\b(?:Placeholder|HelpText|Label|Description|Helper)=""[^""]*\b[Hh]{1,2}:mm\b")]
    private static partial Regex ClockFormatText();

    [GeneratedRegex(@"<AppButton\s(?:[^>""]|""[^""]*"")*?\bLabel=""(?<label>[^""]*)""|<(?:AppSettingsGroup|SettingsSectionPanel)\s(?:[^>""]|""[^""]*"")*?\b(?:Heading|Title)=""(?<label>[^""]*)""")]
    private static partial Regex HeadingOrButton();

    private static IEnumerable<string> SettingsRazorFiles() =>
        Directory.EnumerateFiles(SettingsRoot, "*.razor", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal);

    private static string Relative(string path) => Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');

    [Fact]
    public void NoSettingsButtonIsNamedSaveSomething_ExceptTheAllowedForms()
    {
        var offenders = new List<string>();
        foreach (var path in SettingsRazorFiles())
        {
            var text = LocalizedSource.ReadAllText(path);
            foreach (Match match in SaveLabel().Matches(text))
            {
                var label = match.Value.Trim('"');
                if (!AllowedSaveLabels.ContainsKey(Path.GetFileName(path) + "|" + label))
                {
                    offenders.Add($"{Relative(path)}: {label}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A Settings page saves by itself (switches, choices, typed fields) or uses the shared Save changes button (SettingsSaveBar). Found: " + string.Join("; ", offenders));
        Assert.All(AllowedSaveLabels.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void NoSettingsString_SaysSaveSomethingSettings()
    {
        // The words a Settings component asks the string table for: its keys start with the component's own name.
        var components = SettingsRazorFiles().Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        var doc = XDocument.Load(Path.Combine(WebRoot, "Resources", "SharedStrings.resx"));
        var offenders = doc.Root!.Elements("data")
            .Select(d => (Key: d.Attribute("name")!.Value, Value: d.Element("value")?.Value ?? string.Empty))
            .Where(e => components.Contains(e.Key.Split('_')[0]))
            .Where(e => e.Value.StartsWith("Save ", StringComparison.Ordinal) && e.Value != "Save changes" && !AllowedSaveKeys.Contains(e.Key))
            .Select(e => $"{e.Key} = {e.Value}")
            .ToList();

        Assert.True(offenders.Count == 0, "Settings strings that name a separate Save button: " + string.Join("; ", offenders));
    }

    [Fact]
    public void TimeFields_ArePickersThatFollowTheRegionClock_NotTypedText()
    {
        var typedClock = new List<string>();
        foreach (var path in SettingsRazorFiles())
        {
            if (ClockFormatText().IsMatch(LocalizedSource.ReadAllText(path)))
            {
                typedClock.Add(Relative(path));
            }
        }

        Assert.True(typedClock.Count == 0, "Settings text asks for a typed 24-hour time; use a time picker instead: " + string.Join(", ", typedClock));

        // The maintenance, backup and quiet-hours times are the native picker, which shows the region's 12 or 24-hour clock.
        Assert.Equal(1, TimePickers("BackupSchedulePanel.razor"));
        Assert.Equal(2, TimePickers("NotificationsPanel.razor"));
        Assert.Equal(2, TimePickers("EncodeSettingsTab.razor"));
    }

    private static int TimePickers(string file)
    {
        var path = SettingsRazorFiles().Single(p => Path.GetFileName(p) == file);
        return Regex.Matches(File.ReadAllText(path), @"InputType=""AppInputType\.Time""").Count;
    }

    [Fact]
    public void TheSharedSaveChangesBar_IsUsedByRealSettingsPages()
    {
        var users = SettingsRazorFiles()
            .Where(p => !p.EndsWith("SettingsSaveBar.razor", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p).Contains("<SettingsSaveBar", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);

        // Where several fields must apply together: a path plus limits, a pair of times, a set of limits, a provider's details.
        foreach (var expected in new[]
                 {
                     "LocalAiRuntimePanel.razor", "LocalAiBudgetPanel.razor", "EncodeSettingsTab.razor", "AdvancedNetworkSettingsPanel.razor",
                     "LogRetentionPanel.razor", "SecurityTab.razor",
                 })
        {
            Assert.Contains(expected, users);
        }
    }

    [Fact]
    public void SettingsHeadingsAndButtons_AreInSentenceCase()
    {
        var offenders = new List<string>();
        foreach (var path in SettingsRazorFiles())
        {
            foreach (Match match in HeadingOrButton().Matches(LocalizedSource.ReadAllText(path)))
            {
                var label = match.Groups["label"].Value;
                if (IsTitleCase(label))
                {
                    offenders.Add($"{Path.GetFileName(path)}: {label}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Use sentence case (only the first word and names keep a capital): " + string.Join("; ", offenders.Distinct()));
    }

    // Two or more words after the first, every one capitalised, none a product or page name or an acronym: "Save Runtime Settings".
    private static bool IsTitleCase(string label)
    {
        if (label.Contains('@') || label.Contains('{'))
        {
            return false;
        }

        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Where(w => w.Length > 1 && char.IsLetter(w[0]) && w.Any(char.IsLower)).ToList();
        return words.Count >= 2 && words.All(w => char.IsUpper(w[0]) && !ProperNames.Contains(w.Trim(',', '.', '…')));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
