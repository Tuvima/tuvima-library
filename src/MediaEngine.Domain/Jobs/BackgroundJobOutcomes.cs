using System.Net;

namespace MediaEngine.Domain.Jobs;

/// <summary>
/// Stable failure categories shared by durable workers. These categories describe
/// what happened rather than the worker-specific state used to resume processing.
/// </summary>
public enum BackgroundJobOutcomeCategory
{
    ContentFailure,
    TransientDependencyFailure,
    UnavailableCapability,
    Cancellation,
    PermanentFailure,
}

public sealed record BackgroundJobOutcomePolicy(
    BackgroundJobOutcomeCategory Category,
    bool ConsumesPoisonBudget,
    bool ShouldRetry,
    bool IsCapabilityBlocked,
    bool IsTerminal)
{
    public static BackgroundJobOutcomePolicy For(BackgroundJobOutcomeCategory category) => category switch
    {
        BackgroundJobOutcomeCategory.ContentFailure => new(category, true, true, false, false),
        BackgroundJobOutcomeCategory.TransientDependencyFailure => new(category, false, true, false, false),
        BackgroundJobOutcomeCategory.UnavailableCapability => new(category, false, true, true, false),
        BackgroundJobOutcomeCategory.Cancellation => new(category, false, false, false, false),
        BackgroundJobOutcomeCategory.PermanentFailure => new(category, false, false, false, true),
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };
}

/// <summary>Raised when a configured worker cannot run because its model, provider, or tool is unavailable.</summary>
public sealed class UnavailableCapabilityException : InvalidOperationException
{
    public UnavailableCapabilityException(string message) : base(message) { }
    public UnavailableCapabilityException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>A transient dependency failure that supplies an explicit retry delay.</summary>
public sealed class RateLimitedDependencyException : HttpRequestException
{
    public RateLimitedDependencyException(string message, TimeSpan? retryAfter = null)
        : base(message, null, HttpStatusCode.TooManyRequests)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// A provider could not be reached or is not currently serving requests (marked down, timeout,
/// connection failure, HTTP 5xx or 429). The work should wait and retry; it must never be treated
/// as "no match".
/// </summary>
public sealed class ProviderUnavailableException : HttpRequestException
{
    public ProviderUnavailableException(string message, Exception? innerException = null, HttpStatusCode? statusCode = null)
        : base(message, innerException, statusCode)
    {
    }
}

/// <summary>
/// A provider rejected our request (HTTP 4xx other than 429), meaning the request was invalid or
/// unauthorised. Retrying cannot succeed until configuration or the request changes, so it counts
/// against the job's poison budget.
/// </summary>
public sealed class ProviderRequestRejectedException : HttpRequestException
{
    public ProviderRequestRejectedException(string message, HttpStatusCode statusCode, Exception? innerException = null)
        : base(message, innerException, statusCode)
    {
    }
}

public static class BackgroundJobOutcomeClassifier
{
    public static BackgroundJobOutcomeCategory Classify(Exception exception, CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException)
        {
            return callerToken.IsCancellationRequested
                ? BackgroundJobOutcomeCategory.Cancellation
                : BackgroundJobOutcomeCategory.TransientDependencyFailure;
        }

        if (exception is UnavailableCapabilityException)
        {
            return BackgroundJobOutcomeCategory.UnavailableCapability;
        }

        if (exception is ProviderRequestRejectedException)
        {
            return BackgroundJobOutcomeCategory.ContentFailure;
        }

        if (exception is RateLimitedDependencyException
            || exception is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
        {
            return BackgroundJobOutcomeCategory.TransientDependencyFailure;
        }

        if (exception is TimeoutException or HttpRequestException)
        {
            return BackgroundJobOutcomeCategory.TransientDependencyFailure;
        }

        if (exception.GetType().Name == "SqliteException" && IsBusySqliteError(exception))
        {
            return BackgroundJobOutcomeCategory.TransientDependencyFailure;
        }

        if (exception is InvalidDataException or FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return BackgroundJobOutcomeCategory.ContentFailure;
        }

        if (exception is ArgumentException or NotSupportedException)
        {
            return BackgroundJobOutcomeCategory.PermanentFailure;
        }

        return BackgroundJobOutcomeCategory.PermanentFailure;
    }

    private static bool IsBusySqliteError(Exception exception)
    {
        var code = exception.GetType().GetProperty("SqliteErrorCode")?.GetValue(exception) as int?;
        return code is 5 or 6;
    }
}
