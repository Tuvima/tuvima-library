using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Services.Ui;

public sealed class AppToastOptions
{
    public string? Action { get; set; }
    public Func<AppToast, Task>? OnClick { get; set; }
    public bool ShowCloseIcon { get; set; } = true;
    public bool? RequireInteraction { get; set; }
    public bool CloseAfterNavigation { get; set; }
    public int VisibleStateDuration { get; set; } = 5000;
    public int ShowTransitionDuration { get; set; } = 1000;
    public int HideTransitionDuration { get; set; } = 2000;
    public int MaximumOpacity { get; set; } = 95;
}
public sealed class AppToast(string message, AppSeverity severity, AppToastOptions options)
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Message { get; } = message;
    public AppSeverity Severity { get; } = severity;
    public AppToastOptions Options { get; } = options;
    internal int RemainingMilliseconds { get; set; } = Math.Max(0, options.ShowTransitionDuration) + Math.Max(0, options.VisibleStateDuration);
    internal bool Hiding { get; set; }
    internal long StartedAt { get; set; }
    internal bool PointerInside { get; set; }
    internal bool FocusInside { get; set; }
    internal bool Busy { get; set; }
    internal CancellationTokenSource? Timer { get; set; }
}
public interface IAppToastService
{
    AppToast Add(string message, AppSeverity severity = AppSeverity.Normal, Action<AppToastOptions>? configure = null);
    void Remove(AppToast toast);
    void Clear();
}
public sealed class AppToastService : IAppToastService, IDisposable
{
    public const int MaximumDisplayedToasts = 5;
    private readonly List<AppToast> _toasts = [];
    private readonly object _sync = new();
    public IReadOnlyList<AppToast> Toasts { get { lock (_sync)
    {
        return _toasts.ToArray();
    } } }
    public event Action? Changed;
    private bool _disposed;
    public AppToast Add(string message, AppSeverity severity = AppSeverity.Normal, Action<AppToastOptions>? configure = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var options = new AppToastOptions();
        configure?.Invoke(options);
        var toast = new AppToast(message, severity, options);
        lock (_sync)
        {
            var existing = _toasts.FirstOrDefault(item => string.Equals(item.Message, message, StringComparison.Ordinal));
            if (existing is not null)
            {
                return existing;
            }
            _toasts.Add(toast);
        }
        Start(toast);
        Changed?.Invoke();
        return toast;
    }
    public void Pause(AppToast toast, bool pointer, bool value)
    {
        var wasPaused = toast.PointerInside || toast.FocusInside || toast.Busy;
        if (pointer)
        {
            toast.PointerInside = value;
        }
        else
        {
            toast.FocusInside = value;
        }
        var paused = toast.PointerInside || toast.FocusInside || toast.Busy;
        if (paused && !wasPaused && toast.Timer is not null)
        {
            toast.RemainingMilliseconds = Math.Max(1, toast.RemainingMilliseconds - (int)(Environment.TickCount64 - toast.StartedAt));
            Stop(toast);
        }
        else if (!paused && wasPaused)
        {
            Start(toast);
        }
    }
    public async Task InvokeActionAsync(AppToast toast)
    {
        if (toast.Busy || toast.Options.OnClick is null)
        {
            return;
        }
        toast.Busy = true;
        Stop(toast);
        Changed?.Invoke();
        try { await toast.Options.OnClick(toast); Remove(toast); }
        finally { toast.Busy = false; if (Contains(toast) && !toast.PointerInside && !toast.FocusInside)
        {
            Start(toast);
        } Changed?.Invoke(); }
    }
    private void Start(AppToast toast)
    {
        if ((toast.Options.RequireInteraction ?? !string.IsNullOrWhiteSpace(toast.Options.Action)) || toast.PointerInside || toast.FocusInside || toast.Busy || !IsDisplayed(toast) || toast.Timer is not null)
        {
            return;
        }
        Stop(toast);
        toast.StartedAt = Environment.TickCount64;
        toast.Timer = new CancellationTokenSource();
        _ = ExpireAsync(toast, toast.Timer.Token);
    }
    private async Task ExpireAsync(AppToast toast, CancellationToken token)
    {
        try
        {
            await Task.Delay(Math.Max(1, toast.RemainingMilliseconds), token);
            if (toast.Hiding) { Remove(toast); return; }
            Stop(toast);
            toast.Hiding = true;
            toast.RemainingMilliseconds = Math.Max(0, toast.Options.HideTransitionDuration);
            Start(toast);
            Changed?.Invoke();
        }
        catch (OperationCanceledException) { }
    }
    private static void Stop(AppToast toast) { toast.Timer?.Cancel(); toast.Timer?.Dispose(); toast.Timer = null; }
    private bool Contains(AppToast toast) { lock (_sync)
    {
        return _toasts.Contains(toast);
    } }
    private bool IsDisplayed(AppToast toast) { lock (_sync) { var index = _toasts.IndexOf(toast); return index >= 0 && index < MaximumDisplayedToasts; } }
    public void Remove(AppToast toast)
    {
        bool removed;
        AppToast[] displayed;
        lock (_sync) { Stop(toast); removed = _toasts.Remove(toast); displayed = _toasts.Take(MaximumDisplayedToasts).ToArray(); }
        if (!removed)
        {
            return;
        }
        foreach (var item in displayed)
        {
            Start(item);
        }
        Changed?.Invoke();
    }
    public void Clear() { lock (_sync) { foreach (var toast in _toasts)
    {
        Stop(toast);
    } _toasts.Clear(); } Changed?.Invoke(); }
    public void Dispose() { _disposed = true; Clear(); Changed = null; }
}
