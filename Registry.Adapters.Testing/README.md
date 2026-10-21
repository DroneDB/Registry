# Registry.Adapters.Testing

Test doubles for the ports that `Registry.Adapters` implements (XI-09). Test projects only.

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
* The fake has no state: the fixtures are one recorded sequence, so a meta add does not change a
  later meta list.

Served: `GetVersion`, `TileMimeType`, `ThumbnailMimeType`, `RegisterProcess` (no-op), `Add` (the
recorded add), `Info`, `GetEntry`, `List`, `ListEntries`, `ListEntrySummaries`, `GetBuildStatus`,
`IsBuildable`, `IsBuildComplete`, `GetPendingBuildInfo`, `IsBuildPending`, `MetaAdd`, `MetaSet`,
`MetaGet`, `MetaUnset`, `MetaRemove`, `MetaList`, `MetaDump`, `Stac`, `StacItemCollection`,
`GetRasterInfo`, `GetRasterMetadata`. The `pending-build-info` fixture records another dataset:
read it with `ContractFixture.Load("pending-build-info")`.

`Registry.Web.UnitTest/Contract/FakeDdbWrapperTests.cs` checks that every vendored fixture is
served (or listed as not served).

## Vendored contract files

`Contract/` holds `README.md`, `schemas/` and `fixtures/` of DroneDB `tests/contract/`, byte for
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
