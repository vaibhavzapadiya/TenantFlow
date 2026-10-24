using TenantFlow.Catalog.Application.Products.Services;

namespace TenantFlow.Catalog.Tests;

/// <summary>SPEC-0008 criterion 1: typed text becomes a full-text query of words, the last a prefix, with no operators.</summary>
public sealed class ProductSearchTests
{
    [Fact]
    public void Words_are_joined_the_last_one_is_a_prefix_and_operators_are_dropped()
    {
        Assert.Equal("blue & mu:*", ProductSearch.ToQuery("  Blue   MU "));
        Assert.Equal("mug & 12:*", ProductSearch.ToQuery("mug-12"));
        // A one-character last word is a whole word, not a prefix of nearly everything.
        Assert.Equal("mug & 1", ProductSearch.ToQuery("mug-1"));
        Assert.Equal("a & b", ProductSearch.ToQuery("a' | !b & ("));
        Assert.Null(ProductSearch.ToQuery("'|!&"));
    }
}
