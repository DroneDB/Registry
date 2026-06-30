# ---> Dotnet build stage + nodejs build stage
FROM ubuntu:noble AS dotnet-builder

LABEL Author="Luca Di Leo <ldileo@digipa.it>"

# Prerequisites
ENV TZ=Europe/Rome
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone
RUN apt-get update && apt-get install -y --fix-missing --no-install-recommends git curl software-properties-common gpg-agent wget

# Install dotnet and nodejs
RUN curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0 \
    && curl -sL https://deb.nodesource.com/setup_24.x | bash - \
    && apt-get install -y nodejs \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Add dotnet to PATH
ENV PATH="/root/.dotnet:$PATH"
ENV DOTNET_ROOT="/root/.dotnet"

# Copy registry
COPY . /Registry

# Compile client app
RUN cd /Registry/Registry.Web/ClientApp && npm install && npm run build:prod

# Publish Registry
COPY docker/FolderProfile.xml /Registry/Registry.Web/Properties/PublishProfiles/FolderProfile.pubxml
RUN cd /Registry/Registry.Web && dotnet dev-certs https && dotnet publish -p:PublishProfile=FolderProfile

# ---> Dotnet stage run
FROM ubuntu:noble AS runner

# Prerequisites
ENV DOTNET_GENERATE_ASPNET_CERTIFICATE=false
ENV TZ=Europe/Rome
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

# Install runtime dependencies in single layer including SkiaSharp dependencies
RUN apt-get update \
    && apt-get install -y --fix-missing --no-install-recommends \
        ca-certificates \
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
    && curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0 --runtime aspnetcore \
    && LATEST_RELEASE_URL=$(curl -s https://api.github.com/repos/DroneDB/DroneDB/releases/latest | grep "browser_download_url.*deb" | head -n 1 | cut -d '"' -f 4) \
    && echo "Downloading DroneDB from $LATEST_RELEASE_URL" \
    && curl -L -o dronedb.deb "$LATEST_RELEASE_URL" \
    && apt-get install -y ./dronedb.deb \
    && rm dronedb.deb \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Add dotnet to PATH
ENV PATH="/root/.dotnet:$PATH"
ENV DOTNET_ROOT="/root/.dotnet"
# Ensure SkiaSharp can find native libraries
ENV LD_LIBRARY_PATH="/usr/lib/x86_64-linux-gnu:/usr/lib:/lib"

# Copy compiled Registry
COPY --from=dotnet-builder /Registry/Registry.Web/bin/Release/net10.0/publish/ /Registry

RUN chmod +x /Registry/Registry.Web && mkdir -p /data/temp && chmod 777 /data /data/temp

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

# Run registry
ENTRYPOINT ["sh", "-c", "./Registry/Registry.Web --address 0.0.0.0:5000 --instance-type ${INSTANCE_TYPE} /data"]