# Registry.Adapters.Testing

Test doubles for the ports that `Registry.Adapters` implements. Test projects only.

## FakeDdbWrapper

An `IDdbWrapper` that never loads native libddb. It replays the libddb contract fixtures that
DroneDB records in `tests/contract/` (vendored in `Contract/`, see `Contract/README.md` for every
call and normalisation):

* Any dataset folder stands for the contract dataset. `<root>` becomes the caller's folder,
  `DDBInfo` depths are computed from the caller's absolute path (libddb `io::Path::depth`), the
  normalised meta ids (`"<volatile>"`) become stable UUIDs derived from path, key and data
  (`FakeDdbWrapper.MetaId`), and the recorded STAC roots and collection id become the caller's.
* A call is answered only when a fixture records it. The build status is per path, so any subset
  of the recorded paths is answered; `GetEntry` of a path that is not in the contract index returns
  `null` (libddb's not-found answer). Outputs go through the same Json.NET deserialization as the
  native adapter.
* Every other call throws `NotSupportedException` naming the call: the fake never returns a default.
  Override a call with `new Mock<FakeDdbWrapper> { CallBase = true }` (every member is virtual), as
  `RegistryWebFactory.DdbWrapper` does, or record a fixture in DroneDB and serve it here.
* `FakeDdbWrapper.RasterInfo(params string[] indexLayer)` is the `raster-info` fixture with the given
  `indexLayer` (the spectral indices libddb lists for a raster; the fixture's own RGB raster lists none), for a test that
  overrides `GetRasterInfo` to answer for a multispectral raster the fixtures do not record.
* The fake has no state: the fixtures are one recorded sequence, so a meta add does not change a
  later meta list.

Served: `GetVersion`, `TileMimeType`, `ThumbnailMimeType`, `Add` (the recorded add), `Info`,
`GetEntry`, `List`, `ListEntries`, `ListEntrySummaries`, `GetBuildArtifacts` (per path, like the
build-artifacts fixture), `ResolveBuildFile` (the recorded file; null for an unknown hash),
`GetEntryTypes`, `CopyBuildArtifacts`, `GetPendingBuildInfo`, `IsBuildPending`, `MetaAdd`, `MetaSet`,
`MetaGet`, `MetaUnset`, `MetaRemove`, `MetaList`, `MetaDump`, `Stac`, `StacItemCollection`,
`GetRasterInfo`, `GetRasterMetadata`. The `pending-build-info` and `build-artifacts-pending` fixtures
record another dataset: read them with `ContractFixture.Load(...)`.

## FakeDdbRuntime

An `IDdbRuntime` that never loads libddb: `Capabilities` is the `capabilities` fixture,
`GetEffectiveConfig` the `effective-config` fixture, and the lifecycle calls (`Configure`,
`SetLogSink`, `RegisterProcess`, `Shutdown`) are recorded in order, so a test can check the order a
host makes them in; every call but `Shutdown` after `Shutdown` throws, as libddb refuses it.
`RegistryWebFactory.DdbRuntime` is the one its host uses.

`Registry.Web.UnitTest/Contract/FakeDdbWrapperTests.cs` checks that every vendored fixture is
served (or listed as not served).

## PendingMarkers

`PendingMarkers.Of(path, hash, lastAttempt, missingDependencies...)` builds a `PendingBuildInfo` as
libddb reports it: `RetryAfter` = `lastAttempt` + `LibddbRetryBackoffSeconds` (60, ddb.h "Build
manifest"). The constant is test data only: Registry reads `retryAfter` and keeps no copy of the
rule; `Abi11ContractTests` checks that libddb still applies it.

## Vendored contract files

`Contract/` holds `README.md`, `schemas/`, `fixtures/` and `vectors/` of DroneDB `tests/contract/`, byte for
byte, embedded in the assembly (`ContractFiles`). `Contract/SOURCE` records the DroneDB commit and
the sha256 of every vendored file; `Contract/.gitattributes` keeps git from converting line endings.
`Registry.Web.UnitTest/Contract/ContractSourceTests.cs` fails when a file differs from `SOURCE`,
when a file is missing or when an unlisted file is added, so the fixtures change only on purpose.

### Updating the fixtures

1. In DroneDB, the fixture change is committed (see "Changing an output" in `Contract/README.md`).
2. From the Registry root:

   ```sh
   Registry.Adapters.Testing/update-contract.sh <path to a DroneDB clone> <DroneDB commit>
   ```

   It copies the files with `git archive` (the committed bytes of that commit, whatever the
   checkout) and rewrites `Contract/SOURCE`.
3. Review `git diff Registry.Adapters.Testing/Contract`, run the Registry.Web.UnitTest contract
   tests (`dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Registry.Web.Test.Contract"`),
   adapt `FakeDdbWrapper` to new or changed fixtures and list the update below.

### Updates

* 2026-10-21: first vendoring, DroneDB `d5500af5b66d5d2f804c4441ab0ac334cdb666c4` (libddb 1.12.2).
* 2026-10-21: DroneDB `66390f4ed8e68abdbd9e67a3c500d81d38ddd922`, README only (`release_deb` -> `release_assets`), no fixture or schema change.
* 2026-10-21: DroneDB `c1a9515058c8c0b98bdf80594253441cface0fbc` (DroneDB 2.0.0, libddb ABI 1.1): `vectors/`
  (`entry-paths.json`, the `DDBNormalizePath` golden vectors) is vendored too; 14 new fixtures and 11 new
  schemas (build artifacts, entry types, resolve/copy build files, capabilities, effective config, list
  entries with build, normalize path, data-only STAC); the STAC collection and item schemas changed.
* 2026-10-22: DroneDB `6b9daee968412f7c1485ea0d3f6c0c3bf14af568`: the build object
  (`build-artifacts`, `build-artifacts-pending`, `list-entries-ex`) and `pending-build-info` gain
  `retryAfter` (required in both schemas; volatile next to `lastAttempt` in the two pending fixtures).
  `PendingBuildInfo.RetryAfter` and `EntryBuild.RetryAfter` read it, required.
* 2026-10-23: DroneDB `77f088efab98e982cf1a0258869850d8c16a9526`: `entry-types` reports `thumbnail: "file"` for
  `Panorama` and `GeoPanorama` (was `"none"`; `Video` and `GeoVideo` stay `"none"`), so the thumbnail endpoints
  render panoramas again. The commits since the last vendoring (`6b9daee`) that touch `tests/contract` are that
  one and a README-only one (the CI note: DroneDB no longer runs Registry's contract tests); no other fixture or
  schema changed.
* 2026-10-24: DroneDB `c5633bbe5e2ac474d86f580a9b42f9bbc366c137` (DroneDB 2.0.0 is ONE version: the library, its
  C ABI and the release share it, so libddb is ABI 2.0). The commits since the last vendoring (`77f088e`) change
  only `fixtures/capabilities.json` (`abi` is `{"major": 2, "minor": 0}`) and the descriptions of seven schemas
  (`capabilities`, `entry-types`, `build-artifacts`, `build-file`, `copy-build-artifacts`, `effective-config`,
  `list-entries-ex`: "new in 2.0" instead of "ABI 1.1"); `capabilities.schema.json` requires `abi.major >= 2`.
  The raster, colormap and index fixtures did not change. `DdbAbi` requires libddb 2.x.
* 2026-10-24: DroneDB `d3b4945db9069b90da5ba1a9f3a707bec8a94121`: `raster-info` gains `indexLayer` (required in the
  schema: the subset of `NDVI`, `NDRE`, `NDWI`, `SAVI`, `EVI` the WMS vegetation-index layer renders from the raster, in that
  order, `[]` when none; the fixture's RGB raster lists none). The commits since the last vendoring (`c5633bb`) change
  no other contract file. WMS reads the list instead of its own band-count rule (`IRasterIndexCatalog`).
* 2026-10-24: DroneDB `58a55a560dabf7633fad904ddb11d8a00edb4e98` (the pinned build): no file changes since `d3b4945` (a test-only
  fix), SOURCE names the pinned commit.
* 2026-10-24: DroneDB `2c9508aff26c63321fb5821ca2ac8e3041d7c221`: `effective-config` has `gdalCacheMb` (integer
  16..1048576 MiB, in `settings` and in `sources`, both required by the schema; the fixture sets 256 through `DDBConfigure`), so a libddb
  built before `36d2b54` no longer matches this fixture and schema; `capabilities` lists 13 more `cancellable` exports (26 in all:
  the raster analysis exports, `DDBCalculateVolume`, `DDBDetectStockpile`, `DDBDetectAllStockpiles`, `DDBGenerateContours`,
  `DDBMaskBorders`, `DDBComputeDeltaLocals`, `DDBApplyDelta`, `DDBApplyDeltaEx`: the cancellation steps 3 and 4). The README changelog
  of `Contract/` also describes the entry-path rule (every export that takes an entry path refuses an absolute path, a `..`
  segment, `.ddb` and invalid UTF-8 with `DDBERR_INVALIDARGS`), which changes no fixture. No other file changed.
* 2026-10-24: DroneDB `00b6768fcfd4738ea8d4b308b59db959289af875` (the pinned build): no file changes since `2c9508a`, SOURCE
  names the pinned commit.
* 2026-10-25: DroneDB `345cd5c2ef80ea6bd6ade3ab5f16a090d24d0193`: the artifact role `gsplat.georef` is gone from the `entry-types`
  fixture (the roles of `GaussianSplat` are `gsplat.lod`, `gsplat.spz`, `gsplat.bounds`); the contract files carry no planning
  identifiers any more, so `update-contract.sh` copies them verbatim.
* 2026-10-27: DroneDB `1bedffa8b723992a0ee4d889d664e91c92b0b228`: the `Model` roles in the `entry-types` fixture start with
  `model.bm3d` (`bm3d/model.bm3d`, `application/vnd.dronedb.bm3d`; the legacy `model.nexus` stays, listed only while
  `model.bm3d` is missing); `capabilities` lists `DDBBuildEx`; `build-artifacts.schema.json` allows `fallbackFor` on an
  artifact (`BuildArtifact.FallbackFor`). No other fixture changed (none lists a legacy artifact).
* 2026-10-28: DroneDB `c75d5442e97f258980adcfb141e604db31cfd866`: `resolve-build-file` is the object `{"path", "mediaType"}`
  (`build-file.schema.json` is an object schema; `ParseResolvedBuildFile` reads both shapes); `entry-types` lists the role
  `gsplat.export` of a Gaussian splat (`ArtifactRoles.GsplatExport`, the `.spz` made on demand); `capabilities` lists
  `DDBExportSplatSpz` and `DDBExportSplatTrainingData` in `symbols` and in `cancellable`; `entry.schema.json` describes
  the `georeferenced` and `georef` properties of a splat or model. The two exports are `IDdbWrapper` and `IDDB` members
  (`FakeDdbWrapper` records no fixture for them: a test configures them on a `Mock<FakeDdbWrapper>`).
* 2026-10-28: DroneDB `78a1518b01efffdcce9ab3162df269f6b1bbdcaf` (the pinned build): only the `Contract/README.md` text of
  `DDBExportSplatTrainingData` changed since `c75d5442` (`maxPoints` is at most 10000000, the bound the training data export
  of Registry already refuses above; `outFolder` must be missing or empty and outside of the dataset, else
  `DDBERR_INVALIDARGS`, which the task's own folder is). No fixture, schema or vector changed.
* 2026-10-28: DroneDB `8235fae7b4ddbf265e235de23aea7e0dd5fd047e`: `pending-build-info` gains `awaitingFiles` (boolean,
  required in the schema; `true` in the fixture of the deferred `roads.shp`): the build waits for files it lists in
  `missingDependencies`, so no timer retries it and `DDBIsBuildPending` does not count it; adding or moving in a file
  of one of those names makes it due at once (`awaitingFiles` false, `lastAttempt` 0). `PendingBuildInfo.AwaitingFiles`
  reads it (absent from an older libddb: false) and `FakeDdbWrapper.IsBuildPending` counts only the entries that do not
  await files. No other fixture, schema or vector changed.
* 2026-10-28: DroneDB `11c85d9c28051127b248841396de85ffa0c903b0`: the build object (`build-artifacts`,
  `build-artifacts-pending`, `list-entries-ex`) gains `awaitingFiles` (boolean, required in the schema; `true` in the
  fixture of the deferred `roads.shp`, `false` in the others), the value `pending-build-info` reports for the same
  marker. `EntryBuild.AwaitingFiles` reads it (absent from an older libddb: false) and the entry listing passes it to
  clients as `buildAwaitingFiles` next to `buildMissingDependencies`. No other fixture, schema or vector changed.
* 2026-10-28: DroneDB `a9f5e437ca3ad809442ce0d0bdccedec82a4b2a5`: an artifact of the build object (`build-artifacts`,
  `list-entries-ex`) and of `copy-build-artifacts` gains `version` (string, required in the schema), and
  `resolve-build-file` gains `version` (the version of the artifact the file belongs to; the tiles of a directory have the
  one of its index): it changes whenever a build rewrites or replaces the file a client opens under the same hash. The
  fixtures normalise it to `"<volatile>"`. `BuildArtifact.Version` and `ResolvedBuildFile.Version` read it (absent from an
  older libddb: null, and the URLs of the artifact stay unversioned). No other fixture, schema or vector changed.

* 2026-10-30: DroneDB `f878cef9e5b6f2762680c43e0838072f2cb8acb6`: the `entry-paths` vectors gain the spellings a file
  system opens as the `.ddb` folder, all `reserved` now: another letter case (`.DDB`), trailing dots and spaces
  (`.ddb.`, `.ddb `), an NTFS stream (`.ddb::$INDEX_ALLOCATION`) and the NTFS short name (`DDB~1`, any number).
  `EntryPathRule` follows `isReservedSegment`, and `DatasetPathPolicy` takes the rule from libddb as before. New
  fixtures `gdal-util-probe` (with its schema; the response of the helper program, which Registry does not read) and
  `info-pointcloud-no-footprint`; `entry.schema.json` describes the `warning` property, which the entry properties
  carry as they are. No adapter change.