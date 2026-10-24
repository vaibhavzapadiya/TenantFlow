using FluentValidation;
using TenantFlow.BuildingBlocks.Validation;
using TenantFlow.Catalog.Application.Products.Commands;
using TenantFlow.Catalog.Domain.Products;

namespace TenantFlow.Catalog.Application.Products.Validators;

/// <summary>Reports the domain's rules for an edit as field errors (ADR-0014); the SKU is not editable.</summary>
public sealed class UpdateProductCommandValidator : AbstractValidator<UpdateProductCommand>
{
    public UpdateProductCommandValidator()
    {
        RuleFor(c => c.Name).Custom((value, context) => DomainRuleValidation.Report(context, Product.CheckName(value)));
        RuleFor(c => c.PriceAmount).Custom((value, context) => DomainRuleValidation.Report(context, Product.CheckPriceAmount(value)));
        RuleFor(c => c.PriceCurrency).Custom((value, context) => DomainRuleValidation.Report(context, Product.CheckPriceCurrency(value)));
        RuleFor(c => c.Status).Custom((value, context) => DomainRuleValidation.Report(context, Product.CheckStatus(value)));
    }
}
