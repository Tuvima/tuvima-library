// First-party presentation contracts. Member names preserve the migrated call-site semantics.
namespace MediaEngine.Web.Components.Shared;

public enum AppVariant
{
    Text = 0,
    Filled = 1,
    Outlined = 2,
}

public enum AppColor
{
    Default = 0,
    Primary = 1,
    Secondary = 2,
    Tertiary = 3,
    Info = 4,
    Success = 5,
    Warning = 6,
    Error = 7,
    Dark = 8,
    Transparent = 9,
    Inherit = 10,
    Surface = 11,
}

public enum AppSize
{
    Small = 0,
    Medium = 1,
    Large = 2,
}

public enum AppTypo
{
    inherit = 0,
    h1 = 1,
    h2 = 2,
    h3 = 3,
    h4 = 4,
    h5 = 5,
    h6 = 6,
    subtitle1 = 7,
    subtitle2 = 8,
    body1 = 9,
    body2 = 10,
    button = 11,
    caption = 12,
    overline = 13,
}

public enum AppAdornment
{
    None = 0,
    Start = 1,
    End = 2,
}

public enum AppInputType
{
    Text = 0,
    Password = 1,
    Email = 2,
    Hidden = 3,
    Number = 4,
    Search = 5,
    Telephone = 6,
    Url = 7,
    Color = 8,
    Date = 9,
    DateTimeLocal = 10,
    Month = 11,
    Time = 12,
    Week = 13,
}

public enum AppInputMode
{
    none = 0,
    text = 1,
    @decimal = 2,
    numeric = 3,
    tel = 4,
    search = 5,
    email = 6,
    url = 7,
}

public enum AppObjectFit { Fill, Contain, Cover, None, ScaleDown }

public enum AppMargin
{
    None = 0,
    Dense = 1,
    Normal = 2,
}

public enum AppButtonType
{
    Button = 0,
    Submit = 1,
    Reset = 2,
}

public enum AppPlacement
{
    Left = 0,
    Right = 1,
    End = 2,
    Start = 3,
    Top = 4,
    Bottom = 5,
}

public enum AppOrigin
{
    TopLeft = 0,
    TopCenter = 1,
    TopRight = 2,
    CenterLeft = 3,
    CenterCenter = 4,
    CenterRight = 5,
    BottomLeft = 6,
    BottomCenter = 7,
    BottomRight = 8,
}

public enum AppBreakpoint
{
    Xs = 0,
    Sm = 1,
    Md = 2,
    Lg = 3,
    Xl = 4,
    Xxl = 5,
    SmAndDown = 6,
    MdAndDown = 7,
    LgAndDown = 8,
    XlAndDown = 9,
    SmAndUp = 10,
    MdAndUp = 11,
    LgAndUp = 12,
    XlAndUp = 13,
    None = 14,
    Always = 15,
}

public enum AppMaxWidth
{
    Large = 0,
    Medium = 1,
    Small = 2,
    ExtraLarge = 3,
    ExtraExtraLarge = 4,
    ExtraSmall = 5,
    False = 6,
}

public enum AppAlignItems
{
    Baseline = 0,
    Center = 1,
    Start = 2,
    End = 3,
    Stretch = 4,
}

public enum AppJustify
{
    FlexStart = 0,
    Center = 1,
    FlexEnd = 2,
    SpaceBetween = 3,
    SpaceAround = 4,
    SpaceEvenly = 5,
}

public enum AppAlign
{
    Inherit = 0,
    Left = 1,
    Center = 2,
    Right = 3,
    Justify = 4,
    Start = 5,
    End = 6,
}

public enum AppPosition
{
    Bottom = 0,
    Center = 1,
    Top = 2,
    Left = 3,
    Right = 4,
    Start = 5,
    End = 6,
}

public enum AppAnchor
{
    Left = 0,
    Right = 1,
    Start = 2,
    End = 3,
    Top = 4,
    Bottom = 5,
}

public enum AppDrawerVariant
{
    Temporary = 0,
    Responsive = 1,
    Persistent = 2,
    Mini = 3,
}

public enum AppChartType
{
    Donut = 0,
    Line = 1,
    Pie = 2,
    Bar = 3,
    StackedBar = 4,
    Timeseries = 5,
    HeatMap = 6,
    Rose = 7,
    Radar = 8,
    Sankey = 9,
}

public enum AppUnderline
{
    None = 0,
    Hover = 1,
    Always = 2,
}

public enum AppDialogPosition
{
    Center = 0,
    CenterLeft = 1,
    CenterRight = 2,
    TopCenter = 3,
    TopLeft = 4,
    TopRight = 5,
    BottomCenter = 6,
    BottomLeft = 7,
    BottomRight = 8,
    Custom = 9,
}

public enum AppSeverity
{
    Normal = 0,
    Info = 1,
    Success = 2,
    Warning = 3,
    Error = 4,
}
