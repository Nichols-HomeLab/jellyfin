ARG JELLYFIN_VERSION=10.11.11

FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
WORKDIR /src

COPY . .

RUN dotnet restore Jellyfin.Server/Jellyfin.Server.csproj \
    && dotnet publish Jellyfin.Server/Jellyfin.Server.csproj \
        --configuration Release \
        --no-restore \
        --output /out/server

WORKDIR /plugin
COPY plugins/Jellyfin.Pgsql/ .

# Build the plugin outside the Jellyfin source root so it does not inherit
# Jellyfin's central NuGet and analyzer configuration.
RUN dotnet restore Jellyfin.Plugin.Pgsql/Jellyfin.Plugin.Pgsql.csproj \
    && dotnet publish Jellyfin.Plugin.Pgsql/Jellyfin.Plugin.Pgsql.csproj \
        --configuration Release \
        --no-restore \
        --output /out/plugin

FROM jellyfin/jellyfin:${JELLYFIN_VERSION}

# The PostgreSQL provider uses pg_dump/psql for Jellyfin backup and restore.
# Keep the client on the current PostgreSQL major version so it can connect to
# current or older servers. xmlstarlet safely updates database.xml at startup.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl gnupg xmlstarlet \
    && install -d -m 0755 /usr/share/postgresql-common/pgdg \
    && curl --fail --silent --show-error https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        | gpg --dearmor -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.gpg \
    && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.gpg] https://apt.postgresql.org/pub/repos/apt/ $(. /etc/os-release && echo \"${VERSION_CODENAME}\")-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install --yes --no-install-recommends postgresql-client-18 \
    && rm -rf /var/lib/apt/lists/*

# Publish the complete HA server. Replacing only one assembly is unsafe because
# the HA hooks span Jellyfin.Api, MediaBrowser.Controller, and both server
# implementation assemblies.
COPY --from=build /out/server/ /jellyfin/
COPY --from=build /out/plugin/ /jellyfin-pgsql/plugin/
COPY plugins/Jellyfin.Pgsql/docker/database.xml /jellyfin-pgsql/database.xml
COPY plugins/Jellyfin.Pgsql/docker/entrypoint.sh /entrypoint-pgsql.sh

RUN chmod 0755 /entrypoint-pgsql.sh

ENTRYPOINT ["/entrypoint-pgsql.sh"]
