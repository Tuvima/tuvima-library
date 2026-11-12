namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// The lines the open Playback information view is showing, shared with its footer so "Copy details" copies exactly what is on screen (video-player
/// follow-ups D14). The view writes it each time it draws; the footer reads it when the button is pressed. Held for one opening of the view.
/// </summary>
public sealed class PlaybackInformationState
{
    private IReadOnlyList<PlaybackInformationSection> _sections = [];

    public IReadOnlyList<PlaybackInformationSection> Sections => _sections;

    /// <summary>The lines as plain text ("Section" lines, then "Label: value" lines); empty when there is nothing to show.</summary>
    public string Text => PlaybackInformationRows.ToPlainText(_sections);

    public bool HasText => _sections.Count > 0;

    public event Action? Changed;

    public void Update(IReadOnlyList<PlaybackInformationSection> sections)
    {
        _sections = sections;
        Changed?.Invoke();
    }
}
