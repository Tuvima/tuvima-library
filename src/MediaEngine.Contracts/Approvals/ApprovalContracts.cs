using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Approvals;

/// <summary>The kinds of item that need an approval before they are switched on. These exact words travel on the wire.</summary>
public static class ApprovalKinds
{
    public const string Plugin = "plugin";
    public const string ApplicationCredential = "application_credential";

    /// <summary>An integration's existing keys gaining permissions, or the integration being switched back on while it holds keys. The item id is the application id.</summary>
    public const string Application = "application";
    public const string Webhook = "webhook";
}

/// <summary>The error code of an <see cref="ApprovalRequiredResponse"/>.</summary>
public static class ApprovalErrorCodes
{
    public const string ApprovalRequired = "approval_required";
}

/// <summary>
/// What an item can do and whether anything leaves the machine, in plain words. The Engine writes these words; the
/// Dashboard shows them as they are. <see cref="StatementHash"/> fingerprints exactly what is shown.
/// </summary>
public sealed record ApprovalPreviewResponse
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    /// <summary>Plugin id, AI feature key, application id (credentials, until the key exists) or webhook id (empty for a new webhook).</summary>
    [JsonPropertyName("item_id")]
    public string ItemId { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    /// <summary>Plain-English label for the kind, for example "Plugin".</summary>
    [JsonPropertyName("kind_label")]
    public string KindLabel { get; init; } = "";

    [JsonPropertyName("can_do")]
    public List<string> CanDo { get; init; } = [];

    [JsonPropertyName("sends_data_out")]
    public bool SendsDataOut { get; init; }

    /// <summary>Where data goes when <see cref="SendsDataOut"/> is true; otherwise null.</summary>
    [JsonPropertyName("sends_data_to")]
    public string? SendsDataTo { get; init; }

    [JsonPropertyName("notes")]
    public List<string> Notes { get; init; } = [];

    [JsonPropertyName("statement_hash")]
    public string StatementHash { get; init; } = "";
}

/// <summary>
/// Body of <c>POST /approvals/preview</c>. Plugins and AI features need only <see cref="ItemId"/>; a webhook sends the
/// request it is about to save; a credential names the application and the expiry it is about to be issued with.
/// </summary>
public sealed record ApprovalPreviewRequest
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("item_id")]
    public string? ItemId { get; init; }

    /// <summary>For a webhook: the receiver, events and private-network setting about to be saved.</summary>
    [JsonPropertyName("webhook")]
    public ApprovalWebhookInput? Webhook { get; init; }

    /// <summary>For a credential: the application and the expiry about to be issued.</summary>
    [JsonPropertyName("credential")]
    public ApprovalCredentialInput? Credential { get; init; }
}

/// <summary>The webhook settings a preview describes.</summary>
public sealed record ApprovalWebhookInput
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("event_types")]
    public List<string> EventTypes { get; init; } = [];

    [JsonPropertyName("allow_local_network")]
    public bool AllowLocalNetwork { get; init; }
}

/// <summary>The credential a preview describes.</summary>
public sealed record ApprovalCredentialInput
{
    [JsonPropertyName("application_id")]
    public Guid ApplicationId { get; init; }

    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>One recorded approval, as listed by <c>GET /approvals</c>. Newest first.</summary>
public sealed record ApprovalRecordResponse
{
    [JsonPropertyName("approved_by_name")]
    public string ApprovedByName { get; init; } = "";

    [JsonPropertyName("approved_at")]
    public DateTimeOffset ApprovedAt { get; init; }

    /// <summary>Exactly what the person was shown.</summary>
    [JsonPropertyName("statement")]
    public ApprovalPreviewResponse Statement { get; init; } = new();

    [JsonPropertyName("statement_hash")]
    public string StatementHash { get; init; } = "";
}

/// <summary>
/// The 409 body returned when an action needs an approval it does not have (or has only for an older statement).
/// Show each preview, then repeat the action carrying the hashes.
/// </summary>
public sealed record ApprovalRequiredResponse
{
    [JsonPropertyName("error")]
    public string Error { get; init; } = ApprovalErrorCodes.ApprovalRequired;

    [JsonPropertyName("approvals")]
    public List<ApprovalPreviewResponse> Approvals { get; init; } = [];
}

/// <summary>Optional body of <c>POST /plugins/{pluginId}/enable</c>.</summary>
public sealed record PluginEnableRequest
{
    [JsonPropertyName("approval_hash")]
    public string? ApprovalHash { get; init; }

    /// <summary>True to also switch on the plugins this one needs, dependencies first, under one approval (Plugin architecture spec C22).</summary>
    [JsonPropertyName("also_switch_on_needed")]
    public bool AlsoSwitchOnNeeded { get; init; }

    /// <summary>The hash of each preview in the approval the person confirmed, when several plugins are approved together.</summary>
    [JsonPropertyName("approval_hashes")]
    public List<string>? ApprovalHashes { get; init; }
}

/// <summary>Optional body of <c>POST /plugins/{pluginId}/disable</c>.</summary>
public sealed record PluginDisableRequest
{
    /// <summary>True to also switch off the plugins that need this one (the default offered on the removal screen); false cancels when some do.</summary>
    [JsonPropertyName("also_switch_off_dependants")]
    public bool AlsoSwitchOffDependants { get; init; }
}
