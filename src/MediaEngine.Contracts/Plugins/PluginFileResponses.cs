using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Plugins;

/// <summary>
/// What the Plugin file card says about a plugin's file (Plugins and Integrations screens v5 D54): its name, whether it is valid,
/// and whether it differs from the copy that ships with Library. Carried on the Plugin file card of the page model and by the
/// file actions. Never the file's text.
/// </summary>
public sealed class PluginFileStateDto
{
    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = "";

    /// <summary>
    /// <c>source_file</c> (a provider source file), <c>manifest</c> (a plugin manifest) or <c>shipped_description</c> (the JSON that
    /// ships with Library to describe a plugin that is compiled in; read only).
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "manifest";

    /// <summary><c>valid</c> (no problems that block) or <c>problems</c>. A "thing to know" never makes a file <c>problems</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "valid";

    [JsonPropertyName("error_count")]
    public int ErrorCount { get; set; }

    /// <summary>"Things to know": findings that do not block.</summary>
    [JsonPropertyName("note_count")]
    public int NoteCount { get; set; }

    /// <summary>True when the file's content differs from Tuvima's version. Always false when there is no shipped copy.</summary>
    [JsonPropertyName("differs_from_shipped")]
    public bool DiffersFromShipped { get; set; }

    /// <summary>False for a private plugin: it has no Tuvima version to compare with.</summary>
    [JsonPropertyName("has_shipped_copy")]
    public bool HasShippedCopy { get; set; }

    /// <summary>True when the Engine would let an administrator edit this file (a later packet adds the editor). False for a compiled-in plugin's shipped description.</summary>
    [JsonPropertyName("editable")]
    public bool Editable { get; set; }
}

/// <summary>One finding in a plugin file: where, how serious and what it means in plain words.</summary>
public sealed class PluginFileProblemDto
{
    /// <summary>1-based line the finding points at.</summary>
    [JsonPropertyName("line")]
    public int Line { get; set; } = 1;

    /// <summary>1-based column on that line.</summary>
    [JsonPropertyName("column")]
    public int Column { get; set; } = 1;

    /// <summary><c>error</c> blocks saving; <c>note</c> is a thing to know and does not.</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "error";

    /// <summary>
    /// What kind of finding it is: <c>syntax</c>, <c>type</c>, <c>range</c>, <c>value</c>, <c>missing</c>, <c>unknown_word</c>,
    /// <c>rule</c> (one of Library's own rules) or <c>changed_locked</c> (a locked field differs from Tuvima's version).
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = "rule";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary><c>GET /plugins/{pluginId}/file</c>: the plugin's file text with its state and findings.</summary>
public sealed class PluginFileResponse
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("state")]
    public PluginFileStateDto State { get; set; } = new();

    [JsonPropertyName("problems")]
    public List<PluginFileProblemDto> Problems { get; set; } = [];
}

/// <summary><c>POST /plugins/{pluginId}/file/check</c> body: the text to check. Nothing is written.</summary>
public sealed class PluginFileCheckRequest
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

/// <summary>The result of checking proposed text: the same state and findings as the file itself.</summary>
public sealed class PluginFileCheckResponse
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    [JsonPropertyName("state")]
    public PluginFileStateDto State { get; set; } = new();

    [JsonPropertyName("problems")]
    public List<PluginFileProblemDto> Problems { get; set; } = [];
}

/// <summary>
/// What happened to a plugin's file when an administrator pressed Save changes or Reset to Tuvima's version (Plugins and
/// Integrations screens v5 D55, D56). <c>PUT /plugins/{pluginId}/file</c> and <c>POST /plugins/{pluginId}/file/reset</c> both answer
/// with this. Keys and setting values are stored apart from the file, so neither action touches them.
/// </summary>
public sealed class PluginFileSaveResponse
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    /// <summary>True when the file on disk now holds the proposed text (or Tuvima's version, for a reset). False when nothing was written.</summary>
    [JsonPropertyName("saved")]
    public bool Saved { get; set; }

    /// <summary>
    /// <c>saved</c> (written, reloaded and checked), <c>reset</c> (Tuvima's version put back), <c>invalid</c> (the check found problems
    /// that block, nothing written), <c>read_only</c> (the file cannot be edited here), <c>no_shipped_copy</c> (a reset with no
    /// Tuvima version to go back to) or <c>reload_failed</c> (the Engine could not load what was written).
    /// </summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "invalid";

    /// <summary>One plain sentence saying what happened.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    /// <summary>The file's text as it is on disk after the action.</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("state")]
    public PluginFileStateDto State { get; set; } = new();

    [JsonPropertyName("problems")]
    public List<PluginFileProblemDto> Problems { get; set; } = [];

    /// <summary>True when the Engine loaded the written file again. False when it could not (the page then offers Reset).</summary>
    [JsonPropertyName("reloaded")]
    public bool Reloaded { get; set; }

    /// <summary><c>working</c>, <c>needs_attention</c> or <c>not_checked</c> (the plugin is switched off, so there was nothing to run). Empty when nothing was written.</summary>
    [JsonPropertyName("health")]
    public string Health { get; set; } = "";

    /// <summary>The health check's one-sentence answer, in plain words. Empty when nothing was written.</summary>
    [JsonPropertyName("health_summary")]
    public string HealthSummary { get; set; } = "";
}
