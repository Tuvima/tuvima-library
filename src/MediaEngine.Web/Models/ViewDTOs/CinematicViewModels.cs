namespace MediaEngine.Web.Models.ViewDTOs;

public sealed record SurfaceTabItem(
    string Key,
    string Label,
    string Icon,
    int? Count = null);

public enum CinematicHeroDensity
{
    Full,
    CompactAudio,
}
