using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Text;

namespace TenantFlow.Orders.Domain.Orders;

/// <summary>
/// A customer's order in one tenant (SPEC-0004). It belongs to the membership that created it. Its lines snapshot the
/// catalog, its money is a decimal amount in one currency (AGENTS.md rule 4), and its total is computed here, once, from
/// the lines. Which change is allowed in which state is <see cref="OrderStateMachine"/>'s answer, never a local check.
/// The <c>Check</c> methods are the one definition of valid lines: the validator reports them as field errors and
/// <see cref="Create"/> and <see cref="ReplaceLines"/> enforce them.
/// </summary>
public sealed class Order
{
    public const int MaxLines = 50;
    public const int MaxQuantity = 10_000;

    /// <summary>The largest amount a <c>numeric(19,4)</c> column holds; a larger total is refused, not overflowed.</summary>
    public const decimal MaxAmount = 999_999_999_999_999.9999m;

    private readonly List<OrderLine> _lines = [];

    private Order()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>The membership that owns the order: who may edit and submit it.</summary>
    public Guid CustomerMembershipId { get; private set; }

    public OrderStatus Status { get; private set; }

    /// <summary>ISO 4217 code of every line's price and of the total.</summary>
    public string Currency { get; private set; } = string.Empty;

    public decimal TotalAmount { get; private set; }

    /// <summary>Why the order was rejected (<see cref="OrderRejectionReasons"/>); null unless <see cref="OrderStatus.Rejected"/>.</summary>
    public string? RejectionReason { get; private set; }

    /// <summary>
    /// When a payment for the order was begun (SPEC-0006 criterion 6, ADR-0022). Once set, the order can no longer be
    /// cancelled or expire: a charge may be under way.
    /// </summary>
    public DateTimeOffset? PaymentStartedAt { get; private set; }

    /// <summary>Why a paid order is being cancelled (audit, SPEC-0007 criterion 14); null unless it was.</summary>
    public string? CancellationReason { get; private set; }

    /// <summary>Who cancelled a paid order (audit); null unless it was.</summary>
    public Guid? CancellationRequestedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    public static AppError? CheckLines(IReadOnlyCollection<Guid?>? productIds)
    {
        if (productIds is null || productIds.Count == 0)
        {
            return OrderErrors.LinesRequired;
        }

        if (productIds.Count > MaxLines)
        {
            return OrderErrors.LinesTooMany;
        }

        var named = productIds.OfType<Guid>().ToList();
        return named.Distinct().Count() != named.Count ? OrderErrors.ProductDuplicated : null;
    }

    public static AppError? CheckProductId(Guid? productId) =>
        productId is { } value && value != Guid.Empty ? null : OrderErrors.ProductIdRequired;

    public static AppError? CheckQuantity(int? quantity) =>
        quantity is >= 1 and <= MaxQuantity ? null : OrderErrors.QuantityInvalid;

    /// <summary>Creates a draft order owned by <paramref name="customerMembershipId"/>.</summary>
    /// <exception cref="AppException">A line rule is broken, or the products' currencies differ.</exception>
    /// <exception cref="ArgumentException">An id is empty: a programming error, never a user mistake.</exception>
    public static Order Create(Guid id, Guid tenantId, Guid customerMembershipId, IReadOnlyList<OrderLineDraft> lines, DateTimeOffset now)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || customerMembershipId == Guid.Empty)
        {
            throw new ArgumentException("An order needs an id, a tenant, and an owner.");
        }

        var order = new Order
        {
            Id = id,
            TenantId = tenantId,
            CustomerMembershipId = customerMembershipId,
            Status = OrderStatus.Draft,
            CreatedAt = now,
        };
        order.SetLines(lines, now);
        return order;
    }

    public const int MaxCancellationReasonLength = 500;

    /// <summary>A paid order is cancelled with a reason: 1 to 500 characters, no control characters (SPEC-0007 criterion 14).</summary>
    public static AppError? CheckCancellationReason(string? reason) =>
        reason?.Trim() is { Length: > 0 and <= MaxCancellationReasonLength } trimmed && !TextRules.ContainsDisallowedCharacters(trimmed)
            ? null
            : OrderErrors.CancellationReasonInvalid;

    public bool IsOwnedBy(Guid membershipId) => CustomerMembershipId == membershipId;

    /// <summary>Replaces every line and recomputes the total, while the order is a draft.</summary>
    /// <exception cref="AppException"><see cref="OrderErrors.InvalidTransition"/> after the draft stage, or a line rule.</exception>
    public void ReplaceLines(IReadOnlyList<OrderLineDraft> lines, DateTimeOffset now)
    {
        NextOrThrow(OrderTrigger.EditLines);
        SetLines(lines, now);
    }

    /// <summary>The state <paramref name="trigger"/> leads to from the current one.</summary>
    /// <exception cref="AppException"><see cref="OrderErrors.InvalidTransition"/>.</exception>
    public OrderStatus NextOrThrow(OrderTrigger trigger) =>
        OrderStateMachine.Next(Status, trigger) ?? throw new AppException(OrderErrors.InvalidTransition);

    private void SetLines(IReadOnlyList<OrderLineDraft> lines, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lines);
        Enforce(CheckLines([.. lines.Select(static l => (Guid?)l.ProductId)])
            ?? lines.Select(static l => CheckProductId(l.ProductId) ?? CheckQuantity(l.Quantity)).FirstOrDefault(static e => e is not null));
        var currencies = lines.Select(static l => l.Currency.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();
        Enforce(currencies.Count == 1 ? null : OrderErrors.MixedCurrency);
        var total = lines.Sum(static l => l.UnitPrice * l.Quantity);
        Enforce(total <= MaxAmount ? null : OrderErrors.TotalTooLarge);

        _lines.Clear();
        _lines.AddRange(lines.Select((line, index) => OrderLine.From(TenantId, Id, index + 1, line)));
        Currency = currencies[0];
        TotalAmount = total;
        UpdatedAt = now;
    }

    private static void Enforce(AppError? error)
    {
        if (error is not null)
        {
            throw new AppException(error);
        }
    }
}
