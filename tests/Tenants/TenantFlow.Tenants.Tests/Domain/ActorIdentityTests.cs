using TenantFlow.BuildingBlocks.Abstractions.Identity;

namespace TenantFlow.Tenants.Tests.Domain;

/// <summary>
/// Who did it, as a value: issuer plus subject (ADR-0009). It cannot be blank or larger than the columns that
/// store it (pre-S5 review 2.10), and it never prints what it holds in an error.
/// </summary>
public sealed class ActorIdentityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Issuer_is_required(string? issuer)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ActorIdentity(issuer!, "operator-a"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Subject_is_required(string? subject)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ActorIdentity("https://idp.example", subject!));
    }

    [Fact]
    public void Values_at_exactly_the_stored_limits_are_accepted_and_one_more_is_not()
    {
        var atLimit = new ActorIdentity(new string('i', IdentityLimits.IssuerMaxLength), new string('s', IdentityLimits.SubjectMaxLength));

        Assert.Equal(IdentityLimits.IssuerMaxLength, atLimit.Issuer.Length);
        Assert.Equal(IdentityLimits.SubjectMaxLength, atLimit.Subject.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActorIdentity(new string('i', IdentityLimits.IssuerMaxLength + 1), "s"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActorIdentity("i", new string('s', IdentityLimits.SubjectMaxLength + 1)));
    }

    [Fact]
    public void An_error_never_contains_the_value_that_caused_it()
    {
        var oversized = "secret-" + new string('x', IdentityLimits.SubjectMaxLength);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new ActorIdentity("https://idp.example", oversized));

        Assert.DoesNotContain("secret-", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_actors_with_the_same_issuer_and_subject_are_equal()
    {
        Assert.Equal(new ActorIdentity("https://idp.example", "a"), new ActorIdentity("https://idp.example", "a"));
        Assert.NotEqual(new ActorIdentity("https://idp.example", "a"), new ActorIdentity("https://other.example", "a"));
    }
}
