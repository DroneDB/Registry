# Notice

Copyright © 2020–2026 Digipa S.R.L.

DroneDB Registry and DroneDB Hub are licensed under the DroneDB Source-Available License 1.0 ([LICENSE.md](LICENSE.md)). Uses that the licence does not permit need a commercial licence: see [COMMERCIAL.md](COMMERCIAL.md).

## Earlier releases

Releases before 3.0.0 were published under earlier licence terms. Those terms still apply to those releases. Releases before 3.0.0 get no further fixes, including security fixes.

## Third-party components

The licence covers only the work of Digipa S.R.L. Each component below keeps its own licence. Nothing in the DroneDB Source-Available License or in a commercial licence agreement limits the rights that these licences give you.

### Linked into Registry

| Component | Licence | Note |
| --- | --- | --- |
| DroneDB library (libddb) | MPL-2.0 | Full source: <https://github.com/DroneDB/DroneDB> |
| Hangfire.Core, Hangfire.AspNetCore, Hangfire.NetCore, Hangfire.InMemory | LGPL-3.0 | Loaded as separate assemblies. You may replace or modify them and reverse engineer Registry to debug such modifications. |
| Hangfire.MySqlStorage | LGPL-3.0 | As above |
| GEOS (inside the DroneDB library, through GDAL) | LGPL-2.1 | The full source of the DroneDB library is public, so you can relink it with a modified GEOS. |
| Other NuGet packages | MIT, Apache-2.0, BSD-3-Clause, Microsoft .NET Library License | The list with licence texts is in [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md). |

The libraries inside the DroneDB library (GDAL, PROJ, PDAL and others) and their licence texts are listed in `/usr/share/doc/ddb/third-party` in the Docker image and in the DroneDB packages.

### Separate programs in the Docker image

The image also contains programs that Registry runs as separate processes. They are not part of Registry and are not licensed under the DroneDB Source-Available License:

| Program | Licence | Source |
| --- | --- | --- |
| `ddb` (DroneDB command line), `gdal-util` (sandboxed GDAL helper) | MPL-2.0 | <https://github.com/DroneDB/DroneDB>, <https://github.com/DroneDB/gdal-util> |
| `metadata-util` (image metadata helper, uses Exiv2) | GPL-2.0-or-later | The source archive of the metadata-util release that DroneDB pins: <https://github.com/DroneDB/metadata-util/releases> |
| `bm3d` (3D model builder, derived from Nexus, vcglib and corto) | GPL-3.0 | The source archive of the bm3d release that DroneDB pins: <https://github.com/DroneDB/bm3d/releases> |
| `Obj2Tiles` (3D Tiles builder) | AGPL-3.0 | <https://github.com/OpenDroneMap/Obj2Tiles> |
| `untwine` (point cloud indexer) | GPL-3.0-only | <https://github.com/hobuinc/untwine> |
| `dwg2dxf` (LibreDWG, DWG to DXF converter) | GPL-3.0-or-later | <https://www.gnu.org/software/libredwg/> |
| `build-lod` (Gaussian splat levels of detail, from Spark) | MIT | <https://github.com/sparkjsdev/spark> |

Their licence texts are in `/usr/share/doc/ddb` in the image.

### In the web client (Hub)

| Component | Licence |
| --- | --- |
| Potree and its libraries | BSD-2-Clause and the licences in `potree/libs` |
| bm3d-viewer (3D model viewer) | MIT |
| Nexus web viewer (models built before bm3d) | MIT |
| DOMPurify | MPL-2.0 or Apache-2.0 |
| Font Awesome Free | CC-BY-4.0, OFL-1.1 and MIT |
| Inter font | OFL-1.1 |
| Other npm packages | MIT, ISC, BSD-2-Clause, BSD-3-Clause, Apache-2.0, Zlib, Python-2.0, CC0-1.0 |

## Trademarks

DroneDB is a trademark of Digipa S.R.L. (U.S. registration 6368406). Neither the licence nor this notice grants any right to use it.
