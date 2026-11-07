namespace MediaEngine.Web.Components.Shared;

/// <summary>Whether a dialog presents as a bottom sheet instead of a centred dialog.</summary>
public enum AppDialogSheetMode
{
    Never = 0,
    Phone = 1,
    PhoneAndTablet = 2,
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
