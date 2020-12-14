FROM mcr.microsoft.com/dotnet/sdk:5.0-focal as builder

LABEL Author="Luca Di Leo <ldileo@digipa.it>"

ENV TZ=Europe/Rome
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

RUN apt update && apt install -y --fix-missing --no-install-recommends build-essential software-properties-common
RUN add-apt-repository -y ppa:ubuntugis/ubuntugis-unstable
RUN apt install -y --fix-missing --no-install-recommends ca-certificates cmake git sqlite3 spatialite-bin libgeos-dev libgdal-dev g++-10 gcc-10
RUN update-alternatives --install /usr/bin/gcc gcc /usr/bin/gcc-10 1000 --slave /usr/bin/g++ g++ /usr/bin/g++-10

# Build DroneDB
RUN git clone --recurse-submodules https://github.com/uav4geo/DroneDB.git && \
    cd DroneDB && \
    mkdir build && \
    cd build && \
    cmake .. && make -j $(cat /proc/cpuinfo | grep processor | wc -l) 
    #make install

# Set ENV variables to let Registry find ddb libs
ENV PATH="/DroneDB/build:${PATH}"
ENV LD_LIBRARY_PATH="/DroneDB/build:${LD_LIBRARY_PATH}"

# Clone Registry
RUN cd / && git clone --recurse-submodules https://github.com/DroneDB/Registry.git

# Copy publish profile
RUN mkdir /Registry/Registry.Web/Properties/PublishProfiles
COPY FolderProfile.xml /Registry/Registry.Web/Properties/PublishProfiles/FolderProfile.pubxml

# Publish
RUN cd /Registry/Registry.Web && dotnet publish --configuration Release /p:PublishProfile=FolderProfile
#RUN ls -l /Registry/Registry.Web/bin/Release/net5.0/linux-x64

EXPOSE 5000/tcp
EXPOSE 5001/tcp

#RUN apt install -y --fix-missing --no-install-recommends libexiv2-dev

#ENV LD_DEBUG=libs 

WORKDIR /Registry/Registry.Web/bin/Release/net5.0/linux-x64
ENTRYPOINT dotnet Registry.Web.dll --urls="http://0.0.0.0:5000;https://0.0.0.0:5001"


