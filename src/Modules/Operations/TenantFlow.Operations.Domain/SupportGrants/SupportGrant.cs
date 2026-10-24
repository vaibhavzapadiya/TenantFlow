using System.Text.RegularExpressions;
using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.Operations.Domain.SupportGrants;

/// <summary>
/// A support operator's time-limited, read-only access to one tenant (SPEC-0009 criteria 16 to 18, ADR-0027 decision 2).
/// It names the operator by a keyed reference, never a subject, and its reason by a ticket reference, never free text.
/// It is active from its start until it expires or is ended, whichever comes first.
/// </summary>
public sealed partial class SupportGrant
{
    public const int MaxMinutes = 240;

    public const int TicketMaxLength = 21;

    // Also the constructor the database mapping uses.
    private SupportGrant(Guid id, Guid tenantId, string operatorRef, string ticket, DateTimeOffset startsAt, DateTimeOffset expiresAt)
    {
        Id = id;
        TenantId = tenantId;
        OperatorRef = operatorRef;
        Ticket = ticket;
        StartsAt = startsAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>The operator's keyed reference (ADR-0026 decision 3).</summary>
    public string OperatorRef { get; private set; }

    /// <summary>A ticket reference such as <c>SUP-1234</c>.</summary>
    public string Ticket { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>The reference of whoever ended it: its operator, or a platform administrator who revoked it.</summary>
    public string? EndedBy { get; private set; }

    /// <summary>Null when a grant can be opened with this ticket and duration; otherwise why not.</summary>
    public static AppError? Check(string? ticket, int? minutes)
    {
        if (ticket is null || ticket.Length > TicketMaxLength || !TicketPattern().IsMatch(ticket))
        {
            return OperationsErrors.TicketInvalid;
        }

        return minutes is null or < 1 or > MaxMinutes ? OperationsErrors.DurationInvalid : null;
    }

    /// <exception cref="ArgumentException">A ticket or duration that <see cref="Check"/> refuses: a programming error.</exception>
    public static SupportGrant Open(Guid id, Guid tenantId, string operatorRef, string ticket, int minutes, DateTimeOffset now) =>
        Check(ticket, minutes) is null
            ? new SupportGrant(id, tenantId, operatorRef, ticket, now, now.AddMinutes(minutes))
            : throw new ArgumentException("A support grant needs a ticket reference and a duration of 1 to 240 minutes.");

    public bool IsActive(DateTimeOffset now) => EndedAt is null && ExpiresAt > now;

    [GeneratedRegex("^[A-Z]{2,10}-[0-9]{1,10}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex TicketPattern();
}
