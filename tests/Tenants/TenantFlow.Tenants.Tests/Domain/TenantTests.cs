using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Tenants.Tests.Domain;

/// <summary>
/// The tenant's own invariants (pre-S5 review 2.4, 2.10): no tenant can exist with an invalid name, whoever
/// creates it, and the lifecycle only moves along its legal transitions. No host and no database.
/// </summary>
public sealed class TenantTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string?, string> InvalidNames => new()
    {
        { null, "tenant.name.required" },
        { string.Empty, "tenant.name.required" },
        { "   ", "tenant.name.required" },
        { "\t\r\n", "tenant.name.required" },
        { new string('x', 201), "tenant.name.too_long" },
        { "   " + new string('x', 201) + "   ", "tenant.name.too_long" },
        { "Acme\0Corp", "tenant.name.invalid_characters" },
        { "Acme\nCorp", "tenant.name.invalid_characters" },
        { "Acme\tCorp", "tenant.name.invalid_characters" },
        { "Acme‮Corp", "tenant.name.invalid_characters" },
        { "Acme‪Corp", "tenant.name.invalid_characters" },
        { "Acme⁦Corp", "tenant.name.invalid_characters" },
        { "Acme⁩Corp", "tenant.name.invalid_characters" },
    };

    // Real names: accents, other scripts, apostrophes, and the joiners that Persian and emoji need.
    public static TheoryData<string> ValidNames => new()
    {
        "Acme Ltd",
        "  Acme  ",
        "Ünïcödé Café",
        "株式会社テナント",
        "O'Brien & Sons",
        "شرکت می‌خواهم",
        "Family \U0001F468‍\U0001F469‍\U0001F467",
        "x",
    };

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void CheckName_reports_the_first_broken_rule(string? name, string expectedCode)
    {
        Assert.Equal(expectedCode, Tenant.CheckName(name)?.Code);
    }

    [Theory]
    [MemberData(nameof(ValidNames))]
    public void CheckName_accepts_real_world_names(string name)
    {
        Assert.Null(Tenant.CheckName(name));
    }

    [Fact]
    public void CheckName_accepts_exactly_the_maximum_length_after_trimming()
    {
        Assert.Null(Tenant.CheckName("  " + new string('x', Tenant.NameMaxLength) + "  "));
        Assert.Equal("tenant.name.too_long", Tenant.CheckName(new string('x', Tenant.NameMaxLength + 1))?.Code);
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void Create_refuses_an_invalid_name_whoever_calls_it(string? name, string expectedCode)
    {
        var exception = Assert.Throws<AppException>(() => Tenant.Create(Guid.NewGuid(), name, Now));

        Assert.Equal(expectedCode, exception.Error.Code);
        Assert.Equal(ErrorKind.Validation, exception.Error.Kind);
    }

    [Fact]
    public void Create_refuses_an_empty_id()
    {
        // An empty id is a programming error, not a user mistake: it is not an AppException and is never a 400.
        Assert.Throws<ArgumentException>(() => Tenant.Create(Guid.Empty, "Acme", Now));
    }

    [Fact]
    public void Create_makes_an_active_tenant_with_a_trimmed_name()
    {
        var id = Guid.NewGuid();

        var tenant = Tenant.Create(id, "  Acme Ltd  ", Now);

        Assert.Equal(id, tenant.Id);
        Assert.Equal("Acme Ltd", tenant.Name);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(Now, tenant.CreatedAt);
        Assert.Equal(Now, tenant.UpdatedAt);
    }

    [Fact]
    public void Suspend_moves_an_active_tenant_to_suspended_and_stamps_the_time()
    {
        var tenant = Tenant.Create(Guid.NewGuid(), "Acme", Now);
        var later = Now.AddMinutes(5);

        tenant.Suspend(later);

        Assert.Equal(TenantStatus.Suspended, tenant.Status);
        Assert.Equal(later, tenant.UpdatedAt);
        Assert.Equal(Now, tenant.CreatedAt);
    }

    [Fact]
    public void Suspending_a_suspended_tenant_is_refused_and_changes_nothing()
    {
        var tenant = Tenant.Create(Guid.NewGuid(), "Acme", Now);
        tenant.Suspend(Now.AddMinutes(1));

        var exception = Assert.Throws<AppException>(() => tenant.Suspend(Now.AddMinutes(2)));

        Assert.Equal("tenant.already_suspended", exception.Error.Code);
        Assert.Equal(Now.AddMinutes(1), tenant.UpdatedAt);
    }

    [Fact]
    public void Reactivate_moves_a_suspended_tenant_back_to_active()
    {
        var tenant = Tenant.Create(Guid.NewGuid(), "Acme", Now);
        tenant.Suspend(Now.AddMinutes(1));

        tenant.Reactivate(Now.AddMinutes(2));

        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(Now.AddMinutes(2), tenant.UpdatedAt);
    }

    [Fact]
    public void Reactivating_an_active_tenant_is_refused_and_changes_nothing()
    {
        var tenant = Tenant.Create(Guid.NewGuid(), "Acme", Now);

        var exception = Assert.Throws<AppException>(() => tenant.Reactivate(Now.AddMinutes(1)));

        Assert.Equal("tenant.not_suspended", exception.Error.Code);
        Assert.Equal(Now, tenant.UpdatedAt);
    }
}
