#nullable enable
using Registry.Ports.DroneDB;

namespace Registry.Web.Models;

/// <summary>
/// A dataset entry the current user is allowed to read, resolved once per request: access is
/// checked, the path exists and its render source (the entry or its build artifact) is on disk.
/// Lets a caller decide on the entry's identity (e.g. answer a conditional request with 304)
/// before any rendering, without checking access a second time.
/// </summary>
/// <param name="OrgSlug">Organization slug.</param>
/// <param name="DsSlug">Dataset slug.</param>
/// <param name="Path">Requested path, relative to the dataset.</param>
/// <param name="Hash">Entry hash: identifies the content.</param>
/// <param name="SourcePath">Path rendered for this entry (the entry itself or its build artifact).</param>
/// <param name="Ddb">Dataset the entry belongs to.</param>
public sealed record ReadableObject(string OrgSlug, string DsSlug, string Path, string Hash, string SourcePath, IDDB Ddb);
