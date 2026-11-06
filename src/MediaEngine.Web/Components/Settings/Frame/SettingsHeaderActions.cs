using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// The slot at the top right of a Settings page header. <see cref="SettingsPageFrame"/> cascades one of these to the page
/// inside it, and a page that owns a header action (Libraries' Add library) sets it so the button sits on the title row
/// instead of in the content. The frame redraws its header when the content changes; a page clears it when it goes away.
/// </summary>
public sealed class SettingsHeaderActions
{
    /// <summary>Raised when <see cref="Content"/> was replaced.</summary>
    public event Action? Changed;

    /// <summary>What the header draws at its right edge; null draws nothing.</summary>
    public RenderFragment? Content { get; private set; }

    /// <summary>Replaces the content. Passing the same fragment again does nothing, so a page can call this on every render.</summary>
    public void Set(RenderFragment? content)
    {
        if (ReferenceEquals(Content, content))
        {
            return;
        }

        Content = content;
        Changed?.Invoke();
    }
}
