# Golden JSON contract tests

One file per case of `Baselines/GoldenJsonTests.cs`: the answer of a JSON
endpoint the Hub calls (`Registry.Web/ClientApp/webapp/js/libs/ddb/*.js`), requested through
`RegistryWebFactory` (the real pipeline, SQLite, `FakeDdbWrapper` over the DroneDB contract
fixtures, `TestAuthHandler` identities).

These goldens are the safety net of the wire format (ProblemDetails errors, System.Text.Json).
**Any difference must be on purpose.** The entries of `ds-list-root` and `ds-search` carry `artifacts` (the built `ortho.tif`: its `raster.cog` link), additive. `sys-features` has `entryTypes` (libddb's `DDBGetEntryTypes`, passed through) and `archiveExtensions` (the extractor's rule) for the Hub, additive: no existing member changed. Update on purpose, then review the diff:

```
REGISTRY_UPDATE_SNAPSHOTS=1 dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~GoldenJsonTests"
```

A failing case prints an ordered line diff (`- golden`, `+ now`, `@@ line N` of the new file).

## File format

```json
{
  "request": "GET /sys/features",          // method and path as sent (no host)
  "caller": "anonymous",                    // anonymous | user (no membership) | organization owner | organization owner (write org) | admin
  "status": 200,
  "contentType": "application/json; charset=utf-8",   // null when the response has none
  "headers": { "X-Total-Count": "1" },      // only the contract headers, only when present
  "bodyKind": "json",                       // json | text | empty
  "body": { }                               // the scrubbed body
}
```

* Object keys keep the order and casing the server wrote: both are part of the wire format.
* Numbers are kept as written (`1.0` stays `1.0`, `100` stays `100`).
* String escaping is normalised (the file writes non-ASCII and `<>&'` as is): only the JSON value is
  compared, not how the serializer escaped it.
* `bodyKind` tells a JSON string body (`"1.2.3"`) from a plain-text one (`1.2.3`).
* Every case declares its status: a 5xx is never recorded, even in update mode.

## Scrubbed values (`JsonScrubber`, one helper for every case)

| What | Rule | Placeholder | Type kept |
|---|---|---|---|
| `token` (any casing) | property | `"<token>"` | string |
| `expires` (any casing): token expiry, epoch seconds = now + `TokenExpirationInDays` | property | `0` | number |
| `instanceHash` (any casing): the hash of the instance id, random per host | property | `"<instance-hash>"` | string |
| `registryVersion`, `hubVersion`, `ddbVersion` (any casing) | property | `"<version>"` (also when the value is null: `HubInfo` is process-wide, so null or a version depending on what ran before) | string |
| The Registry version (read from `GET /sys/version` at start), the fake libddb version (`FakeDdbWrapper.Version`), `HubInfo.CurrentVersion` | whole string value | `"<version>"` | string |
| The factory temp root (`/tmp/registry-web-factory/<id>`), in any string | substring | `<root>` | string |
| A JWT (`eyJ….….…`) | whole string value | `"<jwt>"` | string |
| An ISO 8601 date-time | whole string value | `"<datetime>"` (no offset), `"<datetime:Z>"` (UTC `Z`), `"<datetime:offset>"` (`±hh:mm`) | string |
| A GUID (user ids, meta ids, dataset internal references), in any string | substring | `<guid>` | string |

Date-times keep their offset form on purpose: how a serializer writes the `DateTimeKind` is part of
the wire format; the fraction digits are not (Json.NET trims trailing zeros).
Not scrubbed because they are stable: the fixture values (`mtime` 1700000000, hashes, sizes, meta
`mtime` 0), the TestServer host `http://localhost` and `ddb+unsafe://localhost`.

## Order

Arrays keep the server order, except where it is not part of the contract (sorted by the test,
ordinal):

* `users-list`, `users-detailed`: by `userName` (the identity store has no defined order);
* `orgs-list`, `orgs-list-admin`: by `slug` (`OrganizationsManager.List` has no `ORDER BY`).

Read-only cases share one host; a case that changes state (`orgs-create`, `orgs-datasets-create`,
`users-create`) runs on its own host, so no golden depends on the test order.

## libddb answers

From the DroneDB contract fixtures (`Registry.Adapters.Testing/Contract`), through
`FakeDdbWrapper`. The calls no fixture records are set up in the test (`GoldenDdb`) with the
shapes libddb returns:

* dataset root meta (`name`, `visibility`, `DDBMetaGet`/`DDBMetaSet` on path ""): a per-folder
  store, seeded for `golden-ds`; a missing key throws `DdbException` like libddb; text that is not
  JSON is stored as a JSON string (`MetaManager::validateData`);
* dataset root info (`DDBInfo` of the dataset folder, no hash): a `DroneDB` (7) entry with
  `properties.entries` / `properties.meta` (`Database::getProperties`) and the size of the
  contract dataset (`list` fixture), 0 for a created dataset;
