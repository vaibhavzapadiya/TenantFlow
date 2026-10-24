using FluentValidation;
using TenantFlow.BuildingBlocks.Validation;
using TenantFlow.Catalog.Application.Products.Commands;
using TenantFlow.Catalog.Domain.Products;

namespace TenantFlow.Catalog.Application.Products.Validators;

/// <summary>
/// Reports the domain's rules as field errors (ADR-0014). It holds no rule of its own: each rule is a
/// <c>Product.Check</c> method, so the API and <c>Product.Create</c> can never disagree.
/// </summary>
public sealed class CreateProductCommandValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductCommandValidator()
    {
        RuleFor(c => c.Sku).Custom((value, context) => DomainRuleValidation.Report(context, Product.CheckSku(value)));
        RuleFor(c => c.Name).Custom((value, context) => DomainRuleValidation.Report(context, Product.CheckName(value)));
        RuleFor(c => c.PriceAmount).Custom((value, context) => DomainRuleValidation.Report(context, Product.CheckPriceAmount(value)));
        RuleFor(c => c.PriceCurrency).Custom((value, context) => DomainRuleValidation.Report(context, Product.CheckPriceCurrency(value)));
    }
}
