namespace TenantFlow.BuildingBlocks.Abstractions.Errors;

/// <summary>
/// Thrown by Domain and Application code for an expected failure (a rule was broken, a resource is
/// missing, a state conflicts). It is a normal outcome, not an incident: the API turns it into a Problem
/// Details response and does not log it as an error. Anything else that escapes is an unexpected fault.
/// </summary>
public sealed class AppException : Exception
{
    public AppException(AppError error, Exception? innerException = null)
        : base(error?.Message, innerException)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public AppError Error { get; }
}
