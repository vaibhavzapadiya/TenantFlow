# Third-party notices

TenantFlow's own code is licensed under the [MIT License](../LICENSE). The repository also contains the third-party
material below, which keeps its original license.

NuGet packages, container images and GitHub Actions are **not** included in this repository. They are downloaded at
build or run time under their own licenses: see `Directory.Packages.props`, `deploy/compose/compose.yaml`, the
Dockerfiles in `deploy/` and the workflows in `.github/workflows/`.

## Quartz.NET job store schema

- **Where:** `src/BuildingBlocks/TenantFlow.BuildingBlocks.Scheduling/Persistence/Migrations/20261021160000_QuartzTables.cs`
- **What:** the PostgreSQL table definitions from `database/tables/tables_postgres.sql` of Quartz.NET 3.22.4, placed
  in the `scheduler` schema and without the original DROP block.
- **Source:** <https://github.com/quartznet/quartznet>
- **License:** Apache License 2.0, <https://www.apache.org/licenses/LICENSE-2.0>. Copyright the Quartz.NET project
  and its contributors.

## Amazon RDS certificate bundle

- **Where:** `deploy/certs/rds-ca-ap-south-1.crt`
- **What:** the public certificate authority bundle that Amazon RDS publishes for the `ap-south-1` region. The
  images use it to verify the database's TLS certificate.
- **Source:** <https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/UsingWithRDS.SSL.html>
- **License:** published by Amazon Web Services for public use in TLS verification. It is not covered by this
  repository's MIT license.
