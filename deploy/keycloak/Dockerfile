# syntax=docker/dockerfile:1
# The deployed Keycloak (ADR-0035; SPEC-0012 S3g): the pinned Compose image, built ahead for PostgreSQL, with both
# production realms and the RDS certificate authority. The context is the repository root:
#   docker build -f deploy/keycloak/Dockerfile -t tenantflow/keycloak:<tag> .
#
# It starts with "start --optimized --import-realm". The import only creates a realm that does not exist yet, so these
# files seed a new environment; later realm changes are made in the admin console until a config-as-code tool is added.
# The build cache is made here, so the container runs with a read-only root and a writable data/tmp only.

ARG KEYCLOAK_IMAGE=quay.io/keycloak/keycloak:26.8.1@sha256:a78d6b79572168cb25886b80a8b5b8d4ed4390743e3eecb810538683e209dd6b

FROM ${KEYCLOAK_IMAGE} AS build
# Build-time options; the runtime must not contradict them (deploy/terraform/environment/services.tf).
ENV KC_DB=postgres \
    KC_HEALTH_ENABLED=true \
    KC_HTTP_MANAGEMENT_PORT=9000
RUN /opt/keycloak/bin/kc.sh build

FROM ${KEYCLOAK_IMAGE} AS keycloak
COPY --from=build /opt/keycloak/ /opt/keycloak/
COPY --chown=keycloak:root deploy/identity/realms/production/tenantflow-realm.json /opt/keycloak/data/import/tenantflow-realm.json
COPY --chown=keycloak:root deploy/identity/realms/production/tenantflow-platform-realm.json /opt/keycloak/data/import/tenantflow-platform-realm.json
# Trusted only by the JDBC connection (sslrootcert=), with sslmode=verify-full.
COPY deploy/certs/rds-ca-ap-south-1.crt /opt/keycloak/certs/rds-ca.pem
USER 1000
ENTRYPOINT ["/opt/keycloak/bin/kc.sh"]
CMD ["start", "--optimized", "--import-realm"]
