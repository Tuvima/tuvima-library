using MudBlazor;

namespace MediaEngine.Web.Services.Theming;

/// <summary>
/// Manages the active UI theme. Dark mode only; light mode has been removed.
/// The theme is aligned to the centralized Tuvima design tokens.
/// </summary>
public sealed class ThemeService
{
    /// <summary>Always dark; light mode has been removed.</summary>
    public bool IsDarkMode => true;

    /// <summary>
    /// The active MudBlazor theme. Uses the centralized dark media-library palette.
    /// </summary>
    public MudTheme Theme { get; } = BuildTheme();

    private static MudTheme BuildTheme()
    {
        const string primaryHex = "#8852FC";

        return new MudTheme
        {
            LayoutProperties = new LayoutProperties
            {
                DefaultBorderRadius = "8px",
            },

            PaletteDark = new PaletteDark
            {
                Primary = primaryHex,
                PrimaryDarken = "#7652D6",
                PrimaryLighten = "#A46FFF",
                Secondary = "#38BDF8",
                SecondaryDarken = "#0284C7",
                Background = "#070A12",
                BackgroundGray = "#0B1020",
                Surface = "#111827",
                AppbarBackground = "#0B1020",
                DrawerBackground = "#0B1020",
                DrawerText = "#F5F7FB",
                DrawerIcon = "#B6C2D6",
                TextPrimary = "#F5F7FB",
                TextSecondary = "#B6C2D6",
                TextDisabled = "rgba(127,141,165,0.58)",
                ActionDefault = "#B6C2D6",
                LinesDefault = "rgba(148,163,184,0.16)",
                Divider = "rgba(148,163,184,0.10)",
                OverlayDark = "rgba(0,0,0,0.72)",
                Error = "#EF4444",
                Warning = "#F59E0B",
                Info = "#38BDF8",
                Success = "#22C55E",
            },

            Typography = new Typography
            {
                Default = new DefaultTypography
                {
                    FontFamily =
                    [
                        "Segoe UI Variable",
                        "Segoe UI",
                        "system-ui",
                        "-apple-system",
                        "BlinkMacSystemFont",
                        "Helvetica Neue",
                        "Arial",
                        "sans-serif",
                    ],
                    FontSize = "14px",
                    FontWeight = "400",
                    LineHeight = "1.5",
                },
                H1 = new H1Typography { FontWeight = "600", FontSize = "20px", LetterSpacing = "0", LineHeight = "1.3" },
                H2 = new H2Typography { FontWeight = "600", FontSize = "18px", LetterSpacing = "0", LineHeight = "1.35" },
                H3 = new H3Typography { FontWeight = "600", FontSize = "17px", LetterSpacing = "0", LineHeight = "1.35" },
                H4 = new H4Typography { FontWeight = "600", FontSize = "16px", LetterSpacing = "0", LineHeight = "1.4" },
                H5 = new H5Typography { FontWeight = "600", FontSize = "14px", LetterSpacing = "0", LineHeight = "1.4" },
                H6 = new H6Typography { FontWeight = "600", FontSize = "13px", LetterSpacing = "0", LineHeight = "1.4" },
                Body1 = new Body1Typography { FontWeight = "400", FontSize = "14px", LineHeight = "1.5" },
                Body2 = new Body2Typography { FontWeight = "400", FontSize = "13px", LineHeight = "1.5" },
                Subtitle1 = new Subtitle1Typography { FontWeight = "600", FontSize = "14px", LineHeight = "1.5" },
                Subtitle2 = new Subtitle2Typography { FontWeight = "600", FontSize = "13px", LineHeight = "1.45" },
                Button = new ButtonTypography { FontWeight = "600", FontSize = "14px", TextTransform = "none" },
                Caption = new CaptionTypography { FontWeight = "400", FontSize = "12px", LineHeight = "1.4" },
                Overline = new OverlineTypography { FontWeight = "600", FontSize = "11px", LetterSpacing = "0.02em", TextTransform = "uppercase" },
            },
        };
    }

    /// <summary>Multiplies each RGB channel by <paramref name="factor"/> to produce a darker shade.</summary>
    private static string DarkenHex(string hex, double factor = 0.72)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 3)
            {
                hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            }

            if (hex.Length != 6)
            {
                return $"#{hex}";
            }

            var r = Math.Clamp((int)(Convert.ToInt32(hex[..2], 16) * factor), 0, 255);
            var g = Math.Clamp((int)(Convert.ToInt32(hex[2..4], 16) * factor), 0, 255);
            var b = Math.Clamp((int)(Convert.ToInt32(hex[4..6], 16) * factor), 0, 255);
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        catch
        {
            return hex;
        }
    }

    /// <summary>Adds <paramref name="amount"/> to each RGB channel to produce a lighter shade.</summary>
    private static string LightenHex(string hex, int amount = 48)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 3)
            {
                hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            }

            if (hex.Length != 6)
            {
                return $"#{hex}";
            }

            var r = Math.Clamp(Convert.ToInt32(hex[..2], 16) + amount, 0, 255);
            var g = Math.Clamp(Convert.ToInt32(hex[2..4], 16) + amount, 0, 255);
            var b = Math.Clamp(Convert.ToInt32(hex[4..6], 16) + amount, 0, 255);
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        catch
        {
            return hex;
        }
    }
}
