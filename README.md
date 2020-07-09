# DroneDB Registry

![license](https://img.shields.io/github/license/DroneDB/registry) ![commits](https://img.shields.io/github/commit-activity/m/DroneDB/registry) ![languages](https://img.shields.io/github/languages/top/DroneDB/registry)

DroneDB Registry is a useful tool to remotely store DroneDB datasets. It features JWT authentication and implements a full REST API. 

To learn more check the wiki article: [REST Interface Specification](https://github.com/DroneDB/registry/wiki/REST-Interface-Specification)

## Project architecture

![dronedb-registry-architecture](https://user-images.githubusercontent.com/7868983/87065148-f4c46b80-c210-11ea-9f68-3e2dd13687bf.jpg)

## Building

```
dotnet build
```

## Running

```
dotnet run --project DroneDB.Registry.Web
```
It will start a web server listening on two endpoints: `https://localhost:5001` and `http://localhost:5000`

## Note

DroneDB Registry is in early development stages and is targeted at GIS developers and early adopters. It is not ready for mainstream use. To contribute to the project, please see the [contributing guidelines](CONTRIBUTING.md).
