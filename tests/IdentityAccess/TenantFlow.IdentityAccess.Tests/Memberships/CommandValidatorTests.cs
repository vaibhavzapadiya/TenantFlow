using FluentValidation.Results;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Application.Memberships.Commands;
using TenantFlow.IdentityAccess.Application.Memberships.Validators;

namespace TenantFlow.IdentityAccess.Tests.Memberships;

/// <summary>ADR-0014: the validators report the domain's rules as field errors with stable codes, and echo no input.</summary>
public sealed class CommandValidatorTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private const string Issuer = "https://idp.test/realms/tenantflow";

    private static ValidationResult Add(string? issuer = Issuer, string? subject = "user-1", string? role = "customer") =>
        new AddMemberCommandValidator().Validate(new AddMemberCommand(Tenant, issuer, subject, role));

    [Fact]
    public void A_complete_command_is_valid()
    {
        Assert.True(Add().IsValid);
        Assert.True(Add(role: "tenant-admin").IsValid);
        Assert.True(new ChangeRoleCommandValidator().Validate(new ChangeRoleCommand(Tenant, Guid.CreateVersion7(), "customer")).IsValid);
    }

    [Theory]
    [InlineData(null, "membership.issuer.invalid")]
    [InlineData("", "membership.issuer.invalid")]
    [InlineData("   ", "membership.issuer.invalid")]
    public void A_missing_issuer_is_reported_on_the_issuer_field(string? issuer, string code)
    {
        var failure = Assert.Single(Add(issuer: issuer).Errors);

        Assert.Equal(code, failure.ErrorCode);
        Assert.Equal("Issuer", failure.PropertyName);
    }

    [Fact]
    public void An_issuer_larger_than_the_stored_column_is_refused()
    {
        Assert.True(Add(issuer: new string('i', IdentityLimits.IssuerMaxLength)).IsValid);
        Assert.Equal("membership.issuer.invalid", Assert.Single(Add(issuer: new string('i', IdentityLimits.IssuerMaxLength + 1)).Errors).ErrorCode);
    }

    [Theory]
    [InlineData(null, "membership.subject.required")]
    [InlineData("  ", "membership.subject.required")]
    [InlineData("a\rb", "membership.subject.invalid_characters")]
    public void The_subject_rule_comes_from_the_domain(string? subject, string code)
    {
        var failure = Assert.Single(Add(subject: subject).Errors);

        Assert.Equal(code, failure.ErrorCode);
        Assert.Equal("Subject", failure.PropertyName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admin")]
    [InlineData("Customer")]
    public void An_unknown_role_is_reported_on_the_role_field_for_both_commands(string? role)
    {
        var add = Assert.Single(Add(role: role).Errors);
        var change = Assert.Single(new ChangeRoleCommandValidator().Validate(new ChangeRoleCommand(Tenant, Guid.CreateVersion7(), role)).Errors);

        Assert.Equal("membership.role.invalid", add.ErrorCode);
        Assert.Equal("membership.role.invalid", change.ErrorCode);
        Assert.Equal("Role", add.PropertyName);
    }

    [Fact]
    public void Messages_never_echo_the_input()
    {
        var result = Add(issuer: "secret-issuer", subject: "secret\nsubject", role: "secret-role");

        Assert.All(result.Errors, failure =>
        {
            Assert.DoesNotContain("secret", failure.ErrorMessage, StringComparison.Ordinal);
        });
        Assert.Equal(2, result.Errors.Count);
    }
}
