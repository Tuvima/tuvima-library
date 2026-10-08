using System.Collections;
using System.Linq.Expressions;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Services.Ui;

public sealed record AppDialogOptions
{
    public AppMaxWidth? MaxWidth { get; init; }
    public AppDialogPosition? Position { get; init; }
    public bool? FullWidth { get; init; }
    public bool? FullScreen { get; init; }
    public bool? CloseButton { get; init; }
    public bool? BackdropClick { get; init; }
    public bool? CloseOnEscapeKey { get; init; }
    public bool? NoHeader { get; init; }
    public string? BackgroundClass { get; init; }
}

public class AppDialogParameters : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    public void Add(string name, object? value) => _values[name] = value;
    public object? this[string name] { get => _values[name]; set => _values[name] = value; }
    public IDictionary<string, object> ToDictionary() => _values.ToDictionary(pair => pair.Key, pair => pair.Value!);
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class AppDialogParameters<TComponent> : AppDialogParameters
{
    public void Add<TValue>(Expression<Func<TComponent, TValue>> expression, TValue value)
    {
        if (expression.Body is not MemberExpression member)
        {
            throw new ArgumentException("A component parameter property is required.", nameof(expression));
        }
        Add(member.Member.Name, value);
    }
}

public sealed record AppDialogResult(object? Data, bool Canceled, Type? DataType = null)
{
    public static AppDialogResult Ok<T>(T data) => new(data, false, typeof(T));
    public static AppDialogResult Cancel() => new(null, true);
}

public interface IAppDialogReference
{
    Guid Id { get; }
    Task<AppDialogResult?> Result { get; }
    void Close(AppDialogResult? result = null);
}

public interface IAppDialogContext
{
    Guid Id { get; }
    AppDialogOptions Options { get; }
    string? PresentationClass { get; }
    void Close(AppDialogResult? result = null);
    void Cancel();
    Task CloseAsync(AppDialogResult? result = null);
    Task CancelAsync();
    Task SetOptionsAsync(AppDialogOptions options);
    Task SetPresentationClassAsync(string? value);
    void SetCloseGuard(Func<Task<bool>>? guard);
    IDisposable RegisterCloseGuard(Func<Task<bool>> guard);
}

public interface IAppDialogService
{
    Task<IAppDialogReference> ShowAsync<TComponent>(string? title = null, AppDialogParameters? parameters = null, AppDialogOptions? options = null) where TComponent : IComponent;
    Task<IAppDialogReference> ShowAsync<TComponent>(string? title, AppDialogOptions options) where TComponent : IComponent;
    Task<bool?> ShowMessageBoxAsync(string? title, string message, string yesText = "OK", string? noText = null, string? cancelText = null, AppDialogOptions? options = null);
    Task<bool?> ShowMessageBoxAsync(string? title, MarkupString message, string yesText = "OK", string? noText = null, string? cancelText = null, AppDialogOptions? options = null);
}

public sealed class AppDialogService : IAppDialogService, IDisposable
{
    private readonly List<AppDialogEntry> _dialogs = [];
    private readonly List<Guid> _visibleFrames = [];
    public IReadOnlyList<AppDialogEntry> Dialogs => _dialogs;
    public Guid? ActiveFrameId => _visibleFrames.Count > 0 ? _visibleFrames[^1] : null;
    public event Action? Changed;
    private bool _disposed;

