namespace MediaEngine.Identity;

/// <summary>
/// Signals that a profile's PIN is locked after too many wrong attempts from outside the home.
/// Derives from <see cref="UnauthorizedAccessException"/> like <see cref="ProfilePinRequiredException"/>; callers
/// that care catch it first.
/// </summary>
public sealed class ProfilePinLockedException : UnauthorizedAccessException
{
    public ProfilePinLockedException()
        : base("Too many attempts. Try again later.")
    {
    }
}
