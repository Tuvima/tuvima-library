namespace MediaEngine.Web.Components.Shared;

/// <summary>The two Pop-out sizes (Look v3 D14): a Menu grows with its longest label between 240 and 360 px and has 48 px rows (Look v3.1 D2), a Panel is 400 px wide for tools. Both become a Sheet on phones.</summary>
public enum AppPopoutSize
{
    /// <summary>No fixed size: the caller's own width stays (typed select, autocomplete).</summary>
    None = 0,

    /// <summary>240 to 360 px, 48 px rows: "..." menus and account menus.</summary>
    Menu = 1,

    /// <summary>400 px: tools such as the player's speed, audio, subtitles and queue, and the library folders pop-over.</summary>
    Panel = 2,
}
