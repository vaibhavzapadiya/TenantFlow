using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Text;

namespace TenantFlow.Inventory.Domain.Stock;

/// <summary>
/// One change to a product's stock: a signed delta, why, and which membership made it. Append-only: the application role
/// cannot update or delete these rows, and each is written in the same transaction as the balance change it explains.
/// The actor is a membership id, never a name or an email (AGENTS.md rule 6).
/// </summary>
public sealed class StockAdjustment
{
    public const int MaxDelta = 1_000_000;
    public const int ReasonMaxLength = 200;

    private StockAdjustment()
    {
    }

    public long Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProductId { get; private set; }

    public int Delta { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public Guid ActorMembershipId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public static AppError? CheckDelta(int? delta) =>
        delta is { } value && value != 0 && value >= -MaxDelta && value <= MaxDelta ? null : InventoryErrors.DeltaInvalid;

    public static AppError? CheckReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return InventoryErrors.ReasonRequired;
        }

        var trimmed = reason.Trim();
        if (trimmed.Length > ReasonMaxLength)
        {
            return InventoryErrors.ReasonTooLong;
        }

        return TextRules.ContainsDisallowedCharacters(trimmed) ? InventoryErrors.ReasonInvalidCharacters : null;
    }

    /// <exception cref="AppException">The delta or reason breaks a rule; see the <c>Check</c> methods.</exception>
    /// <exception cref="ArgumentException">An id is empty: a programming error, never a user mistake.</exception>
    public static StockAdjustment Record(Guid tenantId, Guid productId, int? delta, string? reason, Guid actorMembershipId, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || productId == Guid.Empty || actorMembershipId == Guid.Empty)
        {
            throw new ArgumentException("A stock adjustment needs a tenant, a product, and an actor.");
        }

        if ((CheckDelta(delta) ?? CheckReason(reason)) is { } error)
        {
            throw new AppException(error);
        }

        return new StockAdjustment
        {
            TenantId = tenantId,
            ProductId = productId,
            Delta = delta!.Value,
            Reason = reason!.Trim(),
            ActorMembershipId = actorMembershipId,
            OccurredAt = now,
        };
    }
}
