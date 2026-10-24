namespace TenantFlow.Payments.Domain.Payments;

/// <summary>
/// The stable names a <see cref="PaymentStatus"/> has in the database. A stored name nobody recognises is data corruption
/// and fails loudly. The API shows <see cref="PaymentStatus.Unknown"/> as <c>processing</c> (SPEC-0006 criterion 5).
/// </summary>
public static class PaymentStatusNames
{
    public const string Processing = "processing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
    public const string RequiresReview = "requires_review";

    /// <summary>Every name, for the database CHECK constraint.</summary>
    public static readonly IReadOnlyList<string> All = [Processing, Succeeded, Failed, Unknown, RequiresReview];

    /// <exception cref="InvalidOperationException">The status is not one the domain defines.</exception>
    public static string ToName(this PaymentStatus status) => status switch
    {
        PaymentStatus.Processing => Processing,
        PaymentStatus.Succeeded => Succeeded,
        PaymentStatus.Failed => Failed,
        PaymentStatus.Unknown => Unknown,
        PaymentStatus.RequiresReview => RequiresReview,
        _ => throw new InvalidOperationException("The payment status is not one the domain defines."),
    };

    /// <exception cref="InvalidOperationException">The stored name is not a known status name (case matters).</exception>
    public static PaymentStatus Parse(string name) => name switch
    {
        Processing => PaymentStatus.Processing,
        Succeeded => PaymentStatus.Succeeded,
        Failed => PaymentStatus.Failed,
        Unknown => PaymentStatus.Unknown,
        RequiresReview => PaymentStatus.RequiresReview,
        _ => throw new InvalidOperationException("The stored payment status is not a known status name."),
    };
}
