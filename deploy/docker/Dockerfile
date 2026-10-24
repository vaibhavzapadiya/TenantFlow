# syntax=docker/dockerfile:1
# Images for the three deployable processes (blueprint section 18; SPEC-0012 S3d). One build stage, three final targets:
#   docker build -f deploy/docker/Dockerfile --target api      -t tenantflow/api:<tag> .
#   docker build -f deploy/docker/Dockerfile --target worker   -t tenantflow/worker:<tag> .
#   docker build -f deploy/docker/Dockerfile --target migrator -t tenantflow/migrator:<tag> .
# The context is the repository root; .dockerignore lets in only what the build needs (never deploy/compose/.env).
#
# Final images are Ubuntu chiseled: no shell or package manager, non-root by default, and nothing on disk but the
# runtime and the app. They run with a read-only root file system and a writable /tmp only (deploy/terraform).
# .NET 10 LTS (ADR-0003), moved from .NET 9 by SPEC-0015 S3, which closed SC-A13. Base images are pinned by digest
# (2026-10-24); a patch release means a new digest here, in a reviewed commit.

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2
ARG ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:9e3919aff68057f3e45125c5255a71d3b5f40364e45ede883348688851b9f0df
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra@sha256:2e4f4e0a8f0807397c91585b3b6142989b43e9030a6494af1b7abd251b915de1

FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY . .
# Restore from the versions in Directory.Packages.props. The NuGet folder is a BuildKit cache mount, so a source change
# re-downloads nothing; it exists only during this RUN, so restore and publish share it.
RUN --mount=type=cache,id=tenantflow-nuget,target=/root/.nuget/packages \
    dotnet restore src/Hosts/TenantFlow.Api/TenantFlow.Api.csproj \
 && dotnet restore src/Hosts/TenantFlow.Worker/TenantFlow.Worker.csproj \
 && dotnet restore src/Hosts/TenantFlow.Migrator/TenantFlow.Migrator.csproj \
 && dotnet publish src/Hosts/TenantFlow.Api/TenantFlow.Api.csproj -c Release --no-restore -o /out/api -p:UseAppHost=false \
 && dotnet publish src/Hosts/TenantFlow.Worker/TenantFlow.Worker.csproj -c Release --no-restore -o /out/worker -p:UseAppHost=false \
 && dotnet publish src/Hosts/TenantFlow.Migrator/TenantFlow.Migrator.csproj -c Release --no-restore -o /out/migrator -p:UseAppHost=false \
 && rm -f /out/*/appsettings.Development.json
# Development settings (API docs on, debug logging, the simulator switched on) never ship in an image.

# Shared settings of the final images: no diagnostics pipes in /tmp, and the RDS certificate authority that the
# database connection alone trusts (Root Certificate= in the connection string; SPEC-0012 question 8).
FROM ${ASPNET_IMAGE} AS api
WORKDIR /app
ENV DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_HTTP_PORTS=8080
COPY deploy/certs/rds-ca-ap-south-1.crt /app/certs/rds-ca.pem
COPY --from=build /out/api .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "TenantFlow.Api.dll"]

FROM ${ASPNET_IMAGE} AS worker
WORKDIR /app
ENV DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_HTTP_PORTS=8080
COPY deploy/certs/rds-ca-ap-south-1.crt /app/certs/rds-ca.pem
COPY --from=build /out/worker .
USER $APP_UID
ENTRYPOINT ["dotnet", "TenantFlow.Worker.dll"]

FROM ${RUNTIME_IMAGE} AS migrator
WORKDIR /app
ENV DOTNET_EnableDiagnostics=0
COPY deploy/certs/rds-ca-ap-south-1.crt /app/certs/rds-ca.pem
COPY --from=build /out/migrator .
USER $APP_UID
ENTRYPOINT ["dotnet", "TenantFlow.Migrator.dll"]
