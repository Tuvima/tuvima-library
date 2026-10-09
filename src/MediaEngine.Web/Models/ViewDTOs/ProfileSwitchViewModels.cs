namespace MediaEngine.Web.Models.ViewDTOs;

public enum ProfileSwitchStatus
{
    Succeeded,
    PinRequired,
    Forbidden,
    NotFound,
    TooManyAttempts,
    Failed,
}

public sealed record ProfileSwitchOutcome(
    ProfileSwitchStatus Status,
    ProfileViewModel? Profile = null)
{
    public bool Succeeded => Status == ProfileSwitchStatus.Succeeded && Profile is not null;
}
