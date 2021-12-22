# DroneDB Registry

![commits](https://img.shields.io/github/commit-activity/m/DroneDB/registry) ![languages](https://img.shields.io/github/languages/top/DroneDB/registry) ![.NET Core](https://github.com/DroneDB/Registry/workflows/.NET%20Core/badge.svg?branch=master)

DroneDB Registry is a simple, user-friendly aerial data management and storage application . It features JWT authentication and implements a full REST API. 

To learn more check the wiki article: [REST Interface Specification](https://github.com/DroneDB/registry/wiki/REST-Interface-Specification)

## Project architecture

![dronedb-registry-architecture](https://user-images.githubusercontent.com/7868983/87065148-f4c46b80-c210-11ea-9f68-3e2dd13687bf.jpg)

## Running

You can use docker to setup Registry:

```
git clone https://github.com/DroneDB/Registry
cd Registry
git submodule update --init --recursive
docker build . -t dronedb/registry
docker run -ti -p 5000:5000 -p 5001:5001 dronedb/registry
```
Notes:
- login credentials are `admin:password`
- `ddb` commands must use the `127.0.0.1` syntax, not `localhost`

## Building Natively

```
dotnet build
```

## Running Natively

```
dotnet run --project Registry.Web
```
It will start a web server listening on two endpoints: `https://localhost:5001` and `http://localhost:5000`. 
You can change the endpoints using the `urls` parameter:

```
dotnet run --project Registry.Web --urls="http://0.0.0.0:6000;https://0.0.0.0:6001"
```

## Note

DroneDB Registry is in early development stages and is targeted at GIS developers and early adopters. It is not ready for mainstream use. To contribute to the project, please see the [contributing guidelines](CONTRIBUTING.md).
