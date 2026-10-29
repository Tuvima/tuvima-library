using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Services.Settings;

/// <summary>
/// An administrator's unsaved edit of the Ingestion Flow: for each media type the providers in order, each with a role
/// (Primary, Fallback, Adds details) or Off. Nothing reaches the Engine until Save changes; the Engine applies the same
/// one-identity-provider rule again when it receives the edit.
/// </summary>
public sealed class IngestionFlowDraft
{
    public const string Off = "off";

    private readonly Dictionary<string, List<FlowRow>> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FlowOption> _options = new(StringComparer.OrdinalIgnoreCase);

    public sealed class FlowRow(string name, string displayName, string role)
    {
        public string Name { get; } = name;
        public string DisplayName { get; } = displayName;
        public string Role { get; set; } = role;
    }

    public sealed record FlowOption(string Name, string DisplayName, bool Enabled, bool SendsTitlesOnline, IReadOnlyList<string> MediaTypes);

    /// <summary>The media types that have a flow, in the order they are shown.</summary>
    public IReadOnlyList<string> MediaTypes { get; private set; } = [];

    public static IngestionFlowDraft Create(PipelineConfiguration pipelines, PipelineFlowOptionsResponse options, IEnumerable<string> mediaTypeOrder)
    {
        var draft = new IngestionFlowDraft();
        foreach (var option in options.Providers)
        {
            draft._options[option.Name] = new FlowOption(option.Name, option.DisplayName, option.Enabled, option.SendsTitlesOnline, option.MediaTypes);
        }

        var order = mediaTypeOrder.ToList();
        draft.MediaTypes = [.. pipelines.Pipelines.Keys
            .OrderBy(key => IndexOf(order, key))
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)];
        foreach (var mediaType in draft.MediaTypes)
        {
            var rows = pipelines.Pipelines[mediaType].Providers
                .OrderBy(entry => entry.Rank)
                .Select(entry => new FlowRow(entry.Name, draft.DisplayNameOf(entry.Name), RoleOf(entry)))
                .ToList();
            draft._rows[mediaType] = rows;
            draft._baseline[mediaType] = Fingerprint(rows);
        }

