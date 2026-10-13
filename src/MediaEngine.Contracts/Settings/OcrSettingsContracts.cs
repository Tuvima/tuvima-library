using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

/// <summary>The text recognition choices a person can change in Settings.</summary>
public sealed class OcrSettingsDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>"normal" or "low".</summary>
    [JsonPropertyName("effort")]
    public string Effort { get; set; } = "normal";

    /// <summary>Language codes to read with.</summary>
    [JsonPropertyName("languages")]
    public List<string> Languages { get; set; } = ["eng"];
}

/// <summary>One language in the Settings checklist.</summary>
public sealed class OcrLanguageStatusDto
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>True when the language data is on this machine.</summary>
    [JsonPropertyName("installed")]
    public bool Installed { get; set; }

    [JsonPropertyName("selected")]
    public bool Selected { get; set; }
}

/// <summary>What text recognition can do right now, plus its queue. File names only; never document text.</summary>
public sealed class OcrStatusDto
{
    [JsonPropertyName("settings")]
    public OcrSettingsDto Settings { get; set; } = new();

    /// <summary>True when the helper and its language data are in place.</summary>
    [JsonPropertyName("installed")]
    public bool Installed { get; set; }

    /// <summary>One plain sentence about why recognition cannot run, when it cannot.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("languages")]
    public List<OcrLanguageStatusDto> Languages { get; set; } = [];

    /// <summary>Files waiting to be read, including the one being read.</summary>
    [JsonPropertyName("queue_length")]
    public int QueueLength { get; set; }

    [JsonPropertyName("current_file_name")]
    public string? CurrentFileName { get; set; }

    [JsonPropertyName("reread_running")]
    public bool RereadRunning { get; set; }

    [JsonPropertyName("reread_queued")]
    public int RereadQueued { get; set; }
}

/// <summary>A short plain-language result for an action such as a language download.</summary>
public sealed class OcrMessageDto
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
