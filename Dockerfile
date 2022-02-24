FROM ubuntu:focal as builder

LABEL Author="Luca Di Leo <ldileo@digipa.it>"

# Prerequisites
ENV TZ=Europe/Rome
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone
RUN apt update && apt install -y --fix-missing --no-install-recommends build-essential software-properties-common
RUN add-apt-repository -y ppa:ubuntugis/ubuntugis-unstable
RUN apt install -y --fix-missing --no-install-recommends ca-certificates cmake git checkinstall sqlite3 spatialite-bin libgeos-dev libgdal-dev g++-10 gcc-10 pdal libpdal-dev libzip-dev
RUN update-alternatives --install /usr/bin/gcc gcc /usr/bin/gcc-10 1000 --slave /usr/bin/g++ g++ /usr/bin/g++-10
RUN apt install -y curl && curl -L https://github.com/DroneDB/libnexus/releases/download/v1.0.0/nxs-ubuntu-20.04-amd64.deb --output /tmp/nxs-ubuntu-20.04-amd64.deb && \
    dpkg-deb -x /tmp/nxs-ubuntu-20.04-amd64.deb /usr && \
    rm /tmp/nxs-ubuntu-20.04-amd64.deb && \
    apt remove -y curl

# Build DroneDB
RUN git clone --recurse-submodules https://github.com/DroneDB/DroneDB.git
RUN cd DroneDB && mkdir build && cd build && \
    cmake .. && \
    make -j $(cat /proc/cpuinfo | grep processor | wc -l)
RUN cd /DroneDB/build && checkinstall --install=no --pkgname DroneDB --default

# ---> Dotnet build stage
FROM mcr.microsoft.com/dotnet/sdk:6.0-focal as dotnet-builder

# Copy registry
COPY . /Registry

# Copy publish profile
COPY docker/FolderProfile.xml /Registry/Registry.Web/Properties/PublishProfiles/FolderProfile.pubxml

# Publish Registry
RUN cd /Registry/Registry.Web && dotnet dev-certs https && dotnet publish --configuration Release /p:PublishProfile=FolderProfile

# ---> Node build stage
FROM node:14-buster as node-builder

# Copy Hub
COPY ./Registry.Web/ClientApp /ClientApp

# Compile client app
RUN npm install -g webpack@4 webpack-cli
RUN cd /ClientApp && npm install && webpack --mode=production 

# ---> Dotnet stage run
FROM mcr.microsoft.com/dotnet/aspnet:6.0-focal as runner

ENV DOTNET_GENERATE_ASPNET_CERTIFICATE=false

RUN apt update && apt install -y --fix-missing --no-install-recommends gnupg2 && \
    echo "deb https://ppa.launchpadcontent.net/ubuntugis/ubuntugis-unstable/ubuntu focal main" >> /etc/apt/sources.list && \
    echo "deb-src https://ppa.launchpadcontent.net/ubuntugis/ubuntugis-unstable/ubuntu focal main" >> /etc/apt/sources.list && \
    apt-key adv --keyserver keyserver.ubuntu.com --recv-keys 6b827c12c2d425e227edca75089ebe08314df160 && \
    apt-get update && apt-get install -y curl libspatialite7 libgdal30 libzip5 libpdal-base12 libgeos3.10.1 && \
    curl -L https://github.com/DroneDB/libnexus/releases/download/v1.0.0/nxs-ubuntu-20.04-amd64.deb --output /tmp/nxs-ubuntu-20.04-amd64.deb && \
    dpkg-deb -x /tmp/nxs-ubuntu-20.04-amd64.deb /usr && \
    rm /tmp/nxs-ubuntu-20.04-amd64.deb && \
    apt remove -y gnupg2 curl && \
    apt-get autoremove -y && \
    rm -rf /var/lib/apt/lists/*

# Install DroneDB from deb package and set library path
COPY --from=builder /DroneDB/build/*.deb /
RUN dpkg -i *.deb
ENV LD_LIBRARY_PATH="/usr/local/lib:${LD_LIBRARY_PATH}"

# Copy compiled Registry
COPY --from=dotnet-builder /Registry/Registry.Web/bin/Release/net6.0/linux-x64/ /Registry

# Copy compiled client app in the appropriate folder
RUN mkdir -p /Registry/ClientApp/build
COPY --from=node-builder /ClientApp/build/ /Registry/ClientApp/build

EXPOSE 5000/tcp
VOLUME [ "/Registry/App_Data" ]

WORKDIR /Registry

# Run registry
ENTRYPOINT dotnet Registry.Web.dll --urls="http://0.0.0.0:5000"