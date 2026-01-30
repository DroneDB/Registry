# DroneDB Registry

![GitHub Release](https://img.shields.io/github/v/release/DroneDB/Registry) ![commits](https://img.shields.io/github/commit-activity/m/DroneDB/registry) ![languages](https://img.shields.io/github/languages/top/DroneDB/registry) ![.NET Core](https://github.com/DroneDB/Registry/workflows/.NET%20Core/badge.svg?branch=master)

DroneDB Registry is a comprehensive geospatial data management and storage platform. It provides JWT authentication, a full REST API, and STAC compliance for interoperability.

View orthophotos, point clouds, 3D models (OBJ, GLTF, GLB), panoramas and more directly in the browser with interactive measurement tools.

## 📚 Documentation

**Full documentation is available at [docs.dronedb.app](https://docs.dronedb.app)**

| Guide | Description |
|-------|-------------|
| [Registry Guide](https://docs.dronedb.app/docs/registry) | Installation, configuration, deployment |
| [User Management](https://docs.dronedb.app/docs/user-management) | Users, roles, organizations, quotas |
| [API Reference](https://docs.dronedb.app/docs/api-reference) | REST API documentation |

## ✨ Features

- **Dataset Management** - Create, organize and share datasets with fine-grained permissions
- **Interactive Visualization** - View orthophotos, point clouds, 3D models and panoramas in browser
- **Measurements** - 2D and 3D measurement tools on maps and point clouds
- **STAC Compliance** - Standard SpatioTemporal Asset Catalog API
- **On-Demand Processing** - Automatic thumbnails, tiles, COG and streaming format generation
- **User Management** - Role-based access control with organizations and storage quotas

### Supported Formats

| Category | Formats |
|----------|---------|
| Images | JPG, JPEG, DNG, TIF, TIFF, PNG, GIF, WEBP |
| Point Clouds | LAS, LAZ, PLY |
| 3D Models | OBJ, GLTF, GLB, PLY |
| Rasters | GeoTIFF (orthophotos, DEMs) |
| Vector | GeoJSON, SHP, KML, KMZ, DXF, DWG, GPKG |
| Other | Panoramas (360°), Videos (MP4, MOV), Markdown, PDF |

### Live Examples

- [Brighton Beach](https://hub.dronedb.app/r/hedo88/brighton-beach) - Point cloud
- [ODM Seneca](https://hub.dronedb.app/r/hedo88/odm-seneca) - Orthophoto with measurements
- [Panorama Example](https://hub.dronedb.app/r/pierotofy/panoexample/) - 360° panorama viewer

## 🚀 Quick Start with Docker

```bash
docker run -it --rm -p 5000:5000 -v ${PWD}/registry-data:/data dronedb/registry
```

Open http://localhost:5000 • Default credentials: `admin` / `password`

> ⚠️ Change the default password immediately at http://localhost:5000/account

### Useful Endpoints

| Endpoint | Description |
|----------|-------------|
| `/scalar/v1` | API Documentation |
| `/stac` | STAC Catalog |
| `/hangfire` | Background jobs dashboard (requires auth) |

For production deployment with MySQL/MariaDB, see the [full documentation](https://docs.dronedb.app/docs/registry#running-in-production).

## 🛠️ Development

### Requirements

- [.NET 9.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
- [Node.js 22+](https://nodejs.org/) (LTS recommended)
- [DroneDB Library](https://github.com/DroneDB/DroneDB/releases/latest) (add to PATH)

### Build from Source

```bash
git clone https://github.com/DroneDB/Registry
cd Registry
git submodule update --init --recursive

# Build Vue.js frontend
cd Registry.Web/ClientApp
npm install
npm run build
cd ../..

# Build and run
dotnet build
dotnet run --project Registry.Web ./registry-data
```

### Run Tests

```bash
dotnet test
```

## 🐳 Docker Build

```bash
docker build . -t dronedb/registry
```

## 📄 License

This project is dual-licensed. See [LICENSE.md](LICENSE.md) for details.

## 🤝 Contributing

Contributions are welcome! Please see the [contributing guidelines](CONTRIBUTING.md).