        return draft;
    }

    public IReadOnlyList<FlowRow> RowsFor(string mediaType) => _rows.TryGetValue(mediaType, out var rows) ? rows : [];

    public bool IsChanged(string mediaType) => _rows.TryGetValue(mediaType, out var rows) && Fingerprint(rows) != _baseline[mediaType];

    public bool HasChanges => _rows.Keys.Any(IsChanged);

    /// <summary>Installed providers that can handle this media type and are not in its flow yet.</summary>
    public IReadOnlyList<FlowOption> Addable(string mediaType)
    {
        var inFlow = RowsFor(mediaType).Select(row => row.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _options.Values
            .Where(option => !inFlow.Contains(option.Name) && option.MediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public FlowOption? OptionFor(string name) => _options.GetValueOrDefault(name);

    public bool MoveUp(string mediaType, int index) => Swap(mediaType, index, index - 1);

    public bool MoveDown(string mediaType, int index) => Swap(mediaType, index, index + 1);

    public void SetRole(string mediaType, int index, string role)
    {
        var rows = RowsFor(mediaType);
        if (index >= 0 && index < rows.Count)
        {
            rows[index].Role = role;
        }
    }

    /// <summary>Adds a provider to the flow. It is the first identity provider as Primary, otherwise a Fallback, and the administrator can change it.</summary>
    public void Add(string mediaType, string name)
    {
        if (!_rows.TryGetValue(mediaType, out var rows) || rows.Any(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var hasIdentity = rows.Any(row => IsIdentity(row.Role));
        rows.Add(new FlowRow(name, DisplayNameOf(name), hasIdentity ? PipelineFlowRoles.Fallback : PipelineFlowRoles.Primary));
    }

    public void Discard()
    {
        foreach (var mediaType in _rows.Keys.ToList())
        {
            var original = _baseline[mediaType];
            _rows[mediaType] = [.. original.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split(':'))
                .Select(part => new FlowRow(part[0], DisplayNameOf(part[0]), part[1]))];
        }
    }

    /// <summary>The plain-English reason a media type cannot be saved, or null.</summary>
    public string? ProblemFor(string mediaType) =>
        _rows.TryGetValue(mediaType, out var rows) && !rows.Any(row => IsIdentity(row.Role))
            ? $"{mediaType} needs at least one provider that identifies it. Keep one Primary or Fallback provider."
            : null;

    public IReadOnlyList<string> Problems => [.. MediaTypes.Select(ProblemFor).Where(problem => problem is not null).Select(problem => problem!)];

    /// <summary>What Review changes lists: one plain sentence per change, grouped by media type.</summary>
    public IReadOnlyList<(string MediaType, IReadOnlyList<string> Lines)> Review(Func<string, string> mediaTypeLabel)
    {
        var result = new List<(string, IReadOnlyList<string>)>();
        foreach (var mediaType in MediaTypes.Where(IsChanged))
        {
            var before = _baseline[mediaType].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split(':')).ToList();
            var after = _rows[mediaType];
            var lines = new List<string>();
            foreach (var row in after)
            {
                var previous = before.FirstOrDefault(part => string.Equals(part[0], row.Name, StringComparison.OrdinalIgnoreCase));
                if (previous is null && row.Role != Off)
                {
                    lines.Add($"{row.DisplayName} is added as {RoleLabel(row.Role)}.");
                }
                else if (previous is not null && row.Role == Off)
                {
                    lines.Add($"{row.DisplayName} is turned off.");
                }
                else if (previous is not null && previous[1] != row.Role)
                {
                    lines.Add($"{row.DisplayName} changes from {RoleLabel(previous[1])} to {RoleLabel(row.Role)}.");
                }
            }

            var kept = after.Where(row => row.Role != Off && before.Any(part => string.Equals(part[0], row.Name, StringComparison.OrdinalIgnoreCase))).Select(row => row.Name).ToList();
            var keptBefore = before.Where(part => kept.Contains(part[0], StringComparer.OrdinalIgnoreCase)).Select(part => part[0]).ToList();
            if (!kept.SequenceEqual(keptBefore, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add($"The order is now {string.Join(", then ", after.Where(row => row.Role != Off).Select(row => row.DisplayName))}.");
            }

            result.Add((mediaTypeLabel(mediaType), lines));
        }

        return result;
    }

    /// <summary>The edit as the Engine expects it: only the media types that changed, Off rows left out.</summary>
    public PipelineFlowSaveRequest ToRequest()
    {
        var request = new PipelineFlowSaveRequest();
        foreach (var mediaType in MediaTypes.Where(IsChanged))
        {
            request.MediaTypes[mediaType] = [.. _rows[mediaType]
                .Where(row => row.Role != Off)
                .Select(row => new PipelineFlowProviderDto { Name = row.Name, Role = row.Role })];
        }

        return request;
    }

    public static string RoleLabel(string role) => role switch
    {
        PipelineFlowRoles.Primary => "Primary",
        PipelineFlowRoles.Fallback => "Fallback",
        PipelineFlowRoles.Enrichment => "Adds details",
        _ => "Off",
    };

    public static string RoleOf(PipelineProviderEntry entry)
    {
        if (entry.UseAsIdentityFallback)
        {
            return PipelineFlowRoles.Fallback;
        }

        return string.Equals(entry.Purpose, "enrichment", StringComparison.OrdinalIgnoreCase)
            ? PipelineFlowRoles.Enrichment
            : PipelineFlowRoles.Primary;
    }

    private static bool IsIdentity(string role) => role is PipelineFlowRoles.Primary or PipelineFlowRoles.Fallback;

    private string DisplayNameOf(string name) => _options.TryGetValue(name, out var option) ? option.DisplayName : name;

    private bool Swap(string mediaType, int from, int to)
    {
        var rows = RowsFor(mediaType) as List<FlowRow>;
        if (rows is null || from < 0 || to < 0 || from >= rows.Count || to >= rows.Count)
        {
            return false;
        }

        (rows[from], rows[to]) = (rows[to], rows[from]);
        return true;
    }

    private static string Fingerprint(IEnumerable<FlowRow> rows) => string.Concat(rows.Where(row => row.Role != Off).Select(row => $"{row.Name}:{row.Role};"));

    private static int IndexOf(List<string> order, string key)
    {
        var index = order.FindIndex(value => string.Equals(value, key, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }
}
