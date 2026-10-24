using TenantFlow.Payments.Application.Payments.Commands;
using TenantFlow.Payments.Application.Payments.Validators;

namespace TenantFlow.Payments.Tests;

/// <summary>SPEC-0006 criteria 2 and 15: the idempotency key's shape, and no card number ever passes as a payment method.</summary>
public sealed class CreatePaymentCommandValidatorTests
{
    private static readonly CreatePaymentCommandValidator Validator = new();

    [Fact]
    public void A_provider_token_and_a_visible_key_pass()
    {
        var result = Validator.Validate(new CreatePaymentCommand(Guid.CreateVersion7(), "order-42/attempt-1", "pm_card_visa"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Card_numbers_and_malformed_keys_are_refused_with_their_codes()
    {
        var order = Guid.CreateVersion7();
        string?[] badMethods = ["4242424242424242", "pm_4242424242424242", "4242_4242_4242_4242", "pm_424242_424242", "4242 4242 4242 4242", "pm", null, new string('a', 101)];
        string?[] badKeys = [null, string.Empty, "has space", "tab\tkey", "é", new string('k', 256)];

        var methodCodes = badMethods.Select(m => Validator.Validate(new CreatePaymentCommand(order, "key", m)).Errors.Select(static e => e.ErrorCode).Single()).Distinct();
        var keyCodes = badKeys.Select(k => Validator.Validate(new CreatePaymentCommand(order, k, "sim_success")).Errors.Select(static e => e.ErrorCode).Single()).Distinct();

        Assert.Equal(["payments.payment_method.invalid"], methodCodes);
        Assert.Equal(["payments.idempotency_key.invalid"], keyCodes);
    }
}
