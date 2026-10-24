using TenantFlow.Tenants.Application.Lifecycle.Commands;
using TenantFlow.Tenants.Application.Lifecycle.Validators;
using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Tenants.Tests.Lifecycle;

/// <summary>
/// The validator and the domain must never disagree (pre-S5 review 2.4): the name rule is written once, on
/// <see cref="Tenant.CheckName"/>, and the validator only reports it as a field error. These tests would
/// fail if someone adds a second, different rule to either side.
/// </summary>
public sealed class CreateTenantCommandValidatorTests
{
    private static readonly CreateTenantCommandValidator Validator = new();

    public static IEnumerable<object?[]> Names() =>
    [
        [null],
        [string.Empty],
        ["   "],
        ["Acme Ltd"],
        ["  Acme  "],
        ["株式会社テナント"],
        ["شرکت می‌خواهم"],
        ["Acme\nCorp"],
        ["Acme\0Corp"],
        ["Acme‮Corp"],
        ["Acme⁦Corp"],
        ["x"],
    ];

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Validator_agrees_with_the_domain_for_every_name(string? name)
    {
        var result = await Validator.ValidateAsync(new CreateTenantCommand(name), TestContext.Current.CancellationToken);

        var domainError = Tenant.CheckName(name);
        Assert.Equal(domainError is null, result.IsValid);
        if (domainError is not null)
        {
            // One error per field, with the catalog's code and message, never a validator type name.
            var failure = Assert.Single(result.Errors);
            Assert.Equal(domainError.Code, failure.ErrorCode);
            Assert.Equal(domainError.Message, failure.ErrorMessage);
            Assert.Equal(nameof(CreateTenantCommand.Name), failure.PropertyName);
        }
    }

    [Fact]
    public async Task Too_long_name_is_reported_once_and_with_its_own_code()
    {
        var result = await Validator.ValidateAsync(
            new CreateTenantCommand(new string('x', Tenant.NameMaxLength + 1)), TestContext.Current.CancellationToken);

        Assert.Equal("tenant.name.too_long", Assert.Single(result.Errors).ErrorCode);
    }
}
