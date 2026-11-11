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

    /// <summary>Files finished so far while something is being added, or null when no progress is showing.</summary>
    public int? ProgressDone { get; private set; }

    public int? ProgressTotal { get; private set; }

    public bool InProgress => ProgressTotal is > 0;


    public event Action? Changed;

    public void Show(string message, bool isError = false)
    {
        Message = message;
        IsError = isError;
        ProgressDone = null;
        ProgressTotal = null;
        At = DateTimeOffset.Now;
        Changed?.Invoke();
    }

    /// <summary>Says what is being added and how far along it is; the ribbon draws a progress bar until the next <see cref="Show"/>.</summary>
    public void ShowProgress(string message, int done, int total)
    {
        Message = message;
        IsError = false;
        At = DateTimeOffset.Now;
        ProgressDone = done;
        ProgressTotal = total;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (Message is null)
        {
            return;
        }

        Message = null;
        ProgressDone = null;
        ProgressTotal = null;
        Changed?.Invoke();
    }
}
