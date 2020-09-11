FROM debian:buster
MAINTAINER Piero Toffanin <pt@uav4geo.com>
ENV DEBIAN_FRONTEND noninteractive
ENV DOTNET_CLI_TELEMETRY_OPTOUT 1

#install dependencies
RUN apt-get update && apt-get install  -y --fix-missing --no-install-recommends\
    apt-transport-https \	
	build-essential \
    ca-certificates \
    cmake \
    git \
    sqlite3 \
    spatialite-bin \
	libsqlite3-mod-spatialite \
    exiv2 \
    libexiv2-dev \
    libgeos-dev \
    libgdal-dev \
    wget \
	python \
	python-pip \
	python-setuptools \
	python-wheel \
	python-dev \
	curl

# Install dotnet

RUN wget https://packages.microsoft.com/config/debian/10/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb && \
    dpkg -i /tmp/packages-microsoft-prod.deb && \
    apt-get update && \
    apt-get install -y dotnet-sdk-3.1

# Exodus
RUN pip install --user exodus wheel setuptools

# Instal ddb
RUN curl -fsSL https://get.dronedb.app -o get-ddb.sh && sh get-ddb.sh

COPY . /registry

# TODO: @Hedo88TH appsettings.json should be in the bin directory?
# I shouldn't have to modify /registry/Registry.Web/appsettings.json
# let's improve this.

#RUN rm /registry/Registry.Web/appsettings.json && ln -s /registry/docker/appsettings.json /registry/Registry.Web/appsettings.json
WORKDIR /registry

RUN apt-get clean && rm -r /tmp/*

ENTRYPOINT ["/usr/bin/dotnet", "run", "--project", "Registry.Web"]
