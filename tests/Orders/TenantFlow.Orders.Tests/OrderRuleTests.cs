using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Orders.Application.Orders.Commands;
using TenantFlow.Orders.Application.Orders.Validators;
using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Orders.Tests;

/// <summary>SPEC-0004 criteria 6 and 7: the snapshot and total, and the line rules as the validator reports them.</summary>
public sealed class OrderRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    internal static Order Draft(params OrderLineDraft[] lines) =>
        Order.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), lines.Length > 0 ? lines : [Line(2.5m, 3)], Now);

    private static OrderLineDraft Line(decimal price, int quantity, string currency = "EUR") =>
        new(Guid.CreateVersion7(), "SKU-1", "Widget", price, currency, quantity);

    [Fact]
    public void A_draft_snapshots_its_lines_and_computes_the_total_once()
    {
        var order = Draft(Line(2.5m, 3), Line(0.1234m, 10));

        Assert.Equal(OrderStatus.Draft, order.Status);
        Assert.Equal("EUR", order.Currency);
        Assert.Equal([7.5m, 1.234m], order.Lines.Select(static l => l.LineTotal));
        Assert.Equal([1, 2], order.Lines.Select(static l => l.LineNo));
        Assert.Equal(8.734m, order.TotalAmount);
    }

    [Fact]
    public void Lines_in_different_currencies_or_beyond_the_largest_total_are_refused()
    {
        var mixed = Assert.Throws<AppException>(() => Draft(Line(1m, 1, "EUR"), Line(1m, 1, "USD")));
        var huge = Assert.Throws<AppException>(() => Draft(Line(Order.MaxAmount, 2)));

        Assert.Equal(OrderErrors.MixedCurrency, mixed.Error);
        Assert.Equal(OrderErrors.TotalTooLarge, huge.Error);
    }

    [Fact]
    public void The_validator_reports_each_broken_line_rule_as_a_field_error()
    {
        var validator = new OrderLinesCommandValidator();
        var product = Guid.CreateVersion7();

        var none = validator.Validate(new OrderLinesCommand([]));
        var tooMany = validator.Validate(new OrderLinesCommand([.. Enumerable.Range(0, Order.MaxLines + 1).Select(_ => new OrderLineInput(Guid.CreateVersion7(), 1))]));
        var bad = validator.Validate(new OrderLinesCommand([new OrderLineInput(product, 1), new OrderLineInput(product, 0), new OrderLineInput(null, Order.MaxQuantity + 1)]));
        var good = validator.Validate(new OrderLinesCommand([new OrderLineInput(product, Order.MaxQuantity)]));

        Assert.Equal([OrderErrors.LinesRequired.Code], none.Errors.Select(static e => e.ErrorCode));
        Assert.Equal([OrderErrors.LinesTooMany.Code], tooMany.Errors.Select(static e => e.ErrorCode));
        Assert.Equal(
            [("Lines", OrderErrors.ProductDuplicated.Code), ("Lines[1].Quantity", OrderErrors.QuantityInvalid.Code), ("Lines[2].ProductId", OrderErrors.ProductIdRequired.Code), ("Lines[2].Quantity", OrderErrors.QuantityInvalid.Code)],
            bad.Errors.Select(static e => (e.PropertyName, e.ErrorCode)));
        Assert.True(good.IsValid);
    }
}
