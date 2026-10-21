# ---> Dotnet build stage + nodejs build stage
FROM ubuntu:noble AS dotnet-builder

LABEL Author="Luca Di Leo <ldileo@digipa.it>"

# Prerequisites (build-only: no interactive tzdata/apt prompts)
ARG DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --fix-missing --no-install-recommends git curl software-properties-common gpg-agent wget

# Install dotnet (the SDK band pinned by global.json) and nodejs
COPY global.json /tmp/global.json
RUN curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --jsonfile /tmp/global.json \
    && curl -sL https://deb.nodesource.com/setup_24.x | bash - \
    && apt-get install -y nodejs \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Add dotnet to PATH
ENV PATH="/root/.dotnet:$PATH"
ENV DOTNET_ROOT="/root/.dotnet"

WORKDIR /Registry

# Restore first, from the manifests only, so a source change does not invalidate the npm ci and
# dotnet restore layers
COPY Registry.Web/ClientApp/package.json Registry.Web/ClientApp/package-lock.json Registry.Web/ClientApp/
RUN cd Registry.Web/ClientApp && npm ci

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY Registry.Web/.config/dotnet-tools.json Registry.Web/.config/
COPY Registry.Web/Registry.Web.csproj Registry.Web/
COPY Registry.Adapters/Registry.Adapters.csproj Registry.Adapters/
COPY Registry.Common/Registry.Common.csproj Registry.Common/
COPY Registry.Ports/Registry.Ports.csproj Registry.Ports/
COPY Registry.Web.Data/Registry.Web.Data.csproj Registry.Web.Data/
COPY Registry.Web.Data.MySqlMigrations/Registry.Web.Data.MySqlMigrations.csproj Registry.Web.Data.MySqlMigrations/
COPY Registry.Web.Data.SqliteMigrations/Registry.Web.Data.SqliteMigrations.csproj Registry.Web.Data.SqliteMigrations/
COPY Registry.Web.Identity/Registry.Web.Identity.csproj Registry.Web.Identity/
COPY Registry.Web.Identity.MySqlMigrations/Registry.Web.Identity.MySqlMigrations.csproj Registry.Web.Identity.MySqlMigrations/
COPY Registry.Web.Identity.SqliteMigrations/Registry.Web.Identity.SqliteMigrations.csproj Registry.Web.Identity.SqliteMigrations/
RUN cd Registry.Web && dotnet tool restore && dotnet restore -r linux-x64

# Copy the rest of the sources
COPY . /Registry

# Compile client app
RUN cd /Registry/Registry.Web/ClientApp && npm run build:prod

# Publish Registry (self-contained single file, see docker/FolderProfile.xml)
COPY docker/FolderProfile.xml /Registry/Registry.Web/Properties/PublishProfiles/FolderProfile.pubxml
RUN cd /Registry/Registry.Web && dotnet dev-certs https && dotnet publish -p:PublishProfile=FolderProfile

# ---> Dotnet stage run
FROM ubuntu:noble AS runner

# Prerequisites
ARG DEBIAN_FRONTEND=noninteractive
ENV DOTNET_GENERATE_ASPNET_CERTIFICATE=false

# DroneDB: the release pinned in Directory.Build.props (DdbVersion, DdbDebSha256), never the latest
# one. The download is verified with sha256sum -c. scripts/ddb-release.sh check (CI) fails when these
# defaults differ from Directory.Build.props.
ARG DDB_VERSION=1.12.2
ARG DDB_DEB_SHA256=b1c2c86c368d2915ebf14112b835f475d4c47f4cafb46224c31e02b7566473c4

# Install runtime dependencies in single layer including SkiaSharp dependencies.
# No .NET runtime: the app is published self-contained (docker/FolderProfile.xml).
# tzdata: the image runs in UTC; set TZ (e.g. TZ=Europe/Rome, see docker/production) to change it.
RUN apt-get update \
    && apt-get install -y --fix-missing --no-install-recommends \
        ca-certificates \
        tzdata \
        wget \
        curl \
        libfontconfig1 \
        libfreetype6 \
        libgl1 \
        libglib2.0-0 \
        libharfbuzz0b \
        libice6 \
        libsm6 \
        libx11-6 \
        libxext6 \
        libxrender1 \
        zlib1g \
        libldap2 \
        libldap-common \
        libsasl2-2 \
    && curl -fsSL --retry 3 -o /tmp/dronedb.deb \
        "https://github.com/DroneDB/DroneDB/releases/download/v${DDB_VERSION}/ddb_${DDB_VERSION}_amd64.deb" \
    && echo "${DDB_DEB_SHA256}  /tmp/dronedb.deb" | sha256sum -c - \
    && apt-get install -y /tmp/dronedb.deb \
    && rm /tmp/dronedb.deb \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Ensure SkiaSharp can find native libraries
ENV LD_LIBRARY_PATH="/usr/lib/x86_64-linux-gnu:/usr/lib:/lib"

# Unprivileged user the app runs as (uid/gid 1654, the .NET container convention; uid 1000 is the
# image's "ubuntu" user). docker/entrypoint.sh starts as root only to hand /data to this user.
ARG APP_UID=1654
RUN groupadd --system --gid "$APP_UID" registry \
    && useradd --system --uid "$APP_UID" --gid registry --create-home --home-dir /home/registry \
        --shell /usr/sbin/nologin registry

# Copy compiled Registry
COPY --from=dotnet-builder /Registry/Registry.Web/bin/Release/net10.0/publish/ /Registry
COPY --chmod=755 docker/entrypoint.sh /entrypoint.sh
COPY --chmod=755 docker/healthcheck.sh /healthcheck.sh

# Strip CRs in case the scripts were checked out with CRLF (Windows autocrlf)
RUN sed -i 's/\r$//' /entrypoint.sh /healthcheck.sh \
    && chmod +x /Registry/Registry.Web \
    && mkdir -p /data/temp && chown -R registry:registry /data

ENV REGISTRY_PORT=5000
EXPOSE 5000/tcp

# Set default instance type
ENV INSTANCE_TYPE=0

# Redirect GDAL/Nexus and general temporary files to a subdirectory of the data
# directory instead of the container working directory / root filesystem.
# Without this, multi-GB COG and Nexus build scratch can be written to the
# process working directory, which on the Registry containers is an anonymous
# Docker volume on the host root partition. Deployments bind-mount /data/temp
# onto the large data volume; the in-process build pipeline additionally scopes
# its own scratch to the per-build temp folder. /tmp is often a (small) tmpfs in
# deployments, so it must NOT be used for these potentially large files.
ENV TMPDIR=/data/temp
ENV CPL_TMPDIR=/data/temp
ENV TEMP=/data/temp

# Liveness per instance type (HTTP probe, or the process for a processing node)
HEALTHCHECK --interval=30s --timeout=10s --start-period=120s --retries=5 CMD ["/healthcheck.sh"]

# Run registry (exec'ed by the entrypoint: PID 1, receives SIGTERM, runs as the registry user)
ENTRYPOINT ["/entrypoint.sh"]
