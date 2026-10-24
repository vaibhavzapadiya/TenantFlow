using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Inventory.Application.Stock.Commands;
using TenantFlow.Inventory.Application.Stock.Validators;
using TenantFlow.Inventory.Domain.Stock;

namespace TenantFlow.Inventory.Tests;

/// <summary>SPEC-0003 criterion 11: the adjustment rules, in the domain, reported as field errors by the validator.</summary>
public sealed class StockAdjustmentRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_valid_adjustment_keeps_its_signed_delta_and_a_trimmed_reason()
    {
        var adjustment = StockAdjustment.Record(Guid.CreateVersion7(), Guid.CreateVersion7(), -3, " Damaged ", Guid.CreateVersion7(), Now);

        Assert.Equal((-3, "Damaged"), (adjustment.Delta, adjustment.Reason));
    }

    [Theory]
    [InlineData(0, "Count", "inventory.delta.invalid")]
    [InlineData(null, "Count", "inventory.delta.invalid")]
    [InlineData(1_000_001, "Count", "inventory.delta.invalid")]
    [InlineData(5, " ", "inventory.reason.required")]
    public void An_invalid_adjustment_is_rejected_by_the_domain_and_reported_by_the_validator(int? delta, string reason, string expectedCode)
    {
        var domain = Assert.Throws<AppException>(() => StockAdjustment.Record(Guid.CreateVersion7(), Guid.CreateVersion7(), delta, reason, Guid.CreateVersion7(), Now));
        var failures = new AdjustStockCommandValidator().Validate(new AdjustStockCommand(delta, reason)).Errors;

        Assert.Equal(expectedCode, domain.Error.Code);
        Assert.Contains(failures, failure => failure.ErrorCode == expectedCode);
    }
}
