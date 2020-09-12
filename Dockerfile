FROM mcr.microsoft.com/dotnet/core/aspnet:3.1
MAINTAINER Piero Toffanin <pt@uav4geo.com>
ENV DEBIAN_FRONTEND noninteractive
ENV DOTNET_CLI_TELEMETRY_OPTOUT 1

# Instal ddb
RUN curl -fsSL https://get.dronedb.app -o get-ddb.sh && sh get-ddb.sh

COPY . /registry

# TODO: @Hedo88TH appsettings.json should be in the bin directory?
# I shouldn't have to modify /registry/Registry.Web/appsettings.json
# let's improve this.

#RUN rm /registry/Registry.Web/appsettings.json && ln -s /registry/docker/appsettings.json /registry/Registry.Web/appsettings.json
WORKDIR /registry

ENTRYPOINT ["/usr/bin/dotnet", "run", "--project", "Registry.Web"]
