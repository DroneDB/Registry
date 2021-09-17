FROM ubuntu:focal as builder

LABEL Author="Luca Di Leo <ldileo@digipa.it>"

# Prerequisites
ENV TZ=Europe/Rome
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone
RUN apt update && apt install -y --fix-missing --no-install-recommends build-essential software-properties-common
RUN add-apt-repository -y ppa:ubuntugis/ubuntugis-unstable
RUN apt install -y --fix-missing --no-install-recommends ca-certificates cmake git checkinstall sqlite3 spatialite-bin libgeos-dev libgdal-dev g++-10 gcc-10 pdal libpdal-dev libzip-dev
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

# Copy registry
COPY . /Registry

# Compile client app
RUN npm install -g webpack@4 webpack-cli
RUN cd /Registry/Registry.Web/ClientApp && npm install && webpack --mode=production

# Copy publish profile
COPY docker/production/FolderProfile.xml /Registry/Registry.Web/Properties/PublishProfiles/FolderProfile.pubxml

# Publish Registry
RUN cd /Registry/Registry.Web && dotnet dev-certs https && dotnet publish --configuration Release /p:PublishProfile=FolderProfile

# Install DroneDB libraries (can be slimmed down somehow)
ENV TZ=Europe/Rome
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone
RUN apt update && apt install -y --fix-missing --no-install-recommends software-properties-common
RUN add-apt-repository -y ppa:ubuntugis/ubuntugis-unstable
RUN apt install -y --fix-missing --no-install-recommends ca-certificates sqlite3 spatialite-bin libgeos-dev libgdal-dev pdal libpdal-dev libzip-dev

# Install DroneDB from deb package and set library path
COPY --from=builder /DroneDB/build/*.deb /
RUN ls -l
RUN dpkg -i *.deb
ENV LD_LIBRARY_PATH="/usr/local/lib:${LD_LIBRARY_PATH}"

# Copy compiled client app in the appropriate folder
RUN mkdir -p /Registry/Registry.Web/bin/Release/net5.0/linux-x64/ClientApp/build
RUN cp -r /Registry/Registry.Web/ClientApp/build /Registry/Registry.Web/bin/Release/net5.0/linux-x64/ClientApp

EXPOSE 5000/tcp
EXPOSE 5001/tcp

WORKDIR /Registry/Registry.Web/bin/Release/net5.0/linux-x64

# Run registry
ENTRYPOINT dotnet Registry.Web.dll --urls="http://0.0.0.0:5000;https://0.0.0.0:5001"
