namespace MediaEngine.Api.Http;

/// <summary>
/// Single source of truth for handled endpoint error responses.
///
/// Every handled endpoint error MUST go through <c>ApiErrors</c> so the wire shape is
/// uniformly <c>application/problem+json</c> (RFC 7807), instead of the six incompatible ad-hoc
/// shapes that predate this class (<c>Results.BadRequest(new { error })</c>,
/// <c>Results.NotFound(new { error })</c>, <c>Results.Conflict(new { error })</c>,
/// <c>new { message }</c>, bare string bodies, and bare <c>Results.NotFound()</c> /
/// <c>Results.BadRequest()</c>).
///
/// <para>
/// <b>traceId flows automatically.</b> <c>Program.cs</c> calls
/// <c>builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ...)</c>,
/// which stamps <c>ProblemDetails.Extensions["traceId"]</c> onto every problem/validation-problem
/// result produced by <c>Results.Problem(...)</c> and <c>Results.ValidationProblem(...)</c> before
/// it is written to the response. Because every helper below returns one of those two result
/// types, callers do not need to add <c>traceId</c> themselves — it is populated centrally, once,
/// for every handled error produced through this class.
/// </para>
/// </summary>
public static class ApiErrors
{
    /// <summary>404 Not Found as an RFC 7807 problem body.</summary>
    public static IResult NotFound(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found.");

    /// <summary>400 Bad Request as an RFC 7807 problem body.</summary>
    public static IResult BadRequest(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid request.");

    /// <summary>409 Conflict as an RFC 7807 problem body.</summary>
    public static IResult Conflict(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict.");

    /// <summary>
    /// 409 Conflict that also carries a stable machine-readable <c>code</c> (for example
    /// <c>secure_account_first</c>) next to the human <c>detail</c>, so the Dashboard can react without parsing text.
    /// </summary>
    public static IResult Conflict(string code, string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict.",
            extensions: new Dictionary<string, object?> { ["code"] = code });

    /// <summary>422 Unprocessable Entity as an RFC 7807 problem body.</summary>
    public static IResult Unprocessable(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Unprocessable request.");

    /// <summary>403 Forbidden as an RFC 7807 problem body.</summary>
    public static IResult Forbidden(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status403Forbidden,
            title: "Access denied.");

    /// <summary>
    /// 403 Forbidden that also carries a stable machine-readable <c>code</c> (for example <c>confirm_its_you</c>)
    /// next to the human <c>detail</c>, so the Dashboard can react without parsing text.
    /// </summary>
    public static IResult Forbidden(string code, string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status403Forbidden,
            title: "Access denied.",
            extensions: new Dictionary<string, object?> { ["code"] = code });

    /// <summary>
    /// 400 Bad Request field-validation failure, rendered as an RFC 7807
    /// <c>application/problem+json</c> body with a per-field <c>errors</c> map (the
    /// <c>Results.ValidationProblem</c> shape), not a plain <c>BadRequest</c>.
    /// </summary>
    public static IResult Validation(IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors);

    /// <summary>
    /// General escape hatch for handled errors that do not fit the named helpers above —
    /// e.g. a non-standard status code, or a case that needs a custom title. Still an RFC 7807
    /// <c>application/problem+json</c> body with the same automatic <c>traceId</c> flow.
    /// </summary>
    public static IResult Problem(int statusCode, string title, string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: statusCode,
            title: title);
}
