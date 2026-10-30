using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Jobs;
using Microsoft.Extensions.Logging;

namespace MediaEngine.AI.Llama;

/// <summary>
/// Runs one-shot text inference with explicit lifetime leases and typed outcomes.
/// </summary>
public sealed class LlamaInferenceService : ILlamaInferenceService, IAsyncDisposable
{
    // Kept as a local cached static rather than MediaEngine.Domain.Services.MediaEngineJson:
    // this combination (case-insensitive + trailing commas + comment-skip, used for lenient
    // parsing of model JSON output under GBNF grammar constraints) isn't one of the three
    // shapes MediaEngineJson offers. See
    // tests/MediaEngine.Domain.Tests/JsonSerializerOptionsGuardrailAllowlist.txt.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly AiRuntimeSettingsSnapshot _settings;
    private readonly ModelInventory _inventory;
    private readonly IModelRuntimeCoordinator _runtimeCoordinator;
    private readonly ILlamaExecutionBackend _backend;
    private readonly ILogger<LlamaInferenceService> _logger;
    private readonly IDisposable _disposerRegistration;
    private int _disposed;

    public LlamaInferenceService(
        AiSettings settings,
        IModelLifecycleManager lifecycle,
        ModelInventory inventory,
        ILogger<LlamaInferenceService> logger)
        : this(
            settings,
            lifecycle,
            inventory,
            logger,
            new LlamaSharpExecutionBackend(settings, inventory, logger))
    {
    }

    internal LlamaInferenceService(
        AiSettings settings,
        IModelLifecycleManager lifecycle,
        ModelInventory inventory,
        ILogger<LlamaInferenceService> logger,
        ILlamaExecutionBackend backend)
    {
        _settings = AiRuntimeSettingsSnapshot.Create(settings);
        _inventory = inventory;
        _runtimeCoordinator = lifecycle as IModelRuntimeCoordinator
            ?? throw new ArgumentException(
                $"{nameof(IModelLifecycleManager)} must also implement {nameof(IModelRuntimeCoordinator)}.",
                nameof(lifecycle));
        _backend = backend;
        _logger = logger;
        _disposerRegistration = _runtimeCoordinator.RegisterModelDisposer(_backend.DisposeModelAsync);
    }

    public async Task<string> InferAsync(
        AiModelRole role,
        string prompt,
        string? gbnfGrammar = null,
        CancellationToken ct = default)
    {
        var outcome = await InferWithOutcomeAsync(role, prompt, gbnfGrammar, null, ct)
            .ConfigureAwait(false);
        return GetValueOrThrow(outcome, ct);
    }

    public async Task<T?> InferJsonAsync<T>(
        AiModelRole role,
        string prompt,
        string gbnfGrammar,
        CancellationToken ct = default) where T : class
    {
        var outcome = await InferJsonWithOutcomeAsync<T>(role, prompt, gbnfGrammar, null, ct)
            .ConfigureAwait(false);
        if (outcome.Status == InferenceOutcomeStatus.InvalidResponse)
        {
            return null;
        }

        return GetValueOrThrow(outcome, ct);
    }

