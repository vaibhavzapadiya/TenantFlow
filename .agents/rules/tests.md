<!-- Applies to tests/**. Read before writing or changing a test. -->
# Test conventions

- **Rules get host-free unit tests** in `tests/<Module>/TenantFlow.<Module>.Tests`. Do not start a host to
  test something that does not need one.
- **Use the shared factories**, `ApiFactory.CreateProduction` and `WorkerFactory.CreateProduction`, never
  `WebApplicationFactory<Program>` directly: they set the environment, telemetry and connection defaults.
  Hosts run in `Production` so no Development-only default hides a missing setting.
  `ApiFactory.CreateDevelopment` exists only for parity tests.
- **Host tests run sequentially.** Each host-test assembly declares
  `[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]` in `AssemblyInfo.cs`, because
  `WebApplicationFactory` intercepts the next host built in the process.
- **Telemetry goes nowhere.** Export to `TelemetryCapture.UnreachableOtlpEndpoint` with
  `TelemetryCapture.ShortExportTimeoutMilliseconds`, and assert through `TelemetryCapture`, never a live
  collector. Count metrics with `MeterCapture`.
- **Share one database per test class** through a class fixture rather than one per test. Create tenants
  through the harness, which adds a unique suffix; never two tenants with the same literal name.
- **No real secrets or personal data.** Placeholders only, and synthetic tenant and user identifiers.
- **Async all the way.** No `.Result`, `.Wait()` or `.GetAwaiter().GetResult()` (xUnit1031, rule 5).
- **Tenant-scoped behaviour gets a cross-tenant denial test** (`AGENTS.md`, rule 1).
- **Filter while iterating**; the gate runs everything once, at the end.
