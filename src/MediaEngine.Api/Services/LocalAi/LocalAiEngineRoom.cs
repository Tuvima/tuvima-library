using System.Diagnostics;
using MediaEngine.AI.Configuration;
using MediaEngine.AI.EngineRoom;
using MediaEngine.AI.Infrastructure;
using MediaEngine.AI.Llama;
using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.LocalAi;

/// <summary>Waiting times for the engine room. Shortened in tests.</summary>
/// <param name="PluginAdmissionPoll">How often a waiting plugin request re-checks the machine's rules.</param>
public sealed record LocalAiEngineRoomTiming(TimeSpan PluginAdmissionPoll)
{
    public static LocalAiEngineRoomTiming Default { get; } = new(TimeSpan.FromSeconds(1));
}

/// <summary>
/// The one door to the machine's local engines (D-Q, D-S, D-U). Every caller asks for an ability; the engine room picks
/// the engine, keeps one queue per engine in priority order (a person, ingestion, Library background work, plugins),
/// applies the machine's rules and each plugin's hourly allowance, and writes one usage record for every request that
/// reaches it, except Library background requests that were only deferred.
/// </summary>
public sealed class LocalAiEngineRoom : IAiEngineRoom, IDisposable
{
    internal const string NotAvailableYet = AiAbilityCatalog.NotAvailableYet;
    internal const string LocalAiOff = "Local AI is off";
    internal const string RuntimeNotInstalled = "AI runtime not installed";
    internal const string ModelNotDownloaded = "Model not downloaded";
    internal const string TextReadingOff = "Text reading is off";
    internal const string TextRecognitionNotInstalled = "Text recognition is not installed.";
    internal const string MachineBusy = "Waiting: the machine is busy";

    private const string PreemptedMessage = "Paused to make way for something you were doing";
    private const string CancelledMessage = "Cancelled before it finished";
    private const string WaitedTooLongMessage = "Waited too long for its turn";
    private const string RequestFailedMessage = "The request failed";
    private const string InvalidJsonMessage = "The model did not return valid JSON";
    private const string AllowanceCheckFailedMessage = "The Local AI allowance could not be checked";
    private const string AvailabilityCheckFailedMessage = "Text reading could not be checked";

    private readonly AiSettings _settings;
    private readonly IAiFeatureSwitches _switches;
    private readonly ModelInventory _inventory;
    private readonly IAiRuntimeStatus _runtime;
    private readonly IAiAdmissionRules _admission;
    private readonly ITextInferenceService _text;
    private readonly IOcrService _ocr;
    private readonly IConfigurationLoader _configuration;
    private readonly IAiUsageRecordRepository _usage;
    private readonly PluginAiBudget _budget;
    private readonly ILogger<LocalAiEngineRoom> _logger;
    private readonly LocalAiEngineRoomTiming _timing;
    private readonly TimeProvider _time;
    private readonly AiEngineQueue _textQueue = new(1);

    // Tesseract has no limit today: the text-reading worker runs one file at a time and the engine room adds none.
    private readonly AiEngineQueue _tesseractQueue = new(AiEngineQueue.Unlimited);
    private int _disposed;

    public LocalAiEngineRoom(
        AiSettings settings,
        IAiFeatureSwitches switches,
        ModelInventory inventory,
        IAiRuntimeStatus runtime,
        IAiAdmissionRules admission,
        ITextInferenceService text,
        IOcrService ocr,
        IConfigurationLoader configuration,
        IAiUsageRecordRepository usage,
        PluginAiBudget budget,
        ILogger<LocalAiEngineRoom> logger,
        LocalAiEngineRoomTiming? timing = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(switches);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _switches = switches;
        _inventory = inventory;
        _runtime = runtime;
        _admission = admission;
        _text = text;
        _ocr = ocr;
        _configuration = configuration;
        _usage = usage;
        _budget = budget;
        _logger = logger;
        _timing = timing ?? LocalAiEngineRoomTiming.Default;
        _time = time ?? TimeProvider.System;
        _admission.InteractiveTrafficStarted += OnInteractiveTrafficStarted;
    }

