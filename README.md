# DroneDB Registry

![commits](https://img.shields.io/github/commit-activity/m/DroneDB/registry) ![languages](https://img.shields.io/github/languages/top/DroneDB/registry) ![.NET Core](https://github.com/DroneDB/Registry/workflows/.NET%20Core/badge.svg?branch=master)

DroneDB Registry is a simple, user-friendly aerial data management and storage application. It features JWT authentication and implements a full REST API. 

Combined with [Hub](https://github.com/DroneDB/Hub), it provides a simple, fast and scalable platform for hosting and sharing geospatial images and data.
It also allows you to view orthophotos and point clouds easily and effortlessly directly in the browser.

### Orthophoto and flight path

![orthophoto](https://user-images.githubusercontent.com/7868983/152324827-d16949b8-dd96-4d3a-b5c5-a732e999f070.png)

### Files with previews

![files](https://user-images.githubusercontent.com/7868983/152324902-abfe0910-6115-46c5-b561-59bc5a417dda.png)

### Point cloud interactive view

![point-cloud](https://user-images.githubusercontent.com/7868983/152324757-4ee73f71-bf8e-4c72-9910-7073a68daee3.png)

## Getting started

To get started, you need to install the following applications (if they are not installed already):

  - [Git](https://git-scm.com/downloads)
  - [Docker](https://www.docker.com/)
  - [Docker-compose](https://docs.docker.com/compose/install/)

Clone the repo and initialize submodules:

```bash
git clone https://github.com/DroneDB/Registry
cd Registry
git submodule update --init --recursive
```

And then run the following commands:

### Linux

```bash
cd docker/production
./run.sh
```

### Windows

```bash
*** Coming soon ***
```

Check that everything is running smoothly:

```bash
docker-compose ps
```

When all the containers are running, you can then open http://localhost:5000 in your browser, use `admin:password` as default credentials.

You can stop the application by issuing:

```bash
docker-compose down
```

The `run.sh` script will create the default `appsettings.json` file, the database initialization script and start the Docker containers.
It is possible to customize the startup settings by creating a `.env` file in the same folder. Here it is an example:

```bash
MYSQL_ROOT_PASSWORD="default-root-password"
MYSQL_PASSWORD="default-mysql-password"
REGISTRY_ADMIN_MAIL="test@test.it"
REGISTRY_ADMIN_PASSWORD="password"
REGISTRY_SECRET="longandrandomsecrettobegeneratedusingcryptographicallystrongrandomnumbergenerator"
EXTERNAL_URL=""
CONTROL_SWITCH="$controlSwitch"
```



### Standalone installation with docker (only for testing)

The following steps start a new instance of `registry` with the default configuration and `SQLite` as backend database. They work both on linux and windows (powershell):

```bash
wget -O appsettings.json https://raw.githubusercontent.com/DroneDB/Registry/master/Registry.Web/appsettings-default.json

docker run -it --rm -p 5000:5000 -v ${PWD}/registry-data:/Registry/App_Data -v ${PWD}/appsettings.json:/Registry/appsettings.json dronedb/registry:latest
```

> `Registry` can use `SQLite`, `MySQL` (`MariaDB`) or `SQL Server` as a database. Nevertheless, the application is primarily designed to be used with `MariaDB`. There are no migration scripts for the other databases, so you have to manually upgrade the schema between versions. The above steps are for test only, and should not be used in production.

## Build Docker image

If you want to build the image from scratch, you can use the following commands:

```bash
git clone https://github.com/DroneDB/Registry
cd Registry
git submodule update --init --recursive
docker build . -t dronedb/registry
```

Notes:
- `ddb` commands must use the `127.0.0.1` syntax, not `localhost`

## Building Natively

Registry is written in C# on .NET Core 6 platform and runs natively on both Linux and Windows.
To install the latest .NET SDK see the [official download page](https://dotnet.microsoft.com/en-us/download/dotnet/6.0). Before building registry ensure you have `ddblib` in your path, if not download the [latest release](https://github.com/DroneDB/DroneDB/releases) and add it to `PATH`.

Clone the repository:

```bash
git clone https://github.com/DroneDB/Registry
cd Registry
git submodule update --init --recursive
```

Build the solution from the command line:

```bash
dotnet build
```

Run the tests to make sure the project is working correctly:

```bash
dotnet test
```

Then build the Hub interface (need [NodeJS 14+](https://nodejs.org/download/release/v14.18.3/)):

```bash
cd Registry.Web/ClientApp
npm install -g webpack@4
npm install
webpack
```

## Running Natively

```bash
dotnet run --project Registry.Web
```

It will start a web server listening on two endpoints: `https://localhost:5001` and `http://localhost:5000`. 
You can change the endpoints using the `urls` parameter:

```bash
dotnet run --project Registry.Web --urls="http://0.0.0.0:6000;https://0.0.0.0:6001"
```

## Project architecture

![dronedb-registry-architecture](https://user-images.githubusercontent.com/7868983/151846022-891685f7-ef47-4b93-8199-d4ac4e788c5d.png)

## Note

DroneDB Registry is in early development stages and is targeted at GIS developers and early adopters. It is not ready for mainstream use. To contribute to the project, please see the [contributing guidelines](CONTRIBUTING.md).
