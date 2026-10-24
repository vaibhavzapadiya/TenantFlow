using System.Globalization;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Catalog.Application.Products.Commands;
using TenantFlow.Catalog.Application.Products.Validators;
using TenantFlow.Catalog.Domain.Products;

namespace TenantFlow.Catalog.Tests;

/// <summary>SPEC-0003 criterion 7: the product rules, in the domain, reported as field errors by the validator.</summary>
public sealed class ProductRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_valid_product_is_created_active_with_a_trimmed_sku_and_an_upper_case_currency()
    {
        var product = Product.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), " ABC-1 ", "Widget", 12.5m, "eur", Now);

        Assert.Equal(("ABC-1", "Widget", 12.5m, "EUR", ProductStatus.Active), (product.Sku, product.Name, product.PriceAmount, product.PriceCurrency, product.Status));
    }

    [Theory]
    [InlineData(null, "Widget", "1", "EUR", "catalog.sku.required")]
    [InlineData("A", " ", "1", "EUR", "catalog.name.required")]
    [InlineData("A", "Widget", "-1", "EUR", "catalog.price.amount_invalid")]
    [InlineData("A", "Widget", "1.00001", "EUR", "catalog.price.amount_invalid")]
    [InlineData("A", "Widget", "1000000000000000", "EUR", "catalog.price.amount_invalid")]
    [InlineData("A", "Widget", "1", "EURO", "catalog.price.currency_invalid")]
    public void An_invalid_product_is_rejected_by_the_domain_and_reported_by_the_validator(string? sku, string? name, string amount, string? currency, string expectedCode)
    {
        var price = decimal.Parse(amount, CultureInfo.InvariantCulture);

        var domain = Assert.Throws<AppException>(() => Product.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), sku, name, price, currency, Now));
        var failures = new CreateProductCommandValidator().Validate(new CreateProductCommand(sku, name, price, currency)).Errors;

        Assert.Equal(expectedCode, domain.Error.Code);
        Assert.Contains(failures, failure => failure.ErrorCode == expectedCode);
    }

    [Fact]
    public void A_missing_price_amount_is_invalid_not_zero()
    {
        var failures = new CreateProductCommandValidator().Validate(new CreateProductCommand("A", "Widget", null, "EUR")).Errors;

        Assert.Contains(failures, failure => failure.ErrorCode == "catalog.price.amount_invalid");
    }
}
