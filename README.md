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
- **Feature Gating** - Control which processing tools users see and may run via server configuration
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
| [User Management](https://docs.dronedb.app/docs/registry/admin/user-management) | Users, roles, organizations, quotas |
| [API Reference](https://docs.dronedb.app/docs/integrations/api-reference) | REST API documentation |

## 💬 Community

**[Join the DroneDB Discord](https://discord.gg/e9M3vBvzge)** to get help, share feedback, discuss features, and connect with other DroneDB users


## 🚀 Quick Start with Docker

The fastest way to get started is with Docker. Download [Docker](https://www.docker.com/community-edition) and run:

```bash
docker run -it --rm -p 5000:5000 -v ${PWD}/registry-data:/data dronedb/registry
```

Data will be stored in the local folder `registry-data`.
Open [http://localhost:5000](http://localhost:5000) in your browser to start using the application.

> **macOS users:** do not bind-mount a local folder to `/data`. Docker Desktop shares host folders through virtiofs, and DroneDB cannot create dataset indexes on it. Use a named volume instead:
>
> ```bash
> docker volume create registry-data
> docker run -d --name registry --restart unless-stopped \
>   --platform linux/amd64 \
>   -p 5000:5000 \
>   -v registry-data:/data \
>   dronedb/registry
> ```
>
> The image is built for `linux/amd64` only. On Apple Silicon the `--platform linux/amd64` line is required, otherwise Docker stops with `no matching manifest for linux/arm64/v8`. Docker Desktop then runs the image through emulation (turn on *Use Rosetta for x86_64/amd64 emulation* in Settings → General).
>
> See [macOS: use a named volume](https://docs.dronedb.app/docs/registry/getting-started#macos-use-a-named-volume) for details.

**Default credentials**: `admin` / `password123`

> ⚠️ Change the default password immediately after first login at [http://localhost:5000/account](http://localhost:5000/account)

### Useful Endpoints

| Endpoint                                          | Description                               |
| ------------------------------------------------- | ----------------------------------------- |
| [/scalar/v1](http://localhost:5000/scalar/v1)     | API Documentation (Scalar UI)             |
| [/version](http://localhost:5000/version)         | Version information                       |
| [/quickhealth](http://localhost:5000/quickhealth) | Quick health check (requires auth)        |
| [/health](http://localhost:5000/health)           | Detailed health check (requires auth)     |
| [/hangfire](http://localhost:5000/hangfire)       | Background jobs dashboard (requires auth) |
| [/stac](http://localhost:5000/stac)               | STAC Catalog root                         |

The log files are in `registry-data/logs/` and roll daily: `registryYYYYMMDD.txt` for the default instance, `registry-webYYYYMMDD.txt`, `registry-node-<host>YYYYMMDD.txt` (one per node host name; the production compose names them `node-a` and `node-b`) and `registry-thumbsYYYYMMDD.txt` for the web server, processing node and thumbnail generator instance types.

### Processing Platform

On-demand builds (thumbnails, tiles, COG, streaming formats) run automatically or can be triggered manually. Large downloads are offloaded to background tasks. Configuration options including per-user task limits, per-org output budgets, remote processing nodes (ODX/NodeODX), and feature gating are documented in the [Registry Guide](https://docs.dronedb.app/docs/registry).

For production deployment with MySQL/MariaDB, see the [full documentation](https://docs.dronedb.app/docs/registry/admin/production#running-in-production).

### DSM altitude key

libddb looks up ground elevation (OpenTopography global DEM) to compute the relative altitude of images that carry none in their EXIF. The API key comes from the `DDB_OPENTOPOGRAPHY_KEY` environment variable of every Registry process (the production compose passes it through); without it the lookup is skipped, a warning is logged once, and the relative altitude falls back to the GPS altitude.

### Reverse proxy

Behind a reverse proxy (nginx), the proxy must send `X-Forwarded-For` and `X-Forwarded-Proto` (and pass the original `Host`): the Registry takes the client address (rate and download limits, logs) and the scheme (`Secure` cookies, HSTS) from them. They are trusted only when the peer the Registry sees is listed in `AppSettings:KnownProxies` (IP addresses, default loopback) or inside `AppSettings:KnownNetworks` (CIDR); from any other peer they are ignored. With a host nginx proxying to a port published by Docker (`docker/production`), the peer is the Docker bridge gateway (`172.x.0.1`), not loopback, so the production template sets `"KnownNetworks": [ "172.16.0.0/12" ]` (Docker's default bridge address pool); adjust it if your Docker networks use another range.

### Update check

Registry asks `dronedb.app` once a day which version is the latest, shows an update in the Hub to administrators, and can e-mail them about it (`AppSettings:UpdateCheckEmail`, needs the SMTP settings). At every start it logs what the check sends:

> Update check: enabled (daily). This deployment sends its instance ID (`<instance id>`), Registry/DroneDB versions, platform, public URL, host name, network domain and declared use to dronedb.app, and a contact email only if an admin opted in; dronedb.app also records the source IP address and checks the public URL. Purposes: update notices, release notes and license-compliance verification. Docs: docs.dronedb.app/docs/registry/admin/update-check  ·  Opt out: UpdateCheckCron="disabled"

To switch it off set `AppSettings:UpdateCheckCron` to `disabled` (environment variable `AppSettings__UpdateCheckCron`): nothing is sent and the log says `Update check: disabled`. The same text is in the Hub, under Admin, Configuration, "Update check".

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
`qgis-test-setup.ps1`). See the [OGC services documentation](https://docs.dronedb.app/docs/integrations/ogc-services)
for detailed QGIS configuration steps.

## 🛠️ Development

### Requirements

* [.NET 10.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
* [Node.js](https://nodejs.org/) — the frontend pins a version in `Registry.Web/ClientApp/.nvmrc`; the container image builds with the current Node LTS
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

