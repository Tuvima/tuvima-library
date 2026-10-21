namespace MediaEngine.Domain.Models;

public sealed class ApplicationWebhook
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public string Url { get; set; } = "";
    public string EventTypesJson { get; set; } = "[]";
    public bool AllowLocalNetwork { get; set; }
    public bool IsEnabled { get; set; }
    public string SecretCiphertext { get; set; } = "";
    public long Version { get; set; } = 1;
    public long LastEventSequence { get; set; }
    public string LastStatus { get; set; } = "Waiting for events";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
}

public sealed class ApplicationWebhookDelivery
{
    public Guid Id { get; set; }
    public Guid WebhookId { get; set; }
    public long WebhookVersion { get; set; }
    public Guid EventId { get; set; }
    public long EventSequence { get; set; }
    public string EventJson { get; set; } = "";
    public string Status { get; set; } = "pending";
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One attempt to send an event to a webhook receiver, kept for the delivery history on the Integrations page.
/// <see cref="Result"/> is short plain text ("HTTP 503", "Receiver unavailable"); it never holds a response body,
/// the event body or the signing secret.
/// </summary>
public sealed class ApplicationWebhookAttempt
{
    public Guid Id { get; set; }
    public Guid WebhookId { get; set; }
    public Guid DeliveryId { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = "";
    public int AttemptNumber { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
    public bool Delivered { get; set; }
    public string Result { get; set; } = "";
    public int? HttpStatus { get; set; }
    public long DurationMs { get; set; }
    public bool IsTest { get; set; }
}

/// <summary>The newest webhook delivery attempt: when it happened and the delivery status after it.</summary>
public sealed class ApplicationWebhookLastAttempt
{
    public DateTimeOffset At { get; set; }
    public string Status { get; set; } = "";
}
