namespace MediaEngine.Domain.PersonalMedia;

/// <summary>Where a file in Library's Import or Rejected area is in its life (Photo and video filing spec D1).</summary>
public static class ViewIntakeStates
{
    /// <summary>In the Import area, waiting to be filed.</summary>
    public const string Waiting = "waiting";

    /// <summary>Being moved to its target right now. A row left in this state after a stop is finished or put back at the next start.</summary>
    public const string Filing = "filing";

    /// <summary>Filed into a library folder.</summary>
    public const string Filed = "filed";

    /// <summary>An exact copy of something already in the destination; it was not filed twice.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>Refused with a reason and held in the Rejected area until it expires.</summary>
    public const string Rejected = "rejected";

    /// <summary>A file found in the Rejected area that Library did not put there.</summary>
    public const string Stray = "stray";

    /// <summary>The states in which the file still sits in the Import or Rejected area.</summary>
    public static bool IsOpen(string state) => state is Waiting or Filing or Rejected or Stray;
}

/// <summary>Why a file was refused (Photo and video filing spec D6). The code is stored; the sentence is what a person reads.</summary>
public static class ViewIntakeReasons
{
    public const string Damaged = "damaged";
    public const string Empty = "empty";
    public const string UnsupportedType = "unsupported_type";
    public const string BadName = "bad_name";
    public const string NotEnoughSpace = "not_enough_space";
    public const string DestinationOffline = "destination_offline";
    public const string UploadIncomplete = "upload_incomplete";
    public const string DuplicateInTrash = "duplicate_in_trash";
    public const string Stray = "stray";
    public const string BeingTidied = "being_tidied";

    /// <summary>The plain sentence for a reason code.</summary>
    public static string Sentence(string code) => code switch
    {
        Damaged => "damaged or unreadable",
        Empty => "empty",
        UnsupportedType => "not a photo or video type Library reads",
        BadName => "a name Library can't store",
        NotEnoughSpace => "too big for the space left",
        DestinationOffline => "the library folder is offline",
        BeingTidied => "the library is being tidied; it will be filed when that finishes",
        UploadIncomplete => "the upload did not finish",
        DuplicateInTrash => "already in your library (in Trash)",
        Stray => "not put here by Library",
        _ => "could not be used",
    };
}

/// <summary>One file Library is tracking in the Import or Rejected area, or has just filed from it.</summary>
public sealed record ViewIntakeFile(
    Guid Id,
    string ContentKind,
    string ScopeKind,
    Guid? OwnerProfileId,
    Guid? AddedByProfileId,
    string OriginalName,
    string CurrentPath,
    string? TargetPath,
    string? FiledPath,
    string? GroupKey,
    string? GroupRole,
    long ByteSize,
    string? ContentHash,
    string State,
    string? ReasonCode,
    string? ReasonText,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>A group of exact copies the duplicates job found inside one space or the Shared Library (Photo and video filing spec D16).</summary>
public sealed record ViewDuplicateGroup(
    Guid Id,
    string ScopeKind,
    Guid LibraryId,
    string ContentHash,
    long CopyCount,
    string State,
    DateTimeOffset FirstFoundAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? DismissedAt,
    Guid? DismissedBy);

/// <summary>A space or the Shared Library that holds more than one item with exactly the same contents.</summary>
public sealed record ViewDuplicateCandidate(string ScopeKind, Guid LibraryId, string ContentHash, long Copies);

/// <summary>One copy inside a duplicate group.</summary>
public sealed record ViewDuplicateCopy(Guid ItemId, string FileName, string? CapturedAt, string CreatedAt, long ByteSize);

/// <summary>The states of a Tidy this folder run.</summary>
public static class ViewTidyStates
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Planned = "planned";
    public const string Moved = "moved";
}

/// <summary>One Tidy this folder run on one Managed folder (Photo and video filing spec D17).</summary>
public sealed record ViewTidyJob(
    Guid Id,
    Guid SourceId,
    string TriggerKind,
    string State,
    long TotalFiles,
    long MovedFiles,
    long FailedFiles,
    string? Message,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>One planned or finished rename in a Tidy run: the ledger that lets a stopped run resume.</summary>
public sealed record ViewTidyMove(Guid JobId, Guid SourceFileId, string FromPath, string ToPath, string State);

/// <summary>A file in a folder with the day it was taken, as Tidy needs to plan its place.</summary>
public sealed record ViewTidySourceFile(Guid SourceFileId, string FilePath, string? TakenAt);
