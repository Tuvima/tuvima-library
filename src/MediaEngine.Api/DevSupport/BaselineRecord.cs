using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MediaEngine.Api.DevSupport;

// The ingestion-baseline record: what a full ingestion of a sample library identified. Everything outside
// "metadata" and "timings" is comparable between two runs, so it carries no GUIDs, row ids, run folders or
// wall-clock times. Dictionaries are written in ordinal key order and lists in a stable order.

public sealed class BaselineRecord
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = BaselineRecordJson.SchemaName;

    [JsonPropertyName("metadata")]
    public BaselineMetadata Metadata { get; set; } = new();

    [JsonPropertyName("counts")]
    public BaselineCounts Counts { get; set; } = new();

    [JsonPropertyName("items")]
    public List<BaselineItem> Items { get; set; } = [];

    [JsonPropertyName("view_items")]
    public List<BaselineViewItem> ViewItems { get; set; } = [];

    [JsonPropertyName("collections")]
    public List<BaselineCollection> Collections { get; set; } = [];

    [JsonPropertyName("series_members")]
    public List<BaselineSeriesMember> SeriesMembers { get; set; } = [];

    [JsonPropertyName("unattached_review")]
    public List<BaselineReview> UnattachedReview { get; set; } = [];

    [JsonPropertyName("timings")]
    public BaselineTimings Timings { get; set; } = new();
}

/// <summary>Run facts. Not compared: it names the build, the folders and the clock of one run.</summary>
public sealed class BaselineMetadata
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("build_commit")]
    public string? BuildCommit { get; set; }

    [JsonPropertyName("sample_root")]
    public string? SampleRoot { get; set; }

    [JsonPropertyName("started_at")]
    public string? StartedAt { get; set; }

    [JsonPropertyName("ended_at")]
    public string? EndedAt { get; set; }

    [JsonPropertyName("exported_at")]
    public string? ExportedAt { get; set; }

    [JsonPropertyName("total_duration_seconds")]
    public double? TotalDurationSeconds { get; set; }

    [JsonPropertyName("catalogue_folders")]
    public List<string> CatalogueFolders { get; set; } = [];

    [JsonPropertyName("view_folders")]
    public List<string> ViewFolders { get; set; } = [];

    /// <summary>Claims written per provider: shows which providers answered.</summary>
    [JsonPropertyName("provider_claims")]
    public IDictionary<string, int> ProviderClaims { get; set; } = new SortedDictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Outgoing request counts per host, read from the Engine log by the run script.</summary>
    [JsonPropertyName("http_hosts")]
    public IDictionary<string, int> HttpHosts { get; set; } = new SortedDictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Whether the run recorded outside answers or replayed a recording, with counts and the folder.</summary>
    [JsonPropertyName("provider_answers")]
    public string? ProviderAnswers { get; set; }

    /// <summary>Replays only: the distinct requests the recording did not hold (each answered "not found").</summary>
    [JsonPropertyName("provider_answers_not_recorded")]
    public List<string> ProviderAnswersNotRecorded { get; set; } = [];
}

public sealed class BaselineCounts
{
    [JsonPropertyName("by_media_type")]
    public IDictionary<string, BaselineTypeCounts> ByMediaType { get; set; } =
        new SortedDictionary<string, BaselineTypeCounts>(StringComparer.Ordinal);

    [JsonPropertyName("identity_job_states")]
    public IDictionary<string, int> IdentityJobStates { get; set; } = new SortedDictionary<string, int>(StringComparer.Ordinal);

    [JsonPropertyName("view_items_by_kind")]
    public IDictionary<string, int> ViewItemsByKind { get; set; } = new SortedDictionary<string, int>(StringComparer.Ordinal);

    [JsonPropertyName("works")]
    public int Works { get; set; }

    [JsonPropertyName("collections")]
    public int Collections { get; set; }
}

public sealed class BaselineTypeCounts
{
    [JsonPropertyName("assets")]
    public int Assets { get; set; }

    [JsonPropertyName("works")]
    public int Works { get; set; }

    [JsonPropertyName("with_wikidata_qid")]
    public int WithWikidataQid { get; set; }

