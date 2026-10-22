namespace MediaEngine.Api.Services.Backups;

/// <summary>A user export or import that cannot go ahead because of something real (a clash, a missing email). The message is a plain sentence the administrator can be shown.</summary>
public sealed class UserTransferRefusedException : Exception
{
    /// <param name="message">The plain sentence shown to the administrator.</param>
    public UserTransferRefusedException(string message)
        : base(message)
    {
    }

    /// <param name="message">The plain sentence shown to the administrator.</param>
    /// <param name="innerException">What refused.</param>
    public UserTransferRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
