# TenantFlow engineering handbook

The handbook grows with each milestone. Documents are listed only once they exist.

| Area | Document |
|------|----------|
| Requirements (source of truth) | [PROJECT_BLUEPRINT.md](PROJECT_BLUEPRINT.md) |
| Plan | [roadmap.md](roadmap.md): prioritized milestones, gates, and sequencing notes |
| Progress dashboard | [progress/README.md](progress/README.md): `dotnet run --project tools/TenantFlow.Progress -- --verify --open` |
| Feature specs | [specs/](specs/README.md): when a spec is required, its lifecycle, and the index |
| Engineering conventions | [engineering-conventions.md](engineering-conventions.md): structure, naming, layers, API, errors, auth, configuration, logging, testing, ADRs |
| Local environment | [operations/local-environment.md](operations/local-environment.md): dev script, ports, Docker setup, troubleshooting |
| Working with Claude Code | [`AGENTS.md`](../AGENTS.md) and [ADR-0006](architecture/decisions/0006-claude-code-setup-and-spec-driven-workflow.md) |
| Milestone guides | [M0](milestones/M0-engineering-foundation.md), [M1](milestones/M1-local-platform.md), [M2](milestones/M2-identity-and-tenant-boundary.md) |
| Architecture decisions | [architecture/decisions/](architecture/decisions/README.md) |
| Open decisions | [architecture/open-decisions.md](architecture/open-decisions.md) |

Planned sections, following blueprint section 22: `architecture/` overview and dependency rules
(M3), `domain/` (M3 onward), `api/` conventions (M3), `security/` and `threat-models/` (M2 onward),
`testing/` (M3, with Testcontainers), `operations/` (M10), `performance/` (M11), `delivery/` (M12), and `learning-labs/` (M14).
