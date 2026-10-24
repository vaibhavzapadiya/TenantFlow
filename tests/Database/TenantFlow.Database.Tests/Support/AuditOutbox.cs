namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// SQL that counts a module's <c>audit.recorded.v1</c> outbox events for the target <c>@id</c>, made by the member whose
/// membership id the requester query returns (SPEC-0009 criterion 2). For <see cref="PaymentScenario.CountAsync"/>.
/// </summary>
internal static class AuditOutbox
{
    public static string Count(string schema, string action, string targetType, string requesterSql) => $"""
        SELECT count(*) FROM {schema}.outbox_messages
        WHERE type = 'audit.recorded.v1' AND payload->>'action' = '{action}' AND payload->>'targetType' = '{targetType}'
          AND payload->>'targetId' = @id::text AND payload->>'actorKind' = 'member' AND payload->>'actorRef' = ({requesterSql})::text
        """;
}