* entry queries the fixtures do not record (`DDBListEntries`/`DDBListEntrySummaries`): the
  whole-dataset fixture filtered with libddb's `listEntries` rules (folder descendants, depth when
  not recursive, types, case-insensitive name glob, ordered by path); empty for a created dataset;
* `DDBGet` of an indexed path without a `get-*` fixture: its entry of the `list` fixture.

The fixtures are stateless: `meta-set`/`meta-unset` do not change a later read, and `meta-list`
(fixture) lists `name` and `tags` while the root store holds `name` and `visibility`.

## Cases

| Golden | Request | Caller | Status |
|---|---|---|---|
| sys-config | GET /sys/config | admin | 200 |
| sys-version | GET /sys/version | user | 200 |
| sys-features | GET /sys/features | anonymous | 200 |
| sys-processing-nodes | GET /sys/processingNodes | user | 200 |
| sys-tasks | GET /sys/tasks | admin | 200 |
| users-authenticate | POST /users/authenticate (form) | anonymous | 200 |
| users-authenticate-refresh | POST /users/authenticate/refresh | admin | 200 |
| users-list | GET /users | admin | 200 |
| users-detailed | GET /users/detailed | admin | 200 |
| users-roles | GET /users/roles | admin | 200 |
| users-storage | GET /users/storage | owner | 200 |
| users-user-storage | GET /users/{user}/storage | admin | 200 |
| users-user-orgs | GET /users/{user}/orgs | admin | 200 |
| users-create | POST /users (JSON) | admin | 200 |
| orgs-list | GET /orgs | owner | 200 |
| orgs-list-admin | GET /orgs | admin | 200 |
| orgs-create | POST /orgs (form) | owner | 201 |
| orgs-get | GET /orgs/{org}/ | owner | 200 |
| orgs-datasets | GET /orgs/{org}/ds | owner | 200 |
| orgs-datasets-paged | GET /orgs/{org}/ds?includeThumbnailCheck&skip&take&sort (`X-Total-Count`) | owner | 200 |
| orgs-import-sources | GET /orgs/{org}/import/sources | owner | 200 |
| orgs-datasets-create | POST /orgs/{org}/ds (form) | owner | 201 |
| ds-get | GET /orgs/{org}/ds/{ds} | owner | 200 |
| ds-list-root | POST /orgs/{org}/ds/{ds}/list | owner | 200 |
| ds-list-folder | POST /orgs/{org}/ds/{ds}/list path=sub | owner | 200 |
| ds-search | POST /orgs/{org}/ds/{ds}/search query=* | owner | 200 |
| ds-builds-pending | GET /orgs/{org}/ds/{ds}/builds/pending | owner | 200 |
| ds-ex, ds-stamp, ds-batches | GET /orgs/{org}/ds/{ds}/ex, …/stamp, …/batches (the other dataset answers that carry libddb values) | owner | 200 |
| ds-chattr | POST …/chattr attrs={"public":true} | owner | 200 |
| meta-dump | POST …/meta/dump | owner | 200 |
| share-init | POST /share/init tag=org/ds | owner | 200 |
| users-meta, users-user-meta | GET /users/meta, /users/{user}/meta | owner, admin | 200 |
| orgs-public | GET /orgs/public | anonymous | 200 |
| meta-list, meta-list-entry | GET …/meta/list[?path=sub/img.jpg] | owner | 200 |
| meta-get, meta-get-list | GET …/meta/get/annotation?path=sub/img.jpg, …/meta/get/tags | owner | 200 |
| meta-set, meta-unset | POST …/meta/set, …/meta/unset (form) | owner | 200 |
| tasks-tools, tasks-list | GET …/tasks/tools, …/tasks | owner | 200 |
| stac-collection, stac-item, stac-item-image | GET …/stac, …/stac/{base64("ortho.tif")}, …/stac/{base64("sub/img.jpg")} | owner | 200 |
| stac-api-landing, stac-api-collections | GET /stac, /stac/collections | owner | 200 |
| stac-api-collection, stac-api-items, stac-api-item | GET /stac/collections/{org}/{ds}[/items[/{base64url("ortho.tif")}]] | owner | 200 |
| stac-api-search | GET /stac/search?collections={org}/{ds} | owner | 200 |
| ogcapi-landing, ogcapi-conformance, ogcapi-collections | GET …/ogcapi[/conformance\|/collections]?f=json | owner | 200 |

Error bodies: RFC 9457 `ProblemDetails`, `application/problem+json`
with `type`, `title`, `status`, `detail` (the message), `traceId` (scrubbed to `<trace-id>`) and the
`noRetry` extension member; a model-validation body is a `ValidationProblemDetails` (`errors` per
field, `detail` with the joined field messages). 401 only for anonymous callers.

