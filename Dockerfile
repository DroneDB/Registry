FROM ubuntu:focal as builder

LABEL Author="Luca Di Leo <ldileo@digipa.it>"

# Prerequisites
ENV TZ=Europe/Rome
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone
RUN apt update && apt install -y --fix-missing --no-install-recommends build-essential software-properties-common
RUN add-apt-repository -y ppa:ubuntugis/ubuntugis-unstable
RUN apt install -y --fix-missing --no-install-recommends ca-certificates cmake git checkinstall sqlite3 spatialite-bin libgeos-dev libgdal-dev g++-10 gcc-10
RUN update-alternatives --install /usr/bin/gcc gcc /usr/bin/gcc-10 1000 --slave /usr/bin/g++ g++ /usr/bin/g++-10

# Build DroneDB
RUN git clone --recurse-submodules https://github.com/uav4geo/DroneDB.git
RUN cd DroneDB && mkdir build && cd build && \
    cmake .. && \
    make -j $(cat /proc/cpuinfo | grep processor | wc -l)
RUN cd /DroneDB/build && checkinstall --install=no --pkgname DroneDB --default

# ---> Dotnet stage
FROM mcr.microsoft.com/dotnet/sdk:5.0-focal as runner

# Install NodeJS
RUN apt update && apt install -y --fix-missing sudo gpg-agent curl lsb-release
RUN curl -sL https://deb.nodesource.com/setup_14.x | sudo -E bash -
RUN apt update && apt install -y --fix-missing nodejs

# Compile client app
#RUN git clone --recurse-submodules https://github.com/DroneDB/Hub.git
RUN npm install -g webpack@4 webpack-cli
#RUN cd /Hub && npm install && webpack

# Clone Registry
#RUN cd / && git clone --recurse-submodules https://github.com/DroneDB/Registry.git
COPY . /Registry

RUN cd /Registry/Registry.Web/ClientApp && npm install && webpack

# Copy publish profile
#RUN mkdir /Registry/Registry.Web/Properties/PublishProfiles
COPY docker/production/FolderProfile.xml /Registry/Registry.Web/Properties/PublishProfiles/FolderProfile.pubxml

# Publish Registry
RUN cd /Registry/Registry.Web && dotnet publish --configuration Release /p:PublishProfile=FolderProfile

# Install DroneDB libraries (can be slimmed down somehow)
ENV TZ=Europe/Rome
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone
RUN apt update && apt install -y --fix-missing --no-install-recommends software-properties-common
RUN add-apt-repository -y ppa:ubuntugis/ubuntugis-unstable
RUN apt install -y --fix-missing --no-install-recommends ca-certificates sqlite3 spatialite-bin libgeos-dev libgdal-dev

# Install DroneDB from deb package and set library path
COPY --from=builder /DroneDB/build/*.deb /
RUN ls -l
RUN dpkg -i *.deb
ENV LD_LIBRARY_PATH="/usr/local/lib:${LD_LIBRARY_PATH}"

# Copy compiled client app in the appropriate folder
#RUN mkdir -p /Registry/Registry.Web/bin/Release/net5.0/linux-x64/ClientApp/build
#COPY --from=builder /Hub/build /Registry/Registry.Web/bin/Release/net5.0/linux-x64/ClientApp/build

EXPOSE 5000/tcp
EXPOSE 5001/tcp

WORKDIR /Registry/Registry.Web/bin/Release/net5.0/linux-x64

# Copy config
COPY docker/production/appsettings.json .

# Run registry
ENTRYPOINT dotnet Registry.Web.dll --urls="http://0.0.0.0:5000;https://0.0.0.0:5001"
