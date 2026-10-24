# Fault drills

Rehearsals of the runbooks against the local stack (SPEC-0010 criterion 14, decision 8). Each drill is a test in
`tests/Drills` (`FaultDrills`), run by hand at a milestone gate, one at a time, never by the gate, because it stops
shared dependencies: `dotnet run --project tests/Drills/TenantFlow.Drills.Tests -c Release -- -method "*Database_down*"` (the test names follow the drills below). The test output
holds the times and the runbook. Prerequisites: `docker compose --profile observability up -d --wait` (which runs the Migrator), and both hosts running.

| Drill | Fault | Expected alert | Runbook |
|---|---|---|---|
| `database` | `postgres` container stopped, then started | `DatabaseDown` (cause; inhibits its symptoms) | database-saturation-and-locks |
| `broker` | `rabbitmq` container stopped, then started | `BrokerDown` (cause) | queue-backlog-and-poison-messages |
| `provider` | three payments with `sim_timeout_unknown`: the simulator never settles them | `PaymentsStuck` | stuck-payment-and-reconciliation |
| `worker` | the Worker process stopped by the drill, then started again from the build output | `WorkerTelemetryMissing` (cause) | hosts-and-scheduled-jobs |
| `poison` | a message without a valid envelope published to `notifications.order-events` | `DeadLettersGrowing` | queue-backlog-and-poison-messages |
| `probe` | a member of one tenant requests other tenants' products | `CrossTenantProbing` | suspected-cross-tenant-access |

A record per run lists, for each drill: when the fault was injected, when the alert was active, the runbook it named,
the runbook queries run and what they found (the failing trace and its logs), and when the alert resolved.
