FROM ubuntu:jammy as builder

LABEL Author="Luca Di Leo <ldileo@digipa.it>"

# Prerequisites
ENV TZ=Europe/Rome
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone
RUN apt-get update && apt-get install -y --fix-missing --no-install-recommends git curl software-properties-common gpg-agent checkinstall

# Build DroneDB
RUN git clone --recurse-submodules https://github.com/DroneDB/DroneDB.git
RUN DroneDB/scripts/ubuntu_deps.sh
RUN cd DroneDB && mkdir build && cd build && \
    cmake .. && \
    make -j $(cat /proc/cpuinfo | grep processor | wc -l)
RUN cd /DroneDB/build && checkinstall --install=no --pkgname DroneDB --default

# ---> Dotnet build stage + nodejs build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0-jammy as dotnet-builder

RUN curl -sL https://deb.nodesource.com/setup_16.x | bash -
RUN apt-get install -y nodejs

# Copy registry
COPY . /Registry

# Compile client app
RUN npm install -g webpack@4 webpack-cli
RUN cd /Registry/Registry.Web/ClientApp && npm install && webpack --mode=production 

# Copy publish profile
COPY docker/FolderProfile.xml /Registry/Registry.Web/Properties/PublishProfiles/FolderProfile.pubxml

# Publish Registry
RUN cd /Registry/Registry.Web && dotnet dev-certs https && dotnet publish -p:PublishProfile=FolderProfile

# ---> Dotnet stage run
FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy as runner

ENV DOTNET_GENERATE_ASPNET_CERTIFICATE=false

RUN apt-get update && apt-get install -y --fix-missing --no-install-recommends gnupg2 && \
    echo "deb https://ppa.launchpadcontent.net/ubuntugis/ubuntugis-unstable/ubuntu jammy main" >> /etc/apt/sources.list && \
    echo "deb-src https://ppa.launchpadcontent.net/ubuntugis/ubuntugis-unstable/ubuntu jammy main" >> /etc/apt/sources.list && \
    apt-key adv --keyserver keyserver.ubuntu.com --recv-keys 6b827c12c2d425e227edca75089ebe08314df160 && \
    apt-get update && apt-get install -y curl libspatialite7 libgdal32 libzip4 libpdal-base13 libgeos3.10.2 && \
    curl -L https://github.com/DroneDB/libnexus/releases/download/v1.0.0/nxs-ubuntu-22.04-amd64.deb --output /tmp/nxs-ubuntu-22.04-amd64.deb && \
    dpkg-deb -x /tmp/nxs-ubuntu-22.04-amd64.deb /usr && \
    rm /tmp/nxs-ubuntu-22.04-amd64.deb && \
    apt-get remove -y gnupg2 curl && \
    apt-get autoremove -y && \
    rm -rf /var/lib/apt/lists/*

# Install DroneDB from deb package and set library path
COPY --from=builder /DroneDB/build/*.deb /
RUN dpkg -i *.deb && rm *.deb
ENV LD_LIBRARY_PATH="/usr/local/lib:${LD_LIBRARY_PATH}"

# Copy compiled Registry
COPY --from=dotnet-builder /Registry/Registry.Web/bin/Release/net8.0/publish/ /Registry

RUN chmod +x /Registry/Registry.Web && mkdir /data && chmod 777 /data

EXPOSE 5000/tcp
VOLUME [ "/data" ]

# Run registry
ENTRYPOINT ./Registry/Registry.Web --address 0.0.0.0:5000 /data