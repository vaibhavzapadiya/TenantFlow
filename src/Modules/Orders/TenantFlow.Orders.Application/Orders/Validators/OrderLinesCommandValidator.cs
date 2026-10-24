using FluentValidation;
using TenantFlow.BuildingBlocks.Validation;
using TenantFlow.Orders.Application.Orders.Commands;
using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Orders.Application.Orders.Validators;

/// <summary>
/// Reports the domain's line rules as field errors (ADR-0014): <c>lines</c> for the list, <c>lines[i].productId</c> and
/// <c>lines[i].quantity</c> for each line. It holds no rule of its own: each is an <c>Order.Check</c> method.
/// </summary>
public sealed class OrderLinesCommandValidator : AbstractValidator<OrderLinesCommand>
{
    public OrderLinesCommandValidator()
    {
        RuleFor(c => c.Lines).Custom((lines, context) =>
            DomainRuleValidation.Report(context, Order.CheckLines(lines?.Select(static l => l?.ProductId).ToList())));
        RuleForEach(c => c.Lines).Custom((line, context) =>
        {
            DomainRuleValidation.Report(context, Order.CheckProductId(line?.ProductId), "ProductId");
            DomainRuleValidation.Report(context, Order.CheckQuantity(line?.Quantity), "Quantity");
        });
    }
}
