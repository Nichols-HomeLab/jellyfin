# Jellyfin HA with PostgreSQL

This fork combines three pieces on a Jellyfin 10.11.11 base:

- the Redis-backed transcode coordination code from `ZoltyMat/jellyfin-ha`;
- the PostgreSQL query optimizations from `Nichols-HomeLab/jellyfin`;
- the PostgreSQL database provider from `Nichols-HomeLab/Jellyfin.Pgsql`, pinned as a Git submodule.

The PostgreSQL provider remains experimental. Back up the Jellyfin config and
database before migrating an existing server.

## Build

Clone the fork and initialize the provider submodule:

```bash
git clone https://github.com/Nichols-HomeLab/jellyfin-ha.git
cd jellyfin-ha
git submodule update --init plugins/Jellyfin.Pgsql
docker build -f Dockerfile.pgsql -t jellyfin-ha-pgsql:10.11.11 .
```

The image publishes the complete HA server and the PostgreSQL plugin together.
It deliberately does not overlay a single patched assembly on an official
Jellyfin image because the HA changes span several server assemblies.

## Required configuration

Configure both PostgreSQL and Redis:

```yaml
services:
  jellyfin:
    image: jellyfin-ha-pgsql:10.11.11
    environment:
      POSTGRES_CONNECTION_STRING: >-
        Host=postgres;Port=5432;Database=jellyfin;Username=jellyfin;Password=change-me
      Jellyfin__TranscodeStore__RedisConnectionString: redis:6379,abortConnect=false
      Jellyfin__TranscodeStore__LeaseDurationSeconds: "30"
      JELLYFIN_INSTANCE_ID: jellyfin-1
    volumes:
      - jellyfin-config:/config
      - jellyfin-cache:/cache
      - jellyfin-transcode:/transcode
      - /path/to/media:/media:ro
```

`POSTGRES_CONNECTION_STRING` takes precedence over the legacy
`POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_DB`, `POSTGRES_USER`, and
`POSTGRES_PASSWORD` variables. `JELLYFIN_POSTGRES_CONNECTION_STRING` is also
accepted as an alias.

For multiple replicas, every instance needs:

- the same PostgreSQL database;
- the same Redis deployment;
- a unique `JELLYFIN_INSTANCE_ID` (a pod or task name is suitable);
- shared read-write `/config` and `/transcode` storage;
- the same read-only media paths on every node.

Use a load balancer with session affinity for normal playback traffic. Redis
leases coordinate recovery; they are not a replacement for stable request
routing while the owning instance is healthy.

## First start

Use an empty PostgreSQL database and an empty Jellyfin config directory for the
first validation start. The entrypoint installs the provider under
`/config/plugins/PostgreSQL`, creates `/config/config/database.xml` when it is
missing, and writes the configured connection string into that file before
starting Jellyfin.

The existing SQLite-to-PostgreSQL migration procedure is documented in the
provider submodule's `README.md`. Do not point an untested build at the only
copy of an existing Jellyfin library.

## Validation

The server build and HA-focused tests can be run without installing the .NET
SDK on the host:

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet build Jellyfin.Server/Jellyfin.Server.csproj --configuration Release
```

Build the final image with `Dockerfile.pgsql` to validate that the pinned
provider and server source compile together.
