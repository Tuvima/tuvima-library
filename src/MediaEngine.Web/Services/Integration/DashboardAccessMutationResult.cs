using System.Net;
using MediaEngine.Contracts.Approvals;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// A safe, UI-facing result for Access mutations. It deliberately retains only
/// the failure class and validation field names: Engine error bodies can contain
/// submitted values such as PINs or credentials and must never be replayed.
/// </summary>
/// <param name="Message">
/// The Engine's own plain-language reason, kept only by calls that opt in (the Households actions, whose refusals are
/// fixed sentences with no submitted values). Null everywhere else.
/// </param>
public sealed record DashboardAccessMutationResult(
    DashboardAccessMutationFailure Failure = DashboardAccessMutationFailure.None,
    HttpStatusCode? StatusCode = null,
    IReadOnlyList<string>? ValidationFields = null,
    string? Message = null,
    ApprovalRequiredResponse? Approval = null)
{
    public bool Succeeded => Failure == DashboardAccessMutationFailure.None;

    public static DashboardAccessMutationResult Success() => new();

    public static DashboardAccessMutationResult FailureResult(
        DashboardAccessMutationFailure failure,
        HttpStatusCode? statusCode = null,
        IReadOnlyList<string>? validationFields = null,
        string? message = null,
        ApprovalRequiredResponse? approval = null) =>
        new(failure, statusCode, validationFields ?? [], message, approval);
}

public sealed record DashboardAccessMutationResult<T>(
    T? Value = default,
    DashboardAccessMutationFailure Failure = DashboardAccessMutationFailure.None,
    HttpStatusCode? StatusCode = null,
    IReadOnlyList<string>? ValidationFields = null,
    string? Message = null,
    ApprovalRequiredResponse? Approval = null)
{
    public bool Succeeded => Failure == DashboardAccessMutationFailure.None;

    public static DashboardAccessMutationResult<T> Success(T value) => new(value);

    public static DashboardAccessMutationResult<T> FailureResult(
        DashboardAccessMutationFailure failure,
        HttpStatusCode? statusCode = null,
        IReadOnlyList<string>? validationFields = null,
        string? message = null,
        ApprovalRequiredResponse? approval = null) =>
        new(default, failure, statusCode, validationFields ?? [], message, approval);
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
    /// <summary>The Engine wants the person to see what this will do first (409 <c>approval_required</c>); see <c>Approval</c>.</summary>
    ApprovalRequired,
    NotFound,
    Transient,
    InvalidResponse,
    Failed,
}