    [JsonPropertyName("with_artwork")]
    public int WithArtwork { get; set; }

    [JsonPropertyName("review_pending")]
    public int ReviewPending { get; set; }

    [JsonPropertyName("review_closed")]
    public int ReviewClosed { get; set; }

    [JsonPropertyName("text_tracks")]
    public int TextTracks { get; set; }
}

public sealed class BaselineItem
{
    /// <summary>Path relative to the sample root, forward slashes.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("identity")]
    public BaselineIdentity Identity { get; set; } = new();

    [JsonPropertyName("work")]
    public BaselineWork Work { get; set; } = new();

    [JsonPropertyName("parent_work")]
    public BaselineWork? ParentWork { get; set; }

    [JsonPropertyName("edition")]
    public BaselineEdition Edition { get; set; } = new();

    /// <summary>Series first, then wider groups such as a Universe: nearest group first.</summary>
    [JsonPropertyName("groups")]
    public List<BaselineGroupRef> Groups { get; set; } = [];

    /// <summary>Provider and bridge ids found in current claims, per claim key, values sorted.</summary>
    [JsonPropertyName("external_ids")]
    public IDictionary<string, List<string>> ExternalIds { get; set; } = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

    /// <summary>Canonical values by scope: "asset", "work" and "parent".</summary>
    [JsonPropertyName("values")]
    public IDictionary<string, BaselineValueSet> Values { get; set; } = new SortedDictionary<string, BaselineValueSet>(StringComparer.Ordinal);

    /// <summary>The preferred artwork per scope and role, such as "work/Primary".</summary>
    [JsonPropertyName("artwork")]
    public IDictionary<string, BaselineArtwork> Artwork { get; set; } = new SortedDictionary<string, BaselineArtwork>(StringComparer.Ordinal);

    [JsonPropertyName("people")]
    public List<BaselinePerson> People { get; set; } = [];

    [JsonPropertyName("text_tracks")]
    public List<BaselineTextTrack> TextTracks { get; set; } = [];

    [JsonPropertyName("review")]
    public List<BaselineReview> Review { get; set; } = [];
}

public sealed class BaselineIdentity
{
    [JsonPropertyName("job_state")]
    public string? JobState { get; set; }

    [JsonPropertyName("resolved_qid")]
    public string? ResolvedQid { get; set; }

    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("wikidata_qid")]
    public string? WikidataQid { get; set; }
}

public sealed class BaselineWork
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("qid")]
    public string? Qid { get; set; }

    [JsonPropertyName("ordinal")]
    public double? Ordinal { get; set; }

    [JsonPropertyName("ownership")]
    public string? Ownership { get; set; }

    [JsonPropertyName("match_level")]
    public string? MatchLevel { get; set; }

    [JsonPropertyName("external_identifiers")]
    public string? ExternalIdentifiers { get; set; }

    /// <summary>Paths of every asset grouped under this work, sorted: this is the grouping.</summary>
    [JsonPropertyName("assets")]
    public List<string> Assets { get; set; } = [];
}

public sealed class BaselineEdition
{
    [JsonPropertyName("format_label")]
    public string? FormatLabel { get; set; }

    [JsonPropertyName("qid")]
    public string? Qid { get; set; }

    [JsonPropertyName("assets")]
    public List<string> Assets { get; set; } = [];
}

public sealed class BaselineGroupRef
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("qid")]
    public string? Qid { get; set; }
}

public sealed class BaselineValueSet
{
    [JsonPropertyName("scalars")]
    public IDictionary<string, BaselineValue> Scalars { get; set; } = new SortedDictionary<string, BaselineValue>(StringComparer.Ordinal);

    /// <summary>Multi-valued metadata; each entry is "value" or "value [Qid]", sorted.</summary>
    [JsonPropertyName("arrays")]
    public IDictionary<string, List<string>> Arrays { get; set; } = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
}

public sealed class BaselineValue
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>The provider whose claim won the Priority Cascade for this value.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("conflicted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Conflicted { get; set; }

    [JsonPropertyName("needs_review")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NeedsReview { get; set; }
}

public sealed class BaselineArtwork
{
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("source_url")]
    public string? SourceUrl { get; set; }

    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }
}