| Golden | Request | Caller | Status | Body |
|---|---|---|---|---|
| error-400-invalid-slug | POST /orgs/{org}/ds slug="Not A Slug!" | owner | 400 | manager `BadRequestException` |
| error-400-model-validation | POST …/meta/remove without `id` | owner | 400 | model validation |
| error-401-anonymous-gate | GET /users/storage | anonymous | 401 | empty, no Content-Type (`[Authorize]` challenge) |
| error-401-anonymous-private-org | GET /orgs/{org}/ds | anonymous | 401 | manager `ForbiddenException`, anonymous caller |
| error-403-non-member | GET /orgs/{org}/ds/{ds} | user | 403 | manager `ForbiddenException`, authenticated caller |
| error-403-non-admin | GET /sys/config | user | 403 | empty, no Content-Type (Admin policy) |
| error-404-missing-dataset | GET /orgs/{org}/ds/golden-missing | owner | 404 | manager `NotFoundException` |
| error-404-missing-organization | GET /orgs/golden-nowhere/ | owner | 404 | manager `NotFoundException` |

The empty 401/403 come from the test authentication scheme's challenge/forbid; production's JWT
scheme also answers with an empty body (plus `WWW-Authenticate: Bearer`, not recorded).

## Not covered (and why)

* `raster-info`, `raster-metadata`, `raster-value-info`, `raster-point-value`, `raster-area-stats`,
  `raster-profile`, `contours`, `stockpile/*`: they read a built COG on disk (404 "Processed data …
  not available yet" without one) and most libddb raster calls have no fixture.
* `ogcapi/collections/{id}/items` (the Hub's features call): needs the built `vec/source.gpkg` and
  `DDBQueryVector` has no fixture; Registry passes libddb's GeoJSON through, so a set-up value would
  only test the mock.
* `/orgs/{org}/members`: member management is disabled by default (500; the endpoint smoke test records it); the Hub calls it only when the feature is on.
* `POST …/tasks`, `GET …/tasks/{id}`, `…/log`, `…/retry`, `…/delete`, `/sys/tasks/{id}/delete`:
  they enqueue or need a Hangfire job; only the empty listings are recorded.
* Dataset writes (`obj` upload/delete/move/copy, `rename`, `transfer`, `build`, `rescan`, `chattr`,
  push, share): libddb mutations without fixtures (`DDBAdd` with hints, `DDBRemove`,
  `DDBMoveEntry`, …) and file-system side effects.
* `PUT`/`DELETE` of users, organizations and datasets: empty `200` bodies or file-system side
  effects; `/sys/processingNodes/{id}/*`: no node configured.
* Binary or non-JSON endpoints: downloads, thumbnails, tiles, MVT, `upload-probe`, OGC XML (WMS,
  WFS, WCS, WMTS).

## Observed today (recorded as is, not fixed here)

* `orgs-datasets-create` returns `permissions: null` and `hasThumbnail: null`.
* `ds-get` answers an array with one entry.
* `error-401-non-member`: an authenticated user without access gets 401.

## Fixed since recorded (goldens updated on purpose)

* `sys-features` / `sys-config`: `datasetThumbnailCandidates` (and `sys-config`'s
  `BlockedFileExtensions`) listed every item twice: the configuration binder appended the
  configured array to the settings' initializer array. Config-bound lists have no initializer now;
  their defaults apply when not set (`SettingsListBindingTests`). `sys-config`'s Thumbnails
  section is no longer active (its current values equal the defaults).
* `users-list`: users without a role had `"roles": [null]`, now `[]` like `users-detailed`
  (`UsersManagerTest.GetAll_UserWithoutRole_HasNoRoles`).
* `orgs-create` returned `owner` as the user id, now the user name like `orgs-get`/`orgs-list`
  (`OrganizationManagerTest.AddNew_AnswersTheOwnerLikeGetAndList`).

## Additions (goldens updated on purpose)

* `sys-config`: a `Smtp` section (outgoing e-mail, off by default). Its password is a sensitive field: only
  whether it is set is shown (`EmailEndpointTests.ConfigView_ShowsTheSmtpSection_WithoutThePassword`).
* `sys-features`: `emailEnabled`, whether the server sends e-mail (`AppSettings:Smtp:Enabled`), so clients can hide
  what needs e-mail (`EmailEndpointTests.Features_TellWhetherEmailIsEnabled_ToAnonymousClients`).
* `sys-config`: an `UpdateCheck` section (the daily update check): its description is the disclosure of what the check
  sends, with the instance id (`<guid>` here), and its fields are the cron, the e-mail mode, the instance id and the
  documentation link.
* `sys-features`: `updateCheckEnabled` (the cron is not disabled) and `instanceHash` (scrubbed: random per host).
