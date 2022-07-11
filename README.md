# DroneDB Registry

![commits](https://img.shields.io/github/commit-activity/m/DroneDB/registry) ![languages](https://img.shields.io/github/languages/top/DroneDB/registry) ![.NET Core](https://github.com/DroneDB/Registry/workflows/.NET%20Core/badge.svg?branch=master)

DroneDB Registry is a simple, user-friendly aerial data management and storage application. It features JWT authentication and implements a full REST API. 

Combined with [Hub](https://github.com/DroneDB/Hub), it provides a simple, fast and reliable platform for hosting and sharing geospatial images and data.
It also allows you to view orthophotos, point clouds and 3d models (obj) easily and effortlessly directly in the browser.

### Orthophoto and flight path

![orthophoto](https://user-images.githubusercontent.com/7868983/152324827-d16949b8-dd96-4d3a-b5c5-a732e999f070.png)

### Files with previews

![files](https://user-images.githubusercontent.com/7868983/152324902-abfe0910-6115-46c5-b561-59bc5a417dda.png)

### Point cloud interactive view

![point-cloud](https://user-images.githubusercontent.com/7868983/152324757-4ee73f71-bf8e-4c72-9910-7073a68daee3.png)

### Example repositories

- [Brighton Beach](https://hub.dronedb.app/r/hedo88/brighton-beach)
- [ODM Seneca](https://hub.dronedb.app/r/hedo88/odm-seneca)
- [ODM Sance](https://hub.dronedb.app/r/hedo88/odm-sance)
- [Panorama Example](https://hub.dronedb.app/r/pierotofy/panoexample/)

## Getting started with Docker

To get started, download [Docker](https://www.docker.com/community-edition) and install it. Then run this command:


```
docker run -it --rm -p 5000:5000 -v ${PWD}/registry-data:/data dronedb/registry
```

The data will be stored in the local folder `registry-data`.
Open https://localhost:5000 in your browser to start using the application.

Default credentials are `admin` and `password`. 

Useful links:
 - Swagger: http://localhost:5000/swagger
 - Version: http://localhost:5000/version
 - (req auth) Quick Health: http://localhost:5000/quickhealth
 - (req auth) Health: http://localhost:5000/health
 - (req auth) Hangfire: http://localhost:5000/hangfire

The log file is located in `registry-data/logs/registry.txt`.

## Getting started natively

You need to install the latest version of the [DroneDB library](https://github.com/DroneDB/DroneDB/releases/latest) and add it to PATH. 

Download the [latest release](https://github.com/DroneDB/Registry/releases/latest) for your platform and run the following command:

```bash
./Registry.Web ./registry-data
```

There are several other command line options:

```
-a, --address              (Default: localhost:5000) Address to listen on
-c, --check                Check configuration and exit.
-r, --reset-hub            Reset the Hub folder by re-creating it.
--help                     Display this help screen.
--version                  Display version information.
Storage folder (pos. 0)    Required. Points to a directory on a filesystem where to store Registry data.
```

> **_NOTE:_**  This configuration uses sqlite as database. It is for local testing only. If you want to use the application in a heavy load environment, check the following section.

### Change admin password

Go to https://localhost:5000/account to change password.
Otherwise, you can change the admin password by changing the value of the field `DefaultAdmin.Password` in the `appsettings.json` file. After changing the password you need to restart the application.

## Running with docker-compose

```bash
cd docker/testing
docker-compose up -d
```

The stack is composed of:
 - MariaDB database
 - PHPMyAdmin, exposed on port [8080](http://localhost:8080)
 - Registry, exposed on port [5000](http://localhost:5000)

## Running in production

You will need [Git](https://git-scm.com/downloads). Clone the repo and initialize submodules:

```bash
git clone https://github.com/DroneDB/Registry
cd Registry
git submodule update --init --recursive
```

And then run the following commands:

### Linux

```bash
cd docker/production
chmod +x run.sh
./run.sh
```

### Windows

```bash
cd docker/production
run.bat
```

Check that everything is running smoothly:

```bash
docker-compose ps
docker-compose logs -f
```

When all the containers are running, you can then open http://localhost:5000 in your browser, use `admin:password` as default credentials.

You can stop the application by issuing:

```bash
docker-compose down
```

The `run.sh` / `run.bat` script will create the default `appsettings.json` file, the database initialization script and start the Docker containers.

It is possible to customize the startup settings by creating a `.env` file in the same folder. Here's an example:

### Linux (quotes are important)
```bash
MYSQL_ROOT_PASSWORD="default-root-password"
MYSQL_PASSWORD="default-mysql-password"
REGISTRY_ADMIN_MAIL="test@test.it"
REGISTRY_ADMIN_PASSWORD="password"
REGISTRY_SECRET="longandrandomsecrettobegeneratedusingcryptographicallystrongrandomnumbergenerator"
EXTERNAL_URL=""
CONTROL_SWITCH='$controlSwitch'
```

### Windows (values without quotes)
```batch
MYSQL_ROOT_PASSWORD=default-root-password
MYSQL_PASSWORD=default-mysql-password
REGISTRY_ADMIN_MAIL=test@test.it
REGISTRY_ADMIN_PASSWORD=password
REGISTRY_SECRET=longandrandomsecrettobegeneratedusingcryptographicallystrongrandomnumbergenerator
EXTERNAL_URL=
CONTROL_SWITCH=$controlSwitch
```

If you want to reduce the log verbosity, you can change `"Information"` to `"Warning"` in `appsettings.json`:

```json
    "LevelSwitches": {
        "$CONTROL_SWITCH": "Warning"
    }
```

then run

```
docker-compose restart registry
````

> **_Info:_** Any changes to the configuration file need to restart the registry container  

## Build Docker image

If you want to build the image from scratch, you can use the following commands:

```bash
git clone https://github.com/DroneDB/Registry
cd Registry
git submodule update --init --recursive
docker build . -t dronedb/registry
```

## Running from source

`Registry` is written in C# on .NET Core 6 platform and runs natively on both Linux and Windows.
To install the latest .NET SDK see the [official download page](https://dotnet.microsoft.com/en-us/download/dotnet/6.0). Before building registry ensure you have `ddblib` in your path, if not, download the [latest release](https://github.com/DroneDB/DroneDB/releases) and add it to `PATH`.

Clone the repository:

```bash
git clone https://github.com/DroneDB/Registry
cd Registry
git submodule update --init --recursive
```

Build the Hub interface (need [NodeJS 14+](https://nodejs.org/download/release/v14.18.3/)):

```bash
cd Registry.Web/ClientApp
npm install -g webpack@4
npm install
webpack
```

Build the solution from the command line:

```bash
dotnet build
```

Run the tests to make sure the project is working correctly:

```bash
dotnet test
```

Then you can run the application:

```bash
dotnet run --project Registry.Web ./registry-data
```

## Updating

In order to update the application, you need to replace the executable with the latest version. It will perform the required migrations and update the database at the next startup.

With docker or docker-compose, you update the application by pulling the latest image and restarting the container:

```bash
docker-compose down
docker-compose pull
docker-compose up -d 
```

## Project architecture

![dronedb-registry-architecture](https://user-images.githubusercontent.com/7868983/151846022-891685f7-ef47-4b93-8199-d4ac4e788c5d.png)

## Note

DroneDB Registry is under development and is targeted at GIS developers and tech enthusiasts. To contribute to the project, please see the [contributing guidelines](CONTRIBUTING.md).