    /// <summary>Text requests waiting for the one text-model slot. For tests and diagnostics.</summary>
    internal int TextRequestsWaiting => _textQueue.WaitingCount;

    /// <summary>Text requests running now (at most one). For tests and diagnostics.</summary>
    internal int TextRequestsRunning => _textQueue.ActiveCount;

    /// <summary>Text-reading requests running now. For tests and diagnostics.</summary>
    internal int TextReadingRequestsRunning => _tesseractQueue.ActiveCount;

    // ── Text abilities ───────────────────────────────────────────────────────

    public async Task<AiTextResult> WriteTextAsync(
        AiCaller caller,
        AiAbility ability,
        string prompt,
        string? grammar = null,
        AiRequestOptions? options = null,
        CancellationToken ct = default)
    {
        var info = ValidateTextRequest(caller, ability, prompt, grammar);
        var (value, outcome) = await RunTextAsync(
            caller,
            info,
            options,
            (role, requestOptions, token) => _text.InferWithOutcomeAsync(role, prompt, grammar, requestOptions, token),
            ct).ConfigureAwait(false);
        return new AiTextResult(value, outcome);
    }

    public async Task<AiJsonResult<T>> ExtractJsonAsync<T>(
        AiCaller caller,
        AiAbility ability,
        string prompt,
        string grammar,
        AiRequestOptions? options = null,
        CancellationToken ct = default) where T : class
    {
        var info = ValidateTextRequest(caller, ability, prompt, grammar);
        if (string.IsNullOrWhiteSpace(grammar))
        {
            throw new ArgumentException("Structured output needs a grammar.", nameof(grammar));
        }

        var (value, outcome) = await RunTextAsync(
            caller,
            info,
            options,
            (role, requestOptions, token) => _text.InferJsonWithOutcomeAsync<T>(role, prompt, grammar, requestOptions, token),
            ct).ConfigureAwait(false);
        return new AiJsonResult<T>(value, outcome);
    }