    public Task<IAppDialogReference> ShowAsync<TComponent>(string? title = null, AppDialogParameters? parameters = null, AppDialogOptions? options = null) where TComponent : IComponent
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entry = new AppDialogEntry(this, typeof(TComponent), title, parameters ?? new(), options ?? new());
        _dialogs.Add(entry);
        Changed?.Invoke();
        return Task.FromResult<IAppDialogReference>(entry);
    }
    public Task<IAppDialogReference> ShowAsync<TComponent>(string? title, AppDialogOptions options) where TComponent : IComponent => ShowAsync<TComponent>(title, null, options);
    public Task<bool?> ShowMessageBoxAsync(string? title, string message, string yesText = "OK", string? noText = null, string? cancelText = null, AppDialogOptions? options = null) => MessageBoxAsync(title, message, yesText, noText, cancelText, options);
    public Task<bool?> ShowMessageBoxAsync(string? title, MarkupString message, string yesText = "OK", string? noText = null, string? cancelText = null, AppDialogOptions? options = null) => MessageBoxAsync(title, message, yesText, noText, cancelText, options);
    private async Task<bool?> MessageBoxAsync(string? title, object message, string yesText, string? noText, string? cancelText, AppDialogOptions? options)
    {
        var parameters = new AppDialogParameters { { nameof(AppMessageBox.Message), message }, { nameof(AppMessageBox.YesText), yesText }, { nameof(AppMessageBox.NoText), noText }, { nameof(AppMessageBox.CancelText), cancelText } };
        var dialog = await ShowAsync<AppMessageBox>(title, parameters, options ?? new() { MaxWidth = AppMaxWidth.Small, FullWidth = true });
        var result = await dialog.Result;
        return result is { Canceled: false, Data: bool answer } ? answer : null;
    }
    internal void NotifyChanged() => Changed?.Invoke();
    public void SetFrameVisibility(Guid id, bool visible)
    {
        if (visible) { if (_visibleFrames.Contains(id))
        {
            return;
        } _visibleFrames.Add(id); }
        else if (!_visibleFrames.Remove(id))
        {
            return;
        }
        Changed?.Invoke();
    }
    internal void Complete(AppDialogEntry entry, AppDialogResult? result)
    {
        if (!_dialogs.Remove(entry))
        {
            return;
        }
        entry.Complete(result);
        Changed?.Invoke();
    }
    public void Dispose()
    {
        _disposed = true;
        foreach (var dialog in _dialogs)
        {
            dialog.Complete(AppDialogResult.Cancel());
        }
        _dialogs.Clear();
        _visibleFrames.Clear();
        Changed = null;
    }
}

public sealed class AppDialogEntry : IAppDialogReference, IAppDialogContext
{
    private readonly AppDialogService _owner;
    private readonly TaskCompletionSource<AppDialogResult?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<Task<bool>>? _guard;
    private bool _closing;
    internal AppDialogEntry(AppDialogService owner, Type componentType, string? title, AppDialogParameters parameters, AppDialogOptions options)
    { _owner = owner; ComponentType = componentType; Title = title; Parameters = parameters.ToDictionary(); Options = options; }
    public Guid Id { get; } = Guid.NewGuid();
    public Type ComponentType { get; }
    public string? Title { get; }
    public IDictionary<string, object> Parameters { get; }
    public Task<AppDialogResult?> Result => _result.Task;
    public AppDialogOptions Options { get; private set; }
    public string? PresentationClass { get; private set; }
    public void Close(AppDialogResult? result = null) => _ = CloseAsync(result);
    public void Cancel() => _ = CancelAsync();
    public Task CancelAsync() => CloseAsync(AppDialogResult.Cancel());
    public async Task CloseAsync(AppDialogResult? result = null)
    {
        if (_closing || _result.Task.IsCompleted)
        {
            return;
        }
        _closing = true;
        try { if (_guard is null || await _guard())
        {
            _owner.Complete(this, result ?? AppDialogResult.Cancel());
        } }
        finally { _closing = false; }
    }
    public Task SetOptionsAsync(AppDialogOptions options) { Options = options; _owner.NotifyChanged(); return Task.CompletedTask; }
    public Task SetPresentationClassAsync(string? value) { if (PresentationClass == value)
    {
        return Task.CompletedTask;
    } PresentationClass = value; _owner.NotifyChanged(); return Task.CompletedTask; }
    public void SetCloseGuard(Func<Task<bool>>? guard) => _guard = guard;
    public IDisposable RegisterCloseGuard(Func<Task<bool>> guard) { _guard = guard; return new GuardRegistration(this, guard); }
    internal void Complete(AppDialogResult? result) => _result.TrySetResult(result);
    private sealed class GuardRegistration(AppDialogEntry context, Func<Task<bool>> guard) : IDisposable
    { public void Dispose() { if (context._guard == guard)
    {
        context._guard = null;
    } } }
}
