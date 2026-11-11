namespace MediaEngine.Web.Components.Shared;

/// <summary>The two Pop-out sizes (Look v3 D14): a Menu is 272 px wide with 44 px rows, a Panel is 400 px wide for tools. Both become a Sheet on phones.</summary>
public enum AppPopoutSize
{
    /// <summary>No fixed size: the caller's own width stays (typed select, autocomplete).</summary>
    None = 0,

    /// <summary>272 px, 44 px rows: "..." menus and account menus.</summary>
    Menu = 1,

    /// <summary>400 px: tools such as the player's speed, audio, subtitles and queue, and the library folders pop-over.</summary>
    Panel = 2,
}
