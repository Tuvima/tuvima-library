namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// The name a phone's back bar shows for the page below it. The shell hands one of these to the page it draws; a page that
/// is better named than its area (My profile, Household, a person's name under Household) sets it and clears it when it
/// closes, and the shell falls back to the area's name (Settings) while it is empty (profile-and-household D16).
/// </summary>
public sealed class PhoneBarTitle
{
    /// <summary>The page's own name, or null when the area's name is used.</summary>
    public string? Text { get; private set; }

    /// <summary>Raised when the text changed, so the shell draws the bar again.</summary>
    public event Action? Changed;

    public void Set(string? text)
    {
        var clean = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (string.Equals(Text, clean, StringComparison.Ordinal))
        {
            return;
        }

        Text = clean;
        Changed?.Invoke();
    }
}
