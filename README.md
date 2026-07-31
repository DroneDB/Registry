# DroneDB Registry

![GitHub Release](https://img.shields.io/github/v/release/DroneDB/Registry) ![commits](https://img.shields.io/github/commit-activity/m/DroneDB/registry) ![languages](https://img.shields.io/github/languages/top/DroneDB/registry) ![.NET Core](https://github.com/DroneDB/Registry/actions/workflows/dotnet-core.yml/badge.svg) [![Discord](https://img.shields.io/discord/1491016144310767670?label=Discord&logo=discord&color=5865F2)](https://discord.gg/e9M3vBvzge)

DroneDB Registry is a comprehensive geospatial data management and storage platform. It provides a responsive UI, full REST API, STAC / OGC Services compliance, and a configurable processing platform.

View orthophotos, point clouds, 3D models, Vector files, 3D Tiles, Gaussian splats, panoramas and more directly in the browser with interactive measurement tools.

## ✨ Features

- **Dataset Management** - Create, organize and share datasets with fine-grained permissions
- **Interactive Visualization** - View orthophotos, point clouds, 3D models and panoramas in browser
- **Measurements** - 2D and 3D measurement tools on maps and point clouds
- **STAC Compliance** - Compliant with STAC 1.1.0 and STAC API 1.0.0
- **OGC Services** - WMS, WFS, WMTS, WCS and OGC API (Features + Tiles) served directly from any dataset
- **On-Demand Processing** - Automatic thumbnails, tiles, COG, streaming format generation, and build artifact downloads (COG, COPC, GPKG)
- **Remote Imports** - Pull datasets from another DroneDB Registry or download archives from URL, with SSRF protection and remote org/dataset browsing
- **Streaming Uploads** - Memory-efficient file uploads with configurable size limits
- **Feature Gating** - Control which processing tools are visible and available per organization via configuration
- **3D Tiles** - Native support for the 3D Tiles streaming format
- **User Management** - Role-based access control with organizations, storage quotas, and optional LDAP authentication

### Supported Formats

| Category | Formats |
|----------|---------|
| Images | JPG, JPEG, DNG, TIF, TIFF, PNG, GIF, WEBP |
| Point Clouds | LAS, LAZ, E57, PTS, XYZ, PLY* |
| 3D Models | OBJ, GLTF, GLB, PLY* |
| 3D Tiles | 3TZ |
| Gaussian Splats | SPLAT, SPZ |
| Rasters | GeoTIFF (orthophotos, DEMs) |
| Vector | GeoJSON, DXF, DWG, SHP, SHZ, FGB, TopoJSON, KML, KMZ, GPKG |
| Videos | MP4, MOV, WEBM, M4V, AVI, MKV |
| Other | Panoramas (360°), Markdown, PDF |

*PLY files are automatically classified as point clouds or 3D models based on their content.

### Live Examples

- [Zoo](https://hub.dronedb.app/r/odm/zoo) - Point cloud
- [ODM Seneca](https://hub.dronedb.app/r/hedo88/odm-seneca) - Orthophoto with measurements
- [Panorama Example](https://hub.dronedb.app/r/pierotofy/panoexample/) - 360° panorama viewer

## 📚 Documentation

**Full documentation is available at [docs.dronedb.app](https://docs.dronedb.app)**

| Guide | Description |
|-------|-------------|
| [Registry Guide](https://docs.dronedb.app/docs/registry) | Installation, configuration, deployment |
| [User Management](https://docs.dronedb.app/docs/user-management) | Users, roles, organizations, quotas |
| [API Reference](https://docs.dronedb.app/docs/api-reference) | REST API documentation |

## 💬 Community

**[Join the DroneDB Discord](https://discord.gg/e9M3vBvzge)** to get help, share feedback, discuss features, and connect with other DroneDB users


## 🚀 Quick Start with Docker

```bash
docker run -it --rm -p 5000:5000 -v ${PWD}/registry-data:/data dronedb/registry
```

Open [http://localhost:5000](http://localhost:5000) • Default credentials: `admin` / `password123`

> Change the default password immediately at [http://localhost:5000/account](http://localhost:5000/account)

### Useful Endpoints

| Endpoint     | Description                               |
| ------------ | ----------------------------------------- |
| `/scalar/v1` | API Documentation                         |
| `/stac`      | STAC Catalog                              |
| `/hangfire`  | Background jobs dashboard (requires auth) |

### Processing Platform

On-demand builds (thumbnails, tiles, COG, streaming formats) run automatically or can be triggered manually. Large downloads are offloaded to background tasks. Configuration options including per-user task limits, per-org output budgets, remote processing nodes (ODX/NodeODX), and feature gating are documented in the [Registry Guide](https://docs.dronedb.app/docs/registry).

For production deployment with MySQL/MariaDB, see the [full documentation](https://docs.dronedb.app/docs/registry#running-in-production).

## 🌍 OGC Services

Every dataset exposes a full suite of OGC-compliant endpoints at
`/orgs/{orgSlug}/ds/{dsSlug}/{service}`.

| Standard | Version | Endpoint | Notes |
|----------|---------|----------|-------|
| WMS | 1.1.1 / 1.3.0 | `…/wms` | Raster layers (orthophotos, DEMs) + folder-scoped `…/wms/p/{folder}` |
| WFS | 2.0 | `…/wfs` | Vector layers as GeoJSON / GML + folder-scoped variant |
| WMTS | 1.0.0 | `…/wmts` | KVP + RESTful `…/wmts/1.0.0/{layer}/{style}/{tms}/{z}/{y}/{x}.{ext}` |
| WCS | 2.0.1 / 1.1.1 / 1.0.0 | `…/wcs` | GeoTIFF / PNG / JPEG `GetCoverage`; `ACCEPTVERSIONS` first-match negotiation |
| OGC API: Features | 1.0 | `…/ogcapi` | JSON landing, conformance, collections, items |
| OGC API: Tiles | 1.0 | `…/ogcapi/collections/{id}/tiles` | MVT (`pbf`) for vector layers, PNG for raster |

### WMTS tile formats

`pbf` (MVT), `png`, `jpg` / `jpeg`. WebP is not supported.

### WCS version negotiation

WCS supports `ACCEPTVERSIONS` (comma-separated, client-preference order): the first
version both client and server support is selected. Supported versions:
`2.0.1`, `1.1.1`, `1.0.0`.

### OGC error envelopes

Authentication failures and OGC exceptions always return the version-appropriate
XML envelope (WMS `ServiceExceptionReport`, WFS/WMTS/WCS `ows:ExceptionReport`),
not the generic Registry error page.

### QGIS setup

Ready-made QGIS setup scripts are in [`scripts/`](scripts/) (`qgis-test-setup.sh` /
`qgis-test-setup.ps1`). See the [OGC services documentation](https://docs.dronedb.app/ogc-services)
for detailed QGIS configuration steps.

## 🛠️ Development

### Requirements

* [.NET 10.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
* [Node.js 24+](https://nodejs.org/) (LTS recommended)
* [DroneDB Library](https://github.com/DroneDB/DroneDB/releases/latest) (add to PATH)

### Build from Source

```bash
git clone https://github.com/DroneDB/Registry
cd Registry
git submodule update --init --recursive

# Build Vue.js frontend (copies output to registry-data/ClientApp/)
cd Registry.Web/ClientApp
npm install
npm run pub-dev
cd ../..

# Build and run
dotnet build
dotnet run --project Registry.Web ./registry-data
```

> For production builds, use `npm run build:prod` instead of `npm run pub-dev`.

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

