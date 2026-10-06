namespace MediaEngine.Web.Services.Theming;

/// <summary>First-party dark Dashboard theme identity; presentation variables live in AppThemeStyles.</summary>
public sealed class ThemeService
{
    public bool IsDarkMode => true;
    public string TextPrimary => "#F5F7FB";
    public string Background => "#070A12";
}
