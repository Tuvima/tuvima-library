namespace MediaEngine.Web.Components.Shared;

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

public enum AppAdornment
{
    None = 0,
    Start = 1,
    End = 2,
}

public enum AppButtonType
{
    Button = 0,
    Submit = 1,
    Reset = 2,
}
