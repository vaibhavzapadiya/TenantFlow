using FluentValidation;
using TenantFlow.BuildingBlocks.Validation;
using TenantFlow.Inventory.Application.Stock.Commands;
using TenantFlow.Inventory.Domain.Stock;

namespace TenantFlow.Inventory.Application.Stock.Validators;

/// <summary>
/// Reports the domain's rules as field errors (ADR-0014). It holds no rule of its own: each rule is a
/// <c>StockAdjustment.Check</c> method, so the API and <c>StockAdjustment.Record</c> can never disagree.
/// </summary>
public sealed class AdjustStockCommandValidator : AbstractValidator<AdjustStockCommand>
{
    public AdjustStockCommandValidator()
    {
        RuleFor(c => c.Delta).Custom((value, context) => DomainRuleValidation.Report(context, StockAdjustment.CheckDelta(value)));
        RuleFor(c => c.Reason).Custom((value, context) => DomainRuleValidation.Report(context, StockAdjustment.CheckReason(value)));
    }
}
