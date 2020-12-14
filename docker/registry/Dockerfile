FROM mcr.microsoft.com/dotnet/sdk:5.0-focal as builder

LABEL Author="Luca Di Leo <ldileo@digipa.it>"

ENV TZ=Europe/Rome
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

# Install DroneDB dependencies
#RUN apt update && apt install -y --fix-missing \
    #--no-install-recommends \
#    build-essential \
#    ca-certificates \
#    cmake \
    # git \
#    sqlite3 \
#    spatialite-bin \
    #exiv2 \
    #libexiv2-dev \
#    libgeos-dev \
#    libgdal-dev
    #apt-transport-https 
    #\
    #wget

RUN apt update && apt install -y --fix-missing --no-install-recommends \
    software-properties-common \
    build-essential sudo gpg-agent

RUN add-apt-repository ppa:ubuntugis/ubuntugis-unstable

RUN curl -sL https://deb.nodesource.com/setup_14.x | sudo -E bash - 
RUN apt install -y nodejs && \
    npm install -g cmake-js

RUN apt update && apt install -y --fix-missing --no-install-recommends \
    ca-certificates \
    cmake \
    git \
    sqlite3 \
    spatialite-bin \
    libgeos-dev \
    libgdal-dev \
    g++-10 \
    gcc-10

RUN update-alternatives --install /usr/bin/gcc gcc /usr/bin/gcc-10 1000 --slave /usr/bin/g++ g++ /usr/bin/g++-10

# Install .NET Core (not needed anymore)
#RUN wget https://packages.microsoft.com/config/ubuntu/20.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
#RUN dpkg -i packages-microsoft-prod.deb
#RUN apt update && apt install -y --fix-missing --no-install-recommends dotnet-sdk-5.0

# Build DroneDB
RUN git clone --recurse-submodules https://github.com/uav4geo/DroneDB.git && \
    cd DroneDB && \
    mkdir build && \
    cd build && \
    cmake .. && make -j $(cat /proc/cpuinfo | grep processor | wc -l)

# Clone Registry
RUN git clone --recurse-submodules https://github.com/DroneDB/Registry.git

# Copy publish profile
RUN mkdir /Registry/Registry.Web/Properties/PublishProfiles
COPY FolderProfile.pubxml /Registry/Registry.Web/Properties/PublishProfiles

# Publish
RUN cd Registry/Registry.Web && dotnet publish --configuration Release /p:PublishProfile=FolderProfile
RUN ls -l /Registry/Registry.Web/bin/Release/net5.0/linux-x64/

# --configuration Release --no-restore
#FROM mcr.microsoft.com/dotnet/sdk:5.0-focal
#FROM mcr.microsoft.com/dotnet/runtime:5.0-focal

# Install dependencies
#RUN apt update && apt install -y --fix-missing --no-install-recommends\
#    ca-certificates \
#    sqlite3 \
#    spatialite-bin \
#    libsqlite3-mod-spatialite \
#    libexiv2-27 libexpat1 \
#    libgeos-3.8.0 libgeos-c1v5 \
#    libgdal26 \
#    apt-transport-https

#RUN mkdir DroneDB && mkdir Registry

#COPY --from=builder /DroneDB/build/* /DroneDB/
#COPY --from=builder /Registry/Registry.Web/bin/Release/net5.0/linux-x64/* /Registry/

#RUN ls -l /Registry/
#RUN ls -l /DroneDB/

# Set ENV variables to let Registry find ddb libs
ENV PATH="/DroneDB/build:${PATH}"
ENV LD_LIBRARY_PATH="/DroneDB/build:${LD_LIBRARY_PATH}"

EXPOSE 5000/tcp
EXPOSE 5001/tcp

WORKDIR /Registry/Registry.Web/bin/Release/net5.0/linux-x64/
ENTRYPOINT dotnet Registry.Web.dll --urls="http://0.0.0.0:5000;https://0.0.0.0:5001"