    private static AiAbilityInfo ValidateTextRequest(AiCaller caller, AiAbility ability, string prompt, string? grammar)
    {
        ValidateCaller(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var info = AiAbilityCatalog.Get(ability);
        if (info.Engine == AiEngine.Tesseract)
        {
            throw new ArgumentException("Reading text from an image uses ReadTextFromImageAsync.", nameof(ability));
        }

        if (info.IsAvailableNow && info.RequiresGrammar && string.IsNullOrWhiteSpace(grammar))
        {
            throw new ArgumentException($"{info.DisplayName} needs a grammar.", nameof(grammar));
        }

        return info;
    }

    private async Task<(TValue? Value, AiRequestOutcome Outcome)> RunTextAsync<TValue>(
        AiCaller caller,
        AiAbilityInfo info,
        AiRequestOptions? options,
        Func<AiModelRole, InferenceRequestOptions?, CancellationToken, Task<InferenceOutcome<TValue>>> infer,
        CancellationToken ct) where TValue : class
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var startedAt = _time.GetUtcNow();

        if (!info.IsAvailableNow || info.Role is not { } role)
        {
            return (null, await FinishAsync(caller, info, startedAt, AiRequestStatus.Unavailable, NotAvailableYet).ConfigureAwait(false));
        }

        // Library features and ingestion have their own switches (the feature flags), and the hardware check must run
        // before any switch is on, so "Local AI is off" is a rule for plugins and for the question "can I use this?".
        var unavailable = CheckTextAvailability(role, applyLocalAiSwitch: caller.Kind == AiCallerKind.Plugin);
        if (unavailable is not null)
        {
            return (null, await FinishAsync(caller, info, startedAt, AiRequestStatus.Unavailable, unavailable).ConfigureAwait(false));
        }

        // A plugin's allowance is checked and the request admitted as one step, so requests arriving together cannot all
        // pass on the same count. The admission is held until the usage record is written.
        PluginAiBudget.PluginAdmission? admission = null;
        if (caller.Kind == AiCallerKind.Plugin)
        {
            try
            {
                admission = await _budget.TryAdmitAsync(caller.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not check the Local AI allowance of {Caller}.", caller.Id);
                return (null, await FinishAsync(caller, info, startedAt, AiRequestStatus.Failed, AllowanceCheckFailedMessage, recordMessage: AllowanceCheckFailedMessage).ConfigureAwait(false));
            }

            if (admission is null)
            {
                return (null, await FinishAsync(caller, info, startedAt, AiRequestStatus.RefusedOverBudget, PluginAiBudget.OverBudgetMessage).ConfigureAwait(false));
            }
        }

        try
        {
            return await RunAdmittedTextAsync(caller, info, role, startedAt, options, infer, ct).ConfigureAwait(false);
        }
        finally
        {
            admission?.Dispose();
        }
    }

    private async Task<(TValue? Value, AiRequestOutcome Outcome)> RunAdmittedTextAsync<TValue>(
        AiCaller caller,
        AiAbilityInfo info,
        AiModelRole role,
        DateTimeOffset startedAt,
        AiRequestOptions? options,
        Func<AiModelRole, InferenceRequestOptions?, CancellationToken, Task<InferenceOutcome<TValue>>> infer,
        CancellationToken ct) where TValue : class
    {
        var requestOptions = ResolveOptions(role, options);
        var queueLimit = caller.Kind == AiCallerKind.Plugin
            ? TimeSpan.FromSeconds(_settings.GetPluginBudget(caller.Id).MaxWaitSeconds)
            : requestOptions?.Timeout ?? TimeSpan.FromSeconds(_settings.InferenceTimeoutSeconds);

        var waiting = Stopwatch.StartNew();
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(queueLimit);

        if (caller.Kind == AiCallerKind.LibraryBackground)
        {
            var reason = _admission.WhyNotNow(role);
            if (reason is not null)
            {
                return (null, Defer(caller, role, reason));
            }
        }
        else if (caller.Kind == AiCallerKind.Plugin)
        {
            // A plugin waits for the machine to be free of person-facing traffic instead of being turned away.
            while (_admission.WhyNotNow(role) is not null)
            {
                try
                {
                    await Task.Delay(_timing.PluginAdmissionPoll, waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return (null, await FinishWaitFailureAsync(caller, info, startedAt, waiting.Elapsed, ct).ConfigureAwait(false));
                }
            }
        }

        AiEngineSlot? slot;
        try
        {
            slot = await _textQueue
                .AcquireAsync(caller.Kind, refuseIfKindActive: caller.Kind == AiCallerKind.LibraryBackground, waitCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (null, await FinishWaitFailureAsync(caller, info, startedAt, waiting.Elapsed, ct).ConfigureAwait(false));
        }

        if (slot is null)
        {
            return (null, Defer(caller, role, "another-background-inference-is-active"));
        }

        using (slot)
        {
            if (waitCts.IsCancellationRequested)
            {
                return (null, await FinishWaitFailureAsync(caller, info, startedAt, waiting.Elapsed, ct).ConfigureAwait(false));
            }

            if (caller.Kind == AiCallerKind.LibraryBackground)
            {
                PerformanceMetrics.BackgroundAiAdmissions.Add(1, new KeyValuePair<string, object?>("role", role.ToString()));
            }

            var wait = waiting.Elapsed;
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct, slot.PreemptToken);
            var running = Stopwatch.StartNew();
            InferenceOutcome<TValue> result;
            try
            {
                result = await infer(role, requestOptions, runCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Local AI request from {Caller} failed before the model could answer.", caller.Id);
                result = new InferenceOutcome<TValue>(InferenceOutcomeStatus.Failed, null, ex.Message, running.Elapsed);
            }
            catch (OperationCanceledException)
            {
                result = new InferenceOutcome<TValue>(InferenceOutcomeStatus.Cancelled, null, CancelledMessage, running.Elapsed);
            }

            running.Stop();
            var preempted = slot.WasPreempted && !ct.IsCancellationRequested;
            var outcome = await FinishAsync(
                caller,
                info,
                startedAt,
                ToStatus(result.Status),
                MessageFor(result, preempted),
                wait,
                running.Elapsed,
                invalidResponse: result.Status == InferenceOutcomeStatus.InvalidResponse,
                preempted: preempted && result.Status == InferenceOutcomeStatus.Cancelled,
                recordMessage: result.Status switch
                {
                    InferenceOutcomeStatus.Success => null,
                    InferenceOutcomeStatus.InvalidResponse => InvalidJsonMessage,
                    InferenceOutcomeStatus.Failed => RequestFailedMessage,
                    _ => null,
                }).ConfigureAwait(false);
            return (outcome.IsSuccess ? result.Value : null, outcome);
        }
    }

    private InferenceRequestOptions? ResolveOptions(AiModelRole role, AiRequestOptions? options)
    {
        if (options is null || (options.MaxTokens is null && options.Temperature is null && options.Timeout is null))
        {
            return null; // The inference service applies the role's own defaults.
        }

        var definition = _inventory.GetDefinition(role);
        return new InferenceRequestOptions(
            options.Temperature ?? definition.Temperature,
            options.MaxTokens ?? (definition.MaxTokens > 0 ? definition.MaxTokens : 256),
            options.Timeout ?? TimeSpan.FromSeconds(_settings.InferenceTimeoutSeconds));
    }

    private static AiRequestStatus ToStatus(InferenceOutcomeStatus status) => status switch
    {
        InferenceOutcomeStatus.Success => AiRequestStatus.Succeeded,
        InferenceOutcomeStatus.TimedOut => AiRequestStatus.TimedOut,
        InferenceOutcomeStatus.Cancelled => AiRequestStatus.Cancelled,
        InferenceOutcomeStatus.ModelUnavailable => AiRequestStatus.Unavailable,
        _ => AiRequestStatus.Failed,
    };

    private static string MessageFor<TValue>(InferenceOutcome<TValue> result, bool preempted) where TValue : class =>
        result.Status switch
        {
            InferenceOutcomeStatus.Success => "Done",
            InferenceOutcomeStatus.Cancelled => preempted ? PreemptedMessage : CancelledMessage,
            InferenceOutcomeStatus.ModelUnavailable => result.Error ?? ModelNotDownloaded,
            InferenceOutcomeStatus.InvalidResponse => InvalidJsonMessage,
            _ => result.Error ?? RequestFailedMessage,
        };

    // ── Reading text from images ─────────────────────────────────────────────

    public async Task<AiImageTextResult> ReadTextFromImageAsync(
        AiCaller caller,
        AiImageTextRequest request,
        CancellationToken ct = default)
    {
        ValidateCaller(caller);
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var info = AiAbilityCatalog.Get(AiAbility.ReadTextFromImage);
        var startedAt = _time.GetUtcNow();

        // Text reading is a tool, not AI for the person: it never depends on Local AI being on.
        string? unavailable;
        try
        {
            unavailable = await CheckTextReadingAvailabilityAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not check whether text reading is available for {Caller}.", caller.Id);
            return new AiImageTextResult(null, await FinishAsync(caller, info, startedAt, AiRequestStatus.Failed, AvailabilityCheckFailedMessage, recordMessage: AvailabilityCheckFailedMessage).ConfigureAwait(false));
        }

        if (unavailable is not null)
        {
            return new AiImageTextResult(null, await FinishAsync(caller, info, startedAt, AiRequestStatus.Unavailable, unavailable).ConfigureAwait(false));
        }

        PluginAiBudget.PluginAdmission? admission = null;
        if (caller.Kind == AiCallerKind.Plugin)
        {
            try
            {
                admission = await _budget.TryAdmitAsync(caller.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not check the Local AI allowance of {Caller}.", caller.Id);
                return new AiImageTextResult(null, await FinishAsync(caller, info, startedAt, AiRequestStatus.Failed, AllowanceCheckFailedMessage, recordMessage: AllowanceCheckFailedMessage).ConfigureAwait(false));
            }

            if (admission is null)
            {
                return new AiImageTextResult(null, await FinishAsync(caller, info, startedAt, AiRequestStatus.RefusedOverBudget, PluginAiBudget.OverBudgetMessage).ConfigureAwait(false));
            }
        }

        try
        {
            return await ReadAdmittedAsync(caller, info, startedAt, request, ct).ConfigureAwait(false);
        }
        finally
        {
            admission?.Dispose();
        }
    }

    private async Task<AiImageTextResult> ReadAdmittedAsync(
        AiCaller caller,
        AiAbilityInfo info,
        DateTimeOffset startedAt,
        AiImageTextRequest request,
        CancellationToken ct)
    {
        var queueLimit = caller.Kind == AiCallerKind.Plugin
            ? TimeSpan.FromSeconds(_settings.GetPluginBudget(caller.Id).MaxWaitSeconds)
            : request.Timeout;
        var waiting = Stopwatch.StartNew();
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(queueLimit);

        AiEngineSlot? slot;
        try
        {
            slot = await _tesseractQueue.AcquireAsync(caller.Kind, refuseIfKindActive: false, waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new AiImageTextResult(null, await FinishWaitFailureAsync(caller, info, startedAt, waiting.Elapsed, ct).ConfigureAwait(false));
        }

        // Only the "one background task at a time" rule can refuse a place, and it is not used for text reading.
        var place = slot ?? throw new InvalidOperationException("The text reading engine always has a place.");
        using (place)
        {
            if (waitCts.IsCancellationRequested)
            {
                return new AiImageTextResult(null, await FinishWaitFailureAsync(caller, info, startedAt, waiting.Elapsed, ct).ConfigureAwait(false));
            }

            var wait = waiting.Elapsed;
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct, place.PreemptToken);
            var running = Stopwatch.StartNew();
            AiRequestStatus status;
            string message;
            AiImageText? value = null;
            Exception? unexpected = null;
            try
            {
                var recognition = await _ocr.RecognizeAsync(
                    request.ImagePath,
                    new OcrRunOptions(request.Languages, request.LowPriority, request.Timeout),
                    runCts.Token).ConfigureAwait(false);
                value = new AiImageText(recognition.Text, recognition.AverageConfidence, recognition.WordCount);
                status = AiRequestStatus.Succeeded;
                message = "Done";
            }
            catch (OperationCanceledException)
            {
                status = AiRequestStatus.Cancelled;
                message = place.WasPreempted && !ct.IsCancellationRequested ? PreemptedMessage : CancelledMessage;
            }
            catch (TimeoutException ex)
            {
                status = AiRequestStatus.TimedOut;
                message = ex.Message;
            }
            catch (InvalidOperationException ex)
            {
                status = AiRequestStatus.Failed;
                message = ex.Message;
            }
            catch (Exception ex)
            {
                // Any other failure still leaves its record; the caller sees the exception exactly as before.
                status = AiRequestStatus.Failed;
                message = RequestFailedMessage;
                unexpected = ex;
            }

            running.Stop();
            var preempted = status == AiRequestStatus.Cancelled && place.WasPreempted && !ct.IsCancellationRequested;
            var outcome = await FinishAsync(
                caller,
                info,
                startedAt,
                status,
                message,
                wait,
                running.Elapsed,
                preempted: preempted,
                recordMessage: status == AiRequestStatus.Failed ? RequestFailedMessage : null).ConfigureAwait(false);
            if (unexpected is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(unexpected).Throw();
            }

            return new AiImageTextResult(value, outcome);
        }
    }

    // ── Availability ─────────────────────────────────────────────────────────

    public async Task<AiAvailability> GetAvailabilityAsync(AiAbility ability, CancellationToken ct = default)
    {
        var info = AiAbilityCatalog.Get(ability);
        if (!info.IsAvailableNow)
        {
            return AiAvailability.Unavailable(NotAvailableYet);
        }

        var reason = info.Engine == AiEngine.Tesseract
            ? await CheckTextReadingAvailabilityAsync(ct).ConfigureAwait(false)
            : CheckTextAvailability(info.Role!.Value, applyLocalAiSwitch: true);
        return reason is null ? AiAvailability.Available : AiAvailability.Unavailable(reason);
    }

    private string? CheckTextAvailability(AiModelRole role, bool applyLocalAiSwitch)
    {
        if (applyLocalAiSwitch && !_switches.IsLocalAiOn(_settings))
        {
            return LocalAiOff;
        }

        if (!_runtime.TextRuntimeLoads)
        {
            return RuntimeNotInstalled;
        }

        return _inventory.GetState(role) is AiModelState.Ready or AiModelState.Loaded ? null : ModelNotDownloaded;
    }

    private async Task<string?> CheckTextReadingAvailabilityAsync(CancellationToken ct)
    {
        if (!_configuration.LoadOcr().Enabled)
        {
            return TextReadingOff;
        }

        var capability = await _ocr.GetCapabilityAsync(ct).ConfigureAwait(false);
        return capability.IsAvailable ? null : TextRecognitionNotInstalled;
    }

    // ── Shared steps ─────────────────────────────────────────────────────────

    private static void ValidateCaller(AiCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller.Id);
    }

    private AiRequestOutcome Defer(AiCaller caller, AiModelRole role, string reason)
    {
        PerformanceMetrics.BackgroundAiDeferrals.Add(
            1,
            new KeyValuePair<string, object?>("role", role.ToString()),
            new KeyValuePair<string, object?>("reason", reason));
        _logger.LogDebug("Background AI {Caller} ({Role}) deferred: {Reason}", caller.Id, role, reason);

        // Deferred requests write no usage record: background polls would flood the table.
        return new AiRequestOutcome(AiRequestStatus.Deferred, MachineBusy);
    }

    private Task<AiRequestOutcome> FinishWaitFailureAsync(
        AiCaller caller,
        AiAbilityInfo info,
        DateTimeOffset startedAt,
        TimeSpan waited,
        CancellationToken ct) =>
        ct.IsCancellationRequested
            ? FinishAsync(caller, info, startedAt, AiRequestStatus.Cancelled, CancelledMessage, waited)
            : FinishAsync(caller, info, startedAt, AiRequestStatus.TimedOut, WaitedTooLongMessage, waited);

    private async Task<AiRequestOutcome> FinishAsync(
        AiCaller caller,
        AiAbilityInfo info,
        DateTimeOffset startedAt,
        AiRequestStatus status,
        string message,
        TimeSpan wait = default,
        TimeSpan duration = default,
        bool invalidResponse = false,
        bool preempted = false,
        string? recordMessage = null)
    {
        var outcome = new AiRequestOutcome(status, message, wait, duration, invalidResponse, preempted);
        try
        {
            await _usage.AddAsync(
                new AiUsageRecord(
                    Guid.NewGuid(),
                    startedAt,
                    caller.Kind,
                    caller.Id,
                    info.Ability,
                    info.Engine,
                    (int)Math.Min(wait.TotalMilliseconds, int.MaxValue),
                    (int)Math.Min(duration.TotalMilliseconds, int.MaxValue),
                    status,
                    recordMessage ?? (status == AiRequestStatus.Succeeded ? null : message)),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A usage record must never fail the AI request itself.
            _logger.LogWarning(ex, "Could not record Local AI use by {Caller} ({Ability}).", caller.Id, info.Ability);
        }

        return outcome;
    }

    private void OnInteractiveTrafficStarted()
    {
        // Someone opened the app: running Library background text work steps aside, as it always has.
        if (_textQueue.Preempt(AiCallerKind.LibraryBackground) > 0)
        {
            PerformanceMetrics.BackgroundAiPreemptions.Add(1);
            _logger.LogInformation("Cancelling background AI inference because interactive work arrived");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _admission.InteractiveTrafficStarted -= OnInteractiveTrafficStarted;
        }
    }
}
