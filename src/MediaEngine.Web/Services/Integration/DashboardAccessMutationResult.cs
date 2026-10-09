using System.Net;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// A safe, UI-facing result for Access mutations. It deliberately retains only
/// the failure class and validation field names: Engine error bodies can contain
/// submitted values such as PINs or credentials and must never be replayed.
/// </summary>
public sealed record DashboardAccessMutationResult(
    DashboardAccessMutationFailure Failure = DashboardAccessMutationFailure.None,
    HttpStatusCode? StatusCode = null,
    IReadOnlyList<string>? ValidationFields = null)
{
    public bool Succeeded => Failure == DashboardAccessMutationFailure.None;

    public static DashboardAccessMutationResult Success() => new();

    public static DashboardAccessMutationResult FailureResult(
        DashboardAccessMutationFailure failure,
        HttpStatusCode? statusCode = null,
        IReadOnlyList<string>? validationFields = null) =>
        new(failure, statusCode, validationFields ?? []);
}

public sealed record DashboardAccessMutationResult<T>(
    T? Value = default,
    DashboardAccessMutationFailure Failure = DashboardAccessMutationFailure.None,
    HttpStatusCode? StatusCode = null,
    IReadOnlyList<string>? ValidationFields = null)
{
    public bool Succeeded => Failure == DashboardAccessMutationFailure.None;

    public static DashboardAccessMutationResult<T> Success(T value) => new(value);

    public static DashboardAccessMutationResult<T> FailureResult(
        DashboardAccessMutationFailure failure,
        HttpStatusCode? statusCode = null,
        IReadOnlyList<string>? validationFields = null) =>
        new(default, failure, statusCode, validationFields ?? []);
}

public enum DashboardAccessMutationFailure
{
    None,
    Validation,
    Unauthorized,
    Forbidden,
    /// <summary>The Engine wants the person to prove it is them first (403 <c>confirm_its_you</c>).</summary>
    ConfirmItsYou,
    Conflict,
    NotFound,
    Transient,
    InvalidResponse,
    Failed,
}