    public async Task<InferenceOutcome<string>> InferWithOutcomeAsync(
        AiModelRole role,
        string prompt,
        string? gbnfGrammar = null,
        InferenceRequestOptions? options = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (role == AiModelRole.Audio)
        {
            throw new ArgumentException("Use WhisperInferenceService for audio models.", nameof(role));
        }

        var requestOptions = options ?? CreateDefaultOptions(role);
        ValidateOptions(requestOptions);
        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = new CancellationTokenSource(requestOptions.Timeout);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var output = new StringBuilder();

        try
        {
            await using var lease = await _runtimeCoordinator
                .AcquireInferenceLeaseAsync(role, operationCts.Token)
                .ConfigureAwait(false);
            await foreach (var chunk in _backend
                .InferAsync(role, prompt, gbnfGrammar, requestOptions, operationCts.Token)
                .WithCancellation(operationCts.Token)
                .ConfigureAwait(false))
            {
                output.Append(chunk);
            }

            var value = output.ToString().Trim();
            _logger.LogDebug("Inference complete for {Role}: {Length} chars", role, value.Length);
            return new(InferenceOutcomeStatus.Success, value, null, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(InferenceOutcomeStatus.Cancelled, null, "Inference was cancelled.", stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning("Inference timed out after {Timeout} for role {Role}", requestOptions.Timeout, role);
            return new(InferenceOutcomeStatus.TimedOut, null, "Inference timed out.", stopwatch.Elapsed);
        }
        catch (AiRuntimeNotInstalledException ex)
        {
            // The optional runtime was never installed: nothing is broken, so no error and no stack trace.
            _logger.LogInformation("Local AI is off for {Role}: the AI runtime is not installed.", role);
            return new(InferenceOutcomeStatus.ModelUnavailable, null, ex.Message, stopwatch.Elapsed);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Model {Role} is unavailable", role);
            return new(InferenceOutcomeStatus.ModelUnavailable, null, ex.Message, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inference failed for role {Role}", role);
            return new(InferenceOutcomeStatus.Failed, null, ex.Message, stopwatch.Elapsed);
        }
    }

    public async Task<InferenceOutcome<T>> InferJsonWithOutcomeAsync<T>(
        AiModelRole role,
        string prompt,
        string gbnfGrammar,
        InferenceRequestOptions? options = null,
        CancellationToken ct = default) where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        var firstOptions = options ?? CreateDefaultOptions(role);
        var first = await InferWithOutcomeAsync(role, prompt, gbnfGrammar, firstOptions, ct)
            .ConfigureAwait(false);
        if (!first.IsSuccess)
        {
            return ConvertFailure<T>(first, stopwatch.Elapsed);
        }

        var parsed = TryParseJson<T>(first.Value);
        if (parsed is not null)
        {
            return new(InferenceOutcomeStatus.Success, parsed, null, stopwatch.Elapsed);
        }

        _logger.LogWarning(
            "JSON parse failed for {Role}; returning an invalid-response outcome without another full inference",
            role);
        return new(
            InferenceOutcomeStatus.InvalidResponse,
            null,
            "The model did not return valid JSON.",
            stopwatch.Elapsed);
    }

    private InferenceRequestOptions CreateDefaultOptions(AiModelRole role)
    {
        var definition = _inventory.GetDefinition(role);
        return new(
            definition.Temperature,
            definition.MaxTokens > 0 ? definition.MaxTokens : 256,
            TimeSpan.FromSeconds(_settings.InferenceTimeoutSeconds));
    }

    private static void ValidateOptions(InferenceRequestOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Temperature, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Temperature, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxTokens);
        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Inference timeout must be positive.");
        }
    }

    private static T GetValueOrThrow<T>(InferenceOutcome<T> outcome, CancellationToken callerToken)
    {
        if (outcome.IsSuccess && outcome.Value is not null)
        {
            return outcome.Value;
        }

        throw outcome.Status switch
        {
            InferenceOutcomeStatus.Cancelled => new OperationCanceledException(callerToken),
            InferenceOutcomeStatus.TimedOut => new TimeoutException(outcome.Error),
            InferenceOutcomeStatus.ModelUnavailable => new UnavailableCapabilityException(
                outcome.Error ?? "The requested AI model is unavailable."),
            InferenceOutcomeStatus.InvalidResponse => new JsonException(outcome.Error),
            _ => new InvalidOperationException(outcome.Error ?? "Inference failed."),
        };
    }

    private static InferenceOutcome<T> ConvertFailure<T>(
        InferenceOutcome<string> source,
        TimeSpan duration,
        int attempts = 1) where T : class
    {
        return new(source.Status, null, source.Error, duration, attempts);
    }

    private static T? TryParseJson<T>(string? raw) where T : class
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (TryDeserialize(raw, out T? direct))
        {
            return direct;
        }

        var objectStart = raw.IndexOf('{');
        var objectEnd = raw.LastIndexOf('}');
        if (objectStart >= 0
            && objectEnd > objectStart
            && TryDeserialize(raw[objectStart..(objectEnd + 1)], out T? extractedObject))
        {
            return extractedObject;
        }

        var arrayStart = raw.IndexOf('[');
        var arrayEnd = raw.LastIndexOf(']');
        return arrayStart >= 0
            && arrayEnd > arrayStart
            && TryDeserialize(raw[arrayStart..(arrayEnd + 1)], out T? extractedArray)
                ? extractedArray
                : null;
    }

    private static bool TryDeserialize<T>(string raw, out T? value) where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(raw, JsonOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            value = null;
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposerRegistration.Dispose();
        await _backend.DisposeAsync().ConfigureAwait(false);
    }
}
