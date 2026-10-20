using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Ingestion;

public sealed class IngestionProviderActivityDto
{
    [JsonPropertyName("provider_name")]
    public string ProviderName { get; set; } = "";

    [JsonPropertyName("active_requests")]
    public int ActiveRequests { get; set; }

    [JsonPropertyName("waiting_requests")]
    public int WaitingRequests { get; set; }

    [JsonPropertyName("requests_total")]
    public long RequestsTotal { get; set; }

    [JsonPropertyName("requests_last_minute")]
    public int RequestsLastMinute { get; set; }

    [JsonPropertyName("max_active_last_minute")]
    public int MaxActiveLastMinute { get; set; }

    [JsonPropertyName("errors_total")]
    public long ErrorsTotal { get; set; }

    [JsonPropertyName("errors_last_minute")]
    public int ErrorsLastMinute { get; set; }

    [JsonPropertyName("throttle_wait_ms_total")]
    public long ThrottleWaitMsTotal { get; set; }

    [JsonPropertyName("wait_ms_last_minute")]
    public long WaitMsLastMinute { get; set; }

    [JsonPropertyName("average_wait_ms")]
    public double AverageWaitMs { get; set; }

    [JsonPropertyName("average_latency_ms")]
    public double AverageLatencyMs { get; set; }

    [JsonPropertyName("last_success_at")]
    public DateTimeOffset? LastSuccessAt { get; set; }

    [JsonPropertyName("last_request_at")]
    public DateTimeOffset? LastRequestAt { get; set; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }
}