public sealed class BaselinePerson
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("qid")]
    public string? Qid { get; set; }
}

public sealed class BaselineTextTrack
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("source_format")]
    public string SourceFormat { get; set; } = string.Empty;

    [JsonPropertyName("normalized_format")]
    public string NormalizedFormat { get; set; } = string.Empty;
}

public sealed class BaselineReview
{
    /// <summary>The asset path, or "work:" plus a work title when the entry is not about a file.</summary>
    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

public sealed class BaselineViewItem
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("media_kind")]
    public string MediaKind { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("byte_size")]
    public long ByteSize { get; set; }

    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("captured_at")]
    public string? CapturedAt { get; set; }

    /// <summary>Indexed details (size in pixels, place, device, text source, ...), by column name.</summary>
    [JsonPropertyName("details")]
    public IDictionary<string, string> Details { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("annotations")]
    public List<string> Annotations { get; set; } = [];
}

public sealed class BaselineCollection
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("qid")]
    public string? Qid { get; set; }

    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Paths of the owned assets that belong to the group, sorted.</summary>
    [JsonPropertyName("assets")]
    public List<string> Assets { get; set; } = [];

    [JsonPropertyName("relationships")]
    public List<string> Relationships { get; set; } = [];
}

public sealed class BaselineSeriesMember
{
    [JsonPropertyName("series_qid")]
    public string SeriesQid { get; set; } = string.Empty;

    [JsonPropertyName("work_qid")]
    public string WorkQid { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("position")]
    public string? Position { get; set; }

    [JsonPropertyName("owned")]
    public bool Owned { get; set; }
}

/// <summary>How long each item took. Never compared as a difference; compared against a margin instead.</summary>
public sealed class BaselineTimings
{
    [JsonPropertyName("total_seconds")]
    public double? TotalSeconds { get; set; }

    /// <summary>Seconds from registration to the last identity step, by item path.</summary>
    [JsonPropertyName("registered_to_identified_seconds")]
    public IDictionary<string, double> RegisteredToIdentified { get; set; } = new SortedDictionary<string, double>(StringComparer.Ordinal);
}

/// <summary>What the prepare step knew about a run, plus what the run script added (label, commit, start and end).</summary>
public sealed record BaselineRunInfo(
    string SampleRoot,
    IReadOnlyList<string> CatalogueFolders,
    IReadOnlyList<string> ViewFolders,
    string? Label,
    string? BuildCommit,
    string? StartedAt,
    string? EndedAt,
    IReadOnlyDictionary<string, int> HttpHosts,
    string? ProviderAnswers = null,
    IReadOnlyList<string>? ProviderAnswersNotRecorded = null)
{
    public static BaselineRunInfo? Load(string configDirectory)
    {
        var path = BaselinePrepare.RunInfoPathFor(configDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        string? Text(string name) => node[name]?.GetValue<string>();
        List<string> List(string name) => node[name] is JsonArray array
            ? array.Select(item => item!.GetValue<string>()).ToList()
            : [];
        var hosts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (node["http_hosts"] is JsonObject hostNode)
        {
            foreach (var (host, count) in hostNode)
            {
                hosts[host] = count!.GetValue<int>();
            }
        }

        return new BaselineRunInfo(
            Text("sample_root") ?? string.Empty, List("catalogue_folders"), List("view_folders"),
            Text("label"), Text("build_commit"), Text("started_at"), Text("ended_at"), hosts, Text("provider_answers"),
            List("provider_answers_not_recorded"));
    }
}

public static class BaselineRecordJson
{
    public const string SchemaName = "tuvima-ingestion-baseline/1";

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(BaselineRecord record) => JsonSerializer.Serialize(record, Options);

    public static BaselineRecord Deserialize(string json) =>
        JsonSerializer.Deserialize<BaselineRecord>(json, Options)
        ?? throw new InvalidDataException("The baseline record is empty.");

    public static BaselineRecord Load(string path)
    {
        var record = Deserialize(File.ReadAllText(path));
        if (!string.Equals(record.Schema, SchemaName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{path}' is not a {SchemaName} record (found '{record.Schema}').");
        }

        return record;
    }
}
