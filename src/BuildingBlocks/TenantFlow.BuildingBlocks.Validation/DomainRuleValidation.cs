using FluentValidation;
using FluentValidation.Results;
using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.BuildingBlocks.Validation;

/// <summary>
/// Reports a domain rule's error as a field error with the rule's own code and message (ADR-0014, ADR-0016). Validators
/// hold no rule of their own: they call a domain <c>Check</c> method and pass its answer here.
/// </summary>
public static class DomainRuleValidation
{
    /// <param name="context">The validation context of the property being checked.</param>
    /// <param name="error">The rule's answer; null means the value is valid and nothing is reported.</param>
    /// <param name="property">A member of the checked value to name in the path (for example a line's <c>Quantity</c>).</param>
    public static void Report<T>(ValidationContext<T> context, AppError? error, string? property = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (error is not null)
        {
            var path = property is null ? context.PropertyPath : $"{context.PropertyPath}.{property}";
            context.AddFailure(new ValidationFailure(path, error.Message) { ErrorCode = error.Code });
        }
    }
}
