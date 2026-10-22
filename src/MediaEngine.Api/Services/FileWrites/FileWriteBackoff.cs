namespace MediaEngine.Api.Services.FileWrites;

/// <summary>
/// How long a move that failed waits before it is tried again, so a file briefly open in a player is not given up on within seconds
/// (batched write-back D10): a minute, five minutes, half an hour, two hours, and then the move is given up.
/// </summary>
internal static class FileWriteBackoff
{
    private static readonly TimeSpan[] Waits =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
    ];

    /// <summary>The longest wait.</summary>
    public static TimeSpan Longest => Waits[^1];

    /// <summary>The wait after the <paramref name="attempt"/>th try (1 is the first) failed, or null when it is time to give up.</summary>
    public static TimeSpan? After(int attempt) => attempt >= 1 && attempt <= Waits.Length ? Waits[attempt - 1] : null;
}
