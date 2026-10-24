using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Tenants.Tests.Domain;

/// <summary>
/// One definition of a status's stable name (pre-S5 review 2.10). The database and the API both use it, and a
/// stored value nobody recognises is data corruption: it must fail loudly, never turn into some other status.
/// </summary>
public sealed class TenantStatusNamesTests
{
    [Theory]
    [InlineData(TenantStatus.Active, "active")]
    [InlineData(TenantStatus.Suspended, "suspended")]
    public void Every_status_has_a_stable_name_that_parses_back(TenantStatus status, string name)
    {
        Assert.Equal(name, status.ToName());
        Assert.Equal(status, TenantStatusNames.Parse(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Active")]
    [InlineData("SUSPENDED")]
    [InlineData("deleted")]
    [InlineData(" active")]
    public void An_unrecognised_name_is_refused_not_guessed(string name)
    {
        Assert.Throws<InvalidOperationException>(() => TenantStatusNames.Parse(name));
    }

    [Fact]
    public void A_status_outside_the_enumeration_has_no_name()
    {
        Assert.Throws<InvalidOperationException>(() => ((TenantStatus)99).ToName());
    }

    [Fact]
    public void Every_defined_status_is_covered_so_adding_one_cannot_be_forgotten()
    {
        foreach (var status in Enum.GetValues<TenantStatus>())
        {
            Assert.Equal(status, TenantStatusNames.Parse(status.ToName()));
        }
    }
}
