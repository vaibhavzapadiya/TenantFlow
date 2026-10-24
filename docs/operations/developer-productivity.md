# Developer productivity

Shortcuts for the daily loop. None of this changes what the gate checks; it makes getting there faster.
The local environment itself is in [local-environment.md](local-environment.md).

## Try the API

- **IDE HTTP client:** `src/Hosts/TenantFlow.Api/TenantFlow.Api.http` signs in as the platform operator and as
  `dev-user-a`, creates a tenant, and walks every feature with ids chained from earlier responses. Choose the `dev`
  environment; resend a login request when a call answers 401 (tokens live 5 minutes).
- **Scalar:** `http://localhost:5080/scalar/v1` opens when the API starts from Visual Studio. Under Authentication, pick
  **Tenant user** or **Platform operator** and click **Authorize** to sign in at the local Keycloak; the token is then
  sent with every request.

## Open less, build less

Open a solution filter from `solution-filters/` instead of the whole solution when you work on one module or host:
it loads and builds a fraction of the 68 projects. Build one project from the command line with
`dotnet build src/Modules/Catalog/TenantFlow.Catalog.Infrastructure -c Release`.

## Run fewer tests

The test projects are xUnit v3 executables: `dotnet run` takes filters after `--`.

| Goal | Command |
|------|---------|
| One test class | `dotnet run --project tests/Api/TenantFlow.Api.Tests -c Release -- -class "*OpenApiContractTests"` |
| One test | `dotnet run --project tests/Api/TenantFlow.Api.Tests -c Release -- -method "*grouped_under_a_feature_tag"` |
| Skip the rebuild | add `--no-build` after a `dotnet build` |
| One database test class (stack running) | `dotnet run --project tests/Database/TenantFlow.Database.Tests -c Release -- -class "*IsolationMatrixTests"` |
| Accept an intended API contract change | `TENANTFLOW_UPDATE_OPENAPI_SNAPSHOT=1`, then run `OpenApiContractTests` once and review the diff |

In Visual Studio, Test Explorer → right-click a group → **Add to Playlist** keeps a set you rerun often.

## Code snippets

`tools/snippets/` holds Visual Studio snippets. Install once: **Tools → Code Snippets Manager → Language: C# → Add…**
and choose the folder. Type the shortcut and press Tab twice.

| Shortcut | Inserts |
|----------|---------|
| `tfendpoints` | A tenant endpoint group: `MapGroup`, OpenAPI tag, Problem Details, tenant permission |
| `tflog` | A `[LoggerMessage]` method (ADR-0007) |
| `tffact` | An async xUnit v3 test with the test's cancellation token |
| `tfdeny` | A cross-tenant denial test for a database test class (rule 1) |

Rider: import them through **Settings → Editor → Live Templates**, or keep them as a reference.

## Visual Studio settings (per developer)

- **Format on save:** Tools → Options → Text Editor → Code Cleanup → *Run Code Cleanup profile on Save*, with a
  profile that applies `.editorconfig` formatting. The build treats formatting as an error; this fixes it first.
- **Hot Reload on save:** Debug → Options → .NET / C++ Hot Reload → *Apply Hot Reload on File Save*. Edits to
  endpoint and handler bodies apply to the running API and Worker without a restart. From the command line:
  `dotnet watch --project src/Hosts/TenantFlow.Api`.
- **Faster startup:** Tools → Options → Projects and Solutions → General → untick *Reopen documents on solution load*
  if you open a filter often.

## Git (per developer)

- `git config --global rerere.enabled true`: a resolved conflict is resolved again automatically on the next rebase.
- `git config --global alias.lg "log --oneline --graph --decorate -20"`: a compact history with `git lg`.

## Claude Code

`.claude/settings.json` allows the gate's `dotnet run` checks without a prompt and runs `dotnet format whitespace` on
each C# file Claude edits, so a formatting slip never reaches the build.
