FROM ubuntu:20.04
MAINTAINER Piero Toffanin <pt@uav4geo.com>
ENV DEBIAN_FRONTEND noninteractive

#install dependencies
RUN apt update && apt install  -y --fix-missing --no-install-recommends\
    build-essential \
    ca-certificates \
    apt-transport-https \
    cmake \
    git \
    sqlite3 \
    spatialite-bin \
    exiv2 \
    libexiv2-dev \
    libgeos-dev \
    libgdal-dev \
    wget

# Install dotnet

RUN wget https://packages.microsoft.com/config/debian/10/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb && \
    dpkg -i /tmp/packages-microsoft-prod.deb && \
    apt update && \
    apt install -y dotnet-sdk-3.1

RUN git clone --recurse-submodules https://github.com/DroneDB/DroneDB /ddb && \
    cd /ddb && \
    mkdir build && \
    cd build && \
    cmake .. && make -j$(nproc) && make install

COPY . /registry

# TODO: @Hedo88TH appsettings.json should be in the bin directory?
# I shouldn't have to modify /registry/Registry.Web/appsettings.json
# let's improve this.

RUN rm /registry/Registry.Web/appsettings.json && ln -s /registry/docker/appsettings.json /registry/Registry.Web/appsettings.json
WORKDIR /registry

RUN apt clean && rm -r /tmp/*

ENTRYPOINT ["/usr/bin/dotnet", "run", "--project", "Registry.Web"]
