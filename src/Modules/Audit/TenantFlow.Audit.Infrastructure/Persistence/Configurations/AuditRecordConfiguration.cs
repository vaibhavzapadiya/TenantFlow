using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantFlow.Audit.Contracts;
using TenantFlow.Audit.Domain.Records;

namespace TenantFlow.Audit.Infrastructure.Persistence.Configurations;

/// <summary>
/// The audit record row (ADR-0026). Targets and members are referenced by id only: no foreign key crosses into another
/// module's schema (ADR-0012). Codes are stored as they are, and the database refuses any outside the closed lists.
/// Append-only is enforced by the migration (privileges and an update trigger), not by this mapping.
/// </summary>
internal sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> record)
    {
        record.ToTable("audit_records", table =>
        {
            table.HasCheckConstraint("ck_audit_records_action", $"action IN ({Quoted(AuditActions.All)})");
            table.HasCheckConstraint("ck_audit_records_actor_kind", $"actor_kind IN ({Quoted(AuditActorKinds.All)})");
            table.HasCheckConstraint("ck_audit_records_target_type", $"target_type IN ({Quoted(AuditTargetTypes.All)})");
            // A tenant's log never names another tenant (SPEC-0009 S2 review fix).
            table.HasCheckConstraint("ck_audit_records_tenant_target", $"target_type <> '{AuditTargetTypes.Tenant}' OR target_id = tenant_id");
        });
        record.HasKey(r => r.Id).HasName("pk_audit_records");
        record.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        record.Property(r => r.TenantId).HasColumnName("tenant_id");
        record.Property(r => r.OccurredAt).HasColumnName("occurred_at");
        record.Property(r => r.ActorKind).HasColumnName("actor_kind").HasMaxLength(32);
        record.Property(r => r.ActorRef).HasColumnName("actor_ref").HasMaxLength(AuditActor.OperatorReferenceLength);
        record.Property(r => r.Action).HasColumnName("action").HasMaxLength(64);
        record.Property(r => r.TargetType).HasColumnName("target_type").HasMaxLength(32);
        record.Property(r => r.TargetId).HasColumnName("target_id");
        record.Property(r => r.TraceId).HasColumnName("trace_id").HasMaxLength(AuditRecord.TraceIdLength);
        // A tenant's log, newest first, with the id as the tie-breaker for keyset paging (SPEC-0009 S3).
        record.HasIndex(r => new { r.TenantId, r.OccurredAt, r.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_audit_records_tenant_id_occurred_at_id");
    }

    // Sorted, so the generated constraint does not change between builds.
    private static string Quoted(IEnumerable<string> values) => string.Join(", ", values.Order(StringComparer.Ordinal).Select(static v => $"'{v}'"));
}
