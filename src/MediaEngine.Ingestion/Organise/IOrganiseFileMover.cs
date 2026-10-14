namespace MediaEngine.Ingestion.Organise;

/// <summary>
/// Renames one file inside a folder Tuvima manages. It is a seam so the organising job can be tested with a file that is
/// open for playback, or a process that stops right after a file moved, without relying on how the operating system
/// treats open files.
/// </summary>
public interface IOrganiseFileMover
{
    /// <summary>
    /// Moves <paramref name="from"/> to <paramref name="to"/> on the same drive and never overwrites. Throws
    /// <see cref="IOException"/> when the file is in use or <paramref name="to"/> already exists.
    /// </summary>
    void Move(string from, string to);

    /// <summary>
    /// True when both paths lead to one file on disk (a letter-case-only name on a drive that ignores case, a hard link, a
    /// folder reached two ways), or when that cannot be told. Two names for one file are never a duplicate of each other.
    /// </summary>
    bool IsSameFile(string first, string second) => FileIdentity.AreSameFile(first, second);
}

/// <summary>The real thing: a plain rename that refuses to replace an existing file.</summary>
public sealed class SystemOrganiseFileMover : IOrganiseFileMover
{
    public void Move(string from, string to) => File.Move(from, to, overwrite: false);
}
