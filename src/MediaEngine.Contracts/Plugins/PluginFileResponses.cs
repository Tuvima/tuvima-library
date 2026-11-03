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
