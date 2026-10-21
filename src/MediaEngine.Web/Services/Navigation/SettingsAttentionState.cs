using System.Globalization;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Settings;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// What needs a server administrator's attention in Settings, held once so the banner above every Settings page, the
/// shading in the Settings menu and the Local AI page notice can never disagree: the Needs Review count, the Required
/// tools that are missing (with download progress), whether Local AI is blocked, and which banner lines this profile
/// dismissed. It shows nothing at all to anyone who is not a server administrator, and does not ask the Engine for them.
/// </summary>
/// <remarks>
/// The Settings page starts it when it opens and stops it when it closes. While running it looks again rarely when
/// nothing is happening and every couple of seconds while a download is running, and follows the live Needs Review
/// count. A banner line is dismissed with what was true at the time (the count, or the missing tool ids) and returns
/// only when something new needs attention.
/// </remarks>
public sealed class SettingsAttentionState(
    IEngineApiClient api,
    UIOrchestratorService orchestrator,
    DashboardSessionAccessor session,
    ILogger<SettingsAttentionState>? logger = null) : IDisposable
{
    /// <summary>Stored when the Needs Review count fell to zero, so a later rise brings the line back.</summary>
    private const string NoReviewItemsFingerprint = "0";

    /// <summary>Stored when no Required tool was missing, so a tool that breaks later brings the line back.</summary>
    private const string NoMissingToolsFingerprint = "none";

    private readonly object _gate = new();
    private CancellationTokenSource? _watching;
    private ToolRequirementsDto? _tools;
    private Dictionary<string, string> _dismissed = new(StringComparer.Ordinal);
    private int _reviewCount;
    private bool _loaded;
    private bool _pluginsBroken;
    private bool _integrationsBroken;
    private bool _aiFeaturesOn;
    private bool _dismissalsLoaded;
    private bool _busy;
    private string? _actionError;
    private bool _disposed;

    /// <summary>How often everything is read again while nothing is happening: rarely, because the banner is a note, not a monitor.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often progress is read while a download is running.</summary>
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Raised after the state changes: counts loaded or moved, a line dismissed, a download progressed.</summary>
    public event Action? Changed;

    /// <summary>A server administrator (the only person who is told). Anyone else sees and triggers nothing.</summary>
    public bool IsServerAdministrator => session.Authority?.EffectiveAdministrator == true;

    /// <summary>True once the first read has finished, so all lines appear in one update and the page moves at most once.</summary>
    public bool IsLoaded => Volatile.Read(ref _loaded);

    /// <summary>True while an install or download this state started is being requested.</summary>
    public bool IsWorking => Volatile.Read(ref _busy);

    /// <summary>The Engine's plain reason the last install or download request was refused, if it was.</summary>
    public string? ActionError => Volatile.Read(ref _actionError);

    /// <summary>Items waiting in Needs Review; zero for anyone who is not a server administrator.</summary>
    public int ReviewCount => IsServerAdministrator ? Volatile.Read(ref _reviewCount) : 0;

    /// <summary>Required tools that are not ready: missing, broken or being downloaded. Empty for anyone who is not a server administrator.</summary>
    public IReadOnlyList<ToolRequirementDto> RequiredToolsNotReady => !IsServerAdministrator
        ? []
        : Volatile.Read(ref _tools)?.Tools
            .Where(tool => tool.Kind == "tool"
                && tool.Requirement == ToolRequirementLevels.Required
                && tool.State != ToolRequirementStates.Ready)
            .OrderBy(tool => tool.Id, StringComparer.Ordinal)
            .ToList() ?? [];

    /// <summary>The Required tools being downloaded right now.</summary>
    public IReadOnlyList<ToolRequirementDto> DownloadingTools =>
        RequiredToolsNotReady.Where(tool => tool.State == ToolRequirementStates.Downloading).ToList();

    /// <summary>The Required tools that are missing or broken and not being downloaded.</summary>
    public IReadOnlyList<ToolRequirementDto> MissingTools =>
        RequiredToolsNotReady.Where(tool => tool.State != ToolRequirementStates.Downloading).ToList();

    /// <summary>The id of the fast text model: the one model Local AI cannot run without.</summary>
    public const string FastTextModelId = ToolRequirementIds.AiModelPrefix + "text_fast";

    /// <summary>
    /// What stops Local AI working: the local AI engine or the fast text model, whichever is not ready. The other models
    /// (quality, audio, scholar, CJK) are optional extras, so their absence blocks nothing.
    /// </summary>
    public IReadOnlyList<ToolRequirementDto> LocalAiBlockers => !IsServerAdministrator
        ? []
        : Volatile.Read(ref _tools)?.Tools
            .Where(tool => (tool.Id == ToolRequirementIds.AiRuntime || tool.Id == FastTextModelId)
                && tool.State != ToolRequirementStates.Ready)
            .ToList() ?? [];

    /// <summary>Local AI is switched off until the tools it needs are downloaded.</summary>
    public bool LocalAiBlocked => LocalAiBlockers.Count > 0;

    /// <summary>
    /// An AI feature plugin is switched on, so it needs Local AI. Local AI itself has no switch: it only counts as broken
    /// (red in the menu) when something switched on is waiting on it. False for anyone who is not a server administrator.
    /// </summary>
    public bool AiFeaturesOn => IsServerAdministrator && Volatile.Read(ref _aiFeaturesOn);

    /// <summary>The Official AI feature plugins all have ids starting with this.</summary>
    private const string AiFeaturePluginPrefix = "tuvima.ai-";

    private static bool AnyAiFeatureOn(IEnumerable<PluginSummaryResponse> plugins) =>
        plugins.Any(plugin => plugin.Enabled && plugin.Id.StartsWith(AiFeaturePluginPrefix, StringComparison.Ordinal));

    /// <summary>
    /// A plugin that is switched on cannot do its job (it did not load, or it does not work with this Library), so the
    /// Plugins menu row shows red. A plugin that is switched off never counts. False for anyone who is not a server administrator.
    /// </summary>
    public bool PluginsBroken => IsServerAdministrator && Volatile.Read(ref _pluginsBroken);

    /// <summary>
    /// A webhook that is switched on keeps failing, so it cannot do its job and the Applications row shows red. One failed
    /// delivery never does this. False for anyone who is not a server administrator.
    /// </summary>
    public bool IntegrationsBroken => IsServerAdministrator && Volatile.Read(ref _integrationsBroken);

    /// <summary>
    /// Takes the plugin list the Plugins page just loaded or changed, so the menu agrees with the page straight away
    /// instead of waiting for the next look.
    /// </summary>
    public void ReportPlugins(IEnumerable<PluginSummaryResponse> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        var list = plugins as IReadOnlyCollection<PluginSummaryResponse> ?? plugins.ToList();
        var broken = list.Any(StateCardHealth.IsPluginBroken);
        var aiOn = AnyAiFeatureOn(list);
        var changed = Interlocked.Exchange(ref _pluginsBroken, broken) != broken;
        changed |= Interlocked.Exchange(ref _aiFeaturesOn, aiOn) != aiOn;
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>The Needs Review line is shown: items are waiting and none of these were already dismissed.</summary>
    public bool ReviewLineVisible
    {
        get
        {
            if (!IsLoaded || ReviewCount <= 0)
            {
                return false;
            }

            return !TryGetDismissal(ProfileNoticeKeys.NeedsReview, out var fingerprint)
                || !int.TryParse(fingerprint, NumberStyles.None, CultureInfo.InvariantCulture, out var dismissedCount)
                || ReviewCount > dismissedCount;
        }
    }

    /// <summary>
    /// The tools line is shown: a download is running (always shown, never dismissed), or a tool is missing that the
    /// dismissal did not already cover.
    /// </summary>
    public bool ToolsLineVisible
    {
        get
        {
            if (!IsLoaded)
            {
                return false;
            }

            if (DownloadingTools.Count > 0)
            {
                return true;
            }

            var missing = MissingTools;
            if (missing.Count == 0)
            {
                return false;
            }

            if (!TryGetDismissal(ProfileNoticeKeys.MissingTools, out var fingerprint))
            {
                return true;
            }

            var covered = fingerprint.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            return missing.Any(tool => !covered.Contains(tool.Id));
        }
    }

    // ── Starting and stopping ────────────────────────────────────────────────

    /// <summary>
    /// Reads everything once and then keeps it fresh until <see cref="Stop"/>. Calling it again while running does nothing.
    /// The first read finishes before <see cref="IsLoaded"/> turns true, so lines never trickle in one by one.
    /// </summary>
    public async Task StartAsync()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || _watching is not null)
            {
                return;
            }

            _watching = new CancellationTokenSource();
            token = _watching.Token;
            session.OnAuthorityChanged += OnAuthorityChanged;
            orchestrator.OnReviewCountChanged += OnReviewCountChanged;
            orchestrator.OnProfileChanged += OnProfileChanged;
        }

        await RefreshAsync(reloadDismissals: true, token).ConfigureAwait(false);
        _ = WatchAsync(token);
    }

    /// <summary>Stops looking. The last values stay, so reopening Settings shows them at once and then refreshes.</summary>
    public void Stop()
    {
        CancellationTokenSource? watching;
        lock (_gate)
        {
            watching = _watching;
            _watching = null;
            session.OnAuthorityChanged -= OnAuthorityChanged;
            orchestrator.OnReviewCountChanged -= OnReviewCountChanged;
            orchestrator.OnProfileChanged -= OnProfileChanged;
        }

        if (watching is not null)
        {
            watching.Cancel();
            watching.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Stop();
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    /// <summary>Reads the count, the tools and (when asked) this profile's dismissals, then tells listeners once.</summary>
    public async Task RefreshAsync(bool reloadDismissals, CancellationToken ct = default)
    {
        try
        {
            if (!IsServerAdministrator)
            {
                Volatile.Write(ref _reviewCount, 0);
                Volatile.Write(ref _tools, null);
                Volatile.Write(ref _pluginsBroken, false);
                Volatile.Write(ref _integrationsBroken, false);
                Volatile.Write(ref _aiFeaturesOn, false);
                Volatile.Write(ref _loaded, true);
                Changed?.Invoke();
                return;
            }

            var reviewTask = orchestrator.TryRefreshReviewCountAsync(ct);
            var toolsTask = api.GetToolRequirementsAsync(ct);
            var pluginsTask = api.GetPluginsAsync(ct);
            var integrationsTask = api.GetIntegrationHealthAsync(ct);
            var noticesTask = reloadDismissals || !HasDismissalsLoaded
                ? api.GetSettingsNoticesAsync(ct)
                : Task.FromResult<ProfileNoticesResponse?>(null);
            await Task.WhenAll(reviewTask, toolsTask, pluginsTask, integrationsTask, noticesTask).ConfigureAwait(false);

            // The Engine could not be asked (or this person may not see the queue): keep what was last known rather than
            // clearing a real problem, and never treat the failed read as "nothing needs review".
            var reviewRead = await reviewTask.ConfigureAwait(false);
            if (reviewRead is { } reviewCount)
            {
                Volatile.Write(ref _reviewCount, reviewCount);
            }

            var toolsRead = await toolsTask.ConfigureAwait(false);
            if (toolsRead is { } tools)
            {
                Volatile.Write(ref _tools, tools);
            }

            var pluginsRead = await pluginsTask.ConfigureAwait(false);
            Volatile.Write(ref _pluginsBroken, pluginsRead.Any(StateCardHealth.IsPluginBroken));
            Volatile.Write(ref _aiFeaturesOn, AnyAiFeatureOn(pluginsRead));
            if (await integrationsTask.ConfigureAwait(false) is { } integrations)
            {
                Volatile.Write(ref _integrationsBroken, integrations.BrokenWebhooks > 0);
            }

            if (await noticesTask.ConfigureAwait(false) is { } notices)
            {
                lock (_gate)
                {
                    _dismissed = notices.Dismissed.ToDictionary(row => row.Key, row => row.Fingerprint, StringComparer.Ordinal);
                    _dismissalsLoaded = true;
                }
            }

            await ForgetDismissalsOfSolvedProblemsAsync(reviewRead == 0, toolsRead is not null, ct).ConfigureAwait(false);
            Volatile.Write(ref _loaded, true);
            Changed?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Settings closed while a read was running; there is nothing left to show.
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Reading what needs the administrator's attention in Settings failed.");
        }
    }

    private bool HasDismissalsLoaded
    {
        get
        {
            lock (_gate)
            {
                return _dismissalsLoaded;
            }
        }
    }

    // When the Engine reports the Needs Review count at zero, or every Required tool works, the earlier dismissal is
    // replaced by a "nothing was wrong" mark, so the line comes back the next time something is. Only a read that
    // succeeded in this refresh counts: a failed read must never erase what the administrator chose to dismiss.
    private async Task ForgetDismissalsOfSolvedProblemsAsync(bool reviewReadAsZero, bool toolsReadNow, CancellationToken ct)
    {
        if (reviewReadAsZero
            && TryGetDismissal(ProfileNoticeKeys.NeedsReview, out var review) && review != NoReviewItemsFingerprint)
        {
            await StoreDismissalAsync(ProfileNoticeKeys.NeedsReview, NoReviewItemsFingerprint, ct).ConfigureAwait(false);
        }

        if (toolsReadNow && RequiredToolsNotReady.Count == 0
            && TryGetDismissal(ProfileNoticeKeys.MissingTools, out var tools) && tools != NoMissingToolsFingerprint)
        {
            await StoreDismissalAsync(ProfileNoticeKeys.MissingTools, NoMissingToolsFingerprint, ct).ConfigureAwait(false);
        }
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(DownloadingTools.Count > 0 || IsLocalAiDownloading ? ProgressInterval : RefreshInterval, ct).ConfigureAwait(false);
                await RefreshAsync(reloadDismissals: false, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Settings closed; there is nothing left to refresh.
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Refreshing what needs the administrator's attention in Settings stopped.");
        }
    }

    private bool IsLocalAiDownloading => LocalAiBlockers.Any(tool => tool.State == ToolRequirementStates.Downloading);

    private void OnAuthorityChanged() => _ = RefreshInBackgroundAsync(reloadDismissals: true);

    private void OnProfileChanged() => _ = RefreshInBackgroundAsync(reloadDismissals: true);

    private void OnReviewCountChanged()
    {
        Volatile.Write(ref _reviewCount, orchestrator.ReviewCount);
        if (IsLoaded)
        {
            Changed?.Invoke();
        }
    }

    private async Task RefreshInBackgroundAsync(bool reloadDismissals)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_watching is null)
            {
                return;
            }

            token = _watching.Token;
        }

        await RefreshAsync(reloadDismissals, token).ConfigureAwait(false);
    }

    // ── Dismissing ───────────────────────────────────────────────────────────

    /// <summary>Hides the Needs Review line until the count grows. The menu keeps its amber shading.</summary>
    public Task DismissReviewAsync(CancellationToken ct = default) =>
        DismissAsync(ProfileNoticeKeys.NeedsReview, ReviewCount.ToString(CultureInfo.InvariantCulture), ct);

    /// <summary>Hides the missing tools line until a tool that was not in it goes missing.</summary>
    public Task DismissToolsAsync(CancellationToken ct = default)
    {
        var ids = MissingTools.Select(tool => tool.Id).ToList();
        return ids.Count == 0
            ? Task.CompletedTask
            : DismissAsync(ProfileNoticeKeys.MissingTools, string.Join(',', ids), ct);
    }

    /// <summary>
    /// Hides the line straight away and asks the Engine to remember it. If the Engine cannot save the choice the line
    /// stays hidden for this visit only, and returns next time.
    /// </summary>
    private async Task DismissAsync(string noticeKey, string fingerprint, CancellationToken ct)
    {
        if (!IsServerAdministrator)
        {
            return;
        }

        lock (_gate)
        {
            _dismissed[noticeKey] = fingerprint;
        }

        Changed?.Invoke();
        await PersistDismissalAsync(noticeKey, fingerprint, ct).ConfigureAwait(false);
    }

    private async Task StoreDismissalAsync(string noticeKey, string fingerprint, CancellationToken ct)
    {
        lock (_gate)
        {
            _dismissed[noticeKey] = fingerprint;
        }

        await PersistDismissalAsync(noticeKey, fingerprint, ct).ConfigureAwait(false);
    }

    private async Task PersistDismissalAsync(string noticeKey, string fingerprint, CancellationToken ct)
    {
        if (!await api.DismissSettingsNoticeAsync(noticeKey, fingerprint, ct).ConfigureAwait(false))
        {
            logger?.LogWarning("The Engine could not remember dismissing the {NoticeKey} notice.", noticeKey);
        }
    }

    private bool TryGetDismissal(string noticeKey, out string fingerprint)
    {
        lock (_gate)
        {
            return _dismissed.TryGetValue(noticeKey, out fingerprint!);
        }
    }

    // ── Installing and downloading ───────────────────────────────────────────

    /// <summary>The label of the missing tools button: install when the Engine offers it, download again for a broken tool, else go to the Tools page.</summary>
    public MissingToolsAction MissingToolsButton =>
        MissingTools.Any(tool => tool.CanInstall) ? MissingToolsAction.Install
        : MissingTools.Any(tool => tool.CanDownloadAgain) ? MissingToolsAction.DownloadAgain
        : MissingToolsAction.SeeTools;

    /// <summary>Starts the download of every missing Required tool the Engine offers to install (or download again).</summary>
    public async Task InstallMissingToolsAsync(CancellationToken ct = default)
    {
        if (!IsServerAdministrator || IsWorking)
        {
            return;
        }

        SetWorking(true, null);
        string? error = null;
        try
        {
            foreach (var tool in MissingTools.Where(tool => tool.CanInstall || tool.CanDownloadAgain))
            {
                var result = await api.InstallToolAsync(tool.Id, ct).ConfigureAwait(false);
                if (result.Success)
                {
                    if (result.Tools is { } fresh)
                    {
                        Volatile.Write(ref _tools, fresh);
                    }
                }
                else
                {
                    error ??= result.ErrorMessage;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Settings closed while the request was running.
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Starting the download of a missing tool failed.");
            error = "The Library could not start that download. Check that the server is online and try again.";
        }
        finally
        {
            SetWorking(false, error);
        }
    }

    /// <summary>
    /// Starts the downloads the Tools page offers for the Local AI models that are missing. The local AI engine itself is
    /// not installed this way, so it is left to the Tools page.
    /// </summary>
    public async Task DownloadLocalAiAsync(CancellationToken ct = default)
    {
        if (!IsServerAdministrator || IsWorking)
        {
            return;
        }

        SetWorking(true, null);
        string? error = null;
        try
        {
            foreach (var model in LocalAiBlockers.Where(IsDownloadableModel))
            {
                var role = model.Id[ToolRequirementIds.AiModelPrefix.Length..];
                var result = await orchestrator.StartAiModelDownloadAsync(role, ct).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    error ??= $"The download of {model.Name} could not start.";
                }
            }

            await RefreshAsync(reloadDismissals: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Settings closed while the request was running.
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Starting the download of a Local AI model failed.");
            error = "The Library could not start that download. Check that the server is online and try again.";
        }
        finally
        {
            SetWorking(false, error);
        }
    }

    /// <summary>A Local AI model the Engine can download now.</summary>
    public static bool IsDownloadableModel(ToolRequirementDto tool) =>
        tool.Kind == "ai_model" && tool.CanDownloadAgain && tool.State != ToolRequirementStates.Downloading;

    private void SetWorking(bool working, string? error)
    {
        Volatile.Write(ref _busy, working);
        Volatile.Write(ref _actionError, error);
        Changed?.Invoke();
    }
}

/// <summary>What the missing tools line's button does.</summary>
public enum MissingToolsAction
{
    /// <summary>Install for me: the Engine can fetch the tool.</summary>
    Install,

    /// <summary>Download again: the tool is broken and the Engine can fetch it again.</summary>
    DownloadAgain,

    /// <summary>See tools: the Library cannot fix this itself, so open the Tools page.</summary>
    SeeTools,
}
