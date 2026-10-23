using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>The people who would lose sight of the files, or the plain reason the question was refused.</summary>
public sealed record VisibilityAfterMoveResult(IReadOnlyList<VisibilityLossDto>? People, string? Refusal)
{
    public static VisibilityAfterMoveResult Refused(string reason) => new(null, reason);
}

/// <summary>
/// "If these files move into library L, which people can see them now but would not in L?" (simpler access D13). For every account that is
/// not an administrator, a file is seen now when <see cref="LibraryVisibilityRule"/> allows the library it is in, and after the move when
/// the rule allows L; the files seen now and not after are counted. Content limits and Vault mode do not change with a move, so they are
/// not part of the answer. Counts are files (media assets), not titles. The per-library file counts come from one grouped read; the
/// per-account facts are one feature read and one hidden-libraries read each. The caller must already be an effective administrator
/// (the endpoint's policy): this service answers about everyone.
/// </summary>
public interface IVisibilityAfterMoveReadService
{
    Task<VisibilityAfterMoveResult> GetAsync(VisibilityAfterMoveRequest request, CancellationToken ct = default);
}
