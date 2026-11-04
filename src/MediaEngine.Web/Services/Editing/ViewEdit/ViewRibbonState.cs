namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// The slim ribbon under the top bar for View (spec photos-documents-editor D6, D16): it says plainly what was just saved or done, in words, with
/// no toast and no sound. The editor's Save and the one-click actions (Favorite, Archive, Add to gallery, New gallery) all report here, and it goes
/// away on its own.
/// </summary>
public sealed class ViewRibbonState
{
    public string? Message { get; private set; }

    public bool IsError { get; private set; }

    public DateTimeOffset At { get; private set; }

    public event Action? Changed;

    public void Show(string message, bool isError = false)
    {
        Message = message;
        IsError = isError;
        At = DateTimeOffset.Now;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (Message is null)
        {
            return;
        }

        Message = null;
        Changed?.Invoke();
    }
}
