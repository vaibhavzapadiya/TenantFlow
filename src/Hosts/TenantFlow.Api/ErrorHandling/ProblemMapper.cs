using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.Api.ErrorHandling;

/// <summary>
/// Turns the exceptions that mean "the request is wrong" into Problem Details. This is the only
/// place that knows how <see cref="ErrorKind"/> becomes a status code (ADR-0016). Titles and details are
/// fixed sentences: exception messages and user input are never copied into a response.
/// </summary>
internal static class ProblemMapper
{
    private static readonly Regex CodePattern = new(AppError.CodePattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Code of a field error whose validator forgot to name one; never a validator type name.</summary>
    private const string InvalidValue = "request.invalid_value";

    public static bool TryCreate(Exception exception, out ProblemDetails problem)
    {
        switch (exception)
        {
            case AppException app:
                problem = Create(StatusOf(app.Error.Kind), app.Error.Code, app.Error.Message);
                return true;
            case ValidationException validation:
                problem = Create(StatusCodes.Status400BadRequest, HttpProblemCodes.ValidationFailed, "One or more fields are invalid.");
                problem.Extensions["errors"] = validation.Errors.Select(static failure => new FieldError(
                    FieldName(failure.PropertyName),
                    CodePattern.IsMatch(failure.ErrorCode ?? string.Empty) ? failure.ErrorCode! : InvalidValue,
                    failure.ErrorMessage)).ToArray();
                return true;
            case BadHttpRequestException badRequest:
                var (code, title) = HttpProblemCodes.For(badRequest.StatusCode);
                problem = Create(badRequest.StatusCode, code, title);
                return true;
            default:
                problem = null!;
                return false;
        }
    }

    private static ProblemDetails Create(int status, string code, string title)
    {
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        return problem;
    }

    private static int StatusOf(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        ErrorKind.UnprocessableContent => StatusCodes.Status422UnprocessableEntity,
        ErrorKind.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorKind.PayloadTooLarge => StatusCodes.Status413PayloadTooLarge,
        ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>"Address.Street" becomes "address.street", matching the camelCase JSON the client sent.</summary>
    private static string FieldName(string propertyName) =>
        string.Join('.', propertyName.Split('.').Select(static segment =>
            segment.Length == 0 ? segment : char.ToLowerInvariant(segment[0]) + segment[1..]));

    internal sealed record FieldError(string Field, string Code, string Message);
}
