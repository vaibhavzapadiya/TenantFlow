using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Text;
using TenantFlow.Catalog.Contracts;

namespace TenantFlow.Catalog.Domain.Products;

/// <summary>
/// A sellable item owned by one tenant (SPEC-0003). The SKU never changes after creation. The rules below are the one
/// definition of a valid product: the validator reports them as field errors, and <see cref="Create"/> and
/// <see cref="Change"/> enforce them, so no caller can bypass them. Money is an amount plus an ISO 4217 currency.
/// </summary>
public sealed class Product
{
    public const int SkuMaxLength = ProductLimits.SkuMaxLength;
    public const int NameMaxLength = ProductLimits.NameMaxLength;
    public const int PriceScale = 4;

    /// <summary>The largest amount the price column (numeric(19,4)) holds: 15 integer digits and 4 decimals.</summary>
    public const decimal MaxPriceAmount = 999_999_999_999_999.9999m;

    private Product()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public decimal PriceAmount { get; private set; }

    /// <summary>ISO 4217 alphabetic code, upper case.</summary>
    public string PriceCurrency { get; private set; } = string.Empty;

    public ProductStatus Status { get; private set; } = ProductStatus.Active;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static AppError? CheckSku(string? sku)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return ProductErrors.SkuRequired;
        }

        var trimmed = sku.Trim();
        if (trimmed.Length > SkuMaxLength)
        {
            return ProductErrors.SkuTooLong;
        }

        return TextRules.ContainsDisallowedCharacters(trimmed) ? ProductErrors.SkuInvalidCharacters : null;
    }

    public static AppError? CheckName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ProductErrors.NameRequired;
        }

        var trimmed = name.Trim();
        if (trimmed.Length > NameMaxLength)
        {
            return ProductErrors.NameTooLong;
        }

        return TextRules.ContainsDisallowedCharacters(trimmed) ? ProductErrors.NameInvalidCharacters : null;
    }

    public static AppError? CheckPriceAmount(decimal? amount) =>
        amount is { } value && value >= 0 && value <= MaxPriceAmount && ScaleOf(value) <= PriceScale ? null : ProductErrors.PriceAmountInvalid;

    public static AppError? CheckPriceCurrency(string? currency) =>
        currency is { Length: 3 } && currency.All(static c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') ? null : ProductErrors.PriceCurrencyInvalid;

    public static AppError? CheckStatus(string? status) =>
        ProductStatusNames.TryParse(status, out _) ? null : ProductErrors.StatusInvalid;

    /// <summary>Creates an active product.</summary>
    /// <exception cref="AppException">An input breaks a rule; see the <c>Check</c> methods.</exception>
    /// <exception cref="ArgumentException">An id is empty: a programming error, never a user mistake.</exception>
    public static Product Create(Guid id, Guid tenantId, string? sku, string? name, decimal? priceAmount, string? priceCurrency, DateTimeOffset now)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("A product needs an id and a tenant id.");
        }

        Enforce(CheckSku(sku) ?? CheckName(name) ?? CheckPriceAmount(priceAmount) ?? CheckPriceCurrency(priceCurrency));
        return new Product
        {
            Id = id,
            TenantId = tenantId,
            Sku = sku!.Trim(),
            Name = name!.Trim(),
            PriceAmount = priceAmount!.Value,
            PriceCurrency = priceCurrency!.ToUpperInvariant(),
            Status = ProductStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Replaces everything but the SKU.</summary>
    /// <exception cref="AppException">An input breaks a rule; see the <c>Check</c> methods.</exception>
    public void Change(string? name, decimal? priceAmount, string? priceCurrency, string? status, DateTimeOffset now)
    {
        Enforce(CheckName(name) ?? CheckPriceAmount(priceAmount) ?? CheckPriceCurrency(priceCurrency) ?? CheckStatus(status));
        Name = name!.Trim();
        PriceAmount = priceAmount!.Value;
        PriceCurrency = priceCurrency!.ToUpperInvariant();
        Status = ProductStatusNames.Parse(status!);
        UpdatedAt = now;
    }

    private static void Enforce(AppError? error)
    {
        if (error is not null)
        {
            throw new AppException(error);
        }
    }

    private static int ScaleOf(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;
}
