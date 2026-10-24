# deploy/

Everything that builds, runs or deploys TenantFlow outside `dotnet run`. Folders are grouped by what the files are, so
a file used both locally and in AWS lives in one place that neither side owns.

| Folder | Holds | Used by |
|--------|-------|---------|
| `certs/` | The RDS certificate authority bundle (`docs/THIRD-PARTY-NOTICES.md`) | The API, Worker, Migrator and Keycloak images |
| `identity/realms/local/` | Keycloak realms with synthetic users and the local clients | The Compose Keycloak (imported on every new container) |
| `identity/realms/production/` | Keycloak realms with no users, OTP and brute-force protection | The deployed Keycloak image (`keycloak/Dockerfile`) |
| `observability/` | Prometheus recording and alert rules with their promtool tests; Grafana dashboards (ADR-0029) | The Compose observability profile; a deployed telemetry backend once chosen |
| `compose/` | The local stack (`compose.yaml`, `.env.example`) and its runtime configuration (`config/`) | `docker compose` from the repository root (ADR-0037) |
| `docker/` | The API, Worker and Migrator image (`Dockerfile`); the Migrator image also runs `provision` | CI, the release workflow, Compose (provisioner and Migrator) |
| `keycloak/` | The deployed Keycloak image (`Dockerfile`) | The release workflow |
| `terraform/` | AWS infrastructure (`README.md` has the runbook) | The operator |

The local and production realms must keep the same realm roles and token shape (`RealmParityTests`). Every path here
is also named in tests (`ComposeFileTests`, `ContainerImageTests`, `ObservabilityConfigTests`, ...), so a move fails
the build until they follow.

There are no shell scripts here (`LocalToolingTests`): database and broker provisioning is the Migrator's `provision`
command (`src/Hosts/TenantFlow.Migrator/Provisioning`), releases use the official ECS actions
(`.github/workflows/deploy.yml`), and an edited migration fails the build (`AppliedMigrationTests`).
