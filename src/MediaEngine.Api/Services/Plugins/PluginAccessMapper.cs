using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Turns a <see cref="PluginDeclaration"/> into the Access section of the Plugins page (Plugin architecture spec, 4a). The one
/// place the words are made, so a plugin and a source say the same thing the same way. Pure: no storage, no clock.
/// </summary>
internal static class PluginAccessMapper
{
    public const string NothingLeaves = "Nothing leaves this machine";

    private static readonly IReadOnlyDictionary<string, string> MediaLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["books"] = "Books",
        ["comics"] = "Comics",
        ["audiobooks"] = "Audiobooks",
        ["movies"] = "Movies",
        ["tv"] = "TV",
        ["music"] = "Music",
        ["photos"] = "Photos",
    };

    /// <param name="declaration">The item's declaration, or null when it states nothing.</param>
    /// <param name="destinations">Each place the item contacts: the host and a plain sentence on what is sent (null for a source, which does not say).</param>
    /// <param name="usesKey">True when the item sends the person's own key with its requests.</param>
    /// <param name="workedOnLabels">Plain labels for the media it works on (empty means every kind).</param>
    /// <param name="libraryIds">Libraries the administrator limited it to (empty means all).</param>
    /// <param name="listsOnly">True for a source that only reads lists a person pastes as an address (Collections and lists spec D7).</param>
    public static PluginAccessDto Map(
        PluginDeclaration? declaration,
        IReadOnlyList<(string Host, string? Sends)> destinations,
        bool usesKey,
        IReadOnlyList<string> workedOnLabels,
        IEnumerable<string>? mediaTypes,
        string? category,
        IReadOnlyList<string>? libraryIds = null,
        bool listsOnly = false)
    {
        var resolved = PluginDeclarationRules.Resolve(declaration, destinations.Count > 0, mediaTypes, category);
        return new PluginAccessDto
        {
            Audience = PluginDeclarationRules.AudienceWord(resolved.Audience),
            AudienceIsDefault = !resolved.AudienceStated,
            WorksOn = [.. workedOnLabels],
            LimitedToLibraryIds = [.. libraryIds ?? []],
            VaultUse = PluginDeclarationRules.VaultWord(resolved.VaultUse),
            VaultWords = PluginDeclarationRules.VaultWords(resolved.VaultUse),
            DataLeaving = DataLeavingLabel(destinations, usesKey, listsOnly),
            WeightClass = PluginDeclarationRules.WeightWord(resolved.WeightClass),
            TellMeOnFailure = resolved.TellMeOnFailure,
            Changelog = declaration?.Changelog?.Trim(),
            Languages = [.. declaration?.Languages ?? []],
            Requires = (declaration?.Requires ?? []).Select(requirement => new PluginRequirementDto
            {
                Kind = requirement.Kind.Trim().ToLowerInvariant(),
                Id = requirement.Id.Trim(),
                VersionRange = requirement.VersionRange,
                Optional = requirement.Optional,
            }).ToList(),
            SavedOutput = declaration?.Stores?.SavedOutput,
            KeepsRecords = declaration?.Stores?.Records == true,
            OnRemoval = resolved.RemoveOutputOnRemoval ? "remove" : "keep",
            DisplaySlots = (declaration?.DisplaySlots ?? []).Select(slot => slot.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList(),
            Actions = (declaration?.Actions ?? []).Select(action => new PluginActionDto
            {
                Id = action.Id,
                Kind = action.Kind.Trim().ToLowerInvariant(),
                Label = action.Label.Trim(),
                Slot = (action.Slot ?? "item_menu").Trim().ToLowerInvariant(),
                Audience = PluginDeclarationRules.AudienceWord(PluginDeclarationRules.ActionAudience(action, resolved.Audience)),
                Priority = action.Priority,
                Edits = PluginDeclarationRules.ActionEdits(action),
            }).OrderBy(action => action.Priority).ThenBy(action => action.Label, StringComparer.OrdinalIgnoreCase).ToList(),
            TextTrack = declaration?.TextTrack?.Trim().ToLowerInvariant(),
        };
    }

    /// <summary>Plain labels for plugin media types; an empty list means every kind.</summary>
    public static List<string> MediaLabelsFor(IEnumerable<string> mediaTypes) =>
        mediaTypes.Where(type => MediaLabels.ContainsKey(type)).Select(type => MediaLabels[type]).ToList();

    /// <summary>
    /// The data-leaving label (D8), worked out from the network list and the use of a key, never typed by hand: "Nothing leaves
    /// this machine", "Sends titles and years to X" (the plugin's own plain sentence, when it gives one) or "Sends your key and
    /// searches to X".
    /// </summary>
    public static string DataLeavingLabel(IReadOnlyList<(string Host, string? Sends)> destinations, bool usesKey, bool listsOnly = false)
    {
        if (destinations.Count == 0)
        {
            return NothingLeaves;
        }

        var hosts = string.Join(", ", destinations.Select(destination => destination.Host).Distinct(StringComparer.OrdinalIgnoreCase));
        if (listsOnly)
        {
            // A list source asks for one address the person pasted: nothing about their library goes with it.
            return usesKey
                ? $"Your key, this address and the list's identifiers go to {hosts}"
                : $"Only this address and the list's identifiers go to {hosts}";
        }

        if (usesKey)
        {
            return $"Sends your key and searches to {hosts}";
        }

        if (destinations.Count == 1 && destinations[0].Sends is { Length: > 0 } sends)
        {
            var sentence = sends.Trim().TrimEnd('.');
            // "Sends titles and years" is used as written; any other sentence is lead in with "Sends".
            sentence = sentence.StartsWith("Sends ", StringComparison.OrdinalIgnoreCase)
                ? sentence
                : $"Sends {char.ToLowerInvariant(sentence[0])}{sentence[1..]}";
            return $"{sentence} to {hosts}";
        }

        return $"Sends titles and details to {hosts}";
    }
}
