# Runbook: Failed migration

## Symptoms and impact
The Migrator (`src/Hosts/TenantFlow.Migrator`) exits non-zero, or a host fails readiness after a release because the
schema it expects is missing. No alert exists: the Migrator is a one-shot job whose exit code and logs are the signal
(deployed: the pipeline step fails, M12). Hosts of the new version must not start against an unmigrated schema.

## Severity and escalation
Blocks the release; treat as `page` during a deployment. Escalate to the author of the migration.

## Preconditions / access required
The Migrator's output; the owner database role (`tenantflow_owner`) for inspection; never the application role.

## Dashboards and diagnostic queries
- The Migrator's logs: it logs one line per module (events 3002 "Migrating ...", 3003 "All module migrations are
  applied") and the failing module's exception.
- Applied migrations per module (SQL, owner role):
  `SELECT "MigrationId" FROM <schema>."__EFMigrationsHistory" ORDER BY "MigrationId";`
- Locks held by a running migration (SQL): `SELECT pid, state, wait_event_type, left(query, 120) FROM pg_stat_activity WHERE datname = current_database();`
- Hosts failing readiness (PromQL): `min by (service_name) (tenantflow_health_status{check="postgresql"})`

## Immediate safe mitigations
Stop the rollout; keep the previous version serving (expand-contract migrations keep the old version compatible,
AGENTS.md rule 7). Do not edit an applied migration.

## Recovery procedure
Each module migrates in its own transaction, so a failed module leaves its schema at the previous migration. Fix the
migration in a **new** migration (never edit an applied one), run the Migrator again, then continue the rollout.

## Verification
The Migrator ends with event 3003; every module's history table lists the new migration; hosts become ready; the
database tests pass against the migrated schema.

## Rollback / forward-fix
Forward-fix only: a contract step that already ran cannot be undone without a restore (M13). Expand steps are safe to
leave in place.

## Customer / stakeholder communication
None while the old version serves; otherwise announce the release delay.

## Follow-up actions
Add the failing case to the migration tests (`MigratorTests`, `MigratorFailureTests`).
