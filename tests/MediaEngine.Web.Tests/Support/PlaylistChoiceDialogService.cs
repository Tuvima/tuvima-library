using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests.Support;

/// <summary>
/// A hand-written stand-in for the dialog service that hands back the playlist choice a person would have made, without drawing the
/// window, and remembers what the caller asked it to show (title, parameters, options such as the phone bottom sheet).
/// </summary>
internal sealed class PlaylistChoiceDialogService : IAppDialogService
{
    /// <summary>What the person "chose"; null means they closed the list without choosing.</summary>
    public PlaylistChoice? Choice { get; set; }

    /// <summary>When set, the window stays open (its result does not arrive) until the test completes this.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public List<string?> Titles { get; } = [];

    public AppDialogParameters? LastParameters { get; private set; }

    public AppDialogOptions? LastOptions { get; private set; }

    public Task<IAppDialogReference> ShowAsync<TComponent>(string? title = null, AppDialogParameters? parameters = null, AppDialogOptions? options = null)
        where TComponent : IComponent
    {
        Titles.Add(title);
        LastParameters = parameters;
        LastOptions = options;
        return Task.FromResult<IAppDialogReference>(new Reference(Choice, Gate));
    }

    public Task<IAppDialogReference> ShowAsync<TComponent>(string? title, AppDialogOptions options)
        where TComponent : IComponent => ShowAsync<TComponent>(title, null, options);

    public Task<bool?> ShowMessageBoxAsync(string? title, string message, string yesText = "OK", string? noText = null, string? cancelText = null, AppDialogOptions? options = null) =>
        Task.FromResult<bool?>(null);

    public Task<bool?> ShowMessageBoxAsync(string? title, MarkupString message, string yesText = "OK", string? noText = null, string? cancelText = null, AppDialogOptions? options = null) =>
        Task.FromResult<bool?>(null);

    private sealed class Reference(PlaylistChoice? choice, TaskCompletionSource? gate) : IAppDialogReference
    {
        public Guid Id { get; } = Guid.NewGuid();

        public Task<AppDialogResult?> Result => ResultAsync();

        private async Task<AppDialogResult?> ResultAsync()
        {
            if (gate is not null)
            {
                await gate.Task;
            }

            return choice is null ? AppDialogResult.Cancel() : AppDialogResult.Ok(choice);
        }

        public void Close(AppDialogResult? result = null)
        {
        }
    }
}
