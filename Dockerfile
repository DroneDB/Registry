FROM ubuntu:focal as builder

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
FROM ubuntu:focal as dotnet-builder

# Prerequisites
ENV TZ=Europe/Rome
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone
RUN apt-get update && apt-get install -y --fix-missing --no-install-recommends git curl software-properties-common gpg-agent wget

# Install dotnet and nodejs
RUN wget https://packages.microsoft.com/config/ubuntu/20.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
RUN dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb
RUN apt-get update && apt-get install -y dotnet-sdk-8.0
RUN curl -sL https://deb.nodesource.com/setup_16.x | bash -
RUN apt-get install -y nodejs

# Copy registry
COPY . /Registry

# Compile client app
RUN npm install -g webpack@4 webpack-cli
RUN cd /Registry/Registry.Web/ClientApp && npm install && webpack --mode=production

# Publish Registry
COPY docker/FolderProfile.xml /Registry/Registry.Web/Properties/PublishProfiles/FolderProfile.pubxml
RUN cd /Registry/Registry.Web && dotnet dev-certs https && dotnet publish -p:PublishProfile=FolderProfile

# ---> Dotnet stage run
FROM ubuntu:focal as runner

# Prerequisites
ENV DOTNET_GENERATE_ASPNET_CERTIFICATE=false
ENV TZ=Europe/Rome
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

# Install dotnet runtime
RUN apt-get update && apt-get install -y --fix-missing --no-install-recommends ca-certificates wget
RUN wget https://packages.microsoft.com/config/ubuntu/20.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
RUN dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb
RUN apt-get update && apt-get install -y aspnetcore-runtime-8.0

# Install DroneDB dependencies
RUN apt update && apt install -y --fix-missing --no-install-recommends gnupg2 && \
    echo "deb https://ppa.launchpadcontent.net/ubuntugis/ubuntugis-unstable/ubuntu focal main" >> /etc/apt/sources.list && \
    echo "deb-src https://ppa.launchpadcontent.net/ubuntugis/ubuntugis-unstable/ubuntu focal main" >> /etc/apt/sources.list && \
    apt-key adv --keyserver keyserver.ubuntu.com --recv-keys 6b827c12c2d425e227edca75089ebe08314df160 && \
    apt-get update && apt-get install -y curl libspatialite7 libgdal30 libzip5 libpdal-base12 libgeos3.10.2 && \
    curl -L https://github.com/DroneDB/libnexus/releases/download/v1.0.0/nxs-ubuntu-20.04-amd64.deb --output /tmp/nxs-ubuntu-20.04-amd64.deb && \
    dpkg-deb -x /tmp/nxs-ubuntu-20.04-amd64.deb /usr && \
    rm /tmp/nxs-ubuntu-20.04-amd64.deb && \
    apt remove -y gnupg2 curl && \
    apt-get autoremove -y && \
    rm -rf /var/lib/apt/lists/*

# Install DroneDB from deb package and set library path
COPY --from=builder /DroneDB/build/*.deb /
RUN dpkg -i *.deb && rm *.deb
ENV LD_LIBRARY_PATH="/usr/local/lib:${LD_LIBRARY_PATH}"

# Copy compiled Registry
COPY --from=dotnet-builder /Registry/Registry.Web/bin/Release/net9.0/publish/ /Registry

RUN chmod +x /Registry/Registry.Web && mkdir /data && chmod 777 /data

EXPOSE 5000/tcp
VOLUME [ "/data" ]

# Set default instance type
ENV INSTANCE_TYPE=0

# Run registry
ENTRYPOINT ./Registry/Registry.Web --address 0.0.0.0:5000 --instance-type ${INSTANCE_TYPE} /data